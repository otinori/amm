using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Amm.Core.Mcp.Gateway;

namespace Amm.Core.Mcp;

/// <summary>
/// amm GUI 内に常駐する MCP/JSON-RPC over Named Pipe サーバ
/// (UDR-amm-20260427T0225-7a3)。
///
/// パイプ名は規定で <c>amm-mcp-{Environment.UserName}</c> (multi-instance)。
/// パイプ ACL は current user のみへ明示的に付与し、同一ログオンユーザーの
/// プロセスからのみ接続できるようにする。MCP 公式仕様
/// (initialize / tools/list / tools/call) のうち AMM 用に必要な最小セットだけを
/// 実装する。フレーミングは NDJSON (1 line = 1 JSON)。
/// </summary>
public sealed class McpPipeServer : IAsyncDisposable
{
    public const string ProtocolVersion = "2024-11-05";
    public const string ServerName = "amm-operator";
    public const string ServerVersion = "0.3.0";

    private readonly MessageDispatcher _dispatcher;
    private readonly ApprovalBroker? _approvalBroker;
    private readonly WaitBroker? _waitBroker;
    private volatile GatewayManager? _gateway;
    private readonly string _pipeName;
    private readonly CancellationTokenSource _cts = new();
    private Task? _acceptLoop;

    public string PipeName => _pipeName;

    public McpPipeServer(MessageDispatcher dispatcher, string? pipeName = null,
        ApprovalBroker? approvalBroker = null, WaitBroker? waitBroker = null,
        GatewayManager? gateway = null)
    {
        _dispatcher = dispatcher;
        _approvalBroker = approvalBroker;
        _waitBroker = waitBroker;
        _gateway = gateway;
        _pipeName = pipeName ?? $"amm-mcp-{Environment.UserName}";
    }

    /// <summary>実行中のゲートウェイを差し替える (設定変更時のホットリロード)。</summary>
    public void UpdateGateway(GatewayManager? gateway) => _gateway = gateway;

