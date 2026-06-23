using System.Text.Json.Nodes;
using Amm.Core.Mcp;

namespace Amm.Forms;

/// <summary>
/// ToolUse 許可要求の集約回答ポップアップ (Approval Hub Level 2)。
///
/// 非モーダル + TopMost + フォーカス非奪取 (WS_EX_NOACTIVATE) — 別アプリで
/// タイピング中に出現してもキーボード入力を奪わず、Enter での誤許可が
/// 構造的に起きない。同じ理由でキーボード操作は受けず、マウスのみで答える。
///
/// 複数要求はウィンドウを増やさず 1 枚に「1/N 件」でキュー表示し、先頭から
/// 順に答える。誤操作ガードとして表示直後 500ms は許可/拒否ボタンを無効化。
/// 判断はすべてイベントで親 (MdiParentForm) へ委譲する dumb view。
/// </summary>
public sealed class ApprovalPopupForm : Form
{
    /// <summary>表示直後にボタンを無効化する誤操作ガード時間。</summary>
    private static readonly TimeSpan ClickGuard = TimeSpan.FromMilliseconds(500);

    public event Action<long>? AllowRequested;
    public event Action<long>? DenyRequested;
    /// <summary>「閉じる」= 表示中の要求を決定なしで解放 (ペイン内プロンプトへ)。</summary>
    public event Action<long>? DismissRequested;
    /// <summary>「ペインへ移動」= 決定なし解放 + 対象ペインをアクティブ化。</summary>
    public event Action<long, string>? JumpRequested;

    private readonly Label _sourceLabel;
    private readonly Label _toolLabel;
    private readonly TextBox _inputBox;
    private readonly Label _countdownLabel;
    private readonly Button _jumpButton;
    private readonly Button _denyButton;
    private readonly Button _allowButton;
    private readonly Button _dismissButton;
    private readonly System.Windows.Forms.Timer _tickTimer;

    private long _displayedId = -1;
    private string _displayedToken = "";
    private DateTime _guardUntilUtc;
    private DateTime _deadlineUtc;

