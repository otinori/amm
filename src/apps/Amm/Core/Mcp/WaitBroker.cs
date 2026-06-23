namespace Amm.Core.Mcp;

/// <summary>
/// MDI セッション状態の待機台帳 (req-20260622-mdi-window-control R-6)。
/// ApprovalBroker と同パターンで、amm.waitState リクエストを非同期保留し
/// amm/notify または WaitPatternDetector の状態遷移で解放する。
///
/// <list type="bullet">
///   <item>RegisterWait — amm.waitState / mdi/wait_state 受信時に呼ぶ (TCS を登録して Task を返す)</item>
///   <item>ResolveByToken — amm/notify 受信時・WaitPatternDetector 遷移時に呼ぶ</item>
///   <item>ReleaseBySession — セッションクローズ時にすべての pending を timeout 扱いで解放</item>
///   <item>ReleaseAll — アプリ終了時</item>
/// </list>
///
/// セッション識別は外部向け session_id (GUID) を用い、解放はパイプ経由の
/// NotifyToken をキーに行う (token → session_id のマッピングを内部管理)。
/// スレッドセーフ。イベントは任意スレッドから発火するので GUI 側で marshal すること。
/// </summary>
public sealed class WaitBroker
{
    public const int DefaultTimeoutMs = 300_000; // 5 分

    public sealed record WaitResult(string State, int ElapsedMs);

    private sealed class WaitEntry
    {
        public required string SessionId { get; init; }
        public required string TargetState { get; init; }  // "idle" | "attention"
        public required DateTime StartedUtc { get; init; }
        public required TaskCompletionSource<WaitResult> Tcs { get; init; }
        public System.Threading.Timer? TimeoutTimer { get; set; }
        public CancellationTokenRegistration CtRegistration { get; set; }
    }

    private readonly object _lock = new();
    // session_id → pending waits (同一セッションに複数並行 wait が来ても捌ける)
    private readonly Dictionary<string, List<WaitEntry>> _bySessionId = new(StringComparer.OrdinalIgnoreCase);
    // notifyToken → session_id の逆引き (amm/notify の token でルックアップ)
    private readonly Dictionary<string, string> _tokenToSession = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// amm.waitState 受信時: TCS を登録して Task を返す (非ブロッキング)。
    /// notifyToken は session に対応する TerminalChildForm.NotifyToken。
    /// ct はパイプセッションの CancellationToken (切断時に timeout 扱い解放)。
    /// </summary>
    public Task<WaitResult> RegisterWait(
        string sessionId, string notifyToken,
        string targetState, int timeoutMs,
        CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        var tcs = new TaskCompletionSource<WaitResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var entry = new WaitEntry
        {
            SessionId = sessionId,
            TargetState = targetState,
            StartedUtc = now,
            Tcs = tcs,
        };

        lock (_lock)
        {
            _tokenToSession[notifyToken] = sessionId;
            if (!_bySessionId.TryGetValue(sessionId, out var list))
            {
                list = [];
                _bySessionId[sessionId] = list;
            }
            list.Add(entry);
        }

        // タイマ / ct 登録は lock 外 (ApprovalBroker と同じ方針)
        entry.TimeoutTimer = new System.Threading.Timer(
            _ => RemoveAndResolve(entry, "timeout"),
            null, timeoutMs, Timeout.Infinite);

        if (ct.CanBeCanceled)
            entry.CtRegistration = ct.Register(() => RemoveAndResolve(entry, "timeout"));

        return tcs.Task;
    }

    /// <summary>
    /// amm/notify (token 付き) または WaitPatternDetector 遷移時に呼ぶ。
    /// token に対応する session_id の pending waits のうち targetState が一致するものを解放。
    /// </summary>
    public void ResolveByToken(string notifyToken, string state)
    {
        string? sessionId;
        lock (_lock)
        {
            if (!_tokenToSession.TryGetValue(notifyToken, out sessionId)) return;
        }
        ResolveBySessionId(sessionId, state);
    }

    /// <summary>
    /// session_id 直接指定で pending waits を解放。
    /// targetState が一致するエントリを state で解決する。
    /// </summary>
    public void ResolveBySessionId(string sessionId, string state)
    {
        WaitEntry[] toResolve;
        lock (_lock)
        {
            if (!_bySessionId.TryGetValue(sessionId, out var list)) return;
            toResolve = list
                .Where(e => string.Equals(e.TargetState, state, StringComparison.OrdinalIgnoreCase))
                .ToArray();
        }
        foreach (var e in toResolve) RemoveAndResolve(e, state);
    }

    /// <summary>セッションクローズ時: すべての pending wait を "timeout" で解放。</summary>
    public void ReleaseBySession(string sessionId)
    {
        WaitEntry[] toRelease;
        lock (_lock)
        {
            if (!_bySessionId.TryGetValue(sessionId, out var list)) return;
            toRelease = list.ToArray();
        }
        foreach (var e in toRelease) RemoveAndResolve(e, "timeout");
    }

    /// <summary>アプリ終了時: 全 pending を "timeout" で解放。</summary>
    public void ReleaseAll()
    {
        string[] sessionIds;
        lock (_lock) sessionIds = [.. _bySessionId.Keys];
        foreach (var sid in sessionIds) ReleaseBySession(sid);
    }

    private void RemoveAndResolve(WaitEntry entry, string state)
    {
        bool removed;
        lock (_lock)
        {
            if (!_bySessionId.TryGetValue(entry.SessionId, out var list))
            {
                removed = false;
            }
            else
            {
                removed = list.Remove(entry);
                if (removed && list.Count == 0)
                    _bySessionId.Remove(entry.SessionId);
            }
        }
        if (!removed) return; // 先勝ち
        entry.TimeoutTimer?.Dispose();
        entry.CtRegistration.Dispose();
        var elapsed = (int)(DateTime.UtcNow - entry.StartedUtc).TotalMilliseconds;
        entry.Tcs.TrySetResult(new WaitResult(state, elapsed));
    }
}
