# poc-tauri-terminal — 検証結果

## 構成
Node + Express + `ws` + `node-pty` + `@xterm/xterm`(+ addon-fit / addon-unicode11)。
Tauri本体（Rustプロセス + OS標準WebView）の代理として、「WebView内でxterm.jsを実PTYに繋ぐ」という
Tauriのアーキテクチャ上のUI層を直接検証する目的。ヘッドレスChromium(Playwright)で実ブラウザ描画を確認。

## 検証項目と結果

| 項目 | 結果 | 備考 |
|---|---|---|
| ANSIカラー(前景色 8色+リセット) | ✅ | `shots/01a-colors-unicode-emphasis.png` |
| 太字/斜体/下線 | ✅ | 同上 |
| 日本語テキスト | ✅ | `insertText`経由(後述の注記参照) |
| 絵文字 | ✅ (表示はされるがフォント依存) | このコンテナのデフォルトフォントでは絵文字の色/意匠が簡素。実機ではOSの絵文字フォントに依存 |
| 罫線(box-drawing) | △ | グリフはこのヘッドレス環境のフォールバックフォントに依存。実機(Windows/Mac標準フォント)では別途確認要 |
| リサイズ(fit-addon → pty resize) | ✅ | `shots/02-resized.png`: ビューポート1000→700pxで`tput cols`が100→79に追従、実シェル側も認識 |
| スクロールバック + ホイールスクロール | ✅ | `shots/01c-scrolled-up.png` |
| WebSocket経由の双方向PTY入出力 | ✅ | 実shell(bash)を起動し、入力送信・出力受信とも問題なし |

## 気づき・注意点

- **Playwrightの`keyboard.type()`は日本語/絵文字などマルチバイト文字で文字化け/誤動作した**
  (Tab補完が誤発火するなど)。1文字ずつkeydownを合成する方式のため。`keyboard.insertText()`
  (IME確定やペーストに近い単発input イベント)に切り替えて解消。→ **xterm.js側の問題ではなく
  テストツール側の入力合成方式の制約**。実際のIME確定/ペースト経路であれば問題ない。
- 罫線・絵文字の意匠はコンテナのフォールバックフォントに強く依存するため、この結果だけで
  実機(Windows/Mac)の見た目を断定はできない。実機確認が望ましい。
- xterm.jsの機能面(ANSI/色/太字/斜体/下線/リサイズ/スクロールバック/日本語)は、現行amm(WinForms+WebView2+xterm.js)と
  **同一のxterm.jsライブラリをそのまま使う**ため、実質的にゼロリスクで移植できることが確認できた。

## 起動方法(再現用)

```bash
cd reference/poc-tauri-terminal
npm install
node server.js   # http://localhost:5173
node probe.js     # Playwrightでの自動検証 + shots/ にスクリーンショット出力
```

## 実機(macOS)検証: WebKitエンジンでの再検証(2026-07-11)

**検証環境**: macOS 26.5.x, Apple Silicon(実機、サンドボックス代理ではない)。
Playwrightの **WebKitエンジン**(TauriがmacOSで使う`WKWebView`と同じレンダリングエンジン)で
`shots/`のバッテリを再実行し、上表で「実機確認要」としていた項目を解消した。

| 項目 | 結果 | 備考 |
|---|---|---|
| ANSIカラー/太字/斜体/下線 | ✅ | `shots-macos-webkit/01a-colors-unicode-emphasis.png` |
| 日本語テキスト | ✅ | 同上 |
| 絵文字(🎉🚀🔥😀👍✅❌⚠️) | ✅ **フルカラーのApple純正絵文字グリフ**、フォールバック無し | `shots-macos-webkit/05-emoji-zoom.png` |
| 罫線(box-drawing) | ✅ **実際に箱を組んだ状態で連続した罫線として正しく描画** | `shots-macos-webkit/04-boxdraw-grid.png`(`┌─┬─┐│A│B│├─┼─┤│C│D│└─┴─┘`を複数行のprintfで描画、隙間や欠けなし) |
| リサイズ | ✅ | `shots-macos-webkit/02-resized.png`: 1000→700pxで`tput cols`が100→79に追従(サンドボックス結果と一致) |
| スクロールバック+ホイールスクロール | ✅ | `shots-macos-webkit/01b-after-scrollback.png` / `01c-scrolled-up.png` |
| 外部フォントCDNへの依存 | ✅ なし | `public/index.html`に`@font-face`/CDN参照は無く、xterm.jsはOS標準フォントにフォールバックする構成。Flutter Web(CanvasKit)のようなGoogle Fonts依存は構造的に発生しない |

**注記**: `01a-colors-unicode-emphasis.png`の罫線行(`罫線: ┌─┬─┐│ │ │└─┴─┘`)は一見grid状に繋がっていないように見えるが、
これは元の文字列が「個々の罫線グリフを1行に並べただけ」(実際の箱を組んでいない)ためで、描画バグではない。
`04-boxdraw-grid.png`で実際に複数行に渡って箱を組んだ場合は完全に連続した罫線として正しく描画されることを確認済み。

