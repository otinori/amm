using System.Diagnostics;
using System.Reflection;
using Amm.Core;
using Amm.Core.Git;
using Amm.Core.Mcp;
using Amm.Core.Mcp.Gateway;

namespace Amm.Forms;

public partial class MdiParentForm : Form, IMcpHost
{
    private readonly AppLaunchOptions _launchOptions;
    private SessionProfile[] _profiles = [];
    private readonly InputHistory _inputHistory = new();

    // UI Controls
    private MenuStrip _menuStrip = null!;
    private Panel _bottomPanel = null!;
    private Splitter _bottomSplitter = null!;
    private TextBox _inputBox = null!;
    private Button _historyButton = null!;
    private Button _editorButton = null!;
    private Button _alignButton = null!;
    private Label _targetLabel = null!;
    private FlowLayoutPanel _mdiButtonBar = null!;
    private ToolStripMenuItem _commentFilterMenuItem = null!;
    private ToolStripMenuItem _clearAfterSendMenuItem = null!;
    // 入力欄 Ctrl+S 送信の有効/無効 (OFF = エディタの保存癖による誤送信防止)。
    // Ctrl+1..9 / Ctrl+Shift+S は対象外。layout.json に永続化される。
    private ToolStripMenuItem _ctrlSSendMenuItem = null!;
    // 入力欄からの送信に確定改行 (Enter) を同梱するか。OFF = テキストだけ届け、
    // 確定はターミナル側で人間が行う (誤 MDI 送信対策)。layout.json に永続化される。
    private ToolStripMenuItem _sendSubmitEnterMenuItem = null!;
    // ON = MDI 切替のたびに入力欄の内容を MDI ごとに保存/復元する (下書きを
    // 子ウィンドウ単位で持つ)。OFF (既定) = 従来どおり入力欄は MDI 間で共有。
    // モード自体 (ON/OFF) は layout.json に永続化されるが、各 MDI の下書き本文
    // 自体はセッション (TerminalChildForm) と同様プロセス内のみで amm 終了時に失われる。
    private ToolStripMenuItem _perMdiInputDraftMenuItem = null!;
    // _perMdiInputDraftMenuItem が ON のとき、直前にアクティブだった MDI 子。
    // 次のアクティブ化までに入力欄の内容をこの子の DraftInputText へ退避する。
    private TerminalChildForm? _inputDraftActiveChild;
    // エディタ連携でファイル保存 → 送信した直後の動作を 3 択で選ぶ排他メニュー。
    // "Focus" = 対象 MDI をアクティブ化 / "Maximize" = アクティブ化 + 最大化 /
    // "None" = 何もしない。値は layout.json に永続化される。
    private ToolStripMenuItem _editorPostSendFocusMenuItem = null!;
    private ToolStripMenuItem _editorPostSendMaximizeMenuItem = null!;
    private ToolStripMenuItem _editorPostSendNoneMenuItem = null!;
    // 下部パネルの 3 行 (ボタンバー / 入力欄 / 送信先ステータス) を個別に
    // 表示/非表示できるようにするトグル。layout.json に永続化される。
    private ToolStripMenuItem _showButtonBarMenuItem = null!;
    private ToolStripMenuItem _showInputBoxMenuItem = null!;
    private ToolStripMenuItem _showStatusBarMenuItem = null!;
    private TableLayoutPanel _inputPanel = null!;
    // Phase 2 (UDR-amm-20260427T0159-d4e): メニュー再構成のためトップメニュー
    // 自体を field 化し、index ではなく参照で扱う。
    private ToolStripMenuItem _commandMenu = null!;
    private ToolStripMenuItem _viewMenu = null!;
    private ToolStripSeparator _viewMenuSeparator = null!;
    private ToolStripMenuItem _restoreLayoutMenuItem = null!;
    private ToolStripMenuItem _clearLayoutMenuItem = null!;

    // Phase 3 (UDR-amm-20260427T0225-7a3): MCP サーバ
    private MessageQueue? _mcpQueue;
    private MessageDispatcher? _mcpDispatcher;
    private McpPipeServer? _mcpServer;

    // Approval Hub Level 2: ToolUse 許可集約 (PermissionRequest hook → ポップアップ)
    private ApprovalBroker? _approvalBroker;
    private ApprovalPopupForm? _approvalPopup;
    // ポップアップを出すかのトグル (表示メニュー、layout.json 永続化)。
    // OFF = 要求を即時解放してペイン内プロンプトに委ねる (= Level 1 相当)。
    // フック登録解除は CLI 再起動が必要なため、即時に効く GUI 側スイッチを持つ。
    private ToolStripMenuItem _approvalPopupMenuItem = null!;
    // 許可/拒否の決定をステータスバーに一時表示して数秒後に通常表示へ戻す
    private System.Windows.Forms.Timer? _statusRevertTimer;

    // 生成順 (= ボタンバー表示順 / Ctrl+1..9 割当順) を保つ独自リスト。
    // MdiChildren は z-order なのでボタンの並びが不安定になる。
    private readonly List<TerminalChildForm> _childOrder = new();

    // 親ウィンドウのリサイズ/最大化時に MDI 子を再フィットさせる「直近のタイル整列」。
    // タイル系 (Ｚ/縦/横) を実行するとセットされ、SizeChanged のデバウンス後に再適用される。
    // カスケード / 記憶した配置で表示にすると null へ戻り、子の手動配置を尊重する
    // (= 自動再整列しない)。SizeChanged はドラッグ中に連続発火するためタイマでデバウンス。
    private Action? _autoFitLayout;
    private System.Windows.Forms.Timer? _resizeFitTimer;

    // 直近にアクティブだった MDI 子。WebView2 コンテンツ領域にフォーカスが
    // あると MDI の activation chain が完結せず ActiveMdiChild が null を返す
    // ことがある (OpenTerminal 内 child.Activated 保険と同根)。Ctrl+S 送信が
    // 「何も起きない」事故になるため、送信先解決のフォールバックとして保持する。
    private TerminalChildForm? _lastActiveTerminal;

    // 送信時にコメント行 (' または // で始まる行) を除外するモード
    private bool _commentFilterEnabled;

    // 直近の保存/ロード時点での _profiles JSON シリアライズ結果。
    // 終了時の dirty 判定 / 差分提示で baseline として参照する。
    private string? _savedProfilesJson;

    // 直近の保存/ロード時点の _profiles を deep clone したスナップショット。
    // _savedProfilesJson はカノニカル化 (WhenWritingDefault) で defaults を落として
    // しまうため、フィールド初期化子が型デフォルトと異なる bool (例: AutoChcp=true
    // 初期化に対し JSON で false 指定) を roundtrip で復元できず誤検出が起きる。
    // 詳細 diff はこちらのロスレス snapshot を使う。
    private SessionProfile[]? _savedProfilesSnapshot;

    // エディタ連携ブリッジ (テキストエディタで保存するたびに該当子へ送信)
    private readonly List<EditorBridge> _editorBridges = new();

    // システムトレイアイコン (req-20260622-tray-icon)
    private TrayIconManager? _trayManager;

    // MDI ウィンドウ制御 (req-20260622-mdi-window-control)
    // session_id (TerminalChildForm.SessionId) → TerminalChildForm の逆引き辞書
    private readonly Dictionary<string, TerminalChildForm> _sessionMap = new(StringComparer.OrdinalIgnoreCase);
    private WaitBroker? _waitBroker;
    // OpenTerminal の戻り値を IMcpHost.OpenWindow に伝えるための一時保持
    private TerminalChildForm? _lastOpenedChild;

    // MCP ゲートウェイ (req-20260622-mcp-gateway)
    private GatewayManager? _gateway;
    private McpServerConfig[]? _mcpServers;       // profiles.amm から読んだ設定
    private McpServerConfig[]? _mcpServersGlobal; // %LOCALAPPDATA%\amm\mcp-servers.json
    private ToolStripMenuItem _gatewayStatusMenuItem = null!;

    public MdiParentForm(AppLaunchOptions launchOptions)
    {
        _launchOptions = launchOptions;
        IsMdiContainer = true;
        Text = "amm";
        Size = new Size(1400, 900);
        StartPosition = FormStartPosition.CenterScreen;
        Font = new Font("Cascadia Code", 10F, FontStyle.Regular, GraphicsUnit.Point);
        Icon = TerminalChildForm.LoadAppIcon();

        InitializeComponents();
        LoadProfiles();
        _trayManager = new TrayIconManager(this,
            child =>
            {
                if (child == null || child.IsDisposed) return;
                child.Activate();
            },
            child =>
            {
                if (child == null || child.IsDisposed) return;
                child.WindowState = FormWindowState.Maximized;
                child.Activate();
                child.FocusTerminal();
            });
        LoadLayout();
        // LoadLayout 内で _inputHistory.SetMaxEntries (HistoryMaxEntries) を
        // 反映済み。LoadHistory はそれを踏まえた上限で読み込むので順序が重要。
        LoadHistory();
        SetupProfilesWatcher();
        LoadGlobalMcpServers();
        StartMcpServer();
        Shown += OnShown;
        FormClosing += OnFormClosingInternal;

        // 親リサイズ/最大化で MDI 子をタイル再フィット (直近にタイル整列した場合のみ)。
        // SizeChanged はドラッグ中に連続発火するため ~100ms デバウンスして 1 回だけ再適用する。
        _resizeFitTimer = new System.Windows.Forms.Timer { Interval = 100 };
        _resizeFitTimer.Tick += (_, _) =>
        {
            _resizeFitTimer!.Stop();
            if (!IsDisposed && WindowState != FormWindowState.Minimized)
                _autoFitLayout?.Invoke();
        };
        SizeChanged += (_, _) =>
        {
            if (_autoFitLayout == null || _resizeFitTimer == null) return;
            _resizeFitTimer.Stop();
            _resizeFitTimer.Start();
        };
    }

    /// <summary>タイル整列を実行し、以後の親リサイズで同じ整列を再適用するよう記録する。</summary>
    private void ApplyTileLayout(Action tile)
    {
        _autoFitLayout = tile;
        tile();
    }

    // ---- Phase 3: MCP サーバ ----

    private void StartMcpServer()
    {
        _mcpQueue = new MessageQueue(maxPerNickname: 100);
        _mcpDispatcher = new MessageDispatcher(this, _mcpQueue);
        _approvalBroker = new ApprovalBroker();
        _approvalBroker.PendingChanged += OnApprovalPendingChanged;
        _waitBroker = new WaitBroker();

        // MCP ゲートウェイ: グローバル + ファイル固有の結合リストで GatewayManager を起動
        var allGatewayConfigs = (_mcpServersGlobal ?? []).Concat(_mcpServers ?? []).ToArray();
        if (allGatewayConfigs.Length > 0)
        {
            _gateway = new GatewayManager(allGatewayConfigs);
            _ = _gateway.StartAutoStartServersAsync();
            AppLogger.Info($"[gateway] {allGatewayConfigs.Length} server(s) configured " +
                $"(global={(_mcpServersGlobal?.Length ?? 0)}, file={(_mcpServers?.Length ?? 0)})");
        }

        _mcpServer = new McpPipeServer(_mcpDispatcher, approvalBroker: _approvalBroker,
            waitBroker: _waitBroker, gateway: _gateway);
        _mcpServer.Start();
        AppLogger.Info($"[mcp] pipe server started: {_mcpServer.PipeName}");
    }

    // ---- Approval Hub Level 2: ToolUse 許可集約 ----

    private void OnApprovalPendingChanged()
    {
        // 任意スレッド (パイプ/タイマ) から来るので UI thread へ marshal
        try { BeginInvoke(new Action(ProcessApprovalQueue)); }
        catch { /* shutdown 中 */ }
    }

    /// <summary>
    /// 台帳のスナップショットを取り、表示すべき要求とその場で解放すべき要求を
    /// 仕分けてポップアップを更新する。Resolve は PendingChanged を再発火させる
    /// が、本処理は冪等なので収束する。
    /// </summary>
    private void ProcessApprovalQueue()
    {
        if (IsDisposed || _approvalBroker == null) return;
        var pending = _approvalBroker.Snapshot();

        // トグル OFF: すべて決定なし解放 → 各ペイン内プロンプトへ (Level 1 相当)
        if (!_approvalPopupMenuItem.Checked)
        {
            foreach (var r in pending) _approvalBroker.Resolve(r.Id, null);
            _approvalPopup?.Hide();
            return;
        }

        bool ammForeground = IsHandleCreated && NativeMethods.GetForegroundWindow() == Handle;
        var items = new List<(ApprovalBroker.ApprovalRequest Request, string PaneLabel)>();
        foreach (var r in pending)
        {
            var child = _childOrder.FirstOrDefault(c => !c.IsDisposed && c.NotifyToken == r.Token);
            if (child == null)
            {
                // 不明 token (ペインを閉じた直後の遅延 hook 等) → 即解放
                _approvalBroker.Resolve(r.Id, null);
                continue;
            }
            // ユーザーが対象ペインを今まさに見ている (アクティブ + amm 前面) なら
            // ポップアップを介さず即解放し、ペイン内プロンプトに出させる
            if (ammForeground && ReferenceEquals(ActiveMdiChild, child))
            {
                _approvalBroker.Resolve(r.Id, null);
                continue;
            }
            items.Add((r, child.DisplayName));
        }

        if (items.Count == 0)
        {
            _approvalPopup?.Hide();
            return;
        }
        EnsureApprovalPopup().UpdateRequests(items);
        // attention (Level 1) と同様、amm 非フォアグラウンドならタスクバーも点滅
        FlashTaskbarIfBackground();
    }

    private ApprovalPopupForm EnsureApprovalPopup()
    {
        if (_approvalPopup == null || _approvalPopup.IsDisposed)
        {
            _approvalPopup = new ApprovalPopupForm();
            _approvalPopup.AllowRequested += id => ResolveApproval(id, "allow");
            _approvalPopup.DenyRequested += id => ResolveApproval(id, "deny");
            _approvalPopup.DismissRequested += id => ResolveApproval(id, null);
            _approvalPopup.JumpRequested += (id, token) =>
            {
                ResolveApproval(id, null); // 決定なし解放 → ペイン内プロンプトへ
                var child = _childOrder.FirstOrDefault(c => !c.IsDisposed && c.NotifyToken == token);
                child?.FocusTerminal();
            };
        }
        return _approvalPopup;
    }

    /// <summary>ポップアップの回答を台帳へ反映し、決定 (allow/deny) はログと
    /// ステータスバーに記録する (ペイン内にプロンプト痕跡が残らない補い)。</summary>
    private void ResolveApproval(long id, string? decision)
    {
        if (_approvalBroker == null) return;
        var req = _approvalBroker.Snapshot().FirstOrDefault(r => r.Id == id);
        if (!_approvalBroker.Resolve(id, decision)) return; // タイマ/切断と競合 → 先勝ち
        if (req == null || decision == null) return;

        var child = _childOrder.FirstOrDefault(c => !c.IsDisposed && c.NotifyToken == req.Token);
        var label = child?.DisplayName ?? "(closed)";
        var verb = decision == "allow" ? "許可" : "拒否";
        // 機密保護: toolInput (Bash コマンド/ファイル内容/書込ペイロード等に機密が含まれ得る)
        // は app.log に記録しない。ツール名と決定のみ残す。
        AppLogger.Info($"[approval] {label}: {req.ToolName} を{verb}");
        ShowTransientStatus($"⚠ {label}: {req.ToolName} を{verb}しました");
    }

    /// <summary>ステータスバーに一時メッセージを表示し、4 秒後に通常の
    /// 送信先表示へ戻す。</summary>
    private void ShowTransientStatus(string text)
    {
        _targetLabel.Text = text;
        if (_statusRevertTimer == null)
        {
            _statusRevertTimer = new System.Windows.Forms.Timer { Interval = 4000 };
            _statusRevertTimer.Tick += (_, _) =>
            {
                _statusRevertTimer!.Stop();
                UpdateSendTarget();
            };
        }
        _statusRevertTimer.Stop();
        _statusRevertTimer.Start();
    }

    Participant[] IMcpHost.GetParticipants()
    {
        if (InvokeRequired)
            return (Participant[])Invoke(new Func<Participant[]>(() => ((IMcpHost)this).GetParticipants()));

        return _childOrder
            .Where(c => !c.IsDisposed && !string.IsNullOrEmpty(c.Profile.Nickname))
            .Select(c => new Participant
            {
                Nickname = c.Profile.Nickname!,
                ProfileName = c.ProfileName,
                Instance = c.InstanceNumber,
                State = c.CurrentWaitState switch
                {
                    WaitState.Running => "running",
                    WaitState.WaitingForInput => "waiting",
                    WaitState.Stopped => "stopped",
                    _ => "unknown",
                },
                IsWaiting = c.CurrentWaitState == WaitState.WaitingForInput,
                SessionId = c.SessionId,
            })
            .ToArray();
    }

    void IMcpHost.Inject(string nickname, int instance, string message)
    {
        // 入力パネル / Ctrl+S と同じディスパッチ経路に通すことで、profile の
        // UseBracketedPaste / SendLineByLine を尊重する。BeginInvoke + async
        // ラムダにすることで、bracketed paste の Task.Delay 継続 (= 確定 \r
        // 書き込み) が UI thread 上で確実に走る。fire-and-forget の `_ =
        // DispatchSendAsync(...)` 形式だと例外が観測できず、原因切り分けも
        // 困難なため、try/catch + AppLogger で log に残す。
        BeginInvoke(new Action(async () =>
        {
            try
            {
                var target = _childOrder.FirstOrDefault(c =>
                    !c.IsDisposed
                    && string.Equals(c.Profile.Nickname, nickname, StringComparison.OrdinalIgnoreCase)
                    && c.InstanceNumber == instance);
                if (target == null)
                {
                    AppLogger.Warn($"[mcp] inject target not found: {nickname}#{instance}");
                    return;
                }
                AppLogger.Info($"[mcp] inject begin: {nickname}#{instance} ({message.Length} chars, useBracketedPaste={target.Profile.UseBracketedPaste}, sendLineByLine={target.Profile.SendLineByLine})");
                await DispatchSendAsync(target, message);
                AppLogger.Info($"[mcp] inject done: {nickname}#{instance}");
            }
            catch (Exception ex)
            {
                AppLogger.Error("[mcp] inject failed", ex);
            }
        }));
    }

    bool IMcpHost.NotifyChildState(string token, string state)
    {
        // CLI hook 由来の状態通知 (UDR-amm-20260605T0523-7e1)。token は ConPTY
        // 起動時に AMM_NOTIFY_ID として注入した GUID。_childOrder の走査は
        // UI thread に限定する (GetParticipants と同じ規約)。
        if (InvokeRequired)
            return (bool)Invoke(new Func<bool>(() => ((IMcpHost)this).NotifyChildState(token, state)));

        var target = _childOrder.FirstOrDefault(c => !c.IsDisposed && c.NotifyToken == token);
        if (target == null)
        {
            // amm 外の CLI からは届かない設計 (env 不在 → notify が no-op) なので、
            // ここに来るのは「MDI を閉じた直後に hook が遅れて発火」等の正常系。
            AppLogger.Info($"[mcp] notify: no child for token (state={state})");
            return false;
        }
        AppLogger.Info($"[mcp] notify: {target.ProfileName}#{target.InstanceNumber} state={state}");
        target.NotifyExternalWaitState(state);

        // attention (許可・確認待ち) は別アプリ作業中でも気付けるよう、amm が
        // 非フォアグラウンドならタスクバーを点滅させる (UDR-amm-20260605T1043-3af)。
        // FLASHW_TIMERNOFG なので amm を前面にした時点で点滅は自動停止する。
        if (state == "attention")
            FlashTaskbarIfBackground();

        // WaitBroker は McpPipeServer.HandleAmmNotify で既に呼ばれている (二重呼び出しは
        // RemoveAndResolve の先勝ちロジックで無害)。こちらは WaitPatternDetector 経由の
        // 通知パスをカバーするためのフォールバックとして残す。
        // (実際は McpPipeServer 側が先に処理するので通常は no-op になる)
        return true;
    }

    // ---- req-20260622-mdi-window-control ----

    OpenWindowResult IMcpHost.OpenWindow(OpenWindowParams p)
    {
        if (InvokeRequired)
            return (OpenWindowResult)Invoke(new Func<OpenWindowResult>(() => ((IMcpHost)this).OpenWindow(p)));

        try
        {
            _lastOpenedChild = null;
            SessionProfile profile;

            if (!string.IsNullOrEmpty(p.ProfileName))
            {
                // 既存プロファイルを名前で検索してクローン (元プロファイルは変更しない)
                var existing = _profiles.FirstOrDefault(pr =>
                    string.Equals(pr.Name, p.ProfileName, StringComparison.OrdinalIgnoreCase));
                if (existing == null)
                    return new OpenWindowResult { Error = $"profile not found: {p.ProfileName}" };

                var clone = JsonClone(existing);
                if (clone == null)
                    return new OpenWindowResult { Error = "profile clone failed" };

                // MCP 経由の自動起動: ダイアログ系をすべて抑止
                clone.SelectWorkingDirOnStart = false;
                clone.PromptNewNameOnCommandAdd = false;
                // 呼び出し元が明示したフィールドだけ上書き (省略時はプロファイル値を継承)
                if (p.WorkingDirectory != null) clone.WorkingDirectory = p.WorkingDirectory;
                if (p.Title != null) clone.Name = p.Title;
                profile = clone;
            }
            else
            {
                // コマンド指定の一時プロファイル (従来動作)
                profile = new SessionProfile
                {
                    Name = p.Title ?? p.Command ?? "",
                    Executable = p.Command ?? "",
                    Args = p.Args,
                    WorkingDirectory = p.WorkingDirectory ?? "",
                    PromptNewNameOnCommandAdd = false,
                    SelectWorkingDirOnStart = false,
                };
            }

            OpenTerminal(profile);
            var child = _lastOpenedChild;
            _lastOpenedChild = null;
            if (child == null)
                return new OpenWindowResult { Error = "Window creation failed" };
            return new OpenWindowResult { SessionId = child.SessionId };
        }
        catch (Exception ex)
        {
            AppLogger.Error("[mcp] OpenWindow failed", ex);
            return new OpenWindowResult { Error = ex.Message };
        }
    }

