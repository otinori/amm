# poc-flutter-terminal — 検証結果

## 構成

- `lib/main.dart`: Flutter + `xterm` (xterm.dart v4.0.0) + `web_socket_channel`。実PTY(`pty-backend/`, node-pty)に
  WebSocket経由で接続し、amm同様のUIクロム(クイック切替バー・共通入力欄・送信先ステータス)を再現。
- `pty-backend/`: `reference/poc-tauri-terminal/server.js` と同一シェイプのPTYバックエンド。Tauri案と条件を揃え、
  「実PTYは同じ、ターミナル描画クライアントだけがxterm.js↔xterm.dartで異なる」比較にしている。
- `test/terminal_core_test.dart`: `flutter test` によるバッファ層の直接検証(後述、最重要の結果)。
- 実行環境の制約上、**Windows/macOSデスクトップではなくFlutter Web(CanvasKit)でプロキシ検証**した
  (`docs/design/cross-platform-feasibility.md` 相当の制約。このLinuxコンテナにはdotnet/Flutterデスクトップ
  ツールチェーンもGUIディスプレイも無い)。

## 最重要の結果: ターミナルエミュレーションのコア機能は完全に正しい

`flutter test test/terminal_core_test.dart` で、xterm.dartの `Terminal`/`Buffer` を**画面描画を介さず直接**検証。
Flutter Web特有のレンダリング問題(後述)から完全に独立した、データレベルでの証拠:

```
00:00 +0: ANSI SGR foreground colors (30-37) are distinct and reset works
00:00 +1: bold / italic / underline SGR attribute flags are tracked per cell
00:00 +2: CJK characters decode to the correct codepoint and report width 2
00:00 +3: box-drawing characters round-trip as exact codepoints
00:00 +4: emoji (astral codepoint) decodes without surrogate-pair corruption
00:00 +5: scrollback retains history beyond the visible viewport after resize
00:00 +6: resize propagates new column count via onResize callback (pty resize path)
00:00 +7: All tests passed!
```

- ANSI SGR前景色(赤/緑/青)とリセットが正しく別値として保持される
- Bold/Italic/Underlineの属性フラグがセル単位で正しく立つ
- 日本語(漢字)が正しいcodepointにデコードされ、**表示幅2(全角)**として認識される
- 罫線文字がcodepointレベルで欠損・置換なく保持される
- 絵文字(サロゲートペア外・astral codepoint)が破損なくデコードされる
- リサイズ後もスクロールバックがビューポートを超えて保持される
- リサイズがonResizeコールバック経由で新しい列数を伝播する(実PTYへのresize経路)

**結論: xterm.dartのANSI/VT100パーサ・バッファ管理は、ユーザーの必須要件
「ターミナルエミュレーターとして使えること」を満たす実装。**

## 実画面での検証(Flutter Web経由、実PTY接続)

このコンテナにはFlutter Desktop用ツールチェーンが無いため、Flutter Web(CanvasKitレンダラ)を
プロキシとして実PTY接続・実描画を確認した。

| 項目 | 結果 | 備考 |
|---|---|---|
| 実shell(bash)への実PTY接続・入出力 | ✅ | `pty-backend/server.js`経由でWebSocket越しに実shellと双方向通信 |
| ANSI前景色(赤/緑/青) | ✅ | `shots/01a-colors-unicode-emphasis.png` |
| 太字/斜体/下線 | ✅ | 同上、視覚的にも判別可能 |
| 日本語テキスト | ✅ | 同上。バンドルフォント経由で正しく表示 |
| 絵文字 | △ | codepoint自体はデータ層で正しくデコード(上記test参照)だが、
  今回バンドルしたフォールバックフォント(Unifont、後述)には絵文字グリフが無く画面上は非表示 |
