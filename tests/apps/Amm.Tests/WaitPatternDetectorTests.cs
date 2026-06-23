using Amm.Core;

namespace Amm.Tests;

public class WaitPatternDetectorTests
{
    [Fact]
    public void Feed_MovesToRunning()
    {
        var d = new WaitPatternDetector();
        d.Feed("some output\n");
        Assert.Equal(WaitState.Running, d.CurrentState);
    }

    [Fact]
    public void NotifyProcessExited_MovesToStopped()
    {
        var d = new WaitPatternDetector();
        d.Feed("running\n");
        d.NotifyProcessExited();
        Assert.Equal(WaitState.Stopped, d.CurrentState);
    }

    [Fact]
    public async Task Feed_CmdPrompt_EventuallyTransitionsToWaitingForInput()
    {
        var d = new WaitPatternDetector(["[>]\\s*$"]);
        var tcs = new TaskCompletionSource<WaitState>();
        d.StateChanged += s =>
        {
            if (s == WaitState.WaitingForInput) tcs.TrySetResult(s);
        };

        d.Feed("C:\\>");

        var result = await Task.WhenAny(tcs.Task, Task.Delay(2000));
        Assert.Same(tcs.Task, result);
        Assert.Equal(WaitState.WaitingForInput, d.CurrentState);
    }

    [Fact]
    public async Task Feed_NoMatchingPattern_StaysInRunning()
    {
        // 出力のバースト間の無音期間 (500ms+) で Running → Unknown に落ちて
        // UI がフラップすることを防ぐため、パターン不一致時は Running を維持する
        var d = new WaitPatternDetector(["\\$ $"]);
        d.Feed("some mid-line text with no prompt");

        // タイマー (500ms) の 2 倍以上待って再評価が確実に走る状況に。
        // 既定の沈黙しきい値 (4000ms) 未満なので、この時点では Running を維持する。
        await Task.Delay(1200);

        Assert.Equal(WaitState.Running, d.CurrentState);
    }

    [Fact]
    public async Task Feed_NoMatch_AfterProlongedSilence_FallsBackToWaitingForInput()
    {
        // 「処理が終わっているのに処理中アイコンのまま、MDI をリサイズすると直る」
        // 事象の回帰テスト。完了直後の末尾再描画でプロンプト行が照合窓から押し出され
        // Running に固着するケースでも、出力が quietToWaitingMs 以上途絶えたら
        // WaitingForInput へ自力回復することを確認する (従来は新規出力が来るまで固着)。
        var d = new WaitPatternDetector(["\\$ $"], quietToWaitingMs: 200);
        var tcs = new TaskCompletionSource<WaitState>();
        d.StateChanged += s =>
        {
            if (s == WaitState.WaitingForInput) tcs.TrySetResult(s);
        };

        d.Feed("output with no parseable prompt");  // → Running

        var result = await Task.WhenAny(tcs.Task, Task.Delay(3000));
        Assert.Same(tcs.Task, result);
        Assert.Equal(WaitState.WaitingForInput, d.CurrentState);
    }

    [Fact]
    public void Feed_StripsAnsiBeforeMatch()
    {
        var d = new WaitPatternDetector(["PS\\s+\\S+>\\s*$"]);
        // ANSI エスケープ付きの PowerShell プロンプトを与えて、StripAnsi 後に一致することを確認
        d.Feed("[32mPS C:\\>[0m ");
        // Feed は即時にパターン照合するので、ANSI 剥がしが効いていれば Feed 直後に WaitingForInput。
        // 効かなければ ANSI コードが残った文字列には PS\s+\S+>\s*$ がヒットせず Running 維持。
        Assert.Equal(WaitState.WaitingForInput, d.CurrentState);
    }

    [Fact]
    public async Task Feed_ClaudeCodeBoxPrompt_TransitionsToWaitingForInput()
    {
        // Claude Code / Codex 風の TUI box プロンプト。両端の `│` を剥がした
        // あとに `^>(?:\s|$)` で入力待ちと判定されることを確認 (デフォルトパターン)。
        var d = new WaitPatternDetector();
        var tcs = new TaskCompletionSource<WaitState>();
        d.StateChanged += s =>
        {
            if (s == WaitState.WaitingForInput) tcs.TrySetResult(s);
        };

        d.Feed("╭─────────────────────╮\n│ > Try \"edit foo.py\" │\n╰─────────────────────╯\n");

        var result = await Task.WhenAny(tcs.Task, Task.Delay(2000));
        Assert.Same(tcs.Task, result);
        Assert.Equal(WaitState.WaitingForInput, d.CurrentState);
    }

    [Fact]
    public async Task Feed_HorizontalRulePrompt_TransitionsToWaitingForInput()
    {
        // Claude Code の新 UI: 横罫線 (─) で囲んだ帯の右端に `>` を置く。
        //   ─────────────…─────────────>             (← この行)
        //   ─────────────…─────────────             (← フレームのみで除外)
        //     ? for shortcuts                       (← ヒント行)
        // 直近 N 行のうち先頭行が両端トリム後に `>` 単体になり、
        // ^>(?:\s|$) でマッチすることを確認。
        var d = new WaitPatternDetector();
        var tcs = new TaskCompletionSource<WaitState>();
        d.StateChanged += s =>
        {
            if (s == WaitState.WaitingForInput) tcs.TrySetResult(s);
        };

        var dashes = new string('─', 100);
        d.Feed(
            $"{dashes}>             \n" +
            $"{dashes}\n" +
            "  ? for shortcuts                ◉ xhigh · /effort\n");

        var result = await Task.WhenAny(tcs.Task, Task.Delay(2000));
        Assert.Same(tcs.Task, result);
        Assert.Equal(WaitState.WaitingForInput, d.CurrentState);
    }

