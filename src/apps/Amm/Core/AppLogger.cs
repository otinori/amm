using System.Collections.Concurrent;
using System.Text;

namespace Amm.Core;

/// <summary>
/// 診断用アプリログ。%LOCALAPPDATA%\amm\log\app.log に追記し、
/// 1MB 超過で app.log.1 にローテーション (1 世代保持)。
///
/// 書き込みは専用バックグラウンドスレッド + 有界キューで行う。これにより:
///  - 呼び出し元 (特に UI スレッド) が同期ファイル I/O でブロックしない。
///    UDR-amm-20260612T0132-b4e の OOM 真因 (UI スレッド同期書込の滞留 →
///    WebView2 メッセージキュー膨張) を、JS 側レート制限に頼らず構造的に解消する。
///  - キュー満杯時は行を捨て (有界なのでメモリは膨張しない)、捨てた件数を後で 1 行残す。
///  - ログディレクトリは current user 限定 ACL で保護 (app.log には IME 診断・approval・
///    OSC9・例外スタック等、機微情報が混入し得るため sessionLog と同等の保護を与える)。
/// ファイル I/O 例外は握り潰して呼び出し元に波及させない。
/// </summary>
public static class AppLogger
{
    private const long MaxBytes = 1024 * 1024;
    private const int QueueCapacity = 10000; // 有界。満杯時は drop してメモリ膨張を防ぐ。

    private static readonly BlockingCollection<string> _queue =
        new(new ConcurrentQueue<string>(), QueueCapacity);
    private static long _dropped;
    private static bool _aclApplied;

    private static string LogDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "amm", "log");

    private static string LogPath => Path.Combine(LogDir, "app.log");
    private static string LogPathOld => Path.Combine(LogDir, "app.log.1");

    static AppLogger()
    {
        var worker = new Thread(WorkerLoop)
        {
            IsBackground = true,
            Name = "AppLogger",
        };
        worker.Start();
    }

    public static void Info(string message) => Enqueue("INFO", message);
    public static void Warn(string message) => Enqueue("WARN", message);
    public static void Error(string message, Exception? ex = null)
        => Enqueue("ERROR", ex == null ? message : $"{message}\n{ex}");

    private static void Enqueue(string level, string message)
    {
        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level,-5}] {message}";
        // 非ブロッキング。満杯なら捨てて件数だけ数える (呼び出し元を絶対に待たせない)。
        if (!_queue.TryAdd(line))
            Interlocked.Increment(ref _dropped);
    }

    private static void WorkerLoop()
    {
        foreach (var first in _queue.GetConsumingEnumerable())
        {
            try
            {
                Directory.CreateDirectory(LogDir);
                EnsureDirectoryAcl();
                RotateIfNeeded();

                // 開閉コストを抑えるため、溜まっている分をまとめて 1 回で書く。
                var sb = new StringBuilder();
                sb.Append(first).Append(Environment.NewLine);
                while (sb.Length < 64 * 1024 && _queue.TryTake(out var more))
                    sb.Append(more).Append(Environment.NewLine);

                var dropped = Interlocked.Exchange(ref _dropped, 0);
                if (dropped > 0)
                    sb.Append($"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [WARN ] AppLogger dropped {dropped} log line(s) (queue full)")
                      .Append(Environment.NewLine);

                File.AppendAllText(LogPath, sb.ToString(), Encoding.UTF8);
            }
            catch
            {
                // ログ書込み失敗はアプリ本体を巻き込まない
            }
        }
    }

    private static void RotateIfNeeded()
    {
        try
        {
            var fi = new FileInfo(LogPath);
            if (!fi.Exists || fi.Length <= MaxBytes) return;
            if (File.Exists(LogPathOld)) File.Delete(LogPathOld);
            File.Move(LogPath, LogPathOld);
        }
        catch { }
    }

    /// <summary>
    /// ログディレクトリの ACL を current user の FullControl のみへ絞る (継承遮断)。
    /// 1 回だけ適用 (best-effort)。Windows 専用。
    /// </summary>
    private static void EnsureDirectoryAcl()
    {
        if (_aclApplied) return;
        _aclApplied = true; // 失敗してもリトライしない (毎回 I/O を増やさない)
        if (!OperatingSystem.IsWindows()) return;
        try
        {
            using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
            var user = identity.User;
            if (user == null) return;

            var di = new DirectoryInfo(LogDir);
            var sec = new System.Security.AccessControl.DirectorySecurity();
            sec.SetOwner(user);
            // 継承を無効化し既存の継承 ACE を捨てる。
            sec.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            sec.AddAccessRule(new System.Security.AccessControl.FileSystemAccessRule(
                user,
                System.Security.AccessControl.FileSystemRights.FullControl,
                System.Security.AccessControl.InheritanceFlags.ContainerInherit
                    | System.Security.AccessControl.InheritanceFlags.ObjectInherit,
                System.Security.AccessControl.PropagationFlags.None,
                System.Security.AccessControl.AccessControlType.Allow));
            System.IO.FileSystemAclExtensions.SetAccessControl(di, sec);
        }
        catch { /* ACL 設定失敗はログ機能本体に波及させない */ }
    }
}
