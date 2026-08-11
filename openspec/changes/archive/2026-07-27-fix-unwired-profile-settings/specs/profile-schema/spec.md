## MODIFIED Requirements

### Requirement: プロファイルファイルスキーマ
`.amm` 拡張子のプロファイルファイル（既定ファイル名 `profiles.amm`）は `{ "profiles": [...], "mcpServers": [...] }` 形式の JSON でなければならず (SHALL)、各 profile エントリは `SessionProfile` の全フィールド (name / commandType / executable / args / resumeOnStart / outputEncoding / autoChcp / waitPatterns / workingDirectory / ctrlCCopyOnSelection / initialCommands / sessionLog / chatRecord / chatRecordTailChars / stats / theme / closeOnExit / autoStartCount / closeProhibited / collapseBlankLines / commentPrefixes / windowGeometry / nickname / sendLineByLine / selectWorkingDirOnStart / promptNewNameOnCommandAdd / quickPrompts / autoSendOnIdle / fontSize) を、未指定時は既定値で解決可能でなければならない。`newlineMode` / `useBracketedPaste` は廃止済みフィールドであり、読み込み時に存在しても無視しなければならない (SHALL)。

#### Scenario: 既定値でのロード
- **WHEN** JSON に v2 拡張キー (autoStartCount 等) が存在しない旧形式ファイルをロードする
- **THEN** `autoStartCount=0` / `closeProhibited=false` / `collapseBlankLines=true` / `commentPrefixes=["'", "//"]` / `windowGeometry=[]` / `selectWorkingDirOnStart=false` / `promptNewNameOnCommandAdd=false` / `quickPrompts=[]` として動作する

#### Scenario: 不正 JSON のエラーハンドリング
- **WHEN** プロファイルファイルの JSON 構文が壊れている
- **THEN** `InvalidDataException` をファイルパスを含むメッセージで送出し、呼び出し元 (GUI) は既定の CMD プロファイル 1 個へフォールバックしたうえで警告ダイアログを表示する

#### Scenario: 存在しないファイル
- **WHEN** 指定されたプロファイルファイルが存在しない
- **THEN** 例外を出さずに既定の CMD プロファイル 1 個 (`waitPatterns=["[>]\\s*$"]`) を返す

#### Scenario: 廃止フィールドを含むファイルの読み込み
- **WHEN** JSON に `newlineMode` または `useBracketedPaste` キーを含むプロファイルエントリをロードする
- **THEN** エラーにはならず、両キーの値は単に読み捨てられる (以後の再保存でも書き戻されない)

### Requirement: コマンド追加テンプレートと per-command 編集ダイアログ
「コマンド追加」操作は静的テンプレート一覧から選択でき、選択後は全フィールドを自由編集できなければならない (SHALL)。

#### Scenario: テンプレート一覧
- **WHEN** コマンド追加ダイアログ (isAddMode=true) を開く
- **THEN** CMD / PowerShell / Claude Code / Codex / Copilot / (空テンプレート) の順でテンプレートを選択でき、選択した瞬間に実行ファイル・引数・waitPatterns・AutoChcp・出力エンコーディング・作業ディレクトリ選択・コマンド追加時の新規名入力・クイック送信初期値などのプリセット値がフォームへ流し込まれる

#### Scenario: per-window 設定ダイアログ
- **WHEN** システムメニュー「AMM 設定…」を開く
- **THEN** `CloseProhibited` / `CollapseBlankLines` / `CommentPrefixes` (カンマ区切りテキスト) の 3 項目を編集でき、確定後は AMM ファイルへ書き戻すかどうかを確認する

## REMOVED Requirements

### Requirement: bracketed paste 送信設定
**Reason**: `useBracketedPaste` は宣言・編集はできるが実行時に一切参照されておらず、Copilot CLI 等の Ink 系 TUI 向けの bracketed paste 送信自体が実装されていなかった。実装せず設定ごと削除する方針とした。
**Migration**: 対応する代替機能はない。bracketed paste が必要な送信は今後もフィルタ済みテキストの単純書き込みで行われる。

### Requirement: 改行モード設定
**Reason**: `newlineMode` は Tauri 版だけでなく旧 `.NET` 版でもどこからも参照されない死んだフィールドと判明した (実装調査でいずれの版でも実使用箇所ゼロを確認)。
**Migration**: 対応する代替機能はない。送信時の改行は常に `\r` 単独付加のまま変更しない。

## ADDED Requirements

### Requirement: コマンド追加時の新規プロファイル作成 (promptNewNameOnCommandAdd)
`promptNewNameOnCommandAdd=true` のプロファイルを「コマンド」メニューから起動するとき、GUI は新しい名前の入力を求め、元プロファイルを複製した新規プロファイルとして追加してから、その複製で起動しなければならない (SHALL)。`selectWorkingDirOnStart` も真の場合は、作業ディレクトリ選択(次項)を名前入力より先に行う。

