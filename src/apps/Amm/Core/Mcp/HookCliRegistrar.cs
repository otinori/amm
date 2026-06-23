using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Amm.Core.Mcp;

/// <summary>
/// AI CLI のユーザースコープ設定に、hook 駆動の状態通知 (amm-mcp.exe notify) と
/// 許可集約 (amm-mcp.exe approve) を登録 / 削除する
/// (UDR-amm-20260605T0523-7e1 / UDR-amm-20260605T1124-9c4)。
///
/// CLI ごとの登録先と内容:
/// - Claude Code : ~/.claude/settings.json の hooks
///     Stop / Notification → notify (入力待ち・attention)
///     PermissionRequest   → approve (許可集約、Level 2)
/// - Codex       : ~/.codex/config.toml
///     ルート notify キー → notify --source codex (agent-turn-complete = idle。
///       JSON は stdin でなく argv 末尾に渡る)
///     [tui] notifications → OSC9 ターミナル通知を有効化し、approval-requested
///       (許可待ち) を amm 側の xterm.js OSC9 ハンドラで attention として拾う。
///       Codex にはブロッキング型 hook が無いため Level 2 (許可の代答) は不可。
///     既存の notify / notifications 設定 (ユーザー自身のもの) には触れず、
///     notify が他プログラムを指している場合は明示エラーにする (単一キーのため
///     同居不能)。amm が足した行は `# added by amm` マーカーで識別する。
/// - Copilot CLI : ~/.copilot/hooks/amm-hooks.json (amm 専有ファイル)
///     agentStop         → notify --state idle --source copilot
///     permissionRequest → approve --source copilot (許可集約、Level 2)
///     ファイルごと生成 / 削除するので他設定との干渉がない。
///
/// Claude / Copilot の command は自己ガード形式 (`cmd /c if exist ...`) で登録し、
/// amm をアンインストールして exe が消えても静かに no-op する。Codex の notify は
/// JSON が argv で渡るため cmd 経由にできず (引用が壊れる)、exe 直接指定とする
/// (exe 不在時は Codex が起動失敗を自身のログに記すのみで動作には影響しない)。
/// 既存の設定 (ユーザー自身のエントリ) は保全し、書き込みは atomic write。
/// </summary>
public static class HookCliRegistrar
{
    /// <summary>マーカー: amm が config.toml に追加した行の識別子。</summary>
    private const string AmmMarker = "# added by amm";

    public static string DisplayName(McpCliKind kind) => kind switch
    {
        McpCliKind.ClaudeCode => "Claude Code",
        McpCliKind.Codex => "Codex",
        McpCliKind.CopilotCli => "Copilot CLI",
        _ => kind.ToString(),
    };

    /// <summary>対象設定ファイルの絶対パス。homeDir はテスト用注入点。</summary>
    public static string ConfigPath(McpCliKind kind, string? homeDir = null)
    {
        var home = homeDir ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return kind switch
        {
            McpCliKind.ClaudeCode => Path.Combine(home, ".claude", "settings.json"),
            McpCliKind.Codex => Path.Combine(home, ".codex", "config.toml"),
            McpCliKind.CopilotCli => Path.Combine(home, ".copilot", "hooks", "amm-hooks.json"),
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };
    }

    /// <summary>登録済みなら true。ファイル無し / 解析不能は false。</summary>
    public static bool IsRegistered(McpCliKind kind, string? homeDir = null) =>
        GetRegisteredCommand(kind, homeDir) != null;

    /// <summary>
    /// 登録済み amm エントリの exe パスを返す。未登録 / ファイル無しは null。
    /// パスが古い (移動済み) 場合の再登録判定に使う。
    /// </summary>
    public static string? GetRegisteredCommand(McpCliKind kind, string? homeDir = null)
    {
        var path = ConfigPath(kind, homeDir);
        if (!File.Exists(path)) return null;
        try
        {
            return kind switch
            {
                McpCliKind.ClaudeCode => GetClaudeCommand(path),
                McpCliKind.Codex => GetCodexCommand(path),
                McpCliKind.CopilotCli => GetCopilotCommand(path),
                _ => null,
            };
        }
        catch
        {
            return null; // 解析不能 → 未登録扱い (Register 時に明示エラーにする)
        }
    }

