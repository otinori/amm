# 方式設計書：WinForms MDI Multi-Terminal Manager v3

## 1. 概要

Windows Forms の MDI を活用し、複数の独立した CLI セッション（Claude Code, PowerShell, CMD 等）をタイル状に並べて管理するデスクトップアプリケーション。  
**完全オフライン動作**を前提とし、外部 CDN に依存しない。  
MDI ペインでは通常のターミナル操作（1行入力・矢印キー操作）を行い、**複数行の編集・確定送信は専用入力パネル**からフォーカスウィンドウへ一括送信する方式を基本とする。

---

## 2. 技術スタック

| 分類 | 採用技術 | 備考 |
|------|----------|------|
| ホスト | .NET 8 / Windows Forms | |
| MDI 管理 | `Form.IsMdiContainer = true` | 標準 WinForms 機能 |
| Terminal UI | xterm.js v5.x（ローカル同梱） | オフライン前提。バージョン固定 |
| xterm.js Addon | xterm-addon-fit v0.8.x（ローカル同梱） | リサイズ自動フィット。必須 |
| ブラウザエンジン | Microsoft.Web.WebView2 | Fixed Version Runtime をプロジェクト同梱 |
| Pty ブリッジ | Windows ConPTY API（P/Invoke 直書き） | NuGet 依存なし |

### オフライン対応方針

- xterm.js / xterm-addon-fit は npm からビルド済み JS を `Resources/js/` に同梱
- WebView2 は Fixed Version Runtime を `runtimes/webview2/` に同梱。CDN・自動更新不要
- インターネット接続ゼロで全機能が動作すること

---

## 3. UI 設計

### 全体レイアウト

```
┌─────────────────────────────────────────────────────┐
│  メニューバー [新規▼] [整列▼] [設定]                │
├─────────────────────────────────────────────────────┤
│  MDI エリア（通常操作・1行入力・カーソル移動）        │
│ ┌──────────────┐  ┌──────────────┐                 │
│ │ ● CMD        │  │ ⚙ PowerShell │  ← 入力待ち表示 │
│ │ C:\>_        │  │ PS C:\>_     │                 │
│ │  ↑↓←→で操作  │  │  直接1行入力  │                 │
│ └──────────────┘  └──────────────┘                 │
├─────────────────────────────────────────────────────┤
│  複数行入力パネル（常時表示・固定）                   │
│ ┌──────────────────────────────┐ [送信] [クリア]    │
│ │ 複数行テキストボックス        │ ↑↓ 履歴           │
│ │ （Shift+Enter で改行）        │                   │
│ └──────────────────────────────┘                   │
│  送信先: [● CMD - 入力待ち] [Ctrl+Enter で一括送信] │
└─────────────────────────────────────────────────────┘
```

### 操作モデル

| 操作 | 手段 | 備考 |
|------|------|------|
| カーソル移動・履歴選択 | MDI ペイン内で矢印キー操作 | xterm.js に直接キーを流す |
| 1行入力・即時実行 | MDI ペイン内で直接タイプ → Enter | 通常のターミナル操作と同じ |
| 複数行を編集して確定送信 | 複数行入力パネルで編集 → Ctrl+Enter | フォーカスウィンドウへ一括送信 |
| 過去の送信内容を再利用 | 入力パネルで ↑↓ キー | 送信履歴をサイクル |

### 複数行入力パネル仕様

| 項目 | 仕様 |
|------|------|
| コントロール | `TextBox` (Multiline=true, ScrollBars=Vertical) |
| フォント | 等幅フォント（Cascadia Code / Consolas）。xterm.js 側と統一 |
| 送信操作 | `Ctrl+Enter` キー または [送信] ボタン |
| 改行挿入 | `Shift+Enter` キー（パネル内での改行） |
| 送信後動作 | 入力パネルをクリア、フォーカスをアクティブ MDI 子に戻す |
| 送信先表示 | パネル下部に「送信先: [セッション名 - 状態]」を常時表示 |
| 送信方式 | テキスト全体を **1回で一括送信**（行分割・待機制御なし）。改行コードはプロファイルの `newlineMode` に従う |
| 送信ボタン制御 | 送信先が「実行中」状態、または子ウィンドウがゼロのとき [送信] を無効化 |
| 入力履歴 | 送信成功時に履歴へ追加。↑↓ キーでサイクル（最大 100 件） |

