## 1. useBracketedPaste / newlineMode の削除

- [x] 1.1 `profile.rs`の`SessionProfile`構造体から`use_bracketed_paste`/`newline_mode`フィールドを削除する
- [x] 1.2 `app.js`の`defaultSessionProfile()`/`COMMAND_TYPE_PRESETS`(Cmd/PowerShell/ClaudeCode/Codex/CopilotCli/Gemini相当)から`useBracketedPaste`/`newlineMode`を削除する
- [x] 1.3 `openCommandTemplateDialog`から改行モードのセレクト・bracketed pasteのチェックボックスのUI要素と、保存時の`result`への詰め込み、プリセット適用時(`typeSelect`の`change`ハンドラ)の該当行を削除する
- [x] 1.4 `cargo build`で`use_bracketed_paste`/`newline_mode`の残存参照が無いことを確認する

## 2. autoChcp: 起動コマンドのコードページ強制

- [x] 2.1 `spawn_pty_for_pane_with_patterns`に`auto_chcp: bool`引数を追加し、`true`の場合`shell`/`args`から組み立てた単一コマンドライン文字列を`cmd.exe /d /s /c "chcp 65001 > nul && <inner>"`でラップするよう`CommandBuilder`構築を分岐する
- [x] 2.2 `spawn_pty_for_pane`(引数なし版)の呼び出し元は`auto_chcp: false`固定で据え置く
- [x] 2.3 `mcp.rs::open_pane`から、解決済みプロファイルの`auto_chcp`を`spawn_pty_for_pane_with_patterns`へ渡す
- [x] 2.4 単体テスト: コマンドライン組み立てヘルパー(クォート付与)を単体テスト可能な関数として切り出し、スペースを含む引数・含まない引数の両方でテストする

## 3. outputEncoding: pty 出力デコード

- [x] 3.1 `Cargo.toml`に`encoding_rs`を追加する
- [x] 3.2 `profile.rs`に旧`.NET`版`GetEncoding()`相当のマッピング関数(`UTF-8`/`UTF8`→UTF-8、`SHIFT_JIS`/`SHIFT-JIS`→Shift-JIS、それ以外→UTF-8)を追加し単体テストする
- [x] 3.3 pty読み取りループの起動時にプロファイルの`outputEncoding`から`encoding_rs::Decoder`を解決・生成する(`PtyEntry`ではなくreaderスレッド内のローカル状態として保持 - 外部からの参照が不要なため設計を簡略化)
- [x] 3.4 pty読み取りループの`String::from_utf8_lossy(&buf[..n])`を、保持しているデコーダでのストリーミングデコードに置き換える(チャンク境界をまたぐマルチバイト文字が壊れないこと)
- [x] 3.5 `spawn_pty_for_pane_with_patterns`に`output_encoding`引数を追加し、`mcp.rs::open_pane`から解決済みプロファイルの値を渡す

## 4. fontSize: ペイン初期フォントサイズ

- [x] 4.1 `mcp.rs::open_pane`が組み立てる`amm-pane-opened`イベントペイロードにプロファイルの`fontSize`(未設定なら`null`)を追加する
- [x] 4.2 `app.js`の`createPane`が`opts.fontSize`(未指定なら既定13)を受け取り、`new Terminal({..., fontSize: ...})`へ反映するようにする
- [x] 4.3 `amm-pane-opened`リスナーが受け取った`fontSize`を`createPane`の呼び出しへ渡すよう配線する
- [x] 4.4 実機確認(2026-07-27、実機`amm.exe`をCDP接続): `fontSize:20`のプロファイルを起動→初期`term.options.fontSize`が20であることを確認。タイトルバー右クリックの「小」を適用→11に変わることを確認、独立して動作

## 5. sendLineByLine: 行ごと送信

