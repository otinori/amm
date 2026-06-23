using Amm.Core.Mcp.Gateway;

namespace Amm.Core;

/// <summary>
/// MCP ゲートウェイ設定ダイアログ (req-20260622-mcp-gateway)。
/// 「AMM 共通」と「このファイル固有」の 2 グループを並べ、サーバの追加 / 編集 / 削除 /
/// 並べ替えを行う。OK で両リストの変更を確定、キャンセルで破棄。
/// </summary>
public sealed class McpGatewayDialog : Form
{
    // ---- 結果 ----
    public McpServerConfig[] GlobalServers { get; private set; } = [];
    public McpServerConfig[] FileServers { get; private set; } = [];

    // ---- 内部状態 ----
    private readonly List<McpServerConfig> _global;
    private readonly List<McpServerConfig> _file;
    private readonly GatewayManager? _gatewayForStatus;

    // グローバル側
    private readonly ListBox _globalList;
    private readonly Button _globalEdit;
    private readonly Button _globalRemove;
    private readonly Button _globalUp;
    private readonly Button _globalDown;

    // ファイル固有側
    private readonly ListBox _fileList;
    private readonly Button _fileEdit;
    private readonly Button _fileRemove;
    private readonly Button _fileUp;
    private readonly Button _fileDown;

    public McpGatewayDialog(
        McpServerConfig[] globalServers,
        McpServerConfig[] fileServers,
        GatewayManager? gatewayForStatus = null)
    {
        _global = [.. globalServers];
        _file   = [.. fileServers];
        _gatewayForStatus = gatewayForStatus;

        Text = "MCP ゲートウェイ設定";
        FormBorderStyle = FormBorderStyle.Sizable;
        StartPosition = FormStartPosition.CenterParent;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        Font = new Font("Yu Gothic UI", 9F);

        var btnH = Math.Max(28, (SystemFonts.MenuFont?.Height ?? 16) + 12);
        ClientSize = new Size(560, 520 + btnH + 16);
        MinimumSize = new Size(480, 440);

        const int pad = 12;
        const int listH = 160;
        const int btnW = 120;
        const int gap = 8;

        // ---- グローバルグループ ----
        var globalGroup = new GroupBox
        {
            Text = "AMM 共通  (全ワークスペースで読み込まれる)",
            Location = new Point(pad, pad),
            Size = new Size(ClientSize.Width - pad * 2, listH + btnH + 16 + 24),
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
        };

        _globalList = MakeList(globalGroup, listH, btnW + gap);
        (_globalEdit, _globalRemove, _globalUp, _globalDown) = MakeButtons(
            globalGroup, _globalList.Right + gap, _globalList.Top, btnH, btnW,
            () => AddEntry(_global, _globalList),
            () => EditEntry(_global, _globalList),
            () => RemoveEntry(_global, _globalList),
            () => MoveEntry(_global, _globalList, -1),
            () => MoveEntry(_global, _globalList, +1));
        _globalList.SelectedIndexChanged += (_, _) => UpdateButtonStates(_globalList, _globalEdit, _globalRemove, _globalUp, _globalDown);

        // ---- ファイル固有グループ ----
        int fileGroupY = globalGroup.Bottom + gap;
        var fileGroup = new GroupBox
        {
            Text = "このファイル固有  (現在開いている profiles.amm にのみ適用)",
            Location = new Point(pad, fileGroupY),
            Size = new Size(ClientSize.Width - pad * 2, listH + btnH + 16 + 24),
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
        };

        _fileList = MakeList(fileGroup, listH, btnW + gap);
        (_fileEdit, _fileRemove, _fileUp, _fileDown) = MakeButtons(
            fileGroup, _fileList.Right + gap, _fileList.Top, btnH, btnW,
            () => AddEntry(_file, _fileList),
            () => EditEntry(_file, _fileList),
            () => RemoveEntry(_file, _fileList),
            () => MoveEntry(_file, _fileList, -1),
            () => MoveEntry(_file, _fileList, +1));
        _fileList.SelectedIndexChanged += (_, _) => UpdateButtonStates(_fileList, _fileEdit, _fileRemove, _fileUp, _fileDown);

        // ---- 凡例ラベル ----
        var legend = new Label
        {
            Text = "✓ 実行中  ⏳ 起動中  ✗ エラー  ○ 停止  ● 未設定 (ゲートウェイ停止中)",
            AutoSize = true,
            ForeColor = SystemColors.GrayText,
            Font = new Font("Yu Gothic UI", 8F),
        };

        // ---- OK / キャンセル ----
        int bottomY = ClientSize.Height - btnH - pad;
        var ok = new Button
        {
            Text = "OK",
            DialogResult = DialogResult.OK,
            Size = new Size(90, btnH),
            Anchor = AnchorStyles.Bottom | AnchorStyles.Right,
        };
        var cancel = new Button
        {
            Text = "キャンセル",
            DialogResult = DialogResult.Cancel,
            Size = new Size(90, btnH),
            Anchor = AnchorStyles.Bottom | AnchorStyles.Right,
        };
        ok.Click += (_, _) =>
        {
            GlobalServers = [.. _global];
            FileServers = [.. _file];
        };

        Controls.Add(globalGroup);
        Controls.Add(fileGroup);
        Controls.Add(legend);
        Controls.Add(ok);
        Controls.Add(cancel);
        AcceptButton = ok;
        CancelButton = cancel;

        // 動的リサイズ対応
        Resize += (_, _) => LayoutControls(globalGroup, fileGroup, legend, ok, cancel, pad, btnH, gap, listH, btnW);
        LayoutControls(globalGroup, fileGroup, legend, ok, cancel, pad, btnH, gap, listH, btnW);

        RefreshList(_globalList, _global);
        RefreshList(_fileList, _file);
        UpdateButtonStates(_globalList, _globalEdit, _globalRemove, _globalUp, _globalDown);
        UpdateButtonStates(_fileList, _fileEdit, _fileRemove, _fileUp, _fileDown);
    }

