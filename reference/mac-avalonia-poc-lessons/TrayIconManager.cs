using System.Diagnostics;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using Amm.Desktop.Panes;

namespace Amm.Desktop;

// amm patch: notifications (openspec/changes/mac-desktop-feature-parity).
// Avalonia port of the Windows version's TrayIconManager
// (src/apps/Amm/Forms/TrayIconManager.cs). Not under Core/ - TrayIcon is an
// Avalonia control, same rationale as MainWindow itself living outside
// Core/. Polls TerminalWorkspace.ListParticipants() on a 1s DispatcherTimer
// rather than subscribing to PaneHost.StateNotified (which only fires
// "idle"/"attention", never a "left waiting" transition back to running) -
// see spec.md's scope-down note for why this was chosen over extending that
// event's contract. Scoped down from Windows' balloon-tip-with-click-target
// design: Avalonia's TrayIcon has no notification API at all, and
// UNUserNotificationCenter's click-callback needs Objective-C interop this
// project isn't taking on (spec.md's scope-down note) - notifications here
// are a fire-and-forget `osascript -e 'display notification'` process,
// consumed only for their non-clickable banner; jumping to a waiting pane
// happens via the tray icon's own menu instead.
public sealed class TrayIconManager : IDisposable
{
    private readonly Window _owner;
    private readonly TerminalWorkspace _workspace;
    private readonly TrayIcon _trayIcon;
    private readonly NativeMenuItem _waitingMenuItem;
    private readonly NativeMenuItem _notifyToggleItem;
    private readonly DispatcherTimer _pollTimer;
    private bool _notifyEnabled = true;

    // token -> UTC time it first became waiting/attention (oldest-first jump target)
    private readonly Dictionary<string, DateTime> _waitingSince = new();
    private readonly Dictionary<string, string> _waitingLabels = new();
    // token -> UTC time a notification was last shown for it (5s dedup)
    private readonly Dictionary<string, DateTime> _lastNotifiedAt = new();

    private const int NotifyDedupMs = 5000;
    private const int PollIntervalMs = 1000;

    public TrayIconManager(Window owner, TerminalWorkspace workspace)
    {
        _owner = owner;
        _workspace = workspace;

        var menu = new NativeMenu();

        var showItem = new NativeMenuItem("amm を表示");
        showItem.Click += (_, _) => BringToForeground(null);
        menu.Items.Add(showItem);

        _waitingMenuItem = new NativeMenuItem("入力待ちセッションなし")
        {
            IsEnabled = false,
            Menu = new NativeMenu(),
        };
        menu.Items.Add(_waitingMenuItem);

        menu.Items.Add(new NativeMenuItemSeparator());

        _notifyToggleItem = new NativeMenuItem("通知")
        {
            ToggleType = MenuItemToggleType.CheckBox,
            IsChecked = true,
        };
        _notifyToggleItem.Click += (_, _) =>
        {
            _notifyToggleItem.IsChecked = !_notifyToggleItem.IsChecked;
            _notifyEnabled = _notifyToggleItem.IsChecked;
        };
        menu.Items.Add(_notifyToggleItem);

        menu.Items.Add(new NativeMenuItemSeparator());

        var exitItem = new NativeMenuItem("終了");
        exitItem.Click += (_, _) =>
            (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.Shutdown();
        menu.Items.Add(exitItem);

        _trayIcon = new TrayIcon
        {
            Icon = CreateStatusIcon(),
            ToolTipText = "amm — 起動中",
            IsVisible = true,
            Menu = menu,
        };
        _trayIcon.Clicked += (_, _) => BringToForeground(null);

        // amm patch: TrayIcon.SetIcons(Application, TrayIcons) is the actual
        // registration point - merely constructing a TrayIcon instance and
        // setting IsVisible=true does not attach it to anything (confirmed
        // via reflecting Avalonia.Controls.dll's public API: TrayIcon only
        // exposes this as a static attached-property setter, no instance
        // "Show()").
        if (Application.Current != null)
            TrayIcon.SetIcons(Application.Current, [_trayIcon]);

        _pollTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(PollIntervalMs) };
        _pollTimer.Tick += (_, _) => PollWaitingStates();
        _pollTimer.Start();
    }

    private void PollWaitingStates()
    {
        var current = _workspace.ListParticipants();
        var currentTokens = new HashSet<string>();

        foreach (var p in current)
        {
            var waiting = p.State is "waiting" or "attention";
            if (!waiting) continue;
            currentTokens.Add(p.Token);
            var label = string.IsNullOrEmpty(p.Nickname) ? p.Title : p.Nickname;
            _waitingLabels[p.Token] = label;
            if (!_waitingSince.ContainsKey(p.Token))
            {
                _waitingSince[p.Token] = DateTime.UtcNow;
                MaybeNotify(p.Token, label);
            }
        }

        foreach (var token in _waitingSince.Keys.Where(t => !currentTokens.Contains(t)).ToList())
        {
            _waitingSince.Remove(token);
            _waitingLabels.Remove(token);
        }

        // 既に閉じたペインの dedup 記録も一緒に掃除する (無限に溜まらないように)。
        var allTokens = current.Select(p => p.Token).ToHashSet();
        foreach (var token in _lastNotifiedAt.Keys.Where(t => !allTokens.Contains(t)).ToList())
            _lastNotifiedAt.Remove(token);

        UpdateMenu();
    }

