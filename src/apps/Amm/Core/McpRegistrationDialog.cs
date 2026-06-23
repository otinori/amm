using Amm.Core.Mcp;

namespace Amm.Core;

/// <summary>
/// AI CLI (Claude Code / Codex / Copilot CLI) のユーザースコープ設定ファイルへ
/// amm の MCP サーバ (amm-mcp.exe) を登録 / 削除する一括ダイアログ。
/// [コマンド] メニュー →「CLI への MCP 登録...」から開く。
///
/// チェック = 登録 / チェック外し = 削除。差分のみ [適用] で書き込む。
/// 登録済みでも command パスが現在の amm-mcp.exe と異なる場合は再登録して
/// パスを更新する (インストール先移動 / publish → MSI 切替の追従)。
/// </summary>
public sealed class McpRegistrationDialog : Form
{
    private sealed record Row(McpCliKind Kind, CheckBox Check, Label Status, bool WasRegistered, string? RegisteredCommand);
    private sealed record HookRow(McpCliKind Kind, CheckBox Check, Label Status, bool WasRegistered, string? RegisteredCommand);

    private readonly List<Row> _rows = new();
    private readonly List<HookRow> _hookRows = new();
    private readonly string _mcpExePath;

    public McpRegistrationDialog()
    {
        _mcpExePath = McpCliRegistrar.ResolveMcpExePath();

        Text = "CLI への MCP / フック登録";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        Font = new Font("Yu Gothic UI", 9F);

        var buttonHeight = Math.Max(28, (SystemFonts.MenuFont?.Height ?? 16) + 12);
        // ボタン帯の所要高さ = 上下 Padding(8+8) + ボタン上下 Margin(4+4) + ボタン高。
        // 以前は +16 でパディング分しか見ておらず、ボタンの Margin 分だけ下端が切れていた。
        var bottomStripHeight = buttonHeight + 24;

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(12),
            ColumnCount = 2,
            RowCount = 9,
            // 高さ見積りが DPI / フォント差で外れても内容に到達できる保険。
            // 正しく算出できていれば通常スクロールバーは出ない。
            AutoScroll = true,
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));

        // 1 行目: 登録する exe パス (存在しない場合は赤字警告)
        var exeExists = File.Exists(_mcpExePath);
        var exeLabel = new Label
        {
            Text = $"amm-mcp.exe: {_mcpExePath}" + (exeExists ? "" : "  (見つかりません)"),
            ForeColor = exeExists ? SystemColors.ControlText : Color.Firebrick,
            AutoSize = true,
            Margin = new Padding(0, 0, 0, 10),
        };
        layout.Controls.Add(exeLabel);
        layout.SetColumnSpan(exeLabel, 2);

        foreach (var kind in new[] { McpCliKind.ClaudeCode, McpCliKind.Codex, McpCliKind.CopilotCli })
        {
            var registeredCommand = McpCliRegistrar.GetRegisteredCommand(kind);
            var registered = registeredCommand != null;

            var check = new CheckBox
            {
                Text = McpCliRegistrar.DisplayName(kind),
                Checked = registered,
                AutoSize = true,
                Margin = new Padding(0, 2, 12, 2),
            };
            var statusText = !registered
                ? "未登録"
                : string.Equals(registeredCommand, _mcpExePath, StringComparison.OrdinalIgnoreCase)
                    ? "登録済"
                    : "登録済 (パスが異なるため適用時に更新)";
            var status = new Label
            {
                Text = $"{ShortenHome(McpCliRegistrar.ConfigPath(kind))}  [{statusText}]",
                ForeColor = registered ? SystemColors.ControlText : SystemColors.GrayText,
                AutoSize = true,
                Margin = new Padding(0, 5, 0, 2),
            };
            layout.Controls.Add(check);
            layout.Controls.Add(status);
            _rows.Add(new Row(kind, check, status, registered, registeredCommand));
        }

        // ---- フック (入力待ち通知 / 許可集約) セクション ----
        // MCP (AI からのツール呼び出し) とは別系統: CLI 自身のイベントを amm へ
        // push し、入力待ち検知をイベント駆動にする (UDR-amm-20260605T0523-7e1)。
        // Claude Code / Copilot CLI は許可集約 (Level 2) も登録する。Codex は
        // notify + OSC9 通知による Level 1 (idle / attention 表示) まで。
        var isFirstHookRow = true;
        foreach (var kind in new[] { McpCliKind.ClaudeCode, McpCliKind.Codex, McpCliKind.CopilotCli })
        {
            var hookCommand = HookCliRegistrar.GetRegisteredCommand(kind);
            var hookRegistered = hookCommand != null;
            var hookCheck = new CheckBox
            {
                Text = $"{HookCliRegistrar.DisplayName(kind)} フック",
                Checked = hookRegistered,
                AutoSize = true,
                Margin = new Padding(0, isFirstHookRow ? 12 : 2, 12, 2),
            };
            var hookStatusText = !hookRegistered
                ? "未登録"
                : string.Equals(hookCommand, _mcpExePath, StringComparison.OrdinalIgnoreCase)
                    ? "登録済"
                    : "登録済 (パスが異なるため適用時に更新)";
            var hookStatus = new Label
            {
                Text = $"{ShortenHome(HookCliRegistrar.ConfigPath(kind))}  [{hookStatusText}]",
                ForeColor = hookRegistered ? SystemColors.ControlText : SystemColors.GrayText,
                AutoSize = true,
                Margin = new Padding(0, isFirstHookRow ? 15 : 5, 0, 2),
            };
            layout.Controls.Add(hookCheck);
            layout.Controls.Add(hookStatus);
            _hookRows.Add(new HookRow(kind, hookCheck, hookStatus, hookRegistered, hookCommand));
            isFirstHookRow = false;
        }

        var hint = new Label
        {
            Text = "チェック = 登録 / チェック外し = 削除。対象はユーザー単位の設定ファイル\n" +
                   "(プロジェクト単位ではありません)。CLI 起動中の変更は次回起動から有効です。\n" +
                   "フック = CLI の応答完了・許可待ちを amm に通知し、入力待ち表示 (●/⚠) を確実に\n" +
                   "します。Claude Code / Copilot は許可要求のポップアップ集約回答にも使われます。",
            AutoSize = true,
            ForeColor = SystemColors.GrayText,
            Margin = new Padding(0, 10, 0, 0),
        };
        layout.Controls.Add(hint);
        layout.SetColumnSpan(hint, 2);

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
        var apply = new Button
        {
            Text = "適用",
            Width = 100,
            Height = buttonHeight,
            Margin = new Padding(4),
        };
        apply.Click += (_, _) => OnApply();
        buttons.Controls.Add(cancel);
        buttons.Controls.Add(apply);

        Controls.Add(layout);
        Controls.Add(buttons);
        AcceptButton = apply;
        CancelButton = cancel;

        // 高さ・幅は内容 (CLI 3 行 + フック行 + ヒント) から自動算出する。
        // 固定値だと行やヒントを追加した時に下が切れる (フック行追加時に実績あり)。
        // 幅を先に確定し、その幅で高さを測ることで複数行ラベル (ヒント 4 行) の
        // 折返し高さを正しく反映する (幅未指定の PreferredSize は高さを過小評価し
        // 下端が切れる原因になっていた)。
        int contentWidth = Math.Max(560, Math.Min(layout.PreferredSize.Width, 920));
        var pref = layout.GetPreferredSize(new Size(contentWidth, 0));
        ClientSize = new Size(
            contentWidth,
            pref.Height + bottomStripHeight + 16); // +16: DPI / フォント差の保険マージン
    }

    private void OnApply()
    {
        var results = new List<string>();
        var hadError = false;

        foreach (var row in _rows)
        {
            var name = McpCliRegistrar.DisplayName(row.Kind);
            try
            {
                if (row.Check.Checked)
                {
                    var needsUpdate = row.WasRegistered &&
                        !string.Equals(row.RegisteredCommand, _mcpExePath, StringComparison.OrdinalIgnoreCase);
                    if (!row.WasRegistered || needsUpdate)
                    {
                        McpCliRegistrar.Register(row.Kind, _mcpExePath);
                        results.Add($"{name}: {(row.WasRegistered ? "パスを更新しました" : "登録しました")}");
                    }
                }
                else if (row.WasRegistered)
                {
                    McpCliRegistrar.Unregister(row.Kind);
                    results.Add($"{name}: 削除しました");
                }
            }
            catch (Exception ex)
            {
                hadError = true;
                results.Add($"{name}: 失敗 — {ex.Message}");
                AppLogger.Error($"MCP registration failed ({name})", ex);
            }
        }

        foreach (var hookRow in _hookRows)
        {
            var name = $"{HookCliRegistrar.DisplayName(hookRow.Kind)} フック";
            try
            {
                if (hookRow.Check.Checked)
                {
                    var needsUpdate = hookRow.WasRegistered &&
                        !string.Equals(hookRow.RegisteredCommand, _mcpExePath, StringComparison.OrdinalIgnoreCase);
                    if (!hookRow.WasRegistered || needsUpdate)
                    {
                        HookCliRegistrar.Register(hookRow.Kind, _mcpExePath);
                        results.Add($"{name}: {(hookRow.WasRegistered ? "パスを更新しました" : "登録しました")}");
                    }
                }
                else if (hookRow.WasRegistered)
                {
                    HookCliRegistrar.Unregister(hookRow.Kind);
                    results.Add($"{name}: 削除しました");
                }
            }
            catch (Exception ex)
            {
                hadError = true;
                results.Add($"{name}: 失敗 — {ex.Message}");
                AppLogger.Error($"Hook registration failed ({name})", ex);
            }
        }

        if (results.Count == 0)
        {
            results.Add("変更はありません。");
        }

        MessageBox.Show(this, string.Join("\n", results), "CLI への MCP / フック登録",
            MessageBoxButtons.OK, hadError ? MessageBoxIcon.Warning : MessageBoxIcon.Information);

        if (!hadError)
        {
            DialogResult = DialogResult.OK;
            Close();
        }
    }

    /// <summary>表示用にユーザープロファイル配下を ~ に短縮。</summary>
    private static string ShortenHome(string path)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return path.StartsWith(home, StringComparison.OrdinalIgnoreCase)
            ? "~" + path[home.Length..]
            : path;
    }
}