    /// <summary>amm エントリを登録 (既存 amm エントリは置換、他は保全)。失敗は例外。</summary>
    public static void Register(McpCliKind kind, string mcpExePath, string? homeDir = null)
    {
        var path = ConfigPath(kind, homeDir);
        switch (kind)
        {
            case McpCliKind.ClaudeCode: RegisterClaude(path, mcpExePath); break;
            case McpCliKind.Codex: RegisterCodex(path, mcpExePath); break;
            case McpCliKind.CopilotCli: RegisterCopilot(path, mcpExePath); break;
            default: throw new ArgumentOutOfRangeException(nameof(kind));
        }
    }

    /// <summary>amm エントリを削除 (他のエントリは保全)。未登録 / ファイル無しは何もしない。</summary>
    public static void Unregister(McpCliKind kind, string? homeDir = null)
    {
        var path = ConfigPath(kind, homeDir);
        if (!File.Exists(path)) return;
        switch (kind)
        {
            case McpCliKind.ClaudeCode: UnregisterClaude(path); break;
            case McpCliKind.Codex: UnregisterCodex(path); break;
            case McpCliKind.CopilotCli: File.Delete(path); break; // 専有ファイルごと削除
            default: throw new ArgumentOutOfRangeException(nameof(kind));
        }
    }

    // ================================================================
    // Claude Code (~/.claude/settings.json)
    // ================================================================

    /// <summary>
    /// amm エントリを登録する hook イベントと、起動するサブコマンド / timeout (秒)。
    /// - Stop / Notification → notify (入力待ち・attention 通知、即終了するので 10 秒)
    /// - PermissionRequest   → approve (許可集約。人間の回答を待つため 60 秒。
    ///   amm 側台帳 45 秒 < approve 読み取り上限 55 秒 < この 60 秒、の鎖で
    ///   hook の強制 kill には到達しない)
    /// </summary>
    private static readonly (string Event, string SubCommand, int TimeoutSec)[] ClaudeHookEntries =
    [
        ("Stop", "notify --source claude", 10),
        ("Notification", "notify --source claude", 10),
        ("PermissionRequest", "approve", 60),
    ];

    private static readonly string[] ClaudeHookEvents =
        ClaudeHookEntries.Select(e => e.Event).ToArray();

    private static string? GetClaudeCommand(string path)
    {
        var root = JsonNode.Parse(File.ReadAllText(path)) as JsonObject;
        if (root?["hooks"] is not JsonObject hooks) return null;
        foreach (var ev in ClaudeHookEvents)
        {
            if (hooks[ev] is not JsonArray groups) continue;
            foreach (var group in groups)
            {
                var cmd = FindAmmCommand(group);
                if (cmd != null) return ExtractExePath(cmd);
            }
        }
        return null;
    }

    private static void RegisterClaude(string path, string mcpExePath)
    {
        JsonObject root;
        if (File.Exists(path))
        {
            // 解析不能なら例外のまま伝播 (既存内容を壊さない)
            root = JsonNode.Parse(File.ReadAllText(path)) as JsonObject
                ?? throw new InvalidDataException($"{path} のルートが JSON オブジェクトではありません。");
        }
        else
        {
            root = new JsonObject();
        }

        if (root["hooks"] is not JsonObject hooks)
        {
            hooks = new JsonObject();
            root["hooks"] = hooks;
        }

        foreach (var (ev, subCommand, timeoutSec) in ClaudeHookEntries)
        {
            if (hooks[ev] is not JsonArray groups)
            {
                groups = new JsonArray();
                hooks[ev] = groups;
            }
            RemoveAmmGroups(groups);
            // 自己ガード形式: amm をアンインストール / 移動して exe が消えても
            // `if exist` が偽になり静かに no-op する (per-machine MSI からは
            // per-user の settings.json を全ユーザー分掃除できないため、
            // 「残っても無害」を登録時に作り込む)。exe パスは空白を含み得るので
            // 引用符で囲む。細かいイベント判別は notify/approve 側が stdin
            // payload で行う。
            groups.Add(new JsonObject
            {
                ["hooks"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["type"] = "command",
                        ["command"] = $"cmd /c if exist \"{mcpExePath}\" \"{mcpExePath}\" {subCommand}",
                        ["timeout"] = timeoutSec,
                    },
                },
            });
        }