    // ---- レイアウト ----

    private void LayoutControls(
        GroupBox globalGroup, GroupBox fileGroup, Label legend,
        Button ok, Button cancel,
        int pad, int btnH, int gap, int listH, int btnW)
    {
        int w = ClientSize.Width;
        int h = ClientSize.Height;
        int groupW = w - pad * 2;
        int groupH = listH + btnH + 16 + 24;

        globalGroup.Location = new Point(pad, pad);
        globalGroup.Size = new Size(groupW, groupH);
        ResizeGroup(_globalList, globalGroup, btnW + gap);

        fileGroup.Location = new Point(pad, globalGroup.Bottom + gap);
        fileGroup.Size = new Size(groupW, groupH);
        ResizeGroup(_fileList, fileGroup, btnW + gap);

        legend.Location = new Point(pad, fileGroup.Bottom + 6);

        int bottomY = h - btnH - pad;
        ok.Location     = new Point(w - pad - 90 - 8 - 90, bottomY);
        cancel.Location = new Point(w - pad - 90, bottomY);
    }

    private static void ResizeGroup(ListBox list, GroupBox group, int rightMargin)
    {
        list.Size = new Size(group.ClientSize.Width - rightMargin - 12 - 8, list.Height);
    }

    // ---- ファクトリ ----

    private static ListBox MakeList(GroupBox parent, int height, int rightMargin)
    {
        var list = new ListBox
        {
            Location = new Point(8, 20),
            Size = new Size(parent.ClientSize.Width - rightMargin - 8 - 8, height),
            IntegralHeight = false,
            Font = new Font("Consolas", 9F),
        };
        parent.Controls.Add(list);
        return list;
    }

