namespace Amm.Core.Git;

/// <summary>
/// MDI ウィンドウのクローズ前に git の未コミット / 未プッシュを確認するガード。
/// 呼び出し元は戻り値が true ならクローズをキャンセルする。
/// </summary>
internal static class GitGuard
{
    /// <summary>
    /// dir がリポジトリ内の場合に未コミット / 未プッシュを確認する。
    /// ユーザがキャンセルを選んだ場合は true を返す。
    /// </summary>
    internal static bool CheckAndPrompt(IWin32Window owner, string dir)
    {
        if (string.IsNullOrWhiteSpace(dir)) return false;
        var root = GitHelper.GetRepoRoot(dir);
        if (root == null) return false;
        return CheckRoot(owner, root);
    }

    /// <summary>
    /// 複数ディレクトリをリポジトリ単位に集約して確認する (アプリ終了時用)。
    /// </summary>
    internal static bool CheckAndPromptDirs(IWin32Window owner, IEnumerable<string> dirs)
    {
        var roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var dir in dirs)
        {
            if (string.IsNullOrWhiteSpace(dir)) continue;
            var root = GitHelper.GetRepoRoot(dir);
            if (root != null) roots.Add(root);
        }
        foreach (var root in roots)
        {
            if (CheckRoot(owner, root)) return true;
        }
        return false;
    }

    // 戻り値: true = キャンセル (クローズ中止)
    private static bool CheckRoot(IWin32Window owner, string root)
    {
        // ---- 未コミット確認 ----
        var status = GitHelper.GetShortStatus(root);
        if (!string.IsNullOrEmpty(status))
        {
            using var dlg = new GitCommitDialog(root, status);
            var result = dlg.ShowDialog(owner);
            if (result == DialogResult.Cancel) return true;
            if (result == DialogResult.OK)
            {
                var (ok, err) = GitHelper.AddAllAndCommit(root, dlg.CommitMessage);
                if (!ok)
                    MessageBox.Show(owner,
                        $"git commit に失敗しました:\n{err}",
                        "コミットエラー", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        // ---- 未プッシュ確認 ----
        if (!GitHelper.HasRemote(root)) return false;
        var unpushed = GitHelper.GetUnpushedCount(root);
        if (unpushed <= 0) return false;

        var pushResult = MessageBox.Show(
            owner,
            $"{unpushed} 件のコミットが未プッシュです。今すぐプッシュしますか?\n\n{root}",
            $"未プッシュのコミット — {Path.GetFileName(root)}",
            MessageBoxButtons.YesNoCancel,
            MessageBoxIcon.Question,
            MessageBoxDefaultButton.Button1);

        if (pushResult == DialogResult.Cancel) return true;
        if (pushResult == DialogResult.Yes)
        {
            var (ok, output) = GitHelper.Push(root);
            if (!ok)
                MessageBox.Show(owner,
                    $"git push に失敗しました:\n{output}",
                    "プッシュエラー", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        return false;
    }
}
