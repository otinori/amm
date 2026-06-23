using System.Text.Json.Nodes;
using Amm.Core.Mcp;

namespace Amm.Tests;

/// <summary>
/// McpCliRegistrar の設定ファイル編集を一時ディレクトリ (homeDir 注入) で検証する。
/// 実際のユーザープロファイルには触れない。
/// </summary>
public class McpCliRegistrarTests : IDisposable
{
    private readonly string _home;
    private const string Exe = @"C:\Program Files\amm\amm-mcp.exe";

    public McpCliRegistrarTests()
    {
        _home = Path.Combine(Path.GetTempPath(), "amm-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_home);
    }

    public void Dispose()
    {
        try { Directory.Delete(_home, recursive: true); } catch { }
    }

    // ---- Claude Code (~/.claude.json) ----

    [Fact]
    public void Claude_Register_CreatesFileWithStdioEntry()
    {
        McpCliRegistrar.Register(McpCliKind.ClaudeCode, Exe, _home);

        var root = JsonNode.Parse(File.ReadAllText(Path.Combine(_home, ".claude.json")))!;
        var entry = root["mcpServers"]!["amm"]!;
        Assert.Equal("stdio", entry["type"]!.GetValue<string>());
        Assert.Equal(Exe, entry["command"]!.GetValue<string>());
        Assert.True(McpCliRegistrar.IsRegistered(McpCliKind.ClaudeCode, _home));
    }

    [Fact]
    public void Claude_Register_PreservesExistingContent()
    {
        var path = Path.Combine(_home, ".claude.json");
        File.WriteAllText(path,
            """{"numStartups":42,"projects":{"C:\\work":{"history":[]}},"mcpServers":{"other":{"command":"x.exe"}}}""");

        McpCliRegistrar.Register(McpCliKind.ClaudeCode, Exe, _home);

        var root = JsonNode.Parse(File.ReadAllText(path))!;
        Assert.Equal(42, root["numStartups"]!.GetValue<int>());
        Assert.NotNull(root["projects"]!["C:\\work"]);
        Assert.Equal("x.exe", root["mcpServers"]!["other"]!["command"]!.GetValue<string>());
        Assert.Equal(Exe, root["mcpServers"]!["amm"]!["command"]!.GetValue<string>());
    }

    [Fact]
    public void Claude_Unregister_RemovesOnlyAmmEntry()
    {
        McpCliRegistrar.Register(McpCliKind.ClaudeCode, Exe, _home);
        var path = Path.Combine(_home, ".claude.json");
        var withOther = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        withOther["mcpServers"]!["other"] = new JsonObject { ["command"] = "x.exe" };
        File.WriteAllText(path, withOther.ToJsonString());

        McpCliRegistrar.Unregister(McpCliKind.ClaudeCode, _home);

        var root = JsonNode.Parse(File.ReadAllText(path))!;
        Assert.Null(root["mcpServers"]!["amm"]);
        Assert.NotNull(root["mcpServers"]!["other"]);
        Assert.False(McpCliRegistrar.IsRegistered(McpCliKind.ClaudeCode, _home));
    }

    [Fact]
    public void Claude_Register_BrokenJson_ThrowsWithoutClobbering()
    {
        var path = Path.Combine(_home, ".claude.json");
        File.WriteAllText(path, "{ broken json");

        Assert.ThrowsAny<Exception>(() => McpCliRegistrar.Register(McpCliKind.ClaudeCode, Exe, _home));
        Assert.Equal("{ broken json", File.ReadAllText(path)); // 元ファイルは無傷
    }

    [Fact]
    public void Claude_Unregister_MissingFile_IsNoop()
    {
        McpCliRegistrar.Unregister(McpCliKind.ClaudeCode, _home);
        Assert.False(File.Exists(Path.Combine(_home, ".claude.json")));
    }

    // ---- Copilot CLI (~/.copilot/mcp-config.json) ----

    [Fact]
    public void Copilot_Register_CreatesLocalEntryWithTools()
    {
        McpCliRegistrar.Register(McpCliKind.CopilotCli, Exe, _home);

        var root = JsonNode.Parse(File.ReadAllText(Path.Combine(_home, ".copilot", "mcp-config.json")))!;
        var entry = root["mcpServers"]!["amm"]!;
        Assert.Equal("local", entry["type"]!.GetValue<string>());
        Assert.Equal(Exe, entry["command"]!.GetValue<string>());
        Assert.Equal("*", entry["tools"]![0]!.GetValue<string>());
    }

    // ---- Codex (~/.codex/config.toml) ----

    [Fact]
    public void Codex_Register_AppendsSectionWithLiteralPath()
    {
        var path = Path.Combine(_home, ".codex", "config.toml");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "model = \"o3\"\n\n[profiles.fast]\nmodel = \"o4-mini\"\n");

        McpCliRegistrar.Register(McpCliKind.Codex, Exe, _home);

        var text = File.ReadAllText(path);
        Assert.Contains("model = \"o3\"", text);          // 既存内容は保全
        Assert.Contains("[profiles.fast]", text);
        Assert.Contains("[mcp_servers.amm]", text);
        Assert.Contains($"command = '{Exe}'", text);      // literal string で \ エスケープ不要
        Assert.Equal(Exe, McpCliRegistrar.GetRegisteredCommand(McpCliKind.Codex, _home));
    }

    [Fact]
    public void Codex_Register_Twice_KeepsSingleSection()
    {
        McpCliRegistrar.Register(McpCliKind.Codex, Exe, _home);
        McpCliRegistrar.Register(McpCliKind.Codex, @"D:\new\amm-mcp.exe", _home);

        var text = File.ReadAllText(Path.Combine(_home, ".codex", "config.toml"));
        Assert.Equal(2, text.Split("[mcp_servers.amm]").Length); // セクションは 1 つだけ
        Assert.Equal(@"D:\new\amm-mcp.exe", McpCliRegistrar.GetRegisteredCommand(McpCliKind.Codex, _home));
    }

    [Fact]
    public void Codex_Unregister_RemovesOnlyAmmSection()
    {
        var path = Path.Combine(_home, ".codex", "config.toml");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path,
            "model = \"o3\"\n\n[mcp_servers.amm]\ncommand = 'C:\\old\\amm-mcp.exe'\nargs = []\n\n[mcp_servers.other]\ncommand = \"keep.exe\"\n");

        McpCliRegistrar.Unregister(McpCliKind.Codex, _home);

        var text = File.ReadAllText(path);
        Assert.DoesNotContain("[mcp_servers.amm]", text);
        Assert.DoesNotContain("amm-mcp.exe", text);
        Assert.Contains("model = \"o3\"", text);
        Assert.Contains("[mcp_servers.other]", text);     // 他サーバは保全
        Assert.Contains("command = \"keep.exe\"", text);
        Assert.False(McpCliRegistrar.IsRegistered(McpCliKind.Codex, _home));
    }

    [Fact]
    public void Codex_GetRegisteredCommand_ReadsBasicStringToo()
    {
        var path = Path.Combine(_home, ".codex", "config.toml");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "[mcp_servers.amm]\ncommand = \"C:\\\\x\\\\amm-mcp.exe\"\n");

        Assert.Equal(@"C:\x\amm-mcp.exe", McpCliRegistrar.GetRegisteredCommand(McpCliKind.Codex, _home));
    }
}
