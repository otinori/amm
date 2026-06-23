using System.Text.RegularExpressions;

namespace Amm.Core;

public enum WaitState
{
    Unknown,
    Running,
    WaitingForInput,
    Stopped
}

/// <summary>
/// WaitState を「状態が一目で分かる」記号にマップする。タイトルバーと MDI 切替
/// ボタンで共通利用し、表記のブレを防ぐ。再生 / 停止のメタファに寄せている:
///   実行中     = ▶ (再生 = 処理中)
///   入力待ち   = ● (許可・確認待ち時は ⚠ を最優先)
///   停止 (終了)= ■ (停止)
///   不明       = ?
/// </summary>
public static class WaitStateGlyph
{
    public static char For(WaitState state, bool hasAttention = false) => state switch
    {
        WaitState.Running => '▶',
        WaitState.WaitingForInput => hasAttention ? '⚠' : '●',
        WaitState.Unknown => '?',
        WaitState.Stopped => '■',
        _ => ' ',
    };
}

public sealed class WaitPatternDetector : IDisposable
{
    private static readonly Regex[] DefaultPatterns =
    [
        new(@"[\$#>]\s*$", RegexOptions.Compiled),
        new(@"PS\s+\S+>\s*$", RegexOptions.Compiled),
        new(@"\(y/n\)\s*$", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new(@"password[:\s]*$", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new(@":\s*$", RegexOptions.Compiled),
        new(@"\?\s*$", RegexOptions.Compiled),
        // Claude Code / Codex 等の TUI ボックスプロンプト (`│ > placeholder │`)
        // 両端の縦罫線を剥がしたあとに行頭が `>` で始まれば入力待ちとみなす。
        new(@"^>(?:\s|$)", RegexOptions.Compiled),
        // 行頭型 TUI プロンプト (Inquirer.js / Copilot CLI 等で使われる
        // `❯` U+276F / `›` U+203A)。両端枠を剥がしたあとに行頭がこれらで始まれば
        // 入力待ちとみなす。Codex / Copilot CLI の新 UI 互換のための受け口。
        new(@"^[❯›](?:\s|$)", RegexOptions.Compiled),
        new(@"続行するには何かキーを押してください", RegexOptions.Compiled),
    ];

    private readonly Regex[] _patterns;
    private readonly System.Timers.Timer _timeoutTimer;
    // 出力がこの時間 (ms) 以上途絶え、かつプロンプトが一致しないとき、コマンドは
    // 終了して「解析できないプロンプトで入力待ち」とみなし WaitingForInput へ落とす。
    // Claude/Codex は完了直後の末尾再描画でプロンプト行が照合窓から押し出され
    // Running のまま固着することがあり (= リサイズ等の新規出力でしか直らない)、
    // その自力回復のための沈黙しきい値。解析可能プロンプト (cmd/PowerShell) は
    // 即時 or 照合で確定するのでここに到達しない。テスト容易化のため注入可。
    private readonly int _quietToWaitingMs;
    // 直近に Feed (出力到来) した時刻 (Environment.TickCount64)。Feed (ConPTY
    // 読みスレッド) と OnQuietTick (タイマスレッド) の双方から触れるため Interlocked。
    private long _lastFeedAtTicks;
    // _recentLines / _lastLine は Feed (ConPTY 読みスレッド) と Elapsed タイマ
    // (スレッドプール) の両方から触れられる。Queue/string はスレッドセーフでないため
    // この lock で保護する (列挙中変更による例外を防ぐ)。SetState/イベント発火は
    // lock の外で行う。
    private readonly object _linesLock = new();
    private string _lastLine = "";
    // Claude Code / Codex は box プロンプトの「下」にヒント行 (`? for shortcuts`
    // 等) を描画することがあり、最終行はプロンプトではなくヒント行になる。
    // 直近 N 行を残してパターン照合の対象にすることで box 内のプロンプト行を
    // 取りこぼさないようにする。実行中にバースト出力が流れれば古い行は押し
    // 出されるので、誤検知 (古いプロンプトに反応) は実用上ほぼ起きない。
    private readonly Queue<string> _recentLines = new();
    // 大きめに取る理由: Claude Code / Codex は応答終了時にプロンプト行を
    // 再描画するが、その前に流れる応答テキストが 12 行を超えるとプロンプト行が
    // バッファから押し出されて wait 判定が外れる (= 入力待ちなのに「実行中」
    // 表示のまま残る)。50 行あれば典型的な応答末尾の redraw を捕捉できる。
    // 50 string × ~200 char ≈ 10KB / detector で問題ないコスト。
    private const int MaxRecentLines = 50;
    private WaitState _currentState = WaitState.Unknown;

    public event Action<WaitState>? StateChanged;

    public WaitState CurrentState => _currentState;

    public WaitPatternDetector(string[]? waitPatterns = null, int quietToWaitingMs = 4000)
    {
        _quietToWaitingMs = quietToWaitingMs;
        if (waitPatterns != null && waitPatterns.Length > 0)
        {
            // ユーザ指定パターンは defaults に「追加」する形で合成する。
            // 旧仕様 (replace) では profiles.json の `>\s*$` のような末尾型単独
            // 指定が defaults を完全に上書きしてしまい、Codex / Copilot CLI の
            // 行頭型 box プロンプト (`│ > placeholder │`) で wait 検出が外れる
            // 事故が起きていた。defaults を常に併走させることで、profile が
            // 古いままでも最低限の TUI プロンプト検出は効く。
            _patterns =
            [
                .. waitPatterns.Select(CompileUserPattern),
                .. DefaultPatterns,
            ];
        }
        else
        {
            _patterns = DefaultPatterns;
        }

        _timeoutTimer = new System.Timers.Timer(500) { AutoReset = false };
        _timeoutTimer.Elapsed += (_, _) => OnQuietTick();
    }

    public void Feed(string data)
    {
        Interlocked.Exchange(ref _lastFeedAtTicks, Environment.TickCount64);
        _timeoutTimer.Stop();

        // Update last line (track the last non-empty line from the terminal)。
        // ただし Claude Code / Codex のような box drawing UI ではプロンプト行
        // (`│ >    │`) の直後に底辺 (`╰─...─╯`) が描画されるため、最後の
        // 非空行を常に採るとボックスの装飾だけが残ってプロンプトを取りこぼす。
        // 装飾オンリーの行 (= 全文字が空白 + ボックス系) は無視する。
        bool matched;
        lock (_linesLock)
        {
            var lines = data.Split('\n');
            foreach (var line in lines)
            {
                var stripped = StripAnsi(line).TrimEnd('\r');
                if (stripped.Length == 0) continue;
                if (IsAllFrameChars(stripped)) continue;
                _lastLine = stripped;
                _recentLines.Enqueue(stripped);
                while (_recentLines.Count > MaxRecentLines) _recentLines.Dequeue();
            }
            matched = MatchesAnyPatternLocked();
        }

        // 新しい行で既にプロンプトが見えているなら即座に WaitingForInput。
        // この即時判定がないと、キーストロークごとに一旦 Running に落ちて
        // 500ms タイマ満了後にようやく WaitingForInput へ戻り「●→⚙→●」と
        // フリッカが見える。
        //
        // 注: Claude Code / Codex は処理中もプロンプトを表示し続けるため、
        // wait pattern だけでは「待機」と「処理中」を厳密には区別できない。
        // ただしユーザは処理中も追加入力できる (Claude/Codex の仕様) ので、
        // 「待機 = 入力可能」と捉えて wait pattern マッチをそのまま信じる。
        // (SetState/イベント発火は lock の外で行う。)
        if (matched)
        {
            SetState(WaitState.WaitingForInput);
            return;
        }

        SetState(WaitState.Running);
        _timeoutTimer.Start();
    }

    public void NotifyProcessExited()
    {
        _timeoutTimer.Stop();
        SetState(WaitState.Stopped);
    }

    /// <summary>
    /// 外部イベント (CLI hook → amm-mcp notify 経由) による状態の強制確定
    /// (UDR-amm-20260605T0523-7e1)。正規表現スクレイピングと違い CLI 自身の
    /// 申告なので無条件で信じる。ただし Stopped (プロセス終了) からの復活は
    /// 不可。以降の Feed (出力到来) では通常どおり Running へ遷移するので、
    /// 検出器の状態機械とは自然に合流する。
    /// </summary>
    public void ForceState(WaitState state)
    {
        if (_currentState == WaitState.Stopped) return;
        _timeoutTimer.Stop();
        SetState(state);
    }

    /// <summary>
    /// 出力が途絶えてから 500ms ごとに走る再評価 (Running の間だけ意味を持つ)。
    ///   1) プロンプトが一致すれば WaitingForInput へ確定。
    ///   2) 一致しなくても出力が _quietToWaitingMs 以上途絶えていれば、コマンドは
    ///      終了して「解析できないプロンプトで入力待ち」とみなし WaitingForInput へ。
    ///      これが無いと、完了直後の末尾再描画でプロンプト行が照合窓から押し出された
    ///      ケースで Running のまま固着し、MDI リサイズ等の新規出力でしか直らない
    ///      (= タイトル/ボタンの状態アイコンが「処理中」のまま最新化されない事象)。
    ///   3) どちらでもなければ再武装し、沈黙しきい値に達するまで間欠的に再評価する。
    /// 旧実装は 500ms 後に 1 度だけ照合し、未一致なら再評価せず Running に固着していた。
    /// cmd/PowerShell 等の解析可能プロンプトは Feed 即時 or 1) で確定するのでここで
    /// 沈黙フォールバック (2) に到達しない。Unknown には落とさない (従来のフラップ防止
    /// 方針は維持し、未確定は Running、確定済みは WaitingForInput の 2 状態に収束)。
    /// </summary>
    private void OnQuietTick()
    {
        if (_currentState != WaitState.Running) return;

        bool matched;
        lock (_linesLock) { matched = MatchesAnyPatternLocked(); }
        if (matched)
        {
            SetState(WaitState.WaitingForInput);
            return;
        }

        long quietMs = Environment.TickCount64 - Interlocked.Read(ref _lastFeedAtTicks);
        if (quietMs >= _quietToWaitingMs)
        {
            SetState(WaitState.WaitingForInput);
            return;
        }

        // まだ判定がつかない: 再武装して沈黙しきい値に達するまで再評価を続ける
        // (静的バッファでも _quietToWaitingMs 到達で上の沈黙フォールバックが効く)。
        _timeoutTimer.Start();
    }

    /// <summary>
    /// 直近 N 行を新しい順に走査し、両端の空白 + フレーム文字を剥がしてから
    /// 各 wait パターンと照合する。box drawing UI では「最後の行」がヒント
    /// (`? for shortcuts`) でプロンプト行は最終行ではないことがあるため、
    /// _lastLine だけでなく直近全行を対象にする。
    ///   - 末尾型プロンプト (`C:\>` / `$ ` 等)            → 末尾一致でヒット
    ///   - 先頭型 TUI プロンプト (`>` 入力中含む)          → `^>` 等でヒット
    /// </summary>
    /// <summary>_linesLock を保持した状態で呼ぶこと (_recentLines を列挙するため)。</summary>
    private bool MatchesAnyPatternLocked()
    {
        foreach (var raw in _recentLines.Reverse())
        {
            var line = TrimSurroundingFrame(raw);
            foreach (var pattern in _patterns)
            {
                try
                {
                    if (pattern.IsMatch(line))
                        return true;
                }
                catch (RegexMatchTimeoutException)
                {
                    // ユーザー指定パターンの ReDoS 等で時間超過 → 不一致扱いで継続。
                }
            }
        }
        return false;
    }

    /// <summary>
    /// U+2500-U+257F の box drawing 全域 + ASCII 縦棒 `|` を「フレーム文字」と
    /// みなす。縦/横/コーナーいずれも対象 (Claude Code の新 UI は横罫線 `─` を
    /// 区切りに使うため、横罫線の前後を剥がす必要がある)。
    /// </summary>
    private static bool IsFrameChar(char c) =>
        (c >= '─' && c <= '╿') || c == '|';

    /// <summary>
    /// 行が全部「空白 + フレーム文字」だけならば true。プロンプトボックスの
    /// 上下辺 (`╭─...─╮` / `╰─...─╯` / `─...─` のみの行) を _recentLines から
    /// 除外するために使う。
    /// </summary>
    private static bool IsAllFrameChars(string s)
    {
        foreach (var c in s)
        {
            if (char.IsWhiteSpace(c)) continue;
            if (IsFrameChar(c)) continue;
            return false;
        }
        return s.Length > 0;
    }

    /// <summary>
    /// 両端の空白とフレーム文字を交互に剥がす。これにより以下を全て
    /// 「プロンプト本体相当」に正規化でき、wait パターンが効くようになる:
    ///   - `│ > Try "edit" │`           → `> Try "edit"`        (旧 TUI box)
    ///   - `─────...────>           `   → `>`                   (新 Claude Code 横罫線型)
    ///   - `C:\>`                       → `C:\>`                (cmd、両端非フレーム)
    /// </summary>
    private static string TrimSurroundingFrame(string s)
    {
        int start = 0;
        int end = s.Length;
        while (end > start)
        {
            char c = s[end - 1];
            if (char.IsWhiteSpace(c) || IsFrameChar(c)) { end--; continue; }
            break;
        }
        while (start < end)
        {
            char c = s[start];
            if (char.IsWhiteSpace(c) || IsFrameChar(c)) { start++; continue; }
            break;
        }
        return s[start..end];
    }

    private void SetState(WaitState newState)
    {
        if (_currentState == newState) return;
        _currentState = newState;
        StateChanged?.Invoke(newState);
    }

    // ユーザー指定パターンは .amm 由来で信頼度が低い。壊滅的バックトラッキング
    // (ReDoS) で検出スレッドが張り付くのを防ぐため matchTimeout を付ける。
    // default パターンは線形で安全なので付けない (InfiniteMatchTimeout)。
    private static Regex CompileUserPattern(string p) =>
        new(p, RegexOptions.Compiled, TimeSpan.FromMilliseconds(100));

    private static string StripAnsi(string text) => AnsiStripper.Strip(text);

    public void Dispose()
    {
        _timeoutTimer.Stop();
        _timeoutTimer.Dispose();
    }
}