    [Fact]
    public async Task Feed_BoxPrompt_FollowedByHintLine_StillMatches()
    {
        // Claude Code 実機の典型: プロンプト box の下にヒント行 `? for shortcuts`
        // が描画され、最終行はプロンプトではない。直近 N 行を走査して取りこぼし
        // を防げているか確認する。
        var d = new WaitPatternDetector();
        var tcs = new TaskCompletionSource<WaitState>();
        d.StateChanged += s =>
        {
            if (s == WaitState.WaitingForInput) tcs.TrySetResult(s);
        };

        d.Feed(
            "✻ Welcome to Claude Code!\n" +
            "\n" +
            "╭─────────────────────╮\n" +
            "│ > Try \"edit foo.py\" │\n" +
            "╰─────────────────────╯\n" +
            "  ? for shortcuts\n");

        var result = await Task.WhenAny(tcs.Task, Task.Delay(2000));
        Assert.Same(tcs.Task, result);
        Assert.Equal(WaitState.WaitingForInput, d.CurrentState);
    }

    [Fact]
    public void Feed_HorizontalRulePrompt_WithTypedChars_StaysWaitingForInput_NoFlicker()
    {
        // ユーザがプロンプトに 1 文字入力するたびに redraw され、行が
        // `─...─>             ` → `─...─>g            ` に変化する。
        // template の `^>` パターンと Feed 時の即時マッチで、Running への
        // 一瞬の遷移なしに WaitingForInput を維持できることを確認。
        var d = new WaitPatternDetector(["^>"]);
        var states = new List<WaitState>();
        d.StateChanged += s => states.Add(s);

        var dashes = new string('─', 100);
        d.Feed($"{dashes}>             \n{dashes}\n  ? for shortcuts\n");
        d.Feed($"{dashes}>g            \n{dashes}\n  ? for shortcuts\n");
        d.Feed($"{dashes}>git           \n{dashes}\n  ? for shortcuts\n");

        Assert.Equal(WaitState.WaitingForInput, d.CurrentState);
        // 一度も Running に落ちていないこと (Unknown→WaitingForInput の 1 回のみ)
        Assert.Equal(new[] { WaitState.WaitingForInput }, states);
    }

    [Fact]
    public async Task Feed_BoxPrompt_UserSuppliedFrontPattern_Matches()
    {
        // ユーザー定義 WaitPatterns に `^>(?:\s|$)` を入れた場合の挙動を保証
        // (新テンプレート Claude Code / Codex 等が使用)。
        var d = new WaitPatternDetector(["^>(?:\\s|$)"]);
        var tcs = new TaskCompletionSource<WaitState>();
        d.StateChanged += s =>
        {
            if (s == WaitState.WaitingForInput) tcs.TrySetResult(s);
        };

        d.Feed("│ > my-input          │\n");

        var result = await Task.WhenAny(tcs.Task, Task.Delay(2000));
        Assert.Same(tcs.Task, result);
        Assert.Equal(WaitState.WaitingForInput, d.CurrentState);
    }

    [Fact]
    public async Task Feed_BoxPrompt_WithLegacyTrailingOnlyUserPattern_StillMatchesViaDefaults()
    {
        // 旧 profiles.json の `>\s*$` (末尾型) しか定義されていない profile でも、
        // defaults と合成されて行頭型 box プロンプト (Codex / Copilot CLI 想定)
        // を取りこぼさないことを確認する回帰テスト。
        var d = new WaitPatternDetector([">\\s*$"]);
        var tcs = new TaskCompletionSource<WaitState>();
        d.StateChanged += s =>
        {
            if (s == WaitState.WaitingForInput) tcs.TrySetResult(s);
        };

        d.Feed("│ > Try \"fix the bug\"          │\n");

        var result = await Task.WhenAny(tcs.Task, Task.Delay(2000));
        Assert.Same(tcs.Task, result);
        Assert.Equal(WaitState.WaitingForInput, d.CurrentState);
    }

    [Fact]
    public async Task Feed_BoxPrompt_WithModernArrowChar_MatchesViaDefaults()
    {
        // Inquirer.js / 新しい Copilot CLI が使う `❯` プロンプト記号でも
        // 入力待ちと判定できることを確認する。defaults に新パターンを追加した。
        var d = new WaitPatternDetector();
        var tcs = new TaskCompletionSource<WaitState>();
        d.StateChanged += s =>
        {
            if (s == WaitState.WaitingForInput) tcs.TrySetResult(s);
        };

        d.Feed("│ ❯ Type your message      │\n");

        var result = await Task.WhenAny(tcs.Task, Task.Delay(2000));
        Assert.Same(tcs.Task, result);
        Assert.Equal(WaitState.WaitingForInput, d.CurrentState);
    }

    [Fact]
    public void Dispose_DoesNotThrow()
    {
        var d = new WaitPatternDetector();
        d.Feed("x");
        d.Dispose();
    }
}
