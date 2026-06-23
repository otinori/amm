using System.Text.Json;
using System.Text.Json.Serialization;
using Amm.Core.Mcp;

namespace Amm.Core;

/// <summary>
/// 「コマンド → コマンドを管理…」用モーダル。
/// 追加 / 編集 / 削除 を 1 画面に集約し、すべて working copy 上で行う。OK で
/// 親側の _profiles へコミット (= 既存 SessionProfile 参照を可能な限り温存)、
/// キャンセルで全変更を破棄する。
///
/// 親側 (MdiParentForm) は <see cref="Entries"/> を読み取り、各 entry の Original
/// が non-null なら field を流し込む形でその参照を温存しつつ、Original が null
/// なら新規 profile として配列に追加する責務を負う。これにより、起動中の MDI
/// 子が握っている _profile 参照が編集後も生き続ける (旧 OnCommandEdit と同様の
/// 挙動)。
/// </summary>
public sealed class CommandManagerDialog : Form
{
    /// <summary>working copy エントリ。<see cref="Value"/> は表示中の状態、
    /// <see cref="Original"/> は元の SessionProfile 参照 (新規追加なら null)。</summary>
    public sealed class Entry
    {
        public SessionProfile Value { get; set; }
        public SessionProfile? Original { get; set; }
        public Entry(SessionProfile value, SessionProfile? original)
        {
            Value = value;
            Original = original;
        }
    }

    private readonly List<Entry> _entries = new();
    private readonly ListBox _listBox;
    private readonly Button _editButton;
    private readonly Button _removeButton;
    private readonly Button _upButton;
    private readonly Button _downButton;
    private readonly Button _importButton;
    private readonly Button _exportButton;

    /// <summary>OK 確定後に親へ返すエントリ一覧 (キャンセル時はアクセス不可)。</summary>
    public IReadOnlyList<Entry> Entries => _entries;