---

## 4. 入力待ち状態の可視化

CLI ツール（特に Claude Code）が入力待ちになったことをユーザーに明示する。

### 検出方法

ConPTY の出力ストリームを監視し、以下のパターンで入力待ちを判定する。
**パターンはプロファイルごとに `profiles.json` で定義・拡張できる**構造にする。

```csharp
// デフォルト入力待ちパターン例（正規表現）
static readonly Regex[] DefaultWaitPatterns = [
    new(@"[\$#>]\s*$"),                           // 一般シェルプロンプト
    new(@"PS\s+\S+>\s*$"),                        // PowerShell
    new(@"\(y/n\)\s*$", RegexOptions.IgnoreCase), // Yes/No 確認
    new(@"password[:\s]*$", RegexOptions.IgnoreCase), // パスワード入力
    new(@":\s*$"),                                // 入力促進
    new(@"\?\s*$"),                               // ? で終わる対話プロンプト
    new(@"続行するには何かキーを押してください"),  // pause コマンド
    new(@">\s*$"),                                // Claude Code 等
];
```

> **注意：** 出力の完全な停止だけでは判定できない（長時間処理中も無音になる）ため、パターンマッチを主軸とし、タイムアウト補助（約 500ms 無出力）を組み合わせる。

### 視覚的フィードバック

| 状態 | タイトルバー | ステータス色 | 入力パネル送信先表示 |
|------|-------------|-------------|---------------------|
| 実行中 | `⚙ CMD` | グレー | `CMD - 実行中` |
| 入力待ち | `● CMD` | **緑** | `CMD - 入力待ち ✓` |
| 不明/タイムアウト | `? CMD` | 黄 | `CMD - 不明` |
| 停止/エラー | `✗ CMD` | 赤 | `CMD - 停止` |

---

## 5. セッションプロファイル設計

起動ターゲット・改行コード・入力待ちパターンをプロファイルとして外部定義する。
`profiles.json` をアプリ起動フォルダに配置し、起動時に読み込む。

```json
{
  "profiles": [
    {
      "name": "CMD",
      "executable": "cmd.exe",
      "args": [],
      "newlineMode": "CRLF",
      "outputEncoding": "UTF-8",
      "autoChcp": true,
      "waitPatterns": ["[>]\\s*$"]
    },
    {
      "name": "Claude Code",
      "executable": "claude.exe",
      "args": [],
      "newlineMode": "LF",
      "outputEncoding": "UTF-8",
      "autoChcp": false,
      "waitPatterns": [">\\s*$"]
    },
    {
      "name": "PowerShell",
      "executable": "powershell.exe",
      "args": ["-NoLogo"],
      "newlineMode": "CRLF",
      "outputEncoding": "UTF-8",
      "autoChcp": true,
      "waitPatterns": ["PS\\s+\\S+>\\s*$"]
    }
  ]
}
```

### プロファイルフィールド定義

| フィールド | 型 | 説明 |
|-----------|-----|------|
| `name` | string | メニュー表示名・タイトルバー表示名 |
| `executable` | string | 実行ファイルパス |
| `args` | string[] | 起動引数 |
| `newlineMode` | `"CRLF"` \| `"LF"` | 一括送信時の改行コード変換。CMD/PS は CRLF、Claude Code は LF |
| `outputEncoding` | string | ConPTY 出力の文字エンコーディング |
| `autoChcp` | bool | 起動直後に `chcp 65001` を自動送信するか（日本語環境の文字化け対策） |
| `waitPatterns` | string[] | 入力待ち判定パターン（正規表現）。空の場合はデフォルトパターンを使用 |

---

## 6. コンポーネント設計

### A. MdiParentForm（親ウィンドウ）

**役割：** 子ウィンドウ管理・タイリング・複数行入力パネルのホスト

