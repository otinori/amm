# 実機検証チェックリスト(Windows / Mac)— Tauri vs Flutter

**前提**: `reference/poc-tauri-terminal/` `reference/poc-flutter-terminal/` は Linux サンドボックス上での代理検証。
本ドキュメントは、そこで得た知見を踏まえて **実機(Windows/Mac)で何を・どういう手順で確認すべきか** をまとめる。
背景・詳細結果は `reference/terminal-poc-comparison.md` と各PoCの `RESULTS.md` を参照。

## 最優先で確認してほしいこと(1つだけ選ぶならこれ)

**Flutter Desktopが、オフライン下でCJK/絵文字を問題なく・即座に描画できるか。**

- [x] **2026-07-11 実機(macOS 26.5.2, Apple Silicon)で検証済み。結果: 問題なし。**
  `flutter run -d macos --release` でネイティブ(Impeller)ビルドを起動し、`pty-backend/server.js`
  経由で実zshに接続。シェル起動直後に自動出力させた日本語(漢字/ひらがな/カタカナ/全角スペース)・
  絵文字(🎉🚀🔥😀👍)・罫線(┌─┬─┐等)・ANSI色/装飾を**起動から1〜2秒以内に、フォールバック無しの
  正しいグリフで**描画できることを確認した(スクリーンショットで目視確認)。
  `lsof -i -a -p <pid>` でアプリのネットワーク接続を監視した結果、`localhost:5174`(自PTYバックエンド)
  以外への接続は一切発生せず、`fonts.gstatic.com` 等の外部CDNへの通信が無いことを確認した
  (Wi-Fi遮断は本機の他の接続も切れるため代わりにこの方法で検証)。
  → Flutter Web/CanvasKit特有の問題であり、Flutter Desktop(Impeller)では再現しないという仮説が実証された。
  **ただし別の重大な罠を発見(下記「実機検証で判明した既知の罠」参照)**: `flutter create` が生成する
  macOS の `Release.entitlements` には `com.apple.security.network.client` が既定で入っておらず、
  App SandboxがWebSocket等の発信ネットワーク接続を静かにブロックする(エラー無し、画面は永遠にカーソルのみ)。
  `DebugProfile.entitlements`/`Release.entitlements` 両方に追記して解消(本PoCでは修正済み・コミット対象)。
  本番の移植でも `flutter create` 直後に同じ罠を踏むため、macOSターゲットのentitlements設定に
  `network.client` を含めることを移植手順に明記すること。

サンドボックス検証で「Flutter Web(CanvasKit)はデフォルトでCJK/絵文字フォールバックフォントを
Google Fonts CDNから動的取得し、到達不可だと初回描画が数十秒〜無限に停止する」という重大な問題を発見した。
Flutter Desktop(実際の移植先)はネイティブレンダラ(Impeller/Skia)を使うため理論上は再現しないはずだが、
**推測のまま採用判断してはいけない**。これが白黒つく前に他の検証(MDI代替UI・システム統合等)に進むのは
手戻りリスクが大きい。

### 具体的な確認手順

1. `reference/poc-flutter-terminal/lib/main.dart` を流用し、`flutter create --platforms windows` (Mac は `macos`)
   で作ったプロジェクトに配置する。`pubspec.yaml` に `xterm` と `web_socket_channel` を追加(このPoCと同じ)。
2. バックエンドは **新規に書かず** `reference/poc-flutter-terminal/pty-backend/server.js` をそのまま実機で
   `node server.js` 起動(node-pty はWindows ConPTY / macOS pty 双方に対応済みのクロスプラットフォーム実装なので、
   Rust/ConPTY統合やDart FFIを書く前の一次検証として十分機能する)。
3. Flutter Desktopアプリ(Web版ではなくネイティブビルド)を起動し、`ws://localhost:5174` に接続。
4. **ネットワークを切断(Wi-Fi OFF、または hostsファイルで `fonts.gstatic.com` を無効化)した状態**で
   アプリを起動し直し、以下を確認:
   - [ ] 起動から初回描画まで数秒以内か(サンドボックスでは90〜120秒かかった)
   - [ ] 日本語・絵文字・罫線が欠落なく表示されるか
   - [ ] コンソール/ログにフォント取得エラーが出るか、出た場合アプリの動作に支障は無いか