    public CommandManagerDialog(SessionProfile[] initial)
    {
        Text = "コマンドを管理";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        Font = new Font("Yu Gothic UI", 9F);

        // 初期 working copy: 各 profile を JSON clone した値を Value、元参照を
        // Original として保持。Original を後で MdiParentForm 側で更新する。
        foreach (var p in initial)
        {
            var clone = JsonClone(p);
            _entries.Add(new Entry(clone, p));
        }

        // 高 DPI でボタン下端が切れない高さ計算。CommandTemplateDialog と同じ式。
        var buttonHeight = Math.Max(28, (SystemFonts.MenuFont?.Height ?? 16) + 12);

        const int padding = 12;
        const int listWidth = 320;
        const int btnWidth = 120;
        const int gap = 8;
        const int listHeight = 320;

        ClientSize = new Size(
            padding + listWidth + gap + btnWidth + padding,
            padding + listHeight + 16 + buttonHeight + padding);

        // --- 1. すべてのコントロールを先に生成 (フィールド代入も先に済ませる) ---
        // UpdateButtonStates が参照する _editButton / _removeButton / _upButton /
        // _downButton が null のまま RefreshList → UpdateButtonStates が呼ばれて
        // NullReferenceException でコンストラクタが落ちるのを避けるため、
        // 全ボタン生成 → 最後に RefreshList の順に並べる。
        _listBox = new ListBox
        {
            Location = new Point(padding, padding),
            Size = new Size(listWidth, listHeight),
            IntegralHeight = false,
        };

        int btnX = padding + listWidth + gap;
        int btnY = padding;
        var addButton = new Button
        {
            Text = "追加(&N)...",
            Location = new Point(btnX, btnY),
            Size = new Size(btnWidth, buttonHeight),
        };

        btnY += buttonHeight + 6;
        _editButton = new Button
        {
            Text = "編集(&E)...",
            Location = new Point(btnX, btnY),
            Size = new Size(btnWidth, buttonHeight),
        };

        btnY += buttonHeight + 6;
        _removeButton = new Button
        {
            Text = "削除(&R)",
            Location = new Point(btnX, btnY),
            Size = new Size(btnWidth, buttonHeight),
        };

        btnY += buttonHeight + 16;
        _upButton = new Button
        {
            Text = "↑ 上へ",
            Location = new Point(btnX, btnY),
            Size = new Size(btnWidth, buttonHeight),
        };

        btnY += buttonHeight + 6;
        _downButton = new Button
        {
            Text = "↓ 下へ",
            Location = new Point(btnX, btnY),
            Size = new Size(btnWidth, buttonHeight),
        };

        btnY += buttonHeight + 16;
        _importButton = new Button
        {
            Text = "インポート...",
            Location = new Point(btnX, btnY),
            Size = new Size(btnWidth, buttonHeight),
        };

        btnY += buttonHeight + 6;
        _exportButton = new Button
        {
            Text = "エクスポート...",
            Location = new Point(btnX, btnY),
            Size = new Size(btnWidth, buttonHeight),
        };

        int bottomY = padding + listHeight + 16;
        var ok = new Button
        {
            Text = "OK",
            DialogResult = DialogResult.OK,
            Location = new Point(ClientSize.Width - padding - 90 - 8 - 90, bottomY),
            Size = new Size(90, buttonHeight),
        };
        var cancel = new Button
        {
            Text = "キャンセル",
            DialogResult = DialogResult.Cancel,
            Location = new Point(ClientSize.Width - padding - 90, bottomY),
            Size = new Size(90, buttonHeight),
        };

        // --- 2. Controls に追加 ---
        Controls.Add(_listBox);
        Controls.Add(addButton);
        Controls.Add(_editButton);
        Controls.Add(_removeButton);
        Controls.Add(_upButton);
        Controls.Add(_downButton);
        Controls.Add(_importButton);
        Controls.Add(_exportButton);
        Controls.Add(ok);
        Controls.Add(cancel);
        AcceptButton = ok;
        CancelButton = cancel;

        // --- 3. イベント配線 ---
        _listBox.DoubleClick += (_, _) => EditSelected();
        _listBox.SelectedIndexChanged += (_, _) => UpdateButtonStates();
        addButton.Click += (_, _) => AddNew();
        _editButton.Click += (_, _) => EditSelected();
        _removeButton.Click += (_, _) => RemoveSelected();
        _upButton.Click += (_, _) => MoveSelected(-1);
        _downButton.Click += (_, _) => MoveSelected(+1);
        _importButton.Click += (_, _) => ImportProfiles();
        _exportButton.Click += (_, _) => ExportProfiles();

        // --- 4. 初期状態を流し込む (この時点で全フィールドが non-null) ---
        RefreshList();
    }

    private void UpdateButtonStates()
    {
        var has = _listBox.SelectedIndex >= 0;
        _editButton.Enabled = has;
        _removeButton.Enabled = has;
        _upButton.Enabled = has && _listBox.SelectedIndex > 0;
        _downButton.Enabled = has && _listBox.SelectedIndex < _entries.Count - 1;
    }

    private void RefreshList()
    {
        var prevIndex = _listBox?.SelectedIndex ?? -1;
        if (_listBox == null) return;
        _listBox.BeginUpdate();
        try
        {
            _listBox.Items.Clear();
            foreach (var e in _entries)
            {
                var label = e.Value.Name;
                if (e.Original == null) label += "  (新規)";
                _listBox.Items.Add(label);
            }
        }
        finally
        {
            _listBox.EndUpdate();
        }
        if (prevIndex >= 0 && prevIndex < _entries.Count)
            _listBox.SelectedIndex = prevIndex;
        else if (_entries.Count > 0)
            _listBox.SelectedIndex = 0;
        UpdateButtonStates();
    }

