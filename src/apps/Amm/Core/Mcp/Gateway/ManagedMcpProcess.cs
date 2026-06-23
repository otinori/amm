using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Amm.Core.Mcp.Gateway;

/// <summary>
/// 単一の外部 stdio MCP サーバプロセスを管理する (req-20260622-mcp-gateway)。
/// JSON-RPC over stdin/stdout で initialize → tools/list → tools/call を処理する。
/// クラッシュ時は MaxRestarts 回まで自動再起動する。
/// </summary>
public sealed class ManagedMcpProcess : IAsyncDisposable
{
    public enum ServerStatus { Stopped, Starting, Running, Error }

    private readonly McpServerConfig _config;
    private readonly SemaphoreSlim _startLock = new(1, 1);
    private readonly ConcurrentDictionary<int, TaskCompletionSource<JsonNode?>> _pending = new();

    private Process? _process;
    private StreamWriter? _stdin;
    private Task? _readLoop;
    private CancellationTokenSource _cts = new();
    private int _nextId;
    private int _restartCount;

    public string Name => _config.Name;
    public ServerStatus Status { get; private set; } = ServerStatus.Stopped;
    public JsonArray? Tools { get; private set; }
    public string? LastError { get; private set; }

    public ManagedMcpProcess(McpServerConfig config)
    {
        _config = config;
    }

