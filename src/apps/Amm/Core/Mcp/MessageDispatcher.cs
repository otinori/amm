namespace Amm.Core.Mcp;

/// <summary>
/// 1 つの参加者 (= nickname を持ち alive な MDI) のスナップショット
/// (UDR-amm-20260427T0225-7a3)。
/// </summary>
public sealed class Participant
{
    public required string Nickname { get; init; }
    public required string ProfileName { get; init; }
    public required int Instance { get; init; }
    public required string State { get; init; } // "running" | "waiting" | "stopped" | "unknown"
    public required bool IsWaiting { get; init; }
    /// <summary>mdi/wait_state 等で使う session_id。</summary>
    public string? SessionId { get; init; }

    /// <summary>nickname + instance を一意に識別するためのキー。</summary>
    public string Key => $"{Nickname}#{Instance}";
}

/// <summary>MDI ウィンドウをプログラムから開くためのパラメータ (req-20260622-mdi-window-control R-1)。
/// Command または ProfileName のどちらかが必須。ProfileName 指定時は既存プロファイルを使う。</summary>
public sealed class OpenWindowParams
{
    /// <summary>実行コマンド (ProfileName 未指定時は必須)。</summary>
    public string? Command { get; init; }
    public string[] Args { get; init; } = [];
    public string? Title { get; init; }
    public string? WorkingDirectory { get; init; }
    /// <summary>profiles.amm 上のプロファイル名 (大小文字不問)。指定すると既存プロファイルの設定を継承する。</summary>
    public string? ProfileName { get; init; }
}

/// <summary>OpenWindow の戻り値。SessionId が null でなければ成功。</summary>
public sealed class OpenWindowResult
{
    public string? SessionId { get; init; }
    public string? Error { get; init; }
}

/// <summary>
/// MessageDispatcher が UI 層 (MdiParentForm) に依存しないようにする抽象。
/// 実装側が UI thread への marshal を吸収する。
/// </summary>
public interface IMcpHost
{
    Participant[] GetParticipants();

    /// <summary>該当 nickname / instance の MDI に文字列を送る。Wait 状態を見ない強制送信。
    /// 実装は UI thread で SendText を呼ぶよう marshal すること。</summary>
    void Inject(string nickname, int instance, string message);

    /// <summary>
    /// CLI hook 由来の状態通知 (amm/notify) を NotifyToken で識別される MDI へ
    /// 反映する (UDR-amm-20260605T0523-7e1)。該当 MDI が見つかれば true。
    /// nickname の有無に依存しない (hook 検知は MCP 送受信と独立して機能する)。
    /// </summary>
    bool NotifyChildState(string token, string state);

    // ---- req-20260622-mdi-window-control ----

    /// <summary>新しい MDI ウィンドウをプログラムから開く。UI thread へ marshal して
    /// TerminalChildForm を生成し session_id を返す。</summary>
    OpenWindowResult OpenWindow(OpenWindowParams p);

    /// <summary>指定 session_id のウィンドウを閉じる。force=true で確認ダイアログなし。
    /// 見つからない場合は false。</summary>
    bool CloseWindow(string sessionId, bool force);

    /// <summary>session_id に対応する TerminalChildForm.NotifyToken を返す。
    /// WaitBroker の RegisterWait に渡す。見つからなければ null。</summary>
    string? GetNotifyTokenBySessionId(string sessionId);
}

/// <summary>
/// MCP の 3 ツール (send_message / list_participants / peek_queue) のロジック層
/// (UDR-amm-20260427T0225-7a3)。UI / IPC から独立、ユニットテスト可能。
/// </summary>
public sealed class MessageDispatcher
{
    private readonly IMcpHost _host;
    private readonly MessageQueue _queue;

    public MessageDispatcher(IMcpHost host, MessageQueue queue)
    {
        _host = host;
        _queue = queue;
    }

    public sealed class SendResult
    {
        public int DeliveredCount { get; init; }
        public int QueuedCount { get; init; }
        public string[] Recipients { get; init; } = [];
    }

    /// <summary>
    /// メッセージ送信。recipient null/空 = eligible 全員に broadcast。
    /// mode "first" (既定) は入力待ち優先 → 起動順 fallback。"all" は同 nickname 全員。
    /// </summary>
    public SendResult Send(string? recipient, string mode, string message)
    {
        var participants = _host.GetParticipants();
        var targets = ResolveTargets(participants, recipient, mode);

        int delivered = 0;
        int queued = 0;
        var recipientNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var t in targets)
        {
            recipientNames.Add(t.Nickname);
            if (t.IsWaiting)
            {
                _host.Inject(t.Nickname, t.Instance, message);
                delivered++;
            }
            else
            {
                _queue.Enqueue(t.Nickname, message);
                queued++;
            }
        }

        return new SendResult
        {
            DeliveredCount = delivered,
            QueuedCount = queued,
            Recipients = [.. recipientNames],
        };
    }

    /// <summary>
    /// 指定 nickname のキューを flush する。alive な MDI のうち入力待ちかつ最初に
    /// マッチする 1 人へ全件順番に注入。flush できなかったメッセージはキューに残す。
    /// WaitStateChanged で「入力待ち」遷移時に MdiParentForm から呼ばれる。
    /// </summary>
    public int FlushQueue(string nickname)
    {
        var participants = _host.GetParticipants();
        var target = participants.FirstOrDefault(p =>
            string.Equals(p.Nickname, nickname, StringComparison.OrdinalIgnoreCase) && p.IsWaiting);
        if (target == null) return 0;

        var pending = _queue.DequeueAll(nickname);
        foreach (var msg in pending)
            _host.Inject(target.Nickname, target.Instance, msg);
        return pending.Length;
    }

    public Participant[] ListParticipants() => _host.GetParticipants();

    /// <summary>amm/notify (hook 駆動状態通知) の host への pass-through。</summary>
    public bool NotifyChildState(string token, string state) => _host.NotifyChildState(token, state);

    // ---- req-20260622-mdi-window-control ----

    public OpenWindowResult OpenWindow(OpenWindowParams p) => _host.OpenWindow(p);
    public bool CloseWindow(string sessionId, bool force) => _host.CloseWindow(sessionId, force);
    public string? GetNotifyTokenBySessionId(string sessionId) => _host.GetNotifyTokenBySessionId(sessionId);

    public Dictionary<string, string[]> PeekQueue(string? recipient)
    {
        if (string.IsNullOrEmpty(recipient))
            return _queue.Snapshot();
        return new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            [recipient] = _queue.Peek(recipient),
        };
    }

    /// <summary>
    /// recipient / mode から対象の Participant 集合を決定する。
    ///   recipient = null/空 → eligible 全員 (broadcast)
    ///   recipient 指定 + mode = "first" → 入力待ち優先 → 起動順 (= 配列先頭)
    ///   recipient 指定 + mode = "all"   → 同 nickname 全員
    /// 起動順は host が GetParticipants で返す配列順とする。
    /// </summary>
    internal static Participant[] ResolveTargets(Participant[] participants, string? recipient, string mode)
    {
        if (string.IsNullOrEmpty(recipient))
            return participants;

        var matches = participants
            .Where(p => string.Equals(p.Nickname, recipient, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (matches.Length == 0) return [];

        if (string.Equals(mode, "all", StringComparison.OrdinalIgnoreCase))
            return matches;

        // mode = "first" (既定): 入力待ち優先、無ければ起動順 (= 先頭)
        var waiting = matches.FirstOrDefault(p => p.IsWaiting);
        return [waiting ?? matches[0]];
    }
}
