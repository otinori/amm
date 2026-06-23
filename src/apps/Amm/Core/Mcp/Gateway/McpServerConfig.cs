using System.Text.Json.Serialization;

namespace Amm.Core.Mcp.Gateway;

/// <summary>
/// profiles.amm の mcpServers エントリー (req-20260622-mcp-gateway)。
/// amm が外部 MCP サーバを stdio で管理し、ツールを集約して公開する設定。
/// </summary>
public sealed class McpServerConfig
{
    /// <summary>サーバ識別名。ツールプレフィックスに使用 (例: "fs" → "fs/read_file")。</summary>
    [JsonPropertyName("name")]
    public string Name { get; init; } = "";

    /// <summary>起動コマンド (例: "npx", "node", "python")。</summary>
    [JsonPropertyName("command")]
    public string Command { get; init; } = "";

    /// <summary>コマンド引数。</summary>
    [JsonPropertyName("args")]
    public string[] Args { get; init; } = [];

    /// <summary>追加/上書き環境変数。null の場合は親プロセスから継承のみ。</summary>
    [JsonPropertyName("env")]
    public Dictionary<string, string>? Env { get; init; }

    /// <summary>amm 起動時に自動起動する (既定 true)。</summary>
    [JsonPropertyName("autoStart")]
    public bool AutoStart { get; init; } = true;

    /// <summary>クラッシュ後の最大再起動回数 (既定 3)。0 で再起動なし。</summary>
    [JsonPropertyName("maxRestarts")]
    public int MaxRestarts { get; init; } = 3;
}