        WriteAtomicJson(path, root);
    }

    private static void UnregisterClaude(string path)
    {
        var root = JsonNode.Parse(File.ReadAllText(path)) as JsonObject
            ?? throw new InvalidDataException($"{path} のルートが JSON オブジェクトではありません。");
        if (root["hooks"] is not JsonObject hooks) return;

        var changed = false;
        foreach (var ev in ClaudeHookEvents)
        {
            if (hooks[ev] is not JsonArray groups) continue;
            changed |= RemoveAmmGroups(groups);
            if (groups.Count == 0) { hooks.Remove(ev); changed = true; }
        }
        if (!changed) return;
        if (hooks.Count == 0) root.Remove("hooks");

        WriteAtomicJson(path, root);
    }

    // ================================================================
    // Codex (~/.codex/config.toml)
    // ================================================================

    // ルートスコープの notify キー (最初のテーブルヘッダより前のみが対象)
    private static readonly Regex CodexNotifyLine = new(
        @"^\s*notify\s*=", RegexOptions.Compiled);
    private static readonly Regex AnyTableHeader = new(@"^\s*\[", RegexOptions.Compiled);
    private static readonly Regex TuiHeader = new(
        @"^\s*\[tui\]\s*(#.*)?$", RegexOptions.Compiled);
    private static readonly Regex TuiNotificationsLine = new(
        @"^\s*notifications\s*=", RegexOptions.Compiled);
    // notify 行から exe パス (literal string の第 1 要素) を抜く
    private static readonly Regex CodexNotifyExe = new(
        @"'(?<exe>[^']*amm-mcp\.exe)'", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static string? GetCodexCommand(string path)
    {
        var lines = File.ReadAllLines(path);
        var (index, line) = FindCodexNotifyLine(lines);
        if (index < 0 || line == null) return null;
        if (!line.Contains("amm-mcp.exe", StringComparison.OrdinalIgnoreCase)) return null;
        var m = CodexNotifyExe.Match(line);
        return m.Success ? m.Groups["exe"].Value : string.Empty;
    }

    /// <summary>ルートスコープ (最初のテーブルヘッダより前) の notify 行を探す。</summary>
    private static (int index, string? line) FindCodexNotifyLine(string[] lines)
    {
        for (int i = 0; i < lines.Length; i++)
        {
            if (AnyTableHeader.IsMatch(lines[i])) break; // 以降はテーブル内 → 対象外
            if (CodexNotifyLine.IsMatch(lines[i])) return (i, lines[i]);
        }
        return (-1, null);
    }

    private static void RegisterCodex(string path, string mcpExePath)
    {
        var lines = File.Exists(path)
            ? File.ReadAllLines(path).ToList()
            : new List<string>();

        // notify は単一キーのため、他プログラムの既存設定とは同居できない。
        // ユーザー設定を黙って上書きせず、明示エラーでダイアログに伝える。
        var (notifyIndex, notifyLine) = FindCodexNotifyLine(lines.ToArray());
        if (notifyIndex >= 0 && notifyLine != null
            && !notifyLine.Contains("amm-mcp.exe", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "~/.codex/config.toml に既存の notify 設定があるため登録できません。" +
                "手動で notify を amm-mcp.exe に変更するか、既存設定を退避してください。");
        }

        // パスに ' が含まれても TOML が壊れないよう TomlEscape 経由で出力する
        // (通常は literal string、' 含有時は basic string へ自動切替)。
        // Codex は配列末尾にイベント JSON を 1 引数として追加して起動する。
        var newNotify = $"notify = [{TomlEscape.Str(mcpExePath)}, 'notify', '--source', 'codex']";
        if (notifyIndex >= 0)
        {
            lines[notifyIndex] = newNotify; // 既存 amm 行の置換 (パス更新)
        }
        else
        {
            // ルートキーは最初のテーブルヘッダより前にしか書けない。先頭テーブル
            // ヘッダの直前 (なければ末尾) に挿入する。
            int insertAt = lines.FindIndex(l => AnyTableHeader.IsMatch(l));
            if (insertAt < 0)
            {
                while (lines.Count > 0 && string.IsNullOrWhiteSpace(lines[^1])) lines.RemoveAt(lines.Count - 1);
                if (lines.Count > 0) lines.Add(string.Empty);
                lines.Add(newNotify);
            }
            else
            {
                lines.Insert(insertAt, newNotify);
                lines.Insert(insertAt + 1, string.Empty);
            }
        }

        EnsureCodexTuiNotifications(lines);
        WriteAtomicText(path, string.Join("\n", lines) + "\n");
    }

    /// <summary>
    /// [tui] の notifications (OSC9 ターミナル通知) を有効化する。amm の xterm.js
    /// が OSC9 を受けて approval-requested を attention として表示するための前提。
    /// ユーザーが既に notifications を設定している場合は触らない (尊重)。
    /// amm が足した行は AmmMarker 付きで、解除時にその行だけ取り除ける。
    /// </summary>
    private static void EnsureCodexTuiNotifications(List<string> lines)
    {
        int tuiStart = lines.FindIndex(l => TuiHeader.IsMatch(l));
        if (tuiStart < 0)
        {
            while (lines.Count > 0 && string.IsNullOrWhiteSpace(lines[^1])) lines.RemoveAt(lines.Count - 1);
            if (lines.Count > 0) lines.Add(string.Empty);
            lines.Add($"[tui] {AmmMarker}");
            lines.Add($"notifications = [\"agent-turn-complete\", \"approval-requested\"] {AmmMarker}");
            lines.Add($"notification_method = \"osc9\" {AmmMarker}");
            return;
        }

        // 既存 [tui] セクションの範囲を特定
        int tuiEnd = tuiStart + 1;
        bool hasNotifications = false;
        while (tuiEnd < lines.Count && !AnyTableHeader.IsMatch(lines[tuiEnd]))
        {
            if (TuiNotificationsLine.IsMatch(lines[tuiEnd])) hasNotifications = true;
            tuiEnd++;
        }
        // ユーザー自身の notifications 設定があれば method も含めて一切触らない
        // (standalone での Codex 利用 (Windows Terminal 等) の通知方式を壊さない)
        if (hasNotifications) return;
        lines.Insert(tuiStart + 1, $"notifications = [\"agent-turn-complete\", \"approval-requested\"] {AmmMarker}");
        lines.Insert(tuiStart + 2, $"notification_method = \"osc9\" {AmmMarker}");
    }

    private static void UnregisterCodex(string path)
    {
        var lines = File.ReadAllLines(path).ToList();
        var changed = false;

        // amm の notify 行 (ルートスコープ) を除去
        var (notifyIndex, notifyLine) = FindCodexNotifyLine(lines.ToArray());
        if (notifyIndex >= 0 && notifyLine != null
            && notifyLine.Contains("amm-mcp.exe", StringComparison.OrdinalIgnoreCase))
        {
            lines.RemoveAt(notifyIndex);
            changed = true;
        }

        // amm マーカー付きの行 ([tui] 関連) を除去
        for (int i = lines.Count - 1; i >= 0; i--)
        {
            if (lines[i].Contains(AmmMarker, StringComparison.Ordinal))
            {
                // [tui] ヘッダ自体が amm 製の場合、ユーザーが後からキーを足して
                // いれば残す (セクションが空になるときだけ消す)。残すときは
                // マーカーを剥がして以後ユーザー管理であることを明確にする。
                if (TuiHeader.IsMatch(lines[i]))
                {
                    bool sectionEmpty = true;
                    for (int j = i + 1; j < lines.Count && !AnyTableHeader.IsMatch(lines[j]); j++)
                    {
                        if (!string.IsNullOrWhiteSpace(lines[j])) { sectionEmpty = false; break; }
                    }
                    if (!sectionEmpty)
                    {
                        lines[i] = "[tui]";
                        changed = true;
                        continue;
                    }
                }
                lines.RemoveAt(i);
                changed = true;
            }
        }
        if (!changed) return;

        // 連続空行を 1 つへ畳み、末尾空行を除去
        for (int i = lines.Count - 1; i > 0; i--)
        {
            if (string.IsNullOrWhiteSpace(lines[i]) && string.IsNullOrWhiteSpace(lines[i - 1]))
                lines.RemoveAt(i);
        }
        while (lines.Count > 0 && string.IsNullOrWhiteSpace(lines[^1])) lines.RemoveAt(lines.Count - 1);
        WriteAtomicText(path, lines.Count == 0 ? string.Empty : string.Join("\n", lines) + "\n");
    }

    // ================================================================
    // Copilot CLI (~/.copilot/hooks/amm-hooks.json — amm 専有ファイル)
    // ================================================================

    private static string? GetCopilotCommand(string path)
    {
        var root = JsonNode.Parse(File.ReadAllText(path)) as JsonObject;
        if (root?["hooks"] is not JsonObject hooks) return null;
        foreach (var (_, node) in hooks)
        {
            if (node is not JsonArray defs) continue;
            foreach (var def in defs)
            {
                var cmd = def?["command"]?.GetValue<string>();
                if (IsAmmNotifyCommand(cmd)) return ExtractExePath(cmd!);
            }
        }
        return null;
    }

    private static void RegisterCopilot(string path, string mcpExePath)
    {
        // 専有ファイルなので常に全体を書き直す (冪等)。
        // - agentStop: 応答完了 = 入力待ち。payload は見ずに --state idle 固定
        //   (Copilot の camelCase payload にはイベント名フィールドが無いため)。
        // - permissionRequest: 許可集約 (Level 2)。timeoutSec 60 は Claude と同じ
        //   待ち時間の鎖 (台帳 45s < approve 55s < hook 60s)。
        // 自己ガード形式 (cmd /c if exist) は stdin/stdout が cmd を素通しする
        // ので decision JSON の受け渡しに影響しない。
        var root = new JsonObject
        {
            ["version"] = 1,
            ["hooks"] = new JsonObject
            {
                ["agentStop"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["type"] = "command",
                        ["command"] = $"cmd /c if exist \"{mcpExePath}\" \"{mcpExePath}\" notify --state idle --source copilot",
                        ["timeoutSec"] = 10,
                    },
                },
                ["permissionRequest"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["type"] = "command",
                        ["command"] = $"cmd /c if exist \"{mcpExePath}\" \"{mcpExePath}\" approve --source copilot",
                        ["timeoutSec"] = 60,
                    },
                },
            },
        };
        WriteAtomicJson(path, root);
    }

    // ---- 共通 helpers ----

    /// <summary>group (matcher + hooks[]) 内に amm の notify コマンドがあれば返す。</summary>
    private static string? FindAmmCommand(JsonNode? group)
    {
        if (group?["hooks"] is not JsonArray cmds) return null;
        foreach (var c in cmds)
        {
            var cmd = c?["command"]?.GetValue<string>();
            if (IsAmmNotifyCommand(cmd)) return cmd;
        }
        return null;
    }

    /// <summary>groups 配列から amm の notify エントリを含む group を除去。除去したら true。</summary>
    private static bool RemoveAmmGroups(JsonArray groups)
    {
        var removed = false;
        for (int i = groups.Count - 1; i >= 0; i--)
        {
            if (FindAmmCommand(groups[i]) == null) continue;
            // amm 以外のコマンドが同居している group は丸ごと消さず、amm の分だけ抜く
            if (groups[i]?["hooks"] is JsonArray cmds)
            {
                for (int j = cmds.Count - 1; j >= 0; j--)
                {
                    if (IsAmmNotifyCommand(cmds[j]?["command"]?.GetValue<string>()))
                    {
                        cmds.RemoveAt(j);
                        removed = true;
                    }
                }
                if (cmds.Count == 0) groups.RemoveAt(i);
            }
        }
        return removed;
    }

    private static bool IsAmmNotifyCommand(string? command) =>
        command != null
        && command.Contains("amm-mcp.exe", StringComparison.OrdinalIgnoreCase)
        && (command.Contains("notify", StringComparison.OrdinalIgnoreCase)
            || command.Contains("approve", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// 登録コマンドから exe パスを抜き出す。自己ガード形式
    /// (`cmd /c if exist "&lt;exe&gt;" "&lt;exe&gt;" notify ...`) と旧形式
    /// (`"&lt;exe&gt;" notify ...`) の両方に対応するため、最初に現れる
    /// amm-mcp.exe への引用パスを正規表現で拾う。
    /// </summary>
    private static string? ExtractExePath(string command)
    {
        var m = Regex.Match(command, "\"([^\"]*amm-mcp\\.exe)\"", RegexOptions.IgnoreCase);
        if (m.Success) return m.Groups[1].Value;
        var space = command.IndexOf(' ');
        return space > 0 ? command[..space] : command;
    }

    /// <summary>同一ディレクトリの一時ファイルに書いてから置換 (UTF-8 BOM なし)。</summary>
    private static void WriteAtomicJson(string path, JsonObject root)
    {
        var content = root.ToJsonString(new System.Text.Json.JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        }) + "\n";
        WriteAtomicText(path, content);
    }

    private static void WriteAtomicText(string path, string content)
        => AtomicFileWriter.Write(path, content);
}
