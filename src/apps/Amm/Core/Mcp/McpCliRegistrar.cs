using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Amm.Core.Mcp;

/// <summary>
/// 対象 CLI の識別子。表示順 = この enum の定義順。
/// </summary>
public enum McpCliKind
{
    ClaudeCode,
    Codex,
    CopilotCli,
}

/// <summary>
/// AI CLI (Claude Code / Codex / GitHub Copilot CLI) のユーザー (端末) スコープ
/// 設定ファイルに amm の stdio MCP サーバ (amm-mcp.exe) を登録 / 削除する。
///
/// CLI コマンド (`claude mcp add` 等) は経由せず、各 CLI が読むファイルを直接
/// 編集する。CLI が PATH に無くても動作し、何がどこに書かれるかが明示される。
///
/// - Claude Code : ~/.claude.json            ルート mcpServers (user scope)
/// - Codex       : ~/.codex/config.toml      [mcp_servers.amm] セクション
/// - Copilot CLI : ~/.copilot/mcp-config.json mcpServers (type=local)
///
/// 書き込みは「既存内容を保全して該当エントリのみ追加 / 削除」し、
/// 同一ディレクトリの一時ファイル経由で置換する (途中失敗で壊さない)。
/// JSON の整形 (インデント) は書き換え時に再整形されるが内容は保全される。
/// </summary>
public static class McpCliRegistrar
{
    /// <summary>各 CLI に登録する MCP サーバ名。</summary>
    public const string ServerName = "amm";

    /// <summary>amm-mcp.exe の絶対パス (実行中の amm.exe と同じフォルダ)。</summary>
    public static string ResolveMcpExePath() =>
        Path.Combine(AppContext.BaseDirectory, "amm-mcp.exe");

    public static string DisplayName(McpCliKind kind) => kind switch
    {
        McpCliKind.ClaudeCode => "Claude Code",
        McpCliKind.Codex => "Codex",
        McpCliKind.CopilotCli => "Copilot CLI",
        _ => kind.ToString(),
    };

    /// <summary>対象設定ファイルの絶対パス。homeDir はテスト用注入点 (既定: ユーザープロファイル)。</summary>
    public static string ConfigPath(McpCliKind kind, string? homeDir = null)
    {
        var home = homeDir ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return kind switch
        {
            McpCliKind.ClaudeCode => Path.Combine(home, ".claude.json"),
            McpCliKind.Codex => Path.Combine(home, ".codex", "config.toml"),
            McpCliKind.CopilotCli => Path.Combine(home, ".copilot", "mcp-config.json"),
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };
    }

    /// <summary>登録済みなら true。ファイル無し / 解析不能は false。</summary>
    public static bool IsRegistered(McpCliKind kind, string? homeDir = null) =>
        GetRegisteredCommand(kind, homeDir) != null;

    /// <summary>
    /// 登録済みエントリの command (exe パス) を返す。未登録 / ファイル無しは null。
    /// パスが古い (移動済み) 場合の再登録判定に使う。
    /// </summary>
    public static string? GetRegisteredCommand(McpCliKind kind, string? homeDir = null)
    {
        var path = ConfigPath(kind, homeDir);
        if (!File.Exists(path)) return null;
        try
        {
            if (kind == McpCliKind.Codex)
            {
                var (start, end, command) = FindTomlSection(File.ReadAllLines(path));
                return start >= 0 ? command ?? string.Empty : null;
            }
            var root = JsonNode.Parse(File.ReadAllText(path)) as JsonObject;
            var entry = root?["mcpServers"]?[ServerName];
            return entry == null ? null : entry["command"]?.GetValue<string>() ?? string.Empty;
        }
        catch
        {
            return null; // 解析不能 → 未登録扱い (Register 時に明示エラーにする)
        }
    }