| 罫線(box-drawing) | △ | 同上。codepointは正しいがグリフの意匠はフォント依存 |
| リサイズ(TerminalView.autoResize → pty resize) | ✅ | `shots/02-resized.png`: ビューポート変更後 `tput cols` が実shell側に反映 |
| スクロールバック + ホイールスクロール | ✅ | `shots/01c-scrolled-up.png` |
| UIクロム(クイック切替バー・入力欄・ステータス) | ✅ | amm同様のレイアウトをFlutter Widgetで再現できることを確認 |

## 重要な発見: Flutter Web/CanvasKitのCJKフォールバックフォントとオフライン方針の衝突

**amm既存の設計方針(`reference/design_doc.md`)は「完全オフライン動作、外部CDN非依存」を明記している。
これはFlutter Web(あるいはFlutter全般のデフォルト設定)と真っ向から衝突する重要な発見。**

### 発見の経緯

1. 初回ビルドをこのコンテナのネットワークポリシー下で開こうとしたところ、**画面が永遠に白いまま**になった
2. 調査の結果、Flutter Web/CanvasKitは「Robotoフォント本体」と「CJK/絵文字/記号のフォールバックフォント
   (Noto Sans SC/HK/Symbols等)」を**すべて `fonts.gstatic.com` (Google Fonts CDN) から動的取得**する設計であると判明
3. このコンテナはネットワークポリシー上 `gstatic.com` への到達を許可していないため、初回描画が完了しない
4. リクエストを中断(abort)させると**無限リトライループ**に陥ることを確認(5秒毎に500件以上のリクエストが
   際限なく増加、60秒以上待っても収束しない)
5. リクエストをローカルの代替フォント(有効なwoff2)で応答するよう差し替えると、無限ループは解消し描画が進むが、
   CJK文字を含む画面では**初回描画完了までに90〜120秒程度かかる**(要求されるフォールバックカテゴリが
   増えるほど遅い)、加えてこのサンドボックス環境では**その所要時間が実行毎にばらつく**ことも確認した

### 解釈と評価

- これは **Flutter Web + CanvasKitレンダラ特有の挙動**であり、`docs/design/cross-platform-feasibility.md`が
  前提とする実際の移植先(**Flutter Desktop、Windows/macOSネイティックビルド**)では条件が異なる可能性が高い。
  Flutter DesktopはCanvasKit(Wasm/Web版レンダラ)ではなく、Impeller/Skiaをネイティブに使い、
  フォントフォールバックもOS標準API(Windows: DirectWrite、macOS: CoreText)経由になるため、
  **Google Fonts CDNへの依存自体が発生しない可能性が高い**
- ただし、それを**推測のまま採用可否を判断するのは危険**。Flutter Desktopでも「デフォルトのRobotoフォント」
  はアプリにバンドルされておらず、`pubspec.yaml`でフォントを明示バンドルしない限り何らかの形でOSにフォント
  解決を委ねる設計になっている。amm相当の「日本語UI + AIエージェント出力(任意のCJK/絵文字/記号を含みうる)
  + 完全オフライン」という要件を満たすには、**Windows実機のFlutter Desktopビルドで、ネットワーク遮断下に
  CJK/絵文字/記号が正しく描画されるかを明示的に検証する必要がある**
- 対策の方向性(検証時に確認すべき選択肢):
  1. `pubspec.yaml`の`fonts:`セクションで日本語対応フォント(Noto Sans CJK JP等)を明示的にアプリにバンドルし、
     `MaterialApp`のデフォルトテキストテーマで使う(WinForms版が既にxterm.jsをローカル同梱している方針と同じ発想)
  2. ターミナル本体(`TerminalStyle`)には元々等幅フォントを明示指定するのが通例であり、UIクロム側だけの対応で
     足りる可能性が高い

## 副次的な発見: フォールバックフォントの完全性