    bool IMcpHost.CloseWindow(string sessionId, bool force)
    {
        if (InvokeRequired)
            return (bool)Invoke(new Func<bool>(() => ((IMcpHost)this).CloseWindow(sessionId, force)));

        if (!_sessionMap.TryGetValue(sessionId, out var child) || child.IsDisposed)
            return false;

        if (force)
        {
            // 確認ダイアログなしで強制クローズ
            child.Profile.CloseProhibited = false;
            child.Close();
        }
        else
        {
            child.Close();
        }
        return true;
    }

    string? IMcpHost.GetNotifyTokenBySessionId(string sessionId)
    {
        if (InvokeRequired)
            return (string?)Invoke(new Func<string?>(() => ((IMcpHost)this).GetNotifyTokenBySessionId(sessionId)));

        return _sessionMap.TryGetValue(sessionId, out var child) && !child.IsDisposed
            ? child.NotifyToken
            : null;
    }

    /// <summary>amm が前面でないときだけタスクバーボタンを点滅させる。</summary>
    private void FlashTaskbarIfBackground()
    {
        if (!IsHandleCreated) return;
        if (NativeMethods.GetForegroundWindow() == Handle) return; // 前面なら不要
        var info = new NativeMethods.FLASHWINFO
        {
            cbSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.FLASHWINFO>(),
            hwnd = Handle,
            dwFlags = NativeMethods.FLASHW_TRAY | NativeMethods.FLASHW_TIMERNOFG,
            uCount = 0,
            dwTimeout = 0,
        };
        NativeMethods.FlashWindowEx(ref info);
    }

    private FileSystemWatcher? _profilesWatcher;
    private System.Windows.Forms.Timer? _reloadDebounceTimer;

    private void SetupProfilesWatcher()
    {
        _profilesWatcher?.Dispose();
        _profilesWatcher = null;

        var dir = Path.GetDirectoryName(_launchOptions.ProfilesPath);
        var file = Path.GetFileName(_launchOptions.ProfilesPath);
        if (string.IsNullOrWhiteSpace(dir) || string.IsNullOrWhiteSpace(file)) return;
        if (!File.Exists(Path.Combine(dir, file))) return;

        _profilesWatcher = new FileSystemWatcher(dir, file)
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
            EnableRaisingEvents = true,
        };
        _profilesWatcher.Changed += OnProfilesFileChanged;
        _profilesWatcher.Created += OnProfilesFileChanged;
        _profilesWatcher.Renamed += OnProfilesFileChanged;

