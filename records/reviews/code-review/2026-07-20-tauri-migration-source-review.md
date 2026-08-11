# コードレビュー記録 — migrate-to-tauri 実装 (src/apps/Amm.Tauri)

- 日付: 2026-07-20
- 対象: `src/apps/Amm.Tauri`（Rust: `src-tauri/src/*.rs` 約6,400行、フロントエンド: `public/app.js` 約2,100行）
- 対象ブランチ: `terminal-poc-impl`
- 関連: `openspec/changes/migrate-to-tauri/`（phase 8 パリティ検証中）、`UDR-amm-20260713T1037-ff3`（Tauri採用決定）
- レビュー実施者: Claude Code（IT アーキテクト / シニアプログラマー / セキュリティスペシャリストの3視点、ユーザー依頼によるアドホックレビュー。`/security-review` 等の定型スキルは使用せず、セッション内で直接ソースを読んで実施）
- 手法: 静的読解のみ（一部、既発見バグの実機再現検証は別途本セッションで実施済み・別途コミット済み）

## サマリ

3視点で読解した結果、**重大度「高」1件（セキュリティ）**、中〜低の指摘が数件見つかった。良好な点として、Git操作のシェルインジェクション対策・フロントエンドのXSS対策（`innerHTML`未使用の一貫したDOM構築）・実機検証を重視した開発プロセス由来のテストカバレッジは評価できる。

**本記録の時点では、指摘事項はいずれも未対応(証跡記録のみ)。** 対応要否・優先順位はユーザー判断待ち。

---

## 🔴 高: `.amm` プロファイルファイルオープン時の無確認コマンド自動実行

- **該当箇所**: `tauri.conf.json`(`fileAssociations`) / `lib.rs:1206-1214`(CLI引数を`profiles_path`として解釈) / `profile.rs:511-541`(`parse_profiles_path_arg`/`resolve_profiles_path`) / `lib.rs:1312`+`gateway.rs:352-365`(`start_auto_start_servers`、`.setup()`内で無条件実行) / `profile.rs:484-495`(`McpServerConfig::auto_start`のデフォルトが`true`)
- **内容**: `.amm`拡張子はファイル関連付け登録済み(`"role": "Editor"`)。ダブルクリック等で開かれたパスがそのまま`profiles.amm`として読み込まれ、含まれる`mcpServers`配列は確認ダイアログなしに子プロセスとして起動される。`auto_start`はJSON省略時にデフォルト`true`。
- **再現シナリオ**: `{"mcpServers":[{"name":"x","command":"powershell.exe","args":["-enc","<payload>"]}]}` という内容の `evil.amm` を作成し、メール添付・ダウンロード等でユーザーにダブルクリックさせるだけで、確認なしに任意コード実行が成立する(Office マクロや `.lnk`/`.scf` と同種の「ファイルを開いただけで実行される」パターン)。
- ~~**推奨対応(未実施)**~~ **2026-07-21 対応済み・実機E2E確認完了**: アプリ既定パス以外から明示的に開かれた`.amm`は、`%LOCALAPPDATA%\amm\trusted-profiles.json`で承認済みと記録されていない限り`mcpServers`の自動起動を保留し、起動前に確認モーダル(パス・自動起動コマンド一覧を表示、許可/拒否)を挟むよう実装済み(`tasks.md` 8.6.1、`profile::is_path_trusted`/`mark_path_trusted`、`lib.rs`の`PendingUntrustedAutostart`、`app.js`の`openModal`ベース確認ダイアログ)。実機で未承認ファイル起動→確認モーダル表示→許可でサーバー起動+信頼記録／拒否でサーバー停止のまま、を実データで確認済み。詳細は`tasks/pending-real-machine-verification.md`8.6.1項目。

## セキュリティ(その他、中〜低)

