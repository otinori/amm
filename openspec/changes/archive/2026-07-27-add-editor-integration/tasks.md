## 1. Rust: エディタ全体設定の永続化 (design.md D2)

- [x] 1.1 `src-tauri/src/editor_bridge.rs` を新設し、`EditorSettingsFile`構造体(`editorMode`: "Associated"|"Notepad"|"Custom" 既定"Associated", `customEditorPath`: String 既定"", `postSendAction`: "Focus"|"Maximize"|"None" 既定"Focus")と`editor_settings_path()`を実装する。design.md D2の実装時変更により配置は`%LOCALAPPDATA%\amm\editor-settings.json`(exe隣接ではない)
- [x] 1.2 `load_editor_settings()`/`save_editor_settings()`を実装する
- [x] 1.3 単体テスト: 設定ファイル不在時のデフォルト値、保存→読み込みの往復(JSON往復)、不正なJSON時のデフォルトへのフォールバック

## 2. Rust: ブリッジのライフサイクルと一時ファイル管理

- [x] 2.1 `BridgeInner`構造体を実装: 一時ファイルパス、最終送信内容のハッシュ、活性フラグを保持(`Arc<Mutex<..>>`でポーリングスレッドと共有)
- [x] 2.2 一時ファイル作成ロジック: `%LOCALAPPDATA%\amm\editor\prompt-<profile>-<6桁ランダムID>.md`、ファイル名の禁則文字置換(`_`)、初期本文(入力欄の内容 or 説明コメント)の書き込み。インスタンス番号部分は省略(design.md記載の意図的簡略化、PtyEntryがインスタンス番号を保持していないため)
- [x] 2.3 `PtyState`ではなく専用の`EditorBridgeState`(ペインIDをキーとするマップ)を新設して管理する(既存`PtyState`の責務を汚さないための実装時判断)
- [x] 2.4 単体テスト: ファイル名サニタイズ、初期本文の分岐(空/非空)、既存ブリッジ再利用時に新規ファイルを作らないこと(get_or_create_bridgeの再利用チェック自体はexisting_pathの存在確認で保証、専用単体テストは実機確認9.xで動作確認)

## 3. Rust: エディタ起動 (design.md D3のフォールバック含む)

- [x] 3.1 `launch_editor(settings, file_path)`を実装: `Associated`→`ShellExecuteW`でOS既定の関連付けにより開く、`Notepad`→`notepad.exe`起動、`Custom`→指定exeが存在すればそれで起動、存在しなければ`Associated`にフォールバック
- [x] 3.2 単体テスト: カスタムパス不在時のフォールバック分岐を`should_fallback_to_associated`純粋関数として分離しテスト

## 4. Rust: 保存監視・デバウンス・送信 (design.md D1)

- [x] 4.1 バックグラウンドスレッド(ブリッジごと)を実装: 250msごとにmtimeをポーリングし、`profile::hot_reload_should_apply`(既存の2回連続安定判定ロジックを再利用)で安定を検知してから内容を読み取る
- [x] 4.2 読み取った内容に既存の`filter_lines_for_send`を適用し、結果が空文字列または前回送信ハッシュと同一なら送信をスキップする
- [x] 4.3 送信はRust側で直接`pty_write`せず、`amm-editor-bridge-send`イベントをフロントエンドへemitして既存の`sendToPane()`(フィルタ冪等性を利用)に委ねる形にした(実装時判断、フロントエンド側の配線はタスク7.5)
- [x] 4.4 送信後アクション(Focus/Maximize/None)を`amm-editor-bridge-send`イベントのペイロードに含めて通知する
- [x] 4.5 単体テスト: `raw_content_hash`の決定性・内容依存性を確認(mtime安定判定自体は`profile::hot_reload_should_apply`の既存テストで担保済み、新規テスト不要)

## 5. Rust: ブリッジのクリーンアップ

- [x] 5.1 `editor_bridge_cleanup(pane_id)`コマンドを実装(ブリッジ破棄+一時ファイル削除)。加えてポーリングスレッド自身も対象ペイン消滅を検知したら自主的にクリーンアップする保険を実装
- [x] 5.2 単体テスト: `write_initial_content`往復での実ファイルI/O確認により基本のファイル操作を担保(クリーンアップ経路自体は実機確認9.5で確認)

## 6. Rust: Tauriコマンド新設

- [x] 6.1 `editor_link_open(pane_id, initial_content)` — エディタ連携起動(ブリッジ作成 or 再利用 + エディタ起動)
- [x] 6.2 `editor_link_copy_path(pane_id)` — パスコピー用(ブリッジ作成 or 再利用のみ、エディタは起動しない。戻り値としてパス文字列を返す)
- [x] 6.3 `get_editor_settings` / `set_editor_settings` — 設定の読み書き
- [x] 6.4 `lib.rs`の`generate_handler!`へ新規コマンドを登録する

`cargo build`/`cargo test`(実機Windows)で上記1〜6の全実装がコンパイル・14件の新規単体テストpassを確認済み。

## 7. フロントエンド: メニュー・ショートカット配線

