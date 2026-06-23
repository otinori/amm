namespace Amm.Core.Git;

/// <summary>
/// MDI ウィンドウのクローズ前に git commit を促すダイアログ。
/// OK = コミット実行、Ignore = スキップ、Cancel = クローズ中止。
/// </summary>
internal sealed class GitCommitDialog : Form
{
    private readonly TextBox _messageBox;

    internal string CommitMessage => _messageBox.Text.Trim();

    internal GitCommitDialog(string repoRoot, string shortStatus)
    {
        Text = $"変更をコミット — {Path.GetFileName(repoRoot)}";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition   = FormStartPosition.CenterParent;
        MaximizeBox     = false;
        MinimizeBox     = false;
        ShowInTaskbar   = false;
        Font = new Font("Yu Gothic UI", 9F);

        var btnH = Math.Max(28, (SystemFonts.MenuFont?.Height ?? 16) + 12);
        ClientSize = new Size(500, 280 + btnH + 16);

        const int pad = 12;
        int y = pad;

        var infoLabel = new Label
        {
            Text      = $"このウィンドウを閉じる前に、Git リポジトリの変更をコミットしますか?\n{repoRoot}",
            Location  = new Point(pad, y),
            Size      = new Size(ClientSize.Width - pad * 2, 40),
            AutoSize  = false,
        };
        y += 46;

        var statusBox = new TextBox
        {
            Location   = new Point(pad, y),
            Size       = new Size(ClientSize.Width - pad * 2, 130),
            ReadOnly   = true,
            Multiline  = true,
            ScrollBars = ScrollBars.Vertical,
            Text       = shortStatus,
            Font       = new Font("Consolas", 8.5F),
            BackColor  = SystemColors.ControlLight,
        };
        y += 138;

        var msgLabel = new Label
        {
            Text     = "コミットメッセージ:",
            Location = new Point(pad, y),
            AutoSize = true,
        };
        y += 20;

        _messageBox = new TextBox
        {
            Location = new Point(pad, y),
            Size     = new Size(ClientSize.Width - pad * 2, 24),
            Text     = "WIP: 作業を保存",
        };

        int bottomY = ClientSize.Height - btnH - pad;
        var commit = new Button
        {
            Text         = "コミット (&C)",
            DialogResult = DialogResult.OK,
            Location     = new Point(pad, bottomY),
            Size         = new Size(110, btnH),
        };
        var skip = new Button
        {
            Text         = "スキップ (&S)",
            DialogResult = DialogResult.Ignore,
            Location     = new Point(pad + 118, bottomY),
            Size         = new Size(110, btnH),
        };
        var cancel = new Button
        {
            Text         = "閉じない (&X)",
            DialogResult = DialogResult.Cancel,
            Location     = new Point(ClientSize.Width - pad - 110, bottomY),
            Size         = new Size(110, btnH),
        };

        commit.Click += (_, _) =>
        {
            if (string.IsNullOrWhiteSpace(_messageBox.Text))
            {
                MessageBox.Show(this, "コミットメッセージを入力してください。",
                    Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                DialogResult = DialogResult.None;
            }
        };

        Controls.AddRange([infoLabel, statusBox, msgLabel, _messageBox, commit, skip, cancel]);
        AcceptButton = commit;
        CancelButton = cancel;

        Shown += (_, _) => { _messageBox.Focus(); _messageBox.SelectAll(); };
    }
}
