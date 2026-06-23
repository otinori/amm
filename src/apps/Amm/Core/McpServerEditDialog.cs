using Amm.Core.Mcp.Gateway;

namespace Amm.Core;

/// <summary>
/// MCP サーバエントリ 1 件の追加 / 編集ダイアログ (req-20260622-mcp-gateway)。
/// </summary>
public sealed class McpServerEditDialog : Form
{
    private readonly TextBox _nameBox;
    private readonly TextBox _commandBox;
    private readonly TextBox _argsBox;
    private readonly TextBox _envBox;
    private readonly CheckBox _autoStartBox;
    private readonly NumericUpDown _maxRestartsBox;

    public McpServerConfig Result { get; private set; } = new McpServerConfig();

    public McpServerEditDialog(McpServerConfig? existing = null)
    {
        bool isEdit = existing != null;
        Text = isEdit ? "MCP サーバを編集" : "MCP サーバを追加";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        Font = new Font("Yu Gothic UI", 9F);

        var btnH = Math.Max(28, (SystemFonts.MenuFont?.Height ?? 16) + 12);
        ClientSize = new Size(480, 320 + btnH + 16);

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.None,
            Location = new Point(12, 12),
            Size = new Size(ClientSize.Width - 24, ClientSize.Height - btnH - 36),
            ColumnCount = 2,
            RowCount = 7,
            AutoSize = false,
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 108));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        for (int i = 0; i < 6; i++) layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100F)); // env box grows

        _nameBox    = new TextBox { Dock = DockStyle.Fill, Text = existing?.Name ?? "" };
        _commandBox = new TextBox { Dock = DockStyle.Fill, Text = existing?.Command ?? "" };
        _argsBox    = new TextBox { Dock = DockStyle.Fill, Text = JoinArgs(existing?.Args) };
        _envBox = new TextBox
        {
            Dock = DockStyle.Fill,
            Multiline = true,
            ScrollBars = ScrollBars.Vertical,
            Text = JoinEnv(existing?.Env),
            MinimumSize = new Size(0, 60),
        };
        _autoStartBox = new CheckBox
        {
            Text = "amm 起動時に自動起動する",
            AutoSize = true,
            Checked = existing?.AutoStart ?? true,
        };
        _maxRestartsBox = new NumericUpDown
        {
            Minimum = 0,
            Maximum = 99,
            Value = existing?.MaxRestarts ?? 3,
            Width = 60,
        };

        // label / control pairs
        AddRow(layout, 0, "識別名 (*):", _nameBox);
        AddRow(layout, 1, "コマンド (*):", _commandBox);
        AddRow(layout, 2, "引数:", _argsBox);
        AddRow(layout, 3, "環境変数:", _envBox);
        layout.SetRow(_envBox, 3);

        // AutoStart row
        layout.Controls.Add(new Label { Text = "自動起動:", Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft }, 0, 4);
        layout.Controls.Add(_autoStartBox, 1, 4);

        // MaxRestarts row
        var restartPanel = new FlowLayoutPanel { AutoSize = true, WrapContents = false };
        restartPanel.Controls.Add(_maxRestartsBox);
        restartPanel.Controls.Add(new Label
        {
            Text = "回 (0 = 再起動なし)",
            AutoSize = true,
            Anchor = AnchorStyles.Left | AnchorStyles.Top,
            Padding = new Padding(4, 6, 0, 0),
        });
        layout.Controls.Add(new Label { Text = "最大再起動:", Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft }, 0, 5);
        layout.Controls.Add(restartPanel, 1, 5);

        // env hint
        var envHint = new Label
        {
            Text = "※ KEY=VALUE 形式、1行1変数",
            AutoSize = true,
            ForeColor = SystemColors.GrayText,
            Font = new Font("Yu Gothic UI", 8F),
        };
        layout.Controls.Add(new Label(), 0, 6); // placeholder
        layout.Controls.Add(envHint, 1, 6);

        // bottom buttons
        int bottomY = ClientSize.Height - btnH - 12;
        var ok = new Button
        {
            Text = "OK",
            DialogResult = DialogResult.OK,
            Location = new Point(ClientSize.Width - 12 - 90 - 8 - 90, bottomY),
            Size = new Size(90, btnH),
        };
        var cancel = new Button
        {
            Text = "キャンセル",
            DialogResult = DialogResult.Cancel,
            Location = new Point(ClientSize.Width - 12 - 90, bottomY),
            Size = new Size(90, btnH),
        };
        ok.Click += OnOk;

        Controls.Add(layout);
        Controls.Add(ok);
        Controls.Add(cancel);
        AcceptButton = ok;
        CancelButton = cancel;
    }

    private static void AddRow(TableLayoutPanel tbl, int row, string label, Control ctl)
    {
        tbl.Controls.Add(new Label
        {
            Text = label,
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
        }, 0, row);
        tbl.Controls.Add(ctl, 1, row);
    }

    private void OnOk(object? sender, EventArgs e)
    {
        var name = _nameBox.Text.Trim();
        var command = _commandBox.Text.Trim();
        if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(command))
        {
            MessageBox.Show(this, "識別名とコマンドは必須です。", Text,
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            DialogResult = DialogResult.None;
            return;
        }

        Result = new McpServerConfig
        {
            Name        = name,
            Command     = command,
            Args        = SplitArgs(_argsBox.Text),
            Env         = ParseEnv(_envBox.Text),
            AutoStart   = _autoStartBox.Checked,
            MaxRestarts = (int)_maxRestartsBox.Value,
        };
    }

    // ---- helpers ----

    private static string JoinArgs(string[]? args) =>
        args == null || args.Length == 0 ? "" :
        string.Join(" ", args.Select(a => a.Contains(' ') ? $"\"{a}\"" : a));

    internal static string[] SplitArgs(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return [];
        var result = new List<string>();
        var sb = new System.Text.StringBuilder();
        bool inQuote = false;
        foreach (char c in text.Trim())
        {
            if (c == '"') { inQuote = !inQuote; continue; }
            if (c == ' ' && !inQuote)
            {
                if (sb.Length > 0) { result.Add(sb.ToString()); sb.Clear(); }
                continue;
            }
            sb.Append(c);
        }
        if (sb.Length > 0) result.Add(sb.ToString());
        return [.. result];
    }

    private static string JoinEnv(Dictionary<string, string>? env)
    {
        if (env == null || env.Count == 0) return "";
        return string.Join("\r\n", env.Select(kv => $"{kv.Key}={kv.Value}"));
    }

    private static Dictionary<string, string>? ParseEnv(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var dict = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in text.Split('\n'))
        {
            var l = line.Trim().TrimEnd('\r');
            if (string.IsNullOrEmpty(l) || l.StartsWith('#')) continue;
            var eq = l.IndexOf('=');
            if (eq <= 0) continue;
            dict[l[..eq].Trim()] = l[(eq + 1)..];
        }
        return dict.Count > 0 ? dict : null;
    }
}
