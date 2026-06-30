namespace Amm.Core;

/// <summary>
/// 「ファイル → コマンド追加 / 編集」用モーダル (UDR-amm-20260427T0159-d4e)。
/// 追加モードではテンプレート選択 ComboBox が有効になり、選択した瞬間に各
/// フィールドへ既定値が流し込まれる。編集モードでは ComboBox を隠し、与えられた
/// SessionProfile の値を初期表示する。
/// </summary>
public sealed class CommandTemplateDialog : Form
{
    private readonly TextBox _name;
    private readonly TextBox _executable;
    private readonly TextBox _args;
    private readonly TextBox _workingDirectory;
    private readonly ComboBox _newlineMode;
    private readonly ComboBox _outputEncoding;
    private readonly CheckBox _autoChcp;
    private readonly TextBox _waitPatterns;
    private readonly NumericUpDown _autoStartCount;
    private readonly CheckBox _closeProhibited;
    private readonly CheckBox _collapseBlankLines;
    private readonly TextBox _commentPrefixes;
    private readonly TextBox _nickname;
    private readonly CheckBox _sendLineByLine;
    private readonly CheckBox _useBracketedPaste;
    private readonly CheckBox _selectWorkingDirOnStart;
    private readonly CheckBox _resumeOnStart;
    private readonly CheckBox _promptNewNameOnCommandAdd;
    private readonly ComboBox _commandType;

    // コマンドタイプ ComboBox の表示ラベルと enum の対応 (表示順)。
    private static readonly (string Label, CommandType Type)[] CommandTypeItems =
    [
        ("Cmd", CommandType.Cmd),
        ("Powershell", CommandType.PowerShell),
        ("Claude Code", CommandType.ClaudeCode),
        ("Codex", CommandType.Codex),
        ("COPILOT-CLI", CommandType.CopilotCli),
        ("その他", CommandType.Other),
    ];

    private static int CommandTypeIndexOf(CommandType type)
    {
        var idx = Array.FindIndex(CommandTypeItems, x => x.Type == type);
        return idx < 0 ? CommandTypeItems.Length - 1 : idx; // 未知は「その他」
    }
    private readonly ComboBox _fontSize;
    private readonly DataGridView _windowGeometry;
    private readonly DataGridView _quickPrompts;
    private readonly CheckBox _autoSendEnabled;
    private readonly TextBox _autoSendPrompt;
    private readonly NumericUpDown _autoSendDelaySeconds;
    private readonly CheckBox _chatRecord;
    private readonly NumericUpDown _chatRecordTailChars;
    private readonly CheckBox _stats;

    /// <summary>OK 押下時に組み立てた SessionProfile (Cancel ならアクセス不可)。</summary>
    public SessionProfile Result { get; private set; } = new();