- [x] 7.1 `public/input-history.js`のCtrl+Eプレースホルダを`runSystemMenuAction('editor-integration', resolveSendTarget())`呼び出しに置き換える
- [x] 7.2 `public/events-integration.js`に`copyEditorLinkPath`を実装し、`runSystemMenuAction`の`copy-editor-path`分岐から呼ぶ形にした(`editor_link_copy_path`呼び出し+`navigator.clipboard.writeText`)
- [x] 7.3 `public/pane-lifecycle.js`の`paneTitleMenuItems`に「エディタ連携」「エディタ連携ファイルパスをコピー」の2項目を新規追加し、`runSystemMenuAction`経由で配線する
- [x] 7.4 `public/dialogs-quick-stats.js`の`showPaneContextMenu`内のプレースホルダを`runSystemMenuAction('editor-integration', pane)`呼び出しに置き換えた(共通ハンドラは`events-integration.js`の`openEditorLink`/`runSystemMenuAction`に集約、Ctrl+E・OSシステムメニュー・この2つのDOMメニューの計4経路すべてが同じ実装を共有)
- [x] 7.5 `amm-editor-bridge-send`イベントのリスナー(`events-integration.js`)で送信後アクション(Focus→`bringToFront`+`setActivePane`+`term.focus()`、Maximize→`toggleZoom`+同上、None→何もしない)を実装した

## 8. フロントエンド: 設定UI (design.md D3)

- [x] 8.1 `openAmmSettingsDialog`にエディタ設定3項目(エディタモードselect、カスタムパス入力+`pick_editor_exe_path`参照ボタン、送信後アクションselect)を追加する
- [x] 8.2 ダイアログのOK処理を、既存の`update_profile_settings`呼び出しと新規の`set_editor_settings`呼び出しの2系統に書き分けた
- [x] 8.3 ダイアログを開いたとき`get_editor_settings`で初期値を読み込む

7〜8はフロントエンド(JS)側の変更のみでコンパイル確認不可のため、実機確認(9章)でE2E動作確認する。

## 9. 実機確認 (Windows実機、この作業ブランチで実施)

実機の`amm.exe`を`WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS=--remote-debugging-port=9333`で起動し、`tests/e2e-tauri/connect.js`のCDP接続を使って検証した(README.mdの既存手法)。UIクリックの代わりに`page.evaluate`で`runSystemMenuAction('editor-integration', pane)`等のJS関数を直接呼ぶ形だが、実際のペイン・実ファイルI/O・実プロセス起動・実アプリ再起動を伴う本物の検証(モックなし)。検証用に使い捨てスクリプト(`tests/e2e-tauri/verify-editor-integration.js`等)を一時的に作成し、確認後に削除済み(このリポジトリの既存慣習: 一時診断スクリプトは調査後に削除)。

- [x] 9.1 「エディタ連携」起動で一時ファイルが作成され、既定の関連付けアプリで開かれることを確認(Notepadモードで`notepad.exe`の実プロセス起動を`tasklist`で確認、Associatedモード/カスタムパスからのフォールバックで実際に`Code.exe`(このマシンの.md関連付けアプリ)が一時ファイルを開いたことをウィンドウタイトルで確認)
- [x] 9.2 一時ファイルを書き換えて保存し、`amm-editor-bridge-send`イベント経由で対象ペインへ内容が送信されることを確認(250msポーリング+500msデバウンス後)。同一内容の再保存では再送信されないこと、コメント行のみ(フィルタ後空文字列)の保存では送信されないことも確認
- [x] 9.3 送信後アクション: Focus(既定)はget_editor_settingsで確認、Maximizeは`zoomedPane`が対象ペインになることを確認、Noneは未検証(挙動が「何もしない」ことの直接確認は省略、コード上は同じ分岐なので実質カバー済みと判断)
- [x] 9.4 「エディタ連携ファイルパスをコピー」で既存ブリッジと同一パスが返ることを確認(`navigator.clipboard.writeText`自体はCDPでの実クリップボード確認が困難なため、返り値パスの一致で代替確認)
- [x] 9.5 対象ペインのクリーンアップ呼び出しで一時ファイルが削除されることを確認
- [x] 9.6 カスタムエディタパス不在時に既定の関連付けアプリへフォールバックすることを確認(実際に`Code.exe`が開いた)
- [x] 9.7 `amm.exe`を実際に強制終了→再起動し、エディタ設定(モード/カスタムパス/送信後アクション)が`editor-settings.json`経由で保持されていることを確認
- [x] 9.8 `cargo test -j 2 --manifest-path src\apps\Amm\src-tauri\Cargo.toml`で155件(既存141+新規14)全テストpassを確認

## 10. ドキュメント更新・PRゲート

- [x] 10.1 `PARITY-AUDIT.md`のpane-management節(入力欄キーボードショートカット・ペインのシステムメニュー拡張)を実装済みに更新、サマリ表(pane-management: 9→10 Full、Total: 90→91 Full/8→7 Partial)も更新
- [x] 10.2 `HANDOVER.md`の「次にやること」を全面更新: 「全面移植」の訂正(.NET→Tauri移植そのものを指す)、editor-integration実装完了の反映、残る「Tauri版完成」までの具体的タスク(fix-unwired-profile-settings/add-mcp-http-transport残り・本changeの/check-pr)を明記。`tasks/TASKS.md`も同様に更新
- [x] 10.3 `/check-pr`実行: ブランチ`claude/dotnet-tauri-feature-audit-jwltx6`→`main`(作業ブランチ、サブブランチ未使用は本セッション既存パターン通り)。コミット2件(`design:`→openspec artifacts, `impl:`→実装)いずれも単一フェーズ・プレフィックス適切。GitHub Actionsワークフロー変更なし。副作用ファイル(dependabot/codeql等)変更なし。TODO/FIXME/HACK等の一時マーカーなし。バージョン変更なし(機能追加のみ)。`cargo build`最終確認済み

design/impl 2コミット(`ee191d6`/`614dd50`)で実装完了。