**未確認のまま残っている項目**: 本検証は`node server.js`+実ブラウザ(WebKit)による「WebView層としてのxterm.js忠実度」の
一次検証であり、本物のTauriアプリ(Rust側でのWKWebViewホスティング + `.app`バンドル化)ではない。
この実機には`cargo`/`rustc`が未インストールのため、`tauri build`による`.app`バンドル配布形態の確認は
別途Rustツールチェイン導入が必要(ユーザー判断待ち)。

### 再現方法

```bash
cd reference/poc-tauri-terminal
npx playwright install webkit   # 初回のみ
node server.js &
node probe-webkit-macos.js      # shots-macos-webkit/ に出力
node probe-boxdraw-zoom.js      # 罫線の拡大検証(shots-macos-webkit/03,04)
node probe-emoji-zoom.js        # 絵文字の拡大検証(shots-macos-webkit/05)
```

## 本物のTauriアプリ(Rust + WKWebView)での`.app`バンドル検証(2026-07-12)

**検証環境**: macOS 26.5.2, Apple Silicon(実機)。`cargo 1.97.0` / `rustc 1.97.0` / `tauri-cli 2.11.4`を
導入し、上記まで「未確認のまま残っている項目」だった**本物のTauriアプリ**(Node/Playwright代理ではなく
Rust側でのWKWebViewホスティング)を検証した。`src-tauri/`は`tauri.conf.json`の`build.frontendDist`を
`../public`、`app.windows[0].url`を`http://localhost:5173`とし、既存の`public/`(xterm.js)・`server.js`
(WebSocket+node-pty)をそのまま流用する最小構成。

| 項目 | 結果 | 備考 |
|---|---|---|
| `cargo tauri build`(release、`.app`バンドル) | ✅ | `src-tauri/target/release/bundle/macos/poc-tauri-terminal.app`を生成。ビルド自体は12秒程度 |
| `.app`起動 → WKWebViewが`server.js`のページを読み込み | ✅ | `open poc-tauri-terminal.app`後、`com.apple.WebKit.Networking.xpc`ヘルパプロセスに`localhost:5173`へのESTABLISHED TCP接続を確認(`lsof -p <networking-xpc-pid>`) |
| WebSocket(`/pty`)経由でnode-ptyが実shellを起動 | ✅ | `node server.js`のプロセスツリーに`/bin/zsh`の子プロセスが出現、`.app`起動から生存し続けることを確認(スクリーンショットはScreen Recording権限未許可のためこの実行環境からは取得不可。プロセス/ソケットの観測で代替確認) |
| `.dmg`バンドル(`cargo tauri build --bundles dmg`) | ❌ | `bundle_dmg.sh`内のFinder見た目調整AppleScriptが`AppleEventがタイムアウトしました(-1712)`で失敗。原因はこの実行環境でTerminal(このコマンドを実行したプロセス)にFinder操作のAutomation権限が付与されていないことによるものと推定され、Tauri/xterm.js側の問題ではない。`.app`本体は`.dmg`生成の有無に関係なく正常に生成・起動できている |

**結論**: 「本物のTauriアプリ(Rust + WKWebView) + `.app`バンドル化」で懸念していた項目は解消。
xterm.js/node-ptyのアーキテクチャはNode代理検証・WebKit代理検証・実Tauriビルドの三段階すべてで
一貫して問題なく動作しており、移植リスクは低いと判断できる。`.dmg`配布物の生成は権限設定次第の
別問題であり、実機でTerminalにAutomation権限を付与すれば解消する見込み(未検証、必要になった時点で対応)。

### 再現方法

```bash
cd reference/poc-tauri-terminal
node server.js &                        # http://localhost:5173 (既存プロセスがあれば不要)
cargo tauri build                       # src-tauri/target/release/bundle/macos/*.app を生成
open src-tauri/target/release/bundle/macos/poc-tauri-terminal.app
```

## Windows実機: Rust側からのConPTY統合(`portable-pty` crate、Node.js不使用)(2026-07-13)

**検証環境**: Windows 10 Pro, Rust 1.97.0(rustup経由)、Visual Studio Build Tools 2022(C++ワークロード)、
tauri-cli 2.11.4。上記までの検証(`node server.js`をTauriのWebViewでホストするだけの代理検証)とは異なり、
**Node.js/node-ptyを一切使わず**、Rust側で`portable-pty` crateを使って直接ConPTYを叩く統合方式を検証した。

