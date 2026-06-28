using System.Drawing;
using System.Text.Json;
using System.Text.Json.Serialization;
using Amm.Core.Mcp.Gateway;

namespace Amm.Core;

public enum NewlineMode
{
    CRLF,
    LF
}

/// <summary>
/// コマンドの種別。コマンド編集ダイアログでリスト選択し、選択時にその種別の既定
/// 設定一式 (実行ファイル/引数/waitパターン/改行/bracketed paste 等) を適用する。
/// 種別は永続化され、セッション復帰トークン (<see cref="SessionProfile.ResumeArgsFor"/>)
/// など実行時のふるまいも駆動する。Other は既定 (プリセットなし・手動)。
/// </summary>
public enum CommandType
{
    Other,       // その他 (既定: プリセットなし)
    Cmd,         // Cmd
    PowerShell,  // Powershell
    ClaudeCode,  // Claude Code
    Codex,       // Codex
    CopilotCli,  // COPILOT-CLI
}

/// <summary>
/// xterm.js フォントサイズの段階プリセット (px)。システムメニュー「フォントサイズ」と
/// コマンド追加・編集ダイアログの ComboBox から参照する。「中」は terminal.html の
/// DEFAULT_FONT_SIZE と一致させる (= 13)。
/// </summary>
public static class FontSizePresets
{
    public const int XLarge = 20; // 極大
    public const int Large  = 16; // 大
    public const int Medium = 13; // 中 (既定)
    public const int Small  = 11; // 小
    public const int XSmall = 9;  // 極小

    public static readonly (string Label, int Size)[] All =
    [
        ("極大", XLarge),
        ("大",   Large),
        ("中",   Medium),
        ("小",   Small),
        ("極小", XSmall),
    ];
}

/// <summary>
/// AMM ファイルで profile 単位に指定する MDI 起動位置・サイズ。
/// index は 1 始まりで、同 profile の生存 MDI 数 + 1 を参照する
/// (= 全閉じで 1 にリセット、穴埋めなし)。座標は MDI クライアント
/// 領域相対の px。エントリ無しは最大化。
/// </summary>
public sealed class WindowGeometryEntry
{
    [JsonPropertyName("index")]
    public int Index { get; set; }

    [JsonPropertyName("x")]
    public int X { get; set; }

    [JsonPropertyName("y")]
    public int Y { get; set; }

    [JsonPropertyName("w")]
    public int W { get; set; }

    [JsonPropertyName("h")]
    public int H { get; set; }

    /// <summary>
    /// MDI 切替バー / システムメニューの「名前変更…」で設定したユーザー定義の
    /// 表示名。null/空のときは profile.Name + インスタンス番号接尾辞をそのまま使う。
    /// 「現在の配置を記憶」や [ファイル → 上書き保存] で AMM ファイルへ書き戻し、
    /// 次回起動時に再適用される (W/H が 0 で名前だけ持つエントリも許容)。
    /// </summary>
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    /// <summary>
    /// 記憶時に WindowState=Maximized だった場合 true。これで「最大化中に名前を
    /// 付けて記憶した子」を復元するとき、位置 (W/H=0) ではなく最大化状態を再現
    /// できる。既定 null (= false 扱い、旧 AMM ファイル互換)。
    /// </summary>
    [JsonPropertyName("maximized")]
    public bool? Maximized { get; set; }

    /// <summary>
    /// 「現在の配置を記憶」時にこの MDI が使っていた作業ディレクトリ (= 起動時に
    /// 渡した OverrideWorkingDirectory)。null/空のときは profile.WorkingDirectory
    /// にフォールバック。値が指定されている場合、復元起動時は profile の
    /// SelectWorkingDirOnStart を強制 OFF 扱いしてフォルダ選択ダイアログを出さない。
    /// </summary>
    [JsonPropertyName("workingDirectory")]
    public string? WorkingDirectory { get; set; }
}

/// <summary>
/// アイドル時自動送信の per-profile 設定 (req-20260622-auto-send-idle)。
/// WaitingForInput かつ HasAttention=false の状態へ Running から遷移した
/// タイミングで DelayMs 後に Prompt を自動送信する。
/// </summary>
public sealed class AutoSendOnIdleSettings
{
    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; } = false;

    [JsonPropertyName("prompt")]
    public string Prompt { get; set; } = "";

    /// <summary>遷移後から送信までの遅延 (ms)。0 以下は即時送信。</summary>
    [JsonPropertyName("delayMs")]
    public int DelayMs { get; set; } = 3000;
}

/// <summary>
/// MDI 切替ボタンの右クリックメニュー「クイック送信 ▶」に並べる定型プロンプト。
/// 各エントリは Label (メニュー表示名) と Prompt (送信本文) の 2 列で、profile
/// 単位に複数件持てる。送信は通常の入力欄送信と同じ DispatchSendAsync ルートを
/// 通り、UseBracketedPaste / SendLineByLine も尊重する。
/// </summary>
public sealed class QuickPrompt
{
    [JsonPropertyName("label")]
    public string Label { get; set; } = "";

