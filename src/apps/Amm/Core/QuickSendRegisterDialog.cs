namespace Amm.Core;

/// <summary>
/// 右クリックメニュー「クイック送信に登録...」から呼ばれる登録ダイアログ。
/// ラベル (メニュー表示名) と送信テキストを入力させる。
/// </summary>
internal sealed class QuickSendRegisterDialog : Form
{
    private readonly TextBox _labelBox;
    private readonly TextBox _promptBox;

    public string ResultLabel => _labelBox.Text.Trim();
    public string ResultPrompt => _promptBox.Text;

    public QuickSendRegisterDialog(string initialLabel, string initialPrompt)
    {
        Text = "クイック送信に登録";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MaximizeBox = false;
        MinimizeBox = false;
        ClientSize = new Size(440, 200);

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(12),
            RowCount = 5,
            ColumnCount = 1,
        };
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));   // label header
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 30)); // label input
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));   // prompt header
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); // prompt input
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 36)); // buttons

        layout.Controls.Add(new Label { Text = "メニュー表示名:", AutoSize = true }, 0, 0);

        _labelBox = new TextBox
        {
            Dock = DockStyle.Fill,
            Text = initialLabel,
            MaxLength = 100,
        };
        layout.Controls.Add(_labelBox, 0, 1);

        layout.Controls.Add(new Label { Text = "送信テキスト:", AutoSize = true }, 0, 2);

        _promptBox = new TextBox
        {
            Dock = DockStyle.Fill,
            Text = initialPrompt,
            Multiline = true,
            ScrollBars = ScrollBars.Vertical,
        };
        layout.Controls.Add(_promptBox, 0, 3);

        var btnPanel = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false,
        };
        var ok = new Button { Text = "OK", DialogResult = DialogResult.OK, Width = 75 };
        var cancel = new Button { Text = "キャンセル", DialogResult = DialogResult.Cancel, Width = 90 };
        btnPanel.Controls.AddRange(new Control[] { cancel, ok });
        layout.Controls.Add(btnPanel, 0, 4);

        Controls.Add(layout);
        AcceptButton = ok;
        CancelButton = cancel;

        _labelBox.SelectAll();
    }
}
