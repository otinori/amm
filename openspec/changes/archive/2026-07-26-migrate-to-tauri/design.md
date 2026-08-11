## Context

現行 amm は .NET 9 / WinForms(`MdiParentForm` 親 + `TerminalChildForm` 子)+ WebView2(xterm.js ホスト)+
ConPTY(`ConPtyWrapper`)+ Named Pipe(MCP サーバ内蔵)という構成。GUI 本体(`src/apps/Amm`)以外に、
stdio↔NamedPipe ブリッジ(`Amm.Mcp`、net9.0、Windows 非依存)と PowerShell バイナリモジュール
(`Amm.PowerShell`)がある。

`reference/poc-tauri-terminal/` での実機検証(Windows 10 Pro、macOS 実機)により、Tauri(Rust + WebView2/
WKWebView + xterm.js + `portable-pty`)で以下を確認済み:

- Rust ネイティブ `portable-pty` crate による ConPTY 統合(Node.js 完全排除、実機ビルドで動作確認)
- `windows` crate + 生 HWND(`WebviewWindow::hwnd()`)経由での Win32 API 呼び出し(システムメニュー拡張、
  `FlashWindowEx`、非フォーカス奪取ポップアップ)
- HTML/CSS(`position:absolute` + `z-index`)+ JS のみでの単一ウィンドウ内ペイン管理
  (タイル/カスケード/ドラッグ移動/リサイズ/`localStorage`への配置記憶・復元)
- 単一 WebView 構成によるIME二重送信ガードの構造的解消(CDPの`Input.imeSetComposition`で実証)

移植先フレームワークは `UDR-amm-20260713T1037-ff3` で Tauri に決定済み(判断基準: 操作性の快適さ、
xterm.js の実績とペイン管理・ConPTY統合の検証深度による)。ウィンドウ管理方式は
`UDR-amm-20260713T0447-98f` で MDI 撤廃・ペイン管理採用が決定済み。

## Goals / Non-Goals

**Goals:**
- GUI 本体を Tauri(Rust + WebView2)へ全面移植し、現行 16 capability のうち 14 は挙動パリティを維持する
- MDI を単一ウィンドウ内ペイン管理方式に置き換える(`pane-management` 新設)
- Node.js への依存を排除し、Rust ネイティブの `portable-pty` で ConPTY 統合する
- `Amm.Mcp` / `Amm.PowerShell` との Named Pipe プロトコル互換性を維持し、両者の改修を最小化する

**Non-Goals:**
- Mac/Linux 版の本格対応(Tauri 自体はクロスプラットフォームだが、本変更のスコープは Windows 版の
  技術基盤置き換えであり、Mac/Linux 版の機能追加・配布は別変更として扱う)
- Windows 実機での ConPTY 起因の絵文字描画欠落の解消(既知の制限として許容、対応不要と決定済み)
- xterm.js から別のターミナルレンダリングライブラリへの置き換え(継続利用)
- 新機能の追加(本変更はあくまで技術基盤の置き換えであり、挙動パリティが目標)

## Decisions

### 1. フロントエンドは xterm.js を継続利用し、WebView2 内で完結させる
`reference/poc-tauri-terminal/public/` の資産(xterm.js + addon-fit + addon-unicode11)をそのまま
移植のベースにする。理由: 現行版と同一のレンダリングライブラリのため視覚的・機能的なリグレッションリスクが
最小。共通入力欄とターミナル表示を同一 DOM 内に置くことで IME 合成コンテキストが自然に排他される
(`reference/poc-tauri-terminal/probe-ime.js` で実証済み)。

代替案: xterm.dart(Flutter)への切替。`UDR-amm-20260713T1037-ff3` で棄却済み(操作性の実績面でxterm.jsに劣る)。

### 2. ConPTY 統合は Rust ネイティブの `portable-pty` crate を使う
`reference/poc-tauri-terminal/src-tauri/src/lib.rs` の `pty_spawn`/`pty_write`/`pty_resize` 実装
(約80行)をベースに、複数ペイン対応のため `PtyState` をペイン ID でキー管理する設計に拡張する
(現行 PoC は 1 ウィンドウ = 1 pty の最小構成のため、これは本移植で新たに設計・検証が必要)。