**構成**: `src-tauri/src/lib.rs`に`pty_spawn`/`pty_write`/`pty_resize`の3つの`#[tauri::command]`を実装。
`portable_pty::native_pty_system()`で疑似端末を開き、`COMSPEC`(cmd.exe)をスレーブ側にspawn、マスター側の
リーダーを別スレッドで読み取りTauriイベント(`pty-data`)としてフロントエンドへemitする。
フロントエンド(`public/conpty-native.html`)はWebSocketではなく`window.__TAURI__`のIPC(`invoke`/`listen`)で
xterm.jsとやり取りする。`tauri.conf.json`の`app.windows`をこのテスト用に`conpty-native.html`を指す
`conpty-native`ラベルの単一ウィンドウに変更し、`withGlobalTauri: true`を有効化。

| 項目 | 結果 | 備考 |
|---|---|---|
| `portable-pty` crateの導入・コンパイル | ✅ | 追加のネイティブ依存は不要(Cargo.tomlに1行追加のみ)、初回ビルドは依存クレート込みで約8分 |
| `cargo tauri build`(debug、`.exe`+MSI/NSISインストーラ) | ✅ | `app.exe`単体に加え、`.msi`/`-setup.exe`まで自動生成された(release/`.app`同様、配布物生成は問題なし) |
| アプリ起動 → ConPTY経由でcmd.exeを起動 | ✅ | プロセスツリーで確認: `app.exe`→`conhost.exe --headless --width 100 --height 30`(ConPTYの実体)→`cmd.exe`。`node-pty`を介さずRustから直接ConPTYを掴めることを実プロセスで確認 |
| WebView2⇔Rust間のIPC(`invoke`/`event.listen`)経由の双方向PTY入出力 | ✅ | `shots-windows-headed/conpty-native-02-echo.png`: `echo native-conpty-portable-pty-works`が正しくエコーされた |
| 日本語入力・出力 | ✅ | `shots-windows-headed/conpty-native-03-japanese.png`: 「日本語もConPTY経由で問題なし」が文字化けなく表示 |

**結論**: Windows版の移植において、`portable-pty` crateはnode-pty相当の機能をRustネイティブで代替でき、
実装コストも低い(3コマンド・約80行)ことを確認した。macOS側で確認済みの「Rust + WKWebView」の`.app`バンドル化と
合わせ、Tauriアーキテクチャは両OSともpty統合面でのリスクが低いと判断できる。

**未検証のまま残っている項目**: このPoCは1ウィンドウ=1ptyの最小構成。複数ペイン(前述のMDI代替プロトタイプ)を
Rust側のConPTY統合と組み合わせる場合、`PtyState`をペインID等でキー管理する設計が必要になる(未検証、
本格移植時に設計すること)。

### 再現方法

```bash
cd reference/poc-tauri-terminal
cargo tauri build --debug               # src-tauri/target/debug/app.exe (or bundle/) を生成
./src-tauri/target/debug/app.exe        # conpty-native.html を表示するウィンドウが起動
# 動作確認(任意): WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS=--remote-debugging-port=9333 を設定して起動し、
# node probe-conpty-native.js でPlaywrightのCDP接続経由でスクリーンショット・入力確認ができる
```

## Windows実機: システムメニュー拡張(「AMM ▶」サブメニュー挿入)(2026-07-13)

**検証環境**: 上記ConPTYネイティブ統合と同じ実行ファイル(`app.exe`)に追加実装。`windows` crate 0.61
(`Win32_UI_WindowsAndMessaging`/`Win32_UI_Shell`)を使い、Tauriの`WebviewWindow::hwnd()`で取得した
生HWNDに対して直接Win32 APIを呼び出す構成。

**実装**: `src-tauri/src/lib.rs`に以下を追加。
- `install_amm_system_menu`: `GetSystemMenu`+`CreatePopupMenu`+`AppendMenuW`で、標準の「閉じる」等の下に
  セパレータ+「AMM ▶」ポップアップサブメニュー(2項目)を挿入
- `install_amm_syscommand_hook`: comctl32の`SetWindowSubclass`でウィンドウプロシージャをサブクラス化し、
  `WM_SYSCOMMAND`をフックしてカスタムメニュー項目のクリックをTauriイベント(`amm-system-menu-clicked`)に変換
- フロントエンド(`conpty-native.html`)は`listen('amm-system-menu-clicked', ...)`でイベントを受け、
  画面下部にテキスト表示することで受信を可視化

| 項目 | 結果 | 備考 |
|---|---|---|
| `GetSystemMenu`+`AppendMenuW`でのサブメニュー挿入 | ✅ | PowerShell(`GetMenuStringW`)でシステムメニューを列挙し、`[8] 'AMM ▶'`とその配下2項目が実際に挿入されていることを実機で確認 |
| `SetWindowSubclass`での`WM_SYSCOMMAND`フック→Tauriイベント発火→JS側UI更新 | ✅ | `shots-windows-headed/sysmenu-01-after-flash-click.png`: `SendMessage(hwnd, WM_SYSCOMMAND, 0x1000, 0)`でクリックを模擬し、画面下部に`[時刻] システムメニュー[AMM ▶]から受信: flash`が表示されることを確認 |

### 気づき・注意点