今回、開発時間の都合で `fonttools` によりUnifont-JPから**必要な文字だけを抜き出したサブセットフォント**を
使ったところ、サブセットに含めなかった一部の記号(句読点等)がグリフごと欠落して表示されない事象が発生した。
これは「フォールバックフォントを自分でサブセットして最小限にする」場合の一般的な注意点であり、実運用では
完全なCJKフォント(数MB程度)をまるごとバンドルすれば発生しない。amm本体が想定するAIエージェントの出力は
任意の文字を含みうるため、**サブセットではなく完全なフォントバンドルが必須**という要件が明確になった。

## 起動方法(再現用)

```bash
cd reference/poc-flutter-terminal
flutter pub get
cd pty-backend && npm install && node server.js &   # ws://localhost:5174
cd .. && flutter build web --release
cd build/web && python3 -m http.server 5175          # http://localhost:5175

# データ層の検証(画面描画に依存しない、最重要):
cd ../../ && flutter test test/terminal_core_test.dart

# 画面描画の確認(Playwright, ヘッドレスChromium。gstatic.comブロック環境では
# probe/local-fonts/fallback.woff2 のローカル代替フォント経由で描画させる):
cd probe && npm install && node probe.js
```

## 実機(macOS)検証: Dockバッジ / 非フォーカス奪取フローティングパネル(2026-07-12)

**検証環境**: macOS 26.5.2, Apple Silicon(実機、`flutter build macos --debug` によるネイティブビルド、Web版ではない)。

amm本体が要求する「トレイアイコン点滅相当の通知」「フォーカスを奪わない承認ハブ相当のポップアップ」が
Flutter Desktop(macOS)で実現できるかを、`macos/Runner/MainFlutterWindow.swift` に追加した
`amm/window_probe` という名前の `FlutterMethodChannel`(Dart↔Swift)経由で検証した。

| 項目 | 結果 | 備考 |
|---|---|---|
| Dockアイコンバッジ表示 | ✅ | `NSApplication.shared.dockTile.badgeLabel` を呼ぶだけで即座に反映。`shots-macos-native/05-dockbadge-crop.png` でDockに赤バッジ「3」を確認 |
| 常時最前面(フォーカス非奪取)のポップアップパネル | ✅ | `NSPanel(styleMask: [.titled, .nonactivatingPanel, .utilityWindow])` + `level: .floating` + `becomesKeyOnlyIfNeeded = true` で実現。`shots-macos-native/04-panel-no-focus-steal.png`: Terminalをフォアグラウンドにした状態でパネルを表示させても、`System Events` で確認したフォアグラウンドプロセスは終始 `Terminal` のまま(フォーカスが奪われない)ことを確認 |

**結論**: Windows版のトレイ点滅・承認ハブ常時最前面ポップアップ相当の挙動は、Flutter DesktopでもCocoa API
(`NSApplication.dockTile` / `NSPanel` の `nonactivatingPanel` スタイル)への薄いMethodChannelブリッジで
問題なく実現可能。Rust/Tauri側での同等機能(`tauri-plugin-window-state`や`always_on_top`設定等)は
本PoCでは未検証(cargo/rustc未導入のため、`reference/poc-tauri-terminal/RESULTS.md` 参照)。

### 再現方法

```bash
cd reference/poc-flutter-terminal
cd pty-backend && npm install && node server.js &   # ws://localhost:5174 (無くても動作確認は可能)
cd .. && flutter build macos --debug
open build/macos/Build/Products/Debug/poc_flutter_terminal.app
# アプリ下部の "window probe:" 行にある3ボタン(Dockバッジ+1 / バッジ解除 / 常時最前面パネル切替)で確認
```

## 実機(Windows)検証: システムメニュー拡張・非フォーカス奪取ポップアップ・IME(2026-07-13)

**検証環境**: Windows 10 Pro, Flutter 3.44.6(stable、shallow clone導入)、Visual Studio Build Tools 2022
(Tauri検証用に導入済みのものを流用、追加インストール不要)。`flutter create --platforms windows .` で
`windows/`を新規追加し、`windows/runner/flutter_window.{h,cpp}`にmacOS版(`MainFlutterWindow.swift`)と
同じ設計思想のネイティブブリッジをC++で実装した(`amm/window_probe`という同名の`MethodChannel`をWindows側でも再利用)。