代替案: `node-pty` + Node.js プロセス埋め込み。Node.js ランタイムの同梱が必要になり配布サイズ・
起動コストが増えるため不採用。

### 3. ウィンドウ管理は単一ウィンドウ + HTML/CSS/JS によるペイン管理
`reference/poc-tauri-terminal/public/mdi-prototype.html` で検証済みのタイル/カスケード/ドラッグ移動/
リサイズ/`localStorage`記憶を土台に、`pane-management` capability の正式仕様として書き起こす。
ペインごとに独立した ConPTY セッション(pty_spawn 呼び出し)を持たせる(決定2と連動)。

既知の注意点(PoC検証で判明): 重なったペインは露出部分でしかクリックできない(実際のウィンドウマネージャと
同様の挙動)。クリックで最前面に持ってくる操作をタイトルバーだけでなくペイン全体に対応させる設計が必要。

### 4. Win32 ネイティブ統合は `windows` crate 経由で Rust 側に実装する
システムメニュー拡張(`GetSystemMenu`+`AppendMenuW`)、`WM_SYSCOMMAND` フック(`SetWindowSubclass`)、
`FlashWindowEx`、非フォーカス奪取ポップアップ(`WebviewWindowBuilder`の`.always_on_top(true)`+
`.focused(false)`)は、いずれも `reference/poc-tauri-terminal/src-tauri/src/lib.rs` の実装をそのまま
移植のベースにできる。

既知の罠(PoC検証で発見・解消済み): システムコマンドID は 16 の倍数でなければならない(Win32が
wParamの下位4bitを予約するため)。

### 5. Named Pipe MCP サーバは Rust 側で同一ワイヤプロトコルを再実装する
現行の Named Pipe サーバは GUI プロセス(`Amm.exe`)に内蔵されているため、GUI 本体の Rust 移植に伴い
サーバ自体も Rust で再実装する必要がある。`Amm.Mcp`(stdio↔NamedPipe ブリッジ)と `Amm.PowerShell`
(バイナリ PS モジュール)は、Named Pipe のワイヤプロトコル(JSON-RPC メッセージ形式)さえ変わらなければ
**改修不要**とする方針とする(protocol-levelの互換性維持を最優先の制約とする)。

> **`UDR-amm-20260719T0013-b7e` により後半(Amm.Mcp/Amm.PowerShellを.NETのまま維持する部分)は
> 上書き**: 複数言語の保守負担を避けたいというuser方針により、Amm.McpはRust製バイナリcrateへ、
> Amm.PowerShellはPowerShellスクリプトモジュール(.psm1)へ移行する方針に変更した。ワイヤプロトコル
> 互換性を最優先制約とする部分(前半)は変更なし。詳細は同UDR、実装状況はtasks.md新設フェーズ参照。

### 6. テストは `cargo test` + 既存の xUnit テストからの移行方針を個別検討
`tests/apps/Amm.Tests`(xUnit、GUI非起動でのロジック単体テスト)は Rust 側のロジックにはそのまま使えない。
プロファイルスキーマのパース・バリデーション等、フレームワーク非依存のロジックは Rust 側で `cargo test`
として書き直す。GUI 起動を伴う統合テストの扱い(Tauri の WebDriver 対応や Playwright 経由の E2E 等)は
Open Questions で扱う。

### 7. `Amm.PowerShell` は .NET のまま維持し、Rust への移行は行わない (**`UDR-amm-20260719T0013-b7e` により上書き済み・後述**)
`Amm.PowerShell`(バイナリ PS モジュール)は Named Pipe クライアントとして `amm.openPane`/
`amm.closePane`/`amm.waitState` 等の直接 RPC を叩くだけの薄い層であり、GUI 側の実装言語(WinForms/
Tauri いずれか)に依存しない。phase 4(4.2/4.5)で該当 RPC のワイヤプロトコル互換性を生パイプ接続で
個別確認済みのため、cmdlet 側のコード変更は不要と判断できる。よって移行の動機がなく、`net9.0`
ターゲットのまま維持する(tasks.md 5.11 で決定)。