5. 上記で問題があれば、`pubspec.yaml` の `fonts:` セクションでNoto Sans CJK JP等をバンドルして再テスト
   (`reference/poc-flutter-terminal/local-fonts/README.md` に暫定サブセットフォントの作り方の参考あり。
   本番は数MBの完全なCJKフォントをまるごとバンドルする方針にすること)。

## Windows実機で確認してほしいこと(上記に加えて)

- [x] **Tauri版**: `reference/poc-tauri-terminal/` の `server.js`+`public/` はそのままWindows上で
      `node server.js` → ブラウザ(Edge)で `http://localhost:5173` を開いて動作確認できる
      (node-ptyがConPTYを使うため、実質的にWindows上でのxterm.js忠実度を今すぐ確認可能)。
      本物のTauri化(Rust側でのWebView2ホスティング + pty統合)は別途必要だが、
      「WebView + xterm.jsとしての描画・操作性」はこの手順で先取り検証できる。
      — **2026-07-13 実機(Windows 10 Pro)で検証済み**。ANSIカラー/太字/斜体/下線/日本語/罫線(実際に箱を
      組んだ状態で連続描画)/リサイズ(ConPTY追従、1000→700pxで`tput cols`が100→84)/スクロールバック
      いずれも問題なし。ただし**絵文字がxterm.jsのcanvas描画では表示されない**という新たな罠を発見
      (macOSでは無条件表示だったのと対照的)。詳細は`reference/poc-tauri-terminal/RESULTS.md`の
      「実機(Windows)検証」参照。
- [x] ConPTY相当の統合: node-ptyではなく実際にTauri(Rust)からConPTYを叩く場合の統合方法の目処
      (`portable-pty` crate 等の選定) — **2026-07-13 実機(Windows 10 Pro)で検証済み**。
      `portable-pty` crateをRust側に追加し(`#[tauri::command]`3つ、約80行)、Node.js/node-ptyを
      一切使わずに`cargo tauri build`した実バイナリでConPTY(`conhost.exe --headless`)経由の
      `cmd.exe`起動・双方向I/O・日本語表示を確認した。実装コストは低く、移植リスクは小さいと判断できる。
      詳細は`reference/poc-tauri-terminal/RESULTS.md`の「Windows実機: Rust側からのConPTY統合」参照。
- [x] MDI代替のウィンドウ管理(タイル/カスケード/記憶復元)を単一ウィンドウ内の自前ペイン実装で
      再現できるか、簡単なプロトタイプで確認 — **2026-07-13 実機(Windows 10 Pro)で検証済み**。
      `reference/poc-tauri-terminal/public/mdi-prototype.html`に複数ペイン(独立PTY接続)+
      ドラッグ移動/リサイズ/タイル/カスケード/localStorageへの配置記憶・リロード復元を実装し、
      いずれも問題なく動作することを確認した。HTML/CSS(`position:absolute`+`z-index`)+JSのみで
      WinForms MDI相当の体験を代替できる見込み。詳細は
      `reference/poc-tauri-terminal/RESULTS.md`の「MDI代替: 単一ウィンドウ内ペイン管理」参照。
- [x] システムメニュー拡張(「AMM ▶」相当のサブメニュー挿入)— Tauriは生HWND取得+Win32 API呼び出しで
      実現できる想定。Flutterは`window_manager`パッケージ or 自前platform channel/FFIで同等のことが
      できるか確認 — **2026-07-13 実機(Windows 10 Pro)でTauri側を検証済み**。`WebviewWindow::hwnd()`+
      `windows` crateで`GetSystemMenu`/`AppendMenuW`によるサブメニュー挿入、`SetWindowSubclass`による
      `WM_SYSCOMMAND`フック→Tauriイベント→JS側UI更新の往復を実機で確認した。実装中に「システムコマンドIDは
      16の倍数でなければならない」という罠を発見・解消(詳細は`reference/poc-tauri-terminal/RESULTS.md`の
      「システムメニュー拡張」参照)。**Flutter側も2026-07-13検証済み**: `flutter create --platforms windows`で
      追加した`windows/runner/flutter_window.cpp`に同じ`GetSystemMenu`/`AppendMenuW`+`WM_SYSCOMMAND`
      フックをC++で実装し、実機で同様に動作することを確認した(`FlutterWindow::MessageHandler`が
      一元的なwndprocフックポイントを提供するため、Tauriで必要だった`SetWindowSubclass`は不要で
      Tauriより実装がシンプル)。詳細は`reference/poc-flutter-terminal/RESULTS.md`の
      「システムメニュー拡張・非フォーカス奪取ポップアップ・IME」参照。
