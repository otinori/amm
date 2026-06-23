using Amm.Core.Mcp;

namespace Amm.Tests;

/// <summary>
/// ApprovalBroker (ToolUse 許可要求の台帳, Approval Hub Level 2) の検証。
/// 解放トリガー 4 種 (回答 / 明示解放 / タイムアウト / 接続切断) を網羅する。
/// </summary>
public class ApprovalBrokerTests
{
    [Fact]
    public async Task Resolve_Allow_CompletesRequestAndEmptiesPending()
    {
        var broker = new ApprovalBroker();
        var task = broker.RequestAsync("tok-1", "Bash", """{"command":"ls"}""");

        Assert.Equal(1, broker.PendingCount);
        var req = Assert.Single(broker.Snapshot());
        Assert.Equal("Bash", req.ToolName);

        Assert.True(broker.Resolve(req.Id, "allow"));
        Assert.Equal("allow", await task);
        Assert.Equal(0, broker.PendingCount);
    }

    [Fact]
    public async Task Resolve_Deny_ReturnsDeny()
    {
        var broker = new ApprovalBroker();
        var task = broker.RequestAsync("tok-1", "Bash", "{}");
        broker.Resolve(broker.Snapshot()[0].Id, "deny");
        Assert.Equal("deny", await task);
    }

    [Fact]
    public void Resolve_UnknownId_ReturnsFalse()
    {
        var broker = new ApprovalBroker();
        Assert.False(broker.Resolve(999, "allow"));
    }

    [Fact]
    public async Task Resolve_Twice_SecondReturnsFalse()
    {
        var broker = new ApprovalBroker();
        var task = broker.RequestAsync("tok-1", "Bash", "{}");
        var id = broker.Snapshot()[0].Id;
        Assert.True(broker.Resolve(id, "allow"));
        Assert.False(broker.Resolve(id, "deny")); // 先勝ち
        Assert.Equal("allow", await task);
    }

    [Fact]
    public async Task Timeout_ResolvesNull()
    {
        var broker = new ApprovalBroker();
        var task = broker.RequestAsync("tok-1", "Bash", "{}",
            timeout: TimeSpan.FromMilliseconds(50));
        Assert.Null(await task.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(0, broker.PendingCount);
    }

    [Fact]
    public async Task ConnectionCancellation_ResolvesNull()
    {
        var broker = new ApprovalBroker();
        using var cts = new CancellationTokenSource();
        var task = broker.RequestAsync("tok-1", "Bash", "{}", ct: cts.Token);

        cts.Cancel(); // パイプ切断 = hook プロセス消滅
        Assert.Null(await task.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(0, broker.PendingCount); // 幽霊要求が残らない
    }

    [Fact]
    public async Task ReleaseByToken_ReleasesOnlyMatchingPane()
    {
        var broker = new ApprovalBroker();
        var t1 = broker.RequestAsync("pane-A", "Bash", "{}");
        var t2 = broker.RequestAsync("pane-A", "Edit", "{}");
        var t3 = broker.RequestAsync("pane-B", "Bash", "{}");

        Assert.Equal(2, broker.ReleaseByToken("pane-A"));
        Assert.Null(await t1.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Null(await t2.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.False(t3.IsCompleted); // pane-B は影響なし
        Assert.Equal(1, broker.PendingCount);

        broker.ReleaseAll();
        Assert.Null(await t3.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task Snapshot_OrdersByArrival()
    {
        var broker = new ApprovalBroker();
        var t1 = broker.RequestAsync("a", "Tool1", "{}");
        var t2 = broker.RequestAsync("b", "Tool2", "{}");
        var t3 = broker.RequestAsync("c", "Tool3", "{}");

        var snap = broker.Snapshot();
        Assert.Equal(["Tool1", "Tool2", "Tool3"], snap.Select(r => r.ToolName).ToArray());

        broker.ReleaseAll();
        await Task.WhenAll(t1, t2, t3);
    }

    [Fact]
    public void PendingChanged_FiresOnAddAndResolve()
    {
        var broker = new ApprovalBroker();
        int fired = 0;
        broker.PendingChanged += () => Interlocked.Increment(ref fired);

        _ = broker.RequestAsync("tok", "Bash", "{}");
        Assert.Equal(1, fired);
        broker.Resolve(broker.Snapshot()[0].Id, "allow");
        Assert.Equal(2, fired);
    }
}