**主要機能：**
- `profiles.json` を読み込み、メニューにプロファイル一覧を動的生成
- メニューから選択したプロファイルで TerminalChildForm を起動
- `LayoutMdi(MdiLayout.TileVertical / TileHorizontal)` によるウィンドウ整列
- 複数行入力パネルを下部に **常時固定表示**（MDI エリアに侵食されない）
- `ActiveMdiChild` 変更イベントで送信先表示を更新
- 子ウィンドウが閉じられたとき送信先表示を更新、子ゼロ時は [送信] を無効化
- MDI ペイン内のキー操作（矢印・1行入力）は xterm.js に直接流す（介入しない）
- 送信先が「実行中」状態のとき [送信] ボタンを無効化（誤送信防止）

### B. TerminalChildForm（子ウィンドウ）

**役割：** 個別ターミナルセッションのホスト

**構成：**
- `WebView2` を `Dock.Fill` で配置
- `terminal.html`（ローカル埋め込みリソース）をロード
- 起動時に `SessionProfile` を受け取り ConPTY 経由で実行
- `WaitStateChanged` イベントで親フォームに入力待ち状態を通知
- `Activated` イベントで `term.focus()` を明示的に呼び出し

**MDI 内直接入力：**
xterm.js のキーイベントをそのまま ConPTY に流す。矢印キーによるカーソル移動・履歴選択、1行入力・即時実行はすべて MDI ペイン内で完結する。複数行を編集して確定送信する場合のみ複数行入力パネルを使用する。

### C. ConPtyWrapper（通信ハブ）

**役割：** C# ↔ OS プロセス ↔ JavaScript の仲介

**データフロー：**

```
【入力】
複数行入力パネル (C#)  ──→  newlineMode 変換 → テキスト全体を1回で送信  ──→  ConPTY InputPipe  ──→  プロセス
xterm.js (JS)          ──→  WebMessageReceived                           ──→  ConPTY InputPipe  ──→  プロセス

【出力】
プロセス  ──→  ConPTY OutputPipe  ──→  読み取りスレッド (C#)
                                         ├─→  WaitPatternDetector（入力待ち判定）
                                         └─→  UI スレッド Invoke
                                               └─→  PostWebMessageAsString(ansiData)
                                                     └─→  xterm.js term.write(data)
```

### D. WaitPatternDetector（入力待ち判定）

- プロファイルの `waitPatterns` を優先使用。空の場合はデフォルトパターンを適用
- 500ms 無出力タイムアウトを補助判定として併用
- 判定結果を `WaitStateChanged` イベントで通知

---

## 7. 実装の重要ポイント

### ① ConPTY P/Invoke 実装（必須手順）

```csharp
// 必須 API 一覧
[DllImport("kernel32.dll")] CreatePseudoConsole(COORD size, HANDLE hInput, HANDLE hOutput, DWORD flags, out HPCON phPC)
[DllImport("kernel32.dll")] ResizePseudoConsole(HPCON hPC, COORD size)
[DllImport("kernel32.dll")] ClosePseudoConsole(HPCON hPC)
[DllImport("kernel32.dll")] InitializeProcThreadAttributeList(...)
[DllImport("kernel32.dll")] UpdateProcThreadAttribute(..., PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE, hPC, ...)
[DllImport("kernel32.dll")] DeleteProcThreadAttributeList(...)
[DllImport("kernel32.dll")] CreateProcess(..., ref STARTUPINFOEX siEx, ...)

// プロセス起動手順（順序厳守）
// 1. CreatePipe でパイプペアを作成（inputRead/inputWrite, outputRead/outputWrite）
// 2. CreatePseudoConsole(size, inputRead, outputWrite, ...) で hPC を取得
// 3. InitializeProcThreadAttributeList でアトリビュートリストを準備
// 4. UpdateProcThreadAttribute で PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE をセット
// 5. STARTUPINFOEX に lpAttributeList をセット
// 6. CreateProcess で起動
// 7. inputRead / outputWrite（ConPTY 側）は即 CloseHandle
```

### ② OutputPipe 読み取りスレッド設計

`async/await` ではなく**専用スレッド**で実装する。UI スレッドへの配送は `BeginInvoke` を使用。

