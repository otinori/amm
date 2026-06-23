using System.IO.Pipes;
using System.Text;
using System.Text.Json.Nodes;

namespace Amm.PowerShell.Pipe;

/// <summary>
/// amm.exe の Named Pipe への JSON-RPC クライアント。
/// 各 cmdlet からインスタンスを作成して使い捨てる (1 リクエスト = 1 接続)。
/// パイプ名は amm-mcp.exe と同じ規約: amm-mcp-{UserName}。
/// </summary>
internal sealed class AmmPipeClient : IDisposable
{
    private readonly NamedPipeClientStream _pipe;
    private readonly StreamWriter _writer;
    private readonly StreamReader _reader;
    private int _nextId;
    private bool _disposed;

    public static string DefaultPipeName =>
        Environment.GetEnvironmentVariable("AMM_MCP_PIPE_NAME")
        ?? $"amm-mcp-{Environment.UserName}";

    public AmmPipeClient(string? pipeName = null, int connectTimeoutMs = 5000)
    {
        var name = pipeName ?? DefaultPipeName;
        _pipe = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
        _pipe.Connect(connectTimeoutMs);
        _writer = new StreamWriter(_pipe, new UTF8Encoding(false), 4096, leaveOpen: true)
        {
            AutoFlush = true,
            NewLine = "\n",
        };
        _reader = new StreamReader(_pipe, new UTF8Encoding(false), false, 4096, leaveOpen: true);
    }

    /// <summary>
    /// JSON-RPC リクエストを送り、レスポンスオブジェクトを返す。
    /// エラーレスポンスの場合は InvalidOperationException をスロー。
    /// </summary>
    public JsonObject SendRequest(string method, object? @params = null, int readTimeoutMs = 0)
    {
        var id = ++_nextId;
        var req = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id,
            ["method"] = method,
        };
        if (@params != null)
        {
            var paramsJson = System.Text.Json.JsonSerializer.Serialize(@params,
                new System.Text.Json.JsonSerializerOptions
                {
                    PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.SnakeCaseLower,
                });
            req["params"] = JsonNode.Parse(paramsJson);
        }

        _writer.WriteLine(req.ToJsonString());

        // レスポンス読み取り (readTimeoutMs=0 は無制限 — Wait-AmmIdle 用)
        string? line;
        if (readTimeoutMs > 0)
        {
            using var cts = new CancellationTokenSource(readTimeoutMs);
            line = _reader.ReadLineAsync(cts.Token).AsTask().GetAwaiter().GetResult();
        }
        else
        {
            line = _reader.ReadLine();
        }

        if (line == null)
            throw new InvalidOperationException("amm-mcp: pipe closed before response arrived.");

        if (JsonNode.Parse(line) is not JsonObject resp)
            throw new InvalidOperationException($"amm-mcp: unexpected response: {line}");

        if (resp["error"] is JsonObject err)
        {
            var msg = err["message"]?.GetValue<string>() ?? "unknown error";
            throw new InvalidOperationException($"amm-mcp: server error: {msg}");
        }

        return resp;
    }

    /// <summary>
    /// result オブジェクトを取り出す。SendRequest の薄いラッパー。
    /// </summary>
    public JsonObject? GetResult(string method, object? @params = null, int readTimeoutMs = 0)
    {
        var resp = SendRequest(method, @params, readTimeoutMs);
        return resp["result"] as JsonObject;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _writer.Dispose(); } catch { }
        try { _reader.Dispose(); } catch { }
        try { _pipe.Dispose(); } catch { }
    }
}