#### Scenario: 新規名の入力とクローン起動
- **WHEN** `promptNewNameOnCommandAdd=true` のプロファイルを「コマンド」メニューから選択する
- **THEN** 新しい名前の入力ダイアログを表示し、確定すると元プロファイルを複製した新規プロファイルを作成する
- **AND** 複製のニックネームは新しい名前から正規化して設定し、`promptNewNameOnCommandAdd`/`selectWorkingDirOnStart` は `false`、`autoStartCount` は `0`、`windowGeometry` は空にする
- **AND** 複製は現在ロード中のプロファイル一覧にメモリ上追加され (`.amm` ファイルへの書き込みは「上書き保存」の実行まで行わない)、その複製で新規ペインを起動する

#### Scenario: 作業ディレクトリ選択との併用時の順序
- **WHEN** `promptNewNameOnCommandAdd=true` かつ `selectWorkingDirOnStart=true` のプロファイルを起動する
- **THEN** 作業ディレクトリ選択ダイアログを先に表示し、選択したフォルダの名前を新規名の初期値として提案する
- **AND** 作業ディレクトリ選択をキャンセルした場合は名前入力へ進まず起動全体を中止する

#### Scenario: 名前の重複・空入力
- **WHEN** 入力した名前が空、または既存プロファイルの名前と大文字小文字を無視して一致する
- **THEN** エラーを表示しダイアログを維持する (入力済みの内容は破棄しない)

#### Scenario: 名前入力のキャンセル
- **WHEN** 名前入力ダイアログをキャンセルする
- **THEN** 新規プロファイルの作成・起動のいずれも行わない

### Requirement: 起動コマンドのコードページ強制 (autoChcp)
`autoChcp=true` のプロファイルを起動するとき、実行コマンドは `chcp 65001` を実行してから本来のコマンドを起動する形にラップしなければならない (SHALL)。

#### Scenario: UTF-8 コードページの強制
- **WHEN** `autoChcp=true` のプロファイルでペインを起動する
- **THEN** 実際に起動されるプロセスは `chcp 65001` を実行した後に元のコマンド・引数・作業ディレクトリで起動される
- **AND** `autoChcp=false` のプロファイルはラップせず元のコマンドをそのまま起動する

### Requirement: pty 出力のエンコーディング解決 (outputEncoding)
プロファイルの `outputEncoding` が `UTF-8`/`UTF8` 以外を指定する場合、GUI はその文字コードで pty からの出力バイト列をデコードしなければならない (SHALL)。

#### Scenario: Shift-JIS 指定時のデコード
- **WHEN** `outputEncoding="Shift_JIS"` のプロファイルで起動したペインが Shift-JIS でエンコードされたテキストを出力する
- **THEN** 出力は文字化けせず正しくデコードされてターミナルに表示される

#### Scenario: 未指定・不明値のフォールバック
- **WHEN** `outputEncoding` が未指定、または `UTF-8`/`UTF8`/`SHIFT_JIS`/`SHIFT-JIS` のいずれでもない値
- **THEN** UTF-8 としてデコードする

#### Scenario: マルチバイト文字がチャンク境界をまたぐ場合
- **WHEN** pty からの読み取りチャンクの境界がマルチバイト文字の途中で切れる
- **THEN** 次のチャンクと結合して正しくデコードし、文字化けや欠落を起こさない

### Requirement: 行ごと送信 (sendLineByLine)
`sendLineByLine=true` のプロファイルで起動したペインへ複数行テキストを送信するとき、GUI は1行ずつ確定 Enter を挟んで順に送信しなければならない (SHALL)。

#### Scenario: 複数行を1行ずつ遅延送信
- **WHEN** `sendLineByLine=true` のペインへ改行を含む複数行テキストを送信する
- **THEN** 各行を `\r` 付きで順番に書き込み、行の間に短い遅延を挟む
- **AND** `sendLineByLine=false` (既定) のペインは従来通り全文を一括で書き込む

### Requirement: ペイン初期フォントサイズ (fontSize)
プロファイルに `fontSize` が設定されている場合、そのプロファイルから起動したペインの初期フォントサイズに反映しなければならない (SHALL)。

#### Scenario: プロファイル既定フォントサイズの適用
- **WHEN** `fontSize` を設定したプロファイルから新規ペインを起動する
- **THEN** そのペインのターミナルは指定されたフォントサイズで初期表示される

#### Scenario: 未設定時の既定値
- **WHEN** `fontSize` が未設定のプロファイルから起動する、またはプロファイルに紐付かないペインを作成する
- **THEN** 既定のフォントサイズ (13px) で表示される

#### Scenario: セッション中の変更との独立性
- **WHEN** ペイン作成後にタイトルバー右クリックメニューからフォントサイズを変更する
- **THEN** その変更はこのセッションのみに適用され、プロファイルの `fontSize` 設定自体は変更しない