- **システムコマンドIDは16の倍数でなければならない**: Win32のWM_SYSCOMMANDはwParamの下位4bitを
  「どう起動されたか(マウス/キーボード等)」のエンコードに予約しているため、OSがこれを上書きする。
  実装当初カスタムIDを`0x1001`/`0x1002`のように連番で振ったところ、ハンドラ側で`wParam & 0xFFF0`と
  マスクした値(`0x1000`)が元のID(`0x1001`)と一致せず、クリックを検出できないバグを実機テストで発見した。
  `0x1000`/`0x1010`のようにあらかじめ16の倍数で採番することで解消。ドキュメント上は知られた制約だが、
  実機で実際にクリックイベントが飛んでこないという形で顕在化しないと気づきにくい典型例。
- Tauriのフレームワーク自体はシステムメニュー拡張のAPIを提供していないが、`WebviewWindow::hwnd()`で
  生HWNDを取得できるため、`windows` crateで直接Win32 APIを呼び出すアプローチで問題なく実現できる。
  `SetWindowSubclass`はcomctl32の正規のサブクラス化API(`SetWindowLongPtr`でwndprocを直接差し替えるより
  安全)で、Tauri/tao側の既存ウィンドウプロシージャと共存できることを確認した。

### 再現方法

```bash
cd reference/poc-tauri-terminal
cargo tauri build --debug
./src-tauri/target/debug/app.exe
# システムメニュー確認: タイトルバー左上のアイコンを右クリック、またはウィンドウにフォーカスしてAlt+Space
# 自動検証(PowerShell、要management: MainWindowHandle取得 → GetSystemMenu/GetSubMenu/GetMenuStringWで列挙、
# SendMessage(hwnd, 0x0112, 0x1000, 0)でWM_SYSCOMMANDを模擬送信)
```

## Windows実機: タスクバー点滅(`FlashWindowEx`)(2026-07-13)

**検証環境**: 同上(`app.exe`に追加実装)。`FlashWindowEx`(`FLASHW_TRAY | FLASHW_TIMERNOFG`)を呼ぶ
`flash_window` Tauriコマンドを追加し、画面下部のボタンおよび既存のシステムメニュー「AMM: タスクバー点滅」
項目の両方から呼び出せるようにした。

| 項目 | 結果 | 備考 |
|---|---|---|
| `FlashWindowEx`のFFI呼び出し自体 | ✅ | ログに`FlashWindowEx invoked, previous flash state: BOOL(...)`が出力され、パニック・エラーなくOSから有効なBOOL値が返ることを確認 |
| 点滅の視覚的確認(タスクバーアイコンの明滅) | △ 未確認 | この実行環境ではタスクバーが自動非表示(または存在しない)設定になっており、デスクトップ全体のスクリーンショットでタスクバー領域自体を捕捉できなかった。`FlashWindowEx`はウィンドウがフォアグラウンドの間は視覚効果が抑制される仕様のため、`notepad.exe`を起動してフォーカスを外した状態で発火させたが、タスクバー欄が写らずアイコンの明滅は目視確認できず。API呼び出しの成功(戻り値取得)までを実機確認の範囲とする |

**結論**: `FlashWindowEx`のRust側からの呼び出し自体はエラーなく成功することを実機で確認した。
点滅そのものの見た目は、`FLASHW_TIMERNOFG`が「ウィンドウがフォアグラウンドを得るまで点滅し続ける」
という仕様どおりに動作しているかを本番のWindowsデスクトップ環境(タスクバー常時表示)で目視確認することを
推奨する(このセッションの実行環境固有の制約であり、API自体の実現性には疑問はない)。

### 再現方法

```bash
cd reference/poc-tauri-terminal
cargo tauri build --debug
./src-tauri/target/debug/app.exe
# 画面下部の「タスクバー点滅(FlashWindowEx)」ボタンをクリック、
# またはシステムメニュー[AMM ▶]→「AMM: タスクバー点滅(テスト)」を選択
# ウィンドウがフォーカスを失っている状態(他のウィンドウをアクティブにしてから)で実行すると
# タスクバーアイコンが明滅するはず(本番デスクトップ環境での目視推奨)
```

## Windows実機: 非フォーカス奪取の常時最前面ポップアップ(承認ハブ相当)(2026-07-13)

**検証環境**: 同上(`app.exe`に追加実装)。`show_approval_popup` Tauriコマンドで
`tauri::WebviewWindowBuilder`を使い、`.always_on_top(true)` + `.skip_taskbar(true)` + `.focused(false)` +
`.decorations(false)`の新規ウィンドウ(`approval-popup.html`)を生成する。

**検証手順**: `notepad.exe`を起動してフォアグラウンドにした状態(`GetForegroundWindow`で確認)で、
メインウィンドウのボタンからポップアップを表示させ、Win32 API(`GetForegroundWindow`/`GetWindowLong`)で
直接ウィンドウの状態を検査した。