- [x] 5.1 `amm-pane-opened`受信時、`pane.autoSendOnIdle`等と同様に`pane.sendLineByLine`をイベントペイロードからキャプチャする(`mcp.rs::open_pane`のペイロードにも追加)
- [x] 5.2 `sendToPane`を、`pane.sendLineByLine`が真の場合は`filter_text_for_send`適用後のテキストを改行分割し、各行を`pty_write`で順に書き込みつつ行間に80ms待機するよう分岐する
- [x] 5.3 `pane.sendLineByLine=false`(既定)の経路は現状の一括書き込みのまま変更しない
- [x] 5.4 実機確認(2026-07-27): `sendLineByLine:true`のPowerShellプロファイルへ2行(`echo line1\necho line2`)を`sendToPane`経由で送信→送信に84ms(80ms行間待機込み)かかり一括送信ではないことを確認、両行とも実際にシェルへ届いて実行された(ターミナルバッファに両方出力)ことも確認

## 6. promptNewNameOnCommandAdd: コマンド追加時の新規プロファイル作成

- [x] 6.1 Rust側に`add_profile(profile: SessionProfile) -> Result<(), String>`コマンドを新設し、`ProfilesState.file.profiles`へメモリ上追加する(`.amm`への書き込みは行わない)。`invoke_handler`に登録する
- [x] 6.2 `app.js`に新しい名前を尋ねるモーダル(既存の`openModal`/`addModalField`/`addModalActions`基盤を再利用)を実装する。初期値は作業ディレクトリを選択していればそのフォルダ名、なければ元プロファイル名
- [x] 6.3 名前が空、または既存プロファイル(大文字小文字無視)と重複する場合はダイアログを閉じずエラー表示する
- [x] 6.4 `launchProfileFromMenu`に、`profile.selectWorkingDirOnStart`の分岐の後段として`profile.promptNewNameOnCommandAdd`の分岐を追加する: 名前入力→キャンセルなら起動中止→確定なら元プロファイルをクローンし、`name`/`nickname`(既存の`normalizeNickname`で正規化)/`workingDirectory`(選択していれば上書き)/`promptNewNameOnCommandAdd=false`/`selectWorkingDirOnStart=false`/`autoStartCount=0`/`windowGeometry=[]`を設定して`add_profile`で追加し、そのクローンで起動する
- [x] 6.5 実機確認(2026-07-27): `promptNewNameOnCommandAdd:true`(`selectWorkingDirOnStart`なし版で分離確認)のプロファイルで`launchProfileFromMenu`を実行→新しい名前を尋ねるモーダルが表示→名前入力・OK→`add_profile`で複製(`promptNewNameOnCommandAdd:false`に設定済み)が`list_profiles`に反映→その複製が実際に起動され生きたペインになることを確認

## 7. 仕上げ

- [x] 7.1 `cargo build -j 2`が警告なし(新規追加分について)で通ることを確認する
- [x] 7.2 `cargo test -j 2`で新規単体テストを含め全件(既知の不安定テストを除く)passすることを確認する(`mcp::approval_disconnect_tests::releases_promptly_when_reader_already_at_eof`が今回のセッションで単体実行でも4/4回失敗したが、diffで本changeが触れていない範囲(approval.rs関連ロジック)と確認済み。新規追加テスト4件(resolve_output_encoding×2, build_chcp_wrapped_command×2)を含む他88件は全てpass)
- [x] 7.3 `node --check`でapp.jsの構文を確認する
- [x] 7.4 実機で一通り起動確認(既存機能に回帰がないか、特にペイン起動・送信・コマンド管理ダイアログ) - 起動・4ペイン復元・ツールバー表示は確認済み。フォルダ選択/名前入力ダイアログ・chcp/エンコーディングの目視・行ごと送信の挙動はネイティブダイアログ/実際のCLI出力を伴うため自動検証できず、4.4/5.4/6.5として引き続きユーザーによる実機確認待ち
- [x] 7.5 `/check-pr`実行(2026-07-27): ブランチ`claude/dotnet-tauri-feature-audit-jwltx6`→`main`。コミットは`test:`単一フェーズ(4.4/5.4/6.5の実機確認記録のみ、実装自体は既存コミット`7520d68`)。ワークフロー変更なし。副作用ファイル変更なし。TODO/FIXME等なし。バージョン変更なし
