using System.Diagnostics;

namespace Amm.Core.Git;

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

    private static (int exit, string stdout, string stderr) Run(
        string workDir, int timeoutMs, params string[] args)
    {
        if (!Directory.Exists(workDir)) return (-1, "", "");
        try
        {
            var psi = new ProcessStartInfo("git")
            {
                WorkingDirectory        = workDir,
                RedirectStandardOutput  = true,
                RedirectStandardError   = true,
                StandardOutputEncoding  = System.Text.Encoding.UTF8,
                StandardErrorEncoding   = System.Text.Encoding.UTF8,
                UseShellExecute         = false,
                CreateNoWindow          = true,
            };
            foreach (var a in args) psi.ArgumentList.Add(a);
            using var proc = Process.Start(psi) ?? throw new InvalidOperationException("git not found");
            // stderr と stdout を別スレッドで読んでデッドロックを防ぐ。
            var stderrTask = Task.Run(() => proc.StandardError.ReadToEnd());
            var stdout     = proc.StandardOutput.ReadToEnd();
            proc.WaitForExit(timeoutMs);
            return (proc.ExitCode, stdout, stderrTask.GetAwaiter().GetResult());
        }
        catch
        {
            return (-1, "", "");
        }
    }
}
