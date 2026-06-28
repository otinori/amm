using Amm.Core;

namespace Amm.Forms;

/// <summary>
/// システムトレイ常駐アイコン＋入力待ちバルーン通知 (req-20260622-tray-icon)。
/// MdiParentForm が生成・保持し、FormClosing で Dispose する。
/// 全メソッドは UI スレッドから呼ぶこと。
/// </summary>
internal sealed class TrayIconManager : IDisposable
{
    private readonly Form _owner;
    private readonly Action<TerminalChildForm?> _activateChild;
    private readonly Action<TerminalChildForm?> _activateAndMaximize;
    private readonly NotifyIcon _notifyIcon;
    private readonly ToolStripMenuItem _waitingMenu;
    private readonly ToolStripMenuItem _notifyMenuItem;
    private bool _notifyEnabled = true;

    // WaitingForInput な子フォーム → 遷移時刻 (古い順ジャンプ用)
    private readonly Dictionary<TerminalChildForm, DateTime> _waitingChildren = new();
    // セッション別の最終バルーン表示時刻 (5秒 dedup)
    private readonly Dictionary<TerminalChildForm, DateTime> _lastBalloonShownAt = new();
    // 最後にバルーン通知を出した子フォーム (ダブルクリック時の最大化ジャンプ先)
    private TerminalChildForm? _lastBalloonChild;

    private const int BalloonDedupMs = 5000;
    private const int BalloonTimeoutMs = 5000;

    public TrayIconManager(Form owner, Action<TerminalChildForm?> activateChild,
        Action<TerminalChildForm?> activateAndMaximize)
    {
        _owner = owner;
        _activateChild = activateChild;
        _activateAndMaximize = activateAndMaximize;

        var contextMenu = new ContextMenuStrip();

        var showItem = new ToolStripMenuItem("amm を表示");
        showItem.Click += (_, _) => BringToForeground(null);

        _waitingMenu = new ToolStripMenuItem("入力待ちセッション") { Enabled = false };

        _notifyMenuItem = new ToolStripMenuItem("バルーン通知(&N)") { CheckOnClick = true, Checked = true };
        _notifyMenuItem.CheckedChanged += (_, _) => _notifyEnabled = _notifyMenuItem.Checked;

        var exitItem = new ToolStripMenuItem("終了");
        exitItem.Click += (_, _) => Application.Exit();

        contextMenu.Items.Add(showItem);
        contextMenu.Items.Add(_waitingMenu);
        contextMenu.Items.Add(_notifyMenuItem);
        contextMenu.Items.Add(new ToolStripSeparator());
        contextMenu.Items.Add(exitItem);

        _notifyIcon = new NotifyIcon
        {
            Icon = TerminalChildForm.LoadAppIcon(),
            Text = "amm — 起動中",
            Visible = true,
            ContextMenuStrip = contextMenu,
        };
        _notifyIcon.MouseClick += OnMouseClick;
        _notifyIcon.MouseDoubleClick += OnMouseDoubleClick;
        _notifyIcon.BalloonTipClicked += OnBalloonClicked;
    }

    /// <summary>子フォームの WaitState 変化を受け取る。</summary>
    public void OnChildWaitStateChanged(TerminalChildForm child, WaitState state)
    {
        if (state == WaitState.WaitingForInput)
        {
            if (!_waitingChildren.ContainsKey(child))
                _waitingChildren[child] = DateTime.UtcNow;
            MaybeShowBalloon(child);
        }
        else
        {
            _waitingChildren.Remove(child);
        }
        UpdateTooltipAndMenu();
    }

    /// <summary>子フォームが閉じられたとき呼ぶ。</summary>
    public void OnChildClosed(TerminalChildForm child)
    {
        _waitingChildren.Remove(child);
        _lastBalloonShownAt.Remove(child);
        if (ReferenceEquals(_lastBalloonChild, child)) _lastBalloonChild = null;
        UpdateTooltipAndMenu();
    }

    public bool NotifyEnabled
    {
        get => _notifyEnabled;
        set
        {
            _notifyEnabled = value;
            _notifyMenuItem.Checked = value;
        }
    }

    private void MaybeShowBalloon(TerminalChildForm child)
    {
        if (!_notifyEnabled) return;

        // amm がフォアグラウンドなら通知しない
        if (_owner.IsHandleCreated && NativeMethods.GetForegroundWindow() == _owner.Handle) return;

        // 同一セッション 5 秒 dedup
        if (_lastBalloonShownAt.TryGetValue(child, out var last)
            && (DateTime.UtcNow - last).TotalMilliseconds < BalloonDedupMs)
            return;

        _lastBalloonShownAt[child] = DateTime.UtcNow;
        _lastBalloonChild = child;
        _notifyIcon.ShowBalloonTip(BalloonTimeoutMs,
            "amm: 入力待ち",
            $"{child.DisplayName} が入力待ちです",
            ToolTipIcon.Info);
    }

    private void UpdateTooltipAndMenu()
    {
        var count = _waitingChildren.Count;
        var tip = count > 0 ? $"amm — 入力待ち {count} 件" : "amm — 起動中";
        _notifyIcon.Text = tip.Length > 63 ? tip[..63] : tip;

        _waitingMenu.DropDownItems.Clear();
        _waitingMenu.Enabled = count > 0;
        foreach (var (child, _) in _waitingChildren.OrderBy(kv => kv.Value))
        {
            var c = child;
            var item = new ToolStripMenuItem(c.DisplayName);
            item.Click += (_, _) => BringToForeground(c);
            _waitingMenu.DropDownItems.Add(item);
        }
    }

    private void BringToForeground(TerminalChildForm? target)
    {
        if (!_owner.IsHandleCreated) return;
        var hwnd = _owner.Handle;
        NativeMethods.ShowWindow(hwnd, NativeMethods.SW_RESTORE);
        NativeMethods.SetForegroundWindow(hwnd);
        _owner.Activate();

        var jumpTarget = target
            ?? _waitingChildren.OrderBy(kv => kv.Value).FirstOrDefault().Key;
        if (jumpTarget != null && !jumpTarget.IsDisposed)
            _activateChild(jumpTarget);
    }

    private void OnMouseClick(object? sender, MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left)
            BringToForeground(null);
    }

    private void OnMouseDoubleClick(object? sender, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left) return;
        // ダブルクリック直前の 2 回の MouseClick で BringToForeground は既に発火済み。
        // ここでは通知元の MDI を最大化してフォーカスする。
        var target = _lastBalloonChild != null && !_lastBalloonChild.IsDisposed
            ? _lastBalloonChild
            : _waitingChildren.OrderBy(kv => kv.Value).FirstOrDefault().Key;
        _activateAndMaximize(target);
    }

    private void OnBalloonClicked(object? sender, EventArgs e)
        => BringToForeground(null);

    public void Dispose()
    {
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
    }
}
