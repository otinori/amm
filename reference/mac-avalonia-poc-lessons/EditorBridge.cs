using System.Diagnostics;

namespace Amm.Desktop.Core;

// amm patch: ported from the private EditorBridge nested class in
// src/apps/Amm/Forms/MdiParentForm.cs (Windows) for the prompt-assist round
// of Mac/Linux feature parity (openspec/changes/mac-desktop-feature-parity).
// Core logic (temp file, FileSystemWatcher, 500ms debounce, hash-based
// dedup, dispose-on-target-closed) is unchanged. Scoped down:
//   - "_editorMode"/"_customEditorPath" (Custom/Associated selection) now
//     exist as of app-shell's AppSettingsDialog/LayoutState fields - consulted
//     fresh via LayoutState.Load() at each launch (not cached/injected) so a
//     settings change takes effect on the pane's very next "編集" click
//     without needing new plumbing threaded through PaneHost/TerminalWorkspace.
//   - No per-command comment-line filtering (ApplyPerCommandFilter) - that
//     depends on Phase 2 DesktopProfile fields (CommentPrefixes) not
//     implemented yet. Sends the file's raw content as-is.
//   - No "post-send action" (Maximize/None/Focus) - Amm.Desktop panes don't
//     maximize individually. Always focuses the target pane after sending,
//     the closest equivalent to Windows' default "Focus" behavior.
//   - Takes send/focus as delegates rather than a PaneHost reference, so
//     this class doesn't need to depend on the Panes/Avalonia layer (same
//     rationale as McpToolHost's delegate-bundle design).
public sealed class EditorBridge : IDisposable
{
    private readonly string _filePath;
    private readonly FileSystemWatcher _watcher;
    private readonly System.Timers.Timer _debounce;
    private readonly Func<string, Task> _sendAsync;
    private readonly Action _onSent;
    private string _lastSentHash;
    private bool _disposed;

    public string FilePath => _filePath;
    public bool IsActive => !_disposed && File.Exists(_filePath);

    // amm patch: initialSeedHash pre-seeds _lastSentHash with the hash of
    // whatever body CreateAndLaunch just wrote to the file (placeholder
    // comment or prefilled content), rather than starting at "" (never
    // matches anything). Found via real-device testing: some editors touch
    // the file's mtime/size merely by opening it (confirmed with Zed on
    // macOS), which fires FileSystemWatcher.Changed before the user has typed
    // anything - without this seed, that "phantom" change was indistinguishable
    // from a real edit and the placeholder comment text itself got sent to
    // the pane as if it were a prompt (visible on real hardware as
    // `zsh: event not found` / parse errors from the comment's "<!--"/"-->"
    // syntax hitting the shell).
    private EditorBridge(string filePath, Func<string, Task> sendAsync, Action onSent, string initialSeedHash)
    {
        _filePath = filePath;
        _sendAsync = sendAsync;
        _onSent = onSent;
        _lastSentHash = initialSeedHash;

        _watcher = new FileSystemWatcher(
            Path.GetDirectoryName(filePath)!,
            Path.GetFileName(filePath))
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size,
            EnableRaisingEvents = true,
        };
        _watcher.Changed += OnWatcherChanged;

