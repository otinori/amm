namespace Amm.Core;

/// <summary>エクスポートするプロファイルを選択するダイアログ。</summary>
internal sealed class ExportProfilesDialog : Form
{
    private readonly CheckedListBox _list;
    private readonly List<SessionProfile> _profiles;

    public IReadOnlyList<SessionProfile> SelectedProfiles
    {
        get
        {
            var result = new List<SessionProfile>();
            for (int i = 0; i < _list.Items.Count; i++)
                if (_list.GetItemChecked(i)) result.Add(_profiles[i]);
            return result;
        }
    }

    public ExportProfilesDialog(IReadOnlyList<SessionProfile> profiles)
    {
        _profiles = [.. profiles];
        Text = "エクスポートするコマンドを選択";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        ClientSize = new Size(500, 370);
        Font = new Font("Yu Gothic UI", 9F);

        _list = new CheckedListBox
        {
            Location = new Point(12, 12),
            Size = new Size(476, 270),
            CheckOnClick = true,
            IntegralHeight = false,
        };
        foreach (var p in _profiles)
            _list.Items.Add(BuildLabel(p), isChecked: true);

        var selectAll = new Button
        {
            Text = "すべて選択",
            Location = new Point(12, 292),
            Size = new Size(100, 28),
        };
        var clearAll = new Button
        {
            Text = "すべて解除",
            Location = new Point(120, 292),
            Size = new Size(100, 28),
        };

        var ok = new Button
        {
            Text = "OK",
            DialogResult = DialogResult.OK,
            Location = new Point(ClientSize.Width - 12 - 90 - 8 - 90, 330),
            Size = new Size(90, 28),
        };
        var cancel = new Button
        {
            Text = "キャンセル",
            DialogResult = DialogResult.Cancel,
            Location = new Point(ClientSize.Width - 12 - 90, 330),
            Size = new Size(90, 28),
        };

        selectAll.Click += (_, _) => SetAllChecked(true);
        clearAll.Click += (_, _) => SetAllChecked(false);

        Controls.AddRange(new Control[] { _list, selectAll, clearAll, ok, cancel });
        AcceptButton = ok;
        CancelButton = cancel;
    }

    private void SetAllChecked(bool value)
    {
        for (int i = 0; i < _list.Items.Count; i++)
            _list.SetItemChecked(i, value);
    }

    private static string BuildLabel(SessionProfile p)
    {
        var nick = string.IsNullOrEmpty(p.Nickname) ? "" : $"  [{p.Nickname}]";
        var exe = string.IsNullOrWhiteSpace(p.Executable) ? "(なし)"
            : $"{p.Executable} {string.Join(" ", p.Args)}".TrimEnd();
        return $"{p.Name}{nick}  ({p.CommandType})  —  {exe}";
    }
}