| 項目 | 結果 | 備考 |
|---|---|---|
| `always_on_top(true)` → `WS_EX_TOPMOST` | ✅ | `GetWindowLong(GWL_EXSTYLE)`で`WS_EX_TOPMOST`ビットが立っていることを確認 |
| `focused(false)` → フォーカスを奪わない | ✅ | ポップアップ表示前後で`GetForegroundWindow()`が一貫して`notepad.exe`のハンドルのままであることを確認(ポップアップにフォーカスは一切移らなかった) |
| `skip_taskbar(true)` → タスクバー非表示 | △ 未確認(実装は正しい) | tao(Tauriの下層)は`WS_EX_TOOLWINDOW`ではなく`ITaskbarList::DeleteTab` COM APIでタスクバーから除外する実装だった(実装コード確認済み、`reference`元:`tao-0.35.3/src/platform_impl/windows/window.rs`)。このセッションの環境はタスクバーが常時表示されない構成のため、視覚的な除外確認はできなかった(前述のFlashWindowExと同じ環境制約) |

**結論**: `always_on_top`と`focused(false)`(非アクティブ化)は実機で明確に動作を確認できた。
現行WinForms版の承認ハブが持つ「作業を妨げない常時最前面通知」という要件は、Tauriの
`WebviewWindowBuilder`だけで(追加のWin32呼び出し無しに)満たせることが分かった。`skip_taskbar`は
コード上正しいAPIを使っており動作しない理由は見当たらないが、視覚確認は本番デスクトップ環境で
別途行うことを推奨する。

### 再現方法

```bash
cd reference/poc-tauri-terminal
cargo tauri build --debug
./src-tauri/target/debug/app.exe
# 別のウィンドウ(例: メモ帳)をアクティブにした状態で
# 「承認ハブ表示(常時最前面/非アクティブ化)」ボタンをクリック
# → ポップアップが最前面に表示されるが、メモ帳のフォーカスは奪われない
```

## Windows実機: IME二重送信ガード問題の解消確認(2026-07-13)

**背景**: 現行WinForms版は「共通入力欄」がネイティブ`TextBox`、ターミナル表示が`WebView2`という
**2つの別々のIME合成コンテキストにまたがる構成**であり、これが二重送信バグの根本原因(`HANDOVER.md`記載)。
Tauri/Flutter移植では入力欄からターミナル表示まで単一のWebView/単一DOMに統一されるため、この種のバグは
構造的に起きなくなるという仮説を実機で検証した。

**検証方法**: `conpty-native.html`に(現行WinForms版の「共通入力欄」に相当する)`<input id="prompt-input">`を
追加し、xterm.jsターミナル(`<textarea>`が内部的なIME入力面)と同一DOM内に共存させた。Chrome DevTools
Protocolの`Input.imeSetComposition`(実際のIME変換候補表示中の状態を模擬する専用API。`keyboard.insertText()`
より本物のIME合成シーケンスに近い)を使い、(1)共通入力欄、(2)ターミナル本体、それぞれで
「にほんご」→変換→「日本語」で確定する一連の合成イベントを発火させ、`compositionstart`/
`compositionupdate`/`compositionend`の発火回数・発火対象要素をページ全体でキャプチャして記録した。

| 項目 | 結果 | 備考 |
|---|---|---|
| 共通入力欄(`#prompt-input`)でのIME合成 | ✅ 1回のみ、対象要素も正しい | `compositionstart`→`compositionupdate`(候補)→`compositionupdate`(空)→`compositionend`が`INPUT#prompt-input`にのみ発火。ターミナル側(`TEXTAREA`)への漏れ・二重発火は皆無 |
| ターミナル本体でのIME合成 | ✅ 1回のみ、対象要素も正しい | 同様のイベント列が`TEXTAREA`にのみ発火。共通入力欄側への漏れ・二重発火は皆無 |
| 確定後の実際の送信内容 | ✅ 文字化け・重複なし | `shots-windows-headed/ime-03-terminal-composed.png`: `echo 日本語`の実行結果が`日本語`と正しく1回だけ出力された。共通入力欄側の確定値(`日本語`)も過不足なく1回だけpty(cmd.exe)へ送信され、存在しないコマンドとして正しく1回エラーになった(二重送信であれば「日本語日本語」等になるはずだが、そうならなかった) |

**結論**: 単一WebView/単一DOM構成では、入力欄とターミナル表示それぞれのIME合成コンテキストが
DOM要素単位で明確に分離され、ブラウザの標準的なcomposition eventの仕組みにより自然に排他制御される
ため、WinForms版で起きていたような「2つの合成系にまたがることによる二重送信」は**構造的に再現し得ない**
ことを実機のIME合成イベントレベルで確認した。

### 再現方法

```bash
cd reference/poc-tauri-terminal
cargo tauri build --debug --no-bundle   # public/conpty-native.html に #prompt-input を追加済み
./src-tauri/target/debug/app.exe
# WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS=--remote-debugging-port=9333 を設定して起動し、
# node probe-ime.js で CDP の Input.imeSetComposition を使った合成イベントの発火状況を確認できる
# (画面下部の #ime-log にリアルタイムのイベントログが表示される)
```