    private (Button edit, Button remove, Button up, Button down) MakeButtons(
        GroupBox parent, int x, int y, int btnH, int btnW,
        Action onAdd, Action onEdit, Action onRemove, Action onUp, Action onDown)
    {
        var add    = new Button { Text = "追加(&N)...", Location = new Point(x, y), Size = new Size(btnW, btnH) };
        var edit   = new Button { Text = "編集(&E)...", Location = new Point(x, y + btnH + 6), Size = new Size(btnW, btnH) };
        var remove = new Button { Text = "削除(&R)",   Location = new Point(x, y + (btnH + 6) * 2), Size = new Size(btnW, btnH) };
        var up     = new Button { Text = "↑ 上へ",    Location = new Point(x, y + (btnH + 6) * 3 + 8), Size = new Size(btnW, btnH) };
        var down   = new Button { Text = "↓ 下へ",    Location = new Point(x, y + (btnH + 6) * 4 + 8), Size = new Size(btnW, btnH) };

        add.Click    += (_, _) => onAdd();
        edit.Click   += (_, _) => onEdit();
        remove.Click += (_, _) => onRemove();
        up.Click     += (_, _) => onUp();
        down.Click   += (_, _) => onDown();

        parent.Controls.Add(add);
        parent.Controls.Add(edit);
        parent.Controls.Add(remove);
        parent.Controls.Add(up);
        parent.Controls.Add(down);
        return (edit, remove, up, down);
    }

    // ---- リスト操作 ----

    private void AddEntry(List<McpServerConfig> list, ListBox listBox)
    {
        using var dlg = new McpServerEditDialog();
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        list.Add(dlg.Result);
        RefreshList(listBox, list);
        listBox.SelectedIndex = list.Count - 1;
    }

    private void EditEntry(List<McpServerConfig> list, ListBox listBox)
    {
        int idx = listBox.SelectedIndex;
        if (idx < 0 || idx >= list.Count) return;
        using var dlg = new McpServerEditDialog(list[idx]);
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        list[idx] = dlg.Result;
        RefreshList(listBox, list);
        listBox.SelectedIndex = idx;
    }

    private void RemoveEntry(List<McpServerConfig> list, ListBox listBox)
    {
        int idx = listBox.SelectedIndex;
        if (idx < 0 || idx >= list.Count) return;
        var name = list[idx].Name;
        if (MessageBox.Show(this, $"「{name}」を削除しますか？", "削除確認",
            MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
        list.RemoveAt(idx);
        RefreshList(listBox, list);
        if (list.Count > 0)
            listBox.SelectedIndex = Math.Min(idx, list.Count - 1);
    }

    private void MoveEntry(List<McpServerConfig> list, ListBox listBox, int delta)
    {
        int idx = listBox.SelectedIndex;
        int newIdx = idx + delta;
        if (idx < 0 || newIdx < 0 || newIdx >= list.Count) return;
        (list[idx], list[newIdx]) = (list[newIdx], list[idx]);
        RefreshList(listBox, list);
        listBox.SelectedIndex = newIdx;
    }

    private void RefreshList(ListBox listBox, List<McpServerConfig> list)
    {
        var infos = _gatewayForStatus?.GetServerInfos();
        listBox.Items.Clear();
        foreach (var cfg in list)
        {
            var info = infos?.FirstOrDefault(i =>
                string.Equals(i.Name, cfg.Name, StringComparison.OrdinalIgnoreCase));
            var statusIcon = info == null ? "●" : info.Status switch
            {
                ManagedMcpProcess.ServerStatus.Running  => "✓",
                ManagedMcpProcess.ServerStatus.Starting => "⏳",
                ManagedMcpProcess.ServerStatus.Error    => "✗",
                ManagedMcpProcess.ServerStatus.Stopped  => "○",
                _ => "?",
            };
            var toolCount = info is { Status: ManagedMcpProcess.ServerStatus.Running }
                ? $" ({info.ToolCount} ツール)" : "";
            var args = cfg.Args.Length > 0
                ? " " + string.Join(" ", cfg.Args.Select(a => a.Contains(' ') ? $"\"{a}\"" : a))
                : "";
            listBox.Items.Add($"{statusIcon}  [{cfg.Name}]  {cfg.Command}{args}{toolCount}");
        }
    }

    private static void UpdateButtonStates(
        ListBox list, Button edit, Button remove, Button up, Button down)
    {
        bool hasItem = list.SelectedIndex >= 0;
        bool canUp   = list.SelectedIndex > 0;
        bool canDown = list.SelectedIndex >= 0 && list.SelectedIndex < list.Items.Count - 1;
        edit.Enabled   = hasItem;
        remove.Enabled = hasItem;
        up.Enabled     = canUp;
        down.Enabled   = canDown;
    }
}