    [JsonPropertyName("prompt")]
    public string Prompt { get; set; } = "";
}

public sealed class SessionProfile
{
    static SessionProfile()
    {
        // .NET Core / .NET 9 では既定で CP932 等のコードページ系エンコーディングが
        // 登録されていない。profiles.json で Shift_JIS を指定したときの実行時
        // NotSupportedException を防ぐため、型の初回参照時に一度だけ登録する。
        System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);
    }

    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    /// <summary>
    /// コマンド種別 (<see cref="Amm.Core.CommandType"/>)。ダイアログでの選択時に
    /// 既定設定一式を適用し、resume トークン等の実行時ふるまいを駆動する。
    /// 旧 AMM ファイル (commandType 無し) は <see cref="MigrateLegacyFields"/> で
    /// nickname / executable から推測補完する。
    /// </summary>
    [JsonPropertyName("commandType")]
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public CommandType CommandType { get; set; } = CommandType.Other;

    [JsonPropertyName("executable")]
    public string Executable { get; set; } = "cmd.exe";

    [JsonPropertyName("args")]
    public string[] Args { get; set; } = [];

    /// <summary>
    /// true なら起動時にセッション復帰オプションを引数末尾へ付加する
    /// (コマンド管理ダイアログの「起動時にセッションを復帰する」)。付加する
    /// トークンは CLI 種別 (<see cref="Nickname"/>) で異なるため
    /// <see cref="ResumeArgsFor"/> が解決する (claude/copilot=--resume, codex=resume)。
    /// 保存セッションが無いフォルダでは CLI が即終了し得るので既定 false (任意 opt-in)。
    /// </summary>
    [JsonPropertyName("resumeOnStart")]
    public bool ResumeOnStart { get; set; }

    [JsonPropertyName("newlineMode")]
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public NewlineMode NewlineMode { get; set; } = NewlineMode.CRLF;

    [JsonPropertyName("outputEncoding")]
    public string OutputEncoding { get; set; } = "UTF-8";

    [JsonPropertyName("autoChcp")]
    public bool AutoChcp { get; set; } = true;

    [JsonPropertyName("waitPatterns")]
    public string[] WaitPatterns { get; set; } = [];

    [JsonPropertyName("workingDirectory")]
    public string? WorkingDirectory { get; set; }

    /// <summary>
    /// Ctrl+C が押されたとき、xterm.js に選択があればコピーし、なければ子プロセスへ
    /// ^C を送る (Windows Terminal 流)。false なら常に ^C 送信で、コピーは
    /// Ctrl+Shift+C / Ctrl+Insert / 右クリックメニュー経由に限定される。
    /// </summary>
    [JsonPropertyName("ctrlCCopyOnSelection")]
    public bool CtrlCCopyOnSelection { get; set; } = true;

    /// <summary>
    /// ConPTY 起動直後に順次送信するコマンド列。環境変数は展開される。
    /// 改行コードは newlineMode に従う。例: ["cd %USERPROFILE%\\projects", "git status"]
    /// </summary>
    [JsonPropertyName("initialCommands")]
    public string[] InitialCommands { get; set; } = [];

    /// <summary>
    /// true の場合、子プロセスの出力を ANSI 除去して
    /// %LOCALAPPDATA%\amm\sessions\YYYYMMDD-HHMMSS-(profile).log に追記する。
    /// </summary>
    [JsonPropertyName("sessionLog")]
    public bool SessionLog { get; set; } = false;

    /// <summary>
    /// true の場合、送信コマンドと応答末尾を JSON ファイルとして記録する。
    /// 保存先: &lt;workingDirectory&gt;\.amm\&lt;timestamp&gt;-&lt;rand&gt;.json
    /// </summary>
    [JsonPropertyName("chatRecord")]
    public bool ChatRecord { get; set; } = false;

    /// <summary>
    /// チャット記録に残す応答テキストの末尾文字数。既定 2000 文字。
    /// </summary>
    [JsonPropertyName("chatRecordTailChars")]
    public int ChatRecordTailChars { get; set; } = 2000;

    /// <summary>
    /// xterm.js の theme オプションへそのまま渡すキー/値。
    /// 例: {"background": "#000", "foreground": "#0f0", "cursor": "#ff0"}
    /// 省略時は terminal.html の既定 (#1e1e1e / #d4d4d4) を使用。
    /// </summary>
    [JsonPropertyName("theme")]
    public Dictionary<string, string>? Theme { get; set; }

    /// <summary>
    /// 子プロセスが終了したら MDI 子ウィンドウも自動で閉じる。
    /// false にすると "✗ name (exited)" のまま残り、ユーザーが明示的に閉じるまで
    /// 最終出力を確認できる。shell 系は true、ログ確認したい CLI は false を推奨。
    /// </summary>
    [JsonPropertyName("closeOnExit")]
    public bool CloseOnExit { get; set; } = true;

    // ---- v2: per-command 設定 (UDR-amm-20260427T0055-2c1) ----

    /// <summary>
    /// アプリ起動時にこの profile を何個自動起動するか。0 = 自動起動しない (既定)。
    /// </summary>
    [JsonPropertyName("autoStartCount")]
    public int AutoStartCount { get; set; } = 0;

    /// <summary>
    /// true のとき MDI ウィンドウのクローズを禁止 (× / Ctrl+W / システムメニュー
    /// 「閉じる」が全て無効化される)。常駐させたい AI 等で使う。既定 false。
    /// </summary>
    [JsonPropertyName("closeProhibited")]
    public bool CloseProhibited { get; set; } = false;

    /// <summary>
    /// マルチライン送信時、2 行以上連続する空行を 1 行に縮約する。既定 true。
    /// false の場合、入力欄に書かれた空行をそのまま送る。
    /// </summary>
    [JsonPropertyName("collapseBlankLines")]
    public bool CollapseBlankLines { get; set; } = true;

    /// <summary>
    /// 行頭がこのいずれかの接頭辞で始まる行をコメントとして送信スキップする。
    /// 既定 ["'", "//"]。空配列ならコメントフィルタ無効。
    /// かつての既定には "#" も含まれていたが、Markdown 見出し (## など) と衝突して
    /// 送信テキストから見出し行が抜け落ちるため既定から外した
    /// (旧既定そのままの値は <see cref="MigrateLegacyFields"/> で透過移行)。
    /// </summary>
    [JsonPropertyName("commentPrefixes")]
    public string[] CommentPrefixes { get; set; } = ["'", "//"];

    /// <summary>
    /// 起動 N 個目の MDI に適用する位置・サイズ。エントリ無し index は最大化。
    /// 同 profile の生存 MDI 数+1 で参照し、全閉じでリセット (穴埋めなし)。
    /// </summary>
    [JsonPropertyName("windowGeometry")]
    public WindowGeometryEntry[] WindowGeometry { get; set; } = [];

    /// <summary>
    /// MCP メッセージ受信時の宛先名 (UDR-amm-20260427T0225-7a3)。null/空なら
    /// 受信不可で list_participants にも出ない。同名が複数 MDI に居る場合は
    /// 起動順 + 入力待ち優先で「first/all」モードを判定する。
    /// </summary>
    [JsonPropertyName("nickname")]
    public string? Nickname { get; set; }

    /// <summary>
    /// マルチライン入力を送信する際、行ごとに個別に改行 (Enter) を打つモード。
    /// 既定 false (= 全行を 1 度の Write でまとめて送る、従来挙動)。AI CLI 系
    /// で 1 行 = 1 メッセージとして扱わせたい場合に true にする。
    /// </summary>
    [JsonPropertyName("sendLineByLine")]
    public bool SendLineByLine { get; set; } = false;

    /// <summary>
    /// 入力欄 / エディタ連携からの送信を bracketed paste mode (DECSET 2004) で
    /// 囲って届けるモード。Copilot CLI のように Ink ベース TUI が "貼り付け"
    /// として受け取る形式でないと素のバイト書き込みを discard してしまう CLI
    /// 向け。<c>\x1b[200~ ... \x1b[201~</c> でテキストを包み、続けて確定 Enter
    /// (\r) を 1 つ打つ。SendLineByLine と排他: useBracketedPaste 優先。
    /// </summary>
    [JsonPropertyName("useBracketedPaste")]
    public bool UseBracketedPaste { get; set; } = false;

    /// <summary>
    /// コマンド起動時にフォルダ選択ダイアログを出して作業ディレクトリをユーザに
    /// 選ばせるモード。選択した値はそのインスタンスのみに適用され、profile.json
    /// の WorkingDirectory は変更しない。既定 false。
    /// </summary>
    [JsonPropertyName("selectWorkingDirOnStart")]
    public bool SelectWorkingDirOnStart { get; set; } = false;

    /// <summary>
    /// 「コマンド」メニューからの手動コマンド追加時に、起動直後に「新しい名前で
    /// コマンドを追加する」ダイアログを表示するモード。SelectWorkingDirOnStart
    /// が true の場合はフォルダ選択ダイアログの後に出す。設定された名前は MDI
    /// ウィンドウ表示のみに反映され、profile 自体や AMM ファイルは変更しない
    /// (= 名前変更…と同じ一時表示名)。
    /// --all / autoStartCount / 記憶した配置の復元など自動起動経路では発動しない
    /// (= 起動毎にユーザに名前を尋ねたいのは「手動で追加した瞬間」だけのため)。
    /// 既定 false。
    /// </summary>
    [JsonPropertyName("promptNewNameOnCommandAdd")]
    public bool PromptNewNameOnCommandAdd { get; set; } = false;

    /// <summary>
    /// 既知のフィールドに該当しない JSON プロパティの捕獲先。旧 AMM ファイル
    /// (例: <c>promptRenameOnStart</c>) を読み込むときの後方互換マイグレーション
    /// (<see cref="MigrateLegacyFields"/>) で参照する。Serialize 時にも復元され
    /// るため、移行後はキーを削除しておく必要がある。
    /// </summary>
    [JsonExtensionData]
    [JsonInclude]
    public Dictionary<string, JsonElement>? ExtraProperties { get; set; }

    /// <summary>
    /// MDI 切替ボタンの右クリックメニュー「クイック送信 ▶」に並ぶ定型プロンプト。
    /// 0 件なら「クイック送信 ▶」サブメニューを表示しない。順序は配列順がそのまま
    /// メニュー順になる。
    /// </summary>
    [JsonPropertyName("quickPrompts")]
    public QuickPrompt[] QuickPrompts { get; set; } = [];

    /// <summary>アイドル時自動送信の設定。</summary>
    [JsonPropertyName("autoSendOnIdle")]
    public AutoSendOnIdleSettings AutoSendOnIdle { get; set; } = new();

    /// <summary>
    /// xterm.js のフォントサイズ (px) の既定値。null = terminal.html 既定値 (13)
    /// を使う。コマンド追加・編集ダイアログで FontSizePresets から選択し、その値が
    /// 起動時 config メッセージとして terminal.html に渡る。システムメニューの
    /// 「フォントサイズ」は per-MDI のランタイム上書きで、ここには書き戻さない。
    /// </summary>
    [JsonPropertyName("fontSize")]
    public int? FontSize { get; set; } = null;

    /// <summary>
    /// 指定 index に対応する geometry を取得。index は 1 始まり (生存数+1)。
    /// 該当エントリ無し、または W/H が 0 (= 名前だけ持つエントリ) は false。
    /// </summary>
    public bool TryGetGeometryForIndex(int oneBasedIndex, out Rectangle rect)
    {
        var entry = WindowGeometry.FirstOrDefault(e => e.Index == oneBasedIndex);
        if (entry == null || entry.W <= 0 || entry.H <= 0)
        {
            rect = Rectangle.Empty;
            return false;
        }
        rect = new Rectangle(entry.X, entry.Y, entry.W, entry.H);
        return true;
    }

    /// <summary>
    /// 指定 index に対応する記憶済み作業ディレクトリを取得。「現在の配置を記憶」で
    /// 保存した OverrideWorkingDirectory 由来。null/空のエントリは false。
    /// </summary>
    public bool TryGetWorkingDirectoryForIndex(int oneBasedIndex, out string workingDirectory)
    {
        var entry = WindowGeometry.FirstOrDefault(e => e.Index == oneBasedIndex);
        if (entry == null || string.IsNullOrWhiteSpace(entry.WorkingDirectory))
        {
            workingDirectory = "";
            return false;
        }
        workingDirectory = entry.WorkingDirectory!;
        return true;
    }

    /// <summary>
    /// 指定 index に対応する custom display name を取得。geometry の有無に関わらず、
    /// Name フィールドが設定されていれば true を返す。
    /// </summary>
    public bool TryGetNameForIndex(int oneBasedIndex, out string name)
    {
        var entry = WindowGeometry.FirstOrDefault(e => e.Index == oneBasedIndex);
        if (entry == null || string.IsNullOrEmpty(entry.Name))
        {
            name = "";
            return false;
        }
        name = entry.Name!;
        return true;
    }

    /// <summary>
    /// 行が CommentPrefixes のいずれかで始まるか。CommentPrefixes が空なら常に false。
    /// 行頭の前後空白は除去せず比較 (インデント付きコメント "  // foo" は対象外)。
    /// </summary>
    public bool IsCommentLine(string line)
    {
        if (CommentPrefixes.Length == 0) return false;
        foreach (var prefix in CommentPrefixes)
        {
            if (!string.IsNullOrEmpty(prefix) && line.StartsWith(prefix, StringComparison.Ordinal))
                return true;
        }
        return false;
    }

    /// <summary>
    /// CollapseBlankLines / CommentPrefixes を適用し、送信用の行配列を返す。
    /// 入力は元テキストを行に分割した配列 (改行コードは含めない)。
    /// 既定挙動の互換性: 連続空行を 1 行に縮約 + 行頭 ' / // はスキップ。
    /// </summary>
    public List<string> FilterLinesForSend(IEnumerable<string> rawLines)
    {
        var result = new List<string>();
        bool prevBlank = false;
        foreach (var line in rawLines)
        {
            if (IsCommentLine(line)) continue;

            bool isBlank = string.IsNullOrWhiteSpace(line);
            if (isBlank && CollapseBlankLines && prevBlank)
                continue; // 縮約: 直前も空行なら捨てる

            result.Add(line);
            prevBlank = isBlank;
        }
        return result;
    }

    /// <summary>
    /// WorkingDirectory を環境変数展開して返す。未指定 / 空なら現在のカレント
    /// ディレクトリを返す。
    /// </summary>
    public string? ResolveWorkingDirectory()
    {
        if (string.IsNullOrWhiteSpace(WorkingDirectory))
            return Environment.CurrentDirectory;
        return Environment.ExpandEnvironmentVariables(WorkingDirectory);
    }

    /// <summary>
    /// 旧フィールド名で書かれた AMM ファイルを新フィールドへ移行する。Load 時に
    /// 1 度だけ呼ぶ。未知キーは <see cref="ExtraProperties"/> に拾われているので、
    /// 既知の旧キーだけ取り出し、新フィールドに反映してから Dictionary から削除
    /// する (= 以降の Serialize で旧キーが書き戻されないようにする)。
    /// 増えてくるなら switch 文に育てる。現在:
    ///   - "promptRenameOnStart" (bool) → <see cref="PromptNewNameOnCommandAdd"/>
    ///   - commentPrefixes 旧既定 ["'", "//", "#"] → ["'", "//"] (Markdown "#" 衝突回避)
    /// </summary>
    public void MigrateLegacyFields()
    {
        // 旧既定 ["'", "//", "#"] (順序も一致) は "#" 抜きの新既定へ移行する。
        // "#" は Markdown 見出しと衝突するため既定から外した。ユーザーが意図して
        // "#" を含む別構成 (例: ["#"] や ["#", ";"]) にした場合は触らない。
        if (CommentPrefixes.SequenceEqual(["'", "//", "#"], StringComparer.Ordinal))
        {
            CommentPrefixes = ["'", "//"];
        }

        // commandType 未設定 (旧 AMM ファイル / Other 既定) は nickname / executable から
        // 推測補完する。これにより resume トークン等が種別ベースで正しく機能する。
        if (CommandType == CommandType.Other)
        {
            var inferred = InferCommandType(this);
            if (inferred != CommandType.Other) CommandType = inferred;
        }

        if (ExtraProperties == null || ExtraProperties.Count == 0) return;
        if (ExtraProperties.TryGetValue("promptRenameOnStart", out var v))
        {
            if (v.ValueKind == JsonValueKind.True) PromptNewNameOnCommandAdd = true;
            ExtraProperties.Remove("promptRenameOnStart");
        }
        if (ExtraProperties.Count == 0) ExtraProperties = null; // 空 dict も書き出さない
    }

    /// <summary>
    /// 表示・ログ用のコマンドライン。実行ファイル名は解決もクォートもしない
    /// (人が読む intent としての文字列)。実際の起動には
    /// <see cref="BuildLaunchCommandLine"/> を使うこと。
    /// </summary>
    public string CommandLine => BuildCommandLine(resolveExecutable: false);

    /// <summary>
    /// 実際に CreateProcess へ渡す起動コマンドライン。裸の実行ファイル名は安全な
    /// PATH 探索で絶対パスへ解決し (カレント/作業ディレクトリ起点の exe ハイジャック
    /// 防止)、空白を含むパスはクォートする。PATH に見つからない名前は元のまま
    /// (従来動作を壊さないフォールバック)。
    /// </summary>
    public string BuildLaunchCommandLine() => BuildCommandLine(resolveExecutable: true);

    private string BuildCommandLine(bool resolveExecutable)
    {
        // executable と args の両方で %APPDATA% などの環境変数を展開する。
        // これをしないと npm 系 CLI のように %APPDATA%\npm\*.cmd に置かれる
        // ツールを profiles.json に書くときユーザーごとに絶対パスを直書きする
        // 必要が出てきて非ポータブル。
        var exe = resolveExecutable
            ? ResolveExecutablePath()
            : Environment.ExpandEnvironmentVariables(Executable);
        var exeToken = resolveExecutable ? QuoteIfNeeded(exe) : exe;
        var args = EffectiveArgs();
        if (args.Length == 0) return exeToken;
        var expandedArgs = args.Select(Environment.ExpandEnvironmentVariables);
        return $"{exeToken} {string.Join(' ', expandedArgs)}";
    }

    /// <summary>
    /// 実際に起動へ渡す引数列。<see cref="ResumeOnStart"/> が true のとき、解決した
    /// セッション復帰トークンを Args 末尾へ付加する (codex の "resume" サブコマンドも
    /// 末尾付加で成立し、claude/copilot の "--resume" も同様)。
    /// </summary>
    public string[] EffectiveArgs()
    {
        if (!ResumeOnStart) return Args;
        var resume = ResumeArgsFor(this);
        return resume.Length == 0 ? Args : [.. Args, .. resume];
    }

    /// <summary>
    /// セッション復帰トークンを <see cref="CommandType"/> から解決する
    /// (ClaudeCode/CopilotCli=--resume, Codex=resume)。それ以外は空 (= 付加しない)。
    /// codex の "resume" はサブコマンドだが、起動コマンドは
    /// `cmd /c codex.cmd` 形式で末尾に付くため引数末尾付加で成立する。
    /// (Nickname は MCP 受信名として自由化されたため、種別は CommandType で判定する。)
    /// </summary>
    public static string[] ResumeArgsFor(SessionProfile p) => p.CommandType switch
    {
        CommandType.ClaudeCode => ["--resume"],
        CommandType.CopilotCli => ["--resume"],
        CommandType.Codex => ["resume"],
        _ => [],
    };

    /// <summary>
    /// 指定種別の既定プロファイル (プリセット) を返す。コマンド編集ダイアログで
    /// 種別選択時に各フィールドを読み出して適用する。Other は null。
    /// プリセットの実体は <see cref="SessionProfileLoader.CommandTemplates"/> の対応エントリ。
    /// </summary>
    public static SessionProfile? PresetFor(CommandType type)
    {
        if (type == CommandType.Other) return null;
        return Array.Find(SessionProfileLoader.CommandTemplates, p => p.CommandType == type);
    }

    /// <summary>
    /// commandType 未設定の旧 AMM ファイル向けに、nickname / executable / args から
    /// 種別を推測する。テンプレ nickname を最優先、続いて実行ファイル・引数で判定。
    /// </summary>
    private static CommandType InferCommandType(SessionProfile p)
    {
        switch (p.Nickname?.Trim().ToLowerInvariant())
        {
            case "claude": return CommandType.ClaudeCode;
            case "codex": return CommandType.Codex;
            case "copilot": return CommandType.CopilotCli;
        }
        var exe = (p.Executable ?? "").ToLowerInvariant();
        var args = p.Args ?? [];
        var argsJoined = string.Join(" ", args).ToLowerInvariant();
        if (exe.Contains("claude")) return CommandType.ClaudeCode;
        // argsJoined.Contains だと作業ディレクトリ等のパスに "codex"/"copilot" が
        // 部分一致して誤判定する (例: C:\my-codex-notes\)。トークン単位で照合する。
        if (ArgsReferToTool(args, "codex")) return CommandType.Codex;
        if (ArgsReferToTool(args, "copilot")) return CommandType.CopilotCli;
        if (exe.Contains("powershell") || exe.Contains("pwsh")) return CommandType.PowerShell;
        // 純粋な cmd.exe のみ Cmd。`cmd /c 別スクリプト` で他ツール (gemini 等) を
        // 起動している場合はラッパーなので Other 扱い (未対応 CLI は「その他」)。
        if ((exe.EndsWith("cmd.exe") || exe == "cmd") && !argsJoined.Contains("/c")) return CommandType.Cmd;
        return CommandType.Other;
    }

    /// <summary>
    /// args のいずれかのトークン (空白/パス区切りで分割し拡張子を除いた basename) が
    /// <paramref name="tool"/> 名と一致するか。`cmd /c codex.cmd` の "codex" は拾うが、
    /// パスに tool 名を部分的に含むだけのもの (C:\my-codex-notes\ 等) は拾わない。
    /// </summary>
    private static bool ArgsReferToTool(string[] args, string tool)
    {
        foreach (var a in args)
        {
            foreach (var tok in a.Split([' ', '\t', '\\', '/'], StringSplitOptions.RemoveEmptyEntries))
            {
                var name = tok;
                int dot = name.IndexOf('.');
                if (dot > 0) name = name[..dot];
                if (string.Equals(name, tool, StringComparison.OrdinalIgnoreCase)) return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Nickname を MCP 連携で安全な識別子へ正規化する。participant の一意キーが
    /// "{Nickname}#{Instance}" 形式のため '#' は使用不可。空白・制御文字は recipient
    /// 指定を曖昧にするので '_' へ置換し、連続は 1 つに圧縮、前後の '_' は落とす。
    /// 日本語等の Unicode は recipient 文字列照合で扱えるため保持する。
    /// 空 / 空白のみ / 結果が空なら null を返す。手入力・自動付与の両方で通す。
    /// </summary>
    public static string? EscapeNickname(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var sb = new System.Text.StringBuilder(raw.Length);
        bool pendingUnderscore = false;
        foreach (var ch in raw.Trim())
        {
            if (ch == '#' || char.IsWhiteSpace(ch) || char.IsControl(ch))
            {
                pendingUnderscore = sb.Length > 0; // 先頭の連続は無視 (前置 '_' を作らない)
                continue;
            }
            if (pendingUnderscore) { sb.Append('_'); pendingUnderscore = false; }
            sb.Append(ch);
        }
        var result = sb.ToString();
        return result.Length == 0 ? null : result;
    }

    /// <summary>
    /// 実行ファイル名を解決する。パス区切りを含む / ルート付きの指定は
    /// ユーザー明示パスとしてそのまま (環境変数展開のみ)。裸名のみ
    /// <see cref="SafeSearchPath"/> で PATH を探索し絶対パスへ固定する。
    /// </summary>
    public string ResolveExecutablePath()
    {
        var exe = Environment.ExpandEnvironmentVariables(Executable);
        if (string.IsNullOrEmpty(exe)) return exe;
        if (exe.Contains('\\') || exe.Contains('/') || Path.IsPathRooted(exe))
            return exe; // 明示パス: PATH 探索しない
        return SafeSearchPath(exe) ?? exe; // 裸名: 解決失敗時は元のまま
    }

    private static string QuoteIfNeeded(string path)
    {
        if (string.IsNullOrEmpty(path)) return path;
        if (path[0] == '"') return path; // 既にクォート済
        return path.IndexOf(' ') >= 0 ? $"\"{path}\"" : path;
    }

    /// <summary>
    /// PATH 環境変数を探索して裸の実行ファイル名を絶対パスへ解決する。
    /// セキュリティ上、ルート付きでない (= カレント/相対由来の) PATH 成分は
    /// 探索対象から除外し、System32 を先頭に優先する。見つからなければ null。
    /// </summary>
    private static string? SafeSearchPath(string name)
    {
        var hasExt = Path.HasExtension(name);
        var exts = (Environment.GetEnvironmentVariable("PATHEXT") ?? ".EXE;.CMD;.BAT;.COM")
            .Split(';', StringSplitOptions.RemoveEmptyEntries);
        var pathDirs = (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);

        // System32 を先頭に優先 (cmd.exe / powershell.exe 等を確実に解決)。
        var system32 = Environment.GetFolderPath(Environment.SpecialFolder.System);
        IEnumerable<string> dirs = string.IsNullOrEmpty(system32)
            ? pathDirs
            : new[] { system32 }.Concat(pathDirs);

        foreach (var rawDir in dirs)
        {
            var dir = rawDir.Trim().Trim('"');
            if (dir.Length == 0) continue;
            if (!Path.IsPathRooted(dir)) continue; // 相対/カレント由来は除外 (ハイジャック防止)
            try
            {
                if (hasExt)
                {
                    var p = Path.Combine(dir, name);
                    if (File.Exists(p)) return p;
                }
                else
                {
                    foreach (var ext in exts)
                    {
                        var p = Path.Combine(dir, name + ext);
                        if (File.Exists(p)) return p;
                    }
                }
            }
            catch { /* 無効なパス成分は無視 */ }
        }
        return null;
    }

    public System.Text.Encoding GetEncoding()
    {
        return OutputEncoding.ToUpperInvariant() switch
        {
            "UTF-8" or "UTF8" => System.Text.Encoding.UTF8,
            "SHIFT_JIS" or "SHIFT-JIS" => System.Text.Encoding.GetEncoding(932),
            _ => System.Text.Encoding.UTF8
        };
    }
}

public sealed class ProfilesRoot
{
    [JsonPropertyName("profiles")]
    public SessionProfile[] Profiles { get; set; } = [];

    /// <summary>外部 MCP サーバ設定 (req-20260622-mcp-gateway)。null の場合はゲートウェイ無効。</summary>
    [JsonPropertyName("mcpServers")]
    public McpServerConfig[]? McpServers { get; set; }
}

/// <summary>.ammprofiles エクスポートファイルの DTO。</summary>
public sealed class ProfilesExportFile
{
    [JsonPropertyName("version")]
    public int Version { get; set; } = 1;

    [JsonPropertyName("profiles")]
    public List<SessionProfile> Profiles { get; set; } = [];
}

public static class SessionProfileLoader
{
    public static SessionProfile[] CreateDefaultProfiles() => [CreateDefaultCmd()];

    public static SessionProfile[] Load(string path) => LoadRoot(path).Profiles;

    public static ProfilesRoot LoadRoot(string path)
    {
        if (!File.Exists(path))
            return new ProfilesRoot { Profiles = CreateDefaultProfiles() };

        try
        {
            var json = File.ReadAllText(path);
            var rawRoot = JsonSerializer.Deserialize<ProfilesRoot>(json);
            var root = rawRoot ?? new ProfilesRoot();
            if (rawRoot?.Profiles == null)
                root.Profiles = CreateDefaultProfiles();
            // 旧フィールド名 (promptRenameOnStart 等) を新フィールドへ移行。
            // ExtraProperties から既知キーを抜き取り、以降の Serialize でも
            // 旧キーが書き戻されないようにする。
            foreach (var p in root.Profiles) p.MigrateLegacyFields();
            return root;
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException(
                $"profiles 設定ファイルの形式が不正です: {path}. JSON のエスケープやカンマを確認してください。",
                ex);
        }
    }

    private static SessionProfile CreateDefaultCmd() => new()
    {
        Name = "CMD",
        Executable = "cmd.exe",
        NewlineMode = NewlineMode.CRLF,
        AutoChcp = true,
        WaitPatterns = ["[>]\\s*$"],
        // 空文字 = ResolveWorkingDirectory がカレントディレクトリを返す。
        WorkingDirectory = "",
    };

    /// <summary>
    /// 「ファイル → コマンド追加」ダイアログのテンプレート一覧
    /// (UDR-amm-20260427T0159-d4e)。空テンプレートを末尾に置き、ユーザーが
    /// ゼロから入力するケースもカバー。配布物に同梱するため static array。
    /// </summary>
    public static SessionProfile[] CommandTemplates =>
    [
        new()
        {
            Name = "CMD",
            CommandType = CommandType.Cmd,
            Executable = "cmd.exe",
            NewlineMode = NewlineMode.CRLF,
            OutputEncoding = "UTF-8",
            AutoChcp = true,
            WaitPatterns = ["[>]\\s*$"],
            WorkingDirectory = "%USERPROFILE%",
        },
        new()
        {
            Name = "PowerShell",
            CommandType = CommandType.PowerShell,
            Executable = "powershell.exe",
            Args = ["-NoLogo"],
            NewlineMode = NewlineMode.CRLF,
            OutputEncoding = "UTF-8",
            AutoChcp = true,
            WaitPatterns = ["PS\\s+\\S+>\\s*$"],
            WorkingDirectory = "%USERPROFILE%",
        },
        new()
        {
            Name = "Claude Code",
            CommandType = CommandType.ClaudeCode,
            Executable = "claude.exe",
            NewlineMode = NewlineMode.LF,
            OutputEncoding = "UTF-8",
            AutoChcp = false,
            WaitPatterns = ["^>"],
            WorkingDirectory = "%USERPROFILE%",
            // 複数行プロンプトを 1 つの prompt として届けるため bracketed paste で送る。
            UseBracketedPaste = true,
            // AI CLI はプロジェクト単位で作業ディレクトリを切り替えるのが普通の運用。
            // 起動毎にフォルダ選択ダイアログを出させる (UDR-amm-2026???-?: AI 既定 ON)。
            SelectWorkingDirOnStart = true,
            // AI CLI は複数並走させて名前で識別したい運用が多いので、起動時に
            // MDI ウィンドウ名の変更ダイアログも既定で出す。
            PromptNewNameOnCommandAdd = true,
            QuickPrompts =
            [
                new() { Label = "continue", Prompt = "continue" },
            ],
        },
        new()
        {
            Name = "Codex",
            CommandType = CommandType.Codex,
            Executable = "cmd.exe",
            Args = ["/c", "%APPDATA%\\npm\\codex.cmd"],
            NewlineMode = NewlineMode.LF,
            OutputEncoding = "UTF-8",
            AutoChcp = false,
            WaitPatterns = ["^>", "^[❯›]"],
            WorkingDirectory = "%USERPROFILE%",
            UseBracketedPaste = true,
            // AI CLI 系はプロジェクト単位で cwd を切り替えるのが普通の運用。
            SelectWorkingDirOnStart = true,
            PromptNewNameOnCommandAdd = true,
            QuickPrompts =
            [
                new() { Label = "continue", Prompt = "continue" },
            ],
        },
        new()
        {
            Name = "Copilot",
            CommandType = CommandType.CopilotCli,
            Executable = "cmd.exe",
            Args = ["/c", "%APPDATA%\\npm\\copilot.cmd"],
            NewlineMode = NewlineMode.LF,
            OutputEncoding = "UTF-8",
            AutoChcp = false,
            WaitPatterns = ["^>", "^[❯›]"],
            WorkingDirectory = "%USERPROFILE%",
            // Copilot CLI (Ink ベース TUI) は素のバイト書き込みを discard し、
            // bracketed paste mode (\x1b[200~..\x1b[201~) で囲ったペーストのみ
            // 受理するため、useBracketedPaste を既定で ON にする。
            UseBracketedPaste = true,
            // AI CLI 系はプロジェクト単位で cwd を切り替えるのが普通の運用。
            SelectWorkingDirOnStart = true,
            PromptNewNameOnCommandAdd = true,
            QuickPrompts =
            [
                new() { Label = "continue", Prompt = "continue" },
            ],
        },
        new()
        {
            Name = "Antigravity",
            Executable = "agy.exe",
            Args = [],
            NewlineMode = NewlineMode.LF,
            OutputEncoding = "UTF-8",
            AutoChcp = false,
            WaitPatterns = ["^>", "^[❯›]"],
            WorkingDirectory = "%USERPROFILE%",
            UseBracketedPaste = true,
            SelectWorkingDirOnStart = true,
            PromptNewNameOnCommandAdd = true,
            QuickPrompts =
            [
                new() { Label = "continue", Prompt = "continue" },
            ],
        },
        new()
        {
            Name = "(空テンプレート)",
            Executable = "",
        },
    ];
}