## Windows実機: Copilot CLI bracketed paste submit問題の再現確認(2026-07-13)

**背景**: `docs/manual/user-guide/usage.md`記載の既知の制限事項。現行WinForms版は
`useBracketedPaste: true`でCopilot CLI(Ink TUIベース)へ「bracketed pasteシーケンス+確定`\r`×2段」を
送信しているが、Copilot CLIの入力欄にテキストは届くもののEnterがsubmitと認識されず、プロンプトが
入力欄に蓄積したまま実行されない。回避策は「送信後にCopilot CLI側を直接フォーカスし手動でEnterを押す」。
このセッションで実際にGitHub Copilot CLI(v1.0.70、npm経由でインストール・ユーザーが認証済み)を
Tauri/ConPTY統合PoC(`conpty-native.html`)上で起動し、同じバイト列を送って新アーキテクチャでも
同様に発生するかを検証した。

**検証手順**: `pty_write`コマンドで、WinForms版と全く同じバイト列(`\x1b[200~` + テキスト + `\x1b[201~` + `\r\r`)を
アイドル状態(何も実行中でない、入力欄が空)のCopilot CLIセッションへ直接送信し、送信直後の画面を確認した。

| 項目 | 結果 | 備考 |
|---|---|---|
| bracketed paste + `\r\r`直後の状態 | ❌ **同様に再現**、submitされない | `shots-windows-headed/copilot-12-immediately-after-paste.png`: テキスト「this is a bracketed paste submit test」が入力欄に届いているが、送信済みプロンプトの一覧には現れず、画面下部のヒントも「/ commands · ? help」から「@ files · # issues」(入力欄に文字がある状態のヒント)に変わっただけで「Working」状態にはならなかった。**WinForms版と全く同じ症状** |
| 回避策(手動でCopilot CLI側にフォーカスしてEnter) | ✅ 有効 | `shots-windows-headed/copilot-13-after-manual-enter-workaround.png`: 素のキー入力によるEnterで即座にプロンプトが送信され「Working」状態に遷移した |

**結論**: bracketed paste submit問題は、ConPTY/node-ptyが送るバイト列は同一であり、**受信側(Copilot CLIの
Ink TUI実装)がpaste直後の`\r`をどう解釈するかに起因する問題**であるため、ホスト側のアーキテクチャを
WinForms→Tauriに変えても**全く同様に再現する**ことを実機で確認した。既存ドキュメントの推測
(「受信側の挙動が変わらない限り根本解消は困難」)を裏付ける結果であり、Tauri/Flutter移植によって
自動的に解消される問題ではないことが分かった。移植後も同じワークアラウンド(手動Enter、または
bracketed pasteを使わない代替の送信方式の検討)が必要になる。

### 再現方法

```bash
cd reference/poc-tauri-terminal
npm install -g @github/copilot   # 要GitHub認証(初回 `copilot` 起動時にデバイスコードログイン)
cargo tauri build --debug --no-bundle
./src-tauri/target/debug/app.exe
# ターミナルで `copilot` を起動、folder trustプロンプトでEnter(Yesを選択)
# WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS=--remote-debugging-port=9333 を設定して起動していれば、
# node probe-copilot-clean-test.js で bracketed paste + \r\r 送信直後の状態を確認できる
# node probe-copilot-workaround.js で手動Enterによる回避策の有効性を確認できる
```

## 実機(Windows)検証: Edge(Chromium)+ ConPTYでの再検証(2026-07-13)

**検証環境**: Windows 10 Pro, `node` v24.18.0、ブラウザは`msedge`チャンネル(Playwright経由、headed/headless両方で再現性確認)。
`node_modules/node-pty/prebuilds/win32-x64/`にConPTYベースのプリビルドバイナリが同梱されており、追加ビルド不要で
`require('node-pty')`が通った(`npm install`時に`allow-scripts`でnode-ptyのinstallスクリプトがブロックされたが、
プリビルド同梱のため実害なし)。`server.js`は`process.env.SHELL || 'bash'`のフォールバックによりGit Bash(`bash.exe`)を
起動する構成のまま、Windows実機で動作した。

| 項目 | 結果 | 備考 |
|---|---|---|
| ANSIカラー/太字/斜体/下線 | ✅ | `shots-windows-headed/01a-colors-unicode-emphasis.png` |
| 日本語テキスト | ✅ | 同上 |
| 罫線(box-drawing、実際に箱を組んだ状態) | ✅ 隙間・欠けなく連続描画 | `shots-windows-headed/boxdraw-grid.png`。macOSと同じく単一行に並べただけの文字列は繋がって見えないが描画バグではない(既知の注記どおり) |
| リサイズ(fit-addon → ConPTY resize) | ✅ | `shots-windows-headed/02-resized-retry.png`: 1000→700pxで`tput cols`が100→84に追従、実シェル側(Git Bash)も認識 |
| スクロールバック+ホイールスクロール | ✅ | `shots-windows/01b-after-scrollback.png` / `01c-scrolled-up.png` |
| WebSocket経由の双方向PTY入出力(ConPTY) | ✅ | Git Bash上の実shellを起動し、入力送信・出力受信とも問題なし |
| **絵文字(🎉🚀🔥😀👍)** | ❌ **xterm.jsのcanvas描画では表示されない** | 詳細は下記「新たに判明した罠」参照 |