    private void MaybeNotify(string token, string label)
    {
        if (!_notifyEnabled) return;
        // amm がフォアグラウンドなら通知しない。
        if (_owner.IsActive) return;
        if (_lastNotifiedAt.TryGetValue(token, out var last)
            && (DateTime.UtcNow - last).TotalMilliseconds < NotifyDedupMs)
            return;

        _lastNotifiedAt[token] = DateTime.UtcNow;
        ShowMacNotification("amm: 入力待ち", $"{label} が入力待ちです");
    }

    private static void ShowMacNotification(string title, string message)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.OSX)) return;
        try
        {
            var script = $"display notification {EscapeAppleScriptString(message)} with title {EscapeAppleScriptString(title)}";
            using var proc = Process.Start(new ProcessStartInfo("osascript", ["-e", script])
            {
                UseShellExecute = false,
                CreateNoWindow = true,
            });
        }
        catch
        {
            // 通知表示の失敗はペイン本体の動作に影響しない。
        }
    }

    private static string EscapeAppleScriptString(string s) =>
        "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";

    private void UpdateMenu()
    {
        var count = _waitingSince.Count;
        _trayIcon.ToolTipText = count > 0 ? $"amm — 入力待ち {count} 件" : "amm — 起動中";

        _waitingMenuItem.Header = count > 0 ? "入力待ちセッション" : "入力待ちセッションなし";
        _waitingMenuItem.IsEnabled = count > 0;

        var submenu = _waitingMenuItem.Menu!;
        submenu.Items.Clear();
        foreach (var token in _waitingSince.OrderBy(kv => kv.Value).Select(kv => kv.Key))
        {
            var label = _waitingLabels.GetValueOrDefault(token, token);
            var item = new NativeMenuItem(label);
            item.Click += (_, _) => BringToForeground(token);
            submenu.Items.Add(item);
        }
    }

    // amm patch: plain _owner.Activate() alone, and even a WindowState
    // Minimized->restore toggle (the technique NudgeViaWindowStateAsync in
    // MainWindow.axaml.cs proved works for forcing a genuine OS-level window
    // operation on this exact Avalonia/macOS stack), were BOTH confirmed on
    // real hardware (user testing) to fail to steal focus from another
    // frontmost app specifically - jumping to a waiting pane brought amm
    // forward fine when Finder was frontmost, but not when Claude Desktop
    // was. Modern macOS (confirmed Sonoma+) has progressively restricted
    // self-activation via NSApplication APIs regardless of how the request
    // is triggered. `osascript -e 'tell application id "..." to activate'`
    // goes through Apple Events / Launch Services instead of NSApplication
    // self-activation, which macOS treats as a distinctly more privileged
    // activation path (the same reasoning as this class's notification
    // banner already shelling out to osascript rather than using a native
    // API). Falls back to Activate() on non-macOS platforms.
    private void BringToForeground(string? targetToken)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            ActivateViaAppleScript();
        else
            _owner.Activate();

        var jumpToken = targetToken
            ?? _waitingSince.OrderBy(kv => kv.Value).Select(kv => kv.Key).FirstOrDefault();
        if (jumpToken != null)
            _workspace.FocusPaneByToken(jumpToken);
    }

    private static void ActivateViaAppleScript()
    {
        try
        {
            using var proc = Process.Start(new ProcessStartInfo("osascript",
                ["-e", "tell application id \"dev.amm.desktop\" to activate"])
            {
                UseShellExecute = false,
                CreateNoWindow = true,
            });
        }
        catch
        {
            // 前面化の失敗はペイン操作自体には影響しない。
        }
    }

    // amm patch: a small filled circle rendered without any bundled asset
    // file - Amm.Desktop has no icon resources yet (Windows' amm.ico lives
    // under src/apps/Amm/Resources/, a different project/format). Written via
    // Marshal.Write* rather than an `unsafe` pointer block so this doesn't
    // need AllowUnsafeBlocks added to the csproj for one small icon.
    private static WindowIcon CreateStatusIcon()
    {
        const int size = 22;
        var bmp = new WriteableBitmap(new PixelSize(size, size), new Vector(96, 96),
            PixelFormat.Bgra8888, AlphaFormat.Premul);
        using (var fb = bmp.Lock())
        {
            var stride = fb.RowBytes;
            var center = size / 2.0;
            var radius = size / 2.0 - 2;
            for (var y = 0; y < size; y++)
            {
                for (var x = 0; x < size; x++)
                {
                    var dx = x - center + 0.5;
                    var dy = y - center + 0.5;
                    var inside = dx * dx + dy * dy <= radius * radius;
                    var offset = y * stride + x * 4;
                    byte a = inside ? (byte)255 : (byte)0;
                    Marshal.WriteByte(fb.Address, offset + 0, 0);
                    Marshal.WriteByte(fb.Address, offset + 1, 0);
                    Marshal.WriteByte(fb.Address, offset + 2, 0);
                    Marshal.WriteByte(fb.Address, offset + 3, a);
                }
            }
        }
        return new WindowIcon(bmp);
    }

    public void Dispose()
    {
        _pollTimer.Stop();
        _trayIcon.IsVisible = false;
        if (Application.Current != null)
            TrayIcon.SetIcons(Application.Current, []);
        _trayIcon.Dispose();
    }
}
