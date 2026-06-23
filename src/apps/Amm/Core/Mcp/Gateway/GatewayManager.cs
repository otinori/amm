using System.Text.Json.Nodes;

namespace Amm.Core.Mcp.Gateway;

/// <summary>
/// 複数の外部 MCP サーバを管理し、ツールを集約して amm の MCP サーバ経由で公開する
/// (req-20260622-mcp-gateway)。ツール名は "{serverName}/{toolName}" のプレフィックス形式。
/// </summary>
public sealed class GatewayManager : IAsyncDisposable
{
    private readonly (McpServerConfig Config, ManagedMcpProcess Process)[] _entries;

    public GatewayManager(IEnumerable<McpServerConfig> configs)
    {
        _entries = configs
            .Select(c => (c, new ManagedMcpProcess(c)))
            .ToArray();
    }

    /// <summary>autoStart=true のサーバを全て並行起動する。</summary>
    public async Task StartAutoStartServersAsync(CancellationToken ct = default)
    {
        var tasks = _entries
            .Where(e => e.Config.AutoStart)
            .Select(e => e.Process.StartAsync(ct));
        await Task.WhenAll(tasks).ConfigureAwait(false);
    }

    /// <summary>
    /// 全サーバのツールを集約し、"{serverName}/{toolName}" プレフィックス付きで返す。
    /// Running 状態のサーバのみ含む。
    /// </summary>
    public JsonArray GetAggregatedTools()
    {
        var result = new JsonArray();
        foreach (var (_, server) in _entries)
        {
            if (server.Status != ManagedMcpProcess.ServerStatus.Running) continue;
            if (server.Tools == null) continue;

            foreach (var toolNode in server.Tools)
            {
                if (toolNode is not JsonObject tool) continue;
                var originalName = tool["name"]?.GetValue<string>();
                if (string.IsNullOrEmpty(originalName)) continue;

                var prefixedTool = new JsonObject
                {
                    ["name"] = $"{server.Name}/{originalName}",
                    ["description"] = $"[{server.Name}] {tool["description"]?.GetValue<string>() ?? ""}",
                };
                if (tool["inputSchema"] is JsonNode schema)
                    prefixedTool["inputSchema"] = schema.DeepClone();

                result.Add(prefixedTool);
            }
        }
        return result;
    }

    /// <summary>
    /// "{serverName}/{toolName}" 形式のツール名を解決し、対象サーバへ tools/call を転送する。
    /// 該当サーバが存在しない / Running でない場合は null を返す (呼び出し元でエラー化)。
    /// </summary>
    public async Task<JsonNode?> CallToolAsync(string prefixedName, JsonObject args, CancellationToken ct = default)
    {
        var slash = prefixedName.IndexOf('/');
        if (slash < 0) return null;

        var serverName = prefixedName[..slash];
        var toolName = prefixedName[(slash + 1)..];

        var entry = _entries.FirstOrDefault(e =>
            string.Equals(e.Process.Name, serverName, StringComparison.OrdinalIgnoreCase));

        if (entry.Process == null || entry.Process.Status != ManagedMcpProcess.ServerStatus.Running)
            return null;

        return await entry.Process.CallToolAsync(toolName, args, ct).ConfigureAwait(false);
    }

    /// <summary>prefixedName が Gateway の管理するサーバ+ツールに対応するか確認する。</summary>
    public bool IsGatewayTool(string prefixedName)
    {
        var slash = prefixedName.IndexOf('/');
        if (slash < 0) return false;
        var serverName = prefixedName[..slash];
        return _entries.Any(e => string.Equals(e.Process.Name, serverName, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>管理中サーバのスナップショット (名前 / ステータス / ツール数)。</summary>
    public GatewayServerInfo[] GetServerInfos() =>
        _entries.Select(e => new GatewayServerInfo(
            e.Process.Name,
            e.Process.Status,
            e.Process.Tools?.Count ?? 0,
            e.Process.LastError)).ToArray();

    public async ValueTask DisposeAsync()
    {
        foreach (var (_, process) in _entries)
            await process.DisposeAsync().ConfigureAwait(false);
    }
}

public sealed record GatewayServerInfo(
    string Name,
    ManagedMcpProcess.ServerStatus Status,
    int ToolCount,
    string? LastError);