```csharp
_readThread = new Thread(() =>
{
    var buf = new byte[4096];
    while (true)
    {
        int n = _outputStream.Read(buf, 0, buf.Length);
        if (n <= 0) break;
        var ansi = _outputEncoding.GetString(buf, 0, n); // プロファイルの outputEncoding を使用
        _waitDetector.Feed(ansi);
        _webView.BeginInvoke(() =>
            _webView.CoreWebView2.PostWebMessageAsString(
                JsonSerializer.Serialize(new { type = "output", data = ansi })));
    }
}) { IsBackground = true };
_readThread.Start();
```

### ③ 起動順序（順序厳守）

```csharp
// 1. WebView2 環境初期化（共有 UserDataFolder）
var env = await CoreWebView2Environment.CreateAsync(
    browserExecutableFolder: Path.Combine(Application.StartupPath, "runtimes", "webview2"),
    userDataFolder: Path.Combine(Application.StartupPath, "WebViewShared")
);
// 2. WebView2 初期化完了まで待機
await webView.EnsureCoreWebView2Async(env);
// 3. VirtualHostName でリソースフォルダをマッピング（file:// より安全）
webView.CoreWebView2.SetVirtualHostNameToFolderMapping(
    "app.local",
    Path.Combine(Application.StartupPath, "Resources"),
    CoreWebView2HostResourceAccessKind.Allow
);
// 4. ローカル HTML をナビゲート
webView.CoreWebView2.Navigate("https://app.local/terminal.html");
// 5. NavigationCompleted イベント後に ConPTY 起動
webView.CoreWebView2.NavigationCompleted += (s, e) => {
    if (!e.IsSuccess) {
        MessageBox.Show($"terminal.html のロードに失敗しました: {e.WebErrorStatus}");
        return;
    }
    // 初期サイズは 120×30 をデフォルトとする
    conPty.Start(profile, defaultCols: 120, defaultRows: 30);
    // autoChcp が true のプロファイルは起動直後に UTF-8 へ切り替え
    if (profile.AutoChcp) conPty.Write("chcp 65001\r\n");
};
// ※ NavigationCompleted 前に PostWebMessageAsString を呼ぶと例外が発生する
```

### ④ TerminalChildForm の Dispose 順序（順序厳守）

```
1. ConPTY InputPipe を Close
2. プロセスの終了を Wait（タイムアウト 3 秒）
3. ClosePseudoConsole(hPC)
4. OutputPipe を Close → 読み取りスレッド自然終了を待つ
5. SafeHandle / パイプハンドル群を Dispose
```

### ⑤ ConPTY リサイズ（ウィンドウリサイズ連動）

```javascript
// terminal.html 側：リサイズ検知
fitAddon.fit();
window.chrome.webview.postMessage(JSON.stringify({
    type: "resize",
    cols: term.cols,
    rows: term.rows
}));
```

```csharp
// C# 側：WebMessageReceived でリサイズ処理
case "resize":
    ResizePseudoConsole(_hPC, new COORD { X = (short)cols, Y = (short)rows });
    break;
```

### ⑥ xterm.js v5 初期化（terminal.html）

xterm.js v5 では Addon の名前空間が変わっている。v4 系サンプルの混入に注意。

```html
<link rel="stylesheet" href="./js/xterm.css" />
<script src="./js/xterm.js"></script>
<script src="./js/xterm-addon-fit.js"></script>
<script>
  const term = new Terminal({
      convertEol: true,
      fontFamily: '"Cascadia Code", "Consolas", monospace',
      fontSize: 13,
      theme: { background: '#1e1e1e', foreground: '#d4d4d4' }
  });
  const fitAddon = new FitAddon.FitAddon();   // v5: 名前空間付き
  term.loadAddon(fitAddon);
  term.open(document.getElementById('terminal'));
  fitAddon.fit();

  // リサイズ通知
  term.onResize(({ cols, rows }) => {
      window.chrome.webview.postMessage(JSON.stringify({ type: 'resize', cols, rows }));
  });
  new ResizeObserver(() => fitAddon.fit()).observe(document.getElementById('terminal'));

  // C# からの出力受信
  window.chrome.webview.addEventListener('message', e => {
      const msg = JSON.parse(e.data);
      if (msg.type === 'output') term.write(msg.data);
  });

  // キー入力を C# へ転送
  term.onData(data => {
      window.chrome.webview.postMessage(JSON.stringify({ type: 'input', data }));
  });
</script>
```

