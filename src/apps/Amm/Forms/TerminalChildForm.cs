using System.ComponentModel;
using System.Text.Json;
using Microsoft.Web.WebView2.WinForms;
using Amm.Core;
using Amm.Core.Git;

namespace Amm.Forms;

public partial class TerminalChildForm : Form
{
    private readonly WebView2 _webView;
    private readonly SessionProfile _profile;
    private ConPtyWrapper? _conPty;
    private WaitPatternDetector? _waitDetector;
    private StreamWriter? _sessionLogWriter;
    // sessionLog のサイズ上限 (無制限肥大 + 平文機密の際限ない蓄積を防ぐ)。超過したら
    // 末尾に打切り注記を 1 行残して以降の書込みを止める (_sessionLogLock で保護)。
    private const long SessionLogMaxBytes = 50L * 1024 * 1024; // 50 MiB
    private long _sessionLogBytes;
    private bool _sessionLogCapped;
    // 現在表示中のターミナル右クリックメニュー。WebView2 は別 HWND のため、その上を
    // クリックしても ContextMenuStrip の外側クリック自動クローズが効かない。JS の
    // document.click 通知 (click_activate) を契機に明示的に閉じるため参照を保持する。
    private ContextMenuStrip? _activeContextMenu;
    private readonly object _sessionLogLock = new();
    private bool _navigationCompleted;
    private bool _terminalReady;
    // チャット記録: プロファイル設定を起点とし、実行時にトグル可能。
    private bool _chatRecordEnabled;
    private ChatRecorder? _activeRecorder;
    private NativeDropTarget? _nativeDropTarget;
    private readonly List<IntPtr> _dropTargetHwnds = new();
    // xterm.js が fitAddon で算出した実サイズ (ready 時に送られてくる)。
    // これで ConPTY を起動しないと、固定 120×30 と xterm.js の実描画サイズが
    // ズレて cmd.exe のプロンプト/カーソル位置が視覚的に狂う。
    private short _initialCols = 80;
    private short _initialRows = 24;

