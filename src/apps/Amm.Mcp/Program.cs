using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Amm.Mcp;

/// <summary>
/// amm MCP / CLI bridge (UDR-amm-20260427T0225-7a3, UDR-amm-20260427T0238-fb5)。
///
/// 引数の最初の語でモードを分岐:
///   amm-mcp.exe                       → stdio MCP bridge (既定)
///   amm-mcp.exe --bridge              → 同上 (明示)
///   amm-mcp.exe send &lt;nick&gt; [msg...]  → CLI 送信。msg 省略時は stdin
///   amm-mcp.exe send --broadcast [msg]→ broadcast 送信
///   amm-mcp.exe list                  → list_participants の JSON を stdout に
///
/// 共通オプション (両モードで使える):
///   --pipe-name &lt;name&gt;       既定 amm-mcp-{user}
///   --connect-timeout &lt;ms&gt;   既定 5000ms
///
/// 終了コード: 0=成功 / 1=引数不正 / 2=GUI 未起動 / 3=IO エラー / 4=MCP エラー
/// </summary>
internal static class Program
{
    private const int ExitOk = 0;
    private const int ExitBadArgs = 1;
    private const int ExitNoServer = 2;
    private const int ExitIOError = 3;
    private const int ExitMcpError = 4;

    private static async Task<int> Main(string[] args)
    {
        // Windows コンソールの既定 cp (cp932 等) ではソース埋め込みの日本語が
        // 文字化けする。BOM なし UTF-8 に固定。bridge mode (stdin/stdout が
        // pipe redirect) でも UTF-8 で問題ない (MCP も UTF-8 前提)。
        Console.OutputEncoding = new UTF8Encoding(false);
        Console.InputEncoding = new UTF8Encoding(false);

        var (mode, rest) = ResolveMode(args);
        var pipeName = ResolvePipeName(rest);
        var connectTimeoutMs = ResolveTimeout(rest);

        try
        {
            return mode switch
            {
                "bridge" => await RunBridge(pipeName, connectTimeoutMs).ConfigureAwait(false),
                "repl" => await RunRepl(pipeName, connectTimeoutMs).ConfigureAwait(false),
                "send" => await RunSend(rest, pipeName, connectTimeoutMs).ConfigureAwait(false),
                "list" => await RunList(pipeName, connectTimeoutMs).ConfigureAwait(false),
                "notify" => await RunNotify(rest, pipeName).ConfigureAwait(false),
                "approve" => await RunApprove(rest, pipeName).ConfigureAwait(false),
                _ => PrintUsageAndExit(),
            };
        }
        catch (TimeoutException)
        {
            Console.Error.WriteLine(
                $"amm-mcp: amm GUI に接続できませんでした (pipe={pipeName})。" +
                "GUI を起動してから再試行してください。");
            return ExitNoServer;
        }
        catch (IOException ex)
        {
            Console.Error.WriteLine($"amm-mcp: pipe IO error: {ex.Message}");
            return ExitIOError;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"amm-mcp: unexpected error: {ex.Message}");
            return ExitIOError;
        }
    }

    // ---- bridge mode (Phase 3) ----

    private static async Task<int> RunBridge(string pipeName, int timeout)
    {
        using var pipe = await ConnectAsync(pipeName, timeout).ConfigureAwait(false);
        if (pipe == null) return ExitNoServer;

        using var stdin = Console.OpenStandardInput();
        using var stdout = Console.OpenStandardOutput();

        // 双方向コピー。t1=stdin→pipe (クライアント→サーバ)、t2=pipe→stdout (サーバ→
        // クライアント)。どちらか一方の完了でセッション終了。ただし t1 が先に EOF した
        // (= クライアントが stdin を閉じた) 場合、サーバの最終応答が t2 にまだ流れて
        // いる可能性があるため、短時間だけ t2 のドレインを待ってから終了する。pipe は
        // サーバ側が開いたままなので t2 を無制限に待つと return できずハングする →
        // Task.Delay で上限を設ける。return 時に using が pipe/stdout を閉じ t2 は解放。
        var t1 = stdin.CopyToAsync(pipe);
        var t2 = pipe.CopyToAsync(stdout);

        var first = await Task.WhenAny(t1, t2).ConfigureAwait(false);
        if (first == t1)
            await Task.WhenAny(t2, Task.Delay(250)).ConfigureAwait(false); // 最終応答の取りこぼし緩和
        try { await first.ConfigureAwait(false); }
        catch (Exception ex) { Console.Error.WriteLine($"amm-mcp: bridge copy ended: {ex.Message}"); }
        return ExitOk;
    }

    // ---- REPL mode (引数なし対話端末で起動した場合) ----

    /// <summary>
    /// 対話 REPL: list / send / peek / help / quit を受け付ける。
    /// プロンプト `> ` を出して入力を待ち、空行は無視する。Ctrl+C / quit / exit / EOF で終了。
    /// </summary>
    private static async Task<int> RunRepl(string pipeName, int timeout)
    {
        Console.WriteLine("amm-mcp interactive (type 'help' or '?' for commands, 'quit' to exit)");
        Console.WriteLine($"  pipe: {pipeName}");
        Console.WriteLine();

        while (true)
        {
            Console.Write("> ");
            var line = Console.ReadLine();
            if (line == null) { Console.WriteLine(); return ExitOk; } // EOF (Ctrl+Z)
            line = line.Trim();
            if (line.Length == 0) continue;

            if (line is "quit" or "exit" or "q") return ExitOk;
            if (line is "help" or "?" or "h")
            {
                PrintReplHelp();
                continue;
            }

            try
            {
                await DispatchReplCommand(line, pipeName, timeout).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"error: {ex.Message}");
            }
        }
    }

    private static async Task DispatchReplCommand(string line, string pipeName, int timeout)
    {
        var tokens = SplitReplLine(line);
        if (tokens.Count == 0) return;

        switch (tokens[0].ToLowerInvariant())
        {
            case "list":
                await RunList(pipeName, timeout).ConfigureAwait(false);
                break;

            case "send":
                {
                    // send <nick> <msg...> | send --broadcast <msg...>
                    var fakeArgs = new List<string> { "send" };
                    fakeArgs.AddRange(tokens.Skip(1));
                    await RunSend(fakeArgs.ToArray(), pipeName, timeout).ConfigureAwait(false);
                }
                break;

            case "peek":
                {
                    using var pipe = await ConnectAsync(pipeName, timeout).ConfigureAwait(false);
                    if (pipe == null) return;
                    var args = new JsonObject();
                    if (tokens.Count >= 2) args["recipient"] = tokens[1];
                    var resp = await CallToolAsync(pipe, "peek_queue", args).ConfigureAwait(false);
                    if (resp == null) { Console.Error.WriteLine("error: no response"); return; }
                    if (resp["error"] is JsonNode err)
                    {
                        Console.Error.WriteLine($"server error: {err["message"]?.GetValue<string>()}");
                        return;
                    }
                    var queues = resp["result"]?["structuredContent"]?["queues"];
                    Console.WriteLine(queues?.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) ?? "[]");
                }
                break;

            case "open":
                {
                    using var pipe = await ConnectAsync(pipeName, timeout).ConfigureAwait(false);
                    if (pipe == null) return;
                    var args = new JsonObject();
                    if (tokens.Count >= 2)
                        args["profile_name"] = string.Join(" ", tokens.Skip(1));
                    var resp = await CallToolAsync(pipe, "mdi/open", args).ConfigureAwait(false);
                    if (resp == null) { Console.Error.WriteLine("error: no response"); return; }
                    if (resp["error"] is JsonNode openErr)
                    {
                        Console.Error.WriteLine($"server error: {openErr["message"]?.GetValue<string>()}");
                        return;
                    }
                    var sessionId = resp["result"]?["structuredContent"]?["session_id"]?.GetValue<string>();
                    Console.WriteLine($"session_id: {sessionId ?? "(null)"}");
                }
                break;

            case "close":
                {
                    if (tokens.Count < 2) { Console.Error.WriteLine("usage: close <session-id> [--force]"); return; }
                    using var pipe = await ConnectAsync(pipeName, timeout).ConfigureAwait(false);
                    if (pipe == null) return;
                    var args = new JsonObject();
                    args["session_id"] = tokens[1];
                    if (tokens.Contains("--force")) args["force"] = true;
                    var resp = await CallToolAsync(pipe, "mdi/close", args).ConfigureAwait(false);
                    if (resp == null) { Console.Error.WriteLine("error: no response"); return; }
                    if (resp["error"] is JsonNode closeErr)
                    {
                        Console.Error.WriteLine($"server error: {closeErr["message"]?.GetValue<string>()}");
                        return;
                    }
                    var success = resp["result"]?["success"]?.GetValue<bool>() ?? false;
                    Console.WriteLine($"success: {success}");
                }
                break;

            case "wait":
                {
                    if (tokens.Count < 2) { Console.Error.WriteLine("usage: wait <session-id|nickname> [idle|attention] [--timeout-ms N]"); return; }
                    string targetState = "idle";
                    int waitTimeoutMs = 300_000;
                    for (int i = 2; i < tokens.Count; i++)
                    {
                        if (tokens[i] == "--timeout-ms" && i + 1 < tokens.Count)
                        {
                            if (int.TryParse(tokens[i + 1], out var ms)) { waitTimeoutMs = ms; }
                            i++;
                        }
                        else if (tokens[i] is "idle" or "attention")
                        {
                            targetState = tokens[i];
                        }
                    }

                    // UUID なら session_id 直接指定、それ以外は nickname として list_participants で解決
                    string? resolvedSessionId;
                    if (Guid.TryParse(tokens[1], out _))
                    {
                        resolvedSessionId = tokens[1];
                    }
                    else
                    {
                        using var listPipe = await ConnectAsync(pipeName, timeout).ConfigureAwait(false);
                        if (listPipe == null) return;
                        var listResp = await CallToolAsync(listPipe, "list_participants", new JsonObject()).ConfigureAwait(false);
                        var participants = listResp?["result"]?["structuredContent"]?["participants"] as JsonArray;
                        var match = participants?
                            .OfType<JsonObject>()
                            .FirstOrDefault(p => string.Equals(
                                p["nickname"]?.GetValue<string>(), tokens[1],
                                StringComparison.OrdinalIgnoreCase));
                        if (match == null) { Console.Error.WriteLine($"error: nickname '{tokens[1]}' が見つかりません"); return; }
                        resolvedSessionId = match["session_id"]?.GetValue<string>();
                        if (string.IsNullOrEmpty(resolvedSessionId)) { Console.Error.WriteLine($"error: '{tokens[1]}' に session_id がありません"); return; }
                        Console.Error.WriteLine($"  nickname '{tokens[1]}' → session_id: {resolvedSessionId}");
                    }

                    using var pipe = await ConnectAsync(pipeName, timeout).ConfigureAwait(false);
                    if (pipe == null) return;
                    var args = new JsonObject
                    {
                        ["session_id"] = resolvedSessionId,
                        ["target_state"] = targetState,
                        ["timeout_ms"] = waitTimeoutMs,
                    };
                    var resp = await CallToolAsync(pipe, "mdi/wait_state", args).ConfigureAwait(false);
                    if (resp == null) { Console.Error.WriteLine("error: no response"); return; }
                    if (resp["error"] is JsonNode waitErr)
                    {
                        Console.Error.WriteLine($"server error: {waitErr["message"]?.GetValue<string>()}");
                        return;
                    }
                    var sc = resp["result"]?["structuredContent"];
                    var state = sc?["state"]?.GetValue<string>();
                    var elapsed = sc?["elapsed_ms"]?.GetValue<long>();
                    Console.WriteLine($"state: {state ?? "?"}, elapsed_ms: {elapsed?.ToString() ?? "?"}");
                }
                break;

            default:
                Console.Error.WriteLine($"unknown command: {tokens[0]} (type 'help')");
                break;
        }
    }

    private static List<string> SplitReplLine(string line)
    {
        // 簡易パーサ: ダブルクォート内の空白は引数区切りとみなさない。
        // 例: send claude "hello world" → ["send", "claude", "hello world"]
        var result = new List<string>();
        var cur = new StringBuilder();
        bool inQuotes = false;
        foreach (var ch in line)
        {
            if (ch == '"') { inQuotes = !inQuotes; continue; }
            if (char.IsWhiteSpace(ch) && !inQuotes)
            {
                if (cur.Length > 0) { result.Add(cur.ToString()); cur.Clear(); }
                continue;
            }
            cur.Append(ch);
        }
        if (cur.Length > 0) result.Add(cur.ToString());
        return result;
    }

    private static void PrintReplHelp()
    {
        Console.WriteLine("""
  list                              - participants 一覧 (JSON)
  send <nick> <message...>          - 指定 nickname に送信 (入力待ち優先)
  send <nick> --all <message...>    - 同 nickname の全インスタンスに送信
  send --broadcast <message...>     - 登録済み全 nickname に送信
  peek [<nick>]                     - 配信待ちキューを覗き見 (nickname 指定可)
  open [<profile-name>]             - MDI を新規起動 → session_id を返す
  close <session-id> [--force]      - MDI を閉じる
  wait <session-id|nickname> [idle|attention] [--timeout-ms N]
                                    - 指定セッションが状態になるまで待機
                                      nickname 指定時は list で session_id を自動解決
  help / ?                          - このヘルプ
  quit / exit / Ctrl+Z+Enter        - 終了

  メッセージ / プロファイル名にスペースを含めたいときは "..." で囲む
  例: send claude "hello world from REPL"
       open "Claude Code"
       wait d4f7a2b1-... idle --timeout-ms 60000
       wait claude idle
""");
    }

    // ---- send subcommand (Phase 4) ----

    private static async Task<int> RunSend(string[] rest, string pipeName, int timeout)
    {
        // rest は --pipe-name 等の共通オプションも含むので分離が必要
        var sendArgs = rest.Where((a, i) => !IsCommonOption(rest, i)).Skip(1).ToArray();

        bool broadcast = false;
        string mode = "first";
        var positional = new List<string>();
        for (int i = 0; i < sendArgs.Length; i++)
        {
            var a = sendArgs[i];
            if (a == "--broadcast") { broadcast = true; continue; }
            // --all: 同じ nickname を持つ MDI 全てに配信 (mode=all のショートカット)。
            // --broadcast (= 全 nickname へ) との区別が曖昧になりやすいため、
            // ここでは「指定 nickname 全インスタンス」と「全 nickname」を別フラグに分けている。
            if (a == "--all") { mode = "all"; continue; }
            if (a == "--mode" && i + 1 < sendArgs.Length) { mode = sendArgs[++i]; continue; }
            positional.Add(a);
        }

        string? nickname = null;
        string message;
        if (broadcast)
        {
            // positional は全てメッセージとして扱う
            message = positional.Count > 0
                ? string.Join(' ', positional)
                : ReadAllStdin();
        }
        else
        {
            if (positional.Count == 0)
            {
                Console.Error.WriteLine("amm-mcp send: <nickname> が必要です (--broadcast を指定する場合は除く)");
                return ExitBadArgs;
            }
            nickname = positional[0];
            message = positional.Count > 1
                ? string.Join(' ', positional.Skip(1))
                : ReadAllStdin();
        }

        if (string.IsNullOrEmpty(message))
        {
            Console.Error.WriteLine("amm-mcp send: メッセージが空です (引数または stdin から指定してください)");
            return ExitBadArgs;
        }

        using var pipe = await ConnectAsync(pipeName, timeout).ConfigureAwait(false);
        if (pipe == null) return ExitNoServer;

        var args = new JsonObject { ["message"] = message, ["mode"] = mode };
        if (!broadcast) args["recipient"] = nickname;

        var resp = await CallToolAsync(pipe, "send_message", args).ConfigureAwait(false);
        if (resp == null) return ExitMcpError;

        if (resp["error"] is JsonNode err)
        {
            Console.Error.WriteLine($"amm-mcp: server error: {err["message"]?.GetValue<string>()}");
            return ExitMcpError;
        }

        var result = resp["result"]?["structuredContent"];
        var delivered = result?["delivered_count"]?.GetValue<int>() ?? 0;
        var queued = result?["queued_count"]?.GetValue<int>() ?? 0;
        var recipients = (result?["recipients"] as JsonArray)?.Select(n => n!.GetValue<string>()) ?? [];
        Console.Error.WriteLine($"delivered={delivered} queued={queued} recipients=[{string.Join(",", recipients)}]");
        return ExitOk;
    }

    // ---- notify subcommand (hook 駆動の入力待ち通知, UDR-amm-20260605T0523-7e1) ----

    /// <summary>
    /// CLI hook (Claude Code Stop/Notification、Codex notify、Copilot
    /// notification-hooks) から起動され、amm GUI へ状態を push する。
    ///
    ///   amm-mcp.exe notify [--state idle|attention|busy] [--source <label>]
    ///
    /// - MDI の特定は環境変数 AMM_NOTIFY_ID (amm が ConPTY 起動時に注入、hook は
    ///   CLI の子プロセスなので継承する)。**env 不在 = amm 配下でない CLI** からの
    ///   発火なので黙って成功終了する (ユーザーグローバルな hook 設定との両立)。
    /// - ペイロードは stdin JSON (Claude/Copilot) または argv 末尾 JSON (Codex) を
    ///   受け、NotifyPayloadMapper で正規化。--state 指定があれば最優先。
    /// - hook の失敗が CLI 本体の動作を妨げてはならないため、接続失敗を含む
    ///   あらゆる失敗で exit 0 / 出力なし。接続タイムアウトは既定 2000ms と短め
    ///   (GUI 不在時に hook を長く待たせない)。
    /// </summary>
    private static async Task<int> RunNotify(string[] rest, string pipeName)
    {
        try
        {
            var token = Environment.GetEnvironmentVariable("AMM_NOTIFY_ID");
            if (string.IsNullOrEmpty(token)) return ExitOk; // amm 配下でない → no-op

            // notify 専用オプション + positional (Codex の argv JSON) を解釈
            string? stateArg = null;
            string? source = null;
            var positional = new List<string>();
            var args = rest.Where((a, i) => !IsCommonOption(rest, i)).Skip(1).ToArray();
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "--state" && i + 1 < args.Length) { stateArg = args[++i]; continue; }
                if (args[i] == "--source" && i + 1 < args.Length) { source = args[++i]; continue; }
                positional.Add(args[i]);
            }

            string? state = stateArg;
            if (state == null)
            {
                var payload = TryParseNotifyPayload(positional);
                state = NotifyPayloadMapper.MapState(payload);
            }
            if (state == null) return ExitOk; // 対象外イベント

            var timeout = HasExplicitTimeout(rest) ? ResolveTimeout(rest) : 2000;
            using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            await pipe.ConnectAsync(timeout).ConfigureAwait(false);

            var writer = new StreamWriter(pipe, new UTF8Encoding(false), 4096, leaveOpen: true)
            { AutoFlush = true, NewLine = "\n" };
            var reader = new StreamReader(pipe, new UTF8Encoding(false), false, 4096, leaveOpen: true);

            var req = new JsonObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = 1,
                ["method"] = "amm/notify",
                ["params"] = new JsonObject
                {
                    ["token"] = token,
                    ["state"] = state,
                    ["source"] = source,
                },
            };
            await writer.WriteLineAsync(req.ToJsonString()).ConfigureAwait(false);
            await reader.ReadLineAsync().ConfigureAwait(false); // 応答は読み捨て (配達確認のみ)
            return ExitOk;
        }
        catch
        {
            // GUI 未起動 / pipe 切断 / JSON 不正 — いずれも hook の世界では
            // 「通知できなかった」だけ。CLI を妨げない (stderr も汚さない)。
            return ExitOk;
        }
    }

    /// <summary>
    /// notify ペイロードの取得: stdin が redirect されていれば全文を JSON として
    /// 解析 (Claude Code / Copilot)。なければ positional 引数を末尾から見て最初に
    /// JSON object として解析できたものを使う (Codex は argv 末尾に JSON を渡す)。
    /// </summary>
    private static JsonObject? TryParseNotifyPayload(List<string> positional)
    {
        if (Console.IsInputRedirected)
        {
            try
            {
                var text = Console.In.ReadToEnd();
                if (!string.IsNullOrWhiteSpace(text))
                    return JsonNode.Parse(text) as JsonObject;
            }
            catch { /* 解析不能 → fall through */ }
        }
        for (int i = positional.Count - 1; i >= 0; i--)
        {
            try
            {
                if (JsonNode.Parse(positional[i]) is JsonObject obj) return obj;
            }
            catch { /* JSON でない positional は無視 */ }
        }
        return null;
    }

    private static bool HasExplicitTimeout(string[] args) =>
        args.Any(a => string.Equals(a, "--connect-timeout", StringComparison.OrdinalIgnoreCase));

    // ---- approve subcommand (ToolUse 許可集約, Approval Hub Level 2) ----

    /// <summary>
    /// CLI の許可要求 hook (Claude Code: PermissionRequest / Copilot CLI:
    /// permissionRequest) から起動され、許可要求を amm のポップアップへ転送して
    /// 人間の回答を待つ。
    ///
    ///   amm-mcp.exe approve [--source claude|copilot]   (stdin: hook payload JSON)
    ///
    /// - 環境変数 AMM_NOTIFY_ID 不在 (amm 配下でない CLI) は即 no-op。
    /// - 回答 "allow"/"deny" を受けたら CLI ごとの決定 JSON を stdout に出力する
    ///   (--source copilot は {"behavior": ...} 直書き、それ以外は Claude Code の
    ///   hookSpecificOutput 形式)。**決定なし (タイムアウト / ペインアクティブ化 /
    ///   トグル OFF / GUI 不在 / あらゆる失敗) は何も出力せず exit 0** — どちらの
    ///   CLI も決定なしを「通常のペイン内プロンプト表示」として扱うため、これが
    ///   安全側フォールバックになる。
    /// - 待ち時間の鎖: amm 側台帳 45 秒 < 本プロセスの読み取り上限 55 秒 <
    ///   hook 登録 timeout 60 秒。先に amm が必ず解放するので hook の強制 kill
    ///   には到達しない。
    /// </summary>
    private static async Task<int> RunApprove(string[] rest, string pipeName)
    {
        try
        {
            var token = Environment.GetEnvironmentVariable("AMM_NOTIFY_ID");
            if (string.IsNullOrEmpty(token)) return ExitOk; // amm 配下でない → no-op

            string? source = null;
            var args = rest.Where((a, i) => !IsCommonOption(rest, i)).Skip(1).ToArray();
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "--source" && i + 1 < args.Length) { source = args[++i]; }
            }

            JsonObject? payload = null;
            if (Console.IsInputRedirected)
            {
                try { payload = JsonNode.Parse(Console.In.ReadToEnd()) as JsonObject; }
                catch { /* 解析不能 → ツール情報なしで続行 */ }
            }
            // Claude Code は snake_case (tool_name / tool_input)、Copilot CLI の
            // camelCase 形式は toolName / toolArgs。両方を受ける。
            var toolName = payload?["tool_name"]?.GetValue<string>()
                ?? payload?["toolName"]?.GetValue<string>()
                ?? "(unknown)";
            var toolInput = (payload?["tool_input"] ?? payload?["toolArgs"])?.DeepClone();

            using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            await pipe.ConnectAsync(2000).ConfigureAwait(false);

            var writer = new StreamWriter(pipe, new UTF8Encoding(false), 4096, leaveOpen: true)
            { AutoFlush = true, NewLine = "\n" };
            var reader = new StreamReader(pipe, new UTF8Encoding(false), false, 4096, leaveOpen: true);

            var req = new JsonObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = 1,
                ["method"] = "amm/approval",
                ["params"] = new JsonObject
                {
                    ["token"] = token,
                    ["toolName"] = toolName,
                    ["toolInput"] = toolInput,
                },
            };
            await writer.WriteLineAsync(req.ToJsonString()).ConfigureAwait(false);

            using var readCts = new CancellationTokenSource(TimeSpan.FromSeconds(55));
            var line = await reader.ReadLineAsync(readCts.Token).ConfigureAwait(false);
            if (line == null) return ExitOk;

            var decision = (JsonNode.Parse(line) as JsonObject)?["result"]?["decision"]?.GetValue<string>();
            if (decision is "allow" or "deny")
            {
                Console.WriteLine(BuildApproveOutput(source, decision).ToJsonString());
            }
            return ExitOk;
        }
        catch
        {
            // GUI 不在 / 切断 / 解析失敗 — 決定なし = ペイン内プロンプトへ。
            // hook の世界では沈黙が正しい (stderr も汚さない)。
            return ExitOk;
        }
    }

    /// <summary>
    /// 決定 (allow/deny) を CLI ごとの hook 出力形式に組み立てる。
    /// - copilot: permissionRequest hook の応答 {"behavior": "allow"|"deny", ...}
    /// - それ以外 (claude / 省略): PermissionRequest hook の hookSpecificOutput 形式
    /// </summary>
    internal static JsonObject BuildApproveOutput(string? source, string decision)
    {
        const string DenyMessage = "Denied by the user via amm Approval Hub.";
        if (string.Equals(source, "copilot", StringComparison.OrdinalIgnoreCase))
        {
            return decision == "allow"
                ? new JsonObject { ["behavior"] = "allow" }
                : new JsonObject { ["behavior"] = "deny", ["message"] = DenyMessage };
        }
        return new JsonObject
        {
            ["hookSpecificOutput"] = new JsonObject
            {
                ["hookEventName"] = "PermissionRequest",
                ["decision"] = decision == "allow"
                    ? new JsonObject { ["behavior"] = "allow" }
                    : new JsonObject { ["behavior"] = "deny", ["message"] = DenyMessage },
            },
        };
    }

    // ---- list subcommand (Phase 4) ----

    private static async Task<int> RunList(string pipeName, int timeout)
    {
        using var pipe = await ConnectAsync(pipeName, timeout).ConfigureAwait(false);
        if (pipe == null) return ExitNoServer;

        var resp = await CallToolAsync(pipe, "list_participants", new JsonObject()).ConfigureAwait(false);
        if (resp == null) return ExitMcpError;

        if (resp["error"] is JsonNode err)
        {
            Console.Error.WriteLine($"amm-mcp: server error: {err["message"]?.GetValue<string>()}");
            return ExitMcpError;
        }

        var participants = resp["result"]?["structuredContent"]?["participants"];
        Console.WriteLine(participants?.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) ?? "[]");
        return ExitOk;
    }

    // ---- helpers ----

    private static async Task<NamedPipeClientStream?> ConnectAsync(string pipeName, int timeout)
    {
        var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        try
        {
            await pipe.ConnectAsync(timeout).ConfigureAwait(false);
            return pipe;
        }
        catch (TimeoutException)
        {
            Console.Error.WriteLine(
                $"amm-mcp: amm GUI に接続できませんでした (pipe={pipeName})。" +
                "GUI を起動してから再試行してください。");
            pipe.Dispose();
            return null;
        }
    }

    private static async Task<JsonNode?> CallToolAsync(NamedPipeClientStream pipe, string toolName, JsonObject arguments)
    {
        // MCP 標準の initialize → tools/call の順で送る。サーバ側は initialize なし
        // でも応答する寛容実装だが、明示的にハンドシェイクすると将来サーバを厳格化
        // しても CLI 側は動き続ける。
        var writer = new StreamWriter(pipe, new UTF8Encoding(false), 4096, leaveOpen: true)
        { AutoFlush = true, NewLine = "\n" };
        var reader = new StreamReader(pipe, new UTF8Encoding(false), false, 4096, leaveOpen: true);

        var initReq = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = 0,
            ["method"] = "initialize",
            ["params"] = new JsonObject
            {
                ["protocolVersion"] = "2024-11-05",
                ["capabilities"] = new JsonObject(),
                ["clientInfo"] = new JsonObject
                {
                    ["name"] = "amm-mcp-cli",
                    ["version"] = "0.3.0",
                },
            },
        };
        await writer.WriteLineAsync(initReq.ToJsonString()).ConfigureAwait(false);
        await reader.ReadLineAsync().ConfigureAwait(false); // initialize 応答は捨てる

        var req = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = 1,
            ["method"] = "tools/call",
            ["params"] = new JsonObject
            {
                ["name"] = toolName,
                ["arguments"] = arguments,
            },
        };
        await writer.WriteLineAsync(req.ToJsonString()).ConfigureAwait(false);
        var line = await reader.ReadLineAsync().ConfigureAwait(false);
        if (line == null) return null;
        return JsonNode.Parse(line);
    }

    private static string ReadAllStdin()
    {
        if (Console.IsInputRedirected)
            return Console.In.ReadToEnd().TrimEnd('\r', '\n');
        return ""; // 端末から起動された場合は対話入力を要求しない
    }

    /// <summary>
    /// 引数最初の語でモードを判定。
    /// 引数なし時は stdin が tty (= 対話端末) なら REPL、redirect されているなら
    /// MCP stdio bridge にフォールバック (MCP クライアントは stdin を pipe で繋ぐので)。
    /// </summary>
    private static (string mode, string[] rest) ResolveMode(string[] args)
    {
        if (args.Length == 0)
        {
            return Console.IsInputRedirected ? ("bridge", args) : ("repl", args);
        }
        return args[0].ToLowerInvariant() switch
        {
            "send" => ("send", args),
            "list" => ("list", args),
            "notify" => ("notify", args),
            "approve" => ("approve", args),
            "repl" => ("repl", args[1..]),
            "--bridge" => ("bridge", args[1..]),
            "--help" or "-h" or "/?" => ("help", args),
            _ when args[0].StartsWith("--") => ("bridge", args), // --pipe-name 等で始まる場合
            _ => ("help", args),
        };
    }

    private static string ResolvePipeName(string[] args)
    {
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (string.Equals(args[i], "--pipe-name", StringComparison.OrdinalIgnoreCase))
                return args[i + 1];
        }
        var fromEnv = Environment.GetEnvironmentVariable("AMM_MCP_PIPE_NAME");
        if (!string.IsNullOrEmpty(fromEnv)) return fromEnv;
        return $"amm-mcp-{Environment.UserName}";
    }

    private static int ResolveTimeout(string[] args)
    {
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (string.Equals(args[i], "--connect-timeout", StringComparison.OrdinalIgnoreCase)
                && int.TryParse(args[i + 1], out var ms))
                return ms;
        }
        return 5000;
    }

    /// <summary>引数 i が共通オプション (--pipe-name / --connect-timeout) または
    /// その引数値かを判定。send サブコマンドの positional 解釈から除外する。</summary>
    private static bool IsCommonOption(string[] args, int i)
    {
        if (string.Equals(args[i], "--pipe-name", StringComparison.OrdinalIgnoreCase)
            || string.Equals(args[i], "--connect-timeout", StringComparison.OrdinalIgnoreCase))
            return true;
        if (i > 0 && (string.Equals(args[i - 1], "--pipe-name", StringComparison.OrdinalIgnoreCase)
            || string.Equals(args[i - 1], "--connect-timeout", StringComparison.OrdinalIgnoreCase)))
            return true;
        return false;
    }

    private static int PrintUsageAndExit()
    {
        Console.Error.WriteLine("""
amm-mcp.exe — amm MCP server / CLI

使い方:
  amm-mcp.exe                          引数なし: 端末からなら REPL、stdin が
                                       redirect されているなら MCP stdio bridge
  amm-mcp.exe repl                     REPL を明示起動 (list/send/peek/help/quit)
  amm-mcp.exe --bridge                 MCP stdio bridge を明示起動
  amm-mcp.exe send <nickname> [msg]            指定 nickname へ送信 (mode=first、入力待ち優先)
  amm-mcp.exe send <nickname> --all [msg]      同 nickname を持つ全 MDI に送信
  amm-mcp.exe send --broadcast [msg]           nickname 登録済みの全 MDI に送信
  amm-mcp.exe list                             参加者一覧を JSON で stdout に出力
  amm-mcp.exe notify [--state <s>] [--source <l>]  CLI hook 用: 状態を amm GUI へ通知
                                               (環境変数 AMM_NOTIFY_ID 必須。無ければ no-op。
                                                payload は stdin / argv 末尾の JSON を自動判別)
  amm-mcp.exe approve [--source <l>]           CLI hook (Claude: PermissionRequest /
                                               Copilot: permissionRequest) 用: 許可要求を
                                               amm のポップアップへ転送し回答を待つ
                                               (AMM_NOTIFY_ID 必須。無回答は出力なし exit 0。
                                                --source copilot は {"behavior": ...} 形式で応答)
  msg を省略すると stdin から読み込む

オプション (全モード):
  --pipe-name <name>       既定 amm-mcp-{ユーザ名}
  --connect-timeout <ms>   既定 5000、0 で無制限

終了コード: 0=成功 / 1=引数不正 / 2=GUI 未起動 / 3=IO / 4=MCP エラー
""");
        return ExitBadArgs;
    }
}
