namespace Amm.Core.Mcp;

/// <summary>
/// ToolUse 許可要求の台帳 (Approval Hub Level 2, UDR 起票中)。
///
/// CLI の PermissionRequest hook (amm-mcp.exe approve) からパイプ経由で届く
/// 許可要求を id 付きで保持し、GUI (ポップアップ) の回答を待つ。
/// 1 hook 発火 = 1 パイプ接続 = 1 RequestAsync 呼び出しで、複数ペイン /
/// 同一ペインの並列ツールが同時に要求しても独立エントリとして捌ける。
///
/// 解放トリガーは 4 種 (いずれも該当 TCS を resolve して台帳から除去):
///   1. 回答         — GUI が Resolve(id, "allow"/"deny")
///   2. 明示解放     — GUI が Resolve(id, null) / ReleaseByToken (ペイン
///                     アクティブ化・クローズ・トグル OFF)
///   3. タイムアウト — 既定 45 秒で null 解放 (hook 側 timeout 60 秒より先)
///   4. 接続切断     — hook プロセス消滅時に CancellationToken 経由で null 解放
///                     (幽霊要求がポップアップに残らない)
///
/// 「決定なし (null)」で解放された hook は決定 JSON を出力せずに終了し、
/// Claude Code は通常のペイン内プロンプトを表示する (安全側フォールバック)。
/// UI 非依存・スレッドセーフ。イベントは任意スレッドから発火するので
/// GUI 側で marshal すること。
/// </summary>
public sealed class ApprovalBroker
{
    /// <summary>既定の人間回答待ちタイムアウト (秒)。hook 側の登録 timeout
    /// (60 秒) より十分短くし、hook が強制 kill される前に必ず解放する。</summary>
    public const int DefaultTimeoutSeconds = 45;

    public sealed class ApprovalRequest
    {
        public required long Id { get; init; }
        /// <summary>AMM_NOTIFY_ID (= TerminalChildForm.NotifyToken)。</summary>
        public required string Token { get; init; }
        public required string ToolName { get; init; }
        /// <summary>tool_input の JSON 文字列 (表示用、構造は CLI 依存)。</summary>
        public required string ToolInputJson { get; init; }
        public required DateTime CreatedUtc { get; init; }
        public required DateTime DeadlineUtc { get; init; }
    }

    private sealed class Entry
    {
        public required ApprovalRequest Request { get; init; }
        public required TaskCompletionSource<string?> Tcs { get; init; }
        public System.Threading.Timer? TimeoutTimer { get; set; }
        public CancellationTokenRegistration CtRegistration { get; set; }
    }

    private readonly object _lock = new();
    private readonly Dictionary<long, Entry> _entries = new();
    private long _nextId;

    /// <summary>台帳の増減時に発火 (任意スレッド)。GUI はこれを受けて
    /// Snapshot() を取り直しポップアップを更新する。</summary>
    public event Action? PendingChanged;

    /// <summary>保留中の要求を到着順 (Id 昇順) で返す。</summary>
    public ApprovalRequest[] Snapshot()
    {
        lock (_lock)
        {
            return _entries.Values
                .Select(e => e.Request)
                .OrderBy(r => r.Id)
                .ToArray();
        }
    }

    public int PendingCount
    {
        get { lock (_lock) return _entries.Count; }
    }

    /// <summary>
    /// 許可要求を登録し、回答 ("allow" / "deny") または解放 (null) まで待つ。
    /// パイプセッションのワーカーから呼ばれる。ct はそのセッションの
    /// 切断トークン (切断 = hook 消滅 = null 解放)。
    /// </summary>
    public Task<string?> RequestAsync(
        string token, string toolName, string toolInputJson,
        TimeSpan? timeout = null, CancellationToken ct = default)
    {
        var effectiveTimeout = timeout ?? TimeSpan.FromSeconds(DefaultTimeoutSeconds);
        var now = DateTime.UtcNow;
        Entry entry;
        lock (_lock)
        {
            var id = ++_nextId;
            entry = new Entry
            {
                Request = new ApprovalRequest
                {
                    Id = id,
                    Token = token,
                    ToolName = toolName,
                    ToolInputJson = toolInputJson,
                    CreatedUtc = now,
                    DeadlineUtc = now + effectiveTimeout,
                },
                // RunContinuationsAsynchronously: Resolve 呼び出し元 (UI thread /
                // タイマスレッド) 上で await 継続を同期実行させない
                Tcs = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously),
            };
            _entries[id] = entry;
        }
        // タイマ / ct 登録は lock の外で行う。effectiveTimeout が極小、または ct が
        // 登録時点で既にキャンセル済みだと ct.Register / Timer は Resolve を「同期」
        // 起動する。lock 内でこれをやると _lock を保持したままユーザ起点のコールバック
        // チェーンに入る (再入・順序乱れの温床)。エントリは _entries 登録済みなので
        // 即時解決されても Resolve が正しく除去・後始末する。
        entry.TimeoutTimer = new System.Threading.Timer(
            _ => Resolve(entry.Request.Id, null),
            null, effectiveTimeout, Timeout.InfiniteTimeSpan);
        if (ct.CanBeCanceled)
            entry.CtRegistration = ct.Register(() => Resolve(entry.Request.Id, null));
        RaisePendingChanged();
        return entry.Tcs.Task;
    }

    /// <summary>
    /// 要求を解決して台帳から除去する。decision: "allow" / "deny" / null (決定なし解放)。
    /// 既に解決済み / 不在なら false (タイマ・切断・回答の競合は先勝ち)。
    /// </summary>
    public bool Resolve(long id, string? decision)
    {
        Entry? entry;
        lock (_lock)
        {
            if (!_entries.Remove(id, out entry)) return false;
        }
        entry!.TimeoutTimer?.Dispose();
        entry.CtRegistration.Dispose();
        entry.Tcs.TrySetResult(decision);
        RaisePendingChanged();
        return true;
    }

    /// <summary>
    /// 指定 token (= 1 ペイン) の要求をまとめて解放する。ペインのアクティブ化
    /// (回答方法をペイン内に切替) / ペインのクローズで呼ばれる。解放件数を返す。
    /// </summary>
    public int ReleaseByToken(string token, string? decision = null)
    {
        long[] ids;
        lock (_lock)
        {
            ids = _entries.Values
                .Where(e => e.Request.Token == token)
                .Select(e => e.Request.Id)
                .ToArray();
        }
        return ids.Count(id => Resolve(id, decision));
    }

    /// <summary>全要求を解放 (ポップアップトグル OFF / アプリ終了時)。</summary>
    public int ReleaseAll(string? decision = null)
    {
        long[] ids;
        lock (_lock)
        {
            ids = _entries.Keys.ToArray();
        }
        return ids.Count(id => Resolve(id, decision));
    }

    private void RaisePendingChanged()
    {
        try { PendingChanged?.Invoke(); }
        catch { /* GUI 側ハンドラの例外で台帳処理を壊さない */ }
    }
}
