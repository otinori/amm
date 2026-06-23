using System.Text.Json.Nodes;
using Amm.Core.Mcp;

namespace Amm.Tests;

public class MessageQueueTests
{
    [Fact]
    public void Enqueue_KeepsFifoOrder()
    {
        var q = new MessageQueue(maxPerNickname: 10);
        q.Enqueue("alice", "1");
        q.Enqueue("alice", "2");
        q.Enqueue("alice", "3");
        Assert.Equal(new[] { "1", "2", "3" }, q.Peek("alice"));
    }

    [Fact]
    public void Enqueue_OverCap_DropsOldest()
    {
        var q = new MessageQueue(maxPerNickname: 3);
        q.Enqueue("a", "1");
        q.Enqueue("a", "2");
        q.Enqueue("a", "3");
        q.Enqueue("a", "4");
        q.Enqueue("a", "5");
        Assert.Equal(new[] { "3", "4", "5" }, q.Peek("a"));
    }

    [Fact]
    public void DequeueAll_EmptiesAndReturns()
    {
        var q = new MessageQueue();
        q.Enqueue("a", "x");
        q.Enqueue("a", "y");
        var taken = q.DequeueAll("a");
        Assert.Equal(new[] { "x", "y" }, taken);
        Assert.Empty(q.Peek("a"));
    }

    [Fact]
    public void Peek_DoesNotMutate()
    {
        var q = new MessageQueue();
        q.Enqueue("a", "x");
        q.Peek("a");
        q.Peek("a");
        Assert.Single(q.Peek("a"));
    }

    [Fact]
    public void NicknameMatch_IsCaseInsensitive()
    {
        var q = new MessageQueue();
        q.Enqueue("Alice", "1");
        Assert.Single(q.Peek("alice"));
        Assert.Single(q.Peek("ALICE"));
    }

    [Fact]
    public void Snapshot_ReturnsAll()
    {
        var q = new MessageQueue();
        q.Enqueue("a", "x");
        q.Enqueue("b", "y");
        var snap = q.Snapshot();
        Assert.Equal(2, snap.Count);
        Assert.Single(snap["a"]);
        Assert.Single(snap["b"]);
    }
}

public class MessageDispatcherTests
{
    private sealed class FakeHost : IMcpHost
    {
        public Participant[] Participants { get; set; } = [];
        public List<(string nickname, int instance, string message)> Injections { get; } = [];
        public List<(string token, string state)> StateNotifications { get; } = [];
        public string? KnownToken { get; set; }
        public Participant[] GetParticipants() => Participants;
        public void Inject(string nickname, int instance, string message)
            => Injections.Add((nickname, instance, message));
        public bool NotifyChildState(string token, string state)
        {
            StateNotifications.Add((token, state));
            return token == KnownToken;
        }
        public OpenWindowResult OpenWindow(OpenWindowParams p) => throw new NotImplementedException();
        public bool CloseWindow(string sessionId, bool force) => throw new NotImplementedException();
        public string? GetNotifyTokenBySessionId(string sessionId) => throw new NotImplementedException();
    }

    private static Participant MakeP(string nickname, int instance, bool waiting) => new()
    {
        Nickname = nickname,
        ProfileName = nickname,
        Instance = instance,
        State = waiting ? "waiting" : "running",
        IsWaiting = waiting,
    };

    [Fact]
    public void Send_WithRecipient_AndFirstMode_PrefersWaiting()
    {
        var host = new FakeHost
        {
            // 起動順では a#1 が先頭。waiting なのは a#2 → first は a#2 になるべき
            Participants =
            [
                MakeP("a", 1, waiting: false),
                MakeP("a", 2, waiting: true),
            ],
        };
        var queue = new MessageQueue();
        var d = new MessageDispatcher(host, queue);
        var result = d.Send("a", "first", "hello");

        Assert.Equal(1, result.DeliveredCount);
        Assert.Equal(0, result.QueuedCount);
        Assert.Single(host.Injections);
        Assert.Equal(2, host.Injections[0].instance);
    }

    [Fact]
    public void Send_FirstMode_NoWaiting_FallsBackToLaunchOrder()
    {
        var host = new FakeHost
        {
            Participants =
            [
                MakeP("a", 1, waiting: false),
                MakeP("a", 2, waiting: false),
            ],
        };
        var queue = new MessageQueue();
        var d = new MessageDispatcher(host, queue);
        var result = d.Send("a", "first", "hello");

        // 入力待ちが居ないのでキュー行き、宛先は同 nickname の先頭
        Assert.Equal(0, result.DeliveredCount);
        Assert.Equal(1, result.QueuedCount);
        Assert.Equal(new[] { "hello" }, queue.Peek("a"));
    }