        _debounce = new System.Timers.Timer(500) { AutoReset = false };
        _debounce.Elapsed += OnDebounceElapsed;
    }

    /// <summary>
    /// 一時Markdownファイルを作成しエディタで開く。paneLabel はファイル名に
    /// 埋め込まれる (どのペイン向けの下書きかを判別するため)。
    /// </summary>
    public static EditorBridge CreateAndLaunch(string paneLabel, Func<string, Task> sendAsync, Action onSent, string? initialContent = null)
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "amm", "editor");
        Directory.CreateDirectory(dir);

        var safeName = Sanitize(paneLabel);
        var shortId = Guid.NewGuid().ToString("N")[..6];
        var fileName = $"prompt-{safeName}-{shortId}.md";
        var filePath = Path.Combine(dir, fileName);

        // 初期内容を渡されたとき (共有入力ボックスの既存テキストからの起動) は
        // ヘッダなしで本文だけ書く。空のときは使い方コメントを置く
        // (Windows版と同じ「初回プリフィル」方針)。
        var body = !string.IsNullOrEmpty(initialContent)
            ? initialContent
            : $"<!-- amm エディタ連携: 保存するたびに [{paneLabel}] へ送信されます。\n" +
              "     このペインを閉じるかシェルが終了すると自動削除されます。 -->\n";
        File.WriteAllText(filePath, body);

        var bridge = new EditorBridge(filePath, sendAsync, onSent, ComputeHash(body));
        try
        {
            LaunchEditor(filePath);
        }
        catch
        {
            bridge.Dispose();
            throw;
        }
        return bridge;
    }

    /// <summary>同じ一時ファイルへ再度エディタを起動する (「編集」ボタンの
    /// 再クリック用、内容は触らない)。</summary>
    public void RelaunchEditor()
    {
        if (_disposed) return;
        LaunchEditor(_filePath);
    }

    private static void LaunchEditor(string filePath)
    {
        var settings = LayoutState.Load();
        if (settings.EditorMode == "Custom" && !string.IsNullOrWhiteSpace(settings.CustomEditorPath))
        {
            Process.Start(new ProcessStartInfo(settings.CustomEditorPath, [filePath])
            {
                UseShellExecute = false,
            });
            return;
        }
        Process.Start(new ProcessStartInfo(filePath) { UseShellExecute = true });
    }

    private void OnWatcherChanged(object sender, FileSystemEventArgs e)
    {
        if (_disposed) return;
        // FileSystemWatcher のワーカースレッドから呼ばれ、System.Timers.Timer
        // の Elapsed も .NET TP Worker (スレッドプール) 上で発火する - UIスレッド
        // ではない。OnDebounceElapsed 内で _onSent() 経由の呼び出し
        // (PaneHost.FocusTerminal 等) がAvaloniaのUIコントロールに触れるため、
        // _sendAsync/_onSent は呼び出し元 (PaneHost.OnEditClick) が
        // Dispatcher.UIThread 経由でUIスレッドへホップするデリゲートを渡す
        // 契約になっている。実機でこのマーシャリングを怠ったところ
        // 「.NET TP Worker」スレッドでの未捕捉例外によりアプリ全体が
        // クラッシュ(SIGABRT)することを確認済み。
        _debounce.Stop();
        _debounce.Start();
    }

    private async void OnDebounceElapsed(object? sender, System.Timers.ElapsedEventArgs e)
    {
        if (_disposed) return;

        string text;
        try
        {
            using var fs = new FileStream(_filePath, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            using var sr = new StreamReader(fs);
            text = await sr.ReadToEndAsync().ConfigureAwait(false);
        }
        catch
        {
            // 保存直後でエディタがまだファイルを握っている。もう一度待つ。
            if (!_disposed) _debounce.Start();
            return;
        }

        if (string.IsNullOrEmpty(text)) return;

        // 空保存や重複保存の無限送信を防ぐ (Windows版と同じハッシュ方式)。
        // _lastSentHash はプレースホルダー本文のハッシュで初期化済みなので、
        // エディタがファイルを開いただけ (ユーザーがまだ何も編集していない)
        // で発火したイベントもここで弾かれる。
        var hash = ComputeHash(text);
        if (hash == _lastSentHash) return;
        _lastSentHash = hash;

        // amm patch: shell/pty treats \r as Enter, not \n - same conversion
        // as the shared input box broadcast / send_message.
        var ptyText = text.Replace("\r\n", "\n").Replace("\n", "\r");
        if (!ptyText.EndsWith('\r')) ptyText += "\r";

        try
        {
            await _sendAsync(ptyText).ConfigureAwait(false);
            if (!_disposed) _onSent();
        }
        catch
        {
            // amm patch: this is an async void Timer.Elapsed handler running
            // on a background thread pool thread - any exception that
            // escapes here (including one thrown by a caller-supplied
            // delegate that forgot to marshal onto the UI thread) is
            // unhandled and takes down the entire process (confirmed on real
            // hardware: SIGABRT via .NET's unhandled-exception-on-threadpool-
            // thread policy). Swallow rather than let anything propagate.
        }
    }

    private static string Sanitize(string s) =>
        string.Concat(s.Select(ch => Path.GetInvalidFileNameChars().Contains(ch) ? '_' : ch));

    private static string ComputeHash(string text) => $"{text.Length}:{text.GetHashCode()}";

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _watcher.EnableRaisingEvents = false; } catch { }
        _watcher.Changed -= OnWatcherChanged;
        try { _watcher.Dispose(); } catch { }
        try { _debounce.Dispose(); } catch { }
        try { if (File.Exists(_filePath)) File.Delete(_filePath); } catch { }
    }
}
