## MODIFIED Requirements

### Requirement: プロファイルファイルスキーマ
`.amm` 拡張子のプロファイルファイル（既定ファイル名 `profiles.amm`）は `{ "profiles": [...], "mcpServers": [...] }` 形式の JSON でなければならず (SHALL)、各 profile エントリは `SessionProfile` の全フィールド (name / commandType / executable / args / resumeOnStart / newlineMode / outputEncoding / autoChcp / waitPatterns / workingDirectory / ctrlCCopyOnSelection / initialCommands / sessionLog / chatRecord / chatRecordTailChars / stats / theme / closeOnExit / autoStartCount / closeProhibited / collapseBlankLines / commentPrefixes / windowGeometry / nickname / sendLineByLine / useBracketedPaste / selectWorkingDirOnStart / promptNewNameOnCommandAdd / quickPrompts / autoSendOnIdle / fontSize) を、未指定時は既定値で解決可能でなければならない。`theme` は文字列ではなく `{ [key: string]: string }` 形式のオブジェクト（例: `{"background":"#000000","foreground":"#00ff00"}`）でなければならない (SHALL) — 実装は `theme` を単一プロファイルの型不整合のためにファイル全体の読み込みを失敗させてはならない (SHALL NOT)。1件のプロファイルの型エラーは当該プロファイルのみ既定値へフォールバックし、他のプロファイルの読み込みに影響してはならない (SHALL NOT)。

#### Scenario: 既定値でのロード
- **WHEN** JSON に v2 拡張キー (autoStartCount 等) が存在しない旧形式ファイルをロードする
- **THEN** `autoStartCount=0` / `closeProhibited=false` / `collapseBlankLines=true` / `commentPrefixes=["'", "//"]` / `windowGeometry=[]` / `selectWorkingDirOnStart=false` / `promptNewNameOnCommandAdd=false` / `quickPrompts=[]` / `autoChcp=true` / `ctrlCCopyOnSelection=true` / `closeOnExit=true` / `stats=true` として動作する

#### Scenario: 不正 JSON のエラーハンドリング
- **WHEN** プロファイルファイルの JSON 構文が壊れている
- **THEN** `InvalidDataException` をファイルパスを含むメッセージで送出し、呼び出し元 (GUI) は既定の CMD プロファイル 1 個へフォールバックしたうえで警告ダイアログを表示する

#### Scenario: 存在しないファイル
- **WHEN** 指定されたプロファイルファイルが存在しない
- **THEN** 例外を出さずに既定の CMD プロファイル 1 個 (`waitPatterns=["[>]\\s*$"]`) を返す

#### Scenario: 単一プロファイルの型エラーは他プロファイルに波及しない
- **WHEN** `profiles` 配列中の1件が `theme` に文字列（オブジェクトでない値）を持つなど型不整合を含む
- **THEN** 当該プロファイルのみ `theme` を既定値（未設定）として扱い、配列内の他の全プロファイルは正常にロードされる（ファイル全体のロード失敗にはならない）

## ADDED Requirements

### Requirement: クローズ禁止設定によるペインクローズガード
`closeProhibited=true` のプロファイルから起動されたペインは、ユーザー操作によるクローズ（✕ボタン・システムメニューの「閉じる」等）を、Shift キーを押しながらの操作を除きブロックしなければならない (SHALL)。MCP `pane/close` の `force=true` 指定時、およびプロセス自身の終了（`Stopped` 遷移）によるクローズはこのガードの対象外とする。

#### Scenario: 通常クローズはブロックされる
- **WHEN** `closeProhibited=true` のペインを Shift キーなしでクローズ操作する
- **THEN** クローズは中止され、設定で保護されている旨のメッセージが表示される

#### Scenario: Shift 押下でのクローズは許可される
- **WHEN** `closeProhibited=true` のペインを Shift キーを押しながらクローズ操作する
- **THEN** ガードは適用されず通常のクローズ確認フロー（実行中セッション確認等）へ進む

### Requirement: コマンド追加・編集ダイアログでの名前重複時の入力保持
コマンド管理ダイアログの追加・編集サブフォームで、確定操作時に既存プロファイルと名前が重複していた場合、amm はエラーを表示した上で、入力済みのドラフト内容を保持したまま同じサブフォームへ留まらなければならない (SHALL)。ユーザーが手動でキャンセルしない限り入力内容を破棄してはならない (SHALL NOT)。

#### Scenario: 重複名でのエラー後もドラフトが残る
- **WHEN** 既存プロファイルと同名で追加・編集を確定しようとしてエラーになる
- **THEN** サブフォームは開いたままとなり、それまで入力していた全フィールドの内容がそのまま表示され続ける