    [Fact]
    public void Send_AllMode_TargetsEveryMatch()
    {
        var host = new FakeHost
        {
            Participants =
            [
                MakeP("a", 1, waiting: true),
                MakeP("a", 2, waiting: true),
                MakeP("b", 1, waiting: true),
            ],
        };
        var d = new MessageDispatcher(host, new MessageQueue());
        var result = d.Send("a", "all", "hi");

        Assert.Equal(2, result.DeliveredCount);
        Assert.Equal(2, host.Injections.Count);
        Assert.Equal(new[] { "a" }, result.Recipients);
    }

    [Fact]
    public void Send_RecipientNull_BroadcastsToAll()
    {
        var host = new FakeHost
        {
            Participants =
            [
                MakeP("a", 1, waiting: true),
                MakeP("b", 1, waiting: false),
            ],
        };
        var queue = new MessageQueue();
        var d = new MessageDispatcher(host, queue);
        var result = d.Send(null, "first", "ping");

        Assert.Equal(1, result.DeliveredCount); // a
        Assert.Equal(1, result.QueuedCount);    // b queued
        Assert.Contains("a", result.Recipients);
        Assert.Contains("b", result.Recipients);
        Assert.Single(queue.Peek("b"));
    }

    [Fact]
    public void Send_UnknownRecipient_ReturnsZeros()
    {
        var d = new MessageDispatcher(new FakeHost(), new MessageQueue());
        var result = d.Send("ghost", "first", "msg");
        Assert.Equal(0, result.DeliveredCount);
        Assert.Equal(0, result.QueuedCount);
        Assert.Empty(result.Recipients);
    }

    [Fact]
    public void FlushQueue_DeliversPendingWhenWaiting()
    {
        var host = new FakeHost
        {
            Participants = [MakeP("a", 1, waiting: false)],
        };
        var queue = new MessageQueue();
        var d = new MessageDispatcher(host, queue);

        d.Send("a", "first", "m1");
        d.Send("a", "first", "m2");
        Assert.Equal(2, queue.Peek("a").Length);

        // 入力待ち遷移のシミュレーション
        host.Participants = [MakeP("a", 1, waiting: true)];
        var flushed = d.FlushQueue("a");

        Assert.Equal(2, flushed);
        Assert.Empty(queue.Peek("a"));
        Assert.Equal(2, host.Injections.Count);
    }

    [Fact]
    public void FlushQueue_NoWaitingTarget_KeepsMessages()
    {
        var host = new FakeHost
        {
            Participants = [MakeP("a", 1, waiting: false)],
        };
        var queue = new MessageQueue();
        var d = new MessageDispatcher(host, queue);
        d.Send("a", "first", "m1");

        var flushed = d.FlushQueue("a");
        Assert.Equal(0, flushed);
        Assert.Single(queue.Peek("a"));
    }

    [Fact]
    public void PeekQueue_FilteredByRecipient()
    {
        var queue = new MessageQueue();
        queue.Enqueue("a", "x");
        queue.Enqueue("b", "y");
        var d = new MessageDispatcher(new FakeHost(), queue);

        var only = d.PeekQueue("a");
        Assert.Single(only);
        Assert.Single(only["a"]);

        var all = d.PeekQueue(null);
        Assert.Equal(2, all.Count);
    }
}

public class McpPipeServerProtocolTests
{
    private static McpPipeServer NewServerWithFakeHost(out MessageDispatcher dispatcher)
    {
        var host = new MessageDispatcherTests_FakeHost
        {
            Participants =
            [
                new()
                {
                    Nickname = "claude",
                    ProfileName = "Claude Code",
                    Instance = 1,
                    State = "waiting",
                    IsWaiting = true,
                },
            ],
        };
        var queue = new MessageQueue();
        dispatcher = new MessageDispatcher(host, queue);
        return new McpPipeServer(dispatcher, pipeName: "AMM-Test-" + Guid.NewGuid().ToString("N"));
    }

    [Fact]
    public void Initialize_ReturnsServerInfoAndProtocolVersion()
    {
        var server = NewServerWithFakeHost(out _);
        var raw = server.HandleLine("""{"jsonrpc":"2.0","id":1,"method":"initialize","params":{}}""");
        Assert.NotNull(raw);
        var json = JsonNode.Parse(raw!) as JsonObject;
        Assert.NotNull(json);
        var result = json!["result"] as JsonObject;
        Assert.NotNull(result);
        Assert.Equal(McpPipeServer.ProtocolVersion, result!["protocolVersion"]?.GetValue<string>());
        Assert.Equal(McpPipeServer.ServerName, result["serverInfo"]?["name"]?.GetValue<string>());
    }