    public void Start()
    {
        _acceptLoop = Task.Run(() => AcceptLoopAsync(_cts.Token));
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            NamedPipeServerStream? pipe = null;
            try
            {
                pipe = CreateServerStream();
                await pipe.WaitForConnectionAsync(ct).ConfigureAwait(false);
                var captured = pipe;
                _ = Task.Run(() => HandleSessionAsync(captured, ct), ct);
            }
            catch (OperationCanceledException)
            {
                pipe?.Dispose();
                return;
            }
            catch (Exception ex)
            {
                AppLogger.Error("[mcp] accept loop error", ex);
                pipe?.Dispose();
                // 続行 (一時的なパイプ作成失敗から回復させる)
                await Task.Delay(500, ct).ConfigureAwait(false);
            }
        }
    }

    private NamedPipeServerStream CreateServerStream()
    {
        var currentUser = WindowsIdentity.GetCurrent().User
            ?? throw new InvalidOperationException("Current Windows user SID is unavailable.");
        var security = new PipeSecurity();
        security.SetOwner(currentUser);
        security.SetAccessRule(new PipeAccessRule(
            currentUser,
            PipeAccessRights.FullControl,
            AccessControlType.Allow));

        return NamedPipeServerStreamAcl.Create(
                    _pipeName,
                    PipeDirection.InOut,
                    NamedPipeServerStream.MaxAllowedServerInstances,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous,
                    0,
                    0,
                    security);
    }

    private async Task HandleSessionAsync(NamedPipeServerStream pipe, CancellationToken ct)
    {
        // セッション (= 1 パイプ接続) の生存トークン。amm/approval の人間回答待ちに
        // 渡し、接続が切れたら ApprovalBroker 側で「決定なし」解放させる
        // (hook プロセス消滅後の幽霊要求がポップアップに残らない)。
        using var sessionCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        try
        {
            using var reader = new StreamReader(pipe, new UTF8Encoding(false), false, 4096, leaveOpen: true);
            using var writer = new StreamWriter(pipe, new UTF8Encoding(false), 4096, leaveOpen: true)
            {
                AutoFlush = true,
                NewLine = "\n",
            };

            while (!ct.IsCancellationRequested && pipe.IsConnected)
            {
                // ReadLineAsync は改行が来るまで読み続けるため、改行なしの巨大
                // ペイロードを送られると 1 行分が丸ごとヒープに乗り OOM し得る
                // (同一ユーザー権限プロセスからのメモリ DoS)。1 行の上限を設け、
                // 超過したセッションは切断する。
                var line = await ReadLineBoundedAsync(reader, ct).ConfigureAwait(false);
                if (line == null) break;
                if (string.IsNullOrWhiteSpace(line)) continue;

                var response = await HandleLineAsync(line, sessionCts.Token).ConfigureAwait(false);
                if (response != null)
                    await writer.WriteLineAsync(response).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { /* ignore */ }
        catch (IOException) { /* peer closed */ }
        catch (InvalidDataException ex)
        {
            // 行長超過。セッションを閉じるだけ (ログのみ)。
            AppLogger.Error("[mcp] session aborted (payload too large)", ex);
        }
        catch (Exception ex)
        {
            AppLogger.Error("[mcp] session error", ex);
        }
        finally
        {
            sessionCts.Cancel(); // 保留中の approval を解放
            try { pipe.Dispose(); } catch { }
        }
    }

    /// <summary>1 リクエスト (= 1 行) の最大長。これを超えたら <see cref="InvalidDataException"/>。</summary>
    private const int MaxLineLength = 1 * 1024 * 1024; // 1 MiB

    /// <summary>
    /// <see cref="StreamReader.ReadLineAsync(CancellationToken)"/> と同等だが、1 行が
    /// <see cref="MaxLineLength"/> を超えると例外で打ち切る (改行なし巨大入力の OOM 防止)。
    /// </summary>
    private static async Task<string?> ReadLineBoundedAsync(StreamReader reader, CancellationToken ct)
    {
        var sb = new StringBuilder();
        var buf = new char[1];
        while (true)
        {
            int n = await reader.ReadAsync(buf.AsMemory(0, 1), ct).ConfigureAwait(false);
            if (n == 0) return sb.Length == 0 ? null : sb.ToString();
            char c = buf[0];
            if (c == '\n') return sb.ToString();
            if (c == '\r') continue; // CRLF / 裸 CR を吸収
            sb.Append(c);
            if (sb.Length > MaxLineLength)
                throw new InvalidDataException($"Request line exceeds {MaxLineLength} bytes");
        }
    }

    /// <summary>
    /// 1 行を処理する async 入口。ブロッキング待機が必要なメソッド (amm/approval,
    /// amm.waitState, mdi/wait_state tool) だけ async 経路に分岐し、それ以外は
    /// 従来の同期 HandleLine。文字列の事前判定は粗くてよい (誤ヒットしても各
    /// TryHandle* が method/name を正しく検査して同期経路へ差し戻す)。
    /// </summary>
    internal async Task<string?> HandleLineAsync(string line, CancellationToken sessionCt)
    {
        if (line.Contains("\"amm/approval\"", StringComparison.Ordinal))
        {
            var (handled, response) = await TryHandleApprovalAsync(line, sessionCt).ConfigureAwait(false);
            if (handled) return response;
        }
        // amm.waitState: 直接 RPC (PS モジュール等から)
        if (line.Contains("\"amm.waitState\"", StringComparison.Ordinal))
        {
            var (handled, response) = await TryHandleWaitStateAsync(line, sessionCt).ConfigureAwait(false);
            if (handled) return response;
        }
        // mdi/wait_state: tools/call 経由の MCP ツール呼び出し (Claude bridge 経由)
        if (line.Contains("\"mdi/wait_state\"", StringComparison.Ordinal))
        {
            var (handled, response) = await TryHandleMdiWaitStateToolAsync(line, sessionCt).ConfigureAwait(false);
            if (handled) return response;
        }
        // gateway: tools/call が "name":"<server>/<tool>" 形式でゲートウェイツールを指す場合
        if (_gateway != null && line.Contains("\"tools/call\"", StringComparison.Ordinal))
        {
            var (handled, response) = await TryHandleGatewayToolAsync(line, sessionCt).ConfigureAwait(false);
            if (handled) return response;
        }
        return HandleLine(line);
    }

    /// <summary>
    /// amm/approval: CLI の PermissionRequest hook からの許可要求。
    /// params: { token, toolName, toolInput? }。人間の回答 (または解放) まで
    /// ブロックし、result: { decision: "allow" | "deny" | null } を返す。
    /// broker 未配線 (テスト等) は即 null 決定 = ペイン内プロンプトへ。
    /// </summary>
    private async Task<(bool handled, string? response)> TryHandleApprovalAsync(
        string line, CancellationToken sessionCt)
    {
        JsonNode? node;
        try { node = JsonNode.Parse(line); }
        catch (JsonException) { return (false, null); } // 同期経路でエラー応答させる
        if (node is not JsonObject req) return (false, null);
        if (req["method"]?.GetValue<string>() != "amm/approval") return (false, null);

        var id = req["id"];
        var po = req["params"] as JsonObject;
        var token = po?["token"]?.GetValue<string>();
        var toolName = po?["toolName"]?.GetValue<string>();
        if (string.IsNullOrEmpty(token) || string.IsNullOrEmpty(toolName))
            return (true, id == null ? null : MakeError(id, -32602, "token and toolName are required"));

        string? decision = null;
        if (_approvalBroker != null)
        {
            var toolInputJson = po?["toolInput"]?.ToJsonString() ?? "{}";
            try
            {
                decision = await _approvalBroker
                    .RequestAsync(token, toolName!, toolInputJson, ct: sessionCt)
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                AppLogger.Error("[approval] broker error", ex);
            }
        }

        if (id == null) return (true, null);
        var result = new JsonObject
        {
            ["decision"] = decision == null ? null : JsonValue.Create(decision),
        };
        return (true, MakeResult(id, result));
    }

    /// <summary>
    /// 1 行 (1 JSON-RPC リクエスト) を処理し、レスポンス文字列を返す。
    /// notification (id 無し) は null を返す。
    /// </summary>
    internal string? HandleLine(string line)
    {
        JsonNode? node;
        try { node = JsonNode.Parse(line); }
        catch (JsonException ex)
        {
            return MakeError(null, -32700, $"Parse error: {ex.Message}");
        }
        if (node is not JsonObject req) return MakeError(null, -32600, "Invalid Request");

        var id = req["id"];
        var method = req["method"]?.GetValue<string>();
        var paramsNode = req["params"];

        if (string.IsNullOrEmpty(method)) return MakeError(id, -32600, "Missing method");

        try
        {
            return method switch
            {
                "initialize" => HandleInitialize(id),
                "initialized" or "notifications/initialized" => null, // notification, no response
                "tools/list" => HandleToolsList(id),
                "tools/call" => HandleToolsCall(id, paramsNode),
                // amm 独自拡張: CLI hook からの状態通知 (UDR-amm-20260605T0523-7e1)。
                // MCP 標準外だが、同一パイプ上の JSON-RPC として同居させる
                // (別パイプを立てるとライフサイクル管理が二重になる)。
                "amm/notify" => HandleAmmNotify(id, paramsNode),
                // req-20260622-mdi-window-control: PS モジュール等からの直接 RPC
                "amm.openWindow" => HandleLine_AmmOpenWindow(id, paramsNode),
                "amm.closeWindow" => HandleLine_AmmCloseWindow(id, paramsNode),
                // amm.waitState は async — HandleLineAsync の TryHandleWaitStateAsync で処理済み
                "ping" => MakeResult(id, new JsonObject()),
                _ => id == null ? null : MakeError(id, -32601, $"Method not found: {method}"),
            };
        }
        catch (Exception ex)
        {
            // 詳細 (ex.Message にパス等が載り得る) は app.log のみへ。クライアントには
            // 汎用メッセージを返し情報露出を避ける。
            AppLogger.Error($"[mcp] handler error for {method}", ex);
            return MakeError(id, -32603, "Internal error");
        }
    }

    private string HandleInitialize(JsonNode? id)
    {
        var result = new JsonObject
        {
            ["protocolVersion"] = ProtocolVersion,
            ["capabilities"] = new JsonObject
            {
                ["tools"] = new JsonObject(),
            },
            ["serverInfo"] = new JsonObject
            {
                ["name"] = ServerName,
                ["version"] = ServerVersion,
            },
        };
        return MakeResult(id, result);
    }

    private string HandleToolsList(JsonNode? id)
    {
        var tools = new JsonArray
        {
            BuildToolDef("send_message",
                "Send a message to one or more MDI windows that have a registered nickname. " +
                "If recipient is omitted, broadcasts to all eligible. mode='first' (default) picks the input-waiting one, " +
                "falling back to launch order. mode='all' targets every MDI sharing the nickname.",
                new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject
                    {
                        ["recipient"] = SchemaProp("string", "Nickname of the target MDI. Omit to broadcast."),
                        ["mode"] = SchemaProp("string", "'first' or 'all'. Default 'first'."),
                        ["message"] = SchemaProp("string", "Text to inject. Newlines are sent as-is."),
                    },
                    ["required"] = new JsonArray { "message" },
                }),
            BuildToolDef("list_participants",
                "List all MDI windows that have a registered nickname.",
                new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject(),
                }),
            BuildToolDef("peek_queue",
                "Inspect (without dequeuing) messages waiting for delivery. Optionally filter by recipient nickname.",
                new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject
                    {
                        ["recipient"] = SchemaProp("string", "Nickname to filter on. Omit to see all queues."),
                    },
                }),
            // ---- req-20260622-mdi-window-control ----
            BuildToolDef("mdi/open",
                "Open a new MDI terminal window and return its session_id for subsequent operations. " +
                "Specify either 'command' (ephemeral) or 'profile_name' (use existing profiles.amm entry).",
                new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject
                    {
                        ["command"] = SchemaProp("string", "Executable to launch (e.g. 'claude', 'cmd.exe'). Required when profile_name is omitted."),
                        ["profile_name"] = SchemaProp("string", "Name of an existing profile in profiles.amm. Inherits all its settings. Required when command is omitted."),
                        ["args"] = new JsonObject
                        {
                            ["type"] = "array",
                            ["items"] = new JsonObject { ["type"] = "string" },
                            ["description"] = "Command-line arguments (ignored when profile_name is used).",
                        },
                        ["title"] = SchemaProp("string", "Window title override (defaults to profile name or command)."),
                        ["workingDirectory"] = SchemaProp("string", "Working directory override. When profile_name is used, overrides only for this instance."),
                    },
                }),
            BuildToolDef("mdi/close",
                "Close the MDI window identified by session_id.",
                new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject
                    {
                        ["session_id"] = SchemaProp("string", "session_id returned by mdi/open."),
                        ["force"] = new JsonObject { ["type"] = "boolean", ["description"] = "Skip confirmation dialog." },
                    },
                    ["required"] = new JsonArray { "session_id" },
                }),
            BuildToolDef("mdi/wait_state",
                "Block until the specified MDI window reaches the target state or times out. " +
                "target_state: 'idle' = waiting for user input (AI finished), 'attention' = awaiting permission approval.",
                new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject
                    {
                        ["session_id"] = SchemaProp("string", "session_id returned by mdi/open."),
                        ["target_state"] = SchemaProp("string", "'idle' or 'attention'."),
                        ["timeout_ms"] = new JsonObject { ["type"] = "integer", ["description"] = "Timeout in milliseconds (default 300000 = 5 min)." },
                    },
                    ["required"] = new JsonArray { "session_id", "target_state" },
                }),
        };

        // gateway ツールを末尾に追加 (req-20260622-mcp-gateway)
        if (_gateway != null)
        {
            foreach (var gatewayTool in _gateway.GetAggregatedTools())
                tools.Add(gatewayTool?.DeepClone());
        }

        return MakeResult(id, new JsonObject { ["tools"] = tools });
    }

    private string HandleToolsCall(JsonNode? id, JsonNode? paramsNode)
    {
        if (paramsNode is not JsonObject po)
            return MakeError(id, -32602, "Invalid params");

        var name = po["name"]?.GetValue<string>();
        var args = po["arguments"] as JsonObject ?? new JsonObject();

        return name switch
        {
            "send_message" => CallSendMessage(id, args),
            "list_participants" => CallListParticipants(id),
            "peek_queue" => CallPeekQueue(id, args),
            "mdi/open" => CallMdiOpen(id, args),
            "mdi/close" => CallMdiClose(id, args),
            // mdi/wait_state は async — HandleLineAsync の事前フィルタで処理済みのはず。
            // ここには「wait_state が含まれない tools/call」しか到達しない。
            _ => MakeError(id, -32601, $"Unknown tool: {name}"),
        };
    }

    /// <summary>
    /// CLI hook 由来の状態通知。params: { token, state, source? }。
    /// token = ConPTY 起動時に注入した AMM_NOTIFY_ID。state = idle | attention | busy。
    /// id 付きで来たら { matched } を返す (notification として id なしでも可)。
    /// </summary>
    private string? HandleAmmNotify(JsonNode? id, JsonNode? paramsNode)
    {
        if (paramsNode is not JsonObject po)
            return id == null ? null : MakeError(id, -32602, "Invalid params");

        var token = po["token"]?.GetValue<string>();
        var state = po["state"]?.GetValue<string>();
        if (string.IsNullOrEmpty(token) || string.IsNullOrEmpty(state))
            return id == null ? null : MakeError(id, -32602, "token and state are required");

        var matched = _dispatcher.NotifyChildState(token, state);

        // WaitBroker: amm/notify で状態遷移した session の pending wait を解放
        // (idle = 入力待ち / attention = 許可待ち)
        _waitBroker?.ResolveByToken(token!, state!);

        return id == null ? null : MakeResult(id, new JsonObject { ["matched"] = matched });
    }

    private string CallSendMessage(JsonNode? id, JsonObject args)
    {
        var recipient = args["recipient"]?.GetValue<string>();
        var mode = args["mode"]?.GetValue<string>() ?? "first";
        var message = args["message"]?.GetValue<string>();
        if (string.IsNullOrEmpty(message))
            return MakeError(id, -32602, "message is required");

        var result = _dispatcher.Send(recipient, mode, message);
        var payload = new JsonObject
        {
            ["delivered_count"] = result.DeliveredCount,
            ["queued_count"] = result.QueuedCount,
            ["recipients"] = new JsonArray(result.Recipients.Select(s => (JsonNode)s!).ToArray()),
        };
        return MakeToolResult(id, payload);
    }

    private string CallListParticipants(JsonNode? id)
    {
        var participants = _dispatcher.ListParticipants();
        var arr = new JsonArray();
        foreach (var p in participants)
        {
            arr.Add(new JsonObject
            {
                ["nickname"] = p.Nickname,
                ["profile"] = p.ProfileName,
                ["instance"] = p.Instance,
                ["state"] = p.State,
                ["session_id"] = p.SessionId,
            });
        }
        return MakeToolResult(id, new JsonObject { ["participants"] = arr });
    }

    private string CallPeekQueue(JsonNode? id, JsonObject args)
    {
        var recipient = args["recipient"]?.GetValue<string>();
        var snap = _dispatcher.PeekQueue(recipient);
        var queues = new JsonArray();
        foreach (var (nick, messages) in snap)
        {
            queues.Add(new JsonObject
            {
                ["nickname"] = nick,
                ["messages"] = new JsonArray(messages.Select(s => (JsonNode)s!).ToArray()),
            });
        }
        return MakeToolResult(id, new JsonObject { ["queues"] = queues });
    }

    // ---- mdi/open / mdi/close ----

    private string CallMdiOpen(JsonNode? id, JsonObject args)
    {
        var command = args["command"]?.GetValue<string>();
        var profileName = args["profile_name"]?.GetValue<string>();
        if (string.IsNullOrEmpty(command) && string.IsNullOrEmpty(profileName))
            return MakeError(id, -32602, "command or profile_name is required");

        var argsArr = (args["args"] as JsonArray)
            ?.Select(n => n?.GetValue<string>() ?? "")
            .ToArray() ?? [];
        var title = args["title"]?.GetValue<string>();
        var workingDir = args["workingDirectory"]?.GetValue<string>();

        var result = _dispatcher.OpenWindow(new OpenWindowParams
        {
            Command = command,
            ProfileName = profileName,
            Args = argsArr,
            Title = title,
            WorkingDirectory = workingDir,
        });

        if (result.Error != null)
            return MakeToolResult(id, new JsonObject { ["error"] = result.Error });

        return MakeToolResult(id, new JsonObject { ["session_id"] = result.SessionId });
    }

    private string CallMdiClose(JsonNode? id, JsonObject args)
    {
        var sessionId = args["session_id"]?.GetValue<string>();
        if (string.IsNullOrEmpty(sessionId))
            return MakeError(id, -32602, "session_id is required");

        var force = args["force"]?.GetValue<bool>() ?? false;
        var success = _dispatcher.CloseWindow(sessionId, force);

        if (!success)
            return MakeToolResult(id, new JsonObject { ["error"] = $"session not found: {sessionId}" });

        return MakeToolResult(id, new JsonObject { ["success"] = true });
    }

    // ---- amm.openWindow / amm.closeWindow / amm.waitState 直接 RPC ----
    // (PS モジュール、amm-mcp CLI 等から MCP プロトコルなしで呼ぶ場合)

    private string HandleLine_AmmOpenWindow(JsonNode? id, JsonNode? paramsNode)
    {
        if (paramsNode is not JsonObject po)
            return MakeError(id, -32602, "Invalid params");
        var command = po["command"]?.GetValue<string>();
        var profileName = po["profile_name"]?.GetValue<string>();
        if (string.IsNullOrEmpty(command) && string.IsNullOrEmpty(profileName))
            return MakeError(id, -32602, "command or profile_name is required");

        var argsArr = (po["args"] as JsonArray)
            ?.Select(n => n?.GetValue<string>() ?? "").ToArray() ?? [];
        var result = _dispatcher.OpenWindow(new OpenWindowParams
        {
            Command = command,
            ProfileName = profileName,
            Args = argsArr,
            Title = po["title"]?.GetValue<string>(),
            WorkingDirectory = po["working_directory"]?.GetValue<string>(),
        });

        if (result.Error != null)
            return id == null ? MakeError(null, -32603, result.Error)
                              : MakeError(id, -32603, result.Error);
        return MakeResult(id, new JsonObject { ["session_id"] = result.SessionId });
    }

    private string HandleLine_AmmCloseWindow(JsonNode? id, JsonNode? paramsNode)
    {
        if (paramsNode is not JsonObject po)
            return MakeError(id, -32602, "Invalid params");
        var sessionId = po["session_id"]?.GetValue<string>();
        if (string.IsNullOrEmpty(sessionId))
            return MakeError(id, -32602, "session_id is required");

        var force = po["force"]?.GetValue<bool>() ?? false;
        var ok = _dispatcher.CloseWindow(sessionId, force);
        if (!ok) return MakeError(id, -32602, $"session not found: {sessionId}");
        return MakeResult(id, new JsonObject { ["success"] = true });
    }

    // ---- mdi/wait_state async path ----

    /// <summary>
    /// tools/call { name: "mdi/wait_state" } の async ハンドラ。
    /// HandleLineAsync → TryHandleMdiWaitStateToolAsync → WaitBroker.RegisterWait → await
    /// の流れで、目標状態到達 or timeout まで応答を保留する。
    /// </summary>
    private async Task<(bool handled, string? response)> TryHandleMdiWaitStateToolAsync(
        string line, CancellationToken sessionCt)
    {
        JsonNode? node;
        try { node = JsonNode.Parse(line); }
        catch (JsonException) { return (false, null); }
        if (node is not JsonObject req) return (false, null);

        if (req["method"]?.GetValue<string>() != "tools/call") return (false, null);
        var po = req["params"] as JsonObject;
        if (po?["name"]?.GetValue<string>() != "mdi/wait_state") return (false, null);

        var id = req["id"];
        var args = po?["arguments"] as JsonObject ?? new JsonObject();

        var sessionId = args["session_id"]?.GetValue<string>();
        var targetState = args["target_state"]?.GetValue<string>();
        var timeoutMs = args["timeout_ms"]?.GetValue<int>() ?? WaitBroker.DefaultTimeoutMs;

        if (string.IsNullOrEmpty(sessionId))
            return (true, MakeError(id, -32602, "session_id is required"));
        if (string.IsNullOrEmpty(targetState))
            return (true, MakeError(id, -32602, "target_state is required"));

        var (result, err) = await ExecuteWaitStateAsync(sessionId!, targetState!, timeoutMs, sessionCt)
            .ConfigureAwait(false);
        if (err != null) return (true, MakeError(id, -32602, err));
        return (true, MakeToolResult(id, new JsonObject
        {
            ["state"] = result!.State,
            ["elapsed_ms"] = result.ElapsedMs,
        }));
    }

    /// <summary>
    /// amm.waitState 直接 RPC の async ハンドラ (PS モジュール等)。
    /// </summary>
    private async Task<(bool handled, string? response)> TryHandleWaitStateAsync(
        string line, CancellationToken sessionCt)
    {
        JsonNode? node;
        try { node = JsonNode.Parse(line); }
        catch (JsonException) { return (false, null); }
        if (node is not JsonObject req) return (false, null);
        if (req["method"]?.GetValue<string>() != "amm.waitState") return (false, null);

        var id = req["id"];
        var po = req["params"] as JsonObject;

        var sessionId = po?["session_id"]?.GetValue<string>();
        var targetState = po?["target_state"]?.GetValue<string>();
        var timeoutMs = po?["timeout_ms"]?.GetValue<int>() ?? WaitBroker.DefaultTimeoutMs;

        if (string.IsNullOrEmpty(sessionId))
            return (true, MakeError(id, -32602, "session_id is required"));
        if (string.IsNullOrEmpty(targetState))
            return (true, MakeError(id, -32602, "target_state is required"));

        var (result, err) = await ExecuteWaitStateAsync(sessionId!, targetState!, timeoutMs, sessionCt)
            .ConfigureAwait(false);
        if (err != null) return (true, MakeError(id, -32602, err));
        return (true, MakeResult(id, new JsonObject
        {
            ["state"] = result!.State,
            ["elapsed_ms"] = result.ElapsedMs,
        }));
    }

    /// <summary>
    /// WaitBroker.RegisterWait を呼び目標状態まで待つ共通実装。
    /// エラー文字列またはWaitResult を返す (null, null にはならない)。
    /// </summary>
    private async Task<(WaitBroker.WaitResult? result, string? error)> ExecuteWaitStateAsync(
        string sessionId, string targetState, int timeoutMs, CancellationToken ct)
    {
        if (_waitBroker == null)
            return (null, "WaitBroker not configured");

        var notifyToken = _dispatcher.GetNotifyTokenBySessionId(sessionId);
        if (notifyToken == null)
            return (null, $"session not found: {sessionId}");

        var result = await _waitBroker
            .RegisterWait(sessionId, notifyToken, targetState, timeoutMs, ct)
            .ConfigureAwait(false);
        return (result, null);
    }

    // ---- gateway tool dispatch ----

    /// <summary>
    /// tools/call のうちゲートウェイツール ("{server}/{tool}") を非同期で外部プロセスへ転送する。
    /// ゲートウェイツールでなければ (false, null) を返し同期経路へフォールバックする。
    /// </summary>
    private async Task<(bool handled, string? response)> TryHandleGatewayToolAsync(
        string line, CancellationToken sessionCt)
    {
        if (_gateway == null) return (false, null);

        JsonNode? node;
        try { node = JsonNode.Parse(line); }
        catch (JsonException) { return (false, null); }
        if (node is not JsonObject req) return (false, null);
        if (req["method"]?.GetValue<string>() != "tools/call") return (false, null);

        var po = req["params"] as JsonObject;
        var toolName = po?["name"]?.GetValue<string>();
        if (string.IsNullOrEmpty(toolName) || !_gateway.IsGatewayTool(toolName))
            return (false, null);

        var id = req["id"];
        var args = po?["arguments"] as JsonObject ?? new JsonObject();

        try
        {
            var result = await _gateway.CallToolAsync(toolName!, args, sessionCt).ConfigureAwait(false);
            if (result == null)
                return (true, MakeError(id, -32603, $"Gateway server for '{toolName}' is not running"));

            // If result has "error" key (from server error response), surface it
            if (result is JsonObject ro && ro["error"] != null)
                return (true, MakeError(id, -32603, ro["error"]?.ToJsonString() ?? "gateway error"));

            // result should be a MCP tools/call result object — pass through as-is or wrap
            if (result is JsonObject resultObj)
                return (true, MakeResult(id, resultObj));

            return (true, MakeResult(id, new JsonObject { ["content"] = result.DeepClone() }));
        }
        catch (Exception ex)
        {
            AppLogger.Error($"[gateway] tool call failed: {toolName}", ex);
            return (true, MakeError(id, -32603, $"Gateway error: {ex.Message}"));
        }
    }

    // ---- helpers ----

    private static JsonObject BuildToolDef(string name, string description, JsonObject inputSchema) =>
        new()
        {
            ["name"] = name,
            ["description"] = description,
            ["inputSchema"] = inputSchema,
        };

    private static JsonObject SchemaProp(string type, string description) =>
        new() { ["type"] = type, ["description"] = description };

    private static string MakeToolResult(JsonNode? id, JsonObject structuredContent)
    {
        // MCP の tools/call レスポンスは content 配列を返す形が標準。
        // 後方互換のため text 表現も含める (JSON 文字列化)。
        var json = structuredContent.ToJsonString();
        var result = new JsonObject
        {
            ["content"] = new JsonArray
            {
                new JsonObject { ["type"] = "text", ["text"] = json },
            },
            ["isError"] = false,
            ["structuredContent"] = structuredContent,
        };
        return MakeResult(id, result);
    }

    private static string MakeResult(JsonNode? id, JsonNode result)
    {
        var obj = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id?.DeepClone() ?? JsonValue.Create((object?)null),
            ["result"] = result,
        };
        return obj.ToJsonString();
    }

    private static string MakeError(JsonNode? id, int code, string message)
    {
        var obj = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id?.DeepClone() ?? JsonValue.Create((object?)null),
            ["error"] = new JsonObject
            {
                ["code"] = code,
                ["message"] = message,
            },
        };
        return obj.ToJsonString();
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            _cts.Cancel();
            if (_acceptLoop != null)
            {
                try { await _acceptLoop.ConfigureAwait(false); }
                catch (OperationCanceledException) { }
            }
        }
        finally
        {
            _cts.Dispose();
        }
    }
}