        // 300ms デバウンス: 多くのエディタが save-temp-rename を発火させるため
        _reloadDebounceTimer = new System.Windows.Forms.Timer { Interval = 300 };
        _reloadDebounceTimer.Tick += (_, _) =>
        {
            _reloadDebounceTimer!.Stop();
            try { LoadProfiles(); } catch { /* JSON 壊れ等は無視 */ }
        };
    }

    private void OnShown(object? sender, EventArgs e)
    {
        BeginInvoke(() =>
        {
            // 起動時に Shift が押されていれば自動起動しない (Shift+クリックで開いた等)。
            // 確認ダイアログも出さず、空のウィンドウだけ表示する。
            if (_launchOptions.SuppressAutoStart) return;

            // 外部から明示的に開かれた (ダブルクリック / CLI 引数 / ファイル→開く)
            // AMM ファイルは内容が信頼できない可能性があるため、コマンドの自動起動
            // 前にユーザー確認を挟む (悪意ある .amm の自動 RCE 防止)。
            if (!ConfirmAutoStartIfUntrusted()) return;

            // CLI --all は全 profile 各 1 個 (旧挙動)。AMM ファイルの autoStartCount
            // は per-profile の N 個自動起動 (UDR-amm-20260427T0055-2c1)。両方が指定
            // された場合、CLI が優先 (= AutoStartAll true なら autoStartCount 無視)。
            if (_launchOptions.AutoStartAll)
            {
                foreach (var profile in _profiles)
                    OpenTerminal(profile);
                return;
            }
            AutoStartByProfileCount();
        });
    }

    /// <summary>このパスについて自動起動を承認済みか (再確認の抑止用)。</summary>
    private string? _trustedProfilesPath;

    /// <summary>
    /// 外部から明示的に開かれた AMM ファイルの自動起動について、初回のみ確認
    /// ダイアログを出す。既定の profiles.amm (HasExplicitFile=false) や既に承認した
    /// パスは素通し。ユーザーが拒否したら false (= 自動起動しない)。
    /// </summary>
    private bool ConfirmAutoStartIfUntrusted()
    {
        if (!_launchOptions.HasExplicitFile) return true;
        if (string.Equals(_trustedProfilesPath, _launchOptions.ProfilesPath,
                StringComparison.OrdinalIgnoreCase))
            return true;

        // 実際に自動起動されるコマンドを列挙する。
        var cmds = (_launchOptions.AutoStartAll
                ? _profiles.AsEnumerable()
                : _profiles.Where(p => p.AutoStartCount > 0))
            .Select(p => p.CommandLine)
            .Distinct()
            .ToList();

        // 何も自動起動しないなら確認不要 (このパスは承認済み扱いにしておく)。
        if (cmds.Count == 0)
        {
            _trustedProfilesPath = _launchOptions.ProfilesPath;
            return true;
        }

        var shown = cmds.Take(10).Select(c => "  • " + c);
        var list = string.Join("\n", shown);
        if (cmds.Count > 10) list += $"\n  … 他 {cmds.Count - 10} 件";
        var msg =
            "外部から開いた AMM ファイルが、以下のコマンドを自動起動しようとしています。\n\n" +
            list + "\n\n" +
            $"ファイル: {_launchOptions.ProfilesPath}\n\n" +
            "信頼できる提供元のファイルですか? 実行してよければ「はい」を選んでください。";
        var r = MessageBox.Show(this, msg, "AMM — 自動起動の確認",
            MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2);
        if (r == DialogResult.Yes)
        {
            _trustedProfilesPath = _launchOptions.ProfilesPath;
            return true;
        }
        AppLogger.Info("auto-start declined by user for untrusted profiles file");
        return false;
    }

    /// <summary>
    /// 各 profile の autoStartCount に従って MDI 子を起動する。OnShown の初回起動と、
    /// 「ファイル → 開く」での amm 切替時の両方から呼ぶ。
    /// </summary>
    private void AutoStartByProfileCount()
    {
        foreach (var profile in _profiles)
        {
            for (int i = 0; i < profile.AutoStartCount; i++)
                OpenTerminal(profile);
        }
    }

    private void OnProfilesFileChanged(object sender, FileSystemEventArgs e)
    {
        if (IsDisposed || _reloadDebounceTimer == null) return;
        // ワーカースレッドで飛んでくるので UI に marshal
        BeginInvoke(() =>
        {
            _reloadDebounceTimer.Stop();
            _reloadDebounceTimer.Start();
        });
    }

    private void OnFormClosingInternal(object? sender, FormClosingEventArgs e)
    {
        // 実行中の子プロセスがあれば確認してから閉じる。タスクマネージャ等からの
        // 強制終了 (WindowsShutDown / TaskManagerClosing) は確認しない。
        if (e.CloseReason == CloseReason.UserClosing || e.CloseReason == CloseReason.None)
        {
            var running = MdiChildren
                .OfType<TerminalChildForm>()
                .Where(c => c.IsProcessRunning)
                .ToList();
            if (running.Count > 0)
            {
                var names = string.Join("\n  ", running.Select(c => "• " + c.Text));
                var result = MessageBox.Show(
                    this,
                    $"{running.Count} 個のターミナルセッションが実行中です:\n\n  {names}\n\n" +
                    "すべて終了してアプリを閉じますか？",
                    "終了の確認",
                    MessageBoxButtons.YesNo,
                    MessageBoxIcon.Warning,
                    MessageBoxDefaultButton.Button2);
                if (result != DialogResult.Yes)
                {
                    e.Cancel = true;
                    return;
                }
            }

            // _profiles に未保存の変更があれば確認ダイアログ (記憶した配置 / 追加・編集した
            // コマンド等が AMM ファイルに反映されていない場合)。
            if (HasUnsavedProfileChanges(out var diffSummary))
            {
                var msg = "AMM ファイルに保存されていない変更があります:\n\n"
                    + diffSummary + "\n\n保存してから終了しますか?";
                var saveResult = MessageBox.Show(
                    this,
                    msg,
                    "未保存の変更",
                    MessageBoxButtons.YesNoCancel,
                    MessageBoxIcon.Warning,
                    MessageBoxDefaultButton.Button1);
                if (saveResult == DialogResult.Cancel)
                {
                    e.Cancel = true;
                    return;
                }
                if (saveResult == DialogResult.Yes)
                {
                    try
                    {
                        // amm ファイル未オープン時は SaveAs フロー (My Documents 起点)。
                        // ユーザがダイアログでキャンセルした場合は終了処理も中止する。
                        if (!_launchOptions.HasExplicitFile)
                        {
                            OnFileSaveAs();
                            if (!_launchOptions.HasExplicitFile)
                            {
                                e.Cancel = true;
                                return;
                            }
                        }
                        else
                        {
                            SaveProfilesToAmmFile();
                        }
                    }
                    catch (Exception ex)
                    {
                        MessageBox.Show(this,
                            $"AMM ファイル保存に失敗しました:\n{ex.Message}\n\n終了を中止します。",
                            "保存エラー", MessageBoxButtons.OK, MessageBoxIcon.Error);
                        e.Cancel = true;
                        return;
                    }
                }
                // No の場合はそのまま破棄して終了
            }

            // git commit/push ガード: 全子ウィンドウの起動ディレクトリをリポジトリ単位で集約。
            // 子側の OnFormClosing は MdiFormClosing でスキップするのでここで一括処理する。
            var gitDirs = _childOrder
                .Where(c => !c.IsDisposed)
                .Select(c => c.StartupWorkingDirectory)
                .Where(d => !string.IsNullOrEmpty(d))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (GitGuard.CheckAndPromptDirs(this, gitDirs))
            {
                e.Cancel = true;
                return;
            }
        }
        SaveLayout();
        SaveHistory();

        // エディタ連携ブリッジの一括停止 (まだ残っていれば)。
        // Dispose 中に List から自己除去するので逆順でコピーして回す。
        foreach (var b in _editorBridges.ToArray()) b.Dispose();

        // MCP サーバ停止 (UDR-amm-20260427T0225-7a3)
        try { _mcpServer?.DisposeAsync().AsTask().Wait(2000); } catch { }

        // WaitBroker: 保留中の wait を全解放 (amm-mcp 側は "timeout" で返る)
        _waitBroker?.ReleaseAll();

        // MCP ゲートウェイ停止 (req-20260622-mcp-gateway)
        try { _gateway?.DisposeAsync().AsTask().Wait(3000); } catch { }

        // トレイアイコン除去 (幽霊アイコンを残さない)
        _trayManager?.Dispose();
    }

    // ---- レイアウト永続化 ----

    private static string LayoutPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "amm", "layout.json");

    // JSON は拡張しやすい class + 既定値で扱う (positional record だと欠落キーで失敗する)
    private sealed class LayoutState
    {
        public int X { get; set; }
        public int Y { get; set; }
        public int Width { get; set; }
        public int Height { get; set; }
        public int WindowStateCode { get; set; }
        public int BottomPanelHeight { get; set; }
        public bool ClearAfterSend { get; set; } = true;
        public bool CommentFilter { get; set; } = false;
        // 入力欄 Ctrl+S での送信を有効にするか (OFF = 誤送信防止で無視)
        public bool CtrlSSendEnabled { get; set; } = true;
        // 入力欄からの送信に確定改行 (Enter) を同梱するか。false = テキストのみ
        // 送り、確定はターミナル側で人間が行う
        public bool SendSubmitEnter { get; set; } = true;
        // 入力欄を MDI ごとに保持するか (既定 OFF = 従来どおり共有)
        public bool PerMdiInputDraft { get; set; } = false;
        public bool ShowButtonBar { get; set; } = true;
        public bool ShowInputBox { get; set; } = true;
        public bool ShowStatusBar { get; set; } = true;
        // ToolUse 許可要求をポップアップで集約回答するか (Approval Hub Level 2)。
        // false = 要求を即時解放し、従来どおりペイン内プロンプトで回答。
        public bool ApprovalPopupEnabled { get; set; } = true;
        public bool TrayNotifyEnabled { get; set; } = true;
        // エディタ連携で開くアプリの選択。"Associated" = 関連付け / "Notepad" =
        // メモ帳 / "Custom" = ユーザ指定のエディタ。既定は関連付け。
        public string EditorMode { get; set; } = "Associated";
        public string CustomEditorPath { get; set; } = "";
        // エディタ連携保存 → 送信後の動作。"Focus" / "Maximize" / "None"。
        public string EditorPostSendAction { get; set; } = "Focus";
        // 入力履歴 (Ctrl+H / Ctrl+Up/Down) の最大保存件数。終了時に history.json
        // へ最新 N 件を書き出し、起動時に読み込んで復元する。既定 500
        // (InputHistory.DefaultMaxEntries と一致)。layout.json を直接編集して
        // 変更可能。
        public int HistoryMaxEntries { get; set; } = InputHistory.DefaultMaxEntries;
    }

    // 入力履歴永続化ファイル。layout.json と別ファイルに分けることで、容量増大
    // (上限が大きい場合 数百 KB) が layout 読み書きに影響しないようにする。
    private static string HistoryPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "amm", "history.json");

    private sealed class HistoryFile
    {
        // 古い順に並んだエントリ。新しい順にしないのは
        // InputHistory.LoadFromOldestFirst の契約と一致させるため。
        public List<string> Entries { get; set; } = new();
    }

    private string _editorMode = "Associated";
    private string _customEditorPath = "";
    private string _editorPostSendAction = "Focus";

    private void LoadLayout()
    {
        try
        {
            if (!File.Exists(LayoutPath)) return;
            var json = File.ReadAllText(LayoutPath);
            var state = System.Text.Json.JsonSerializer.Deserialize<LayoutState>(json);
            if (state == null) return;

            // サイズ・位置の健全性チェック: 最低サイズ以下 or 全モニタ範囲外なら使わない
            var rect = new Rectangle(state.X, state.Y, state.Width, state.Height);
            if (rect.Width < 400 || rect.Height < 300) return;
            bool onScreen = Screen.AllScreens.Any(s => s.WorkingArea.IntersectsWith(rect));
            if (!onScreen) return;

            StartPosition = FormStartPosition.Manual;
            Location = rect.Location;
            Size = rect.Size;
            WindowState = (FormWindowState)state.WindowStateCode;

            if (state.BottomPanelHeight >= 80 && state.BottomPanelHeight <= 800)
                _bottomPanel.Height = state.BottomPanelHeight;

            // 旧ファイル互換: キー欠落時は既定値
            _clearAfterSendMenuItem.Checked = state.ClearAfterSend;
            _commentFilterMenuItem.Checked = state.CommentFilter;
            _ctrlSSendMenuItem.Checked = state.CtrlSSendEnabled;
            _sendSubmitEnterMenuItem.Checked = state.SendSubmitEnter;
            _perMdiInputDraftMenuItem.Checked = state.PerMdiInputDraft;
            _showButtonBarMenuItem.Checked = state.ShowButtonBar;
            _showInputBoxMenuItem.Checked = state.ShowInputBox;
            _showStatusBarMenuItem.Checked = state.ShowStatusBar;
            _approvalPopupMenuItem.Checked = state.ApprovalPopupEnabled;
            _trayManager!.NotifyEnabled = state.TrayNotifyEnabled;
            ApplyInputPanelRowVisibility();
            _editorMode = string.IsNullOrEmpty(state.EditorMode) ? "Associated" : state.EditorMode;
            _customEditorPath = state.CustomEditorPath ?? "";
            _editorPostSendAction = NormalizeEditorPostSendAction(state.EditorPostSendAction);
            ApplyEditorPostSendActionToMenu();
            // 履歴上限: 0 以下や明らかに異常な値は既定にフォールバック。
            // 旧 layout.json (キー欠落) も既定値で動く。
            int maxHist = state.HistoryMaxEntries > 0 && state.HistoryMaxEntries <= 100_000
                ? state.HistoryMaxEntries
                : InputHistory.DefaultMaxEntries;
            _inputHistory.SetMaxEntries(maxHist);
        }
        catch { /* 読込失敗は既定レイアウトにフォールバック */ }
    }

    private void LoadHistory()
    {
        try
        {
            if (!File.Exists(HistoryPath)) return;
            var json = File.ReadAllText(HistoryPath);
            var file = System.Text.Json.JsonSerializer.Deserialize<HistoryFile>(json);
            if (file?.Entries == null) return;
            _inputHistory.LoadFromOldestFirst(file.Entries);
        }
        catch { /* 履歴読込失敗は空履歴にフォールバック */ }
    }

    private void SaveHistory()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(HistoryPath)!);
            var file = new HistoryFile
            {
                Entries = _inputHistory.SnapshotOldestFirst().ToList(),
            };
            var json = System.Text.Json.JsonSerializer.Serialize(
                file,
                new System.Text.Json.JsonSerializerOptions
                {
                    WriteIndented = false,
                    Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
                });
            File.WriteAllText(HistoryPath, json);
        }
        catch { /* 履歴保存失敗は無視 (次回起動時に空履歴で立ち上がる) */ }
    }

    private void SaveLayout()
    {
        try
        {
            // 最大化・最小化中は RestoreBounds を使って「次回開くときの通常サイズ」を保存
            var bounds = WindowState == FormWindowState.Normal ? Bounds : RestoreBounds;
            var savedState = WindowState == FormWindowState.Minimized
                ? FormWindowState.Normal
                : WindowState;
            // BottomPanelHeight は「入力欄が見えていた時の高さ」を保存。隠した状態
            // のまま終了 → 次回起動 → 再表示 のときに、collapsed 値ではなく元の
            // 高さを復元できるようにする。
            int panelHeightToSave = _inputBox.Visible
                ? _bottomPanel.Height
                : (_bottomPanelHeightWithInput > 0 ? _bottomPanelHeightWithInput : _bottomPanel.Height);

            var state = new LayoutState
            {
                X = bounds.X,
                Y = bounds.Y,
                Width = bounds.Width,
                Height = bounds.Height,
                WindowStateCode = (int)savedState,
                BottomPanelHeight = panelHeightToSave,
                ClearAfterSend = _clearAfterSendMenuItem.Checked,
                CommentFilter = _commentFilterMenuItem.Checked,
                CtrlSSendEnabled = _ctrlSSendMenuItem.Checked,
                SendSubmitEnter = _sendSubmitEnterMenuItem.Checked,
                PerMdiInputDraft = _perMdiInputDraftMenuItem.Checked,
                ShowButtonBar = _showButtonBarMenuItem.Checked,
                ShowInputBox = _showInputBoxMenuItem.Checked,
                ShowStatusBar = _showStatusBarMenuItem.Checked,
                ApprovalPopupEnabled = _approvalPopupMenuItem.Checked,
                TrayNotifyEnabled = _trayManager?.NotifyEnabled ?? true,
                EditorMode = _editorMode,
                CustomEditorPath = _customEditorPath,
                EditorPostSendAction = _editorPostSendAction,
                HistoryMaxEntries = _inputHistory.MaxEntries,
            };

            Directory.CreateDirectory(Path.GetDirectoryName(LayoutPath)!);
            var json = System.Text.Json.JsonSerializer.Serialize(
                state,
                new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(LayoutPath, json);
        }
        catch { /* 保存失敗はクリティカルではない */ }
    }

    // 入力欄が表示されている間の下部パネル高スナップショット。再表示時はこの値に
    // 直接戻す。
    // (旧実装は「現在の panel.Height - inputBox.Height」と「+ inputBox.Height」の
    //  差分計算 + Splitter.MinSize クランプの組合せで行っていたが、クランプで失った
    //  数 px が次の show で戻らず、起動を繰り返すたびに高さが膨らむ不具合があった
    //  ため、純粋な値の保存/復元方式に切り替えた)
    private int _bottomPanelHeightWithInput = 0;

    /// <summary>
    /// 下部パネル内の 3 行 (ボタンバー / 入力欄 / 送信先ステータス) を、設定メニュー
    /// のチェックに従って個別に表示/非表示する。RowStyle の Height を 0 にしないと
    /// Visible=false でも行領域が確保されてしまうため、TableLayoutPanel の RowStyle
    /// 自体を行ごとに切り替える。
    ///
    /// 入力欄のトグルでは、表示されていた高さ分だけ下部パネル全体を縮める/拡げて、
    /// 入力欄を消した後に空白行が残らない / 再表示時にユーザが触っていた高さに戻る、
    /// という挙動にする。
    /// </summary>
    private void ApplyInputPanelRowVisibility()
    {
        if (_inputPanel == null) return;
        var showBar = _showButtonBarMenuItem.Checked;
        var showInput = _showInputBoxMenuItem.Checked;
        var showStatus = _showStatusBarMenuItem.Checked;

        bool wasInputVisible = _inputBox.Visible;

        // 入力欄が見えている間は、現在の下部パネル高をスナップショット。再表示時は
        // この値を復元する (差分計算しないので clamp 由来の高さ膨張バグが起きない)。
        if (wasInputVisible && _bottomPanel.Height > 0)
            _bottomPanelHeightWithInput = _bottomPanel.Height;

        _mdiButtonBar.Visible = showBar;
        _inputBox.Visible = showInput;
        _targetLabel.Visible = showStatus;

        _inputPanel.SuspendLayout();
        try
        {
            _inputPanel.RowStyles[0] = showBar
                ? new RowStyle(SizeType.AutoSize)
                : new RowStyle(SizeType.Absolute, 0F);
            _inputPanel.RowStyles[1] = showInput
                ? new RowStyle(SizeType.Percent, 100F)
                : new RowStyle(SizeType.Absolute, 0F);
            _inputPanel.RowStyles[2] = showStatus
                ? new RowStyle(SizeType.Absolute, 28F)
                : new RowStyle(SizeType.Absolute, 0F);
        }
        finally
        {
            _inputPanel.ResumeLayout();
        }

        if (wasInputVisible && !showInput)
        {
            // 残った行 (バー / ステータス 28) + padding 8 まで縮める。Splitter の
            // MinSize 制約はユーザドラッグのみが対象なので、ここでは下回って良い。
            // ボタンバーは折り返しにより高さが可変なので PreferredSize を用いる。
            int barH = showBar ? Math.Max(36, _mdiButtonBar.PreferredSize.Height) : 0;
            var collapsed = barH + (showStatus ? 28 : 0) + 8;
            _bottomPanel.Height = collapsed;
        }
        else if (!wasInputVisible && showInput && _bottomPanelHeightWithInput > 0)
        {
            _bottomPanel.Height = _bottomPanelHeightWithInput;
        }
    }

    private static string NormalizeEditorPostSendAction(string? raw)
    {
        return raw switch
        {
            "Maximize" => "Maximize",
            "None" => "None",
            _ => "Focus",
        };
    }

    private void ApplyEditorPostSendActionToMenu()
    {
        if (_editorPostSendFocusMenuItem == null) return;
        _editorPostSendFocusMenuItem.Checked = _editorPostSendAction == "Focus";
        _editorPostSendMaximizeMenuItem.Checked = _editorPostSendAction == "Maximize";
        _editorPostSendNoneMenuItem.Checked = _editorPostSendAction == "None";
    }

    private void SetEditorPostSendAction(string value)
    {
        _editorPostSendAction = NormalizeEditorPostSendAction(value);
        ApplyEditorPostSendActionToMenu();
    }

    private void InitializeComponents()
    {
        // Menu bar
        // MDI 子を maximize すると WinForms が子のシステムアイコンを MenuStrip の
        // 先頭に merge し、アイコン画像の高さで AutoSize の行が膨らむ。アイコン
        // 自体はユーザー要望で表示する仕様 (リクエストで非表示→表示に方針変更)
        // のため、AutoSize を切って高さを固定することで行高だけは安定させる。
        // 高さは固定 24 px だと高 DPI 環境 (125%/150%) でメニュー文字の下半分が
        // 切れていたため、システムメニューフォントの行高 + 余白で動的算出する。
        _menuStrip = new MenuStrip
        {
            AutoSize = false,
            ImageScalingSize = new Size(16, 16),
        };
        // SystemFonts.MenuFont は OS の DPI スケールが反映された高さを返す。
        // 余白 12 px (上下 6 px) を足し、最低 28 px は確保する。
        var menuFontHeight = SystemFonts.MenuFont?.Height ?? 16;
        _menuStrip.Height = Math.Max(28, menuFontHeight + 12);

        // ファイル(&F): AMM ファイル入出力に特化
        var fileMenu = new ToolStripMenuItem("ファイル(&F)") { Tag = "own" };
        var fileOpenItem = (ToolStripMenuItem)fileMenu.DropDownItems.Add(
            "AMM を開く(&O)...", null, (_, _) => OnFileOpen());
        fileOpenItem.ToolTipText = "Shift を押しながら開くと autoStartCount による自動起動を抑止します。";
        fileMenu.DropDownItems.Add(new ToolStripSeparator());
        fileMenu.DropDownItems.Add("上書き保存(&S)", null, (_, _) => OnFileSave());
        fileMenu.DropDownItems.Add("名前を付けて保存(&A)...", null, (_, _) => OnFileSaveAs());
        fileMenu.DropDownItems.Add(new ToolStripSeparator());
        fileMenu.DropDownItems.Add("終了(&X)", null, (_, _) => Close());

        // コマンド(&C): profile 起動 + 編集 + エディタ連携設定
        _commandMenu = new ToolStripMenuItem("コマンド(&C)") { Tag = "own" };
        _commandMenu.DropDownOpening += (_, _) => RebuildCommandMenu();

        // 表示(&V): MDI 一覧 + 整列 + 表示パネル切替
        _viewMenu = new ToolStripMenuItem("表示(&V)") { Tag = "own" };
        _viewMenu.DropDownOpening += (_, _) => RefreshViewMenu();
        _viewMenuSeparator = new ToolStripSeparator();
        _viewMenu.DropDownItems.Add(_viewMenuSeparator);
        // タイル Ｚ: 開いた順を左上→右下へ Z 字 (グリッド) 配置。z-order 起点の標準
        // LayoutMdi はアクティブ切替で順番が崩れて不安定なため自前グリッドを使う。
        _viewMenu.DropDownItems.Add("タイル Ｚ(&Z)", null, (_, _) => ApplyTileLayout(() => TileMdiInOpenOrder(MdiLayout.TileVertical)));
        // タイル縦: グリッド整列をやめ、全ウィンドウを 1 列 (フル幅) で縦に並べる。
        // 多数で収まらない場合はオーバーフローさせ MDI 垂直スクロールで送る。
        _viewMenu.DropDownItems.Add("タイル縦(&V)", null, (_, _) => ApplyTileLayout(() => TileMdiLinear(vertical: true)));
        // タイル横: グリッド整列をやめ、全ウィンドウを 1 行 (フル高) で横に並べる。
        _viewMenu.DropDownItems.Add("タイル横(&H)", null, (_, _) => ApplyTileLayout(() => TileMdiLinear(vertical: false)));
        // カスケードは重ね配置。手動配置とみなしリサイズ追従を解除する。
        _viewMenu.DropDownItems.Add("カスケード(&C)", null, (_, _) => { _autoFitLayout = null; LayoutMdi(MdiLayout.Cascade); });
        _viewMenu.DropDownItems.Add(new ToolStripSeparator());

        // 配置の復元 / 記憶 / クリア (ファイル書き出しは「上書き保存」「名前を付けて保存」に委ねる)
        _restoreLayoutMenuItem = new ToolStripMenuItem("記憶した配置で表示(&L)");
        _restoreLayoutMenuItem.Click += (_, _) => OnRestoreMemorizedLayout();
        _viewMenu.DropDownItems.Add(_restoreLayoutMenuItem);
        _viewMenu.DropDownItems.Add("現在の配置を記憶(&M)", null, (_, _) => OnCaptureCurrentLayout());
        _clearLayoutMenuItem = new ToolStripMenuItem("記憶した配置をクリア(&R)");
        _clearLayoutMenuItem.Click += (_, _) => OnClearMemorizedLayout();
        _viewMenu.DropDownItems.Add(_clearLayoutMenuItem);
        _viewMenu.DropDownItems.Add(new ToolStripSeparator());

        _showButtonBarMenuItem = new ToolStripMenuItem("MDI ボタンバーを表示(&B)")
        {
            CheckOnClick = true,
            Checked = true,
        };
        _showButtonBarMenuItem.CheckedChanged += (_, _) => ApplyInputPanelRowVisibility();
        _viewMenu.DropDownItems.Add(_showButtonBarMenuItem);

        _showInputBoxMenuItem = new ToolStripMenuItem("入力欄を表示(&I)")
        {
            CheckOnClick = true,
            Checked = true,
        };
        _showInputBoxMenuItem.CheckedChanged += (_, _) => ApplyInputPanelRowVisibility();
        _viewMenu.DropDownItems.Add(_showInputBoxMenuItem);

        _showStatusBarMenuItem = new ToolStripMenuItem("送信先ステータスを表示(&T)")
        {
            CheckOnClick = true,
            Checked = true,
        };
        _showStatusBarMenuItem.CheckedChanged += (_, _) => ApplyInputPanelRowVisibility();
        _viewMenu.DropDownItems.Add(_showStatusBarMenuItem);

        // MCP ゲートウェイ設定 (req-20260622-mcp-gateway)
        _gatewayStatusMenuItem = new ToolStripMenuItem("MCP ゲートウェイ(&G)...")
        {
            ToolTipText = "外部 MCP サーバの設定と起動状態を管理します。",
        };
        _gatewayStatusMenuItem.Click += (_, _) => ShowGatewaySettingsDialog();
        _viewMenu.DropDownItems.Add(_gatewayStatusMenuItem);

        // Approval Hub Level 2: 許可要求ポップアップのトグル。OFF = 要求を即時
        // 解放してペイン内プロンプトに委ねる (フック登録は解除不要・即時に効く)。
        _approvalPopupMenuItem = new ToolStripMenuItem("許可要求ポップアップ(&P)")
        {
            CheckOnClick = true,
            Checked = true,
            ToolTipText = "Claude Code のツール実行許可をポップアップで集約回答します。\n" +
                          "OFF にすると従来どおり各ペイン内のプロンプトで回答します\n" +
                          "(フック登録の解除は不要、即時に切り替わります)。",
        };
        _approvalPopupMenuItem.CheckedChanged += (_, _) =>
        {
            if (!_approvalPopupMenuItem.Checked)
            {
                _approvalBroker?.ReleaseAll(); // 保留分はペイン内プロンプトへ
                _approvalPopup?.Hide();
            }
        };
        _viewMenu.DropDownItems.Add(_approvalPopupMenuItem);

        // 送信(&S): 送信モード関連のトグルを集約。
        // 全ペイン送信はモードではなく Ctrl+Shift+S の単発操作 (旧 _broadcastMenuItem 廃止)。
        var sendMenu = new ToolStripMenuItem("送信(&S)") { Tag = "own" };

        _ctrlSSendMenuItem = new ToolStripMenuItem("Ctrl+S でプロンプト送信(&S)")
        {
            CheckOnClick = true,
            Checked = true,
            ToolTipText = "OFF にすると入力欄での Ctrl+S を無視します (誤送信防止)。\n" +
                          "Ctrl+1..9 / Ctrl+Shift+S での送信には影響しません。",
        };
        sendMenu.DropDownItems.Add(_ctrlSSendMenuItem);

        _sendSubmitEnterMenuItem = new ToolStripMenuItem("確定改行も一緒に送信(&N)")
        {
            CheckOnClick = true,
            Checked = true,
            ToolTipText = "OFF にすると入力欄からの送信はテキストのみ届き、確定 (Enter) は\n" +
                          "ターミナル側で手動で行います (誤った MDI への送信防止)。",
        };
        sendMenu.DropDownItems.Add(_sendSubmitEnterMenuItem);

        _perMdiInputDraftMenuItem = new ToolStripMenuItem("入力欄をMDIごとに保持(&D)")
        {
            CheckOnClick = true,
            Checked = false,
            ToolTipText = "ON にすると MDI を切り替えるたびに入力欄の内容を MDI ごとに\n" +
                          "保存/復元します。OFF (既定) では従来どおり入力欄は MDI 間で共有されます。\n" +
                          "このON/OFF設定自体は終了後も保存されますが、各MDIの下書き内容は\n" +
                          "amm終了時に失われます。",
        };
        _perMdiInputDraftMenuItem.CheckedChanged += (_, _) =>
        {
            // ON にした瞬間: 現在の入力欄内容を「現在アクティブな子の下書き」として
            // 紐付け直す (OFF だった間の共有テキストを最初の切替で失わないように)。
            _inputDraftActiveChild = ActiveMdiChild as TerminalChildForm;
            if (_perMdiInputDraftMenuItem.Checked && _inputDraftActiveChild != null)
                _inputDraftActiveChild.DraftInputText = _inputBox.Text;
        };
        sendMenu.DropDownItems.Add(_perMdiInputDraftMenuItem);
        sendMenu.DropDownItems.Add(new ToolStripSeparator());

        _clearAfterSendMenuItem = new ToolStripMenuItem("送信後に入力欄をクリア(&L)")
        {
            CheckOnClick = true,
            Checked = true,
        };
        sendMenu.DropDownItems.Add(_clearAfterSendMenuItem);

        _commentFilterMenuItem = new ToolStripMenuItem("コメント行を送信しない(&C) (' または // で始まる行)")
        {
            CheckOnClick = true,
            Checked = false,
        };
        _commentFilterMenuItem.CheckedChanged += (_, _) =>
            _commentFilterEnabled = _commentFilterMenuItem.Checked;
        sendMenu.DropDownItems.Add(_commentFilterMenuItem);

        // エディタ連携保存 → 送信後の動作 (排他 3 択)。RadioCheck で見た目を
        // ラジオに寄せつつ、Click ハンドラ側で他項目の Checked を落として
        // 相互排他を担保する (ToolStripMenuItem はネイティブな radio group を
        // 持たないため)。
        sendMenu.DropDownItems.Add(new ToolStripSeparator());
        var editorPostSendMenu = new ToolStripMenuItem("エディタ連携の送信後(&E)") { Tag = "own" };
        _editorPostSendFocusMenuItem = new ToolStripMenuItem("対象MDIにフォーカス(&F)")
        {
            CheckOnClick = false,
            CheckState = CheckState.Checked,
        };
        _editorPostSendFocusMenuItem.Click += (_, _) => SetEditorPostSendAction("Focus");
        _editorPostSendMaximizeMenuItem = new ToolStripMenuItem("対象MDIを全画面(&M)")
        {
            CheckOnClick = false,
        };
        _editorPostSendMaximizeMenuItem.Click += (_, _) => SetEditorPostSendAction("Maximize");
        _editorPostSendNoneMenuItem = new ToolStripMenuItem("フォーカスはあてない(&N)")
        {
            CheckOnClick = false,
        };
        _editorPostSendNoneMenuItem.Click += (_, _) => SetEditorPostSendAction("None");
        editorPostSendMenu.DropDownItems.Add(_editorPostSendFocusMenuItem);
        editorPostSendMenu.DropDownItems.Add(_editorPostSendMaximizeMenuItem);
        editorPostSendMenu.DropDownItems.Add(_editorPostSendNoneMenuItem);
        sendMenu.DropDownItems.Add(editorPostSendMenu);
        ApplyEditorPostSendActionToMenu();

        var helpMenu = new ToolStripMenuItem("ヘルプ(&H)") { Tag = "own" };
        helpMenu.DropDownItems.Add("バージョン情報(&A)", null, (_, _) => ShowAboutDialog());

        // 順序: ファイル / コマンド / 表示 / 送信 / ヘルプ
        _menuStrip.Items.Add(fileMenu);
        _menuStrip.Items.Add(_commandMenu);
        _menuStrip.Items.Add(_viewMenu);
        _menuStrip.Items.Add(sendMenu);
        _menuStrip.Items.Add(helpMenu);
        // MDI 子最大化時に WinForms が先頭に merge するシステムアイコン項目は
        // OS 既定の SmallIconSize (高 DPI で 20-32 px) で挿入され、MenuStrip
        // の固定 24 px 高からはみ出る。Tag="own" でない (= merge 起源の) 項目
        // を検出して Image を 16×16 へ縮小し、メニューバー高さに収める。
        // ItemAdded 単独だと、子の切替時 (既存 ControlBox 項目の Image 差し替え
        // のみで Items 操作なし) や DPI 変動で Image が後追いセットされるケース
        // を取りこぼす。Layout / Paint でも走査して belt-and-suspenders 化する。
        _menuStrip.ItemAdded += (_, e) => ShrinkMergedSystemIcon(e.Item);
        _menuStrip.Layout += (_, _) => ScanAndShrinkMergedSystemIcons();
        _menuStrip.Paint += (_, _) => ScanAndShrinkMergedSystemIcons();
        MainMenuStrip = _menuStrip;

        // IsMdiContainer=true で自動生成された MdiClient をそのまま残し (Dock=Fill)、
        // その下に Dock=Bottom の入力パネルを重ねる。MdiClient はフォーム直下の
        // 子である必要がある (別コンテナに移すと IsMdiContainer 判定が false に
        // なり MdiParent 設定時に ArgumentException が発生する) ため、ここでは
        // reparent せず Dock のみで共存させる。
        _bottomPanel = new Panel
        {
            Dock = DockStyle.Bottom,
            Height = 180,
        };

        // MDI 領域と下部パネルの間に手動リサイズ用 Splitter を挟む。
        // 掴める箇所が分かりやすいよう 6px の帯 + 標準の SizeNS (上下矢印) カーソル。
        // Splitter 既定は Cursors.HSplit だが、これが環境によって真っ黒な独自
        // カーソルとして表示されるため、他アプリでも一般的な Cursors.SizeNS に
        // 明示切り替えする。
        _bottomSplitter = new Splitter
        {
            Dock = DockStyle.Bottom,
            Height = 6,
            BackColor = SystemColors.ControlDark,
            MinExtra = 120, // MDI 側の最小高
            MinSize = 80,   // _bottomPanel の最小高
            Cursor = Cursors.SizeNS,
        };

        // Input panel (fills _bottomPanel)
        // 行 0: MDI クイック切替ボタンバー (生成順 / Ctrl+1..9, 左端に固定ボタン [履歴] [エディタ連携])
        // 行 1: 入力欄 (全幅)
        // 行 2: 送信先ラベル (全幅)
        _inputPanel = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 3,
            Padding = new Padding(4),
        };
        _inputPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        // 行 0 はボタンバー: AutoSize にしてボタン折り返しに応じて高さを自動調整。
        _inputPanel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        _inputPanel.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
        _inputPanel.RowStyles.Add(new RowStyle(SizeType.Absolute, 28F));

        // ボタン数が多くなった場合 (固定 3 個 + MDI 1 個ずつ) に行内に収まらず、
        // 旧設定の WrapContents=false + AutoScroll=true では水平スクロールバーが
        // 36px 高のセルの内側に出てボタンが垂直方向に切れる ("崩れる") ため、
        // 折り返し + AutoSize に切り替えてセル自体を必要なだけ縦に伸ばす。
        _mdiButtonBar = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = true,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Margin = new Padding(0, 0, 0, 2),
        };

        _inputBox = new NormalizingTextBox
        {
            Dock = DockStyle.Fill,
            Multiline = true,
            ScrollBars = ScrollBars.Vertical,
            Font = new Font("Cascadia Code", 11F),
            // Enter 送信を廃止したので、プレーン Enter は既定の改行挿入に任せる
            AcceptsReturn = true,
            WordWrap = true,
            AllowDrop = true,
        };
        _inputBox.KeyDown += OnInputBoxKeyDown;
        _inputBox.DragEnter += OnInputBoxDragEnter;
        _inputBox.DragDrop += OnInputBoxDragDrop;

        // MDI ボタンバー行の左端に固定配置する固定ボタン 2 つ。
        // RefreshMdiButtonBar は index 0..1 を保持し、以降の動的 MDI ボタンを差し替える。
        _historyButton = new Button
        {
            Text = "履歴 ▼",
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Margin = new Padding(0, 0, 6, 0),
            Padding = new Padding(6, 0, 6, 0),
        };
        _historyButton.Click += OnHistoryButtonClick;
        _mdiButtonBar.Controls.Add(_historyButton);

        _editorButton = new Button
        {
            Text = "エディタ連携 ▼",
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Margin = new Padding(0, 0, 6, 0),
            Padding = new Padding(6, 0, 6, 0),
        };
        _editorButton.Click += OnEditorButtonClick;
        _mdiButtonBar.Controls.Add(_editorButton);

        // 整列ボタン: クリック時の既定動作は「記憶した配置で表示」(profiles に
        // AutoStartCount > 0 または windowGeometry がある場合)、未設定なら
        // 「タイル縦」へフォールバックする。
        _alignButton = new Button
        {
            Text = "整列",
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Margin = new Padding(0, 0, 12, 0),
            Padding = new Padding(6, 0, 6, 0),
        };
        _alignButton.Click += OnAlignButtonClick;
        _mdiButtonBar.Controls.Add(_alignButton);

        _targetLabel = new Label
        {
            Dock = DockStyle.Fill,
            Text = "送信先: (なし)",
            TextAlign = ContentAlignment.MiddleLeft,
            AutoSize = false,
        };

        _inputPanel.Controls.Add(_mdiButtonBar, 0, 0);
        _inputPanel.Controls.Add(_inputBox, 0, 1);
        _inputPanel.Controls.Add(_targetLabel, 0, 2);

        _bottomPanel.Controls.Add(_inputPanel);

        // Controls は Z 順 (後から足した方が高 Z) の逆で dock 処理される = 最後に
        // 足したものが「先に (外側に)」dock する。
        //   ・_menuStrip は Top 側で外側
        //   ・_bottomPanel は Bottom 側で外側 (= 画面下端に貼り付く本体)
        //   ・_bottomSplitter は Bottom 側で内側 (= _bottomPanel の「上」に刺さる帯)
        // なので Add 順は splitter → bottomPanel → menu。こうしないと splitter が
        // _bottomPanel のさらに下 (画面最下端) に貼り付いてしまい、MDI と _bottomPanel
        // の間に掴めるラインが現れない。
        Controls.Add(_bottomSplitter);
        Controls.Add(_bottomPanel);
        Controls.Add(_menuStrip);

        // MDI child events
        MdiChildActivate += OnMdiChildActivate;
    }

    private void LoadProfiles()
    {
        var profilePath = _launchOptions.ProfilesPath;
        try
        {
            var root = SessionProfileLoader.LoadRoot(profilePath);
            _profiles = root.Profiles;
            _mcpServers = root.McpServers;
            AppLogger.Info($"profiles file loaded: {_profiles.Length} profile(s) ({profilePath})");
            if (_mcpServers?.Length > 0)
                AppLogger.Info($"[gateway] {_mcpServers.Length} MCP server(s) configured");
        }
        catch (Exception ex)
        {
            AppLogger.Error($"profiles file load failed: {profilePath}", ex);
            _profiles = SessionProfileLoader.CreateDefaultProfiles();
            MessageBox.Show(
                this,
                $"profiles 設定ファイルの読み込みに失敗したため、既定の CMD プロファイルで起動します。\n\n{ex.Message}",
                "profiles 読み込みエラー",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
        }

        RebuildCommandMenu();
        // 終了時の dirty 判定の baseline をロード時点で固定する。
        _savedProfilesJson = SerializeProfilesToJson();
        _savedProfilesSnapshot = SnapshotProfiles(_profiles);
    }

    // _profiles のロスレス deep clone を作成 (default options なので
    // WhenWritingDefault による defaults スキップが発生しない)。
    private static SessionProfile[] SnapshotProfiles(SessionProfile[] src)
    {
        var result = new SessionProfile[src.Length];
        for (int i = 0; i < src.Length; i++)
        {
            var clone = JsonClone(src[i]);
            result[i] = clone ?? new SessionProfile();
        }
        return result;
    }

    /// <param name="isCommandAdd">
    /// 「コマンド」メニューからユーザが明示的にコマンドを追加した経路で呼ばれた
    /// 場合 true。autoStartCount / --all / 記憶した配置の復元といった自動起動
    /// 経路は false。<see cref="SessionProfile.PromptNewNameOnCommandAdd"/> は
    /// この経路でのみ発動する。
    /// </param>
    private void OpenTerminal(SessionProfile profile, bool isCommandAdd = false)
    {
        // 「コマンド追加時に新しい名前を入力する」: 手動コマンド追加のときだけ
        // 発動する (= "アプリ起動時" の自動経路では発動しない)。
        // SelectWorkingDirOnStart が併設されていればフォルダ選択 → 名前入力
        // の順で訪ね、元プロファイルを clone した「ユーザ固有プロファイル」
        // を _profiles に追加してから、そこから MDI を起動する。
        // クローンの保存は [ファイル → 上書き保存] / [名前を付けて保存] に委ねる。
        // どちらかのダイアログでキャンセルされた場合は MDI 生成も中止し、新規
        // プロファイル追加もしない (= dirty 状態を作らない)。
        if (isCommandAdd && profile.PromptNewNameOnCommandAdd)
        {
            // (a) SelectWorkingDirOnStart が ON のときだけ作業ディレクトリを尋ねる
            //     (両設定は独立; PromptNewNameOnCommandAdd 単独では cwd 既定は
            //     profile.WorkingDirectory を継承する)。
            string? chosenCwd = null;
            if (profile.SelectWorkingDirOnStart)
            {
                string initialDir = profile.ResolveWorkingDirectory() ?? Environment.CurrentDirectory;
                if (string.IsNullOrWhiteSpace(initialDir) || !Directory.Exists(initialDir))
                    initialDir = Environment.CurrentDirectory;
                using var fbd = new FolderBrowserDialog
                {
                    Description = $"「{profile.Name}」の作業ディレクトリを選択",
                    UseDescriptionForTitle = true,
                    ShowNewFolderButton = true,
                    SelectedPath = initialDir,
                };
                if (fbd.ShowDialog(this) != DialogResult.OK) return;
                chosenCwd = fbd.SelectedPath;
            }

            // (b) 名前ダイアログ初期値: cwd を選択した場合はフォルダ basename を
            //     優先 (プロジェクト名と一致するケースが多く、入力負荷が下がる)。
            //     未選択 / ルート選択で basename が空のときは profile.Name に
            //     フォールバック。
            string suggested = profile.Name;
            if (!string.IsNullOrEmpty(chosenCwd))
            {
                var folderName = Path.GetFileName(chosenCwd.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                if (!string.IsNullOrEmpty(folderName)) suggested = folderName;
            }
            var newName = PromptNewCommandName(profile.Name, suggested);
            if (newName == null) return; // キャンセル / 無効入力で中止

            // (c) clone を作成。新プロファイルは「個別保存可能なユーザ固有
            //     コマンド」として扱うので、再度 PromptNewNameOnCommandAdd /
            //     SelectWorkingDirOnStart が発動しないように両方 OFF にし、cwd
            //     を選択していれば clone 側に焼き込む。WindowGeometry は元
            //     profile から引き継がない (= 新規 profile として geometry は
            //     ゼロから)。
            var clone = JsonClone(profile);
            if (clone == null) return;
            clone.Name = newName;
            if (!string.IsNullOrEmpty(chosenCwd)) clone.WorkingDirectory = chosenCwd;
            clone.PromptNewNameOnCommandAdd = false;
            clone.SelectWorkingDirOnStart = false;
            clone.AutoStartCount = 0;
            clone.WindowGeometry = [];
            // Nickname (MCP 受信名) は新コマンド名から付与する。テンプレの nickname
            // (例: "claude") をそのまま引き継ぐと send_message recipient=claude
            // mode=first で派生コマンドが候補に混入してしまうため、一意な新名前を
            // MCP 安全な形へエスケープして使う ('#'・空白等は '_' に正規化)。
            clone.Nickname = SessionProfile.EscapeNickname(newName);

            var list = new List<SessionProfile>(_profiles) { clone };
            _profiles = list.ToArray();
            RebuildCommandMenu();
            // 以降は新プロファイル基準で MDI を起動する。SelectWorkingDirOnStart は
            // OFF にしてあるためフォルダ選択ダイアログは再表示されない。
            profile = clone;
        }

        var instanceNumber = AllocateInstanceNumber(profile.Name);

        // windowGeometry は「同 profile の生存数 + 1」を index として参照する。
        // 全閉じ後の再起動で 1 にリセットされ、穴埋めはしない仕様
        // (UDR-amm-20260427T0055-2c1)。
        var aliveOfProfile = _childOrder.Count(c => c.ProfileName == profile.Name);
        var oneBasedIndex = aliveOfProfile + 1;
        var hasGeometry = profile.TryGetGeometryForIndex(oneBasedIndex, out var geometry);
        var hasSavedName = profile.TryGetNameForIndex(oneBasedIndex, out var savedName);
        var savedEntry = profile.WindowGeometry?.FirstOrDefault(e => e.Index == oneBasedIndex);
        var savedMaximized = savedEntry?.Maximized == true;
        var hasSavedCwd = profile.TryGetWorkingDirectoryForIndex(oneBasedIndex, out var savedCwd);

        // 起動時に作業ディレクトリを毎回選択させるモード (per-command 設定)。
        // キャンセルされたら起動を中止する。選択結果はインスタンス毎の上書きで、
        // profile 自身の WorkingDirectory は変更しない。
        // 「現在の配置を記憶」で WindowGeometryEntry.WorkingDirectory が保存されて
        // いる場合は、そちらを優先しダイアログをスキップする (記憶した配置を
        // そのまま再現する目的)。
        string? overrideWorkingDir = null;
        if (hasSavedCwd)
        {
            overrideWorkingDir = savedCwd;
        }
        else if (profile.SelectWorkingDirOnStart)
        {
            // 初期表示フォルダ: profile.WorkingDirectory (環境変数展開後) が
            // 存在すればそれ、未設定や不存在ならカレントディレクトリ。env 変数
            // 展開失敗 / 削除済みパスでダイアログがルートにフォールバックする
            // 挙動を抑えるため、Directory.Exists で fail-safe する。
            var initialDir = profile.ResolveWorkingDirectory();
            if (string.IsNullOrWhiteSpace(initialDir) || !Directory.Exists(initialDir))
                initialDir = Environment.CurrentDirectory;
            using var fbd = new FolderBrowserDialog
            {
                Description = $"「{profile.Name}」の作業ディレクトリを選択",
                UseDescriptionForTitle = true,
                ShowNewFolderButton = true,
                SelectedPath = initialDir,
            };
            if (fbd.ShowDialog(this) != DialogResult.OK) return;
            overrideWorkingDir = fbd.SelectedPath;
        }

        var child = new TerminalChildForm(profile, instanceNumber)
        {
            MdiParent = this,
            OverrideWorkingDirectory = overrideWorkingDir,
        };
        child.WaitStateChanged += OnChildWaitStateChanged;
        child.FormClosed += OnChildFormClosed;
        // MdiChildActivate は親フォームのイベントで、通常はこれで十分だが、
        // 子側の WebView2 コンテンツ領域クリックで MDI の activation chain が
        // 完結しないケースがある。各子の Activated を直接フックして保険にする。
        child.Activated += (_, _) =>
        {
            _lastActiveTerminal = child;
            ApplyInputDraftSwitch(child);
            UpdateSendTarget();
        };
        child.AmmSettingsRequested += OnAmmSettingsRequested;
        child.EditorPathCopyRequested += OnEditorPathCopyRequested;
        child.EditorLinkRequested += OnEditorLinkRequested;
        child.QuickPromptRequested += async (target, prompt) =>
        {
            // ターミナル本体右クリックメニューからのクイック送信。MDI 切替バー側
            // (BuildMdiButtonContextMenuStrip) と同じ DispatchSendAsync 経路で
            // ConPTY に流し、送信後はターミナルへフォーカスを戻す。
            if (target.IsDisposed || !target.IsProcessRunning) return;
            await DispatchSendAsync(target, prompt);
            target.FocusTerminal();
        };
        child.QuickSendRegisterRequested += (target, initialLabel, initialPrompt) =>
        {
            using var dlg = new QuickSendRegisterDialog(initialLabel, initialPrompt);
            if (dlg.ShowDialog(this) != DialogResult.OK) return;
            var label = dlg.ResultLabel;
            var prompt = dlg.ResultPrompt;
            if (string.IsNullOrEmpty(prompt)) return;

            var profile = target.Profile;
            var existing = profile.QuickPrompts ?? Array.Empty<QuickPrompt>();
            profile.QuickPrompts = [.. existing, new QuickPrompt { Label = label, Prompt = prompt }];
            SaveProfilesToAmmFile();
        };
        child.SendInputRequested += async target =>
        {
            // ターミナル本体右クリックメニュー「プロンプト送信」。MDI 切替バー側
            // と同じく入力欄の現在内容を SendInputToTargetAsync 経由で送る。
            if (target.IsDisposed) return;
            await SendInputToTargetAsync(target);
        };
        child.DisplayNameChanged += _ =>
        {
            // 名前変更は in-memory のみ。AMM ファイルへの永続化は座標と同じく
            // [表示 → 現在の配置を記憶] で WindowGeometryEntry に取り込まれ、
            // [ファイル → 上書き保存] で書き戻される。リネーム単独で WindowGeometry
            // を更新すると「記憶した配置で表示」が誤って有効化される問題を避ける。
            RefreshMdiButtonBar();
            UpdateSendTarget();
        };

        if (hasGeometry)
        {
            // 既定の Maximized を回避するため Show 前に Normal + Manual を確定。
            child.StartPosition = FormStartPosition.Manual;
            child.WindowState = FormWindowState.Normal;
            child.Location = geometry.Location;
            child.Size = geometry.Size;
        }
        child.Show();
        if (hasSavedName)
        {
            // Show 後にセットすることで、初回タイトル設定 (FormatTitle) が
            // CustomDisplayName 入りで再描画される。Show 前だと FormatTitle が
            // _profile.Name にフォールバックする経路を通ってしまう。
            child.ApplyCustomDisplayName(savedName);
        }
        if (savedMaximized)
        {
            // 「最大化中に名前付きで記憶」した子の復元。geometry を持たない
            // name-only エントリでも最大化状態を再現する。
            child.WindowState = FormWindowState.Maximized;
        }
        // 新規子を _childOrder に追加してから (タイル時に末尾として確定的に
        // 配置されるよう) タイルへ進む。元実装は LayoutMdi を呼んだ後に
        // _childOrder に追加していたが、TileMdiInOpenOrder は _childOrder
        // を参照するので順序を入れ替える。
        _childOrder.Add(child);
        // session_id → child の逆引き (mdi/close・mdi/wait_state で使用)
        _sessionMap[child.SessionId] = child;
        _lastOpenedChild = child;
        if (!hasGeometry && !savedMaximized)
        {
            // 「記憶した配置で表示」が利用可能 (= いずれかの profile に
            // AutoStartCount > 0 または WindowGeometry が登録されている) なら、
            // 新規 MDI は既定サイズ (WinForms 自動配置) のまま据え置く。利用
            // 不可なら都度タイル縦 (開いた順) で並べ直す。
            bool memorizedAvailable = _profiles.Any(p =>
                p.AutoStartCount > 0 || (p.WindowGeometry?.Length ?? 0) > 0);
            if (!memorizedAvailable)
            {
                // MDI が 1 つだけ (= 初回起動の 1 つめ) なら全画面 (最大化)、
                // 複数あるならタイル縦で並べる。
                if (CountAliveChildren() == 1)
                    child.WindowState = FormWindowState.Maximized;
                else
                    TileMdiInOpenOrder(MdiLayout.TileVertical);
            }
        }
        RefreshMdiButtonBar();
        UpdateSendTarget();
    }

    /// <summary>
    /// 「コマンド」メニューからの追加時、PromptNewNameOnCommandAdd=true の
    /// プロファイル用にユーザへ新しいコマンド名を尋ねる。OK で空白 / 既存名と
    /// 衝突した場合は MessageBox を出して null を返す (= 中止)。
    /// </summary>
    /// <param name="sourceName">元プロファイル名 (ダイアログのラベル文言に利用)</param>
    /// <param name="suggested">TextBox の初期値</param>
    /// <returns>確定された新名前 (空でない、既存と衝突しない)。キャンセル / 無効入力なら null</returns>
    private string? PromptNewCommandName(string sourceName, string suggested)
    {
        var buttonHeight = Math.Max(28, (SystemFonts.MenuFont?.Height ?? 16) + 12);
        var buttonY = 80;
        using var dlg = new Form
        {
            Text = "新しい名前でコマンドを追加",
            FormBorderStyle = FormBorderStyle.FixedDialog,
            StartPosition = FormStartPosition.CenterParent,
            ClientSize = new Size(380, buttonY + buttonHeight + 16),
            MinimizeBox = false,
            MaximizeBox = false,
            ShowInTaskbar = false,
        };
        var lbl = new Label
        {
            Text = $"「{sourceName}」を元に新しいコマンドを追加します。\n新コマンド名:",
            Location = new Point(12, 8),
            AutoSize = true,
        };
        var tb = new TextBox
        {
            Location = new Point(12, 50),
            Width = 352,
            Text = suggested,
        };
        var ok = new Button
        {
            Text = "OK",
            DialogResult = DialogResult.OK,
            Location = new Point(200, buttonY),
            Width = 80,
            Height = buttonHeight,
        };
        var cancel = new Button
        {
            Text = "キャンセル",
            DialogResult = DialogResult.Cancel,
            Location = new Point(284, buttonY),
            Width = 80,
            Height = buttonHeight,
        };
        dlg.Controls.Add(lbl);
        dlg.Controls.Add(tb);
        dlg.Controls.Add(ok);
        dlg.Controls.Add(cancel);
        dlg.AcceptButton = ok;
        dlg.CancelButton = cancel;
        // ShowDialog 直後にフォーカス + 全選択を確実にする。ここで呼んでも Form が
        // 未 Show のため Selection 状態が反映されないことがあるため、Shown で
        // 改めて適用 (タイミング依存の地雷を踏まないように)。
        dlg.Shown += (_, _) =>
        {
            tb.Focus();
            tb.SelectAll();
        };

        if (dlg.ShowDialog(this) != DialogResult.OK) return null;

        var name = tb.Text.Trim();
        if (string.IsNullOrEmpty(name))
        {
            MessageBox.Show(this, "コマンド名を入力してください。", "新しい名前でコマンドを追加",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return null;
        }
        if (_profiles.Any(p => string.Equals(p.Name, name, StringComparison.Ordinal)))
        {
            MessageBox.Show(this, $"「{name}」は既に存在します。別の名前を指定してください。",
                "新しい名前でコマンドを追加", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return null;
        }
        return name;
    }

    /// <summary>
    /// 同一プロファイル名の既存 MDI 子から使用中番号を集め、最小未使用を払い出す。
    /// 閉じた番号は再利用される (例: 1,2,3 のうち 2 を閉じたら次は 2)。
    /// </summary>
    private int AllocateInstanceNumber(string profileName)
    {
        var used = MdiChildren
            .OfType<TerminalChildForm>()
            .Where(c => c.ProfileName == profileName)
            .Select(c => c.InstanceNumber)
            .ToHashSet();
        int n = 1;
        while (used.Contains(n)) n++;
        return n;
    }

    private void OnMdiChildActivate(object? sender, EventArgs e)
    {
        ApplyInputDraftSwitch();

        if (ActiveMdiChild is TerminalChildForm activated)
            _lastActiveTerminal = activated;
        RefreshMdiButtonBar();
        UpdateSendTarget();

        // ペインのアクティブ化 = 「回答方法をペイン内に切替」。保留中の許可要求を
        // 決定なしで解放し、ペイン内プロンプトを表示させる (Approval Hub Level 2)。
        if (ActiveMdiChild is TerminalChildForm active && _approvalBroker != null)
            _approvalBroker.ReleaseByToken(active.NotifyToken);
    }

    /// <summary>
    /// 「入力欄をMDIごとに保持」モード時、MDI 切替の前後で入力欄の内容を
    /// 子ウィンドウごとの DraftInputText と同期する。OFF の間は何もしない
    /// (= 従来どおり入力欄は共有のまま)。
    /// WebView2 コンテンツ領域クリックで activation chain が完結せず
    /// ActiveMdiChild が更新されないケースがあるため (child.Activated の保険と同根)、
    /// 呼び出し側で確実な対象が分かる場合は <paramref name="next"/> で明示する。
    /// </summary>
    private void ApplyInputDraftSwitch(TerminalChildForm? next = null)
    {
        if (!_perMdiInputDraftMenuItem.Checked) return;
        next ??= ActiveMdiChild as TerminalChildForm;
        if (ReferenceEquals(_inputDraftActiveChild, next)) return;

        if (_inputDraftActiveChild is { IsDisposed: false } prev)
            prev.DraftInputText = _inputBox.Text;

        _inputDraftActiveChild = next;
        _inputBox.Text = next?.DraftInputText ?? "";
        _inputBox.SelectionStart = _inputBox.Text.Length;
    }

    private void OnChildWaitStateChanged(TerminalChildForm child, WaitState state)
    {
        // 常に UpdateSendTarget を呼ぶ。state 変化を起こした子と ActiveMdiChild が
        // 一致するかは UpdateSendTarget 側で (現在の ActiveMdiChild を読んで)
        // 再計算されるので取りこぼしがない。
        // 以前は child == ActiveMdiChild でガードしていたが、メニュー経由で
        // 子を生成した直後は ActiveMdiChild の確定タイミングが state 変化より
        // 後になり、起動初期の Unknown→Running→WaitingForInput の遷移を
        // UI が受け取れず、薄黄のままステイする不具合があった。
        RefreshMdiButtonBar();
        UpdateSendTarget();
        _trayManager?.OnChildWaitStateChanged(child, state);

        // Phase 3: 入力待ち遷移で MCP キューを flush
        if (state == WaitState.WaitingForInput
            && _mcpDispatcher != null
            && !string.IsNullOrEmpty(child.Profile.Nickname))
        {
            try { _mcpDispatcher.FlushQueue(child.Profile.Nickname!); }
            catch (Exception ex) { AppLogger.Error("[mcp] flush queue failed", ex); }
        }

        // WaitBroker: WaitPatternDetector による入力待ち検知でも "idle" wait を解放
        // (amm/notify が来ない場合のフォールバック)
        if (state == WaitState.WaitingForInput)
            _waitBroker?.ResolveByToken(child.NotifyToken, "idle");
    }

    private void OnChildFormClosed(object? sender, FormClosedEventArgs e)
    {
        if (sender is TerminalChildForm closed)
        {
            _childOrder.Remove(closed);
            _sessionMap.Remove(closed.SessionId);
            if (ReferenceEquals(_lastActiveTerminal, closed))
                _lastActiveTerminal = null; // 閉じた子へのフォールバック送信を防ぐ
            if (ReferenceEquals(_inputDraftActiveChild, closed))
                _inputDraftActiveChild = null; // 破棄済み子への下書き書き戻しを防ぐ
            // 閉じたペインの許可要求は決定なしで解放 (幽霊要求の防止)
            _approvalBroker?.ReleaseByToken(closed.NotifyToken);
            // WaitBroker: 閉じたセッションの pending wait を timeout 扱いで解放
            _waitBroker?.ReleaseBySession(closed.SessionId);
            _trayManager?.OnChildClosed(closed);
        }
        BeginInvoke(() =>
        {
            // 複数から 1 つに減ったら、残った MDI を全画面 (最大化) にする。
            MaximizeIfSingleChild();
            RefreshMdiButtonBar();
            UpdateSendTarget();
        });
    }

    /// <summary>
    /// 子のシステムメニュー「AMM 設定…」が選ばれたとき呼ばれる。
    /// ダイアログで 3 項目編集 → 該当 profile に直接反映 (同 profile 全 MDI に効く)。
    /// profiles.amm への書き戻しは [ファイル] → [上書き保存] が担う
    /// (UDR-amm-20260427T0055-2c1 / 機能重複の整理)。
    /// </summary>
    private void OnAmmSettingsRequested(TerminalChildForm child)
    {
        var profile = child.Profile;
        using var dlg = new AmmSettingsDialog(profile.Name, profile);
        if (dlg.ShowDialog(this) != DialogResult.OK) return;

        profile.CloseProhibited = dlg.CloseProhibited;
        profile.CollapseBlankLines = dlg.CollapseBlankLines;
        profile.CommentPrefixes = dlg.CommentPrefixes;
    }

    /// <summary>
    /// システムメニュー「エディタ連携」: 該当子用の bridge をなければ作って起動する。
    /// 既に bridge がある場合は何もしない (= 既存の編集中ファイルが優先)。
    /// </summary>
    private void OnEditorLinkRequested(TerminalChildForm child)
    {
        var existing = _editorBridges.FirstOrDefault(b => ReferenceEquals(b.Target, child) && b.IsActive);
        if (existing != null) return;
        try
        {
            var bridge = EditorBridge.CreateAndLaunch(this, child);
            _editorBridges.Add(bridge);
        }
        catch (Exception ex)
        {
            AppLogger.Error("editor bridge launch failed (from sysmenu link)", ex);
            MessageBox.Show(
                child,
                $"エディタの起動に失敗しました:\n{ex.Message}",
                "エディタ連携",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
        }
    }

    // ---- Phase 4 (UDR-amm-20260427T0238-fb5): エディタ連携ファイルパスコピー ----

    private void OnEditorPathCopyRequested(TerminalChildForm child)
    {
        // 1 アクションで「エディタ連携起動 + パスコピー」を完結させる。
        // すでに該当 MDI 用の bridge があればそれを再利用、無ければ新規起動して
        // 設定 (関連付け / メモ帳 / カスタム) に従いエディタを開く。最後に必ず
        // ファイルパスをクリップボードへ。
        var bridge = _editorBridges.FirstOrDefault(b => ReferenceEquals(b.Target, child) && b.IsActive);
        if (bridge == null)
        {
            try
            {
                bridge = EditorBridge.CreateAndLaunch(this, child);
                _editorBridges.Add(bridge);
            }
            catch (Exception ex)
            {
                AppLogger.Error("editor bridge launch failed (from sysmenu copy)", ex);
                MessageBox.Show(
                    child,
                    $"エディタの起動に失敗しました:\n{ex.Message}",
                    "エディタ連携",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
                return;
            }
        }

        try
        {
            Clipboard.SetText(bridge.FilePath);
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                child,
                $"クリップボード操作に失敗しました:\n{ex.Message}",
                "エディタ連携ファイルパスをコピー",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
        }
    }

    // ---- Phase 2 (UDR-amm-20260427T0159-d4e): ファイル / コマンド / 表示 メニュー ----

    /// <summary>
    /// コモンダイアログ (開く / 名前を付けて保存) の初期フォルダを
    /// <see cref="DialogPaths"/> の優先順位で解決する。
    /// </summary>
    private string ResolveDialogInitialDirectory()
        => DialogPaths.ResolveInitialDirectory(
            _launchOptions.HasExplicitFile ? _launchOptions.ProfilesPath : null,
            _launchOptions.StartupCurrentDirectory,
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            Environment.GetEnvironmentVariable);

    private void OnFileOpen()
    {
        using var dlg = new OpenFileDialog
        {
            Title = "AMM ファイルを開く",
            Filter = "AMM ファイル (*.amm;*.json)|*.amm;*.json|AMM ファイル (*.amm)|*.amm|JSON ファイル (*.json)|*.json|全て (*.*)|*.*",
            FileName = Path.GetFileName(_launchOptions.ProfilesPath),
            InitialDirectory = ResolveDialogInitialDirectory(),
            CheckFileExists = true,
        };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;

        // 既存 MDI 子は影響させない (watcher のホットリロードと同方針)。
        // 以降の「コマンド」メニュー起動・上書き保存は新パスへ向く。
        _launchOptions.ProfilesPath = dlg.FileName;
        _launchOptions.HasExplicitFile = true;
        LoadProfiles();
        SetupProfilesWatcher();
        AppLogger.Info($"profiles file switched: {dlg.FileName}");

        // Shift キー押下中は自動起動を抑止 ("読み込むだけ" の用途。コマンドメニュー
        // からは通常通り起動できる)。ダイアログを閉じた直後のキー状態を見るので、
        // ユーザは「ファイル → 開く ... → ファイル選択 → OK」の間 Shift を押し続ける。
        bool suppressAutoStart = (Control.ModifierKeys & Keys.Shift) == Keys.Shift;
        if (suppressAutoStart)
        {
            AppLogger.Info("auto-start suppressed by Shift modifier on file open");
            return;
        }

        // 外部ファイルの自動起動前に信頼確認 (OnShown と同方針)。
        if (!ConfirmAutoStartIfUntrusted()) return;

        // 新しい amm ファイルに記載されている autoStartCount に従い自動起動。
        // 既存 MDI 子は残し、InstanceNumber は AllocateInstanceNumber が空きを払い出す。
        AutoStartByProfileCount();
    }

    private void OnFileSave()
    {
        // amm ファイルを未だ開いていない (= デフォルトの BaseDirectory/profiles.amm)
        // 状態での「上書き保存」は実体上の保存先が確定していないので、SaveAs と
        // 同等の保存先選択ダイアログを表示する (Program Files 配下への誤書込み防止)。
        if (!_launchOptions.HasExplicitFile)
        {
            OnFileSaveAs();
            return;
        }
        try
        {
            SaveProfilesToAmmFile();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"AMM ファイル保存に失敗しました:\n{ex.Message}",
                "保存エラー", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private void OnFileSaveAs()
    {
        // 初期フォルダは DialogPaths の優先順位で解決 (明示 amm ファイルのフォルダ →
        // 起動時 CWD がシステム系なら マイドキュメント → それ以外は起動時 CWD)。
        // インストール直後の最初の保存で Program Files 配下が提示されるのを避ける。
        string initialFile = _launchOptions.HasExplicitFile
            ? Path.GetFileName(_launchOptions.ProfilesPath)
            : "profiles.amm";
        using var dlg = new SaveFileDialog
        {
            Title = "AMM ファイルを名前を付けて保存",
            Filter = "AMM ファイル (*.amm)|*.amm|JSON ファイル (*.json)|*.json|全て (*.*)|*.*",
            FileName = initialFile,
            InitialDirectory = ResolveDialogInitialDirectory(),
        };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;

        try
        {
            // 「保存先を切り替え + その先に書き込み」を 1 操作で行うため
            // _launchOptions.ProfilesPath を新パスへ更新してから書き戻す。
            // これ以降の上書き保存も新パスへ向く。
            _launchOptions.ProfilesPath = dlg.FileName;
            _launchOptions.HasExplicitFile = true;
            SaveProfilesToAmmFile();
            SetupProfilesWatcher();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"AMM ファイル保存に失敗しました:\n{ex.Message}",
                "保存エラー", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    /// <summary>
    /// 「現在状態を設定に反映」: 生存中の MDI 子から autoStartCount /
    /// 各 profile の AutoStartCount / WindowGeometry を「現在の生存 MDI 子」で
    /// 上書きする。in-memory のみ (ファイル書き込みは「上書き保存」「名前を付けて
    /// 保存」に委ねる)。生存数 0 の profile はリセット (= スナップショット意味論)。
    /// </summary>
    private void OnCaptureCurrentLayout()
    {
        if (_profiles.Length == 0) return;

        var aliveByProfile = _childOrder
            .Where(c => !c.IsDisposed)
            .GroupBy(c => c.ProfileName)
            .ToDictionary(g => g.Key, g => g.OrderBy(c => c.InstanceNumber).ToList());

        foreach (var profile in _profiles)
        {
            var alive = aliveByProfile.TryGetValue(profile.Name, out var list)
                ? list
                : new List<TerminalChildForm>();
            profile.AutoStartCount = alive.Count;
            profile.WindowGeometry = BuildGeometryFromAlive(alive);
        }
    }

    /// <summary>
    /// 記憶した配置 (各 profile の AutoStartCount / WindowGeometry) を in-memory で
    /// クリアする。「記憶した配置で表示」を無効化し、以後の保存で配置情報を書き出さない
    /// 状態へ戻す。ファイル書き込みは「上書き保存」「名前を付けて保存」に委ねる
    /// (OnCaptureCurrentLayout と対称)。破壊的なので確認ダイアログを挟む。
    /// 開いている MDI 子は閉じない (記憶メタデータのみ消す)。
    /// </summary>
    private void OnClearMemorizedLayout()
    {
        if (_profiles.Length == 0) return;
        var hasLayout = _profiles.Any(p => p.AutoStartCount > 0 || p.WindowGeometry.Length > 0);
        if (!hasLayout) return;

        var r = MessageBox.Show(this,
            "記憶した配置 (自動起動数・ウィンドウ位置 / サイズ / 表示名) をクリアします。\n" +
            "開いているウィンドウは閉じません。この変更は AMM ファイルへ保存するまで\n" +
            "ファイルには反映されません。よろしいですか?",
            "記憶した配置をクリア",
            MessageBoxButtons.YesNo, MessageBoxIcon.Question);
        if (r != DialogResult.Yes) return;

        foreach (var profile in _profiles)
        {
            profile.AutoStartCount = 0;
            profile.WindowGeometry = [];
        }
        RefreshViewMenu();
    }

    /// <summary>
    /// 各 profile の AutoStartCount / WindowGeometry を現在の MDI に適用する:
    /// 既存生存子 (InstanceNumber 昇順) を i=1..N の geometry に従って移動し、
    /// 不足分 (alive.Count &lt; target) は新規起動する。記憶対象外 (target を
    /// 超える生存子) はそのまま残す。target = max(AutoStartCount, geometry 件数)。
    /// </summary>
    private void OnRestoreMemorizedLayout()
    {
        // 記憶配置は絶対座標 (位置/サイズ) の復元なので、親リサイズで自動再整列しない。
        _autoFitLayout = null;
        foreach (var profile in _profiles)
        {
            var geomCount = profile.WindowGeometry?.Length ?? 0;
            var target = Math.Max(profile.AutoStartCount, geomCount);
            if (target == 0) continue;

            var alive = _childOrder
                .Where(c => !c.IsDisposed && c.ProfileName == profile.Name)
                .OrderBy(c => c.InstanceNumber)
                .ToList();

            // (a) 既存子の位置を geometry に合わせて移動 (target 件目まで)。
            //     geometry が無い index は「起動時最大化フォールバック」相当だが、
            //     既に開いている子の状態を破壊しないようそのまま据え置く。
            //     name エントリだけ持つ場合 (W/H = 0) は位置移動はせず名前のみ適用。
            //     Maximized=true のエントリは最後に最大化を適用 (位置設定の後)。
            int applyN = Math.Min(alive.Count, target);
            for (int i = 0; i < applyN; i++)
            {
                var c = alive[i];
                var entry = profile.WindowGeometry?.FirstOrDefault(e => e.Index == i + 1);
                if (profile.TryGetGeometryForIndex(i + 1, out var rect))
                {
                    if (c.WindowState != FormWindowState.Normal) c.WindowState = FormWindowState.Normal;
                    c.Location = rect.Location;
                    c.Size = rect.Size;
                }
                if (profile.TryGetNameForIndex(i + 1, out var savedName))
                {
                    c.ApplyCustomDisplayName(savedName);
                }
                if (entry?.Maximized == true && c.WindowState != FormWindowState.Maximized)
                {
                    c.WindowState = FormWindowState.Maximized;
                }
            }

            // (b) 不足分を新規起動。OpenTerminal は aliveOfProfile+1 を index と
            //     して TryGetGeometryForIndex を見る = 自動で次の geometry index
            //     を拾うので、ここではループするだけで OK。
            for (int i = alive.Count; i < target; i++)
            {
                OpenTerminal(profile);
            }
        }

        RefreshMdiButtonBar();
        UpdateSendTarget();
    }

    /// <summary>
    /// 生存 MDI 子 (InstanceNumber 昇順) を 1..N の geometry index に詰めて
    /// 配列化する。最大化のみで名前なしの子は「エントリ無し」(= 起動時最大化の
    /// フォールバック)、名前がある最大化子は <see cref="WindowGeometryEntry.Maximized"/>
    /// を true にした name-only エントリ (W=H=0)。最小化は RestoreBounds を使う。
    /// </summary>
    private static WindowGeometryEntry[] BuildGeometryFromAlive(List<TerminalChildForm> alive)
    {
        var entries = new List<WindowGeometryEntry>();
        for (int i = 0; i < alive.Count; i++)
        {
            var c = alive[i];
            bool isMaximized = c.WindowState == FormWindowState.Maximized;
            bool hasName = !string.IsNullOrEmpty(c.CustomDisplayName);
            bool hasCwd = !string.IsNullOrWhiteSpace(c.OverrideWorkingDirectory);
            // 最大化のみ・名前なし・cwd なしの子は「エントリ不要 (= 起動時最大化
            // フォールバック)」として省略する。cwd が指定されていれば最大化のみでも
            // エントリを残し、復元時に cwd を再適用する。
            if (isMaximized && !hasName && !hasCwd) continue;

            var entry = new WindowGeometryEntry
            {
                Index = i + 1,
                Name = c.CustomDisplayName,
                WorkingDirectory = hasCwd ? c.OverrideWorkingDirectory : null,
            };
            if (isMaximized)
            {
                entry.Maximized = true;
            }
            else
            {
                var r = c.WindowState == FormWindowState.Minimized ? c.RestoreBounds : c.Bounds;
                entry.X = r.X;
                entry.Y = r.Y;
                entry.W = r.Width;
                entry.H = r.Height;
            }
            entries.Add(entry);
        }
        return entries.ToArray();
    }

    private static bool GeometryEqual(WindowGeometryEntry[] a, WindowGeometryEntry[] b)
    {
        if (a.Length != b.Length) return false;
        for (int i = 0; i < a.Length; i++)
        {
            if (a[i].Index != b[i].Index ||
                a[i].X != b[i].X || a[i].Y != b[i].Y ||
                a[i].W != b[i].W || a[i].H != b[i].H) return false;
            if (!string.Equals(a[i].Name ?? "", b[i].Name ?? "", StringComparison.Ordinal)) return false;
            if ((a[i].Maximized ?? false) != (b[i].Maximized ?? false)) return false;
            if (!string.Equals(a[i].WorkingDirectory ?? "", b[i].WorkingDirectory ?? "", StringComparison.Ordinal)) return false;
        }
        return true;
    }

    /// <summary>
    /// 直近の保存/ロード時点 (_savedProfilesJson) から _profiles に未保存の変更が
    /// あるかを判定し、終了時ダイアログ向けの差分サマリを生成する。
    /// 主な変更検出: autoStartCount / windowGeometry / プロファイル名 / 追加・削除 /
    /// その他 (executable や引数等は「コマンド設定が変更されています」と簡潔に表示)。
    /// </summary>
    private bool HasUnsavedProfileChanges(out string summary)
    {
        summary = "";
        if (_savedProfilesJson == null) return false;
        var currentJson = SerializeProfilesToJson();
        if (string.Equals(currentJson, _savedProfilesJson, StringComparison.Ordinal)) return false;

        // baseline は ロスレス snapshot を優先。snapshot 未初期化のケース (旧コード
        // パス互換) のみ JSON roundtrip にフォールバックする。
        // JSON roundtrip は WhenWritingDefault で型デフォルト相当 (autoChcp=false
        // 等) を落とすため、フィールド初期化子で true を指定している bool プロパティ
        // を復元できず、未変更の profile を「変更あり」と誤検出する原因になる。
        SessionProfile[] baseline;
        if (_savedProfilesSnapshot != null)
        {
            baseline = _savedProfilesSnapshot;
        }
        else
        {
            try
            {
                var root = System.Text.Json.JsonSerializer.Deserialize<ProfilesRoot>(_savedProfilesJson);
                baseline = root?.Profiles ?? [];
            }
            catch { baseline = []; }
        }

        var sb = new System.Text.StringBuilder();
        int maxN = Math.Max(baseline.Length, _profiles.Length);
        for (int i = 0; i < maxN; i++)
        {
            if (i >= baseline.Length)
            {
                sb.AppendLine($"■ {_profiles[i].Name} (新規追加)");
                continue;
            }
            if (i >= _profiles.Length)
            {
                sb.AppendLine($"■ {baseline[i].Name} (削除)");
                continue;
            }
            var b = baseline[i];
            var c = _profiles[i];
            var lines = new List<string>();
            if (!string.Equals(b.Name, c.Name, StringComparison.Ordinal))
                lines.Add($"プロファイル名: \"{b.Name}\" → \"{c.Name}\"");
            if (b.AutoStartCount != c.AutoStartCount)
                lines.Add($"自動起動数: {b.AutoStartCount} → {c.AutoStartCount}");
            var bGeomN = b.WindowGeometry?.Length ?? 0;
            var cGeomN = c.WindowGeometry?.Length ?? 0;
            if (!GeometryEqual(b.WindowGeometry ?? [], c.WindowGeometry ?? []))
                lines.Add($"ウィンドウ位置: {bGeomN} 件 → {cGeomN} 件");

            // 他フィールド (executable / args / waitPatterns 等) はまとめて 1 行で示す
            var bJson = System.Text.Json.JsonSerializer.Serialize(b);
            var cJson = System.Text.Json.JsonSerializer.Serialize(c);
            if (!string.Equals(bJson, cJson, StringComparison.Ordinal))
            {
                bool detailed = lines.Count > 0;
                bool otherChanged = false;
                // 上記 3 フィールドの差分を打ち消した状態で再比較
                var bClone = JsonClone(b);
                var cClone = JsonClone(c);
                if (bClone != null && cClone != null)
                {
                    bClone.Name = c.Name;
                    bClone.AutoStartCount = c.AutoStartCount;
                    bClone.WindowGeometry = c.WindowGeometry ?? [];
                    otherChanged = !string.Equals(
                        System.Text.Json.JsonSerializer.Serialize(bClone),
                        System.Text.Json.JsonSerializer.Serialize(cClone),
                        StringComparison.Ordinal);
                }
                if (otherChanged)
                {
                    lines.Add(detailed ? "(その他設定にも変更あり)" : "コマンド設定が変更されています");
                }
            }

            if (lines.Count > 0)
            {
                sb.AppendLine($"■ {c.Name}");
                foreach (var l in lines) sb.AppendLine($"  ・{l}");
            }
        }
        summary = sb.ToString().TrimEnd();
        return summary.Length > 0;
    }

    private static SessionProfile? JsonClone(SessionProfile p)
    {
        var json = System.Text.Json.JsonSerializer.Serialize(p);
        return System.Text.Json.JsonSerializer.Deserialize<SessionProfile>(json);
    }

    /// <summary>
    /// 「コマンド → コマンドを管理…」: 追加 / 編集 / 削除 / 並べ替えを統合した
    /// 単一ダイアログ。OK 確定でのみ <see cref="_profiles"/> を更新する。
    /// 既存 SessionProfile 参照は、編集エントリで Original として保持されている
    /// ので OK 時にフィールドを流し込む形で温存する (= 起動中の MDI 子が握る
    /// _profile 参照は無効化されない)。新規追加分は配列末尾に append。AMM ファイル
    /// 書き戻しは従来通り [ファイル → 上書き保存] に委ねる。
    /// </summary>
    private void OnCommandManage()
    {
        using var dlg = new CommandManagerDialog(_profiles);
        if (dlg.ShowDialog(this) != DialogResult.OK) return;

        var newList = new List<SessionProfile>();
        foreach (var entry in dlg.Entries)
        {
            if (entry.Original != null)
            {
                // 既存 profile: フィールドだけ流し込んで参照を維持。ダイアログが
                // 編集対象としていない Theme / InitialCommands / SessionLog /
                // CtrlCCopyOnSelection / CloseOnExit は entry.Value (JSON clone)
                // 側でも既定値ではなく原本由来の値が入っているので、そのまま
                // コピーして問題ない。
                CopySessionProfileFields(entry.Original, entry.Value);
                newList.Add(entry.Original);
            }
            else
            {
                // 新規追加: そのまま配列に乗せる。
                newList.Add(entry.Value);
            }
        }
        _profiles = newList.ToArray();
        RebuildCommandMenu();
    }

    /// <summary>
    /// 編集ダイアログ由来の値 (<paramref name="src"/>) を、起動中の MDI 子が握って
    /// いる元 SessionProfile 参照 (<paramref name="dst"/>) に流し込む。これにより、
    /// AMM ファイル保存前であっても次回 send 時に新しい設定 (CommentPrefixes /
    /// CollapseBlankLines / CloseProhibited / Nickname / UseBracketedPaste 等) が
    /// 即時反映される。
    /// </summary>
    private static void CopySessionProfileFields(SessionProfile dst, SessionProfile src)
    {
        dst.Name = src.Name;
        dst.CommandType = src.CommandType;
        dst.Executable = src.Executable;
        dst.Args = src.Args;
        dst.WorkingDirectory = src.WorkingDirectory;
        dst.NewlineMode = src.NewlineMode;
        dst.OutputEncoding = src.OutputEncoding;
        dst.AutoChcp = src.AutoChcp;
        dst.WaitPatterns = src.WaitPatterns;
        dst.AutoStartCount = src.AutoStartCount;
        dst.CloseProhibited = src.CloseProhibited;
        dst.CollapseBlankLines = src.CollapseBlankLines;
        dst.CommentPrefixes = src.CommentPrefixes;
        dst.Nickname = src.Nickname;
        dst.SendLineByLine = src.SendLineByLine;
        dst.UseBracketedPaste = src.UseBracketedPaste;
        dst.SelectWorkingDirOnStart = src.SelectWorkingDirOnStart;
        dst.ResumeOnStart = src.ResumeOnStart;
        dst.PromptNewNameOnCommandAdd = src.PromptNewNameOnCommandAdd;
        dst.FontSize = src.FontSize;
        dst.WindowGeometry = src.WindowGeometry;
        dst.QuickPrompts = src.QuickPrompts;
    }

    /// <summary>
    /// _profiles からコマンドメニューを再構築。動的 profile 一覧の後ろに、
    /// 「コマンド追加・編集・削除 / エディタ連携で使うエディタ」の固定項目を追加する。
    /// アプリ内 profile 編集後 / DropDownOpening で呼ばれる。
    /// </summary>
    private void RebuildCommandMenu()
    {
        _commandMenu.DropDownItems.Clear();
        foreach (var profile in _profiles)
        {
            var p = profile;
            _commandMenu.DropDownItems.Add(p.Name, null, (_, _) => OpenTerminal(p, isCommandAdd: true));
        }
        if (_profiles.Length > 0)
        {
            _commandMenu.DropDownItems.Add(new ToolStripSeparator());
        }
        _commandMenu.DropDownItems.Add("コマンドを管理(&M)...", null, (_, _) => OnCommandManage());
        _commandMenu.DropDownItems.Add(new ToolStripSeparator());
        _commandMenu.DropDownItems.Add("エディタ連携で使うエディタ(&D)...", null, (_, _) => ShowEditorPreferenceDialog());
        _commandMenu.DropDownItems.Add(new ToolStripSeparator());
        // CLI (Claude Code / Codex / Copilot CLI) のユーザースコープ設定ファイルへ
        // amm-mcp.exe を MCP サーバとして登録 / 削除する
        _commandMenu.DropDownItems.Add("CLI への MCP / フック登録(&P)...", null, (_, _) =>
        {
            using var dialog = new McpRegistrationDialog();
            dialog.ShowDialog(this);
        });
    }

    /// <summary>
    /// 表示メニューを開く直前に MDI 一覧を再構築する (動的)。整列項目 3 種は
    /// 末尾に固定。アクティブ子は Checked で示す。
    /// </summary>
    private void RefreshViewMenu()
    {
        // 「記憶した配置で表示」の有効/無効: _profiles に AutoStartCount > 0 か
        // WindowGeometry が 1 件以上ある profile があれば有効 (= AMM ファイルに
        // 配置情報があるか、ランタイムで「現在の配置を記憶」済み)。
        _restoreLayoutMenuItem.Enabled = _profiles.Any(p =>
            p.AutoStartCount > 0 || (p.WindowGeometry?.Length ?? 0) > 0);
        // 「記憶した配置をクリア」も同条件 (消すものがあるときだけ有効)。
        _clearLayoutMenuItem.Enabled = _restoreLayoutMenuItem.Enabled;

        // separator より前 (= 動的 MDI 一覧) を一旦除去
        while (_viewMenu.DropDownItems.Count > 0 &&
               _viewMenu.DropDownItems[0] != _viewMenuSeparator)
        {
            _viewMenu.DropDownItems.RemoveAt(0);
        }

        var alive = _childOrder.Where(c => !c.IsDisposed).ToList();
        if (alive.Count == 0)
        {
            var empty = new ToolStripMenuItem("(MDI なし)") { Enabled = false };
            _viewMenu.DropDownItems.Insert(0, empty);
            return;
        }

        for (int i = alive.Count - 1; i >= 0; i--)
        {
            var child = alive[i];
            var item = new ToolStripMenuItem($"{i + 1}. {child.DisplayName}")
            {
                Checked = ReferenceEquals(ActiveMdiChild, child),
                CheckOnClick = false,
            };
            var captured = child;
            item.Click += (_, _) =>
            {
                if (captured.IsDisposed) return;
                if (captured.WindowState == FormWindowState.Minimized)
                    captured.WindowState = FormWindowState.Normal;
                captured.Activate();
            };
            _viewMenu.DropDownItems.Insert(0, item);
        }
    }

    private string SerializeProfilesToJson()
    {
        var root = new ProfilesRoot { Profiles = _profiles, McpServers = _mcpServers };
        return System.Text.Json.JsonSerializer.Serialize(
            root,
            new System.Text.Json.JsonSerializerOptions
            {
                WriteIndented = true,
                DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault,
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            });
    }

    private void SaveProfilesToAmmFile()
    {
        // FileSystemWatcher が自分の書き戻しに反応して LoadProfiles を呼び戻し、
        // ダイアログ反映直後の Profile 参照が古いインスタンスに置き換わる事故を
        // 防ぐため、書き込み中はウォッチャを一時停止する。
        var watcher = _profilesWatcher;
        var wasEnabled = watcher?.EnableRaisingEvents ?? false;
        if (watcher != null) watcher.EnableRaisingEvents = false;
        try
        {
            var json = SerializeProfilesToJson();
            // 主設定 profiles.amm も MCP 設定と同様に原子的書込 (Flush+File.Replace) で
            // 保存する。書込中のクラッシュ/電断で全コマンド定義を喪失する窓を塞ぐ。
            AtomicFileWriter.Write(_launchOptions.ProfilesPath, json);
            _savedProfilesJson = json;
            _savedProfilesSnapshot = SnapshotProfiles(_profiles);
        }
        finally
        {
            if (watcher != null) watcher.EnableRaisingEvents = wasEnabled;
        }
    }

    /// <summary>
    /// 入力欄からの送信 (Ctrl+S / Ctrl+E) の対象となる MDI 子を解決する。
    /// WebView2 内フォーカスなどで ActiveMdiChild が null を返すことがある
    /// (activation chain の不全) ため、直近アクティブの子 → 唯一の子 の順で
    /// フォールバックする。複数子があり直近アクティブも不明な場合のみ null
    /// (誤ったペインへ黙って送るよりは送らない方が安全)。
    /// </summary>
    private TerminalChildForm? ResolveSendTarget()
    {
        if (ActiveMdiChild is TerminalChildForm active) return active;
        if (_lastActiveTerminal is { IsDisposed: false } last) return last;
        var alive = _childOrder.Where(c => !c.IsDisposed).ToList();
        return alive.Count == 1 ? alive[0] : null;
    }

    private void UpdateSendTarget()
    {
        const string Shortcuts = "  |  Ctrl+S: 送信 / Ctrl+Shift+S: 全ペイン送信 / Ctrl+1..9: 指定番号へ送信 / Ctrl+H: 履歴 / Ctrl+E: エディタ連携";

        // 表示とCtrl+S の実挙動が一致するよう、送信先解決も ResolveSendTarget に揃える
        if (ResolveSendTarget() is { } child)
        {
            var state = child.CurrentWaitState;
            var stateText = state switch
            {
                WaitState.Running => "実行中",
                WaitState.WaitingForInput => "入力待ち ✓",
                WaitState.Unknown => "不明",
                WaitState.Stopped => "停止",
                _ => ""
            };
            _targetLabel.Text = $"送信先: [{child.Text} - {stateText}]" + Shortcuts;
            _targetLabel.BackColor = state switch
            {
                WaitState.WaitingForInput => Color.FromArgb(200, 230, 200), // 薄緑
                WaitState.Unknown         => Color.FromArgb(255, 240, 180), // 薄黄
                WaitState.Stopped         => Color.FromArgb(240, 200, 200), // 薄赤
                _                         => SystemColors.Control,          // 実行中 / その他はグレー
            };
        }
        else
        {
            _targetLabel.Text = "送信先: (なし)" + Shortcuts;
            _targetLabel.BackColor = SystemColors.Control;
        }
    }

    private void OnInputBoxKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.KeyCode == Keys.S && e.Control && e.Shift && !e.Alt)
        {
            // Ctrl+Shift+S: 全ペインへ単発ブロードキャスト送信 (旧「全ペイン送信モード」を置換)。
            e.Handled = true;
            e.SuppressKeyPress = true;
            BroadcastInput();
        }
        else if (e.KeyCode == Keys.S && e.Control && !e.Shift && !e.Alt)
        {
            // Ctrl+S: Send to active terminal (旧 Ctrl+Enter を置き換え)。
            // 送信メニュー「Ctrl+S でプロンプト送信」OFF 時は黙って握り潰す
            // (エディタの保存癖での誤送信防止)。
            e.Handled = true;
            e.SuppressKeyPress = true;
            if (_ctrlSSendMenuItem.Checked)
                SendInput();
            else
                AppLogger.Info("[send] Ctrl+S ignored (送信メニューのトグルが OFF)");
        }
        else if (e.KeyCode == Keys.H && e.Control && !e.Shift && !e.Alt)
        {
            // Ctrl+H: 履歴ポップアップを開く (履歴ボタンと同挙動)
            e.Handled = true;
            e.SuppressKeyPress = true;
            OnHistoryButtonClick(_historyButton, EventArgs.Empty);
        }
        else if (e.KeyCode == Keys.E && e.Control && !e.Shift && !e.Alt)
        {
            // Ctrl+E: アクティブ MDI のエディタ連携を直接起動。初回 (bridge 未生成時)
            // は入力欄の現在内容を初期表示として書き込む。2 回目以降は既存 bridge を
            // 同じ一時ファイルで再起動するだけ (内容は上書きしない)。
            e.Handled = true;
            e.SuppressKeyPress = true;
            StartEditorBridgeForActive();
        }
        else if (e.Control && !e.Shift && !e.Alt &&
                 ((e.KeyCode >= Keys.D1 && e.KeyCode <= Keys.D9) ||
                  (e.KeyCode >= Keys.NumPad1 && e.KeyCode <= Keys.NumPad9)))
        {
            // Ctrl+1..9: Send to numbered MDI child (from button bar order)
            var n = (e.KeyCode >= Keys.NumPad1)
                ? (int)(e.KeyCode - Keys.NumPad0)
                : (int)(e.KeyCode - Keys.D0);
            e.Handled = true;
            e.SuppressKeyPress = true;
            SendToIndexed(n);
        }
        // ↑/↓ で履歴ナビゲーションする旧仕様は廃止 (誤操作の温床)。
        // 履歴は 履歴 ▼ ボタン / Ctrl+H からのみ呼び出す。
    }

    /// <summary>
    /// MDI 子最大化で WinForms が _menuStrip に merge するシステムアイコン項目の
    /// Image を 16×16 にダウンスケールして、固定 24 px のメニューバー高さに収める。
    /// 自前で追加した menu (Tag="own") は対象外。
    /// </summary>
    private void ScanAndShrinkMergedSystemIcons()
    {
        // Layout / Paint から呼ばれるので毎フレーム走るが、対象は通常 0-1 件、
        // しかも条件不一致で即抜けるので O(1) コスト。Image を差し替えたときに
        // Layout が再帰しても、次回はサイズ判定で抜けるため無限ループしない。
        for (int i = 0; i < _menuStrip.Items.Count; i++)
            ShrinkMergedSystemIcon(_menuStrip.Items[i]);
    }

    private static void ShrinkMergedSystemIcon(ToolStripItem? raw)
    {
        if (raw is not ToolStripMenuItem item) return;
        if (item.Tag is "own") return;
        if (item.Image is not Image img) return;
        if (img.Width <= 16 && img.Height <= 16) return;

        var small = new Bitmap(16, 16);
        using (var g = Graphics.FromImage(small))
        {
            g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.HighQuality;
            g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
            g.CompositingQuality = System.Drawing.Drawing2D.CompositingQuality.HighQuality;
            g.DrawImage(img, 0, 0, 16, 16);
        }
        item.Image = small;
        item.ImageScaling = ToolStripItemImageScaling.None;
    }

    /// <summary>
    /// エディタ連携で使うエディタを選ばせるダイアログ。3 モード:
    ///   - 関連付けアプリ (.md の関連付け / 既定: ShellExecute)
    ///   - メモ帳 (notepad.exe)
    ///   - 任意のエディタ (フルパス指定 + 参照ボタン)
    /// 結果は _editorMode / _customEditorPath に保存し layout.json に永続化される。
    /// </summary>
    private void ShowEditorPreferenceDialog()
    {
        // ボタンの既定 Height (~23px) は高 DPI 環境で文字下端が切れることが
        // あるため、ここではメニュー行高と同じ「フォント行高 + 余白」で算出する。
        var buttonHeight = Math.Max(28, (SystemFonts.MenuFont?.Height ?? 16) + 12);

        using var dlg = new Form
        {
            Text = "エディタ連携で使うエディタ",
            FormBorderStyle = FormBorderStyle.FixedDialog,
            StartPosition = FormStartPosition.CenterParent,
            ClientSize = new Size(440, 170 + buttonHeight + 16),
            MinimizeBox = false,
            MaximizeBox = false,
            ShowInTaskbar = false,
        };

        var rbAssociated = new RadioButton
        {
            Text = "関連付けされたアプリ (.md の既定エディタ)",
            Location = new Point(16, 16),
            AutoSize = true,
        };
        var rbNotepad = new RadioButton
        {
            Text = "メモ帳 (notepad.exe)",
            Location = new Point(16, 44),
            AutoSize = true,
        };
        var rbCustom = new RadioButton
        {
            Text = "指定したエディタ:",
            Location = new Point(16, 72),
            AutoSize = true,
        };
        var tbPath = new TextBox
        {
            Location = new Point(32, 100),
            Width = 308,
            Text = _customEditorPath,
        };
        var btnBrowse = new Button
        {
            Text = "参照…",
            Location = new Point(346, 98),
            Width = 80,
            Height = buttonHeight,
        };
        btnBrowse.Click += (_, _) =>
        {
            using var ofd = new OpenFileDialog
            {
                Title = "エディタの実行ファイルを選択",
                Filter = "実行ファイル (*.exe)|*.exe|全て (*.*)|*.*",
                FileName = tbPath.Text,
            };
            if (ofd.ShowDialog(dlg) == DialogResult.OK)
            {
                tbPath.Text = ofd.FileName;
                rbCustom.Checked = true;
            }
        };

        var ok = new Button
        {
            Text = "OK",
            DialogResult = DialogResult.OK,
            Location = new Point(264, 170),
            Width = 80,
            Height = buttonHeight,
        };
        var cancel = new Button
        {
            Text = "キャンセル",
            DialogResult = DialogResult.Cancel,
            Location = new Point(348, 170),
            Width = 80,
            Height = buttonHeight,
        };

        switch (_editorMode)
        {
            case "Notepad": rbNotepad.Checked = true; break;
            case "Custom": rbCustom.Checked = true; break;
            default: rbAssociated.Checked = true; break;
        }

        dlg.Controls.Add(rbAssociated);
        dlg.Controls.Add(rbNotepad);
        dlg.Controls.Add(rbCustom);
        dlg.Controls.Add(tbPath);
        dlg.Controls.Add(btnBrowse);
        dlg.Controls.Add(ok);
        dlg.Controls.Add(cancel);
        dlg.AcceptButton = ok;
        dlg.CancelButton = cancel;

        if (dlg.ShowDialog(this) != DialogResult.OK) return;

        if (rbNotepad.Checked) _editorMode = "Notepad";
        else if (rbCustom.Checked) _editorMode = "Custom";
        else _editorMode = "Associated";
        _customEditorPath = tbPath.Text?.Trim() ?? "";
        // 即時永続化: ダイアログ確定直後にクラッシュしても次回起動で同設定を
        // 復元できるよう、OnFormClosingInternal の保存を待たずに書き出す。
        SaveLayout();
    }

    // ---- MCP ゲートウェイ 設定 / ロード / 保存 ----

    private static string GlobalMcpServersPath => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "amm", "mcp-servers.json");

    private void LoadGlobalMcpServers()
    {
        var path = GlobalMcpServersPath;
        if (!File.Exists(path)) return;
        try
        {
            var json = File.ReadAllText(path);
            var root = System.Text.Json.JsonSerializer.Deserialize<GlobalMcpRoot>(json);
            _mcpServersGlobal = root?.McpServers;
            if (_mcpServersGlobal?.Length > 0)
                AppLogger.Info($"[gateway] global config loaded: {_mcpServersGlobal.Length} server(s)");
        }
        catch (Exception ex)
        {
            AppLogger.Error("[gateway] global mcp-servers.json load failed", ex);
        }
    }

    private void SaveGlobalMcpServers()
    {
        var path = GlobalMcpServersPath;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var root = new GlobalMcpRoot { McpServers = _mcpServersGlobal ?? [] };
            var json = System.Text.Json.JsonSerializer.Serialize(root,
                new System.Text.Json.JsonSerializerOptions
                {
                    WriteIndented = true,
                    DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault,
                    Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
                });
            Amm.Core.Mcp.AtomicFileWriter.Write(path, json);
        }
        catch (Exception ex)
        {
            AppLogger.Error("[gateway] global mcp-servers.json save failed", ex);
            MessageBox.Show(this, $"グローバル MCP 設定の保存に失敗しました。\n\n{ex.Message}",
                "保存エラー", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private sealed class GlobalMcpRoot
    {
        [System.Text.Json.Serialization.JsonPropertyName("mcpServers")]
        public McpServerConfig[] McpServers { get; set; } = [];
    }

    private void RestartGateway()
    {
        // 旧 GatewayManager を停止し、新しい結合設定で再起動
        var old = _gateway;
        var allConfigs = (_mcpServersGlobal ?? []).Concat(_mcpServers ?? []).ToArray();

        GatewayManager? newGateway = null;
        if (allConfigs.Length > 0)
        {
            newGateway = new GatewayManager(allConfigs);
            _ = newGateway.StartAutoStartServersAsync();
        }

        _gateway = newGateway;
        _mcpServer?.UpdateGateway(newGateway);

        if (old != null)
            _ = old.DisposeAsync().AsTask();

        AppLogger.Info($"[gateway] restarted: {allConfigs.Length} server(s)");
    }

    private void ShowGatewaySettingsDialog()
    {
        using var dlg = new McpGatewayDialog(
            _mcpServersGlobal ?? [],
            _mcpServers ?? [],
            _gateway);

        if (dlg.ShowDialog(this) != DialogResult.OK) return;

        bool globalChanged = !SequenceEqual(dlg.GlobalServers, _mcpServersGlobal ?? []);
        bool fileChanged   = !SequenceEqual(dlg.FileServers,   _mcpServers ?? []);

        if (!globalChanged && !fileChanged) return;

        if (globalChanged)
        {
            _mcpServersGlobal = dlg.GlobalServers;
            SaveGlobalMcpServers();
        }

        if (fileChanged)
        {
            _mcpServers = dlg.FileServers.Length > 0 ? dlg.FileServers : null;
            SaveProfilesToAmmFile();
        }

        RestartGateway();
    }

    private static bool SequenceEqual(McpServerConfig[] a, McpServerConfig[] b)
    {
        if (a.Length != b.Length) return false;
        for (int i = 0; i < a.Length; i++)
        {
            if (a[i].Name != b[i].Name || a[i].Command != b[i].Command) return false;
        }
        return true;
    }

    private void ShowAboutDialog()
    {
        var asm = typeof(MdiParentForm).Assembly;
        // AssemblyVersion (GetName().Version) は数値のみでプレリリース接尾辞 (-prNN 等) を
        // 保持できないため、CI が Version プロパティ経由で設定する InformationalVersion を使う。
        var version = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? asm.GetName().Version?.ToString() ?? "?";
        var title = "amm について";
        var body =
            $"amm\n" +
            $"Version {version}\n\n" +
            $"Windows ネイティブの AI/CLI マルチプレクサ (MDI + WebView2 + xterm.js + ConPTY)\n\n" +
            $".NET Runtime: {Environment.Version}\n" +
            $"OS: {Environment.OSVersion}";
        MessageBox.Show(this, body, title, MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    private void OnInputBoxDragEnter(object? sender, DragEventArgs e)
    {
        if (e.Data?.GetDataPresent(DataFormats.FileDrop) == true)
            e.Effect = DragDropEffects.Copy;
    }

    private void OnInputBoxDragDrop(object? sender, DragEventArgs e)
    {
        if (e.Data?.GetData(DataFormats.FileDrop) is not string[] files || files.Length == 0)
            return;

        var targets = files.Where(File.Exists).ToArray();
        if (targets.Length == 0) return;

        var allText = FileDropHelper.AllTextFiles(targets);
        var action = TerminalChildForm.AskDropAction(this, targets.Length, allText);
        if (action == TerminalChildForm.DropAction.Cancel) return;

        if (action == TerminalChildForm.DropAction.Path)
        {
            // 絶対パスはカーソル位置に挿入 (既存テキストと共存)
            var inserted = FileDropHelper.JoinPaths(targets);
            if (string.IsNullOrEmpty(inserted)) return;
            var caret = _inputBox.SelectionStart;
            _inputBox.Text = _inputBox.Text.Insert(caret, inserted);
            _inputBox.SelectionStart = caret + inserted.Length;
        }
        else
        {
            // ファイル内容は入力欄を「クリアして置き換え」
            var combined = TerminalChildForm.ReadAndCombine(this, targets, System.Text.Encoding.UTF8);
            if (combined == null) return;
            if (string.IsNullOrEmpty(combined)) return;
            _inputBox.Text = combined;
            _inputBox.SelectionStart = combined.Length;
        }
        _inputBox.Focus();
    }

    // async void イベント経路 (Ctrl+S 等) の未処理例外で UI スレッドがクラッシュ
    // するのを防ぐ共通ガード。例外はログに残して操作を中断する。
    private static async void RunGuarded(Func<Task> action, string label)
    {
        try { await action(); }
        catch (Exception ex) { AppLogger.Error($"[ui] {label} で未処理例外", ex); }
    }

    private void SendInput() => RunGuarded(SendInputAsync, "SendInput");

    private async Task SendInputAsync()
    {
        // 選択中ならその範囲のみ、未選択なら入力欄全体を送信ソースとする。
        bool hadSelection = _inputBox.SelectionLength > 0;
        var rawText = hadSelection ? _inputBox.SelectedText : _inputBox.Text;
        if (string.IsNullOrEmpty(rawText))
        {
            AppLogger.Info("[send] skip: 入力欄が空");
            return;
        }

        // 履歴には (ユーザが入力したまま) フィルタ前のテキストを残す。
        // これがないと復元時にコメントが消えて編集できなくなる。
        if (ResolveSendTarget() is not { } child)
        {
            // 無言で何も起きないと「Ctrl+S が壊れた」ように見えるため痕跡を残す
            AppLogger.Info("[send] skip: 送信先 MDI を解決できない (アクティブな子なし)");
            return;
        }
        var sendText = ApplyPerCommandFilter(rawText, child.Profile, _commentFilterEnabled);
        if (string.IsNullOrEmpty(sendText))
        {
            AppLogger.Info("[send] skip: コメント行フィルタ適用後に空 (フィルタ対象のみの入力)");
            return;
        }
        _inputHistory.Add(rawText);
        AppLogger.Info($"[send] 入力欄 → {child.DisplayName} ({sendText.Length} 文字, submit={_sendSubmitEnterMenuItem.Checked})");
        await DispatchSendAsync(child, sendText, submit: _sendSubmitEnterMenuItem.Checked);
        if (_clearAfterSendMenuItem.Checked && !hadSelection) _inputBox.Clear();
        _inputHistory.ResetCursor();

        // 単一送信時は対象 MDI の WebView2 / xterm.js までフォーカスを降ろす。
        child.FocusTerminal();
    }

    /// <summary>
    /// Ctrl+Shift+S からの全ペイン送信。モードレスな単発操作 (旧「全ペイン送信モード」
    /// メニュートグルを置き換え) で、押下のたびに「現在実行中の全 MDI」を対象に
    /// per-profile 前処理を適用して送る。
    /// </summary>
    private void BroadcastInput() => RunGuarded(BroadcastInputAsync, "BroadcastInput");

    private async Task BroadcastInputAsync()
    {
        bool hadSelection = _inputBox.SelectionLength > 0;
        var rawText = hadSelection ? _inputBox.SelectedText : _inputBox.Text;
        if (string.IsNullOrEmpty(rawText)) return;

        var targets = MdiChildren.OfType<TerminalChildForm>()
            .Where(c => c.IsProcessRunning)
            .ToList();
        if (targets.Count == 0) return;
        _inputHistory.Add(rawText);
        // 送信先ごとに profile が異なる可能性があるため、per-target で前処理
        foreach (var t in targets)
        {
            var send = ApplyPerCommandFilter(rawText, t.Profile, _commentFilterEnabled);
            if (string.IsNullOrEmpty(send)) continue;
            await DispatchSendAsync(t, send, submit: _sendSubmitEnterMenuItem.Checked);
        }
        // 「送信後にクリア」は未選択送信のときのみ全クリア。選択送信時は
        // 残りのテキストを保持 (ユーザ要望)。
        if (_clearAfterSendMenuItem.Checked && !hadSelection) _inputBox.Clear();
        _inputHistory.ResetCursor();
        // ブロードキャスト後はフォーカスを移さず入力欄に保持 (連続送信を想定)。
    }

    /// <summary>
    /// プロファイル設定に応じた送信ルーティング。
    /// <list type="bullet">
    ///   <item>UseBracketedPaste=true (Copilot CLI 等): 内容を <c>\x1b[200~..\x1b[201~</c>
    ///         で包み、最後に確定 Enter を打つ。素のバイト書き込みを discard する
    ///         Ink ベース TUI に対応。</item>
    ///   <item>SendLineByLine=true: 行ごとに <c>\r</c> 付きで間隔を空けて書き込み。
    ///         "1 行 = 1 メッセージ" の AI CLI に対応。</item>
    ///   <item>その他: <see cref="TerminalChildForm.SendText"/> でまとめて送る
    ///         (cmd / PowerShell / Claude / Codex 等の従来挙動)。</item>
    /// </list>
    /// 入力パネル / MCP / エディタ連携いずれの呼び出し経路でも、Ink TUI で
    /// 1 回目 \r が paste 終端処理に吸われて submit にならない問題があるため、
    /// すべての経路で確定 Enter を 1 回追加で打つ (= 仕様統一)。1 回目で submit
    /// 済みなら 2 回目は空入力扱いになり Claude/Codex/Copilot はプロンプト
    /// 再表示するだけで実害なし。
    ///
    /// <paramref name="submit"/> = false (送信メニュー「確定改行も一緒に送信」OFF)
    /// のときはテキストだけ届け、確定 Enter は一切打たない。誤った MDI に送って
    /// しまっても実行前にターミナル側で取り消せる。SendLineByLine profile では
    /// 中間行の \r は「1 行 = 1 メッセージ」の仕様上残り、最終行のみ未確定になる。
    /// </summary>
    private static async Task DispatchSendAsync(TerminalChildForm target, string sendText, bool submit = true)
    {
        if (target.ChatRecordEnabled)
            target.StartChatRecording(sendText);
        if (target.StatsEnabled)
            target.StartStatsTracking();

        if (target.Profile.UseBracketedPaste)
        {
            await target.SendAsBracketedPasteAsync(sendText, extraEnter: submit, submit: submit);
            target.ScrollToBottom();
            return;
        }
        if (target.Profile.SendLineByLine)
        {
            await target.SendTextLineByLineAsync(sendText, submitLastLine: submit);
            if (submit)
            {
                await Task.Delay(80);
                target.SendText("\r", appendEnter: false);
            }
            target.ScrollToBottom();
            return;
        }
        target.SendText(sendText, appendEnter: submit);
        if (submit)
        {
            await Task.Delay(80);
            target.SendText("\r", appendEnter: false);
        }
        target.ScrollToBottom();
    }

    /// <summary>
    /// 指定インデックス (1-based、Ctrl+1..9 想定) の子ウィンドウへ入力欄の
    /// 内容を送信する。送信後は対象の MDI にフォーカスを移す (ユーザ要望)。
    /// </summary>
    private void SendToIndexed(int oneBasedIndex)
        => RunGuarded(() => SendToIndexedAsync(oneBasedIndex), "SendToIndexed");

    private async Task SendToIndexedAsync(int oneBasedIndex)
    {
        var alive = _childOrder.Where(c => !c.IsDisposed).ToList();
        if (oneBasedIndex < 1 || oneBasedIndex > alive.Count) return;

        var target = alive[oneBasedIndex - 1];
        if (!target.IsProcessRunning) return;

        // 選択中ならその範囲のみ、未選択なら入力欄全体を送信ソースとする。
        bool hadSelection = _inputBox.SelectionLength > 0;
        var rawText = hadSelection ? _inputBox.SelectedText : _inputBox.Text;
        if (string.IsNullOrEmpty(rawText)) return;
        var sendText = ApplyPerCommandFilter(rawText, target.Profile, _commentFilterEnabled);
        if (string.IsNullOrEmpty(sendText)) return;

        _inputHistory.Add(rawText);
        await DispatchSendAsync(target, sendText, submit: _sendSubmitEnterMenuItem.Checked);
        if (_clearAfterSendMenuItem.Checked && !hadSelection) _inputBox.Clear();
        _inputHistory.ResetCursor();
        // 単一送信時は対象 MDI へフォーカスを移す (ユーザ要望)。
        target.FocusTerminal();
    }

    private void RefreshMdiButtonBar()
    {
        // 破棄済みが紛れ込まないようにフィルタしてから配列化
        var alive = _childOrder.Where(c => !c.IsDisposed).ToList();

        _mdiButtonBar.SuspendLayout();
        try
        {
            // 先頭 3 個 (履歴 / エディタ連携 / 整列) は固定。以降の動的 MDI ボタンを差し替える。
            const int permanentCount = 3;
            while (_mdiButtonBar.Controls.Count > permanentCount)
            {
                var last = _mdiButtonBar.Controls[_mdiButtonBar.Controls.Count - 1];
                _mdiButtonBar.Controls.RemoveAt(_mdiButtonBar.Controls.Count - 1);
                last.Dispose();
            }

            int idx = 1;
            foreach (var child in alive)
            {
                var stateChar = WaitStateGlyph.For(child.CurrentWaitState, child.HasAttention);
                // 表示名はリネーム済みなら custom 名、未リネームなら ProfileName(+inst)。
                // Ctrl+1..9 対応は 9 までなのでそれ以降は番号表示を省く。
                var prefix = idx <= 9 ? $"[Ctrl+{idx}] " : "  ";
                var text = $"{prefix}{stateChar} {child.DisplayName}";

                // FlatStyle.System はテーマ有効な Windows で BackColor を無視する。
                // 以前 active 子のみ FlatStyle.Flat に切り替えていたため、非アクティブ
                // の入力待ちボタンが黄色にならない不具合があった。すべてのボタンを
                // FlatStyle.Flat に統一して BackColor を確実に反映させる。
                var btn = new MdiSwitchButton
                {
                    Text = text,
                    AutoSize = true,
                    AutoSizeMode = AutoSizeMode.GrowAndShrink,
                    FlatStyle = FlatStyle.Flat,
                    Margin = new Padding(0, 0, 4, 0),
                    Padding = new Padding(6, 0, 6, 0),
                    UseCompatibleTextRendering = false,
                };
                btn.FlatAppearance.BorderColor = Color.FromArgb(160, 160, 160);
                btn.FlatAppearance.BorderSize = 1;
                // 色優先順位:
                //   最小化         → グレー背景 (最小化中を一目で識別)
                //   許可・確認待ち → オレンジ背景 (attention、最優先で気付かせる)
                //   入力待ち       → 黄背景 (アクティブでも黄色を消さない)
                //   アクティブ子   → 薄青背景 (太字 + 青枠で強調)
                //   それ以外       → 既定 (ControlLight)
                bool isMinimized = child.WindowState == FormWindowState.Minimized;
                bool isWaiting = child.CurrentWaitState == WaitState.WaitingForInput;
                bool isActive = ReferenceEquals(ActiveMdiChild, child);
                if (isMinimized)
                {
                    btn.BackColor = Color.FromArgb(200, 200, 200); // グレー: 最小化
                }
                else if (isWaiting && child.HasAttention)
                {
                    btn.BackColor = Color.FromArgb(255, 180, 100); // オレンジ: 許可・確認待ち
                }
                else if (isWaiting)
                {
                    btn.BackColor = Color.FromArgb(255, 230, 130); // 黄: 入力待ち
                }
                else if (isActive)
                {
                    btn.BackColor = Color.FromArgb(180, 210, 240); // 薄青: アクティブ
                }
                else
                {
                    btn.BackColor = SystemColors.ControlLight;
                }
                if (isActive)
                {
                    btn.Font = new Font(btn.Font, FontStyle.Bold);
                    btn.FlatAppearance.BorderColor = Color.FromArgb(40, 90, 180);
                    btn.FlatAppearance.BorderSize = 2;
                }

                var captured = child;
                // 長押し検出: MouseDown でタイマ開始、しきい値経過時に
                // 入力欄の内容を対象 MDI に送信。長押し成立時は直後の MouseClick
                // (最大化トグル) を抑止する。短い左クリックは従来通り最大化トグル。
                const int LongPressMs = 600;
                var longPressTimer = new System.Windows.Forms.Timer { Interval = LongPressMs };
                bool longPressTriggered = false;
                longPressTimer.Tick += async (_, _) =>
                {
                    longPressTimer.Stop();
                    if (captured.IsDisposed) return;
                    longPressTriggered = true;
                    await SendInputToTargetAsync(captured);
                };
                btn.MouseDown += (_, e) =>
                {
                    if (e.Button != MouseButtons.Left) return;
                    longPressTriggered = false;
                    longPressTimer.Stop();
                    longPressTimer.Start();
                };
                btn.MouseUp += (_, e) =>
                {
                    if (e.Button != MouseButtons.Left) return;
                    longPressTimer.Stop();
                };
                btn.MouseLeave += (_, _) => longPressTimer.Stop();
                btn.Disposed += (_, _) => longPressTimer.Dispose();
                // 左クリック (= MouseClick で button == Left) : 対象 MDI を切り替え
                // + 最大化トグル。Button.Click は本来左ボタン専用だが、MouseClick で
                // 明示的に Left をフィルタすることで「フォーカス取得時に first-click が
                // どのボタンでも Click 扱いされる」等の環境差異に依存しないようにする。
                // 長押し成立時は最大化トグルを抑止 (フラグはタイマ Tick でセット)。
                btn.MouseClick += (_, e) =>
                {
                    if (e.Button != MouseButtons.Left) return;
                    if (longPressTriggered)
                    {
                        longPressTriggered = false;
                        return;
                    }
                    if (captured.IsDisposed) return;
                    captured.WindowState = captured.WindowState == FormWindowState.Maximized
                        ? FormWindowState.Normal
                        : FormWindowState.Maximized;
                    captured.FocusTerminal();
                };
                // ダブルクリック: 入力欄の内容を対象 MDI に送信。MouseClick(Left) も
                // 先に発火するが、Activate 副作用は実害なし (送信後に FocusTerminal で
                // 同じ結果)。
                btn.DoubleClick += async (_, _) =>
                {
                    if (captured.IsDisposed) return;
                    await SendInputToTargetAsync(captured);
                };
                // 右クリックメニューは ContextMenuStrip プロパティで WinForms の
                // WM_CONTEXTMENU ルートに乗せる。MouseUp ハンドラで自前 menu.Show を
                // 呼ぶ実装だと、未フォーカス状態でフォーカス遷移 / button rebuild と
                // 競合して MouseUp が届かないケースがあるが、ContextMenuStrip は
                // フレームワーク側で確実に表示される。FocusTerminal は呼ばないので
                // MDI 最大化伝播も起きない。
                var ctxMenu = BuildMdiButtonContextMenuStrip(captured);
                btn.ContextMenuStrip = ctxMenu;
                btn.Disposed += (_, _) => ctxMenu.Dispose();
                _mdiButtonBar.Controls.Add(btn);
                idx++;
            }
        }
        finally
        {
            _mdiButtonBar.ResumeLayout();
        }
    }

    /// <summary>
    /// レガシーコメント行フィルタ (UDR-amm-20260424T1015-9b9 由来)。
    /// TrimStart した先頭が ' または // で始まる行を除去する。
    /// per-command フィルタ (UDR-amm-20260427T0055-2c1) の override として
    /// 「設定 → コメント行を送信しない」が ON の時のみ追加適用する。
    /// </summary>
    internal static string ApplyCommentFilter(string input)
    {
        if (string.IsNullOrEmpty(input)) return input;
        var lines = input.Replace("\r\n", "\n").Split('\n');
        var kept = new List<string>(lines.Length);
        foreach (var line in lines)
        {
            var t = line.TrimStart();
            if (t.StartsWith('\'') || t.StartsWith("//")) continue;
            kept.Add(line);
        }
        return string.Join("\n", kept);
    }

    /// <summary>
    /// per-command の collapseBlankLines / commentPrefixes を適用したうえで、
    /// 必要に応じてレガシーグローバル toggle を上乗せする送信前処理の単一窓口
    /// (UDR-amm-20260427T0055-2c1)。送信先 profile が異なれば結果も異なるため、
    /// broadcast 時は target ごとに呼び分ける。
    /// </summary>
    internal static string ApplyPerCommandFilter(string input, SessionProfile profile, bool legacyGlobalFilter)
    {
        if (string.IsNullOrEmpty(input)) return input;
        var rawLines = input.Replace("\r\n", "\n").Split('\n');
        var filtered = profile.FilterLinesForSend(rawLines);
        var joined = string.Join("\n", filtered);
        if (legacyGlobalFilter) joined = ApplyCommentFilter(joined);
        return joined;
    }

    private void OnHistoryButtonClick(object? sender, EventArgs e)
    {
        var recent = _inputHistory.GetRecent(20);
        if (recent.Count == 0)
        {
            var empty = new ContextMenuStrip();
            empty.Items.Add(new ToolStripMenuItem("(履歴なし)") { Enabled = false });
            empty.Show(_historyButton, new Point(0, _historyButton.Height));
            return;
        }

        var menu = new ContextMenuStrip();
        foreach (var entry in recent)
        {
            var display = entry.Replace("\r\n", " ↩ ").Replace('\n', '↩').Replace('\r', '↩');
            if (display.Length > 80) display = display[..80] + "…";
            var captured = entry; // closure 用
            menu.Items.Add(new ToolStripMenuItem(display, null, (_, _) =>
            {
                _inputBox.Text = captured;
                _inputBox.SelectionStart = _inputBox.Text.Length;
                _inputBox.Focus();
            }));
        }
        menu.Show(_historyButton, new Point(0, _historyButton.Height));
    }

    /// <summary>
    /// Ctrl+E / MDI ボタン長押しメニューから呼ばれる「アクティブ MDI 向けの
    /// エディタ連携起動」。bridge が未生成なら入力欄の現在内容を初期表示として
    /// 一時ファイルに書き込み、関連付け (or 設定の) エディタで開く。既存 bridge
    /// が生きていれば、同じ一時ファイルに対してエディタを再起動するのみで内容は
    /// 保持する。
    /// </summary>
    private void StartEditorBridgeForActive()
    {
        if (ResolveSendTarget() is not { } child)
        {
            MessageBox.Show(this,
                "アクティブな MDI がありません。",
                "エディタ連携",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        StartEditorBridgeFor(child);
    }

    private void StartEditorBridgeFor(TerminalChildForm target)
    {
        if (target.IsDisposed) return;
        try
        {
            var existing = _editorBridges.FirstOrDefault(b => ReferenceEquals(b.Target, target) && b.IsActive);
            if (existing != null)
            {
                // 既に bridge があるならエディタを再前面化するだけ。内容は触らない。
                existing.RelaunchEditor();
                return;
            }
            var initial = _inputBox.Text ?? "";
            var bridge = EditorBridge.CreateAndLaunch(this, target, initial);
            _editorBridges.Add(bridge);
        }
        catch (Exception ex)
        {
            AppLogger.Error("editor bridge launch failed (per-target)", ex);
            MessageBox.Show(this,
                $"エディタの起動に失敗しました:\n{ex.Message}",
                "エディタ連携",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    /// <summary>
    /// 入力欄 (選択中なら選択範囲) の内容を、指定した MDI に送信する。MDI ボタンの
    /// ダブルクリック / 長押しメニュー「プロンプト送信」から共用される。
    /// </summary>
    // 長押し / ダブルクリック / 右クリックメニューの async void ラムダから await される
    // 共有経路。ここで例外を吸収しておくことで、それら全呼び出し元の UI クラッシュを防ぐ。
    private async Task SendInputToTargetAsync(TerminalChildForm target)
    {
        try
        {
            if (target.IsDisposed || !target.IsProcessRunning) return;

            bool hadSelection = _inputBox.SelectionLength > 0;
            var rawText = hadSelection ? _inputBox.SelectedText : _inputBox.Text;
            if (string.IsNullOrEmpty(rawText)) return;
            var sendText = ApplyPerCommandFilter(rawText, target.Profile, _commentFilterEnabled);
            if (string.IsNullOrEmpty(sendText)) return;

            _inputHistory.Add(rawText);
            await DispatchSendAsync(target, sendText, submit: _sendSubmitEnterMenuItem.Checked);
            if (_clearAfterSendMenuItem.Checked && !hadSelection) _inputBox.Clear();
            _inputHistory.ResetCursor();
            target.FocusTerminal();
        }
        catch (Exception ex)
        {
            AppLogger.Error("[send] SendInputToTargetAsync で例外", ex);
        }
    }

    /// <summary>
    /// 整列ボタン: 既定で「記憶した配置で表示」を実行。AMM ファイルに配置情報
    /// (AutoStartCount または windowGeometry) が無ければ「タイル縦 (開いた順)」
    /// にフォールバックする。表示メニュー側の有効/無効判定と同じ条件を使う。
    /// </summary>
    private void OnAlignButtonClick(object? sender, EventArgs e)
    {
        bool hasMemorized = _profiles.Any(p =>
            p.AutoStartCount > 0 || (p.WindowGeometry?.Length ?? 0) > 0);
        if (hasMemorized)
            OnRestoreMemorizedLayout();   // 記憶配置は絶対座標 → 追従しない (内部で _autoFitLayout=null)
        else
            ApplyTileLayout(() => TileMdiInOpenOrder(MdiLayout.TileVertical));
    }

    /// <summary>
    /// 標準 LayoutMdi(TileVertical/TileHorizontal) は内部の z-order でタイル順が
    /// 決まり、アクティブ切替の度に並びが不安定になるため、_childOrder で持って
    /// いる「開いた順」で左上 → 右下のグリッド配置を行う。
    /// - TileVertical: cols = ceil(sqrt(N)), rows = ceil(N / cols) → 横長グリッド
    ///   (縦帯が左から右に並ぶ感覚)
    /// - TileHorizontal: rows = ceil(sqrt(N)), cols = ceil(N / rows) → 縦長グリッド
    ///   (横帯が上から下に積まれる感覚)
    /// 最終列 / 最終行は端数を吸収して余白なく埋める。
    /// 最小化 / 最大化中の子は Normal に戻してから配置する。
    /// </summary>
    private void TileMdiInOpenOrder(MdiLayout layout)
    {
        // 最小化中の子は並び替え対象外 (最小化状態を維持したまま残す)。
        var alive = _childOrder
            .Where(c => !c.IsDisposed && c.Visible && c.WindowState != FormWindowState.Minimized)
            .ToList();
        if (alive.Count == 0) return;

        var mdiClient = Controls.OfType<MdiClient>().FirstOrDefault();
        if (mdiClient == null)
        {
            // 念のためのフォールバック (通常到達しない)。z-order 起点の標準動作。
            LayoutMdi(layout);
            return;
        }

        foreach (var c in alive)
        {
            if (c.WindowState != FormWindowState.Normal)
                c.WindowState = FormWindowState.Normal;
        }

        var area = mdiClient.ClientSize;
        if (area.Width <= 0 || area.Height <= 0) return;

        int n = alive.Count;
        int cols, rows;
        if (layout == MdiLayout.TileHorizontal)
        {
            rows = (int)Math.Ceiling(Math.Sqrt(n));
            cols = (int)Math.Ceiling((double)n / rows);
        }
        else
        {
            cols = (int)Math.Ceiling(Math.Sqrt(n));
            rows = (int)Math.Ceiling((double)n / cols);
        }
        int cellW = area.Width / cols;
        int cellH = area.Height / rows;

        // 最終行のウィンドウ数を求め、不足分の横幅を均等拡張して隙間をなくす。
        int lastRowStart = cols * (rows - 1);
        int lastRowCount = n - lastRowStart;

        for (int i = 0; i < n; i++)
        {
            int row = i / cols;
            int col = i % cols;
            bool isLastRow = (row == rows - 1);

            int x, w;
            if (isLastRow)
            {
                // 最終行のみ独自セル幅で均等割り → 余白なし
                int lastCellW = area.Width / lastRowCount;
                int lastCol = i - lastRowStart;
                x = lastCol * lastCellW;
                w = (lastCol == lastRowCount - 1) ? area.Width - x : lastCellW;
            }
            else
            {
                x = col * cellW;
                w = (col == cols - 1) ? area.Width - x : cellW;
            }
            int y = row * cellH;
            int h = isLastRow ? area.Height - y : cellH;
            alive[i].SetBounds(x, y, w, h);
        }
    }

    /// <summary>
    /// グリッド整列をやめ、開いた順に全ウィンドウを 1 列 (縦) / 1 行 (横) で並べる。
    /// - vertical=true : フル幅で上から下へ縦積み。
    /// - vertical=false: フル高で左から右へ横並び。
    /// クライアント領域を均等割りして余白なく埋める (オーバーフローさせず MDI
    /// クライアント側にスクロールバーを出さない)。最小化 / 最大化中の子は Normal に
    /// 戻してから配置する。
    /// </summary>
    private void TileMdiLinear(bool vertical)
    {
        // 最小化中の子は並び替え対象外 (最小化状態を維持したまま残す)。
        var alive = _childOrder
            .Where(c => !c.IsDisposed && c.Visible && c.WindowState != FormWindowState.Minimized)
            .ToList();
        if (alive.Count == 0) return;

        var mdiClient = Controls.OfType<MdiClient>().FirstOrDefault();
        if (mdiClient == null) return;

        foreach (var c in alive)
        {
            if (c.WindowState != FormWindowState.Normal)
                c.WindowState = FormWindowState.Normal;
        }

        var area = mdiClient.ClientSize;
        if (area.Width <= 0 || area.Height <= 0) return;
        int n = alive.Count;

        if (vertical)
        {
            int cellH = area.Height / n;
            for (int i = 0; i < n; i++)
            {
                int y = i * cellH;
                int h = (i == n - 1) ? area.Height - y : cellH; // 最終ウィンドウで端数吸収
                alive[i].SetBounds(0, y, area.Width, h);
            }
        }
        else
        {
            int cellW = area.Width / n;
            for (int i = 0; i < n; i++)
            {
                int x = i * cellW;
                int w = (i == n - 1) ? area.Width - x : cellW; // 最終ウィンドウで端数吸収
                alive[i].SetBounds(x, 0, w, area.Height);
            }
        }
    }

    /// <summary>生存している MDI 子の数。</summary>
    private int CountAliveChildren() =>
        _childOrder.Count(c => !c.IsDisposed && c.Visible);

    /// <summary>
    /// MDI 子がちょうど 1 つだけのとき、その子を最大化 (全画面) する。
    /// 初回起動の 1 つめ起動時、および複数から 1 つに減ったクローズ時に呼ぶ。
    /// </summary>
    private void MaximizeIfSingleChild()
    {
        var alive = _childOrder.Where(c => !c.IsDisposed && c.Visible).ToList();
        if (alive.Count == 1 && alive[0].WindowState != FormWindowState.Maximized)
            alive[0].WindowState = FormWindowState.Maximized;
    }

    private void OnEditorButtonClick(object? sender, EventArgs e)
    {
        // エディタ連携の送信先となる MDI をポップアップメニューで選択させる。
        // 選んだ時点でその MDI に紐づく一時ファイル (名前に MDI 名を含む) を
        // 作成し、関連付けアプリで開く。以降その MDI に固定される。
        var alive = _childOrder.Where(c => !c.IsDisposed && c.IsProcessRunning).ToList();
        var menu = new ContextMenuStrip();

        if (alive.Count == 0)
        {
            menu.Items.Add(new ToolStripMenuItem("(起動中の MDI がありません)") { Enabled = false });
            menu.Show(_editorButton, new Point(0, _editorButton.Height));
            return;
        }

        int idx = 1;
        foreach (var child in alive)
        {
            var captured = child;
            var label = $"[{idx}] {child.Text}";
            menu.Items.Add(new ToolStripMenuItem(label, null, (_, _) =>
            {
                try
                {
                    var bridge = EditorBridge.CreateAndLaunch(this, captured);
                    _editorBridges.Add(bridge);
                }
                catch (Exception ex)
                {
                    AppLogger.Error("editor bridge launch failed", ex);
                    MessageBox.Show(this,
                        $"エディタの起動に失敗しました:\n{ex.Message}",
                        "エディタ連携",
                        MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
            }));
            idx++;
        }
        menu.Show(_editorButton, new Point(0, _editorButton.Height));
    }

    /// <summary>
    /// MDI 切替ボタンの右クリックメニューを構築する。対象 MDI に対する動作を
    /// 4 種類含む:
    ///   ・プロンプト送信       = 入力欄の内容を対象 MDI に送信
    ///   ・エディタ連携起動     = 対象 MDI 用の EditorBridge を起動 (Ctrl+E と同等)
    ///   ・名前変更…           = 子側 ShowRenameDialog を呼び表示名を変更
    ///   ・フォントサイズ変更 ▶ = FontSizePresets の各サイズへ即時切替
    /// <see cref="Control.ContextMenuStrip"/> プロパティに割り当てることで、WinForms
    /// が WM_CONTEXTMENU を受けてカーソル直下に自動表示する。
    /// </summary>
    private ContextMenuStrip BuildMdiButtonContextMenuStrip(TerminalChildForm target)
    {
        var menu = new ContextMenuStrip();

        // 「改行送信」: 右クリックだけで Enter (\r) を 1 つ送る。ターミナル本体
        // 右クリック (ShowContextMenu) と同位置で最上段に固定。
        menu.Items.Add(new ToolStripMenuItem("改行送信", null, (_, _) =>
        {
            if (target.IsDisposed) return;
            target.SendNewline();
            target.FocusTerminal();
        }));
        // 「プロンプト再送信」: ↑ + Enter で直前の入力履歴を再実行する。
        menu.Items.Add(new ToolStripMenuItem("プロンプト再送信", null, async (_, _) =>
        {
            if (target.IsDisposed) return;
            await target.ResendPreviousPromptAsync();
            if (!target.IsDisposed) target.FocusTerminal();
        }));
        menu.Items.Add(new ToolStripSeparator());

        // クイック送信 ▶ サブメニュー (定型プロンプト)。profile.QuickPrompts が
        // 空のときは何も追加しない (UX 簡潔性)。各エントリは入力欄を介さず
        // DispatchSendAsync 経由で直接 ConPTY へ届け、UseBracketedPaste /
        // SendLineByLine など profile 設定を尊重する。
        var quickPrompts = target.Profile.QuickPrompts ?? Array.Empty<QuickPrompt>();
        if (quickPrompts.Length > 0)
        {
            var quickMenu = new ToolStripMenuItem("クイック送信");
            foreach (var qp in quickPrompts)
            {
                var capturedPrompt = qp.Prompt ?? "";
                var label = string.IsNullOrEmpty(qp.Label) ? capturedPrompt : qp.Label;
                quickMenu.DropDownItems.Add(new ToolStripMenuItem(label, null, async (_, _) =>
                {
                    if (target.IsDisposed || !target.IsProcessRunning) return;
                    if (string.IsNullOrEmpty(capturedPrompt)) return;
                    await DispatchSendAsync(target, capturedPrompt);
                    target.FocusTerminal();
                }));
            }
            menu.Items.Add(quickMenu);
            menu.Items.Add(new ToolStripSeparator());
        }

        menu.Items.Add(new ToolStripMenuItem("プロンプト送信", null, async (_, _) =>
        {
            if (target.IsDisposed) return;
            await SendInputToTargetAsync(target);
        }));
        menu.Items.Add(new ToolStripMenuItem("エディタ連携", null, (_, _) =>
        {
            if (target.IsDisposed) return;
            StartEditorBridgeFor(target);
        }));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("名前変更…", null, (_, _) =>
        {
            if (target.IsDisposed) return;
            target.ShowRenameDialog();
        }));

        var fontMenu = new ToolStripMenuItem("フォントサイズ変更");
        foreach (var (label, size) in FontSizePresets.All)
        {
            var sz = size;
            var item = new ToolStripMenuItem(label, null, (_, _) =>
            {
                if (target.IsDisposed) return;
                target.SetFontSize(sz);
            });
            // 現在値にチェックを付ける。SetFontSize 後に再生成するメニューでは
            // CurrentFontSize が新しい値を反映するので、次回表示時に追従する。
            item.Checked = (sz == target.CurrentFontSize);
            fontMenu.DropDownItems.Add(item);
        }
        menu.Items.Add(fontMenu);

        // 並び替え: _childOrder 上の位置を 1 つ上 / 下に移動。ボタンバーと
        // 表示メニュー (DropDownOpening で自動更新) の両方に即時反映される。
        menu.Items.Add(new ToolStripSeparator());
        var curIdx = _childOrder.IndexOf(target);
        var moveUpItem = new ToolStripMenuItem("左へ移動(&L)", null, (_, _) =>
        {
            var i = _childOrder.IndexOf(target);
            if (i <= 0) return;
            (_childOrder[i - 1], _childOrder[i]) = (_childOrder[i], _childOrder[i - 1]);
            RefreshMdiButtonBar();
        }) { Enabled = curIdx > 0 };
        var moveDownItem = new ToolStripMenuItem("右へ移動(&R)", null, (_, _) =>
        {
            var i = _childOrder.IndexOf(target);
            if (i < 0 || i >= _childOrder.Count - 1) return;
            (_childOrder[i], _childOrder[i + 1]) = (_childOrder[i + 1], _childOrder[i]);
            RefreshMdiButtonBar();
        }) { Enabled = curIdx >= 0 && curIdx < _childOrder.Count - 1 };
        menu.Items.Add(moveUpItem);
        menu.Items.Add(moveDownItem);

        // チャット記録トグル: profiles.amm の初期値を引き継ぎ、実行時に ON/OFF 可能。
        var recItem = new ToolStripMenuItem("チャット記録(&C)")
        {
            CheckOnClick = true,
            Checked      = target.ChatRecordEnabled,
        };
        recItem.CheckedChanged += (_, _) => target.ChatRecordEnabled = recItem.Checked;
        menu.Items.Add(recItem);

        // 統計情報サブメニュー: 出力オンオフのトグルと表示をまとめる。
        var statsMenu = new ToolStripMenuItem("統計情報(&T)");

        var statsItem = new ToolStripMenuItem("統計情報を記録(&E)")
        {
            CheckOnClick = true,
            Checked      = target.StatsEnabled,
        };
        statsItem.CheckedChanged += (_, _) => target.StatsEnabled = statsItem.Checked;
        statsMenu.DropDownItems.Add(statsItem);

        statsMenu.DropDownItems.Add(new ToolStripMenuItem("統計情報を表示…", null, (_, _) =>
        {
            if (target.IsDisposed) return;
            ShowChatStatsDialog(target);
        }));

        menu.Items.Add(statsMenu);

        return menu;
    }

    /// <summary>
    /// 指定 MDI の作業ディレクトリ配下の統計情報 (&lt;workDir&gt;\.amm\stats\&lt;yyyyMMdd&gt;\)
    /// をダイアログで一覧表示する。日付は変更可能、同じ作業ディレクトリを共有する他の
    /// MDI の集計も合わせて表示する。
    /// </summary>
    private static void ShowChatStatsDialog(TerminalChildForm target)
    {
        var workDir = string.IsNullOrEmpty(target.OverrideWorkingDirectory)
            ? (target.Profile.ResolveWorkingDirectory() ?? Environment.CurrentDirectory)
            : target.OverrideWorkingDirectory;
        using var dlg = new ChatStatsDialog(workDir);
        dlg.ShowDialog();
    }

    /// <summary>
    /// MDI クイック切替バー用のボタン。標準 Button では DoubleClick が無効
    /// (StandardDoubleClick=false) なので、両 style を有効化して Click /
    /// DoubleClick の共存を許す。長押し送信は RefreshMdiButtonBar 側で
    /// 個別タイマを張って処理する。
    /// </summary>
    private sealed class MdiSwitchButton : Button
    {
        public MdiSwitchButton()
        {
            SetStyle(ControlStyles.StandardClick | ControlStyles.StandardDoubleClick, true);
        }
    }

    /// <summary>
    /// 貼り付け時にクリップボードの改行を CRLF へ正規化する複数行 TextBox。
    /// Win32 EDIT コントロールは CRLF のみを改行として描画するため、LF 単体
    /// (LF-EOL のファイルや端末/WebView 由来) を貼ると 1 行に潰れて見える。
    /// 既に CRLF の場合の二重化を防ぐため、一旦 LF へ畳んでから CRLF に展開する。
    /// Ctrl+V / Shift+Insert / 右クリック貼り付けはいずれも WM_PASTE を経るので
    /// ここで一括して正規化できる。
    /// </summary>
    private sealed class NormalizingTextBox : TextBox
    {
        private const int WM_PASTE = 0x0302;

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_PASTE)
            {
                try
                {
                    if (Clipboard.ContainsText())
                    {
                        var text = Clipboard.GetText();
                        if (!string.IsNullOrEmpty(text))
                        {
                            var normalized = text
                                .Replace("\r\n", "\n")
                                .Replace("\r", "\n")
                                .Replace("\n", "\r\n");
                            SelectedText = normalized; // キャレット位置に挿入 (選択分は置換)
                            return;
                        }
                    }
                }
                catch { /* クリップボード競合などは既定挙動にフォールバック */ }
            }
            base.WndProc(ref m);
        }
    }

    /// <summary>
    /// テキストエディタ連携のライフサイクル。一時ファイルを作成し関連付け
    /// アプリを起動、保存イベントを検知して紐づけた子ウィンドウへ送信する。
    /// </summary>
    private sealed class EditorBridge : IDisposable
    {
        private readonly MdiParentForm _parent;
        private readonly TerminalChildForm _target;
        private readonly string _filePath;
        private readonly FileSystemWatcher _watcher;
        private readonly System.Windows.Forms.Timer _debounce;
        private string _lastSentHash = "";
        private bool _disposed;

        public TerminalChildForm Target => _target;
        public string FilePath => _filePath;
        public bool IsActive => !_disposed && File.Exists(_filePath);

        private EditorBridge(MdiParentForm parent, TerminalChildForm target, string filePath)
        {
            _parent = parent;
            _target = target;
            _filePath = filePath;

            _watcher = new FileSystemWatcher(
                Path.GetDirectoryName(filePath)!,
                Path.GetFileName(filePath))
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size,
                EnableRaisingEvents = true,
            };
            _debounce = new System.Windows.Forms.Timer { Interval = 500 };
            _debounce.Tick += OnDebounceElapsed;

            _watcher.Changed += OnWatcherChanged;
            _target.FormClosed += OnTargetClosed;
        }

        public static EditorBridge CreateAndLaunch(MdiParentForm parent, TerminalChildForm target, string? initialContent = null)
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "amm", "editor");
            Directory.CreateDirectory(dir);

            // ファイル名に MDI 名を埋め込んで「どのターミナル向けの下書きか」を
            // エディタのタブや保存ダイアログから判別できるようにする。
            // 禁則文字を _ に置換してから `prompt-<name>[(n)]-<short>.md` の形に。
            static string Sanitize(string s) =>
                string.Concat(s.Select(ch =>
                    Path.GetInvalidFileNameChars().Contains(ch) ? '_' : ch));
            var safeName = Sanitize(target.ProfileName);
            var instPart = target.InstanceNumber > 1 ? $"({target.InstanceNumber})" : "";
            var shortId = Guid.NewGuid().ToString("N")[..6];
            var fileName = $"prompt-{safeName}{instPart}-{shortId}.md";
            var filePath = Path.Combine(dir, fileName);
            // ヘッダ (HTML コメント) は AI への送信側でフィルタしづらいので、初期内容
            // を渡されたときは「ヘッダなし」で本文だけ書く。空のときは従来通り使い方
            // 説明コメントを置く。これで Ctrl+E 初回起動時は入力欄の文章がそのまま
            // エディタに表示される (UDR-amm-202605xxx: 初回プリフィル仕様)。
            string body;
            if (!string.IsNullOrEmpty(initialContent))
            {
                body = initialContent;
            }
            else
            {
                body =
                    $"<!-- amm エディタ連携: 保存するたびに [{target.Text}] へ送信されます。\n" +
                    $"     このウィンドウを閉じるか子ターミナルが終了すると自動削除されます。 -->\n";
            }
            File.WriteAllText(filePath, body);

            var bridge = new EditorBridge(parent, target, filePath);

            try
            {
                LaunchEditor(parent, filePath);
            }
            catch
            {
                bridge.Dispose();
                throw;
            }
            return bridge;
        }

        /// <summary>
        /// 同じ一時ファイルに対して再度エディタを起動する。Ctrl+E を再度押された
        /// 際の「エディタウィンドウを再前面化」用パス (内容は触らない)。
        /// </summary>
        public void RelaunchEditor()
        {
            if (_disposed) return;
            LaunchEditor(_parent, _filePath);
        }

        /// <summary>
        /// 設定 (_editorMode / _customEditorPath) に従ってエディタを起動する。
        /// "Notepad"   → notepad.exe (常に存在)
        /// "Custom"    → ユーザ指定の exe を引数にファイルパスで起動。失敗 (パス
        ///                未設定や見つからない) なら関連付けにフォールバック
        /// "Associated" / その他 → 関連付けアプリ (ShellExecute)
        /// </summary>
        private static void LaunchEditor(MdiParentForm parent, string filePath)
        {
            switch (parent._editorMode)
            {
                case "Notepad":
                    Process.Start(new ProcessStartInfo("notepad.exe", $"\"{filePath}\"")
                    {
                        UseShellExecute = false,
                    });
                    return;
                case "Custom":
                    var exe = parent._customEditorPath?.Trim();
                    if (!string.IsNullOrEmpty(exe) && File.Exists(exe))
                    {
                        Process.Start(new ProcessStartInfo(exe, $"\"{filePath}\"")
                        {
                            UseShellExecute = false,
                        });
                        return;
                    }
                    // パス未設定 / 不在のときは関連付けに落とす
                    AppLogger.Warn($"editor: custom path not found, falling back to associated app: '{exe}'");
                    break;
            }
            Process.Start(new ProcessStartInfo(filePath) { UseShellExecute = true });
        }

        private void OnWatcherChanged(object sender, FileSystemEventArgs e)
        {
            if (_disposed) return;
            // ワーカースレッド → UI スレッドに marshal。debounce で連打を抑制。
            try
            {
                _parent.BeginInvoke(() =>
                {
                    if (_disposed) return;
                    _debounce.Stop();
                    _debounce.Start();
                });
            }
            catch (ObjectDisposedException) { /* parent 破棄後 */ }
            catch (InvalidOperationException) { }
        }

        private async void OnDebounceElapsed(object? sender, EventArgs e)
        {
            _debounce.Stop();
            if (_disposed) return;
            if (_target.IsDisposed || !_target.IsProcessRunning) { Dispose(); return; }

            string text;
            try
            {
                using var fs = new FileStream(_filePath, FileMode.Open, FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete);
                using var sr = new StreamReader(fs);
                text = sr.ReadToEnd();
            }
            catch
            {
                // 保存直後でエディタがまだ握っている。もう一度 debounce で待つ。
                _debounce.Start();
                return;
            }

            if (string.IsNullOrEmpty(text)) return;

            // 空保存や重複保存の無限送信を防ぐ
            var hash = $"{text.Length}:{text.GetHashCode()}";
            if (hash == _lastSentHash) return;
            _lastSentHash = hash;

            var send = ApplyPerCommandFilter(text, _target.Profile, _parent._commentFilterEnabled);
            if (string.IsNullOrEmpty(send)) return;
            // SendLineByLine プロファイル (Copilot CLI 等) でも確定 Enter が
            // 落ちるよう、エディットボックスからの送信と同じ DispatchSendAsync
            // 経由に揃える。直接 SendText を呼ぶと 1 段 Enter しか飛ばない。
            // async void イベント (Timer.Tick) なので送信例外で UI をクラッシュさせない。
            try
            {
                await DispatchSendAsync(_target, send);
            }
            catch (Exception ex)
            {
                AppLogger.Error("[editor-bridge] 送信で例外", ex);
                return;
            }

            // 送信後の対象 MDI の扱いを「送信」メニュー設定に従って分岐。
            // Maximize は FocusTerminal も併用し、入力をすぐ受けられる状態にする。
            if (_target.IsDisposed) return;
            switch (_parent._editorPostSendAction)
            {
                case "Maximize":
                    if (_target.WindowState != FormWindowState.Maximized)
                        _target.WindowState = FormWindowState.Maximized;
                    _target.FocusTerminal();
                    break;
                case "None":
                    break;
                case "Focus":
                default:
                    _target.FocusTerminal();
                    break;
            }
        }

        private void OnTargetClosed(object? sender, FormClosedEventArgs e) => Dispose();

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            try { _watcher.EnableRaisingEvents = false; } catch { }
            _watcher.Changed -= OnWatcherChanged;
            _target.FormClosed -= OnTargetClosed;
            try { _watcher.Dispose(); } catch { }
            try { _debounce.Dispose(); } catch { }
            try { if (File.Exists(_filePath)) File.Delete(_filePath); } catch { }
            _parent._editorBridges.Remove(this);
        }
    }
}