> **この決定は`UDR-amm-20260719T0013-b7e`で覆された**: 「複数言語による開発はメンテナンス上避けたい」
> というuser方針により、`Amm.PowerShell`はPowerShellスクリプトモジュール(`.psm1`、コンパイル不要)
> として作り直す方針に変更。旧`.NET`版(`src/modules/Amm.PowerShell/`)は旧WinForms版向けにそのまま
> 維持し、新モジュール(`src/modules/Amm.PowerShell.Tauri/`)を新設して並行運用する。

代替案: Rust への移行(PowerShell バイナリモジュールを Rust で書く)。プロトコル互換性維持以外の
利点がなく、`Amm.Mcp` 同様「protocol-levelの互換性さえ保てば改修不要」の対象に含めるほうが
移行コストに見合う。不採用。

### 8. インストーラは Tauri bundler(NSIS/MSI)へ切り替え、WiX 資産と並行運用する
`tauri.conf.json` の `bundle` に `targets: ["nsis","msi"]`・`fileAssociations`(`.amm`)・
`resources`(`amm-mcp.exe`/`Amm.PowerShell` を dotnet publish でステージした
`artifacts/publish-tauri/stage/*`、および `profiles.amm`/`usage.md`)・`windows.wix.license`/
`windows.nsis.license`(既存`installer/wix/License.rtf`を再利用)を設定し、`tools/publish-tauri.cmd`
/`tools/build-installer-tauri.cmd`を新設した(既存`tools/publish.cmd`/`build-installer.cmd`は不変)。
WiX の `UpgradeCode` は意図的に再利用しない(Rollback戦略「段階的cutover、旧版を廃止しない」の間は
新旧インストーラが互いを検出してアンインストールし合わないようにするため)。PATH自動追加
(WiXの`PathEnvironment`相当)はTauri bundlerに同等機能がなく、必要ならNSISカスタムフック/WiX
fragmentPathsでの追実装が要る(未着手、tasks.md 6.1)。

代替案: 既存WiXインフラ(`installer/wix/Package.wxs`)を維持しビルド成果物だけTauriのexeに差し替える。
移行コストは低いがWiXのXML保守が二重(WinForms版・Tauri版)に残り続けるため、`cutover`完了後も
WiXを触り続ける前提になる。Tauri bundlerへ一本化するほうが最終形に近いと判断し不採用。

### 9. バージョン番号は当面 `Directory.Build.props` と `Cargo.toml`/`tauri.conf.json` を手動同期する
`Directory.Build.props`の`<Version>1.2.0.0</Version>`(正本、.NET版)と、`Cargo.toml`/
`tauri.conf.json`の`"version": "1.2.0"`(Tauri版、末尾の.0リビジョン桁を除く3桁semver)は
現状既に値が一致している。ロールバック戦略により両版は並行維持されるため、共有ビルドスクリプトで
自動同期する仕組みを今作るのは時期尚早と判断し、リリース時に両ファイルを手動で同じ値へ更新する
運用とする(値のずれはレビューで検知可能な軽微なリスクとして許容)。Cargo.tomlワークスペース化に
よる完全統合は、旧WinForms版を廃止する最終cutover時(`Directory.Build.props`自体が不要になる
タイミング)に一度だけ行えばよく、それまで自動化コストを払う必要がない。

代替案: ビルド時に`Directory.Build.props`から`Cargo.toml`へバージョンを注入するスクリプトを今
作る。両版が最終的に一本化されることが確定しているため、並行期間限定の同期自動化に投資する
費用対効果が低いと判断し不採用。

