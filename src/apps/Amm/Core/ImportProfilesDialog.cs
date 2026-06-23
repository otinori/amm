namespace Amm.Core;

internal enum ImportConflictMode { Skip, Rename, Overwrite }

/// <summary>インポートするプロファイルの選択と重複解決方針を設定するダイアログ。</summary>
internal sealed class ImportProfilesDialog : Form
{
    private readonly CheckedListBox _list;
    private readonly List<SessionProfile> _imported;
    private readonly RadioButton _radioSkip;
    private readonly RadioButton _radioRename;
    private readonly RadioButton _radioOverwrite;

    public IReadOnlyList<SessionProfile> SelectedProfiles
    {
        get
        {
            var result = new List<SessionProfile>();
            for (int i = 0; i < _list.Items.Count; i++)
                if (_list.GetItemChecked(i)) result.Add(_imported[i]);
            return result;
        }
    }

    public ImportConflictMode ConflictMode =>
        _radioRename.Checked ? ImportConflictMode.Rename :
        _radioOverwrite.Checked ? ImportConflictMode.Overwrite :
        ImportConflictMode.Skip;

    public ImportProfilesDialog(IReadOnlyList<SessionProfile> imported, IReadOnlySet<string> existingNicknames)
    {
        _imported = [.. imported];
        Text = "インポートするコマンドを選択";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        ClientSize = new Size(540, 446);
        Font = new Font("Yu Gothic UI", 9F);

        _list = new CheckedListBox
        {
            Location = new Point(12, 12),
            Size = new Size(516, 240),
            CheckOnClick = true,
            IntegralHeight = false,
        };
        foreach (var p in _imported)
        {
            var isDup = existingNicknames.Contains(p.Nickname ?? "");
            _list.Items.Add(BuildLabel(p, isDup), isChecked: true);
        }

        var selectAll = new Button
        {
            Text = "すべて選択",
            Location = new Point(12, 262),
            Size = new Size(100, 28),
        };
        var clearAll = new Button
        {
            Text = "すべて解除",
            Location = new Point(120, 262),
            Size = new Size(100, 28),
        };

        var conflictLabel = new Label
        {
            Text = "重複（⚠）時の処理:",
            AutoSize = true,
            Location = new Point(12, 306),
        };
        _radioSkip = new RadioButton
        {
            Text = "スキップ（既定）— 同じ Nickname のコマンドは追加しない",
            AutoSize = true,
            Checked = true,
            Location = new Point(12, 326),
        };
        _radioRename = new RadioButton
        {
            Text = "リネーム — Nickname に連番サフィックス _2, _3, … を付加して追加",
            AutoSize = true,
            Location = new Point(12, 352),
        };
        _radioOverwrite = new RadioButton
        {
            Text = "上書き — 既存のコマンド設定をインポート内容で置換",
            AutoSize = true,
            Location = new Point(12, 378),
        };

        var ok = new Button
        {
            Text = "OK",
            DialogResult = DialogResult.OK,
            Location = new Point(ClientSize.Width - 12 - 90 - 8 - 90, 406),
            Size = new Size(90, 28),
        };
        var cancel = new Button
        {
            Text = "キャンセル",
            DialogResult = DialogResult.Cancel,
            Location = new Point(ClientSize.Width - 12 - 90, 406),
            Size = new Size(90, 28),
        };

        selectAll.Click += (_, _) => SetAllChecked(true);
        clearAll.Click += (_, _) => SetAllChecked(false);

        Controls.AddRange(new Control[]
        {
            _list, selectAll, clearAll,
            conflictLabel, _radioSkip, _radioRename, _radioOverwrite,
            ok, cancel,
        });
        AcceptButton = ok;
        CancelButton = cancel;
    }

    private void SetAllChecked(bool value)
    {
        for (int i = 0; i < _list.Items.Count; i++)
            _list.SetItemChecked(i, value);
    }

    private static string BuildLabel(SessionProfile p, bool isDuplicate)
    {
        var nick = string.IsNullOrEmpty(p.Nickname) ? "" : $"  [{p.Nickname}]";
        var exe = string.IsNullOrWhiteSpace(p.Executable) ? "(なし)"
            : $"{p.Executable} {string.Join(" ", p.Args)}".TrimEnd();
        var dup = isDuplicate ? "  ⚠ 重複" : "";
        return $"{p.Name}{nick}  ({p.CommandType})  —  {exe}{dup}";
    }
}