    /// <summary>プロセスを起動して initialize + tools/list を実行する。既に Running なら no-op。</summary>
    public async Task StartAsync(CancellationToken ct = default)
    {
        await _startLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (Status == ServerStatus.Running) return;
            await LaunchAndInitAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _startLock.Release();
        }
    }

    private async Task LaunchAndInitAsync(CancellationToken ct)
    {
        Status = ServerStatus.Starting;
        LastError = null;
        DisposeProcess();

        _cts = new CancellationTokenSource();
        var linkedCt = CancellationTokenSource.CreateLinkedTokenSource(ct, _cts.Token).Token;

        try
        {
            var psi = BuildProcessStartInfo();
            _process = new Process { StartInfo = psi, EnableRaisingEvents = true };
            _process.Exited += OnProcessExited;

            if (!_process.Start())
                throw new InvalidOperationException($"Failed to start process: {_config.Command}");

            _stdin = new StreamWriter(_process.StandardInput.BaseStream, new UTF8Encoding(false))
            {
                AutoFlush = true,
                NewLine = "\n",
            };

            _readLoop = Task.Run(() => ReadLoopAsync(_process.StandardOutput.BaseStream, _cts.Token));

            // initialize handshake
            var initResult = await SendRequestAsync("initialize", new JsonObject
            {
                ["protocolVersion"] = "2024-11-05",
                ["clientInfo"] = new JsonObject { ["name"] = "amm-gateway", ["version"] = "0.1" },
                ["capabilities"] = new JsonObject(),
            }, linkedCt).ConfigureAwait(false);

            if (initResult == null)
                throw new InvalidOperationException("No response to initialize");

            // tools/list
            var toolsResult = await SendRequestAsync("tools/list", new JsonObject(), linkedCt)
                .ConfigureAwait(false);
            Tools = toolsResult?["tools"] as JsonArray ?? [];

            Status = ServerStatus.Running;
            AppLogger.Info($"[gateway] {_config.Name} started ({Tools.Count} tools)");
        }
        catch (Exception ex)
        {
            Status = ServerStatus.Error;
            LastError = ex.Message;
            AppLogger.Error($"[gateway] {_config.Name} start failed", ex);
            DisposeProcess();
        }
    }

    private ProcessStartInfo BuildProcessStartInfo()
    {
        var psi = new ProcessStartInfo
        {
            FileName = _config.Command,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = false,
            CreateNoWindow = true,
            StandardInputEncoding = new UTF8Encoding(false),
            StandardOutputEncoding = new UTF8Encoding(false),
        };

        foreach (var arg in _config.Args)
            psi.ArgumentList.Add(arg);

        if (_config.Env != null)
        {
            foreach (var (key, value) in _config.Env)
                psi.Environment[key] = value;
        }

        return psi;
    }

    private void OnProcessExited(object? sender, EventArgs e)
    {
        AppLogger.Info($"[gateway] {_config.Name} exited (restarts={_restartCount}/{_config.MaxRestarts})");
        _cts.Cancel();
        DrainPending();

        if (_restartCount < _config.MaxRestarts)
        {
            _restartCount++;
            _ = Task.Delay(1000).ContinueWith(_ => StartAsync());
        }
        else
        {
            Status = ServerStatus.Error;
            LastError = "Process exited and max restarts reached";
        }
    }

    private async Task ReadLoopAsync(Stream stdout, CancellationToken ct)
    {
        using var reader = new StreamReader(stdout, new UTF8Encoding(false), false, 4096, leaveOpen: true);
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var line = await reader.ReadLineAsync(ct).ConfigureAwait(false);
                if (line == null) break;
                if (string.IsNullOrWhiteSpace(line)) continue;
                ProcessIncomingLine(line);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            AppLogger.Error($"[gateway] {_config.Name} read loop error", ex);
        }
    }

    private void ProcessIncomingLine(string line)
    {
        try
        {
            var node = JsonNode.Parse(line);
            if (node is not JsonObject obj) return;

            // Match by id to a pending request
            var idNode = obj["id"];
            if (idNode == null) return; // notification, ignore

            int id;
            try { id = idNode.GetValue<int>(); }
            catch { return; }

            if (!_pending.TryRemove(id, out var tcs)) return;

            if (obj["error"] != null)
                tcs.TrySetResult(new JsonObject { ["error"] = obj["error"]?.DeepClone() });
            else
                tcs.TrySetResult(obj["result"]?.DeepClone());
        }
        catch (JsonException) { /* malformed line, ignore */ }
        catch (Exception ex)
        {
            AppLogger.Error($"[gateway] {_config.Name} incoming parse error", ex);
        }
    }

    /// <summary>外部サーバへ JSON-RPC リクエストを送り、レスポンスを返す。</summary>
    public async Task<JsonNode?> SendRequestAsync(string method, JsonObject paramsObj, CancellationToken ct = default)
    {
        if (_stdin == null || Status == ServerStatus.Error || Status == ServerStatus.Stopped)
            throw new InvalidOperationException($"Server {_config.Name} is not running");

        var id = Interlocked.Increment(ref _nextId);
        var tcs = new TaskCompletionSource<JsonNode?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = tcs;

        var req = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id,
            ["method"] = method,
            ["params"] = paramsObj,
        };

        try
        {
            await _stdin.WriteLineAsync(req.ToJsonString().AsMemory(), ct).ConfigureAwait(false);
        }
        catch
        {
            _pending.TryRemove(id, out _);
            throw;
        }

        using var reg = ct.Register(() =>
        {
            if (_pending.TryRemove(id, out var removed))
                removed.TrySetCanceled(ct);
        });

        return await tcs.Task.ConfigureAwait(false);
    }

    /// <summary>tools/call を実行し MCP result ノードを返す。</summary>
    public async Task<JsonNode?> CallToolAsync(string toolName, JsonObject args, CancellationToken ct = default)
    {
        return await SendRequestAsync("tools/call", new JsonObject
        {
            ["name"] = toolName,
            ["arguments"] = args,
        }, ct).ConfigureAwait(false);
    }

    private void DrainPending()
    {
        foreach (var (_, tcs) in _pending)
            tcs.TrySetCanceled();
        _pending.Clear();
    }

    private void DisposeProcess()
    {
        DrainPending();
        _cts.Cancel();
        try { _process?.Kill(entireProcessTree: true); } catch { }
        _process?.Dispose();
        _process = null;
        _stdin?.Dispose();
        _stdin = null;
    }

    public async ValueTask DisposeAsync()
    {
        DisposeProcess();
        if (_readLoop != null)
        {
            try { await _readLoop.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
            catch { }
        }
        _startLock.Dispose();
        _cts.Dispose();
    }
}
