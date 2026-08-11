## Why

`migrate-to-tauri`のTauri版コマンド編集ダイアログ(`openCommandTemplateDialog`)は`profile-schema`の全フィールドを表示・編集できるが、そのうち6フィールド(`promptNewNameOnCommandAdd`/`useBracketedPaste`/`sendLineByLine`/`autoChcp`/`outputEncoding`/`fontSize`)は**編集はできるが実行時に一切読まれておらず、値を変えても挙動が変わらない**ことがユーザー報告と実装調査(grep で実使用箇所ゼロを確認)で判明した。加えて`newlineMode`は旧`.NET`版時点から実使用箇所が無い死んだフィールドと判明した。

このうち`useBracketedPaste`と`newlineMode`は実装せずフィールド自体を削除し、残り5件(`promptNewNameOnCommandAdd`/`sendLineByLine`/`autoChcp`/`outputEncoding`/`fontSize`)は旧`.NET`版の対応実装(`TerminalChildForm.cs`/`ConPtyWrapper.cs`/`SessionProfile.cs`/`MdiParentForm.cs`)を参照してTauri版に実装する。

## What Changes

- **BREAKING**: `useBracketedPaste`フィールドを`SessionProfile`スキーマ・編集ダイアログ・コマンドタイプ別プリセット(ClaudeCode/Codex/CopilotCli/Gemini)から削除する。bracketed paste送信自体を実装しない(Copilot CLI等のInk系TUIでの取りこぼしリスクは既知の制約として残す)
- **BREAKING**: `newlineMode`フィールドを`SessionProfile`スキーマ・編集ダイアログ・プリセットから削除する(旧版でも未使用の死んだ設定)
- 「コマンド追加時に新しい名前を入力する」(`promptNewNameOnCommandAdd`)を実装: 「コマンド ▶」メニューからの起動時、対象プロファイルが`promptNewNameOnCommandAdd=true`なら、(`selectWorkingDirOnStart`も真なら作業ディレクトリ選択の後に)新しい名前の入力を求め、元プロファイルをクローンして新規プロファイルとして追加(メモリ上、永続化は既存の「上書き保存」に委ねる)、そのクローンで起動する
- 「行ごとに個別の改行を送る」(`sendLineByLine`)を実装: 複数行テキストを1行ずつ`\r`付きで一定間隔を空けて送信するモードを、有効なプロファイルの送信経路に追加する
- 「AutoChcp」を実装: 起動コマンドラインを`chcp 65001`でラップしてUTF-8コードページを強制する処理をペイン起動経路に追加する
- 「出力エンコーディング」(`outputEncoding`)を実装: UTF-8以外(Shift-JIS等)を指定したプロファイルのpty出力デコードに反映する
- 「フォントサイズ」(`fontSize`)を実装: ペイン生成時、プロファイルの既定フォントサイズをxterm.jsの初期値に反映する(現状は常に13px固定)

## Capabilities

### New Capabilities
(なし)

### Modified Capabilities
- `profile-schema`: フィールド定義(useBracketedPaste/newlineMode削除)と、起動時・送信時・出力デコード時の各requirementを変更する

## Impact

- `src/apps/Amm/src-tauri/src/profile.rs`: `SessionProfile`構造体からuseBracketedPaste/newlineModeを削除、chcp/エンコーディング解決・クローン生成ヘルパーを追加
- `src/apps/Amm/src-tauri/src/lib.rs` / `mcp.rs`: ペイン起動時のchcpラップ・出力デコード・行ごと送信コマンドを追加
- `src/apps/Amm/public/app.js`: 編集ダイアログからuseBracketedPaste/newlineModeのUIを削除、コマンド追加時クローン生成フロー・行ごと送信・フォントサイズ初期適用を追加
- `src/apps/Amm/public/index.html`: 影響なし見込み(既存モーダル基盤を再利用)
- 旧`.NET`版(`src/apps/Amm/`)は変更しない(参照専用)
- 既存の`profiles.amm`データにある`useBracketedPaste`/`newlineMode`キーは、Rust側`#[serde(flatten)] extra`に吸収され後方互換で残る(読み込み時にエラーにならない)想定