1. **`read_text_file`(`lib.rs:1032-1035`)が任意パス読み取り可能なTauriコマンド** — `capabilities/default.json`で全ウィンドウに許可、パス制限なし。単体では低リスクだが、上記`.amm`経由の導線と組み合わせると任意ファイル読み取りに転用され得る。
2. **名前付きパイプ(`mcp.rs:698-699`)に明示的ACLなし** — `\\.\pipe\amm-mcp-{username}`は既定セキュリティ記述子。同一Windowsユーザーの別プロセスなら`pane/open`(任意コマンド起動)・`send_message`(任意ペインへの文字列注入)・`resolve_approval`(承認ハブの承認/拒否)を誰でも呼べる。設計上意図的なローカル自動化APIの信頼境界と思われるが、特に`amm/approval`(危険操作への人間承認ゲート)が同ユーザー内の別プロセスから横取り/自動承認され得る点は明文化推奨。
3. **`tray-quit`(`lib.rs:874`)が`app.exit(0)`で即終了** — ウィンドウを閉じる際に通る`onCloseRequested`のGitガード・未保存確認(`app.js:2041-2057`)を完全にバイパスする。本セッションで修正したbug2(終了ダイアログが閉じない不具合、`capabilities/default.json`に`core:window:allow-destroy`追加、コミット`5a48bfd`)とは別経路の抜け穴。
4. **`hook_cli.rs`/`mcp_cli.rs`が他CLIツールの設定ファイルに`cmd /c "..."`形式のコマンド文字列を書き込む**(`hook_cli.rs:109,328,333`) — 書き込みパスは自プロセス実行ファイル位置基準の`resolve_mcp_exe_path()`で通常安全。PATHハイジャック対策自体は別途実装済み(コミット履歴`6eaccec`で確認)。

**良好点**: `git_helper.rs`は`Command::new("git").args([...])`の配列渡しでシェルインジェクションの心配がない。`app.js`の動的DOM構築は一貫して`textContent`/`createElement`を使用し、`innerHTML`への未エスケープ文字列埋め込みは検出されず、XSS対策は堅実。

## アーキテクト視点

- `lib.rs`が1,332行のモノリス(Tauriコマンド定義・トレイアイコン・システムメニューSubclass・状態管理が同居)。`mcp.rs`/`gateway.rs`/`profile.rs`のようなモジュール分割が一部あるので、コマンドハンドラ群を`commands/`以下に分けると将来の見通しが良くなる(緊急性は低い)。
- `std::sync::Mutex`の`.lock().unwrap()`を全体で多用(`PtyState`, `ProfilesState`, `TrayState`等)。パニックしたスレッドがロック保持中に死ぬとミューテックスが汚染(poisoned)され、以降そのステートに触れる全コマンドがパニックする。PTYリーダースレッド(`lib.rs:686-736`)はunwrapを多用しており波及リスクあり。`parking_lot::Mutex`への置き換えや要所への`catch_unwind`は検討の価値あり。
- .NET版とTauri版の並行運用が現設計(`HANDOVER.md`)。Tauri版が.NET版のプロトコル・ファイル形式を完全模倣する戦略が徹底されており(`mcp.rs`冒頭コメント等)、移行リスクを下げる良い設計判断。
- ~~承認ハブ(approval-hub)のポップアップウィンドウがCDPから検証不能という既知の制約~~ **2026-07-21 対応済み**: CDPから検証不能だっただけでなく、実際にアプリ全体を永久デッドロックさせる🔴CRITICAL不具合であることが実機デバッガ(cdb.exe)で判明(第2`WebviewWindow`生成がメインスレッド上のIPCコールバック内から同一スレッドで再帰的にWebView2コントローラ生成を同期待機し、循環待機に陥る)。第2ネイティブウィンドウをやめ、メインウィンドウ内DOMオーバーレイへ置き換えて解消(`show_approval_popup`コマンド削除、`app.js`の`approvalOverlay`)。実機で実際のフック経由承認要求→許可/拒否の一連を確認済み、詳細は`tasks/pending-real-machine-verification.md`3.4項目。

## シニアプログラマー視点

- `git_helper::run`(`git_helper.rs:9-51`)がタイムアウト検出にビジーポーリング(50ms sleep + `try_wait`)を使用。動作はするが`tokio::process`への統一の方が素直(恐らく`onCloseRequested`から呼ばれる都合の同期実装)。
- `extract_exe_path`(`hook_cli.rs:33-40`)のフォールバックが`command.split(' ').next()`。パスにスペースが入る場合に誤動作し得るが、通常は正規表現マッチが先に効くため実害は小さい。フォールバック分岐のテストカバレッジなし。
- テストは要所(`gateway.rs`, `hook_cli.rs`, `profile.rs`)に揃っており、特に`hook_cli.rs`のテストは「ユーザー自身の既存設定を壊さない」という重要な不変条件を明示的に検証していて質が高い。
- `install_amm_syscommand_hook`(`lib.rs:1191-1202`)の`Box::into_raw`によるリークはコメントで意図的と明記されており妥当(アプリ生存期間と同じライフタイム)。

## 次のアクション(未決定)

- 🔴案件(`.amm`自動実行)の対応要否・方式をユーザーと合意の上、対応する場合はUDR記録対象(セキュリティ境界に関わるアーキ判断のため)。
- 他の指摘は`tasks/backlog.md`または`tasks/TASKS.md`へ転記するかは未定(本記録が一次証跡)。