- [x] タスクバー点滅(`FlashWindow` API)の呼び出し確認 — **2026-07-13 実機(Windows 10 Pro)で一部検証済み**。
      `FlashWindowEx`のRust(`windows` crate)からのFFI呼び出しはエラーなく成功し、有効なBOOL戻り値を
      確認した。ただしこのセッションの実行環境はタスクバーが常時表示されない構成のため、点滅そのものの
      目視確認はできていない(API呼び出しの成否とは別の、実行環境固有の制約)。本番のWindowsデスクトップ
      環境での目視確認を推奨。詳細は`reference/poc-tauri-terminal/RESULTS.md`の「タスクバー点滅」参照。
- [x] 非フォーカス奪取の常時最前面ポップアップ(承認ハブ相当)— `alwaysOnTop`/`skipTaskbar`/`focus:false`
      相当の設定で実現できるか — **2026-07-13 実機(Windows 10 Pro)で検証済み**。
      `WebviewWindowBuilder`の`.always_on_top(true)`+`.focused(false)`だけで実現でき、追加のWin32呼び出しは
      不要。別ウィンドウ(メモ帳)をフォアグラウンドにした状態でポップアップを表示させ、`GetForegroundWindow`が
      変化しないこと(フォーカスを奪わない)と`WS_EX_TOPMOST`が立つこと(常時最前面)を実機で確認した。
      `skip_taskbar`はtaoの実装が`ITaskbarList` COM API経由(`WS_EX_TOOLWINDOW`ではない)であることを
      ソースで確認したが、視覚的なタスクバー除外確認はこのセッションの環境制約(タスクバー非表示構成)で
      できていない。詳細は`reference/poc-tauri-terminal/RESULTS.md`の「非フォーカス奪取の常時最前面
      ポップアップ」参照。**Flutter側も2026-07-13検証済み**: 素のWin32ポップアップ
      (`WS_EX_NOACTIVATE | WS_EX_TOPMOST`)を`amm/window_probe` MethodChannel経由で表示させ、
      メモ帳をフォアグラウンドにした状態でも`GetForegroundWindow`が変化しないことを確認した。
      詳細は`reference/poc-flutter-terminal/RESULTS.md`参照。
- [x] **IME二重送信ガード問題の解消確認**: 現行WinForms版はネイティブTextBoxとWebView2の2つの
      IME合成系にまたがることが根本原因(`HANDOVER.md`記載の既知の繊細な調整箇所)。
      Tauri/Flutterはいずれも入力欄からターミナル表示まで単一の描画/入力系に統一されるため、
      この種の二重送信バグが構造的に起きないことを実機のIME(日本語入力)で確認する —
      **2026-07-13 実機(Windows 10 Pro)でTauri側を検証済み**。CDPの`Input.imeSetComposition`で
      共通入力欄・ターミナル本体それぞれのIME合成を模擬し、`compositionstart/update/end`が
      対象要素にのみ1回ずつ発火し、確定後の送信内容にも重複・文字化けが無いことを確認した。
      単一DOM構成では合成コンテキストがDOM要素単位で自然に排他されるため、WinForms版の
      「2つの合成系にまたがる」問題は構造的に再現し得ない。詳細は
      `reference/poc-tauri-terminal/RESULTS.md`の「IME二重送信ガード問題の解消確認」参照。
      **Flutter側も2026-07-13検証済み(コードレベル)**: `xterm`パッケージの`TerminalView`が
      Flutter標準の`TextInputClient`を実装しIME合成状態(`composing`)を正しくハンドリングしていること、
      共通入力欄(`TextField`)も同じ標準パイプラインに乗っていること、Flutter DesktopのIMEは
      エンジンレベルでフォーカス中の1クライアントにのみ排他的にルーティングされる設計であることを
      ソースコードで確認した。ライブの合成イベント発火テストは、この環境のネイティブWin32アプリに対する
      マウス座標自動化がDPIスケーリングの影響で信頼性が低く誤クリックのリスクがあったため見送った
      (詳細は`reference/poc-flutter-terminal/RESULTS.md`の「気づき・注意点」参照)。