    /// <summary>表示してもフォーカス (アクティブ化) を奪わない。</summary>
    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            // WS_EX_NOACTIVATE: クリックしてもこのウィンドウがアクティブに
            // ならない (= 元のウィンドウのキーボードフォーカスを保ったまま
            // ボタンだけ押せる)。WS_EX_TOPMOST は TopMost プロパティと併せて指定。
            cp.ExStyle |= 0x08000000; // WS_EX_NOACTIVATE
            return cp;
        }
    }

    public ApprovalPopupForm()
    {
        Text = "amm — 許可要求";
        FormBorderStyle = FormBorderStyle.FixedToolWindow;
        ControlBox = false;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        Font = new Font("Yu Gothic UI", 9F);
        ClientSize = new Size(440, 240);

        _sourceLabel = new Label
        {
            Location = new Point(12, 10),
            AutoSize = true,
            Font = new Font("Yu Gothic UI", 10F, FontStyle.Bold),
            ForeColor = Color.FromArgb(180, 90, 0),
        };
        _toolLabel = new Label { Location = new Point(12, 36), AutoSize = true };
        _inputBox = new TextBox
        {
            Location = new Point(12, 60),
            Size = new Size(416, 96),
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Vertical,
            BackColor = SystemColors.Window,
            TabStop = false,
        };
        _countdownLabel = new Label
        {
            Location = new Point(12, 162),
            AutoSize = true,
            ForeColor = SystemColors.GrayText,
        };

        var buttonHeight = Math.Max(30, (SystemFonts.MenuFont?.Height ?? 16) + 14);
        var buttonY = 240 - buttonHeight - 12;
        _jumpButton = new Button
        {
            Text = "ペインへ移動",
            Location = new Point(12, buttonY),
            Size = new Size(110, buttonHeight),
        };
        _dismissButton = new Button
        {
            Text = "閉じる",
            Location = new Point(130, buttonY),
            Size = new Size(70, buttonHeight),
        };
        _denyButton = new Button
        {
            Text = "拒否",
            Location = new Point(248, buttonY),
            Size = new Size(85, buttonHeight),
            ForeColor = Color.Firebrick,
        };
        _allowButton = new Button
        {
            Text = "許可",
            Location = new Point(343, buttonY),
            Size = new Size(85, buttonHeight),
        };

        // 既定ボタンは作らない (AcceptButton 未設定)。NOACTIVATE のため
        // そもそもキーボード入力は届かないが、二重に防御しておく。
        _allowButton.Click += (_, _) => RaiseFor(id => AllowRequested?.Invoke(id));
        _denyButton.Click += (_, _) => RaiseFor(id => DenyRequested?.Invoke(id));
        _dismissButton.Click += (_, _) => RaiseFor(id => DismissRequested?.Invoke(id));
        _jumpButton.Click += (_, _) =>
        {
            var (id, token) = (_displayedId, _displayedToken);
            if (id >= 0) JumpRequested?.Invoke(id, token);
        };

        Controls.AddRange([_sourceLabel, _toolLabel, _inputBox, _countdownLabel,
            _jumpButton, _dismissButton, _denyButton, _allowButton]);

        // 250ms tick: 誤操作ガードの解除 + 残り秒数表示の更新
        _tickTimer = new System.Windows.Forms.Timer { Interval = 250 };
        _tickTimer.Tick += (_, _) => UpdateGuardAndCountdown();
    }

    private void RaiseFor(Action<long> raiser)
    {
        if (_displayedId >= 0) raiser(_displayedId);
    }

    /// <summary>
    /// 台帳スナップショットで表示を更新する (UI thread から呼ぶこと)。
    /// 空なら隠れる。先頭要求が変わったら誤操作ガードをかけ直す。
    /// </summary>
    public void UpdateRequests(IReadOnlyList<(ApprovalBroker.ApprovalRequest Request, string PaneLabel)> items)
    {
        if (items.Count == 0)
        {
            _tickTimer.Stop();
            _displayedId = -1;
            Hide();
            return;
        }

        var (req, label) = items[0];
        if (req.Id != _displayedId)
        {
            _displayedId = req.Id;
            _displayedToken = req.Token;
            _guardUntilUtc = DateTime.UtcNow + ClickGuard;
            _toolLabel.Text = $"ツール: {req.ToolName}";
            _inputBox.Text = SummarizeToolInput(req.ToolInputJson);
            _deadlineUtc = req.DeadlineUtc;
        }
        _sourceLabel.Text = items.Count > 1
            ? $"⚠ {label}  (1/{items.Count} 件)"
            : $"⚠ {label}";

        UpdateGuardAndCountdown();
        _tickTimer.Start();

        if (!Visible)
        {
            PositionBottomRight();
            Show(); // ShowWithoutActivation=true なのでフォーカスは移らない
        }
    }

    private void UpdateGuardAndCountdown()
    {
        var now = DateTime.UtcNow;
        var guardOver = now >= _guardUntilUtc;
        _allowButton.Enabled = guardOver;
        _denyButton.Enabled = guardOver;
        _jumpButton.Enabled = guardOver;
        _dismissButton.Enabled = guardOver;

        var remaining = _deadlineUtc - now;
        _countdownLabel.Text = remaining > TimeSpan.Zero
            ? $"{(int)remaining.TotalSeconds} 秒後にペイン内プロンプトに戻ります"
            : "ペイン内プロンプトに戻ります…";
    }

    private void PositionBottomRight()
    {
        var area = Screen.PrimaryScreen?.WorkingArea
            ?? new Rectangle(0, 0, 1200, 800);
        Location = new Point(area.Right - Width - 16, area.Bottom - Height - 16);
    }

    /// <summary>
    /// tool_input JSON を人が読む形に要約する。Bash 系は command をそのまま、
    /// それ以外は整形 JSON (長すぎる場合は切り詰め)。
    /// </summary>
    internal static string SummarizeToolInput(string toolInputJson)
    {
        const int MaxLen = 1000;
        try
        {
            if (JsonNode.Parse(toolInputJson) is JsonObject obj)
            {
                // Bash / シェル系: command だけ見せるのが最も判断しやすい
                if (obj["command"]?.GetValue<string>() is { Length: > 0 } cmd)
                    return Truncate(cmd, MaxLen);
                if (obj.Count == 0) return "(入力なし)";
                var pretty = obj.ToJsonString(new System.Text.Json.JsonSerializerOptions
                {
                    WriteIndented = true,
                    Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
                });
                return Truncate(pretty, MaxLen);
            }
        }
        catch { /* 解析不能はそのまま見せる */ }
        return Truncate(toolInputJson, MaxLen);
    }

    private static string Truncate(string s, int maxLen) =>
        s.Length <= maxLen ? s : s[..maxLen] + " …(省略)";

    protected override void Dispose(bool disposing)
    {
        if (disposing) _tickTimer.Dispose();
        base.Dispose(disposing);
    }
}
