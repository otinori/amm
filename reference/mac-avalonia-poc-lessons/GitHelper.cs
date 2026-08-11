using System.Diagnostics;
using System.Linq;
using System.Text;

namespace Amm.Desktop.Core.Git;

// amm patch: ported verbatim from src/apps/Amm/Core/Git/GitHelper.cs (Windows).
// Fully platform-independent (shells out to `git`, no WinForms dependency) - see
// Core/AnsiStripper.cs for the general rationale on why this is a copy rather
// than a shared library reference. Run() itself was NOT ported verbatim - see
// its own comment for why.
internal static class GitHelper
{
    /// <summary>
    /// dir が git リポジトリ内なら repo ルートを返す。リポジトリ外なら null。
    /// </summary>
    internal static string? GetRepoRoot(string dir)
    {
        if (!Directory.Exists(dir)) return null;
        var (exit, stdout, _) = Run(dir, 3_000, "rev-parse", "--show-toplevel");
        if (exit != 0 || string.IsNullOrWhiteSpace(stdout)) return null;
        return Path.GetFullPath(stdout.Trim());
    }

    /// <summary>git status --short の出力。クリーンなら空文字。</summary>
    internal static string GetShortStatus(string repoRoot)
    {
        var (exit, stdout, _) = Run(repoRoot, 5_000, "-c", "core.quotepath=false", "status", "--short");
        return exit == 0 ? stdout.Trim() : "";
    }

    /// <summary>未プッシュのコミット数。upstream 未設定 / エラー時は 0。</summary>
    internal static int GetUnpushedCount(string repoRoot)
    {
        var (exit, stdout, _) = Run(repoRoot, 5_000, "log", "@{u}..HEAD", "--oneline");
        if (exit != 0 || string.IsNullOrWhiteSpace(stdout)) return 0;
        return stdout.Trim().Split('\n', StringSplitOptions.RemoveEmptyEntries).Length;
    }

    /// <summary>リモートが 1 件以上設定されているか。</summary>
    internal static bool HasRemote(string repoRoot)
    {
        var (exit, stdout, _) = Run(repoRoot, 3_000, "remote");
        return exit == 0 && !string.IsNullOrWhiteSpace(stdout);
    }

    /// <summary>git add -A → git commit -m message。</summary>
    internal static (bool success, string error) AddAllAndCommit(string repoRoot, string message)
    {
        var (addExit, _, addErr) = Run(repoRoot, 5_000, "add", "-A");
        if (addExit != 0) return (false, addErr.Trim());
        var (commitExit, _, commitErr) = Run(repoRoot, 10_000, "commit", "-m", message);
        return (commitExit == 0, commitErr.Trim());
    }

    /// <summary>git push。</summary>
    internal static (bool success, string output) Push(string repoRoot)
    {
        var (exit, stdout, stderr) = Run(repoRoot, 30_000, "push");
        return (exit == 0, (stdout + "\n" + stderr).Trim());
    }

    // amm patch: this method went through two failed fix attempts before landing
    // here - recorded in detail because both looked like they should obviously
    // work and didn't, and the same trap is easy to fall into again.
    //
    // Attempt 1 (verbatim Windows port): read stdout synchronously on the
    // calling thread while reading stderr concurrently via
    // Task.Run(() => proc.StandardError.ReadToEnd()) - the standard "avoid the
    // classic Process stdio deadlock" trick. On this Mac (macOS 26.5.1 ARM64,
    // .NET 9), that concurrent dual-stream read reliably crashed the whole app
    // with System.AccessViolationException inside
    // System.Net.Sockets.Socket.LoadSocketTypeFromHandle, called from
    // SafePipeHandle.CreatePipeSocket during PipeStream.ReadCore.
    //
    // Attempt 2: switched to Process's own event-driven async I/O
    // (OutputDataReceived/ErrorDataReceived + Begin*ReadLine) - the pattern
    // .NET's own docs recommend specifically to avoid concurrent-read
    // deadlocks, and it never touches both streams from two threads at once.
    // Still crashed, same AccessViolationException, just at a different call
    // site (System.Net.Sockets.SocketAsyncEventArgs.FinishOperationSyncFailure,
    // in the async I/O completion callback instead of the sync read path).
    //
    // Conclusion: on this specific macOS/.NET combination, .NET's Unix
    // PipeStream implementation (which always lazily wraps the pipe handle in
    // a Socket, whether read synchronously or asynchronously) appears to be
    // unreliable for *any* redirected Process stdio, not just the concurrent
    // dual-stream case - and AccessViolationException cannot be caught by an
    // ordinary try/catch, so no amount of exception handling here could have
    // masked it. Confirmed both attempts crash within 1-2 pane-close clicks
    // via a minimal isolated repro (any Process.Start with redirected stdio);
    // a git-in-a-console-only harness without Avalonia's GUI thread running
    // alongside did not reproduce it in 500 iterations, so whatever triggers
    // this needs the fuller thread/socket landscape the real app has.
    //
    // Fix: don't redirect stdio through .NET Process pipes at all. Run git
    // through `/bin/sh -c "git ... > tmpOut 2> tmpErr"` with
    // RedirectStandardOutput/Error left false (child inherits real file
    // descriptors pointed at temp files, no PipeStream/Socket involved), then
    // read the temp files back with plain File.ReadAllText after the process
    // exits.
    private static (int exit, string stdout, string stderr) Run(
        string workDir, int timeoutMs, params string[] args)
    {
        if (!Directory.Exists(workDir)) return (-1, "", "");
        string? stdoutFile = null;
        string? stderrFile = null;
        try
        {
            stdoutFile = Path.GetTempFileName();
            stderrFile = Path.GetTempFileName();

            var shellCommand = "git " + string.Join(' ', args.Select(ShellQuote))
                + " > " + ShellQuote(stdoutFile) + " 2> " + ShellQuote(stderrFile);

            var psi = new ProcessStartInfo("/bin/sh")
            {
                WorkingDirectory = workDir,
                UseShellExecute  = false,
                CreateNoWindow   = true,
            };
            psi.ArgumentList.Add("-c");
            psi.ArgumentList.Add(shellCommand);

            using var proc = Process.Start(psi) ?? throw new InvalidOperationException("/bin/sh not found");
            if (!proc.WaitForExit(timeoutMs))
            {
                // amm patch: a hung command (e.g. `git push` blocked on a
                // credential prompt) was previously left running forever -
                // the timeout only stopped *this* method from waiting, not
                // the orphaned /bin/sh + git process tree itself. Kill it
                // before giving up (reading ExitCode on a still-running
                // process would also throw InvalidOperationException here).
                try { proc.Kill(entireProcessTree: true); } catch { /* best-effort */ }
                return (-1, "", "");
            }

            var stdout = File.Exists(stdoutFile) ? File.ReadAllText(stdoutFile, Encoding.UTF8) : "";
            var stderr = File.Exists(stderrFile) ? File.ReadAllText(stderrFile, Encoding.UTF8) : "";
            return (proc.ExitCode, stdout, stderr);
        }
        catch
        {
            return (-1, "", "");
        }
        finally
        {
            if (stdoutFile != null) TryDelete(stdoutFile);
            if (stderrFile != null) TryDelete(stderrFile);
        }
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch { /* best-effort cleanup */ }
    }

    private static string ShellQuote(string s) => "'" + s.Replace("'", "'\\''") + "'";
}