    private void AddNew()
    {
        // 重複名などのバリデーションエラーが発生したらメッセージを出した上で、
        // 入力済みの値を初期表示として CommandTemplateDialog を再オープンする。
        // (旧実装は親 Manager 画面に戻ってしまい、ユーザが入力を失う不満があった)
        SessionProfile? draft = null;
        while (true)
        {
            using var dlg = new CommandTemplateDialog(initial: draft, isAddMode: true);
            if (dlg.ShowDialog(this) != DialogResult.OK) return;

            if (HasNameConflict(dlg.Result.Name, ignoreIndex: -1))
            {
                MessageBox.Show(this, $"同名のコマンドが既に存在します: {dlg.Result.Name}",
                    "コマンド追加", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                draft = dlg.Result;  // 直前の入力を引き継いで再表示
                continue;
            }
            _entries.Add(new Entry(dlg.Result, original: null));
            RefreshList();
            _listBox.SelectedIndex = _entries.Count - 1;
            return;
        }
    }

    private void EditSelected()
    {
        var idx = _listBox.SelectedIndex;
        if (idx < 0 || idx >= _entries.Count) return;
        var entry = _entries[idx];

        // 編集も追加と同様に「エラー時は CommandTemplateDialog に戻す」ループに。
        // 初回は現在の Value、リトライ時は直前の入力を初期表示にする。
        SessionProfile draft = entry.Value;
        while (true)
        {
            using var dlg = new CommandTemplateDialog(initial: draft, isAddMode: false);
            if (dlg.ShowDialog(this) != DialogResult.OK) return;

            if (HasNameConflict(dlg.Result.Name, ignoreIndex: idx))
            {
                MessageBox.Show(this, $"同名のコマンドが既に存在します: {dlg.Result.Name}",
                    "コマンド編集", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                draft = dlg.Result;
                continue;
            }
            entry.Value = dlg.Result;  // Original は据え置き (OK 時に元参照へ流し込む)
            RefreshList();
            _listBox.SelectedIndex = idx;
            return;
        }
    }

    private void RemoveSelected()
    {
        var idx = _listBox.SelectedIndex;
        if (idx < 0 || idx >= _entries.Count) return;
        var entry = _entries[idx];
        var result = MessageBox.Show(this,
            $"コマンド「{entry.Value.Name}」を削除しますか?\n\n" +
            "(OK 押下までは確定しません。キャンセルで削除を取り消せます。)",
            "コマンド削除の確認",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Warning,
            MessageBoxDefaultButton.Button2);
        if (result != DialogResult.Yes) return;
        _entries.RemoveAt(idx);
        RefreshList();
        if (_entries.Count > 0)
            _listBox.SelectedIndex = Math.Min(idx, _entries.Count - 1);
    }

    private void MoveSelected(int delta)
    {
        var idx = _listBox.SelectedIndex;
        if (idx < 0) return;
        var newIdx = idx + delta;
        if (newIdx < 0 || newIdx >= _entries.Count) return;
        (_entries[idx], _entries[newIdx]) = (_entries[newIdx], _entries[idx]);
        RefreshList();
        _listBox.SelectedIndex = newIdx;
    }

    private bool HasNameConflict(string name, int ignoreIndex)
    {
        for (int i = 0; i < _entries.Count; i++)
        {
            if (i == ignoreIndex) continue;
            if (string.Equals(_entries[i].Value.Name, name, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    private void ExportProfiles()
    {
        var profiles = _entries.Select(e => e.Value).ToList();
        if (profiles.Count == 0)
        {
            MessageBox.Show(this, "エクスポートするコマンドがありません。",
                "エクスポート", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        using var selectDlg = new ExportProfilesDialog(profiles);
        if (selectDlg.ShowDialog(this) != DialogResult.OK) return;

        var selected = selectDlg.SelectedProfiles;
        if (selected.Count == 0)
        {
            MessageBox.Show(this, "コマンドが選択されていません。",
                "エクスポート", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        var today = DateTime.Today.ToString("yyyy-MM-dd");
        using var saveDlg = new SaveFileDialog
        {
            Title = "コマンド設定をエクスポート",
            Filter = "AMM Profiles (*.ammprofiles)|*.ammprofiles|AMM File (*.amm)|*.amm|All files (*.*)|*.*",
            FileName = $"profiles-export-{today}.ammprofiles",
            DefaultExt = "ammprofiles",
        };
        if (saveDlg.ShowDialog(this) != DialogResult.OK) return;

        try
        {
            var container = new ProfilesExportFile { Profiles = [.. selected] };
            var json = JsonSerializer.Serialize(container, new JsonSerializerOptions
            {
                WriteIndented = true,
                DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingDefault,
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            });
            AtomicFileWriter.Write(saveDlg.FileName, json);
            MessageBox.Show(this,
                $"{selected.Count} 件のコマンドをエクスポートしました。\n\n{saveDlg.FileName}",
                "エクスポート完了", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"エクスポートに失敗しました。\n\n{ex.Message}",
                "エクスポートエラー", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void ImportProfiles()
    {
        using var openDlg = new OpenFileDialog
        {
            Title = "コマンド設定をインポート",
            Filter = "AMM Profiles (*.ammprofiles)|*.ammprofiles|AMM File (*.amm)|*.amm|All files (*.*)|*.*",
        };
        if (openDlg.ShowDialog(this) != DialogResult.OK) return;

        List<SessionProfile> imported;
        try
        {
            imported = LoadProfilesFromFile(openDlg.FileName);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"ファイルの読み込みに失敗しました。\n\n{ex.Message}",
                "インポートエラー", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }

        if (imported.Count == 0)
        {
            MessageBox.Show(this, "インポートするコマンドが見つかりませんでした。",
                "インポート", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        var existingNicknames = _entries
            .Select(e => e.Value.Nickname ?? "")
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        using var importDlg = new ImportProfilesDialog(imported, existingNicknames);
        if (importDlg.ShowDialog(this) != DialogResult.OK) return;

        var selected = importDlg.SelectedProfiles;
        if (selected.Count == 0) return;

        var mode = importDlg.ConflictMode;
        int added = 0, replaced = 0, skipped = 0;

        foreach (var profile in selected)
        {
            var nick = profile.Nickname ?? "";
            var existingEntry = _entries.FirstOrDefault(e =>
                string.Equals(e.Value.Nickname ?? "", nick, StringComparison.OrdinalIgnoreCase));

            if (existingEntry != null)
            {
                switch (mode)
                {
                    case ImportConflictMode.Skip:
                        skipped++;
                        break;
                    case ImportConflictMode.Rename:
                        var renamed = JsonClone(profile);
                        renamed.Nickname = MakeUniqueNickname(nick, existingNicknames);
                        existingNicknames.Add(renamed.Nickname);
                        _entries.Add(new Entry(renamed, original: null));
                        added++;
                        break;
                    case ImportConflictMode.Overwrite:
                        existingEntry.Value = JsonClone(profile);
                        replaced++;
                        break;
                }
            }
            else
            {
                var clone = JsonClone(profile);
                _entries.Add(new Entry(clone, original: null));
                existingNicknames.Add(clone.Nickname ?? "");
                added++;
            }
        }

        RefreshList();

        var msg = $"インポート完了: 追加 {added} 件";
        if (replaced > 0) msg += $"、上書き {replaced} 件";
        if (skipped > 0) msg += $"、スキップ {skipped} 件";
        MessageBox.Show(this, msg, "インポート完了", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    private static List<SessionProfile> LoadProfilesFromFile(string filePath)
    {
        var json = File.ReadAllText(filePath, System.Text.Encoding.UTF8);
        var opts = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            AllowTrailingCommas = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
        };
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        List<SessionProfile>? profiles;
        if (root.TryGetProperty("profiles", out var arr))
            profiles = JsonSerializer.Deserialize<List<SessionProfile>>(arr.GetRawText(), opts);
        else if (root.ValueKind == JsonValueKind.Array)
            profiles = JsonSerializer.Deserialize<List<SessionProfile>>(json, opts);
        else
            throw new InvalidDataException("profiles キーが見つかりません。AMM Profiles または AMM ファイルを指定してください。");
        profiles ??= [];
        foreach (var p in profiles) p.MigrateLegacyFields();
        return profiles;
    }

    private static string MakeUniqueNickname(string baseName, HashSet<string> existing)
    {
        if (!existing.Contains(baseName)) return baseName;
        for (int i = 2; ; i++)
        {
            var candidate = $"{baseName}_{i}";
            if (!existing.Contains(candidate)) return candidate;
        }
    }

    private static SessionProfile JsonClone(SessionProfile p)
    {
        var json = JsonSerializer.Serialize(p);
        var clone = JsonSerializer.Deserialize<SessionProfile>(json);
        if (clone == null)
            AppLogger.Warn($"JsonClone: SessionProfile '{p.Name}' のクローンに失敗し空プロファイルにフォールバックしました");
        return clone ?? new SessionProfile();
    }
}