### 10. GUI統合テストはPlaywright `connectOverCDP`を正式な方式として採用する
WebView2はChromiumそのものであり、`WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS=--remote-debugging-port=<port>`
付きで起動すればChrome DevTools Protocolを露出する。本セッションのphase 1-5全ての「実機で確認」は
実際にこの方式(`reference/poc-tauri-terminal/probe-sysmenu.js`が先行して使っていたパターンを
実アプリ向けに一般化)で検証しており、新方式の調査は不要と判断し正式な統合テスト方式として採用する。
`tests/e2e-tauri/`(`connect.js`のヘルパー関数+README)としてメソッドを形式知化した。メインウィンドウ
内(ペイン・共通入力欄・DOM実装のダイアログ)はCDP経由のPage APIで検証できるが、
`WebviewWindowBuilder`で動的生成する別ウィンドウ(承認ハブポップアップ等)はCDPのcontexts()に
確実には現れない既知の未解決問題があり(`tasks/pending-real-machine-verification.md`参照)、
そちらはWin32 `EnumWindows`/`PrintWindow`による代替手法が必要になる。

代替案: Tauri公式のWebDriver対応(`tauri-driver`)。調査コストがかかる上、本セッションで
Playwright CDP接続が実際に機能する実績を既に積んでいるため、新方式への切り替え動機がないと
判断し不採用(将来WebDriverの方が優れると判明すれば再検討可)。

## Risks / Trade-offs

- [大規模フルスクラッチによる未知のリグレッション] → 現行 `openspec/specs/` の 16 capability・99
  requirement を正本とし、各 capability の移植完了時に既存 spec との突き合わせレビューを行う
  (`tasks.md` でフェーズごとのチェックポイントとして計画)
- [Windows実機でのConPTY起因の絵文字描画欠落] → 対応不要と決定済み(現行WinForms版でも起きている
  可能性が高く、移植による悪化ではないため許容)
- [Rust/Tauriの学習コスト] → フロントエンド(xterm.js・HTML/CSS/JS)は現行のWebView2ホスト方式の知見を
  ほぼ流用できる。Rust側はPoCで実装パターン(pty統合・Win32相互運用)を確立済みのため、ゼロからの
  試行錯誤は避けられる
- [操作性が期待に届かない場合のリスク] → `UDR-amm-20260713T1037-ff3` でFlutterへの切替を明示的に
  許容する前提としており、Tauri実装が手詰まりになった場合のフォールバック先は既に検討済み
  (`reference/poc-flutter-terminal/`に部分的な実装・検証結果あり)
- [ペイン管理のUX詳細が未確定] → 重なったペインのクリック対象領域など、実装時にUX設計判断が必要になる
  箇所がある(上記決定3参照)。tasks.mdで実装タスクとして明示する
- [Amm.Mcp/Amm.PowerShellとのプロトコル互換性が崩れるリスク] → Named Pipeワイヤプロトコルの互換性を
  最優先制約とし、Rust側サーバ実装時にプロトコルレベルの回帰テストを行う

## Migration Plan

段階的に実装する(詳細タスクは`tasks.md`)。各フェーズ完了時点で動くビルドを維持する:

1. **基盤フェーズ**: Tauriプロジェクト新設、xterm.js移植、単一ペインでのConPTY統合(既存PoCの延長)
2. **ペイン管理フェーズ**: `pane-management`実装(複数ペイン対応のPtyState設計含む)、MDI関連仕様の置換
3. **ネイティブ統合フェーズ**: システムメニュー拡張、トレイアイコン、`FlashWindowEx`、承認ハブポップアップ
4. **プロトコル互換フェーズ**: Named Pipe MCPサーバのRust再実装、`Amm.Mcp`/`Amm.PowerShell`との疎通確認
5. **残り機能フェーズ**: profile-schema、input-history、chat-recording、git-integration、hook-cli、
   wait-detection、quick-command-register、command-import-export、auto-send-idle 等の移植
6. **配布・CI整備フェーズ**: Tauri bundlerでのインストーラ生成、CI(GitHub Actions)のRustツールチェイン化
7. **パリティ検証・切替フェーズ**: 既存specsとの突き合わせ、実機での最終確認、旧WinForms版からの切替判断

ロールバック戦略: 旧WinForms版(`main`ブランチの現行実装)はこの変更が安定するまで並行して維持し、
Tauri版が全capability・実機検証を満たすまで旧版を廃止しない(段階的cutover、big bang置換はしない)。

## Open Questions

- 絵文字描画問題が将来的に許容できなくなった場合の緩和策(テキスト置換フィルタ等)の実装要否の再検討時期
