namespace Amm.Core;

/// <summary>
/// 統計情報 (指示回数 / AI 動作時間 / 人間の応答時間) を MDI ごとに一覧表示する
/// モーダル。表示対象は指定された作業ディレクトリ配下の
/// &lt;workDir&gt;\.amm\stats\&lt;yyyyMMdd&gt;\ にある全 MDI 分の集計ファイル。
/// 日付ピッカーで過去日にも遡れる。
/// </summary>
public sealed class ChatStatsDialog : Form
{
    private readonly string _workDir;
    private readonly DateTimePicker _datePicker;
    private readonly DataGridView _grid;
    private readonly Label _emptyLabel;

    public ChatStatsDialog(string workDir)
    {
        _workDir = workDir;
        Text = "統計情報";
        StartPosition = FormStartPosition.CenterParent;
        Size = new Size(820, 420);
        MinimizeBox = false;
        MaximizeBox = true;
        ShowIcon = false;

        var topPanel = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            Height = 36,
            FlowDirection = FlowDirection.LeftToRight,
            Padding = new Padding(8, 6, 8, 0),
        };
        topPanel.Controls.Add(new Label { Text = "日付:", AutoSize = true, Margin = new Padding(0, 6, 4, 0) });
        _datePicker = new DateTimePicker
        {
            Format = DateTimePickerFormat.Short,
            Value = DateTime.Today,
            Width = 120,
        };
        _datePicker.ValueChanged += (_, _) => Reload();
        topPanel.Controls.Add(_datePicker);

        var refreshButton = new Button { Text = "更新(&R)", AutoSize = true, Margin = new Padding(8, 2, 0, 0) };
        refreshButton.Click += (_, _) => Reload();
        topPanel.Controls.Add(refreshButton);

        _grid = new DataGridView
        {
            Dock = DockStyle.Fill,
            ReadOnly = true,
            AllowUserToAddRows = false,
            AllowUserToDeleteRows = false,
            AllowUserToResizeRows = false,
            RowHeadersVisible = false,
            AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
            SelectionMode = DataGridViewSelectionMode.FullRowSelect,
        };
        _grid.Columns.Add("MdiName", "MDI名");
        _grid.Columns.Add("Profile", "プロファイル");
        _grid.Columns.Add("Count", "指示回数");
        _grid.Columns.Add("AiTotal", "AI動作時間(累計)");
        _grid.Columns.Add("AiAvg", "AI動作時間(平均)");
        _grid.Columns.Add("HumanTotal", "人間の応答時間(累計)");
        _grid.Columns.Add("HumanAvg", "人間の応答時間(平均)");

        _emptyLabel = new Label
        {
            Text = "この日の統計情報はありません。",
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleCenter,
            ForeColor = SystemColors.GrayText,
            Visible = false,
        };

        var closeButton = new Button
        {
            Text = "閉じる",
            DialogResult = DialogResult.OK,
            Dock = DockStyle.Bottom,
            Height = 32,
        };

        Controls.Add(_grid);
        Controls.Add(_emptyLabel);
        Controls.Add(closeButton);
        Controls.Add(topPanel);
        AcceptButton = closeButton;

        Load += (_, _) => Reload();
    }

    private void Reload()
    {
        _grid.Rows.Clear();
        var records = ChatStatsStore.LoadAll(_workDir, _datePicker.Value.Date);
        records.Sort((a, b) => string.CompareOrdinal(a.MdiName, b.MdiName));

        foreach (var r in records)
        {
            _grid.Rows.Add(
                r.MdiName,
                r.Profile,
                r.InstructionCount,
                FormatMs(r.AiTotalMs),
                FormatMs(r.AiAvgMs),
                FormatMs(r.HumanTotalMs),
                r.HumanSampleCount > 0 ? FormatMs(r.HumanAvgMs) : "-");
        }

        _emptyLabel.Visible = records.Count == 0;
        _grid.Visible = records.Count > 0;
    }

    private static string FormatMs(long ms)
    {
        var span = TimeSpan.FromMilliseconds(ms);
        return span.TotalHours >= 1
            ? span.ToString(@"h\:mm\:ss")
            : span.ToString(@"m\:ss");
    }
}
