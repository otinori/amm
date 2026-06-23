using System.Text.Json.Nodes;
using Amm.Core.Mcp;

namespace Amm.Tests;

/// <summary>
/// HookCliRegistrar (Claude Code / Codex / Copilot CLI への hook 登録,
/// UDR-amm-20260605T0523-7e1) を一時ディレクトリ (homeDir 注入) で検証する。
/// 実際のユーザープロファイルには触れない。
/// </summary>
public class HookCliRegistrarTests : IDisposable
{
    private readonly string _home;
    private const string Exe = @"C:\Program Files\amm\amm-mcp.exe";

    public HookCliRegistrarTests()
    {
        _home = Path.Combine(Path.GetTempPath(), "amm-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_home);
    }

    public void Dispose()
    {
        try { Directory.Delete(_home, recursive: true); } catch { }
    }

    private string SettingsPath => Path.Combine(_home, ".claude", "settings.json");
    private string CodexConfigPath => Path.Combine(_home, ".codex", "config.toml");
    private string CopilotHooksPath => Path.Combine(_home, ".copilot", "hooks", "amm-hooks.json");

    // ================================================================
    // Claude Code (~/.claude/settings.json)
    // ================================================================

    [Fact]
    public void Register_CreatesFileWithStopNotificationAndPermissionRequestHooks()
    {
        HookCliRegistrar.Register(McpCliKind.ClaudeCode, Exe, _home);

        var root = JsonNode.Parse(File.ReadAllText(SettingsPath))!;
        foreach (var (ev, subCommand, timeout) in new[]
        {
            ("Stop", "notify", 10),
            ("Notification", "notify", 10),
            ("PermissionRequest", "approve", 60),
        })
        {
            var groups = root["hooks"]![ev] as JsonArray;
            Assert.NotNull(groups);
            Assert.Single(groups!);
            var cmd = groups![0]!["hooks"]![0]!;
            Assert.Equal("command", cmd["type"]!.GetValue<string>());
            var commandLine = cmd["command"]!.GetValue<string>();
            Assert.Contains(Exe, commandLine);
            Assert.Contains(subCommand, commandLine);
            Assert.Equal(timeout, cmd["timeout"]!.GetValue<int>());
            // 自己ガード形式: アンインストール後 (exe 消失後) に発火しても
            // 静かに no-op するため、cmd の if exist で実行を条件化する
            Assert.StartsWith($"cmd /c if exist \"{Exe}\"", commandLine);
        }
        Assert.True(HookCliRegistrar.IsRegistered(McpCliKind.ClaudeCode, _home));
        Assert.Equal(Exe, HookCliRegistrar.GetRegisteredCommand(McpCliKind.ClaudeCode, _home));
    }

    [Fact]
    public void Unregister_RemovesPermissionRequestEntryToo()
    {
        HookCliRegistrar.Register(McpCliKind.ClaudeCode, Exe, _home);
        HookCliRegistrar.Unregister(McpCliKind.ClaudeCode, _home);

        var root = JsonNode.Parse(File.ReadAllText(SettingsPath))!;
        Assert.Null(root["hooks"]); // 全イベント (approve 含む) が除去され hooks キーごと消える
    }

    [Fact]
    public void Register_ReplacesLegacyUnguardedEntry()
    {
        // 旧形式 (`"<exe>" notify --source claude`、ガードなし) で登録済みの
        // settings.json も amm エントリとして認識し、ガード付きに置換できる
        Directory.CreateDirectory(Path.Combine(_home, ".claude"));
        File.WriteAllText(SettingsPath,
            """{"hooks":{"Stop":[{"hooks":[{"type":"command","command":"\"D:\\old\\amm-mcp.exe\" notify --source claude","timeout":10}]}]}}""");

        Assert.Equal(@"D:\old\amm-mcp.exe", HookCliRegistrar.GetRegisteredCommand(McpCliKind.ClaudeCode, _home));

        HookCliRegistrar.Register(McpCliKind.ClaudeCode, Exe, _home);

        var root = JsonNode.Parse(File.ReadAllText(SettingsPath))!;
        var stopGroups = (JsonArray)root["hooks"]!["Stop"]!;
        Assert.Single(stopGroups); // 旧エントリは置換され重複しない
        Assert.StartsWith("cmd /c if exist", stopGroups[0]!["hooks"]![0]!["command"]!.GetValue<string>());
        Assert.Equal(Exe, HookCliRegistrar.GetRegisteredCommand(McpCliKind.ClaudeCode, _home));
    }

    [Fact]
    public void Register_PreservesExistingSettingsAndForeignHooks()
    {
        Directory.CreateDirectory(Path.Combine(_home, ".claude"));
        File.WriteAllText(SettingsPath,
            """{"model":"opus","hooks":{"Stop":[{"hooks":[{"type":"command","command":"my-own-hook.exe"}]}]}}""");

        HookCliRegistrar.Register(McpCliKind.ClaudeCode, Exe, _home);

        var root = JsonNode.Parse(File.ReadAllText(SettingsPath))!;
        Assert.Equal("opus", root["model"]!.GetValue<string>());
        var stopGroups = root["hooks"]!["Stop"] as JsonArray;
        Assert.Equal(2, stopGroups!.Count); // ユーザーの hook + amm の hook
        Assert.Equal("my-own-hook.exe",
            stopGroups[0]!["hooks"]![0]!["command"]!.GetValue<string>());
    }

    [Fact]
    public void Register_IsIdempotent()
    {
        HookCliRegistrar.Register(McpCliKind.ClaudeCode, Exe, _home);
        HookCliRegistrar.Register(McpCliKind.ClaudeCode, Exe, _home);

        var root = JsonNode.Parse(File.ReadAllText(SettingsPath))!;
        Assert.Single((JsonArray)root["hooks"]!["Stop"]!);
        Assert.Single((JsonArray)root["hooks"]!["Notification"]!);
    }

    [Fact]
    public void Register_UpdatesStalePath()
    {
        HookCliRegistrar.Register(McpCliKind.ClaudeCode, @"D:\old\amm-mcp.exe", _home);
        HookCliRegistrar.Register(McpCliKind.ClaudeCode, Exe, _home);

        Assert.Equal(Exe, HookCliRegistrar.GetRegisteredCommand(McpCliKind.ClaudeCode, _home));
        var root = JsonNode.Parse(File.ReadAllText(SettingsPath))!;
        Assert.Single((JsonArray)root["hooks"]!["Stop"]!); // 旧パスのエントリは置換済み
    }

    [Fact]
    public void Unregister_RemovesOnlyAmmEntries()
    {
        Directory.CreateDirectory(Path.Combine(_home, ".claude"));
        File.WriteAllText(SettingsPath,
            """{"hooks":{"Stop":[{"hooks":[{"type":"command","command":"my-own-hook.exe"}]}]}}""");
        HookCliRegistrar.Register(McpCliKind.ClaudeCode, Exe, _home);

        HookCliRegistrar.Unregister(McpCliKind.ClaudeCode, _home);

        Assert.False(HookCliRegistrar.IsRegistered(McpCliKind.ClaudeCode, _home));
        var root = JsonNode.Parse(File.ReadAllText(SettingsPath))!;
        var stopGroups = root["hooks"]!["Stop"] as JsonArray;
        Assert.Single(stopGroups!); // ユーザーの hook は残る
        Assert.Equal("my-own-hook.exe",
            stopGroups![0]!["hooks"]![0]!["command"]!.GetValue<string>());
        Assert.Null(root["hooks"]!["Notification"]); // amm しか居なかった配列はキーごと除去
    }

    [Fact]
    public void Unregister_MissingFile_DoesNothing()
    {
        HookCliRegistrar.Unregister(McpCliKind.ClaudeCode, _home); // 例外にならない
        Assert.False(File.Exists(SettingsPath));
    }

    [Fact]
    public void IsRegistered_UnparsableFile_ReturnsFalse()
    {
        Directory.CreateDirectory(Path.Combine(_home, ".claude"));
        File.WriteAllText(SettingsPath, "{ broken json");
        Assert.False(HookCliRegistrar.IsRegistered(McpCliKind.ClaudeCode, _home));
    }

    // ================================================================
    // Codex (~/.codex/config.toml の notify + [tui] notifications)
    // ================================================================

    [Fact]
    public void Codex_Register_CreatesNotifyAndTuiNotifications()
    {
        HookCliRegistrar.Register(McpCliKind.Codex, Exe, _home);

        var text = File.ReadAllText(CodexConfigPath);
        Assert.Contains($"notify = ['{Exe}', 'notify', '--source', 'codex']", text);
        Assert.Contains("[tui] # added by amm", text);
        Assert.Contains("notifications = [\"agent-turn-complete\", \"approval-requested\"] # added by amm", text);
        Assert.Contains("notification_method = \"osc9\" # added by amm", text);
        Assert.True(HookCliRegistrar.IsRegistered(McpCliKind.Codex, _home));
        Assert.Equal(Exe, HookCliRegistrar.GetRegisteredCommand(McpCliKind.Codex, _home));
    }

    [Fact]
    public void Codex_Register_InsertsNotifyBeforeFirstTableHeader()
    {
        Directory.CreateDirectory(Path.Combine(_home, ".codex"));
        File.WriteAllText(CodexConfigPath, "model = \"o3\"\n\n[mcp_servers.amm]\ncommand = 'x'\n");

        HookCliRegistrar.Register(McpCliKind.Codex, Exe, _home);

        var lines = File.ReadAllLines(CodexConfigPath);
        int notifyIdx = Array.FindIndex(lines, l => l.StartsWith("notify ="));
        int firstHeaderIdx = Array.FindIndex(lines, l => l.StartsWith("["));
        Assert.True(notifyIdx >= 0, "notify 行が存在する");
        Assert.True(notifyIdx < firstHeaderIdx, "notify はルートスコープ (最初のテーブルより前) に入る");
        Assert.Contains("model = \"o3\"", lines); // 既存ルートキーは保全
    }

    [Fact]
    public void Codex_Register_ForeignNotify_Throws()
    {
        Directory.CreateDirectory(Path.Combine(_home, ".codex"));
        File.WriteAllText(CodexConfigPath, "notify = ['my-notifier.exe']\n");

        // notify は単一キー: ユーザーの設定を黙って上書きしない
        Assert.Throws<InvalidOperationException>(
            () => HookCliRegistrar.Register(McpCliKind.Codex, Exe, _home));
        Assert.Contains("my-notifier.exe", File.ReadAllText(CodexConfigPath)); // 無傷
        Assert.False(HookCliRegistrar.IsRegistered(McpCliKind.Codex, _home));
    }

    [Fact]
    public void Codex_Register_PreservesUserTuiNotifications()
    {
        Directory.CreateDirectory(Path.Combine(_home, ".codex"));
        File.WriteAllText(CodexConfigPath, "[tui]\nnotifications = true\n");

        HookCliRegistrar.Register(McpCliKind.Codex, Exe, _home);

        var text = File.ReadAllText(CodexConfigPath);
        Assert.Contains("notifications = true", text); // ユーザー設定はそのまま
        Assert.DoesNotContain("notification_method", text); // method も足さない
        Assert.Contains("notify = ", text); // notify 自体は登録される
    }

    [Fact]
    public void Codex_Register_UpdatesStalePath()
    {
        HookCliRegistrar.Register(McpCliKind.Codex, @"D:\old\amm-mcp.exe", _home);
        HookCliRegistrar.Register(McpCliKind.Codex, Exe, _home);

        var text = File.ReadAllText(CodexConfigPath);
        Assert.DoesNotContain(@"D:\old", text);
        Assert.Equal(Exe, HookCliRegistrar.GetRegisteredCommand(McpCliKind.Codex, _home));
        // notify 行は 1 本のまま (重複しない)
        Assert.Single(File.ReadAllLines(CodexConfigPath), l => l.StartsWith("notify ="));
    }

    [Fact]
    public void Codex_Unregister_RemovesOnlyAmmLines()
    {
        Directory.CreateDirectory(Path.Combine(_home, ".codex"));
        File.WriteAllText(CodexConfigPath, "model = \"o3\"\n\n[mcp_servers.amm]\ncommand = 'x'\n");
        HookCliRegistrar.Register(McpCliKind.Codex, Exe, _home);

        HookCliRegistrar.Unregister(McpCliKind.Codex, _home);

        var text = File.ReadAllText(CodexConfigPath);
        Assert.False(HookCliRegistrar.IsRegistered(McpCliKind.Codex, _home));
        Assert.DoesNotContain("notify =", text);
        Assert.DoesNotContain("# added by amm", text);
        Assert.Contains("model = \"o3\"", text);          // 既存ルートキーは保全
        Assert.Contains("[mcp_servers.amm]", text);       // MCP 登録 (別系統) は保全
    }

    [Fact]
    public void Codex_Unregister_KeepsTuiSectionIfUserAddedKeys()
    {
        HookCliRegistrar.Register(McpCliKind.Codex, Exe, _home);
        // ユーザーが後から [tui] にキーを足したケース
        var lines = File.ReadAllLines(CodexConfigPath).ToList();
        int tuiIdx = lines.FindIndex(l => l.StartsWith("[tui]"));
        lines.Insert(tuiIdx + 1, "theme = \"dark\"");
        File.WriteAllLines(CodexConfigPath, lines);

        HookCliRegistrar.Unregister(McpCliKind.Codex, _home);

        var text = File.ReadAllText(CodexConfigPath);
        Assert.Contains("[tui]", text);          // セクションは残す
        Assert.Contains("theme = \"dark\"", text); // ユーザーのキーは保全
        Assert.DoesNotContain("# added by amm", text);
    }

    // ================================================================
    // Copilot CLI (~/.copilot/hooks/amm-hooks.json — 専有ファイル)
    // ================================================================

    [Fact]
    public void Copilot_Register_CreatesHooksFile()
    {
        HookCliRegistrar.Register(McpCliKind.CopilotCli, Exe, _home);

        var root = JsonNode.Parse(File.ReadAllText(CopilotHooksPath))!;
        Assert.Equal(1, root["version"]!.GetValue<int>());

        var agentStop = (JsonArray)root["hooks"]!["agentStop"]!;
        var stopCmd = agentStop[0]!["command"]!.GetValue<string>();
        Assert.StartsWith($"cmd /c if exist \"{Exe}\"", stopCmd);
        Assert.Contains("notify --state idle --source copilot", stopCmd);
        Assert.Equal(10, agentStop[0]!["timeoutSec"]!.GetValue<int>());

        var permission = (JsonArray)root["hooks"]!["permissionRequest"]!;
        var permCmd = permission[0]!["command"]!.GetValue<string>();
        Assert.Contains("approve --source copilot", permCmd);
        // 待ち時間の鎖: 台帳 45s < approve 読み取り 55s < hook 60s
        Assert.Equal(60, permission[0]!["timeoutSec"]!.GetValue<int>());

        Assert.True(HookCliRegistrar.IsRegistered(McpCliKind.CopilotCli, _home));
        Assert.Equal(Exe, HookCliRegistrar.GetRegisteredCommand(McpCliKind.CopilotCli, _home));
    }

    [Fact]
    public void Copilot_Unregister_DeletesFile()
    {
        HookCliRegistrar.Register(McpCliKind.CopilotCli, Exe, _home);
        HookCliRegistrar.Unregister(McpCliKind.CopilotCli, _home);

        Assert.False(File.Exists(CopilotHooksPath)); // 専有ファイルごと削除
        Assert.False(HookCliRegistrar.IsRegistered(McpCliKind.CopilotCli, _home));
    }

    [Fact]
    public void Copilot_Register_UpdatesStalePath()
    {
        HookCliRegistrar.Register(McpCliKind.CopilotCli, @"D:\old\amm-mcp.exe", _home);
        HookCliRegistrar.Register(McpCliKind.CopilotCli, Exe, _home);

        Assert.Equal(Exe, HookCliRegistrar.GetRegisteredCommand(McpCliKind.CopilotCli, _home));
        Assert.DoesNotContain(@"D:\\old", File.ReadAllText(CopilotHooksPath));
    }
}