    /// <summary>amm エントリを登録 (既存エントリは上書き)。失敗は例外。</summary>
    public static void Register(McpCliKind kind, string mcpExePath, string? homeDir = null)
    {
        var path = ConfigPath(kind, homeDir);
        if (kind == McpCliKind.Codex)
        {
            RegisterToml(path, mcpExePath);
            return;
        }

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

        if (root["mcpServers"] is not JsonObject servers)
        {
            servers = new JsonObject();
            root["mcpServers"] = servers;
        }

        servers[ServerName] = kind == McpCliKind.ClaudeCode
            ? new JsonObject
            {
                ["type"] = "stdio",
                ["command"] = mcpExePath,
                ["args"] = new JsonArray(),
                ["env"] = new JsonObject(),
            }
            : new JsonObject
            {
                ["type"] = "local",
                ["command"] = mcpExePath,
                ["args"] = new JsonArray(),
                ["tools"] = new JsonArray("*"),
            };

        WriteAtomic(path, root.ToJsonString(new System.Text.Json.JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        }) + "\n");
    }

    /// <summary>amm エントリを削除。未登録 / ファイル無しは何もしない。失敗は例外。</summary>
    public static void Unregister(McpCliKind kind, string? homeDir = null)
    {
        var path = ConfigPath(kind, homeDir);
        if (!File.Exists(path)) return;

        if (kind == McpCliKind.Codex)
        {
            UnregisterToml(path);
            return;
        }

        var root = JsonNode.Parse(File.ReadAllText(path)) as JsonObject
            ?? throw new InvalidDataException($"{path} のルートが JSON オブジェクトではありません。");
        if (root["mcpServers"] is not JsonObject servers || !servers.ContainsKey(ServerName))
            return;
        servers.Remove(ServerName);
        WriteAtomic(path, root.ToJsonString(new System.Text.Json.JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        }) + "\n");
    }

    // ---- Codex config.toml (テキスト操作。TOML ライブラリ非依存) ----

    // セクションヘッダは [mcp_servers.amm] / [mcp_servers."amm"] の 2 形式を許容
    private static readonly Regex SectionHeader = new(
        $@"^\s*\[mcp_servers\.(""{ServerName}""|{ServerName})\]\s*(#.*)?$", RegexOptions.Compiled);
    private static readonly Regex AnyTableHeader = new(@"^\s*\[", RegexOptions.Compiled);
    private static readonly Regex CommandLine = new(
        @"^\s*command\s*=\s*(?:'(?<lit>[^']*)'|""(?<basic>[^""]*)"")\s*(#.*)?$", RegexOptions.Compiled);

    /// <summary>
    /// [mcp_servers.amm] セクションの (開始行, 次セクション直前の終了行+1, command 値) を返す。
    /// 見つからなければ start = -1。
    /// </summary>
    private static (int start, int end, string? command) FindTomlSection(string[] lines)
    {
        for (int i = 0; i < lines.Length; i++)
        {
            if (!SectionHeader.IsMatch(lines[i])) continue;
            int end = i + 1;
            string? command = null;
            while (end < lines.Length && !AnyTableHeader.IsMatch(lines[end]))
            {
                var m = CommandLine.Match(lines[end]);
                if (m.Success)
                {
                    // literal string ('...') はそのまま、basic string ("...") は \\ → \ のみ解決
                    command = m.Groups["lit"].Success
                        ? m.Groups["lit"].Value
                        : m.Groups["basic"].Value.Replace(@"\\", @"\");
                }
                end++;
            }
            return (i, end, command);
        }
        return (-1, -1, null);
    }

    private static void RegisterToml(string path, string mcpExePath)
    {
        var lines = File.Exists(path)
            ? File.ReadAllLines(path).ToList()
            : new List<string>();

        // 既存セクションは除去してから末尾に追記 (上書き登録)
        var (start, end, _) = FindTomlSection(lines.ToArray());
        if (start >= 0) lines.RemoveRange(start, end - start);

        // 末尾の空行を 1 つに整えてからセクションを足す
        while (lines.Count > 0 && string.IsNullOrWhiteSpace(lines[^1])) lines.RemoveAt(lines.Count - 1);
        if (lines.Count > 0) lines.Add(string.Empty);
        lines.Add($"[mcp_servers.{ServerName}]");
        // パスに ' が含まれても TOML が壊れないよう TomlEscape 経由で出力する
        // (通常は literal string、' 含有時は basic string へ自動切替)。
        lines.Add($"command = {TomlEscape.Str(mcpExePath)}");
        lines.Add($"args = []");

        WriteAtomic(path, string.Join("\n", lines) + "\n");
    }

    private static void UnregisterToml(string path)
    {
        var lines = File.ReadAllLines(path).ToList();
        var (start, end, _) = FindTomlSection(lines.ToArray());
        if (start < 0) return;
        lines.RemoveRange(start, end - start);
        // 削除位置の直前直後が共に空行なら 1 つへ畳む
        while (start > 0 && start < lines.Count &&
               string.IsNullOrWhiteSpace(lines[start - 1]) && string.IsNullOrWhiteSpace(lines[start]))
        {
            lines.RemoveAt(start);
        }
        // 末尾の空行は除去
        while (lines.Count > 0 && string.IsNullOrWhiteSpace(lines[^1])) lines.RemoveAt(lines.Count - 1);
        WriteAtomic(path, lines.Count == 0 ? string.Empty : string.Join("\n", lines) + "\n");
    }

    /// <summary>同一ディレクトリの一時ファイルに書いてから置換 (UTF-8 BOM なし)。</summary>
    private static void WriteAtomic(string path, string content)
        => AtomicFileWriter.Write(path, content);
}