    [Fact]
    public void ToolsList_ReturnsThreeTools()
    {
        var server = NewServerWithFakeHost(out _);
        var raw = server.HandleLine("""{"jsonrpc":"2.0","id":2,"method":"tools/list"}""");
        var tools = JsonNode.Parse(raw!)!["result"]!["tools"] as JsonArray;
        Assert.NotNull(tools);
        Assert.Equal(6, tools!.Count);
        var names = tools.Select(t => t!["name"]!.GetValue<string>()).ToHashSet();
        Assert.Contains("send_message", names);
        Assert.Contains("list_participants", names);
        Assert.Contains("peek_queue", names);
        Assert.Contains("mdi/open", names);
        Assert.Contains("mdi/close", names);
        Assert.Contains("mdi/wait_state", names);
    }

    [Fact]
    public void ToolsCall_SendMessage_RoutesToDispatcher()
    {
        var server = NewServerWithFakeHost(out _);
        var raw = server.HandleLine(
            """{"jsonrpc":"2.0","id":3,"method":"tools/call","params":{"name":"send_message","arguments":{"recipient":"claude","message":"hi"}}}""");
        var json = JsonNode.Parse(raw!)!;
        var structured = json["result"]!["structuredContent"];
        Assert.Equal(1, structured!["delivered_count"]!.GetValue<int>());
        Assert.Equal(0, structured["queued_count"]!.GetValue<int>());
    }

    [Fact]
    public void ToolsCall_MissingMessage_ReturnsError()
    {
        var server = NewServerWithFakeHost(out _);
        var raw = server.HandleLine(
            """{"jsonrpc":"2.0","id":4,"method":"tools/call","params":{"name":"send_message","arguments":{}}}""");
        var json = JsonNode.Parse(raw!)!;
        Assert.NotNull(json["error"]);
        Assert.Equal(-32602, json["error"]!["code"]!.GetValue<int>());
    }

    [Fact]
    public void UnknownMethod_ReturnsMethodNotFound()
    {
        var server = NewServerWithFakeHost(out _);
        var raw = server.HandleLine("""{"jsonrpc":"2.0","id":5,"method":"unknown/method"}""");
        var json = JsonNode.Parse(raw!)!;
        Assert.Equal(-32601, json["error"]!["code"]!.GetValue<int>());
    }

    [Fact]
    public void Notification_HasNoResponse()
    {
        var server = NewServerWithFakeHost(out _);
        // id 無し = notification
        var raw = server.HandleLine("""{"jsonrpc":"2.0","method":"notifications/initialized"}""");
        Assert.Null(raw);
    }

    [Fact]
    public void ToolsCall_ListParticipants_ReturnsRegistered()
    {
        var server = NewServerWithFakeHost(out _);
        var raw = server.HandleLine(
            """{"jsonrpc":"2.0","id":6,"method":"tools/call","params":{"name":"list_participants","arguments":{}}}""");
        var participants = JsonNode.Parse(raw!)!["result"]!["structuredContent"]!["participants"] as JsonArray;
        Assert.NotNull(participants);
        Assert.Single(participants!);
        Assert.Equal("claude", participants[0]!["nickname"]!.GetValue<string>());
    }

    // ---- amm/notify (hook 駆動状態通知, UDR-amm-20260605T0523-7e1) ----

    private static McpPipeServer NewServerWithHost(out MessageDispatcherTests_FakeHost host)
    {
        host = new MessageDispatcherTests_FakeHost { KnownToken = "tok-123" };
        var dispatcher = new MessageDispatcher(host, new MessageQueue());
        return new McpPipeServer(dispatcher, pipeName: "AMM-Test-" + Guid.NewGuid().ToString("N"));
    }

    [Fact]
    public void AmmNotify_KnownToken_ReturnsMatchedTrue()
    {
        var server = NewServerWithHost(out var host);
        var raw = server.HandleLine(
            """{"jsonrpc":"2.0","id":7,"method":"amm/notify","params":{"token":"tok-123","state":"idle"}}""");
        var json = JsonNode.Parse(raw!)!;
        Assert.True(json["result"]!["matched"]!.GetValue<bool>());
        Assert.Single(host.StateNotifications);
        Assert.Equal(("tok-123", "idle"), host.StateNotifications[0]);
    }