- [x] GitHub Copilot CLI のbracketed paste submit問題(`UDR-amm-20260508T2200-cp1`)が新アーキテクチャでも
      同様に発生するか、あるいは挙動が変わるかを一応確認(致命的ではないが記録漏れ防止) —
      **2026-07-13 実機(Windows 10 Pro)、実際のGitHub Copilot CLI(v1.0.70)で検証済み**。
      WinForms版と全く同じバイト列(bracketed paste + `\r`×2)をTauri/ConPTY統合PoC経由で送信したところ、
      同様にsubmitされず入力欄に蓄積することを確認した(手動Enterのワークアラウンドも同様に有効)。
      **アーキテクチャ非依存、Copilot CLI(Ink TUI)側の受信解釈に起因する問題であることが実機で裏付けられ、
      Tauri/Flutter移植では自動解消しない**。詳細は`reference/poc-tauri-terminal/RESULTS.md`の
      「Copilot CLI bracketed paste submit問題の再現確認」参照。
      (参考: `.udr/records/UDR-amm-20260508T2200-cp1.yaml`は現時点でリポジトリに実体が見当たらず、
      `docs/manual/user-guide/usage.md`側のリンク切れの可能性がある。別途確認要)

## Mac実機で確認してほしいこと

- [x] Tauri: WKWebView相当(PlaywrightのWebKitエンジン)での同バッテリ(ANSI/CJK/絵文字/リサイズ/
      スクロールバック)確認 — **2026-07-11 実機(macOS 26.5.x, Apple Silicon)で検証済み**。
      ANSI/日本語/絵文字(Apple純正フルカラー絵文字、フォールバック無し)/リサイズ(1000→700pxで
      `tput cols` 100→79に追従)/スクロールバックいずれも問題なし。罫線(box-drawing)は実際に箱を
      組んだ状態(`┌─┬─┐│A│B│├─┼─┤│C│D│└─┴─┘`を複数行で描画)で隙間・欠けなく正しく連続描画される
      ことを確認(単一行に罫線グリフを並べただけの旧テスト文字列は繋がって見えないが、これは描画
      バグではなく元の文字列がそもそも箱を構成していないため)。外部フォントCDNへの依存も無し
      (`public/index.html`に`@font-face`/CDN参照なし)。詳細は
      `reference/poc-tauri-terminal/RESULTS.md` の「実機(macOS)検証」参照。
- [x] **`.app`バンドルとしての配布形態確認** — **2026-07-12 実機(macOS 26.5.2, Apple Silicon)で検証済み**。
      `cargo`/`rustc`/`tauri-cli`導入後、`cargo tauri build`で本物のTauriアプリ(Rust + WKWebView)の
      `.app`を生成し起動、WKWebViewが`server.js`(既存の`public/`+node-pty)に接続してWebSocket経由で
      実zshが起動することをプロセス/ソケット観測で確認した。詳細は`reference/poc-tauri-terminal/RESULTS.md`
      の「本物のTauriアプリ(Rust + WKWebView)での`.app`バンドル検証」参照。`.dmg`化のみFinder
      Automation権限起因で未解決(`.app`本体には影響なし)。
- [x] Flutter Desktop(macOS): 同様にオフラインでのCJK/絵文字描画確認(CoreTextベースのフォントフォールバックの
      はずだが、Windows同様に推測せず実機確認) — **2026-07-11 検証済み、上記「最優先」項目を参照**。
      CJK/絵文字/罫線/ANSI装飾すべて即座に正しいグリフで描画、外部CDNへの通信なしを確認。
      ただし `Release.entitlements` の `network.client` 欠落という別の罠を発見(同項目参照)。
- [x] macOS用pty(`posix_openpt`/`forkpty`相当)の統合方式の目処(node-ptyはmacOSも対応済みなので、
      上記Windows同様に一次検証としてそのまま使える) — **2026-07-11 実機(macOS 26.5.1, Apple Silicon)で検証済み**。
      `poc-flutter-terminal/pty-backend` と `poc-tauri-terminal` の両方で、WebSocket→node-pty→実zsh の
      往復を確認(ANSI・日本語・絵文字とも正常)。ただし **重要な既知の罠を発見**: この環境の `npm install` は
      `node-pty` prebuiltバイナリ `spawn-helper` の実行権限(+x)を落とし、`pty.spawn()` が
      `posix_spawnp failed` で失敗する(コード署名/quarantine起因ではなく単純な権限欠落、`codesign`/`xattr`で確認済み)。
      両PoCの `package.json` に `postinstall` で自動 `chmod +x` する `fix-pty-permissions.js` を追加して解消済み。
      実際のTauri/Flutter移植でも同じ罠を踏む可能性が高いため、移植先のビルド手順に同様の対処を組み込むこと。