    private static string WebViewUserDataFolder => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "amm", "WebViewShared");

    /// <summary>
    /// アプリ multi-size .ico を直接ロード。Resources/amm.ico は
    /// 16/20/24/32/40/48/64 を含む multi-size ICO で、MDI 最大化時の
    /// システムメニューには 16x16 が、Alt+Tab には 32x32 が選ばれる。
    /// 取得失敗時は ExtractAssociatedIcon にフォールバック。
    /// </summary>
    internal static Icon LoadAppIcon()
    {
        try
        {
            var path = AppPaths.AppIconPath;
            if (File.Exists(path)) return new Icon(path);
        }
        catch { /* 続行 */ }
        return Icon.ExtractAssociatedIcon(Environment.ProcessPath ?? Application.ExecutablePath)
               ?? SystemIcons.Application;
    }

    public WaitState CurrentWaitState => _waitDetector?.CurrentState ?? WaitState.Unknown;
    public event Action<TerminalChildForm, WaitState>? WaitStateChanged;

    /// <summary>
    /// hook 駆動の入力待ち通知 (UDR-amm-20260605T0523-7e1) でこの MDI を一意に
    /// 識別するトークン。ConPTY 起動時に環境変数 AMM_NOTIFY_ID として子プロセス
    /// へ注入され、CLI の hook が起動する amm-mcp.exe notify が継承 → Named Pipe
    /// 経由で送り返してくる。amm 外で起動した CLI には env が無いので誤通知しない。
    /// </summary>
    public string NotifyToken { get; } = Guid.NewGuid().ToString("N");

    /// <summary>このウィンドウを一意に識別する GUID (req-20260622-mdi-window-control R-4)。
    /// mdi/open の戻り値 / mdi/close・mdi/wait_state の引数として使う。</summary>
    public string SessionId { get; } = Guid.NewGuid().ToString();

    /// <summary>プロファイル名 (タイトル連番管理用の一次キー)。</summary>
    public string ProfileName => _profile.Name;

    /// <summary>この子に紐付く SessionProfile。送信前処理 (collapseBlankLines /
    /// commentPrefixes) や closeProhibited 判定のために親フォームから参照する。</summary>
    public SessionProfile Profile => _profile;

    /// <summary>
    /// チャット記録の有効/無効をランタイムでトグルする。プロファイル値を初期値とし、
    /// MDI 右クリックメニューで切り替えられる (profiles.amm への永続化は行わない)。
    /// </summary>
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool ChatRecordEnabled
    {
        get => _chatRecordEnabled;
        set => _chatRecordEnabled = value;
    }

    /// <summary>
    /// コマンド送信時に呼ぶ。前回の記録が未完了なら先に完了させてから新規開始する。
    /// </summary>
    public void StartChatRecording(string command)
    {
        _activeRecorder?.Complete();
        var workDir = string.IsNullOrEmpty(OverrideWorkingDirectory)
            ? (_profile.ResolveWorkingDirectory() ?? Environment.CurrentDirectory)
            : OverrideWorkingDirectory;
        var saveDir = Path.Combine(workDir, ".amm");
        _activeRecorder = new ChatRecorder(
            saveDir, _profile.ChatRecordTailChars,
            _profile.Name, DisplayName, command);
    }

    /// <summary>同一プロファイル内のインスタンス番号 (1-based)。1 の時はタイトルに番号を付けない。</summary>
    public int InstanceNumber { get; }

    /// <summary>
    /// このインスタンス限定の作業ディレクトリ上書き。<c>SelectWorkingDirOnStart=true</c>
    /// の起動時フォルダ選択ダイアログ、または「現在の配置を記憶」で保存された
    /// <c>windowGeometry[i].workingDirectory</c> の復元値として親フォームから渡される。
    /// null/空のときは <see cref="SessionProfile.ResolveWorkingDirectory"/> にフォールバック。
    /// <para>
    /// 値は ConPTY 起動時 (HandleCreated 直後の OnTerminalSetup) に消費される。
    /// 起動後にこのプロパティを書き換えても子プロセスの CWD は変わらない (ConPTY
    /// を再起動しない限り反映されない)。次回の「現在の配置を記憶」スナップショット
    /// では最新値が書き出されるため、保存用メタデータとしてのみ意味を持つ。
    /// </para>
    /// </summary>
    [System.ComponentModel.Browsable(false)]
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public string? OverrideWorkingDirectory { get; set; }

    /// <summary>ConPTY 起動時に使った作業ディレクトリ (環境変数展開済み)。</summary>
    internal string StartupWorkingDirectory =>
        !string.IsNullOrWhiteSpace(OverrideWorkingDirectory)
            ? Environment.ExpandEnvironmentVariables(OverrideWorkingDirectory)
            : _profile.ResolveWorkingDirectory() ?? "";

    // システムメニュー「名前変更…」で設定されるユーザー定義の表示名。
    // null の間は profile.Name + InstanceNumber 接尾辞をそのまま使う。
    // profiles.amm / Nickname / インスタンス番号払い出しには影響させない仕様
    // (= 純粋な MDI ウィンドウ名のみの一時変更)。
    private string? _customDisplayName;

    /// <summary>MDI ウィンドウやクイック切替バー / 表示メニューで使う表示名。
    /// rename されていれば custom 名、なければ profile.Name (+ インスタンス番号)。</summary>
    public string DisplayName =>
        _customDisplayName ?? (InstanceNumber == 1 ? _profile.Name : $"{_profile.Name} ({InstanceNumber})");

    /// <summary>ユーザがシステムメニュー「名前変更…」で設定した一時表示名。
    /// 未設定なら null。「現在状態を設定に反映」が読む。</summary>
    public string? CustomDisplayName => _customDisplayName;

    /// <summary>「現在状態を設定に反映」で profile.Name へ昇格させた後に呼び、
    /// 一時表示名をクリアして profile 派生名にフォールバックさせる。</summary>
    public void ClearCustomDisplayName()
    {
        if (_customDisplayName == null) return;
        _customDisplayName = null;
        RefreshTitleForCurrentState();
        DisplayNameChanged?.Invoke(this);
    }

    /// <summary>
    /// AMM ファイル (WindowGeometryEntry.Name) から復元した表示名を、ダイアログを
    /// 経由せず直接適用する。null/空ならクリアと同等。DisplayNameChanged を発火し
    /// 親側 (button bar / profiles dirty 追跡) も同期させる。
    /// </summary>
    internal void ApplyCustomDisplayName(string? name)
    {
        var normalized = string.IsNullOrEmpty(name) ? null : name;
        if (_customDisplayName == normalized) return;
        _customDisplayName = normalized;
        RefreshTitleForCurrentState();
        DisplayNameChanged?.Invoke(this);
    }

    private void RefreshTitleForCurrentState()
    {
        Text = FormatTitle(WaitStateGlyph.For(CurrentWaitState, HasAttention));
    }

    /// <summary>表示名が変更されたときに発火。親側の button bar / 表示メニュー再描画用。</summary>
    public event Action<TerminalChildForm>? DisplayNameChanged;

    /// <summary>システムメニュー「AMM 設定…」を選択された時に親へ通知するイベント。
    /// 親側でダイアログを出し、結果を Profile に反映 + 必要なら profiles.amm 書き戻し。</summary>
    public event Action<TerminalChildForm>? AmmSettingsRequested;

    /// <summary>Phase 4 (UDR-amm-20260427T0238-fb5): エディタ連携ファイルパスコピー要求。
    /// 親側で _editorBridges から該当 child の bridge を引き、temp ファイルパスを
    /// クリップボードに格納する。</summary>
    public event Action<TerminalChildForm>? EditorPathCopyRequested;

    /// <summary>システムメニュー「エディタ連携」: この MDI 用にエディタ連携 bridge を
    /// 起動するだけのコマンド。クリップボードへのパスコピーはしない。</summary>
    public event Action<TerminalChildForm>? EditorLinkRequested;

    /// <summary>ターミナル本体の右クリックメニュー「クイック送信 ▶」からのプロンプト
    /// 送信要求。親 (MdiParentForm) が DispatchSendAsync 経由で ConPTY に流す。
    /// MDI 切替バー側のクイック送信と同一動線。</summary>
    public event Action<TerminalChildForm, string>? QuickPromptRequested;

    /// <summary>右クリック「クイック送信に登録...」でプロファイルへの追加を親に依頼する。
    /// 引数は (TerminalChildForm, label, prompt)。親が QuickSendRegisterDialog を出し
    /// 確定後に SessionProfile.QuickPrompts を更新して保存する。</summary>
    public event Action<TerminalChildForm, string, string>? QuickSendRegisterRequested;

    // 直前に端末へ転送したテキスト (JS の lastForward.text)。context_menu メッセージ
    // で毎回送られてくる。右クリック「クイック送信に登録...」の初期値に使う。
    private string _lastForwardForMenu = string.Empty;

    // ---- アイドル時自動送信 (req-20260622-auto-send-idle) ----
    private System.Windows.Forms.Timer? _autoSendCountdownTimer;
    private bool _autoSendArmed;
    private DateTime _autoSendDeadline;
    // OnWaitStateChanged で前の状態を把握するための追跡フィールド。
    // Running → WaitingForInput 遷移でだけカウントダウンを開始するために使う。
    private WaitState _prevWaitState = WaitState.Running;

    /// <summary>ターミナル本体の右クリックメニュー「プロンプト送信」要求。親側で
    /// 入力欄の現在内容を SendInputToTargetAsync 経由で対象 MDI に送信する。
    /// MDI 切替バー右クリック (BuildMdiButtonContextMenuStrip) と同一動線。</summary>
    public event Action<TerminalChildForm>? SendInputRequested;

    /// <summary>子プロセスがまだ生きているか (ConPTY 起動後、ProcessExited が来ていない状態)。</summary>
    public bool IsProcessRunning =>
        _conPty != null && _waitDetector != null && _waitDetector.CurrentState != WaitState.Stopped;

    public TerminalChildForm(SessionProfile profile, int instanceNumber = 1)
    {
        _profile = profile;
        _chatRecordEnabled = profile.ChatRecord;
        InstanceNumber = instanceNumber;
        Text = FormatTitle(WaitStateGlyph.For(WaitState.Running));
        Size = new Size(800, 500);
        // multi-size .ico を直接ロードして Windows に適切なサイズを選ばせる
        // (ExtractAssociatedIcon は単一サイズしか返さないため、MDI 最大化時の
        // システムメニューアイコン位置に大きすぎる絵が表示される)。
        Icon = LoadAppIcon();

        _webView = new WebView2 { Dock = DockStyle.Fill };
        // WebView2 の AllowExternalDrop は true (既定) のまま運用する。false に
        // すると WebView2 の IDropTarget が消え、WinForms 層にもフォールスルー
        // しないため drop 自体が受理されない (Windows は WebView2 の HWND 上に
        // drop target がなく、親 HWND には伝播しない)。
        // そこで AllowExternalDrop=true にして WebView2 に drop を受けさせ、WebView2 が
        // 内部登録する IDropTarget を Revoke → 自前 NativeDropTarget を再登録して
        // ホスト側で処理する (RegisterNativeDropTargetOnWebView)。
        _webView.AllowExternalDrop = true;
        Controls.Add(_webView);

        // 子が maximize されていない時 (フレームの余白) への drop 用フォールバック。
        AllowDrop = true;
        DragEnter += OnFormDragEnter;
        DragOver += OnFormDragEnter;
        DragDrop += OnFormDragDrop;

        Load += OnLoad;
        Activated += OnActivated;
        FormClosing += OnFormClosing;
        HandleCreated += OnHandleCreatedRegisterSysMenu;
    }

    private void OnHandleCreatedRegisterSysMenu(object? sender, EventArgs e)
    {
        // システムメニュー (Alt+Space / タイトルバー左上) に独自項目を追加。
        // MDI 子は最大化時に親 MenuStrip 側へシステムメニューが merge されるが、
        // メニュー項目自体は子の hWnd にぶら下がるため WM_SYSCOMMAND もここで受信できる。
        Win32SystemMenu.RegisterAmmSettings(
            Handle,
            "AMM 設定…",
            "エディタ連携ファイルパスをコピー",
            "名前変更…",
            "エディタ連携");
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == Win32SystemMenu.WM_SYSCOMMAND)
        {
            // wParam の下位 4 ビットはシステム予約。独自 ID は安全帯から取った
            // 値なので 0xFFF0 でマスクしてから比較する。
            int cmd = m.WParam.ToInt32() & 0xFFF0;
            if (cmd == Win32SystemMenu.SC_AMM_SETTINGS)
            {
                AmmSettingsRequested?.Invoke(this);
                return;
            }
            if (cmd == Win32SystemMenu.SC_AMM_COPY_EDITOR_PATH)
            {
                EditorPathCopyRequested?.Invoke(this);
                return;
            }
            if (cmd == Win32SystemMenu.SC_AMM_EDITOR_LINK)
            {
                EditorLinkRequested?.Invoke(this);
                return;
            }
            if (cmd == Win32SystemMenu.SC_AMM_RENAME)
            {
                ShowRenameDialog();
                return;
            }
            if (cmd == Win32SystemMenu.SC_AMM_FONT_XL) { SetFontSize(FontSizePresets.XLarge); return; }
            if (cmd == Win32SystemMenu.SC_AMM_FONT_L)  { SetFontSize(FontSizePresets.Large);  return; }
            if (cmd == Win32SystemMenu.SC_AMM_FONT_M)  { SetFontSize(FontSizePresets.Medium); return; }
            if (cmd == Win32SystemMenu.SC_AMM_FONT_S)  { SetFontSize(FontSizePresets.Small);  return; }
            if (cmd == Win32SystemMenu.SC_AMM_FONT_XS) { SetFontSize(FontSizePresets.XSmall); return; }
        }
        base.WndProc(ref m);
    }

    /// <summary>
    /// 現在の xterm.js フォントサイズ (px)。MDI ボタン右クリックメニューで現在値に
    /// チェックを付けるために C# 側でも追跡する。Profile.FontSize 由来の初期値を
    /// 持ち、SetFontSize で更新する。terminal.html 側の DEFAULT_FONT_SIZE と一致。
    /// </summary>
    internal int CurrentFontSize { get; private set; } = FontSizePresets.Medium;

    /// <summary>
    /// xterm.js のフォントサイズを per-MDI で即時変更する。保存はせず、ウィンドウを
    /// 閉じれば profile の既定値 (Profile.FontSize / 13) に戻る。
    /// </summary>
    internal void SetFontSize(int sizePx)
    {
        if (_webView.CoreWebView2 == null || IsDisposed) return;
        try
        {
            var msg = JsonSerializer.Serialize(new { type = "font_size", size = sizePx });
            _webView.CoreWebView2.PostWebMessageAsString(msg);
            CurrentFontSize = sizePx;
        }
        catch { }
    }

    private string FormatTitle(char prefix, string suffix = "")
    {
        return $"{prefix} {DisplayName}{suffix}";
    }

    private async void OnLoad(object? sender, EventArgs e)
    {
        try
        {
            Directory.CreateDirectory(WebViewUserDataFolder);
            // WebView2 (Edge/Chromium) ランタイムのバックグラウンド通信を抑止する。
            // amm は app.local のローカル HTML しかロードしないため機能影響はなく、
            // Edge の Variations(field trials) / コンポーネント更新 / 診断データ送信を
            // 止める (描画内容=AI 対話/ターミナル出力は元々外部に出ないが、ランタイム
            // 層の匿名テレメトリも閉じてオフライン/閉域要件に対応)。
            var options = new Microsoft.Web.WebView2.Core.CoreWebView2EnvironmentOptions
            {
                AdditionalBrowserArguments =
                    "--disable-background-networking --disable-component-update " +
                    "--disable-features=msEdgeFieldTrialAndVariations,OptimizationHints,Translate " +
                    "--no-first-run",
            };
            var env = await Microsoft.Web.WebView2.Core.CoreWebView2Environment.CreateAsync(
                browserExecutableFolder: null,
                userDataFolder: WebViewUserDataFolder,
                options: options);

            var resourcesPath = AppPaths.ResourcesPath;
            if (!Directory.Exists(resourcesPath))
                throw new DirectoryNotFoundException($"Resources folder not found: {resourcesPath}");

            await _webView.EnsureCoreWebView2Async(env);

            // セキュリティ ハードニング: ターミナルには信頼できない AI CLI 出力が
            // 描画される。DevTools / 既定コンテキストメニュー / ブラウザアクセラレータ
            // キーが有効だと、F12 等から chrome.webview.postMessage を直接叩いて
            // input 経路 (→ ConPTY) に任意文字列を注入できてしまうため明示的に閉じる。
            // contextmenu は terminal.html 側でも preventDefault 済 (二重防御)。
            var s = _webView.CoreWebView2.Settings;
#if DEBUG
            s.AreDevToolsEnabled = true;   // 開発時のみ terminal.html デバッグを許可
#else
            s.AreDevToolsEnabled = false;
#endif
            s.AreDefaultContextMenusEnabled = false;
            s.AreBrowserAcceleratorKeysEnabled = false;
            s.IsStatusBarEnabled = false;
            s.IsPasswordAutosaveEnabled = false;
            s.IsGeneralAutofillEnabled = false;

            // 外部ナビゲーション / 新規ウィンドウは app.local 以外を遮断する。
            // OSC8 ハイパーリンクや出力中 URL を踏んでも WebView が別サイトへ
            // 遷移したり子ウィンドウを開いたりしないようにする多層防御。
            _webView.CoreWebView2.NavigationStarting += OnWebViewNavigationStarting;
            // サブフレーム (将来 iframe が混入した場合) の外部遷移も同じガードで遮断する多層防御。
            _webView.CoreWebView2.FrameNavigationStarting += OnWebViewNavigationStarting;
            _webView.CoreWebView2.NewWindowRequested += OnWebViewNewWindowRequested;

            // Map Resources folder to virtual host
            _webView.CoreWebView2.SetVirtualHostNameToFolderMapping(
                "app.local",
                resourcesPath,
                Microsoft.Web.WebView2.Core.CoreWebView2HostResourceAccessKind.Allow);

            _webView.CoreWebView2.WebMessageReceived += OnWebMessageReceived;
            _webView.CoreWebView2.NavigationCompleted += OnNavigationCompleted;

            // 起動毎のクエリ文字列で WebView2 内の HTTP キャッシュを迂回させる
            // (terminal.html を更新した直後でも古いキャッシュを使われないため)。
            _webView.CoreWebView2.Navigate(
                $"https://app.local/terminal.html?v={DateTime.UtcNow.Ticks}");

            // Chromium は web コンテンツに file:// の絶対パスを渡さない。MDI 子
            // への drop でパス送信機能を成立させるため、WebView2 が内部で HWND に
            // 登録している IDropTarget を Revoke し、自前の NativeDropTarget を再登録
            // する。これで OS→Chromium の経路ではなく OS→自前ハンドラの順で届く。
            RegisterNativeDropTargetOnWebView();
        }
        catch (Exception ex)
        {
            AppLogger.Error($"WebView2 init failed ({_profile.Name})", ex);
            MessageBox.Show($"WebView2 initialization failed:\n{ex.Message}", "Error",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
            Close();
        }
    }

    private void OnNavigationCompleted(object? sender, Microsoft.Web.WebView2.Core.CoreWebView2NavigationCompletedEventArgs e)
    {
        if (!e.IsSuccess)
        {
            MessageBox.Show($"terminal.html load failed: {e.WebErrorStatus}", "Error",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }
        _navigationCompleted = true;
        TryStartConPty();
    }

    private void OnWebViewNavigationStarting(object? sender, Microsoft.Web.WebView2.Core.CoreWebView2NavigationStartingEventArgs e)
    {
        // app.local 仮想ホスト (= 同梱 terminal.html) 以外への遷移は禁止する。
        var uri = e.Uri ?? string.Empty;
        if (!uri.StartsWith("https://app.local/", StringComparison.OrdinalIgnoreCase))
            e.Cancel = true;
    }

    private void OnWebViewNewWindowRequested(object? sender, Microsoft.Web.WebView2.Core.CoreWebView2NewWindowRequestedEventArgs e)
    {
        // 新規ウィンドウ / ポップアップは一切開かない。
        e.Handled = true;
    }

    private void OnWebMessageReceived(object? sender, Microsoft.Web.WebView2.Core.CoreWebView2WebMessageReceivedEventArgs e)
    {
        try
        {
            var json = e.TryGetWebMessageAsString();
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var type = root.GetProperty("type").GetString();

            switch (type)
            {
                case "input":
                    var data = root.GetProperty("data").GetString();
                    if (data != null)
                        _conPty?.Write(data);
                    break;

                case "resize":
                    var cols = root.GetProperty("cols").GetInt16();
                    var rows = root.GetProperty("rows").GetInt16();
                    _conPty?.Resize(cols, rows);
                    break;

                case "ready":
                    if (root.TryGetProperty("cols", out var readyCols) &&
                        root.TryGetProperty("rows", out var readyRows))
                    {
                        _initialCols = readyCols.GetInt16();
                        _initialRows = readyRows.GetInt16();
                    }
                    _terminalReady = true;
                    TryStartConPty();
                    break;

                case "click_activate":
                    // メニュー外 (WebView2 領域) のクリックで、開いている右クリック
                    // メニューを閉じる。WebView2 は別 HWND なので ContextMenuStrip の
                    // 外側クリック自動クローズが効かず、ここで明示的に閉じる。
                    _activeContextMenu?.Close();
                    // JS 側の document.click を契機に MDI 子を確実に activate させる。
                    // WebView2 のクリックが WinForms の MDI activation チェーンに
                    // 乗らないケースの救済措置。
                    if (!IsDisposed && MdiParent != null)
                        Activate();
                    break;

                case "copy":
                    // xterm.js の選択テキストを C# クリップボードへ。
                    // WebMessageReceived は UI スレッド上で呼ばれるので直接 OK。
                    {
                        var copyData = root.GetProperty("data").GetString();
                        var copySource = root.TryGetProperty("source", out var srcEl) ? srcEl.GetString() : null;
                        if (!string.IsNullOrEmpty(copyData))
                        {
                            // OSC52 由来 = 信頼できない端末出力からの「ユーザー操作なし」クリップボード
                            // 書込 (pastejacking の足がかり)。サイズ上限を課し、本文は出さず長さのみ
                            // 監査ログに残す。ユーザー選択コピー (source 無し) は従来どおり無制限。
                            if (copySource == "osc52")
                            {
                                const int Osc52MaxChars = 64 * 1024;
                                if (copyData.Length > Osc52MaxChars)
                                {
                                    AppLogger.Warn($"[osc52] {_profile.Name}: クリップボード書込を破棄 (len={copyData.Length} > {Osc52MaxChars})");
                                    break;
                                }
                                AppLogger.Info($"[osc52] {_profile.Name}: 端末出力由来のクリップボード書込 (len={copyData.Length})");
                            }
                            // Windows クリップボードの慣習に合わせ改行を単一 CRLF へ正規化
                            // (メモ帳等へ貼ったとき改行が無くなる/二重になるのを防ぐ。
                            //  一旦 LF へ畳んでから CRLF へ展開し二重化を回避)。
                            copyData = copyData.Replace("\r\n", "\n").Replace("\r", "\n").Replace("\n", "\r\n");
                            try { Clipboard.SetText(copyData); }
                            catch { /* clipboard が他アプリにロックされている等は無視 */ }
                        }
                    }
                    break;

                case "paste_request":
                    HandlePasteRequest();
                    break;

                case "ime_diag":
                    // terminal.html の IME 二重発火ガードの痕跡。dup_dropped*=重複破棄
                    // (seq=同一keydown / hard=最速リピート未満 / ime_gap=非隣接IME重複)、
                    // dup_passed*=ガードを通り抜けた同一チャンク (hist=非隣接、二重送信調査の鍵)。
                    // detail に composition タイムライン等を含め、原因切り分けに使う。
                    {
                        // 機密保護: 入力本文は記録しない (JS 側で data を送らない多層防御に加え、
                        // 仮に届いてもログに出さない)。reason + detail (len/code 等メタ情報) のみ。
                        var reason = root.TryGetProperty("reason", out var r) ? r.GetString() : null;
                        var detail = root.TryGetProperty("detail", out var dt) ? dt.GetString() : "";
                        AppLogger.Warn($"IME dup guard ({_profile.Name}): {reason} {detail}");
                    }
                    break;

                case "context_menu":
                    {
                        var selectedText = root.TryGetProperty("selectedText", out var selProp)
                            ? selProp.GetString() ?? string.Empty
                            : string.Empty;
                        _lastForwardForMenu = root.TryGetProperty("lastText", out var ltProp)
                            ? ltProp.GetString() ?? string.Empty
                            : string.Empty;
                        ShowContextMenu(selectedText);
                    }
                    break;

                case "osc_notify":
                    // OSC 9 端末通知 (Codex tui.notifications 等)。hook を持たない
                    // CLI のイベント駆動ルート。文言に approval を含めば許可待ち
                    // (attention)、それ以外は「区切りがついた」= idle として扱う。
                    {
                        var text = root.TryGetProperty("data", out var t) ? t.GetString() ?? "" : "";
                        var isAttention = text.Contains("approval", StringComparison.OrdinalIgnoreCase);
                        // 機密保護: OSC9 本文 (CLI が任意文字列を載せられる) は記録せず、
                        // 長さと分類 (attention/idle) のみ残す。
                        AppLogger.Info($"[osc9] {_profile.Name}: len={text.Length} {(isAttention ? "attention" : "idle")}");
                        NotifyExternalWaitState(isAttention ? "attention" : "idle");
                    }
                    break;

            }
        }
        catch (Exception ex)
        {
            // malformed / 未知形状のメッセージは無視するが、プロトコル不整合の切り分け用に
            // 例外種別のみ残す (本文は入力データを含み得るため記録しない)。
            AppLogger.Warn($"web message parse failed ({_profile.Name}): {ex.GetType().Name}");
        }
    }

    private void TryStartConPty()
    {
        if (!_navigationCompleted || !_terminalReady || _conPty != null) return;

        // プロファイル設定を JS 側に push (Ctrl+C 動作等)。
        PushConfigToTerminal();

        _waitDetector = new WaitPatternDetector(_profile.WaitPatterns);
        _waitDetector.StateChanged += OnWaitStateChanged;

        _conPty = new ConPtyWrapper(_profile.GetEncoding());
        _conPty.OutputReceived += OnConPtyOutput;
        _conPty.ProcessExited += OnProcessExited;

        try
        {
            var workingDir = !string.IsNullOrWhiteSpace(OverrideWorkingDirectory)
                ? Environment.ExpandEnvironmentVariables(OverrideWorkingDirectory)
                : _profile.ResolveWorkingDirectory();
            _conPty.Start(
                _profile.BuildLaunchCommandLine(),
                _initialCols,
                _initialRows,
                _profile.AutoChcp,
                workingDir,
                // hook 駆動の入力待ち通知用 (UDR-7e1)。CLI の hook 子プロセス
                // (amm-mcp.exe notify) まで継承され、MDI の逆引きに使われる。
                new Dictionary<string, string> { ["AMM_NOTIFY_ID"] = NotifyToken });

            OpenSessionLogIfRequested();

            // 起動時初期コマンド (profiles.json の initialCommands) を順次送信。
            // 対話ターミナルでの Enter は \r 一文字。\r\n を送ると \n が余計な
            // Enter と見なされ PowerShell PSReadLine が ">>" 継続プロンプトに
            // 入ってしまうため、単純に末尾 \r を付ける。
            if (_profile.InitialCommands.Length > 0)
            {
                foreach (var rawCmd in _profile.InitialCommands)
                {
                    if (string.IsNullOrWhiteSpace(rawCmd)) continue;
                    var expanded = Environment.ExpandEnvironmentVariables(rawCmd);
                    _conPty.Write(expanded + "\r");
                }
            }

            // OnActivated は ConPTY 起動より前に (WebView2 初期化中に) 発火して
            // しまっており、そこでは早期 return している。ここでもう一度 focus を
            // HTML コンテンツに明示的に移しておかないと、ユーザーが console を
            // クリックするまでカーソルが表示されない。
            BeginInvoke(() =>
            {
                try
                {
                    if (_webView.CoreWebView2 != null && !IsDisposed)
                    {
                        _webView.Focus();
                        _ = _webView.CoreWebView2.ExecuteScriptAsync(
                            "window.focus(); document.body.focus(); if (typeof term !== 'undefined') term.focus();");
                    }
                }
                catch { }
            });
        }
        catch (Exception ex)
        {
            AppLogger.Error($"ConPTY start failed ({_profile.Name})", ex);
            MessageBox.Show($"ConPTY start failed:\n{ex.Message}", "Error",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void OnConPtyOutput(string ansiData)
    {
        _waitDetector?.Feed(ansiData);
        WriteSessionLog(ansiData);
        _activeRecorder?.Feed(ansiData);

        if (_navigationCompleted && !IsDisposed)
        {
            try
            {
                BeginInvoke(() =>
                {
                    if (IsDisposed || _webView.CoreWebView2 == null) return;
                    var msg = JsonSerializer.Serialize(new { type = "output", data = ansiData });
                    _webView.CoreWebView2.PostWebMessageAsString(msg);
                });
            }
            catch (ObjectDisposedException) { }
            catch (InvalidOperationException) { }
        }
    }

    private void OnProcessExited()
    {
        _waitDetector?.NotifyProcessExited();
        // プロセス終了時も進行中の記録を書き出す
        var rec = Interlocked.Exchange(ref _activeRecorder, null);
        rec?.Complete();
        try
        {
            BeginInvoke(() =>
            {
                if (IsDisposed) return;
                Text = FormatTitle(WaitStateGlyph.For(WaitState.Stopped), " (exited)");
                // shell で `exit` などを叩いてプロセスが終わった場合、ウィンドウを
                // 自動で閉じる。Close() は OnFormClosing 経由で Dispose まで走る。
                // CloseReason は UserClosing にならないので子側の「実行中確認」
                // ダイアログは出ない (そもそも既に停止状態)。
                if (_profile.CloseOnExit) Close();
            });
        }
        catch { }
    }

    private void OnWaitStateChanged(WaitState state)
    {
        try
        {
            BeginInvoke(() =>
            {
                if (IsDisposed) return;
                var prev = _prevWaitState;
                _prevWaitState = state;
                // 出力再開 / プロセス終了で attention (許可・確認待ち) は自然解除
                // (UDR-amm-20260605T1043-3af)
                if (state is WaitState.Running or WaitState.Stopped)
                    SetAttention(false);
                Text = FormatTitle(WaitStateGlyph.For(state, HasAttention));
                ApplyTitleBarTint(state);
                WaitStateChanged?.Invoke(this, state);
                UpdateAutoSendTimer(prev, state);
                // WaitingForInput (= 応答完了) で記録を書き出す
                if (state == WaitState.WaitingForInput && _activeRecorder != null)
                {
                    _activeRecorder.Complete();
                    _activeRecorder = null;
                }
            });
        }
        catch { }
    }

    /// <summary>
    /// 入力待ち中だけタイトルバーを黄色にし、それ以外は OS 既定色に戻す。
    /// DwmSetWindowAttribute は Windows 11 22000+ で有効。古い OS では no-op。
    /// MDI 子最大化時はタイトルバー自体が消えるので視覚効果は出ないが、
    /// 復帰時に正しい色で再描画される。
    /// </summary>
    private void ApplyTitleBarTint(WaitState state)
    {
        if (!IsHandleCreated) return;
        if (state == WaitState.WaitingForInput)
        {
            // attention (許可・確認待ち) は黄よりも目立つオレンジで区別
            // (UDR-amm-20260605T1043-3af)
            var color = HasAttention
                ? Color.FromArgb(255, 150, 50)  // オレンジ: 許可・確認待ち
                : Color.FromArgb(255, 220, 90); // 黄: 入力待ち
            Win32SystemMenu.SetCaptionColor(Handle, color);
            Win32SystemMenu.SetCaptionTextColor(Handle, Color.Black);
        }
        else
        {
            Win32SystemMenu.SetCaptionColor(Handle, null);     // OS 既定に戻す
            Win32SystemMenu.SetCaptionTextColor(Handle, null);
        }
    }

    internal void ShowRenameDialog()
    {
        // 高 DPI (125%/150%) 環境でボタン下半分が切れていたため、システムフォント
        // の行高 + 余白で高さを動的算出し、ダイアログの ClientSize も合わせる。
        var buttonHeight = Math.Max(28, (SystemFonts.MenuFont?.Height ?? 16) + 12);
        var buttonY = 80;

        using var dlg = new Form
        {
            Text = "MDI ウィンドウ名の変更",
            FormBorderStyle = FormBorderStyle.FixedDialog,
            StartPosition = FormStartPosition.CenterParent,
            ClientSize = new Size(360, buttonY + buttonHeight + 16),
            MinimizeBox = false,
            MaximizeBox = false,
            ShowInTaskbar = false,
        };
        var lbl = new Label
        {
            Text = "新しい表示名",
            Location = new Point(12, 12),
            AutoSize = true,
        };
        var tb = new TextBox
        {
            Location = new Point(12, 36),
            Width = 332,
            Text = DisplayName,
        };
        var ok = new Button
        {
            Text = "OK",
            DialogResult = DialogResult.OK,
            Location = new Point(180, buttonY),
            Width = 80,
            Height = buttonHeight,
        };
        // 「既定に戻す」: 以前は DialogResult.Retry を使い、戻り値分岐で null を
        // 流していたが、ボタン押下直後にダイアログが閉じない / Retry のままで
        // 流れず初期値復元されない不具合があった。明示的に Click で TextBox を
        // 空にしてから DialogResult.OK を立てて閉じる方式に変更すると、後続の
        // ApplyCustomDisplayName("") が IsNullOrEmpty で normalized=null に
        // 落ちて確実に既定名へ戻る (= 元の意図と挙動が一致)。
        var reset = new Button
        {
            Text = "既定に戻す",
            Location = new Point(12, buttonY),
            Width = 100,
            Height = buttonHeight,
        };
        reset.Click += (_, _) =>
        {
            tb.Text = "";
            dlg.DialogResult = DialogResult.OK;
        };
        var cancel = new Button
        {
            Text = "キャンセル",
            DialogResult = DialogResult.Cancel,
            Location = new Point(264, buttonY),
            Width = 80,
            Height = buttonHeight,
        };
        dlg.Controls.Add(lbl);
        dlg.Controls.Add(tb);
        dlg.Controls.Add(ok);
        dlg.Controls.Add(reset);
        dlg.Controls.Add(cancel);
        dlg.AcceptButton = ok;
        dlg.CancelButton = cancel;

        var result = dlg.ShowDialog(this);
        if (result != DialogResult.OK) return;

        // ApplyCustomDisplayName に統一: タイトル再描画と DisplayNameChanged 発火
        // を一箇所に集約。空文字は normalized=null となり既定名 (profile.Name +
        // インスタンス番号) に戻る。
        ApplyCustomDisplayName(tb.Text?.Trim());
    }

    private async void OnActivated(object? sender, EventArgs e)
    {
        // attention (許可・確認待ち) はユーザーがこのペインを見た時点で解除
        // (UDR-amm-20260605T1043-3af)。OnWaitStateChanged 経由で ⚠→● とボタン色を
        // 再描画する。
        if (SetAttention(false))
            OnWaitStateChanged(CurrentWaitState);

        if (_webView.CoreWebView2 != null && _navigationCompleted)
        {
            try
            {
                _webView.Focus();
                // WebView2 WinForms の Focus() だけでは HTML コンテンツに
                // keyboard focus が届かないケースがあるため、window → body →
                // term.focus() の順で明示的にフォーカスを降ろす。
                await _webView.CoreWebView2.ExecuteScriptAsync(
                    "window.focus(); document.body.focus(); if (typeof term !== 'undefined') term.focus();");
            }
            catch { }
        }
    }

    /// <summary>
    /// クイック切替バーのボタン押下など、Form.Activate() だけでは Activated が
    /// 発火しない (= 既にアクティブ MDI である等) ケース向けに、内側の WebView2
    /// と xterm.js に明示的にキーボードフォーカスを降ろす。
    /// </summary>
    public void FocusTerminal()
    {
        if (IsDisposed) return;
        if (WindowState == FormWindowState.Minimized)
            WindowState = FormWindowState.Normal;
        Activate();
        try
        {
            if (_webView.CoreWebView2 != null && _navigationCompleted)
            {
                _webView.Focus();
                _ = _webView.CoreWebView2.ExecuteScriptAsync(
                    "window.focus(); document.body.focus(); if (typeof term !== 'undefined') term.focus();");
            }
            else
            {
                _webView.Focus();
            }
        }
        catch { }
    }

    /// <summary>
    /// xterm.js のビューを最下行までスクロールさせる。入力欄 / エディタ連携 / MCP
    /// などから送信した直後の出力を確実に視野に入れるため、送信経路の最後で呼ぶ。
    /// </summary>
    public void ScrollToBottom()
    {
        if (IsDisposed) return;
        try
        {
            if (_webView.CoreWebView2 != null && _navigationCompleted)
            {
                _ = _webView.CoreWebView2.ExecuteScriptAsync(
                    "if (typeof term !== 'undefined') term.scrollToBottom();");
            }
        }
        catch { }
    }

    private void OnFormDragEnter(object? sender, DragEventArgs e)
    {
        if (e.Data?.GetDataPresent(DataFormats.FileDrop) == true && IsProcessRunning)
            e.Effect = DragDropEffects.Copy;
        else
            e.Effect = DragDropEffects.None;
    }

    internal enum DropAction { Content, Path, Cancel }

    /// <summary>
    /// ドロップされた全ファイルがテキスト形式のときに「内容 / パス」を選ばせる
    /// TaskDialog を表示する。テキスト以外が混じっているときは Path に即確定。
    /// </summary>
    internal static DropAction AskDropAction(IWin32Window owner, int fileCount, bool allText)
    {
        if (!allText)
        {
            // テキスト判定外はパス送信で確定 (中身を送る意味が薄いバイナリ想定)。
            return DropAction.Path;
        }

        var contentBtn = new TaskDialogButton("ファイル内容を送信(&C)");
        var pathBtn = new TaskDialogButton("絶対パスを送信(&P)");
        var page = new TaskDialogPage
        {
            Caption = "amm",
            Heading = "ファイルのドロップ",
            Text = fileCount == 1
                ? "ドロップされたファイルをどう送信しますか?"
                : $"ドロップされた {fileCount} 件のファイルをどう送信しますか?\n" +
                  "・内容: 全ファイルを連結してそのまま送信\n" +
                  "・パス: 絶対パスをスペース区切りで送信",
            Icon = TaskDialogIcon.Information,
            Buttons = { contentBtn, pathBtn, TaskDialogButton.Cancel },
            DefaultButton = contentBtn,
        };
        var result = TaskDialog.ShowDialog(owner, page);
        if (result == contentBtn) return DropAction.Content;
        if (result == pathBtn) return DropAction.Path;
        return DropAction.Cancel;
    }

    /// <summary>
    /// 複数ファイルを UTF-8 / profile エンコーディングで読み連結する。size 制限は
    /// 1MB で、超過時は確認ダイアログを owner に対して表示する。OK でなければ
    /// 空文字列を返す。
    /// </summary>
    internal static string? ReadAndCombine(IWin32Window owner, string[] paths, System.Text.Encoding encoding)
    {
        const long maxBytes = 1024 * 1024; // 1MB
        long total = 0;
        foreach (var f in paths)
        {
            try { total += new FileInfo(f).Length; } catch { }
        }
        if (total > maxBytes)
        {
            var r = MessageBox.Show(
                owner,
                $"連結サイズが {total / 1024} KB となり 1MB を超えます。そのまま送信しますか?",
                "大きなファイルの確認",
                MessageBoxButtons.OKCancel,
                MessageBoxIcon.Warning,
                MessageBoxDefaultButton.Button2);
            if (r != DialogResult.OK) return null;
        }

        var combined = new System.Text.StringBuilder();
        foreach (var f in paths)
        {
            try
            {
                using var fs = new FileStream(f, FileMode.Open, FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete);
                using var sr = new StreamReader(fs, encoding, detectEncodingFromByteOrderMarks: true);
                combined.Append(sr.ReadToEnd());
            }
            catch (Exception ex)
            {
                AppLogger.Error($"file drop read failed: {f}", ex);
            }
        }
        return combined.ToString();
    }

    private void OnFormDragDrop(object? sender, DragEventArgs e)
    {
        if (e.Data?.GetData(DataFormats.FileDrop) is not string[] files || files.Length == 0) return;
        HandleDroppedFiles(files);
    }

    private void RegisterNativeDropTargetOnWebView()
    {
        _nativeDropTarget = new NativeDropTarget(paths =>
        {
            // コールバックは OLE スレッドで飛ぶので UI に marshal
            try { BeginInvoke(() => HandleDroppedFiles(paths)); }
            catch { }
        });

        // _webView 本体 + 全子孫 HWND に対して登録。Chromium は実際の drop 受け
        // 手となる inner HWND を都度作るので、EnumChildWindows で再帰的に巡回。
        RegisterOnHwndTree(_webView.Handle);

        void RegisterOnHwndTree(IntPtr root)
        {
            RegisterOnHwnd(root);
            NativeDropInterop.EnumChildWindows(root, (h, _) =>
            {
                RegisterOnHwnd(h);
                return true; // continue
            }, IntPtr.Zero);
        }

        void RegisterOnHwnd(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero) return;
            NativeDropInterop.RevokeDragDrop(hwnd); // 既存 (Chromium) を外す
            var hr = NativeDropInterop.RegisterDragDrop(hwnd, _nativeDropTarget!);
            if (hr == 0) _dropTargetHwnds.Add(hwnd);
        }
    }

    private void UnregisterNativeDropTargets()
    {
        foreach (var h in _dropTargetHwnds)
        {
            try { NativeDropInterop.RevokeDragDrop(h); } catch { }
        }
        _dropTargetHwnds.Clear();
        _nativeDropTarget = null;
    }

    private void HandleDroppedFiles(string[] files)
    {
        if (!IsProcessRunning) return;
        var targets = files.Where(File.Exists).ToArray();
        if (targets.Length == 0) return;

        var allText = FileDropHelper.AllTextFiles(targets);
        var action = AskDropAction(this, targets.Length, allText);
        if (action == DropAction.Cancel) return;

        if (action == DropAction.Path)
        {
            // パスはシェルの入力バッファに「挿入」するだけで実行しない。
            // 末尾に \r を付けるとパスが単独コマンド扱いされ、例えば .md が
            // 関連付けエディタで開かれてしまう。ユーザが必要なら自分で Enter を
            // 押す/前後にコマンドを足せるように、改行は付けずに Write する。
            var text = FileDropHelper.JoinPaths(targets);
            if (string.IsNullOrEmpty(text)) return;
            SendText(text, appendEnter: false);
            Activate();
            return;
        }

        // 内容送信: 従来通り末尾に \r を付けて実行させる。
        var combined = ReadAndCombine(this, targets, _profile.GetEncoding());
        if (combined == null) return;
        if (string.IsNullOrEmpty(combined)) return;

        SendText(combined);
        Activate();
    }


    /// <summary>
    /// 右クリックメニュー「改行送信」: 入力欄を介さず Enter (\r) 1 つだけを子
    /// プロセスへ送る。AI CLI の確定 / 継続入力や、引数待ちプロンプトでの空 Enter
    /// に使う。プロセス停止中は何もしない。
    /// </summary>
    public void SendNewline()
    {
        if (!IsProcessRunning) return;
        SendText("\r", appendEnter: false);
        ScrollToBottom();
    }

    /// <summary>
    /// 右クリックメニュー「プロンプト再送信」: ↑ (CSI A) で直前の入力履歴を呼び
    /// 出し、少し待ってから Enter (\r) で確定する。AI CLI で同じプロンプトを
    /// もう一度投げ直す用途。↑ と Enter の間隔が短いと TUI 側が履歴の反映
    /// (取得 + 再描画) を終える前に submit を処理して Enter を取りこぼすため、
    /// bracketed paste の確定 Enter (150ms) より長い 300ms を既定にする
    /// (150ms では取りこぼす実績あり)。プロセス停止中は何もしない。
    /// </summary>
    public async Task ResendPreviousPromptAsync(int submitDelayMs = 300)
    {
        if (!IsProcessRunning) return;
        SendText("\x1b[A", appendEnter: false); // ↑ = CSI A (履歴 1 つ戻る)
        await Task.Delay(Math.Max(50, submitDelayMs));
        if (IsDisposed || !IsProcessRunning) return;
        SendText("\r", appendEnter: false);
        ScrollToBottom();
    }

    /// <summary>
    /// この MDI の CLI が「人間の判断 (許可・追加情報) を待っている」状態
    /// (Approval Hub Level 1, UDR-amm-20260605T1043-3af)。WaitState とは独立の
    /// 付加フラグ (attention 中も入力可能 = waiting の一種なので enum は拡張しない)。
    /// 表示: タイトル ⚠ / タイトルバー・切替ボタンのオレンジ。
    /// 解除: ペインのアクティブ化 / Running・Stopped 遷移 / idle・busy 通知。
    /// </summary>
    public bool HasAttention { get; private set; }

    /// <summary>
    /// CLI hook 由来の状態通知 (amm/notify) を wait 検出器へ反映する
    /// (UDR-amm-20260605T0523-7e1)。"idle" / "attention" はいずれも
    /// WaitingForInput として扱い、attention は HasAttention フラグでも保持する。
    /// ForceState は状態が変わらないと StateChanged を発火しないため、フラグが
    /// 変わったのに状態が同じ場合は OnWaitStateChanged を直接呼んで UI を更新する。
    /// Pipe スレッドから呼ばれるが、OnWaitStateChanged が BeginInvoke で
    /// UI thread へ marshal するためここでは何もしない。
    /// </summary>
    public void NotifyExternalWaitState(string state)
    {
        if (IsDisposed) return;
        switch (state)
        {
            case "idle":
            case "attention":
                var attentionChanged = SetAttention(state == "attention");
                var before = CurrentWaitState;
                _waitDetector?.ForceState(WaitState.WaitingForInput);
                if (attentionChanged && CurrentWaitState == before)
                    OnWaitStateChanged(CurrentWaitState); // フラグのみ変化 → 明示再描画
                break;
            case "busy":
                SetAttention(false);
                _waitDetector?.ForceState(WaitState.Running);
                break;
            // 未知の state は無視 (将来の語彙追加に対して前方互換)
        }
    }

    /// <summary>HasAttention を更新し、変化したら true。</summary>
    private bool SetAttention(bool value)
    {
        if (HasAttention == value) return false;
        HasAttention = value;
        // attention 到着でアイドル自動送信カウントダウンをキャンセル。
        // SetAttention は Pipe スレッドから呼ばれることがあるため IsHandleCreated を
        // 確認したうえで BeginInvoke する (OnWaitStateChanged 内呼び出しは既に UI スレッド)。
        if (value && _autoSendArmed && IsHandleCreated)
            BeginInvoke(CancelAutoSendTimer);
        return true;
    }

    // ---- アイドル時自動送信 ロジック ----

    private void UpdateAutoSendTimer(WaitState prev, WaitState next)
    {
        if (next is WaitState.Running or WaitState.Stopped)
        {
            _autoSendArmed = false;
            CancelAutoSendTimer();
            return;
        }
        // WaitingForInput で attention に切り替わったらキャンセル
        if (next == WaitState.WaitingForInput && HasAttention && _autoSendArmed)
        {
            _autoSendArmed = false;
            CancelAutoSendTimer();
            return;
        }
        if (_autoSendArmed) return; // 既にカウントダウン中

        var cfg = _profile.AutoSendOnIdle;
        if (!cfg.Enabled || string.IsNullOrEmpty(cfg.Prompt)) return;
        if (HasAttention) return;

        // Running → WaitingForInput 遷移でのみ開始
        if (prev == WaitState.Running && next == WaitState.WaitingForInput)
        {
            _autoSendArmed = true;
            StartAutoSendCountdown(cfg);
        }
    }

    private void StartAutoSendCountdown(AutoSendOnIdleSettings cfg)
    {
        if (cfg.DelayMs <= 0)
        {
            ExecuteAutoSend(cfg.Prompt);
            return;
        }
        _autoSendDeadline = DateTime.UtcNow.AddMilliseconds(cfg.DelayMs);
        _autoSendCountdownTimer?.Stop();
        _autoSendCountdownTimer?.Dispose();
        _autoSendCountdownTimer = new System.Windows.Forms.Timer { Interval = 500 };
        _autoSendCountdownTimer.Tick += (_, _) =>
        {
            if (IsDisposed || !_autoSendArmed)
            {
                CancelAutoSendTimer();
                return;
            }
            var remaining = _autoSendDeadline - DateTime.UtcNow;
            if (remaining <= TimeSpan.Zero)
            {
                CancelAutoSendTimer();
                ExecuteAutoSend(cfg.Prompt);
            }
            else
            {
                var secs = (int)Math.Ceiling(remaining.TotalSeconds);
                // タイトルに残秒数をオーバーレイ表示（AttentionTint は変えない）
                Text = FormatTitle(WaitStateGlyph.For(WaitState.WaitingForInput, HasAttention),
                    $" ⏱{secs}s");
            }
        };
        _autoSendCountdownTimer.Start();
        // 初回即時表示
        var initSecs = (int)Math.Ceiling(cfg.DelayMs / 1000.0);
        Text = FormatTitle(WaitStateGlyph.For(WaitState.WaitingForInput, HasAttention),
            $" ⏱{initSecs}s");
    }

    private void CancelAutoSendTimer()
    {
        _autoSendCountdownTimer?.Stop();
        _autoSendCountdownTimer?.Dispose();
        _autoSendCountdownTimer = null;
        // タイトルを通常状態に戻す
        if (!IsDisposed && IsHandleCreated)
            Text = FormatTitle(WaitStateGlyph.For(CurrentWaitState, HasAttention));
    }

    private void ExecuteAutoSend(string prompt)
    {
        _autoSendArmed = false;
        if (HasAttention || !IsProcessRunning) return;
        SendText(prompt, appendEnter: true);
        ScrollToBottom();
    }

    // ---- end アイドル時自動送信 ----

    public void SendText(string text, bool appendEnter = true)
    {
        // 対話ターミナルでの Enter は \r 一文字。\r\n や \n を送ると余計な
        // Enter として解釈され PowerShell PSReadLine が ">>" 継続入力モードに
        // 入ってしまう。TextBox が \r\n / \n を含めてきても全部 \r に畳む。
        // appendEnter=true のときは末尾に \r がなければ追加 (従来挙動: 実行)。
        // appendEnter=false のときはそのまま書き込み、シェルの入力バッファ上で
        // ユーザーが追記/編集できる状態にする (path drop 用途)。
        var converted = text.Replace("\r\n", "\r").Replace("\n", "\r");
        if (appendEnter && converted.Length > 0 && !converted.EndsWith('\r'))
            converted += "\r";
        _conPty?.Write(converted);
    }

    /// <summary>
    /// マルチライン入力を 1 行ずつ \r 付きで送信する。AI CLI 系で 1 行 1 メッセージ
    /// として処理させるためのモードで、Profile.SendLineByLine=true のときに親から
    /// 呼ばれる。連続書き込みで CLI 側が改行を取りこぼすことがあるので、行の間に
    /// 短いディレイを挟む。
    ///
    /// 末尾に改行を保証することで、最終行送出後に「確定 Enter (空行 \r)」が必ず
    /// 走るようにする (選択範囲送信時に末尾改行が無いケースで取りこぼしていた)。
    ///
    /// <paramref name="submitLastLine"/> = false のときは末尾改行の保証をやめ、
    /// 最終行を \r なしで書き込む (= 最終行はシェルの入力バッファに残り、確定は
    /// 人間がターミナル側で行う)。中間行の \r は「1 行 = 1 メッセージ」の仕様上
    /// そのまま残る。
    /// </summary>
    public async Task SendTextLineByLineAsync(string text, int delayMs = 80, bool submitLastLine = true)
    {
        if (string.IsNullOrEmpty(text)) return;
        if (submitLastLine && !text.EndsWith('\n') && !text.EndsWith('\r'))
            text += "\n";
        var lines = text.Replace("\r\n", "\n").Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            if (IsDisposed || _conPty == null) return;
            bool isLast = i == lines.Length - 1;
            _conPty.Write(submitLastLine || !isLast ? lines[i] + "\r" : lines[i]);
            if (delayMs > 0 && !isLast)
                await Task.Delay(delayMs);
        }
    }

    /// <summary>
    /// 入力欄 / エディタ連携からの送信を bracketed paste (DECSET 2004) で囲って
    /// 「ペースト」として届ける。Copilot CLI のような Ink ベース TUI は、素の
    /// バイト書き込みを "速すぎるキータイプ" として discard してしまうため、
    /// xterm.js の手動ペースト (<c>term.paste()</c>) と同じ形式で送る必要がある。
    ///
    /// <c>\x1b[200~...\x1b[201~</c> で内容を包み、少し待ってから確定 Enter (\r)
    /// を 1 つ打って prompt を submit する。Profile.UseBracketedPaste=true の
    /// ときに親から呼ばれる。
    ///
    /// <paramref name="submit"/> = false のときは paste 書き込みだけで終え、確定
    /// Enter は一切打たない (確定はターミナル側で人間が行う)。
    /// </summary>
    public async Task SendAsBracketedPasteAsync(string text, int submitDelayMs = 150, bool extraEnter = false, bool submit = true)
    {
        if (string.IsNullOrEmpty(text)) return;
        if (_conPty == null || IsDisposed) return;

        const string PasteBegin = "\x1b[200~";
        const string PasteEnd = "\x1b[201~";

        // 改行は LF に統一 (xterm.js のペーストと同じ表現)。末尾の改行は剥がして
        // 確定 Enter を 1 つだけ後で打つ。
        var normalized = text.Replace("\r\n", "\n").Replace("\r", "\n").TrimEnd('\n');
        if (normalized.Length == 0) return;

        _conPty.Write(PasteBegin + normalized + PasteEnd);
        if (!submit) return; // テキストのみ届け、確定は人間が行う

        // Ink 側の paste 処理 (input フィールドへの反映) が走り終わるのを待ってから
        // 確定 Enter を打つ。delay が小さいと paste 末尾と Enter が同フレームに
        // 入って Ink が submit を取りこぼすケースがある。Claude Code / Codex CLI /
        // Copilot CLI の最近の版では 80ms では足りないため 150ms を既定にした。
        await Task.Delay(Math.Max(50, submitDelayMs));
        if (IsDisposed || _conPty == null) return;
        _conPty.Write("\r");

        if (extraEnter)
        {
            // 1 回目 \r が paste 終端処理に吸われた場合の保険として 2 回目を打つ。
            //   - Claude Code / Codex: 1 回目で submit 済み、2 回目は空入力 (再表示)
            //   - Copilot CLI: 2 回目以降も submit に至らない (制限事項として記録)
            // Copilot CLI 救済のため 3 回目までは増やさない: Claude/Codex 側の
            // 空 submit / プロンプト再描画が増えるデメリットの方が大きい。
            // 詳細は README のトラブルシュート § Copilot CLI 自動 submit 制限。
            await Task.Delay(120);
            if (IsDisposed || _conPty == null) return;
            _conPty.Write("\r");
        }
    }

    private void HandlePasteRequest()
    {
        string clipboardText;
        try
        {
            if (!Clipboard.ContainsText()) return;
            clipboardText = Clipboard.GetText();
        }
        catch { return; }

        if (string.IsNullOrEmpty(clipboardText)) return;

        // マルチライン貼付確認: 改行を含むテキストは誤ってコマンド実行される
        // 危険があるので、内容プレビュー付きで確認ダイアログ。デフォルト選択
        // は Cancel (ユーザーが Enter を気軽に押しても暴走しないように)。
        if (clipboardText.Contains('\n'))
        {
            var lineCount = clipboardText.Split('\n').Length;
            var preview = clipboardText.Length > 200
                ? clipboardText[..200] + "\n…"
                : clipboardText;
            var result = MessageBox.Show(
                this,
                $"{lineCount} 行 ({clipboardText.Length} 文字) を貼り付けます。\n" +
                "改行が含まれているため、そのままコマンド実行される可能性があります。\n\n" +
                $"プレビュー:\n{preview}\n\n続行しますか？",
                "マルチライン貼付の確認",
                MessageBoxButtons.OKCancel,
                MessageBoxIcon.Warning,
                MessageBoxDefaultButton.Button1);
            if (result != DialogResult.OK) return;
        }

        SendPasteResponse(clipboardText);
    }

    private void SendPasteResponse(string text)
    {
        if (_webView.CoreWebView2 == null || IsDisposed) return;
        try
        {
            var msg = System.Text.Json.JsonSerializer.Serialize(new { type = "paste_response", data = text });
            _webView.CoreWebView2.PostWebMessageAsString(msg);
        }
        catch { }
    }

    private void PostSimpleMessage(string type)
    {
        if (_webView.CoreWebView2 == null || IsDisposed) return;
        try
        {
            var msg = System.Text.Json.JsonSerializer.Serialize(new { type });
            _webView.CoreWebView2.PostWebMessageAsString(msg);
        }
        catch { }
    }

    private void OpenSessionLogIfRequested()
    {
        if (!_profile.SessionLog) return;
        try
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "amm", "sessions");
            Directory.CreateDirectory(dir);
            // sessionLog には AI とのやり取り (API キー貼付・トークン・社内コード等が
            // 混入し得る) が平文で残る。継承された緩い ACL を切り、current user のみが
            // アクセスできるよう明示的に絞る。失敗は致命的でない (catch で握る)。
            RestrictDirectoryToCurrentUser(dir);

            // ファイル名禁止文字をサニタイズ
            var safeName = string.Concat(_profile.Name.Select(ch =>
                Path.GetInvalidFileNameChars().Contains(ch) ? '_' : ch));
            var path = Path.Combine(dir,
                $"{DateTime.Now:yyyyMMdd-HHmmss}-{safeName}.log");

            var writer = new StreamWriter(path, append: true, System.Text.Encoding.UTF8)
            {
                AutoFlush = true,
            };
            writer.WriteLine(
                $"# {_profile.Name} session log — start {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            writer.WriteLine($"# commandLine: {_profile.CommandLine}");
            writer.WriteLine("# ---");
            lock (_sessionLogLock)
            {
                _sessionLogWriter = writer;
                _sessionLogBytes = 0;
                _sessionLogCapped = false;
            }
        }
        catch { /* ログ失敗は機能本体に波及させない */ }
    }

    /// <summary>
    /// ディレクトリの ACL を current user の FullControl のみへ絞る (継承を遮断)。
    /// Windows 専用。sessionLog 等の機密を含み得るフォルダの保護に使う。
    /// </summary>
    private static void RestrictDirectoryToCurrentUser(string dir)
    {
        try
        {
            using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
            var user = identity.User;
            if (user == null) return;

            var di = new DirectoryInfo(dir);
            var sec = new System.Security.AccessControl.DirectorySecurity();
            sec.SetOwner(user);
            // 継承を無効化し既存の継承 ACE を捨てる (preserveInheritance:false)。
            sec.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            sec.AddAccessRule(new System.Security.AccessControl.FileSystemAccessRule(
                user,
                System.Security.AccessControl.FileSystemRights.FullControl,
                System.Security.AccessControl.InheritanceFlags.ContainerInherit
                    | System.Security.AccessControl.InheritanceFlags.ObjectInherit,
                System.Security.AccessControl.PropagationFlags.None,
                System.Security.AccessControl.AccessControlType.Allow));
            // .NET では DirectoryInfo.SetAccessControl は拡張メソッド (using 非依存に明示呼び出し)。
            System.IO.FileSystemAclExtensions.SetAccessControl(di, sec);
        }
        catch { /* ACL 設定失敗はログ機能本体に波及させない */ }
    }

    private void WriteSessionLog(string ansiData)
    {
        try
        {
            var stripped = AnsiStripper.Strip(ansiData);
            if (stripped.Length == 0) return;
            lock (_sessionLogLock)
            {
                var w = _sessionLogWriter;
                if (w == null || _sessionLogCapped) return;
                _sessionLogBytes += System.Text.Encoding.UTF8.GetByteCount(stripped);
                if (_sessionLogBytes > SessionLogMaxBytes)
                {
                    // 上限到達: 注記を 1 行残して以降の書込みを停止 (ファイルは保持)。
                    w.WriteLine($"\n# --- session log truncated at {SessionLogMaxBytes / (1024 * 1024)} MiB ---");
                    _sessionLogCapped = true;
                    return;
                }
                w.Write(stripped);
            }
        }
        catch { /* IO エラーは無視 */ }
    }

    private void CloseSessionLog()
    {
        StreamWriter? w;
        lock (_sessionLogLock)
        {
            w = _sessionLogWriter;
            _sessionLogWriter = null;
        }
        if (w == null) return;
        try
        {
            w.WriteLine();
            w.WriteLine($"# --- session end {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            w.Dispose();
        }
        catch { }
    }

    private void PushConfigToTerminal()
    {
        if (_webView.CoreWebView2 == null || IsDisposed) return;
        try
        {
            var msg = System.Text.Json.JsonSerializer.Serialize(new
            {
                type = "config",
                ctrlCCopyOnSelection = _profile.CtrlCCopyOnSelection,
                theme = _profile.Theme,
                fontSize = _profile.FontSize,
            });
            _webView.CoreWebView2.PostWebMessageAsString(msg);
            // 起動時の現在値を追跡: profile 指定がなければ DEFAULT_FONT_SIZE = Medium。
            CurrentFontSize = _profile.FontSize ?? FontSizePresets.Medium;
        }
        catch { }
    }

    private void ShowContextMenu(string selectedText)
    {
        var menu = new ContextMenuStrip();
        var hasSelection = !string.IsNullOrEmpty(selectedText);

        // 「改行送信」: 右クリックだけで Enter (\r) を 1 つ送る。AI CLI の確定 /
        // 継続入力に使うため最上段に固定 (MDI 切替バー右クリックと同位置)。
        menu.Items.Add(new ToolStripMenuItem("改行送信", null, (_, _) =>
        {
            if (IsDisposed) return;
            SendNewline();
        }));
        // 「プロンプト再送信」: ↑ + Enter で直前の入力履歴を再実行する。
        menu.Items.Add(new ToolStripMenuItem("プロンプト再送信", null, async (_, _) =>
        {
            if (IsDisposed) return;
            await ResendPreviousPromptAsync();
        }));
        menu.Items.Add(new ToolStripSeparator());

        // クイック送信 ▶ サブメニュー (QuickPrompts > 0 のときだけ冒頭に追加)。
        // MDI 切替バーボタンの右クリック (BuildMdiButtonContextMenuStrip) と同じ位置
        // (= 冒頭 + セパレータ) でメンタルモデルを揃える。
        var quickPrompts = _profile.QuickPrompts ?? Array.Empty<QuickPrompt>();
        if (quickPrompts.Length > 0)
        {
            var quickMenu = new ToolStripMenuItem("クイック送信");
            foreach (var qp in quickPrompts)
            {
                var capturedPrompt = qp.Prompt ?? "";
                var label = string.IsNullOrEmpty(qp.Label) ? capturedPrompt : qp.Label;
                quickMenu.DropDownItems.Add(new ToolStripMenuItem(label, null, (_, _) =>
                {
                    if (IsDisposed || !IsProcessRunning) return;
                    if (string.IsNullOrEmpty(capturedPrompt)) return;
                    QuickPromptRequested?.Invoke(this, capturedPrompt);
                }));
            }
            menu.Items.Add(quickMenu);
            menu.Items.Add(new ToolStripSeparator());
        }

        // 「クイック送信に登録...」: 直前の送信テキストをクイック送信一覧へ追加する。
        // lastForward が空、またはエスケープシーケンスのみ (矢印キー等) のときはグレーアウト。
        var sanitizedLastForward = AnsiStripper.Strip(_lastForwardForMenu);
        var registerItem = new ToolStripMenuItem("クイック送信に登録...")
        {
            Enabled = !string.IsNullOrEmpty(sanitizedLastForward),
        };
        registerItem.Click += (_, _) =>
        {
            if (IsDisposed) return;
            // ラベルは先頭1行のみ (複数行プロンプトでも改行を含まないようにする)。
            var firstLine = sanitizedLastForward.Split('\n')[0].TrimEnd('\r');
            var label = firstLine.Length > 30 ? firstLine[..30] : firstLine;
            QuickSendRegisterRequested?.Invoke(this, label, sanitizedLastForward);
        };
        menu.Items.Add(registerItem);
        menu.Items.Add(new ToolStripSeparator());

        // コピー / 貼り付け: クイック送信の直下に配置する (頻用するため上方へ)。
        // すべて選択 / 画面クリアは末尾の「表示操作」グループに残す。
        var copyItem = new ToolStripMenuItem("コピー(&C)") { Enabled = hasSelection };
        copyItem.Click += (_, _) =>
        {
            if (!hasSelection) return;
            // copy ハンドラと同じく改行を単一 CRLF へ正規化してからクリップボードへ。
            var normalized = selectedText.Replace("\r\n", "\n").Replace("\r", "\n").Replace("\n", "\r\n");
            try { Clipboard.SetText(normalized); } catch { }
        };
        copyItem.ShortcutKeyDisplayString = "Ctrl+Shift+C";

        var pasteItem = new ToolStripMenuItem("貼り付け(&P)");
        pasteItem.Click += (_, _) => HandlePasteRequest();
        pasteItem.ShortcutKeyDisplayString = "Ctrl+V";

        menu.Items.Add(copyItem);
        menu.Items.Add(pasteItem);
        menu.Items.Add(new ToolStripSeparator());

        // MDI 切替バーボタン右クリック (BuildMdiButtonContextMenuStrip) と同じ
        // 4 アクション。ターミナル本体右クリックからも到達できるようにする。
        menu.Items.Add(new ToolStripMenuItem("プロンプト送信", null, (_, _) =>
        {
            if (IsDisposed) return;
            SendInputRequested?.Invoke(this);
        }));
        menu.Items.Add(new ToolStripMenuItem("エディタ連携", null, (_, _) =>
        {
            if (IsDisposed) return;
            EditorLinkRequested?.Invoke(this);
        }));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("名前変更…", null, (_, _) =>
        {
            if (IsDisposed) return;
            ShowRenameDialog();
        }));
        var fontMenu = new ToolStripMenuItem("フォントサイズ変更");
        foreach (var (label, size) in FontSizePresets.All)
        {
            var sz = size;
            var item = new ToolStripMenuItem(label, null, (_, _) =>
            {
                if (IsDisposed) return;
                SetFontSize(sz);
            });
            item.Checked = (sz == CurrentFontSize);
            fontMenu.DropDownItems.Add(item);
        }
        menu.Items.Add(fontMenu);
        menu.Items.Add(new ToolStripSeparator());

        var selectAllItem = new ToolStripMenuItem("すべて選択(&A)");
        selectAllItem.Click += (_, _) => PostSimpleMessage("select_all");

        var clearItem = new ToolStripMenuItem("画面クリア(&L)");
        clearItem.Click += (_, _) => PostSimpleMessage("clear");

        menu.Items.Add(selectAllItem);
        menu.Items.Add(clearItem);

        // 直前のメニューが残っていれば閉じ、閉じたら参照を片付ける。
        // Dispose は Closed 内で即時に行うと、WinForms がまだメニューのクリック処理 /
        // ModalMenuFilter の最中で、閉じた直後に Handle へアクセスして
        // ObjectDisposedException → アプリクラッシュになる (実績あり)。そのため
        // BeginInvoke で「現在のメッセージ処理完了後」へ遅延させて安全に破棄する。
        _activeContextMenu?.Close();
        menu.Closed += (_, _) =>
        {
            if (ReferenceEquals(_activeContextMenu, menu)) _activeContextMenu = null;
            if (IsHandleCreated && !IsDisposed)
                BeginInvoke(() => menu.Dispose());
        };
        _activeContextMenu = menu;
        menu.Show(Cursor.Position);
    }

    private void OnFormClosing(object? sender, FormClosingEventArgs e)
    {
        // closeProhibited (UDR-amm-20260427T0055-2c1): UserClosing 経路のみ抑止。
        // タスクマネージャ kill / 親フォーム終了 / アプリ終了は素通し。
        // 緊急脱出: Shift 押下中はバイパス (誤操作防止のため通知も出す)。
        if (e.CloseReason == CloseReason.UserClosing && _profile.CloseProhibited)
        {
            bool shiftHeld = (Control.ModifierKeys & Keys.Shift) == Keys.Shift;
            if (!shiftHeld)
            {
                MessageBox.Show(
                    this,
                    "このウィンドウはクローズ禁止に設定されています。\n" +
                    "強制的に閉じる場合は Shift を押しながら閉じてください。",
                    "クローズ禁止",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information);
                e.Cancel = true;
                return;
            }
        }

        // ユーザーが個別に閉じようとしていて、かつプロセス実行中なら確認。
        // 親フォーム経由のシャットダウン (CloseReason.MdiFormClosing) では
        // 親側で既に確認済みなので二重確認しない。
        if (e.CloseReason == CloseReason.UserClosing && IsProcessRunning)
        {
            var result = MessageBox.Show(
                this,
                $"「{Text}」は実行中です。このウィンドウを閉じますか？",
                "子ウィンドウを閉じる",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Warning,
                MessageBoxDefaultButton.Button2);
            if (result != DialogResult.Yes)
            {
                e.Cancel = true;
                return;
            }
        }

        // git commit/push ガード: 個別クローズ時のみ。親フォーム終了 (MdiFormClosing)
        // は MdiParentForm 側で全子ウィンドウをまとめて処理するのでここではスキップ。
        if (e.CloseReason == CloseReason.UserClosing)
        {
            if (GitGuard.CheckAndPrompt(this, StartupWorkingDirectory))
            {
                e.Cancel = true;
                return;
            }
        }

        // 先に ConPTY を破棄して読みスレッド (OnConPtyOutput → _waitDetector.Feed /
        // WriteSessionLog) の join を待ってから _waitDetector を破棄する。逆順だと
        // join 待ち中の最後の Feed が破棄済み Timer に触れて読みスレッドが未処理例外で
        // 落ちる窓があった。
        _conPty?.Dispose();
        _waitDetector?.Dispose();
        CloseSessionLog();
        UnregisterNativeDropTargets();
    }
}