    [Fact]
    public void AmmNotify_UnknownToken_ReturnsMatchedFalse()
    {
        var server = NewServerWithHost(out _);
        var raw = server.HandleLine(
            """{"jsonrpc":"2.0","id":8,"method":"amm/notify","params":{"token":"nope","state":"idle"}}""");
        var json = JsonNode.Parse(raw!)!;
        Assert.False(json["result"]!["matched"]!.GetValue<bool>());
    }

    [Fact]
    public void AmmNotify_MissingParams_ReturnsError()
    {
        var server = NewServerWithHost(out _);
        var raw = server.HandleLine(
            """{"jsonrpc":"2.0","id":9,"method":"amm/notify","params":{"token":"tok-123"}}""");
        var json = JsonNode.Parse(raw!)!;
        Assert.Equal(-32602, json["error"]!["code"]!.GetValue<int>());
    }

    [Fact]
    public void AmmNotify_AsNotificationWithoutId_HasNoResponse()
    {
        var server = NewServerWithHost(out var host);
        var raw = server.HandleLine(
            """{"jsonrpc":"2.0","method":"amm/notify","params":{"token":"tok-123","state":"attention"}}""");
        Assert.Null(raw);
        Assert.Single(host.StateNotifications); // 応答なしでも通知自体は処理される
    }

    // ---- amm/approval (ToolUse 許可集約, Approval Hub Level 2) ----

    private static McpPipeServer NewServerWithBroker(out ApprovalBroker broker)
    {
        broker = new ApprovalBroker();
        var dispatcher = new MessageDispatcher(new MessageDispatcherTests_FakeHost(), new MessageQueue());
        return new McpPipeServer(dispatcher,
            pipeName: "AMM-Test-" + Guid.NewGuid().ToString("N"),
            approvalBroker: broker);
    }

    [Fact]
    public async Task AmmApproval_AllowDecision_ReturnsAllow()
    {
        var server = NewServerWithBroker(out var broker);
        var task = server.HandleLineAsync(
            """{"jsonrpc":"2.0","id":10,"method":"amm/approval","params":{"token":"tok-1","toolName":"Bash","toolInput":{"command":"git push"}}}""",
            CancellationToken.None);

        // 人間の回答を模擬: 台帳に積まれた要求を allow で解決
        ApprovalBroker.ApprovalRequest req = null!;
        for (int i = 0; i < 100 && broker.PendingCount == 0; i++) await Task.Delay(10);
        req = Assert.Single(broker.Snapshot());
        Assert.Equal("Bash", req.ToolName);
        Assert.Contains("git push", req.ToolInputJson);
        broker.Resolve(req.Id, "allow");

        var raw = await task.WaitAsync(TimeSpan.FromSeconds(5));
        var json = JsonNode.Parse(raw!)!;
        Assert.Equal("allow", json["result"]!["decision"]!.GetValue<string>());
    }

    [Fact]
    public async Task AmmApproval_Released_ReturnsNullDecision()
    {
        var server = NewServerWithBroker(out var broker);
        var task = server.HandleLineAsync(
            """{"jsonrpc":"2.0","id":11,"method":"amm/approval","params":{"token":"tok-1","toolName":"Bash"}}""",
            CancellationToken.None);

        for (int i = 0; i < 100 && broker.PendingCount == 0; i++) await Task.Delay(10);
        broker.ReleaseByToken("tok-1"); // ペインアクティブ化 = 決定なし解放

        var raw = await task.WaitAsync(TimeSpan.FromSeconds(5));
        var json = JsonNode.Parse(raw!)!;
        var decision = json["result"]!["decision"];
        Assert.Null(decision); // 決定なし → hook はペイン内プロンプトへフォールバック
    }

    [Fact]
    public async Task AmmApproval_SessionCancelled_ReturnsNullDecision()
    {
        var server = NewServerWithBroker(out var broker);
        using var cts = new CancellationTokenSource();
        var task = server.HandleLineAsync(
            """{"jsonrpc":"2.0","id":12,"method":"amm/approval","params":{"token":"tok-1","toolName":"Bash"}}""",
            cts.Token);

        for (int i = 0; i < 100 && broker.PendingCount == 0; i++) await Task.Delay(10);
        cts.Cancel(); // パイプ切断 (hook プロセス消滅)

        var raw = await task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Null(JsonNode.Parse(raw!)!["result"]!["decision"]);
        Assert.Equal(0, broker.PendingCount); // 台帳から掃除済み
    }

