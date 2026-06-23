namespace Amm.Core;

/// <summary>
/// per-window で AMM 設定 (closeProhibited / collapseBlankLines / commentPrefixes) を
/// 編集するモーダルダイアログ (UDR-amm-20260427T0055-2c1)。
///
/// このダイアログは「現在のセッションへの反映」のみを担う。profiles.amm への
/// 書き戻しは [ファイル] → [上書き保存] で行う (重複機能を削減)。
/// </summary>
public sealed class AmmSettingsDialog : Form
{
    private readonly CheckBox _closeProhibited;
    private readonly CheckBox _collapseBlankLines;
    private readonly TextBox _commentPrefixes;

    public bool CloseProhibited => _closeProhibited.Checked;
    public bool CollapseBlankLines => _collapseBlankLines.Checked;
    public string[] CommentPrefixes => ParsePrefixes(_commentPrefixes.Text);

    public AmmSettingsDialog(string profileName, SessionProfile current)
    {
        Text = $"AMM 設定 — {profileName}";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        Font = new Font("Yu Gothic UI", 9F);

        // ボタン / ボトムストリップ高は OS のメニューフォント行高ベースで動的算出。
        // 固定 23 px だと高 DPI でボタン文字下端が切れ、ボトムストリップ高 40 px
        // だと strip 自体がフォーム下端で削れて見えなくなる。
        var buttonHeight = Math.Max(28, (SystemFonts.MenuFont?.Height ?? 16) + 12);
        var bottomStripHeight = buttonHeight + 16;
        ClientSize = new Size(420, 200 + bottomStripHeight);

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(12),
            ColumnCount = 1,
            RowCount = 5,
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        for (int i = 0; i < 4; i++) layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));

        _closeProhibited = new CheckBox
        {
            Text = "クローズ禁止 (× / Ctrl+W / システムメニュー閉じるを抑止)",
            AutoSize = true,
            Checked = current.CloseProhibited,
            Margin = new Padding(0, 0, 0, 8),
        };
        layout.Controls.Add(_closeProhibited);

        _collapseBlankLines = new CheckBox
        {
            Text = "マルチライン送信時、連続する空行を 1 行にまとめる",
            AutoSize = true,
            Checked = current.CollapseBlankLines,
            Margin = new Padding(0, 0, 0, 8),
        };
        layout.Controls.Add(_collapseBlankLines);

        var prefixesLabel = new Label
        {
            Text = "コメント記号 (CSV、行頭一致でスキップ。例: ',//  ※ # は Markdown 見出しと衝突)",
            AutoSize = true,
            Margin = new Padding(0, 4, 0, 2),
        };
        layout.Controls.Add(prefixesLabel);

        _commentPrefixes = new TextBox
        {
            Text = string.Join(",", current.CommentPrefixes),
            Width = 380,
            Margin = new Padding(0, 0, 0, 8),
        };
        layout.Controls.Add(_commentPrefixes);

        var hint = new Label
        {
            Text = "※ profiles.amm への書き戻しは [ファイル] → [上書き保存] で行います。",
            AutoSize = true,
            ForeColor = SystemColors.GrayText,
            Margin = new Padding(0, 4, 0, 0),
        };
        layout.Controls.Add(hint);

        var buttons = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.RightToLeft,
            Dock = DockStyle.Bottom,
            Height = bottomStripHeight,
            Padding = new Padding(8),
        };
        var cancel = new Button
        {
            Text = "キャンセル",
            DialogResult = DialogResult.Cancel,
            Width = 100,
            Height = buttonHeight,
            Margin = new Padding(4),
        };
        var ok = new Button
        {
            Text = "OK",
            DialogResult = DialogResult.OK,
            Width = 100,
            Height = buttonHeight,
            Margin = new Padding(4),
        };
        buttons.Controls.Add(cancel);
        buttons.Controls.Add(ok);

        Controls.Add(layout);
        Controls.Add(buttons);
        AcceptButton = ok;
        CancelButton = cancel;
    }

    private static string[] ParsePrefixes(string csv)
    {
        if (string.IsNullOrWhiteSpace(csv)) return [];
        return csv.Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(s => s.Trim())
            .Where(s => s.Length > 0)
            .ToArray();
    }
}