    public CommandTemplateDialog(SessionProfile? initial = null, bool isAddMode = false)
    {
        Text = isAddMode ? "コマンド追加" : "コマンド編集";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        ClientSize = new Size(760, 720);
        Font = new Font("Yu Gothic UI", 9F);

        var initialProfile = initial ?? new SessionProfile();

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(12),
            ColumnCount = 2,
            AutoScroll = true,
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 140F));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));

        // ---- コマンドタイプ (常時表示) ----
        // 選択するとそのタイプの既定設定一式 (実行ファイル/引数/wait/改行/paste 方式等)
        // を適用し、resume トークンや wait 検知など実行時のふるまいも駆動する。
        // 「その他」はプリセットなし (手動)。タイプは保存される。
        AddRow(layout, "コマンドタイプ", _commandType = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList,
            Width = 360,
        });
        foreach (var (label, _) in CommandTypeItems)
            _commandType.Items.Add(label);
        _commandType.SelectedIndex = CommandTypeIndexOf(initialProfile.CommandType);
        _appliedType = initialProfile.CommandType;

        // ---- 共通フィールド ----
        AddRow(layout, "名前 (Name)", _name = MakeTextBox(initialProfile.Name));
        AddRow(layout, "実行ファイル (Executable)", _executable = MakeTextBox(initialProfile.Executable));
        AddRow(layout, "引数 (Args, スペース区切り)", _args = MakeTextBox(string.Join(" ", initialProfile.Args)));
        _workingDirectory = MakeTextBox(initialProfile.WorkingDirectory ?? "");
        AddRow(layout, "作業ディレクトリ", BuildWorkingDirectoryField(_workingDirectory));

        // 「起動時に作業ディレクトリを選択する」は作業ディレクトリの直下行に配置
        // (関連の強い設定をまとめる; 旧 v2 セクションから移動)。
        _selectWorkingDirOnStart = new CheckBox
        {
            Text = "起動時に作業ディレクトリを選択する",
            AutoSize = true,
            Checked = initialProfile.SelectWorkingDirOnStart,
        };
        AddRow(layout, "", _selectWorkingDirOnStart);

        // 「起動時にセッションを復帰する」: ON のとき、CLI 種別 (nickname) に応じた
        // セッション復帰オプションを引数末尾へ自動付加して起動する
        // (claude/copilot=--resume, codex=resume)。保存セッションが無いフォルダでは
        // CLI が即終了し得るため既定 OFF。復帰トークンを上書きしたい場合は
        // profiles.amm の resumeArgs を使う。
        _resumeOnStart = new CheckBox
        {
            Text = "起動時にセッションを復帰する (claude/copilot=--resume, codex=resume)",
            AutoSize = true,
            Checked = initialProfile.ResumeOnStart,
        };
        AddRow(layout, "", _resumeOnStart);

        // 「コマンド追加時に新しい名前を入力する」: コマンドメニューから手動で
        // このコマンドを起動した瞬間にだけ発動する (--all / autoStartCount / 記憶
        // 配置の復元では発動しない = "アプリ起動時" ではないため誤認を避けて
        // "コマンド追加時" と表記)。ON のときは作業ディレクトリ選択ダイアログの後
        // に名前入力ダイアログが出て、入力された名前を持つ新しい profile が
        // _profiles に追加される (= テンプレ → ユーザ固有コマンドの派生フロー)。
        // 派生先 profile は in-memory のみ; AMM ファイルへの永続化は
        // [ファイル → 上書き保存] / [名前を付けて保存] が担う。
        _promptNewNameOnCommandAdd = new CheckBox
        {
            Text = "コマンド追加時に新しい名前を入力する",
            AutoSize = true,
            Checked = initialProfile.PromptNewNameOnCommandAdd,
        };
        AddRow(layout, "", _promptNewNameOnCommandAdd);

        _newlineMode = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 120 };
        _newlineMode.Items.AddRange(["CRLF", "LF"]);
        _newlineMode.SelectedItem = initialProfile.NewlineMode.ToString();
        AddRow(layout, "改行モード", _newlineMode);

        // 「マルチライン送信時、行ごとに個別の改行を送る」は改行モードの直下行に配置
        // (改行関連の設定をまとめる; 旧 v2 セクションから移動)。
        _sendLineByLine = new CheckBox
        {
            Text = "マルチライン送信時、行ごとに個別の改行を送る (AI CLI 向け)",
            AutoSize = true,
            Checked = initialProfile.SendLineByLine,
        };
        AddRow(layout, "", _sendLineByLine);

        // bracketed paste mode で送信 (Copilot CLI など Ink ベース TUI 用)。
        // \x1b[200~..\x1b[201~ で囲み + 確定 Enter。SendLineByLine と排他。
        _useBracketedPaste = new CheckBox
        {
            Text = "bracketed paste mode で送信 (Copilot CLI 等)",
            AutoSize = true,
            Checked = initialProfile.UseBracketedPaste,
        };
        AddRow(layout, "", _useBracketedPaste);

        _outputEncoding = new ComboBox { DropDownStyle = ComboBoxStyle.DropDown, Width = 160 };
        _outputEncoding.Items.AddRange(["UTF-8", "Shift_JIS"]);
        _outputEncoding.Text = initialProfile.OutputEncoding;
        AddRow(layout, "出力エンコーディング", _outputEncoding);

        _autoChcp = new CheckBox { Text = "AutoChcp (起動直後に chcp 65001)", AutoSize = true, Checked = initialProfile.AutoChcp };
        AddRow(layout, "", _autoChcp);

        _waitPatterns = new TextBox
        {
            Multiline = true,
            ScrollBars = ScrollBars.Vertical,
            Width = 360,
            Height = 60,
            Text = string.Join(Environment.NewLine, initialProfile.WaitPatterns),
        };
        AddRow(layout, "wait パターン (1行1正規表現)", _waitPatterns);

        // ---- v2: per-command 設定 ----
        var sectionLabel = new Label
        {
            Text = "── per-command 設定 (v2) ──",
            AutoSize = true,
            ForeColor = SystemColors.GrayText,
            Margin = new Padding(0, 8, 0, 4),
        };
        layout.Controls.Add(sectionLabel);
        layout.SetColumnSpan(sectionLabel, 2);

        _autoStartCount = new NumericUpDown { Minimum = 0, Maximum = 16, Value = initialProfile.AutoStartCount, Width = 80 };
        AddRow(layout, "起動時自動生成数", _autoStartCount);

        _closeProhibited = new CheckBox { Text = "クローズ禁止", AutoSize = true, Checked = initialProfile.CloseProhibited };
        AddRow(layout, "", _closeProhibited);

        _collapseBlankLines = new CheckBox { Text = "連続空行を 1 行にまとめる", AutoSize = true, Checked = initialProfile.CollapseBlankLines };
        AddRow(layout, "", _collapseBlankLines);

        _commentPrefixes = MakeTextBox(string.Join(",", initialProfile.CommentPrefixes));
        AddRow(layout, "コメント記号 (CSV)", _commentPrefixes);

        _nickname = MakeTextBox(initialProfile.Nickname ?? "");
        AddRow(layout, "Nickname (MCP 受信名)", _nickname);

        _fontSize = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 140 };
        _fontSize.Items.Add("(既定)");
        foreach (var (label, size) in FontSizePresets.All)
            _fontSize.Items.Add($"{label} ({size}px)");
        SelectFontSize(_fontSize, initialProfile.FontSize);
        AddRow(layout, "フォントサイズ", _fontSize);

        _windowGeometry = BuildGeometryGrid(initialProfile.WindowGeometry);
        AddRow(layout, "Window Geometry (上から起動 1, 2, … 個目)", _windowGeometry);

        var geomHint = new Label
        {
            Text = "※ Name はタイトル名 (空 = profile 名 + インスタンス番号)。\n" +
                   "   WorkingDirectory はこの MDI 限定の作業ディレクトリ (空 = profile 既定; 指定時は\n" +
                   "   起動時のフォルダ選択ダイアログをスキップ)。セルをダブルクリックでフォルダ選択。\n" +
                   "   行削除は行ヘッダ (左端の番号) を選択して Delete キー、または全セルを空に。",
            AutoSize = true,
            ForeColor = SystemColors.GrayText,
            Margin = new Padding(0, 2, 0, 0),
        };
        layout.Controls.Add(geomHint);
        layout.SetColumnSpan(geomHint, 2);

        _quickPrompts = BuildQuickPromptsGrid(initialProfile.QuickPrompts);
        AddRow(layout, "クイック送信 (右クリックメニュー)", _quickPrompts);

        var quickHint = new Label
        {
            Text = "※ Label はメニュー表示名、Prompt は送信本文。\n" +
                   "   MDI 切替バーのボタン右クリック → 「クイック送信 ▶」から呼べる。\n" +
                   "   Prompt は改行を含めても OK (DataGridView 上は \\n でエスケープ表示)。",
            AutoSize = true,
            ForeColor = SystemColors.GrayText,
            Margin = new Padding(0, 2, 0, 0),
        };
        layout.Controls.Add(quickHint);
        layout.SetColumnSpan(quickHint, 2);

        // ---- アイドル時自動送信 ----
        var autoSendSection = new Label
        {
            Text = "── アイドル時自動送信 ──",
            AutoSize = true,
            ForeColor = SystemColors.GrayText,
            Margin = new Padding(0, 8, 0, 4),
        };
        layout.Controls.Add(autoSendSection);
        layout.SetColumnSpan(autoSendSection, 2);

        _autoSendEnabled = new CheckBox
        {
            Text = "アイドル時（ToolUse なし）に自動送信する",
            AutoSize = true,
            Checked = initialProfile.AutoSendOnIdle.Enabled,
        };
        AddRow(layout, "", _autoSendEnabled);

        _autoSendPrompt = new TextBox
        {
            Multiline = true,
            ScrollBars = ScrollBars.Vertical,
            Width = 360,
            Height = 60,
            Text = initialProfile.AutoSendOnIdle.Prompt,
        };
        AddRow(layout, "送信プロンプト", _autoSendPrompt);

        var delaySeconds = Math.Clamp(initialProfile.AutoSendOnIdle.DelayMs / 1000, 0, 60);
        _autoSendDelaySeconds = new NumericUpDown
        {
            Minimum = 0,
            Maximum = 60,
            Value = delaySeconds,
            Width = 80,
        };
        AddRow(layout, "遅延時間（秒、0=即時）", _autoSendDelaySeconds);

        // ---- チャット記録 ----
        var chatRecordSection = new Label
        {
            Text = "── チャット記録 ──",
            AutoSize = true,
            ForeColor = SystemColors.GrayText,
            Margin = new Padding(0, 8, 0, 4),
        };
        layout.Controls.Add(chatRecordSection);
        layout.SetColumnSpan(chatRecordSection, 2);

        _chatRecord = new CheckBox
        {
            Text = "コマンド送信と応答末尾を .amm\\logs\\yyyyMMdd フォルダに JSON 記録する",
            AutoSize = true,
            Checked = initialProfile.ChatRecord,
        };
        AddRow(layout, "", _chatRecord);

        _chatRecordTailChars = new NumericUpDown
        {
            Minimum = 100,
            Maximum = 100000,
            Value = Math.Clamp(initialProfile.ChatRecordTailChars, 100, 100000),
            Width = 100,
        };
        AddRow(layout, "記録末尾文字数", _chatRecordTailChars);

        // ---- 統計情報 ----
        var statsSection = new Label
        {
            Text = "── 統計情報 ──",
            AutoSize = true,
            ForeColor = SystemColors.GrayText,
            Margin = new Padding(0, 8, 0, 4),
        };
        layout.Controls.Add(statsSection);
        layout.SetColumnSpan(statsSection, 2);

        _stats = new CheckBox
        {
            Text = "指示回数・AI動作時間・人間の応答時間を .amm\\stats\\yyyyMMdd フォルダに集計記録する\n(チャット記録とは独立したスイッチ)",
            AutoSize = true,
            Checked = initialProfile.Stats,
        };
        AddRow(layout, "", _stats);

        var hint = new Label
        {
            Text = "※ Theme / InitialCommands / SessionLog / CtrlCCopyOnSelection / CloseOnExit は\n" +
                   "   このダイアログでは編集できません。必要なら profiles.amm を直接編集してください。",
            AutoSize = true,
            ForeColor = SystemColors.GrayText,
            Margin = new Padding(0, 8, 0, 0),
        };
        layout.Controls.Add(hint);
        layout.SetColumnSpan(hint, 2);

        // ---- ボタン ----
        // 高 DPI でボタン文字下端が切れる / ボトムストリップ自体がフォーム下端で
        // 削れる問題を避けるため、メニューフォント行高ベースで動的算出する。
        var buttonHeight = Math.Max(28, (SystemFonts.MenuFont?.Height ?? 16) + 12);
        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            FlowDirection = FlowDirection.RightToLeft,
            Height = buttonHeight + 16,
            Padding = new Padding(8),
        };
        var cancel = new Button { Text = "キャンセル", DialogResult = DialogResult.Cancel, Width = 100, Height = buttonHeight, Margin = new Padding(4) };
        var ok = new Button { Text = "OK", Width = 100, Height = buttonHeight, Margin = new Padding(4) };
        ok.Click += (_, _) =>
        {
            try
            {
                Result = BuildProfile();
                DialogResult = DialogResult.OK;
                Close();
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, ex.Message, "入力エラー", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        };
        buttons.Controls.Add(cancel);
        buttons.Controls.Add(ok);

        Controls.Add(layout);
        Controls.Add(buttons);
        AcceptButton = ok;
        CancelButton = cancel;

        // コマンドタイプ選択イベントは全フィールド組み終わったあとに hook
        // (初期 SelectedIndex 設定はこの hook より前なので発火しない)。
        _commandType.SelectedIndexChanged += OnCommandTypeChanged;
    }

    private CommandType SelectedCommandType =>
        (_commandType.SelectedIndex >= 0 && _commandType.SelectedIndex < CommandTypeItems.Length)
            ? CommandTypeItems[_commandType.SelectedIndex].Type
            : CommandType.Other;

    private void OnCommandTypeChanged(object? sender, EventArgs e)
    {
        var type = SelectedCommandType;
        var preset = SessionProfile.PresetFor(type);
        if (preset == null) return; // 「その他」はプリセットなし (種別タグのみ)

        // 既に実行ファイル/引数/wait に内容があり、プリセットと異なる場合は上書き確認。
        bool hasContent =
            (!string.IsNullOrWhiteSpace(_executable.Text) && _executable.Text.Trim() != preset.Executable)
            || !string.IsNullOrWhiteSpace(_args.Text)
            || !string.IsNullOrWhiteSpace(_waitPatterns.Text);
        if (hasContent)
        {
            var label = CommandTypeItems[_commandType.SelectedIndex].Label;
            var r = MessageBox.Show(this,
                $"「{label}」の既定設定を適用します。\n" +
                "実行ファイル / 引数 / wait パターン / 改行モード / ペースト方式などが上書きされます。\n" +
                "(名前 / Nickname / 作業ディレクトリ / クイック送信は保持します)\n\n続行しますか？",
                "コマンドタイプの既定を適用",
                MessageBoxButtons.OKCancel, MessageBoxIcon.Question, MessageBoxDefaultButton.Button1);
            if (r != DialogResult.OK)
            {
                // 取り消し: 種別選択を元に戻す (イベント再入を避けるため一旦 hook を外す)。
                _commandType.SelectedIndexChanged -= OnCommandTypeChanged;
                _commandType.SelectedIndex = CommandTypeIndexOf(_appliedType);
                _commandType.SelectedIndexChanged += OnCommandTypeChanged;
                return;
            }
        }
        ApplyTypePreset(preset);
        _appliedType = type;
    }

    /// <summary>現在フォームに反映済みの種別 (取り消し時に戻す先)。</summary>
    private CommandType _appliedType;

    /// <summary>
    /// 種別プリセットの「ふるまい関連フィールド」だけをフォームへ適用する。
    /// 名前 / Nickname / 作業ディレクトリ / AutoStart / クイック送信 / 配置 / フォントは
    /// ユーザー固有のため保持する。resumeOnStart も opt-in 維持で触らない。
    /// </summary>
    private void ApplyTypePreset(SessionProfile p)
    {
        _executable.Text = p.Executable;
        _args.Text = string.Join(" ", p.Args);
        _newlineMode.SelectedItem = p.NewlineMode.ToString();
        _outputEncoding.Text = p.OutputEncoding;
        _autoChcp.Checked = p.AutoChcp;
        _waitPatterns.Text = string.Join(Environment.NewLine, p.WaitPatterns);
        _sendLineByLine.Checked = p.SendLineByLine;
        _useBracketedPaste.Checked = p.UseBracketedPaste;
        _selectWorkingDirOnStart.Checked = p.SelectWorkingDirOnStart;
        _promptNewNameOnCommandAdd.Checked = p.PromptNewNameOnCommandAdd;
    }

    private static void SelectFontSize(ComboBox combo, int? size)
    {
        if (size.HasValue)
        {
            for (int i = 0; i < FontSizePresets.All.Length; i++)
            {
                if (FontSizePresets.All[i].Size == size.Value)
                {
                    combo.SelectedIndex = i + 1; // index 0 は "(既定)"
                    return;
                }
            }
        }
        combo.SelectedIndex = 0;
    }

    private static int? ReadFontSize(ComboBox combo)
    {
        var idx = combo.SelectedIndex;
        if (idx <= 0) return null; // 0 = "(既定)" / 未選択
        return FontSizePresets.All[idx - 1].Size;
    }

    private SessionProfile BuildProfile()
    {
        if (string.IsNullOrWhiteSpace(_name.Text))
            throw new InvalidOperationException("名前 (Name) は必須です。");
        if (string.IsNullOrWhiteSpace(_executable.Text))
            throw new InvalidOperationException("実行ファイル (Executable) は必須です。");

        var args = SplitArgsRespectingQuotes(_args.Text);
        var waitPatterns = _waitPatterns.Text
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Select(s => s.Trim())
            .Where(s => s.Length > 0)
            .ToArray();
        var commentPrefixes = _commentPrefixes.Text
            .Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(s => s.Trim())
            .Where(s => s.Length > 0)
            .ToArray();
        var geometry = ReadGeometryGrid(_windowGeometry);

        return new SessionProfile
        {
            Name = _name.Text.Trim(),
            CommandType = SelectedCommandType,
            Executable = _executable.Text.Trim(),
            Args = args,
            WorkingDirectory = string.IsNullOrWhiteSpace(_workingDirectory.Text) ? null : _workingDirectory.Text.Trim(),
            NewlineMode = (_newlineMode.SelectedItem as string) == "LF" ? NewlineMode.LF : NewlineMode.CRLF,
            OutputEncoding = string.IsNullOrWhiteSpace(_outputEncoding.Text) ? "UTF-8" : _outputEncoding.Text.Trim(),
            AutoChcp = _autoChcp.Checked,
            WaitPatterns = waitPatterns,
            AutoStartCount = (int)_autoStartCount.Value,
            CloseProhibited = _closeProhibited.Checked,
            CollapseBlankLines = _collapseBlankLines.Checked,
            CommentPrefixes = commentPrefixes,
            Nickname = SessionProfile.EscapeNickname(_nickname.Text),
            SendLineByLine = _sendLineByLine.Checked,
            UseBracketedPaste = _useBracketedPaste.Checked,
            SelectWorkingDirOnStart = _selectWorkingDirOnStart.Checked,
            ResumeOnStart = _resumeOnStart.Checked,
            PromptNewNameOnCommandAdd = _promptNewNameOnCommandAdd.Checked,
            FontSize = ReadFontSize(_fontSize),
            WindowGeometry = geometry,
            QuickPrompts = ReadQuickPromptsGrid(_quickPrompts),
            AutoSendOnIdle = new AutoSendOnIdleSettings
            {
                Enabled = _autoSendEnabled.Checked,
                Prompt = _autoSendPrompt.Text,
                DelayMs = (int)_autoSendDelaySeconds.Value * 1000,
            },
            ChatRecord = _chatRecord.Checked,
            ChatRecordTailChars = (int)_chatRecordTailChars.Value,
            Stats = _stats.Checked,
        };
    }

    /// <summary>引用符内のスペースは引数区切りとしない簡易パーサ。</summary>
    private static string[] SplitArgsRespectingQuotes(string input)
    {
        if (string.IsNullOrWhiteSpace(input)) return [];
        var result = new List<string>();
        var current = new System.Text.StringBuilder();
        bool inQuotes = false;
        foreach (var ch in input)
        {
            if (ch == '"') { inQuotes = !inQuotes; continue; }
            if (char.IsWhiteSpace(ch) && !inQuotes)
            {
                if (current.Length > 0) { result.Add(current.ToString()); current.Clear(); }
                continue;
            }
            current.Append(ch);
        }
        if (current.Length > 0) result.Add(current.ToString());
        return result.ToArray();
    }

    private static DataGridView BuildGeometryGrid(WindowGeometryEntry[] entries)
    {
        var grid = new DataGridView
        {
            Width = 600,
            Height = 130,
            AllowUserToAddRows = true,
            AllowUserToDeleteRows = true,
            AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
            ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.AutoSize,
            RowHeadersWidth = 40,
            EditMode = DataGridViewEditMode.EditOnEnter,
        };
        AddNameColumn(grid, "Name", "MDI ウィンドウのタイトル (空 = profile 名)");
        AddIntColumn(grid, "X", "MDI 領域相対 X (px)");
        AddIntColumn(grid, "Y", "MDI 領域相対 Y (px)");
        AddIntColumn(grid, "W", "幅 (px)");
        AddIntColumn(grid, "H", "高さ (px)");
        AddWorkingDirectoryColumn(grid);
        FillGeometryGrid(grid, entries);

        // WorkingDirectory セルダブルクリックでフォルダ選択ダイアログを開く
        // (手入力でパスを打つのは負荷が高いため UX 補助)。新規行 (*) でも動く。
        var workingDirColIndex = grid.Columns["WorkingDirectory"]?.Index ?? -1;
        if (workingDirColIndex >= 0)
        {
            grid.CellMouseDoubleClick += (_, e) =>
            {
                if (e.RowIndex < 0 || e.ColumnIndex != workingDirColIndex) return;
                var row = grid.Rows[e.RowIndex];
                var current = row.Cells[workingDirColIndex].Value?.ToString() ?? "";
                string initial;
                try { initial = Environment.ExpandEnvironmentVariables(current); }
                catch { initial = current; }
                if (string.IsNullOrWhiteSpace(initial) || !Directory.Exists(initial))
                    initial = Environment.CurrentDirectory;
                using var fbd = new FolderBrowserDialog
                {
                    Description = "MDI 限定の作業ディレクトリを選択",
                    UseDescriptionForTitle = true,
                    ShowNewFolderButton = true,
                    SelectedPath = initial,
                };
                if (fbd.ShowDialog(grid.FindForm()) == DialogResult.OK)
                {
                    row.Cells[workingDirColIndex].Value = fbd.SelectedPath;
                }
            };
        }

        // 行番号 (= 起動 N 個目の index) を行ヘッダに表示。Rows の追加 / 削除 /
        // 並び替えに追随させる。新規行 (*) は対象外。
        void RenumberHeaders()
        {
            for (int i = 0; i < grid.Rows.Count; i++)
            {
                var row = grid.Rows[i];
                row.HeaderCell.Value = row.IsNewRow ? "" : (i + 1).ToString();
            }
        }
        grid.RowsAdded += (_, _) => RenumberHeaders();
        grid.RowsRemoved += (_, _) => RenumberHeaders();
        RenumberHeaders();
        return grid;
    }

    private static void AddIntColumn(DataGridView grid, string name, string toolTip)
    {
        var col = new DataGridViewTextBoxColumn
        {
            Name = name,
            HeaderText = name,
            ToolTipText = toolTip,
            ValueType = typeof(int),
            // X/Y/W/H は数値で幅が短いので Name 列より相対的に狭くする (FillWeight)。
            FillWeight = 60,
            DefaultCellStyle = new DataGridViewCellStyle
            {
                Alignment = DataGridViewContentAlignment.MiddleRight,
            },
        };
        grid.Columns.Add(col);
    }

    private static void AddNameColumn(DataGridView grid, string name, string toolTip)
    {
        var col = new DataGridViewTextBoxColumn
        {
            Name = name,
            HeaderText = name,
            ToolTipText = toolTip,
            ValueType = typeof(string),
            // タイトル文字列はそれなりの長さになるので Fill 比率を厚めに。
            FillWeight = 200,
        };
        grid.Columns.Add(col);
    }

    /// <summary>
    /// 作業ディレクトリ列。空のときは profile.WorkingDirectory にフォールバック。
    /// 値が入っていると復元起動時に SelectWorkingDirOnStart のフォルダ選択を
    /// スキップし、そのまま渡される。
    /// </summary>
    private static void AddWorkingDirectoryColumn(DataGridView grid)
    {
        var col = new DataGridViewTextBoxColumn
        {
            Name = "WorkingDirectory",
            HeaderText = "WorkingDirectory",
            ToolTipText = "この MDI 限定の作業ディレクトリ (空 = profile 既定)。値があると起動時の cwd 選択ダイアログをスキップ。セルをダブルクリックでフォルダ選択。",
            ValueType = typeof(string),
            FillWeight = 220,
        };
        grid.Columns.Add(col);
    }

    private static void FillGeometryGrid(DataGridView grid, WindowGeometryEntry[] entries)
    {
        grid.Rows.Clear();
        // index 順に並べて GUI 表示。元データに穴 (例 index=1,3) があっても上から
        // 順に詰めて表示し、保存時は行位置で 1..N に振り直す。
        // Name 列は UI で直接編集可。Maximized は UI 列を持たないため Tag に
        // 元エントリを保持して ReadGeometryGrid で復元する。ユーザが行を削除・追加
        // した場合は Tag が無い行は Maximized なしで保存される。
        foreach (var e in entries.OrderBy(e => e.Index))
        {
            var rowIdx = grid.Rows.Add(e.Name ?? "", e.X, e.Y, e.W, e.H, e.WorkingDirectory ?? "");
            grid.Rows[rowIdx].Tag = e;
        }
    }

    private static WindowGeometryEntry[] ReadGeometryGrid(DataGridView grid)
    {
        var result = new List<WindowGeometryEntry>();
        int index = 1;
        foreach (DataGridViewRow row in grid.Rows)
        {
            if (row.IsNewRow) continue;
            // 全セル空の行はスキップ (DataGridView は途中の空行を許容してしまう)。
            // Name 列は空でも残り (X/Y/W/H) があれば有効な行として扱うため、
            // "全セル空" の判定にそのまま全列が含まれている前提のロジックで OK。
            if (row.Cells.Cast<DataGridViewCell>().All(c => c.Value == null || string.IsNullOrWhiteSpace(c.Value.ToString())))
                continue;

            // Name は空でも許容 (= フォールバックで profile 名 + 番号が使われる)。
            var nameCell = row.Cells["Name"].Value?.ToString()?.Trim();
            var name = string.IsNullOrEmpty(nameCell) ? null : nameCell;

            // 座標系は名前のみ行 (W/H = 0) も許容。X/Y/W/H が空なら 0 で埋める
            // (元 UI では全部入力必須にしていたが、Name 列追加で「タイトルだけ
            // 設定したい」ニーズが現実的になったため緩める)。
            var x = ToIntOrDefault(row.Cells["X"].Value);
            var y = ToIntOrDefault(row.Cells["Y"].Value);
            var w = ToIntOrDefault(row.Cells["W"].Value);
            var h = ToIntOrDefault(row.Cells["H"].Value);
            var cwdCell = row.Cells["WorkingDirectory"].Value?.ToString()?.Trim();
            var entry = new WindowGeometryEntry
            {
                Index = index++,
                X = x, Y = y, W = w, H = h,
                Name = name,
                WorkingDirectory = string.IsNullOrEmpty(cwdCell) ? null : cwdCell,
            };
            // Maximized は UI 列を持たないため Tag に保存した元エントリから復元する。
            // 新規行 / 別テンプレ由来で Tag が無いものは null (既定値) になる。
            if (row.Tag is WindowGeometryEntry original)
            {
                entry.Maximized = original.Maximized;
            }
            result.Add(entry);
        }
        return result.ToArray();
    }

    private static int ToIntOrDefault(object? value)
    {
        if (value == null) return 0;
        if (value is int i) return i;
        return int.TryParse(value.ToString(), out var n) ? n : 0;
    }

    private static DataGridView BuildQuickPromptsGrid(QuickPrompt[] entries)
    {
        var grid = new DataGridView
        {
            Width = 480,
            Height = 130,
            AllowUserToAddRows = true,
            AllowUserToDeleteRows = true,
            AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
            ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.AutoSize,
            RowHeadersWidth = 40,
            EditMode = DataGridViewEditMode.EditOnEnter,
        };
        var labelCol = new DataGridViewTextBoxColumn
        {
            Name = "Label",
            HeaderText = "Label",
            ToolTipText = "右クリックメニューに表示する名前 (例: OK / Yes / 続行)",
            ValueType = typeof(string),
            FillWeight = 80,
        };
        grid.Columns.Add(labelCol);
        var promptCol = new DataGridViewTextBoxColumn
        {
            Name = "Prompt",
            HeaderText = "Prompt",
            ToolTipText = "実際に送信する本文 (\\n で改行)",
            ValueType = typeof(string),
            FillWeight = 220,
        };
        grid.Columns.Add(promptCol);
        FillQuickPromptsGrid(grid, entries);
        return grid;
    }

    private static void FillQuickPromptsGrid(DataGridView grid, QuickPrompt[] entries)
    {
        grid.Rows.Clear();
        foreach (var e in entries)
        {
            // DataGridView の textbox セルは生の改行を扱いづらいので \n でエスケープ表示
            var visible = (e.Prompt ?? "").Replace("\r\n", "\n").Replace("\n", "\\n");
            grid.Rows.Add(e.Label ?? "", visible);
        }
    }

    private static QuickPrompt[] ReadQuickPromptsGrid(DataGridView grid)
    {
        var result = new List<QuickPrompt>();
        foreach (DataGridViewRow row in grid.Rows)
        {
            if (row.IsNewRow) continue;
            var label = row.Cells["Label"].Value?.ToString()?.Trim() ?? "";
            var prompt = row.Cells["Prompt"].Value?.ToString() ?? "";
            if (string.IsNullOrEmpty(label) && string.IsNullOrEmpty(prompt)) continue;
            // \n エスケープを実改行に戻す
            var unescaped = prompt.Replace("\\n", "\n");
            result.Add(new QuickPrompt { Label = label, Prompt = unescaped });
        }
        return result.ToArray();
    }

    private static TextBox MakeTextBox(string initial) => new()
    {
        Text = initial,
        Width = 360,
        Margin = new Padding(0, 2, 0, 4),
    };

    /// <summary>
    /// 作業ディレクトリ用 TextBox とフォルダ選択 ([...]) ボタンを横並びに配置した
    /// 複合コントロールを返す。ボタン押下で FolderBrowserDialog を出し、選択結果を
    /// TextBox に書き戻す。TextBox の現在値がフォルダとして実在すればそこを初期表示。
    /// </summary>
    private Control BuildWorkingDirectoryField(TextBox textBox)
    {
        var browse = new Button
        {
            Text = "...",
            Width = 32,
            Height = textBox.PreferredHeight,
            Margin = new Padding(4, 2, 0, 4),
        };
        browse.Click += (_, _) =>
        {
            string initial;
            try
            {
                initial = Environment.ExpandEnvironmentVariables(textBox.Text ?? "");
            }
            catch
            {
                initial = textBox.Text ?? "";
            }
            if (string.IsNullOrWhiteSpace(initial) || !Directory.Exists(initial))
                initial = Environment.CurrentDirectory;
            using var fbd = new FolderBrowserDialog
            {
                Description = "作業ディレクトリを選択",
                UseDescriptionForTitle = true,
                ShowNewFolderButton = true,
                SelectedPath = initial,
            };
            if (fbd.ShowDialog(this) == DialogResult.OK)
            {
                textBox.Text = fbd.SelectedPath;
            }
        };

        // TableLayoutPanel で TextBox が伸縮し、ボタンは固定幅で右端に居続ける。
        var panel = new TableLayoutPanel
        {
            ColumnCount = 2,
            RowCount = 1,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Margin = new Padding(0),
        };
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        panel.Controls.Add(textBox, 0, 0);
        panel.Controls.Add(browse, 1, 0);
        return panel;
    }

    private static void AddRow(TableLayoutPanel layout, string label, Control field)
    {
        var lbl = new Label
        {
            Text = label,
            AutoSize = true,
            TextAlign = ContentAlignment.MiddleRight,
            Margin = new Padding(0, 6, 8, 0),
        };
        layout.Controls.Add(lbl);
        layout.Controls.Add(field);
    }
}