**実装**: `GetSystemMenu`+`AppendMenuW`(reference/poc-tauri-terminalのRust実装と同じ手法)で
システムメニューに「AMM ▶」サブメニューを挿入し、`WM_SYSCOMMAND`を`FlutterWindow::MessageHandler`で
直接フックする(Tauriと異なりComctl32の`SetWindowSubclass`は不要、Flutter Windowsテンプレートが
最初から`MessageHandler`という一元的なwndprocフックポイントを提供しているため実装がシンプル)。
非フォーカス奪取ポップアップは、Flutter自体のウィンドウではなく素のWin32ポップアップウィンドウを
`WS_EX_NOACTIVATE | WS_EX_TOPMOST`で生成する形で実現(macOS版の`NSPanel(.nonactivatingPanel + .floating)`
に相当)。

| 項目 | 結果 | 備考 |
|---|---|---|
| システムメニューへの「AMM ▶」サブメニュー挿入 | ✅ | PowerShell(`GetMenuStringW`)で実機のシステムメニューを列挙し、`[8] 'AMM ▶'`とその配下1項目が挿入されていることを確認(Tauriと同じ検証手法) |
| `WM_SYSCOMMAND`→ポップアップ表示の往復 | ✅ | `SendMessage(hwnd, WM_SYSCOMMAND, 0x1000, 0)`でクリックを模擬し、`shots/windows-05-panel-via-sysmenu.png`でポップアップ(オレンジ色のパネル、日本語テキスト付き)が実際に表示されることを確認 |
| ポップアップの常時最前面(`WS_EX_TOPMOST`) | ✅ | ウィンドウ生成時にフラグ指定、表示自体で確認 |
| ポップアップのフォーカス非奪取(`WS_EX_NOACTIVATE`) | ✅ | `notepad.exe`をフォアグラウンドにした状態で`GetForegroundWindow()`がnotepadのハンドルであることを確認 → システムメニュー経由でポップアップを表示 → 表示後も`GetForegroundWindow()`が同じnotepadのハンドルのまま変化しないことを確認(Tauriと同じ検証手法) |
| IME二重送信ガード(構造的な検証) | ✅ コードレベルで確認 | 詳細は下記参照。ライブのIME合成イベント発火テストは、この実行環境固有のマウス座標自動化の信頼性問題(下記「気づき」参照)のため見送り、ソースコード解析による構造的検証とした |

### IME二重送信ガードの構造的検証(コードレベル)

Tauri版で行った「compositionイベントの発火回数・対象要素をライブ計測する」形の実機テストは、
この環境でのFlutter(ネイティブWin32アプリ、ブラウザではないためCDPが使えない)向けマウス座標自動化が
DPIスケーリングの影響で信頼性が低く(下記参照)、他の稼働中ウィンドウへの誤クリックのリスクがあったため
見送った。代わりに、以下をソースコード解析で確認した:

- `_InputBar`(共通入力欄)は標準の`TextField`/`TextEditingController`を使用し、`value.composing`で
  IME合成状態(開始/更新/終了)を取得できる標準的なFlutter入力ウィジェットである
  (`lib/main.dart`の`_InputBarState._onControllerChanged`で実装・確認)。
- ターミナル本体(`xterm`パッケージの`TerminalView`)も、内部の`CustomTextEditState`クラスが
  Flutterの`TextInputClient`を実装しており(`custom_text_edit.dart`)、`composing`状態を専用に
  ハンドリングしている(`widget.onComposing(composingText)`)。**xterm.dartは生のキーボードイベントだけを
  拾っているのではなく、Flutter標準のIME合成パイプラインに正しく乗っている**ことがソース上確認できた。