### ⑦ WebView2 フォーカス管理

`ActiveMdiChild` を切り替えても xterm.js に自動的にフォーカスは移らない。
`Activated` イベントで明示的に `term.focus()` を呼ぶこと。

```csharp
this.Activated += async (s, e) =>
    await webView.CoreWebView2.ExecuteScriptAsync("term.focus()");
```

### ⑧ 複数行一括送信の改行コード変換

```csharp
// 送信時にプロファイルの newlineMode に従って変換
var nl = profile.NewlineMode == NewlineMode.LF ? "\n" : "\r\n";
var text = inputBox.Text.ReplaceLineEndings(nl);
conPty.Write(text);
```

### ⑨ JS ↔ C# メッセージプロトコル

```json
// JS → C#（キー入力）
{ "type": "input", "data": "\r" }

// JS → C#（リサイズ）
{ "type": "resize", "cols": 120, "rows": 30 }

// C# → JS（出力 / ANSI データ）
{ "type": "output", "data": "\u001b[32mC:\\>\u001b[0m " }
```

### ⑩ WebView2 Fixed Version Runtime の配布方針

Fixed Version Runtime は約 150〜200 MB あるため Git 管理対象外とする。

```
# .gitignore に追記
runtimes/webview2/
```

配布方法はインストーラ（WiX / NSIS 等）に同梱するか、初回起動時にセットアップスクリプトで配置する方針とし、README に手順を記載する。

---

## 8. ソリューション構成

```
MultiTerminalManager/
├── MultiTerminalManager.sln
└── MultiTerminalManager/
    ├── MultiTerminalManager.csproj        # .NET 8, WinForms
    ├── Program.cs
    ├── profiles.json                      # セッションプロファイル定義
    ├── Forms/
    │   ├── MdiParentForm.cs               # 入力パネル・MDI 管理
    │   ├── MdiParentForm.Designer.cs
    │   ├── TerminalChildForm.cs           # WebView2 ホスト
    │   └── TerminalChildForm.Designer.cs
    ├── Core/
    │   ├── ConPtyWrapper.cs               # P/Invoke 実装
    │   ├── WaitPatternDetector.cs         # 入力待ち判定
    │   └── SessionProfile.cs             # プロファイルモデル・JSON デシリアライズ
    ├── Resources/
    │   ├── terminal.html                  # xterm.js ホスト HTML
    │   └── js/
    │       ├── xterm.js                   # v5.x ローカル同梱
    │       ├── xterm.css                  # ローカル同梱
    │       └── xterm-addon-fit.js         # v0.8.x ローカル同梱
    └── runtimes/
        └── webview2/                      # Fixed Version Runtime 同梱（.gitignore 対象）
```

### .csproj 設定

```xml
<ItemGroup>
  <Content Include="runtimes\webview2\**">
    <CopyToOutputDirectory>PreserveNewest</CopyToOutputDirectory>
  </Content>
  <Content Include="Resources\**">
    <CopyToOutputDirectory>PreserveNewest</CopyToOutputDirectory>
  </Content>
  <Content Include="profiles.json">
    <CopyToOutputDirectory>PreserveNewest</CopyToOutputDirectory>
  </Content>
</ItemGroup>
```

---

## 9. 開発フェーズ

| Phase | 内容 | 完了条件 |
|-------|------|----------|
| **Phase 1** | MDI 親フォーム + 複数行入力パネル + WebView2 搭載子フォームの作成 | 子ウィンドウが開き WebView2 が表示される。入力パネルが固定表示される |
| **Phase 2** | xterm.js（ローカル）をロード、C# と疎通確認 | JS↔C# メッセージング往復・リサイズ連動が確認できる |
| **Phase 3** | ConPTY 導入、cmd.exe を xterm.js 上で操作可能に | ANSI カラー付きで cmd.exe が動作する。chcp 65001 自動送信で日本語表示を確認 |
| **Phase 4** | 複数行入力パネルからの一括送信・入力履歴が動作する | Ctrl+Enter でフォーカスウィンドウに一括送信、↑↓ で履歴呼び出し |
| **Phase 5** | 入力待ち状態の検出・可視化が動作する | プロンプト表示時にタイトル・色が変化する |
| **Phase 6** | profiles.json によるプロファイル切り替えが動作する | Claude Code / PowerShell / CMD を選択起動できる |