### 新たに判明した罠: Windows実機でxterm.jsが絵文字を描画できない(原因は ConPTY、xterm.js/Chromium側ではない)

macOS(WKWebView)ではApple純正フルカラー絵文字がフォールバック無しで描画されたが、Windows実機(Edge/Chromium)では
**xterm.jsのターミナル領域内で絵文字が全く描画されない(空白になる)**ことを確認した。

- 同じページ内で素のHTML(`<body>...🎉🚀🔥😀👍</body>`)をレンダリングすると**Segoe UI Emojiでフルカラー描画される**
  ため、OSの絵文字フォント自体・Chromiumのフォントフォールバック自体は正常([`shots-windows-headed/emoji-plain-html.png`](.))。
- `Terminal`の`fontFamily`に明示的に`"Segoe UI Emoji"`を追加してみても解消せず、むしろ空白ではなく「グリフ無し」の
  tofu(□に?)が表示されるようになった([`shots-windows-headed/emoji-xterm-with-explicit-fallback-font.png`](.))。
  単純なfontFamily追加では直らない。
- headless/headedいずれのEdgeでも再現するため、ヘッドレス特有の問題ではない。

**根本原因(2026-07-13、外部調査で特定)**: 当初「xterm.jsのcanvas `fillText`がフォントフォールバックを
していない」という仮説を立てたが、これは誤りだった。実際の原因は**Windows固有の疑似端末API「ConPTY」**に
起因することが、xterm.jsのメンテナ本人の発言と関連issueから判明した。