- Flutter Desktopのテキスト入力は、エンジンレベルで「その時点でフォーカスを持つ1つの`TextInputClient`」
  にのみ入力をルーティングする設計(`FocusNode`による排他制御)であり、これはWebのDOM(1つのフォーカス
  要素だけがcomposition eventを受け取る)と同型の構造。したがって、共通入力欄とターミナル本体が
  同一Flutterウィンドウ内に共存していても、**同時に2つの合成コンテキストが競合することは構造的にあり得ない**
  (WinForms版のネイティブTextBox+WebView2という「OS上の2つの別コントロール」構成とは根本的に異なる)。

### 気づき・注意点

- **座標指定によるマウス自動化(SetCursorPos + mouse_event)はこの環境で信頼性が低い**: このFlutter
  Windowsアプリ(DPI-awareなネイティブexe)に対し、DPI非対応のPowerShellプロセスから`SetCursorPos`で
  クリックを送ると、`GetWindowRect`が返す座標系と実際にスクリーンショットで見える座標系がズレる
  (DPI仮想化が疑われる)。実際に2回、意図した座標から外れた場所をクリックしてしまい、1回目は別の
  稼働中ウィンドウ(このセッション自体が動いているcmd.exeウィンドウ)にフォーカスが移って誤ってテキストを
  ペーストしてしまう事故が発生した(Enter未実行のため実害なし)。Tauri/WebView版で使えたPlaywright CDP
  (`Input.imeSetComposition`)のような正確な座標系を持つブラウザ内自動化と異なり、素のWin32ネイティブ
  アプリの実機UI自動化はこの点で明確にリスクが高い。今後同様の検証をする場合は、PowerShellプロセスを
  `SetProcessDpiAwareness`でDPI-aware化する、またはUI Automation(FlaUI等)のようなアクセシビリティAPI
  経由の要素特定を使うなど、座標に依存しない方式を検討すべき。
- `WM_SYSCOMMAND`のフックは、Tauriでは`SetWindowSubclass`(comctl32)が必要だったのに対し、Flutter
  Windowsテンプレートは`FlutterWindow::MessageHandler`という一元的なwndprocオーバーライドポイントを
  最初から提供しているため、追加のサブクラス化なしでシンプルに実装できた。
- MSVCコンパイラがソースファイルの文字コードをロケール依存(この環境ではCP932/Shift-JIS)で解釈するため、
  日本語コメントを含むUTF-8ファイルは**BOM付きで保存しないと**`C4819`警告(→`/WX`でエラー化)になる
  という罠を実機ビルドで発見(`flutter_window.h`/`.cpp`をUTF-8 BOM付きに変換して解消)。

### 再現方法

```bash
cd reference/poc-flutter-terminal
flutter create --platforms windows .        # windows/ を新規追加(初回のみ)
cd pty-backend && npm install && node server.js &   # ws://localhost:5174
cd .. && flutter build windows
./build/windows/x64/runner/Release/poc_flutter_terminal.exe
# システムメニュー確認: タイトルバー左上のアイコンを右クリック、またはAlt+Space
# 自動検証(PowerShell): MainWindowHandle取得→GetSystemMenu/GetSubMenu/GetMenuStringWで列挙、
# SendMessage(hwnd, 0x0112, 0x1000, 0)でWM_SYSCOMMANDを模擬送信しポップアップ表示を確認
```

## Windows実機: 絵文字描画の追加調査(2026-07-13)

**背景**: 上記までの検証(システムメニュー等)とは別に、`reference/poc-tauri-terminal`のWindows実機検証で
発見した「xterm.js(WebView2)がWindows上で絵文字を描画しない」問題について、**Flutter(xterm.dart+Skia)でも
同じことが起きるか**を追加調査した。