---

## 10. Claude Code への指示プロンプト（Phase 1〜4）

```
以下の方式設計書（v3）を読んで、Phase 1〜4 に相当する最小動作プロトタイプを作成してください。

【前提】
- 完全オフライン動作。CDN 参照は一切使用しない
- xterm.js v5.x / xterm-addon-fit は Resources/js/ にローカル同梱済みとして実装する
- WebView2 は Fixed Version Runtime を runtimes/webview2/ に同梱する前提で実装する
- ConPTY は NuGet を使わず P/Invoke で実装する
- リソース解決は SetVirtualHostNameToFolderMapping を使用する（file:// は使わない）

【作成してほしいもの】

1. MdiParentForm
   - profiles.json を読み込み、メニューにプロファイル一覧を動的生成
   - 下部に複数行入力パネル（TextBox Multiline, 等幅フォント）を常時固定表示
   - Ctrl+Enter でアクティブ MDI 子ウィンドウへ一括送信
     （テキスト全体を1回で送信。プロファイルの newlineMode で改行コードを変換）
   - Shift+Enter で改行挿入
   - ↑↓ キーで送信履歴をサイクル（最大 100 件）
   - ActiveMdiChild 変更・子ウィンドウ消滅時に送信先表示を更新
   - 送信先が「実行中」または子ウィンドウゼロのとき [送信] を無効化
   - メニューから TerminalChildForm を起動、TileVertical 整列

2. TerminalChildForm
   - WebView2（Dock.Fill）で terminal.html をローカルロード
     （SetVirtualHostNameToFolderMapping 使用）
   - NavigationCompleted でエラーチェック後に ConPtyWrapper を起動
   - Activated イベントで term.focus() を明示的に呼び出す
   - ConPtyWrapper を保持し、Dispose 順序を正しく実装する
   - WaitStateChanged イベントで親フォームに入力待ち状態を通知

3. ConPtyWrapper
   - P/Invoke で CreatePseudoConsole / InitializeProcThreadAttributeList /
     UpdateProcThreadAttribute / CreateProcess の正しい手順で実装
   - OutputPipe 読み取りは専用スレッドで実装（async/await 不使用）
   - 出力エンコーディングはプロファイルの outputEncoding に従う
   - resize メッセージで ResizePseudoConsole を呼び出す（初期サイズ 120×30）
   - autoChcp が true のプロファイルは起動直後に chcp 65001 を自動送信
   - Dispose 順序: InputPipe Close → プロセス Wait(3秒) → ClosePseudoConsole
     → OutputPipe Close → スレッド Join → SafeHandle Dispose

4. WaitPatternDetector
   - プロファイルの waitPatterns を優先使用、空の場合はデフォルトパターンを適用
   - 500ms 無出力タイムアウトを補助判定として使用
   - WaitStateChanged イベントで状態変化を通知

5. SessionProfile / profiles.json
   - name / executable / args / newlineMode / outputEncoding / autoChcp / waitPatterns
   - 起動時に profiles.json を読み込む

6. terminal.html
   - xterm.js v5 / xterm-addon-fit をローカルパスで読み込み
   - FitAddon.FitAddon()（v5 の名前空間付き）で初期化
   - フォント: Cascadia Code / Consolas、サイズ 13、テーマ: dark
   - リサイズ時に cols/rows を C# に通知
   - メッセージプロトコル: { type, data } / { type, cols, rows }

【動作確認ポイント】
- オフライン環境で起動し、cmd.exe のプロンプトが表示される
- 日本語が文字化けしない（chcp 65001 自動送信）
- 複数行テキストを Ctrl+Enter で一括送信できる
- 送信履歴を ↑↓ キーで呼び出せる
- MDI 内での直接キー入力も動作する
- ウィンドウリサイズで xterm.js と ConPTY のサイズが連動する
- 子ウィンドウが閉じられたとき送信先表示が正しく更新される

実装は動作するコードを生成してください。TODO コメントは最小限に。
```