- xterm.js Issue [#2693](https://github.com/xtermjs/xterm.js/issues/2693)(2020年〜未解決)でメンテナ
  `jerch`氏: 「問題はconptyが使う画面リフレッシュのパターン周りにある。絵文字自体は届いているが、
  リフレッシュのオフセット計算がずれてマルチバイト文字の一部が欠落する」
- 2024年12月時点でも同issueに同様の実例が報告されている(Pythonの`print("🪟X")`で絵文字の右半分にXが
  重なって表示。**Windows Terminalでは正常だがVSCode統合ターミナル(xterm.js使用)では壊れる**)
- 関連するmicrosoft/terminal Issue [#2066](https://github.com/microsoft/terminal/issues/2066)・
  [#4345](https://github.com/microsoft/terminal/issues/4345)も、ConPTY/Windows Terminal側が
  絵文字等の「曖昧幅文字」の表示幅(1セルか2セルか)をどう決定するかというWindows端末エコシステム共通の
  長年の課題を示している

つまり、**xterm.jsのcanvas描画自体は正常で、フォント設定をどういじっても直らない**。原因はConPTYが
生成するVT出力ストリームの文字幅計算(またはxterm.js側でのその解釈)がずれ、絵文字の直後の文字データが
カーソル位置のズレによって欠落・上書きされるという、Windows側の疑似端末レイヤーに起因する問題であるため。

**影響**: AIエージェント(Claude Code/Copilot CLI/Codex CLI等)のCLI出力には絵文字が含まれることがあり
(例: ステータス絵文字、装飾)、現行WinForms版(WebView2 + xterm.js、node-pty経由でやはりConPTYを使う)でも
同様の問題が起きている可能性が高い(現行版での実際の見え方は別途確認要)。

**追記(2026-07-13、`reference/poc-flutter-terminal`側での追加調査)**: この問題はTauri/WebView2固有ではなく、
**Flutter(xterm.dart、portable-pty経由でやはりConPTYを使う)でも全く同じ症状(絵文字部分が空白)が再現する**
ことを確認した。両者とも「Windows上でConPTYを介して絵文字を含む出力を受け取る」という点が共通しており、
上記のConPTY起因説と整合する。同じFlutterウィンドウ内の素の`Text`ウィジェット(ConPTYを経由しない直書き
文字列)は絵文字を正しく描画できたが、これは「ConPTYを経由しないので当然影響を受けない」という結果であり、
xterm.dart側のフォント処理の優劣を示すものではなかった(対照実験の解釈を訂正)。詳細は
`reference/poc-flutter-terminal/RESULTS.md`の「絵文字描画の追加調査」参照。

**この問題への対応方針**: 根本原因はamm自身のコードではなくConPTY(またはConPTYが出すVT出力をxterm.js/
xterm.dartがどう解釈するか)という、Windows/xterm.jsエコシステム側の長年未解決(2018年頃〜)の問題であり、
**amm側で完全に修正することはできない**。緩和策(CLI出力の絵文字をテキスト置換するフィルタ、文字幅判定の
調整等)も検討したが、**2026-07-13、対応不要と決定**(既知の制限として許容する。現行WinForms版でも
起きている可能性が高い問題であり、移植によって悪化するものではないため)。

### 再現方法

```bash
cd reference/poc-tauri-terminal
npm install                             # node-ptyはプリビルド同梱、追加ビルド不要
node server.js &                        # http://localhost:5173
node probe-windows.js                   # shots-windows/ に出力(headless)
node probe-boxdraw-windows.js           # 罫線グリッド検証(headed)
node probe-resize-windows.js            # リサイズ+ConPTY追従検証(headed)
node probe-emoji-plain.js               # 絵文字フォント疎通確認(素のHTML、対照実験)
node probe-emoji-xterm-fallback.js      # fontFamilyへの明示的なSegoe UI Emoji追加を試した記録(効果なし)
```

## MDI代替: 単一ウィンドウ内ペイン管理のプロトタイプ検証(2026-07-13, Windows実機)

**目的**: 現行WinForms版のMDI(複数の子ウィンドウ)を、Tauri/Flutter移植では単一ウィンドウ内の
自前ペイン実装(タイル/カスケード/ドラッグ移動/リサイズ/配置の記憶復元)で置き換えられるかを検証する。

`public/mdi-prototype.html`として、複数の独立したxterm.js+PTY接続ペインを持つ簡易プロトタイプを実装
(既存の`server.js`をそのまま流用。1 WebSocket接続 = 1 `pty.spawn()`のため複数ペインが自然に独立動作する)。

| 項目 | 結果 | 備考 |
|---|---|---|
| 複数ペインが独立したPTYに接続 | ✅ | ペインごとに新規`pty.spawn()`、入出力が混線しない |
| タイトルバードラッグでの移動 | ✅ | `shots-windows-headed/mdi-02-after-drag.png` |
| リサイズハンドルでの拡縮(xterm fit-addon連動) | ✅ | ドラッグに応じ`fitAddon.fit()`→PTY resizeも追従 |
| タイル配置ボタン | ✅ | `shots-windows-headed/mdi-03-tiled.png`: グリッド状に均等再配置 |
| カスケード配置ボタン | ✅ | `shots-windows-headed/mdi-04-cascaded.png`: 階段状にオフセット再配置 |
| 配置の記憶・復元(localStorage、ページリロード後) | ✅ | `shots-windows-headed/mdi-06-after-reload.png`: 位置・サイズ・重なり順(z-index)ともリロード前と一致 |
| リロード後もペインが操作可能(新規PTYで再接続) | ✅ | `shots-windows-headed/mdi-07-pty-alive-check.png`: 入力→エコー確認。ただし**PTYセッション自体はページ再読み込みのたびに作り直される**(WinFormsのタブ切替のようなセッション継続ではなく、ブラウザリロード=新規接続のため。実装がタブ内SPAである限りリロードは基本発生しない想定だが、Tauri/Flutter側でのウィンドウ再作成時の扱いは移植時に要設計) |

### 気づき・注意点

- **重なったペインは露出部分でしかクリックできない**: カスケード配置でペイン同士がわずかにしか
  ズレていない場合、下側(低z-index)のペインの本体領域はほぼ全て上のペインに隠れてクリック不能になる。
  実際のウィンドウマネージャと同様の挙動であり不具合ではないが、「クリックで最前面に持ってくる」操作を
  タイトルバーだけでなくペイン全体(またはクリックされた地点が見えている限り)に対応させるなど、
  実装時のUX設計として意識する必要がある(このプロトタイプはタイトルバーのみ対応)。
- 配置の保存キーはプロトタイプでは`id`をそのままラベル表示に使う設計にしたが、複数ペイン作成の
  カウンタ(`nextId`)と保存済みIDの整合を取らないとラベルがずれるバグを実装中に発見・修正した
  (`createPane`は必ず`rect?.id ?? nextId++`で生成すべきところ、当初`nextId++`のみだったため復元後に
  ラベルが1つずつずれていた)。実装移植時も「保存した識別子をそのまま複合キーとして使う」設計に注意。
- タイル/カスケードいずれも、ペイン数に応じた再計算(`Math.ceil(Math.sqrt(n))`によるグリッド分割)だけの
  単純な実装で十分成立した。WinForms版のMDIが提供する複雑なウィンドウ管理APIに依存せず、
  HTML/CSS(`position: absolute` + `z-index`)とJSだけで同等の体験を再現できることを確認した。

### 再現方法

```bash
cd reference/poc-tauri-terminal
node server.js &                        # http://localhost:5173
node probe-mdi-prototype.js             # shots-windows-headed/mdi-*.png に出力
# または手動: http://localhost:5173/mdi-prototype.html をブラウザで開く
```
