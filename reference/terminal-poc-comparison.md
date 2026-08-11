# Tauri vs Flutter 試作比較 — ターミナルエミュレータとしての実現性

**検証ブランチ**: `terminal-poc`(作業ブランチ `claude/amm-windows-rearchitecture-ak5gpg` から分岐、特例)
**関連**: `reference/poc-tauri-terminal/RESULTS.md`, `reference/poc-flutter-terminal/RESULTS.md`,
`docs/design/cross-platform-feasibility.md`

## 検証の目的とスコープ

ユーザーの必須要件は「見た目は一新してよいが、**ターミナルエミュレータとして使えないのはダメ**」。
実装コストは度外視し、Tauri相当(xterm.js + 実PTY)とFlutter相当(xterm.dart + 実PTY)それぞれで
最小プロトタイプを作り、実shellに接続して比較した。

このコンテナ環境はLinuxかつGUIディスプレイ無しのため、両方とも**本番ターゲット(Windows/macOSネイティブ)
そのものではなく代理検証**である点に注意:

- **Tauri代理**: Node + Express + `ws` + `node-pty` + `@xterm/xterm`。Tauriの実体(Rust + OS標準WebView)は
  未検証だが、UI層(xterm.jsをWebViewでホストする)はTauriと完全に同一技術なので、この代理検証の再現度は高い
- **Flutter代理**: Flutter Web(CanvasKitレンダラ)+ `xterm`(xterm.dart) + 同じPTYバックエンド。
  Flutter Desktop(Windows/macOSネイティブ)はこのコンテナでビルドできないため、Web版で代用。
  加えて、画面描画に依存しない**データ層の直接検証**(`flutter test`)も実施し、Web版特有の問題と
  切り離してエミュレータのコア機能を検証した

両方とも実PTY(`node-pty`)で実shell(bash)を起動し、WebSocket経由で入出力する同一バックエンドを使い、
「描画クライアントの違いだけ」を比較対象にした。

## 結果サマリ

| 項目 | Tauri相当(xterm.js) | Flutter相当(xterm.dart) |
|---|---|---|
| 実PTY接続・実shell入出力 | ✅ | ✅ |
| ANSI前景色 | ✅ | ✅ |
| 太字/斜体/下線 | ✅ | ✅ |
| 日本語テキスト | ✅ | ✅(データ層はtest済み、画面描画も確認) |
| 絵文字 | ✅(表示、フォント依存) | △(codepointは正しいが今回のフォールバックフォントに絵文字グリフ無し) |
| 罫線 | △(フォント依存) | △(フォント依存) |
| リサイズ(pty resize伝播) | ✅ | ✅ |
| スクロールバック + ホイールスクロール | ✅ | ✅ |
| 初回描画までの時間(このサンドボックスでの体感) | 数百ms | **90〜120秒**(CJKフォールバックフォント解決待ち。Flutter Web/CanvasKit固有の問題、詳細は下記) |
| オフライン動作(CDN非依存) | ✅ そのまま(xterm.jsはローカル同梱前提で今の設計書通り) | ⚠️ **要対応**: デフォルトはGoogle Fonts CDNに依存。ローカルフォントバンドルの追加実装が必須 |

**結論: どちらの技術も「ターミナルエミュレータとして使えない」ということは無い。**
Flutter側はデータ層のテスト(`flutter test`、7件全PASS)により、ANSI/CJK/絵文字/罫線/スクロールバック/
リサイズいずれもエンジンとして正しく動作することを確認済み。画面描画の見え方の差はフォント資産の
バンドル方法の問題であり、エンジンの限界ではない。

## 一番重要な非対称性の発見

**Flutter Web(CanvasKitレンダラ)は、デフォルトでCJK/絵文字/記号のフォールバックフォントを
Google Fonts CDN (`fonts.gstatic.com`) から動的取得する設計になっている。**
このサンドボックスのようにそのドメインへ到達できないネットワーク方針下では:

- リクエストを失敗させると**無限リトライループ**に陥り、初回描画が永久に完了しない(実測: 5秒毎に
  500件以上リクエストが増加し続け、60秒以上待っても収束しない)
- ローカル代替フォントで応答を成立させると解消するが、CJK文字を含む画面では初回描画完了までに
  **90〜120秒程度、かつ実行毎にばらつく**という不安定な挙動になった

amm既存の設計書(`reference/design_doc.md`)は「完全オフライン動作、外部CDN非依存」を明記しており、
**Flutter Webの標準構成はこの方針と衝突する**。

ただし本番ターゲットは Flutter Web ではなく **Flutter Desktop(Windows/macOSネイティブビルド)** であり、
これはCanvasKit(Web/Wasm版レンダラ)ではなくImpeller/Skiaをネイティブに使い、フォントフォールバックも
OS標準API(Windows: DirectWrite、macOS: CoreText)経由になるため、**この特定の問題は再現しない可能性が高い**。
とはいえ「可能性が高い」を推測のまま採用判断に使うのは危険であり、**Windows実機のFlutter Desktopビルドで、
ネットワーク遮断下にCJK/絵文字/記号が正しく描画されるかを明示的に検証する必要がある**(未検証・次の
投資判断ポイント)。対策として、`pubspec.yaml`でNoto Sans CJK JP等のフォントをアプリに直接バンドルする
方法が確立している(ちょうどWinForms版が既にxterm.js/xterm-addon-fitをローカル同梱している方針と同じ発想)。

## Tauri側の所見

- xterm.jsをそのまま流用しているため、ANSI/日本語/太字斜体下線/リサイズ/スクロールバックいずれも
  現行amm(WinForms + WebView2 + xterm.js)と実質同一の挙動・忠実度で、移植リスクは低いことを確認
- 唯一の注意点は「Playwrightの`keyboard.type()`がマルチバイト文字で誤動作する」というテストツール側の
  制約であり、xterm.js自体の問題ではない(`insertText`で解消)

## 未検証・次の投資判断ポイント

- [ ] Windows実機でのFlutter Desktopビルドで、オフライン下のCJK/絵文字フォント描画を確認
- [ ] Tauri実体(Rust + WebView2)でのビルド・ConPTY相当のWindows pty統合の確認(今回はNode代理のみ)
- [ ] 罫線・絵文字の実際の意匠は両方式ともOS標準フォントに依存するため、Windows実機のフォント環境での
      見え方を確認
- [ ] MDI相当の複数ペイン管理・システムメニュー拡張・タスクバー点滅等、Windows深部統合の実現性
      (前回討議済み、docs/design/cross-platform-feasibility.md参照)

## 参照

- `reference/poc-tauri-terminal/` — xterm.js + node-pty 試作一式(`RESULTS.md`にスクリーンショット・詳細)
- `reference/poc-flutter-terminal/` — xterm.dart + node-pty 試作一式(`RESULTS.md`、`test/terminal_core_test.dart`)
- `docs/design/cross-platform-feasibility.md` — Avalonia + Photino.NET案(既存検討)との比較対象