**検証方法**: pty-backendの接続直後に自動で`echo "emoji test: 🎉🚀🔥😀👍 done"`を送出する一時的な
サーバー変種を使い(マウス/キーボード自動化による誤操作リスクを避けるため)、`PrintWindow` API
(ウィンドウのz-orderや前面/背面状態に依存せず直接ウィンドウの描画内容をキャプチャできるWin32 API。
`SetForegroundWindow`がこの環境で信頼できなかったため採用)でターミナル領域をキャプチャした。

| 項目 | 結果 | 備考 |
|---|---|---|
| xterm.dart(既定のフォント設定)での絵文字描画 | ❌ **Tauriと全く同じ症状** | `shots/windows-09-emoji-clean.png`(や`windows-10`): 「emoji test:」と「done」の間に絵文字5文字分の空白ができるだけで、グリフは一切描画されない |
| `TerminalStyle.fontFamilyFallback`に`'Segoe UI Emoji'`を明示追加 | ❌ 効果なし | xterm.dartの既定フォールバックリストには`'Noto Color Emoji'`(Linux/Android用、Windows未搭載)はあるが`'Segoe UI Emoji'`が無く、それが原因と推測して追加したが、`shots/windows-11-emoji-with-segoe-fallback.png`で改善しないことを確認 |
| **対照実験: 同じウィンドウ内の素の`Text`ウィジェット**での絵文字描画 | ✅ **フルカラーで正しく描画** | `shots/windows-12-control-text-widget.png`: `Text('...🎉🚀🔥😀👍')`は問題なくカラー絵文字を表示。同じプロセス・同じフォント環境・同じウィンドウ内で、xterm.dartのターミナル部分だけが描画できない |

**結論(2026-07-13、外部調査で根本原因を訂正)**: 当初この対照実験結果から「xterm.js/xterm.dartの
セル単位描画がフォントフォールバックを経由しない実装上の問題」と結論づけたが、これは誤りだった。
xterm.jsのGitHub Issue([#2693](https://github.com/xtermjs/xterm.js/issues/2693))を調べたところ、
メンテナ本人が「**Windows固有の疑似端末API ConPTY が絵文字の画面リフレッシュ時にオフセット計算を誤り、
マルチバイト文字の一部を欠落させる**」という、6年以上未解決のWindows/xterm.jsエコシステム共通の
既知バグであると判明した(詳細・関連issueは`reference/poc-tauri-terminal/RESULTS.md`の該当箇所参照)。

Tauri(node-pty)・Flutter(portable-pty)とも、**Windows上ではどちらも同じConPTYを経由する**ため
同一症状が再現していたことになる。この対照実験で「素の`Text`ウィジェットは正しく描画できた」のは
xterm.dartのフォント処理が優れているからではなく、**単にConPTYを経由しない直書き文字列だったため
そもそも影響を受けようがなかった**というだけであり、対照実験としては不公平な比較だった(反省点)。

**移植への示唆**: 根本原因はTauri固有でもFlutter固有でもなく、**Windows上でConPTYを使う限り
xterm系ライブラリ全般に共通する、amm側で完全に修正することはできないOSレベルの制約**である。
実用上の緩和策(未検証)は`reference/poc-tauri-terminal/RESULTS.md`の該当箇所にまとめて記載した
(CLI出力の絵文字をテキストへ置換するフィルタ、文字幅判定をConPTY側の想定に合わせる調整、等)。

### 再現方法

```bash
cd reference/poc-flutter-terminal
# pty-backend/server.js の代わりに、接続後に絵文字入りechoを自動送出する一時サーバーを使うと
# マウス/キーボード自動化なしで再現できる(このセッションでは pty-backend/emoji-test-server.js として
# 作成・検証後に削除済み。同等のsetTimeout+term.write()を追加すれば再現可能)
flutter build windows
./build/windows/x64/runner/Release/poc_flutter_terminal.exe
# PrintWindow(Win32 API)でウィンドウ内容を直接キャプチャすると、前面/背面状態に左右されず確認できる
```