    [Fact]
    public async Task AmmApproval_MissingParams_ReturnsError()
    {
        var server = NewServerWithBroker(out _);
        var raw = await server.HandleLineAsync(
            """{"jsonrpc":"2.0","id":13,"method":"amm/approval","params":{"token":"tok-1"}}""",
            CancellationToken.None);
        Assert.Equal(-32602, JsonNode.Parse(raw!)!["error"]!["code"]!.GetValue<int>());
    }

    [Fact]
    public async Task AmmApproval_WithoutBroker_ReturnsNullDecisionImmediately()
    {
        var server = NewServerWithFakeHost(out _); // broker 未配線
        var raw = await server.HandleLineAsync(
            """{"jsonrpc":"2.0","id":14,"method":"amm/approval","params":{"token":"tok-1","toolName":"Bash"}}""",
            CancellationToken.None);
        Assert.Null(JsonNode.Parse(raw!)!["result"]!["decision"]);
    }

    [Fact]
    public async Task HandleLineAsync_NonApprovalLine_FallsThroughToSyncPath()
    {
        var server = NewServerWithBroker(out _);
        // body に "amm/approval" を含むが method は send_message → 同期経路で正しく処理
        var raw = await server.HandleLineAsync(
            """{"jsonrpc":"2.0","id":15,"method":"tools/call","params":{"name":"send_message","arguments":{"recipient":"claude","message":"see \"amm/approval\" docs"}}}""",
            CancellationToken.None);
        var json = JsonNode.Parse(raw!)!;
        Assert.NotNull(json["result"]); // tools/call として処理された
    }
}

/// <summary>
/// amm-mcp approve の決定出力が CLI ごとの hook 応答形式になることを検証する
/// (Claude Code: hookSpecificOutput / Copilot CLI: behavior 直書き)。
/// </summary>
public class ApproveOutputFormatTests
{
    [Fact]
    public void Claude_Allow_UsesHookSpecificOutput()
    {
        var output = Amm.Mcp.Program.BuildApproveOutput("claude", "allow");
        Assert.Equal("PermissionRequest",
            output["hookSpecificOutput"]!["hookEventName"]!.GetValue<string>());
        Assert.Equal("allow",
            output["hookSpecificOutput"]!["decision"]!["behavior"]!.GetValue<string>());
    }

    [Fact]
    public void SourceOmitted_FallsBackToClaudeFormat()
    {
        var output = Amm.Mcp.Program.BuildApproveOutput(null, "deny");
        Assert.Equal("deny",
            output["hookSpecificOutput"]!["decision"]!["behavior"]!.GetValue<string>());
        Assert.False(string.IsNullOrEmpty(
            output["hookSpecificOutput"]!["decision"]!["message"]!.GetValue<string>()));
    }

    [Fact]
    public void Copilot_Allow_UsesBehaviorDirectly()
    {
        var output = Amm.Mcp.Program.BuildApproveOutput("copilot", "allow");
        Assert.Equal("allow", output["behavior"]!.GetValue<string>());
        Assert.Null(output["hookSpecificOutput"]);
    }

    [Fact]
    public void Copilot_Deny_IncludesMessage()
    {
        var output = Amm.Mcp.Program.BuildApproveOutput("copilot", "deny");
        Assert.Equal("deny", output["behavior"]!.GetValue<string>());
        Assert.False(string.IsNullOrEmpty(output["message"]!.GetValue<string>()));
    }
}

/// <summary>
/// MessageDispatcherTests 内で同名の private FakeHost を再利用したいが
/// 別クラスからアクセスできないため public 版を別途用意。
/// </summary>
public sealed class MessageDispatcherTests_FakeHost : IMcpHost
{
    public Participant[] Participants { get; set; } = [];
    public List<(string nickname, int instance, string message)> Injections { get; } = [];
    public List<(string token, string state)> StateNotifications { get; } = [];
    public string? KnownToken { get; set; }
    public Participant[] GetParticipants() => Participants;
    public void Inject(string nickname, int instance, string message)
        => Injections.Add((nickname, instance, message));
    public bool NotifyChildState(string token, string state)
    {
        StateNotifications.Add((token, state));
        return token == KnownToken;
    }
    public OpenWindowResult OpenWindow(OpenWindowParams p) => throw new NotImplementedException();
    public bool CloseWindow(string sessionId, bool force) => throw new NotImplementedException();
    public string? GetNotifyTokenBySessionId(string sessionId) => throw new NotImplementedException();
}