- [x] ウィンドウ管理・システム統合系(Windowsほど深いOS統合は無い前提だが、Dockアイコンバッジ通知や
      常時最前面ポップアップ相当の実現性は確認) — **2026-07-12 実機(macOS 26.5.2, Apple Silicon)で
      Flutter Desktopのみ検証済み**。`NSApplication.dockTile.badgeLabel` によるDockバッジ表示、
      `NSPanel`(`.nonactivatingPanel` + `level: .floating`)によるフォーカス非奪取の常時最前面
      ポップアップともに、Swift側に薄いMethodChannelブリッジを追加するだけで問題なく実現できることを
      確認した(フォアグラウンドがTerminalのままパネルが最前面表示されることを`System Events`で確認)。
      詳細は `reference/poc-flutter-terminal/RESULTS.md` の該当項参照。**Tauri側は未検証**
      (cargo/rustc未導入のため、上記Tauriの`.app`バンドル化未実施と同じ制約)。

## 検証時の共通の注意点(サンドボックス検証から得た知見)

- 罫線・絵文字の意匠はOS標準フォントに強く依存する。サンドボックスの結果を鵜呑みにせず、実機の
  実際のフォント環境で見え方を確認すること
- Flutterでフォントをバンドルする際は、今回作った「サブセットフォント」ではなく**完全なCJKフォント**を
  使うこと(AIエージェントの出力は任意の文字を含みうるため、サブセットだと特定の文字だけグリフが
  欠落する不具合が起きる。詳細は `reference/poc-flutter-terminal/RESULTS.md` の「副次的な発見」参照)
- 自動テストツール(Playwright等)でIME/日本語入力をシミュレートする場合、`keyboard.type()`ではなく
  `keyboard.insertText()`相当(ペースト/IME確定に近い単発イベント)を使うこと。前者はマルチバイト文字で
  誤動作する(xterm.js自体の問題ではなくテストツールの制約)
- **絵文字の描画可否はOS/エンジンではなく「Windows固有の疑似端末API ConPTY」に起因する**:
  macOS(WKWebView)ではフォールバック無しでフルカラー描画されたが、Windows(Edge/Chromium)では
  xterm.jsのcanvas描画で絵文字が全く表示されない。当初「xterm.jsのcanvas fillTextがフォント
  フォールバックをしていない」と推測したが誤りで、xterm.jsメンテナ本人の発言・関連issue
  ([#2693](https://github.com/xtermjs/xterm.js/issues/2693)、6年以上未解決)により、
  **ConPTYが絵文字の画面リフレッシュ時の文字幅計算を誤りマルチバイト文字を欠落させる**という
  Windows/xterm.jsエコシステム共通の既知バグだと判明した。Flutter(xterm.dart、portable-pty経由で
  同じくConPTYを使う)でも同一Windows実機で同じ症状が再現することも確認済みで、原因がフレームワーク
  固有ではなくConPTY側にあるという説と整合する。fontFamilyへの絵文字フォント名追加は両ライブラリとも
  効果なし(当然、フォントの問題ではないため)。1プラットフォーム・1フレームワークの結果を安易に
  一般化しないこと。詳細は `reference/poc-tauri-terminal/RESULTS.md` の「実機(Windows)検証」と
  `reference/poc-flutter-terminal/RESULTS.md` の「絵文字描画の追加調査」参照

## 参照

- `reference/terminal-poc-comparison.md` — 今回のサンドボックス検証の結果サマリ
- `reference/poc-tauri-terminal/RESULTS.md` / `reference/poc-flutter-terminal/RESULTS.md` — 各PoCの詳細
- `docs/design/cross-platform-feasibility.md` — Avalonia + Photino.NET案(既存検討、Tauri/Flutterとの比較対象)
- `HANDOVER.md` §未解決の前提 — IME二重送信ガードの経緯
