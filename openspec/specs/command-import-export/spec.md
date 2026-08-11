# command-import-export Specification

## Purpose
TBD - created by archiving change establish-baseline-specs. Update Purpose after archive.
## Requirements
### Requirement: エクスポート対象の選択
`CommandManagerDialog` の「エクスポート...」操作は、現在ロード中の全 `SessionProfile` を `ExportProfilesDialog`（`CheckedListBox`、既定で全件チェック済み）に一覧表示し、ユーザーが選択したものだけをエクスポート対象としなければならない (SHALL)。一覧の各行は `{Name}  [{Nickname}]  ({CommandType})  —  {Executable} {Args}` の形式で表示し、「すべて選択」「すべて解除」ボタンを提供する。

#### Scenario: エクスポート対象が0件の場合
- **WHEN** ロード中のプロファイルが0件の状態で「エクスポート...」を実行する
- **THEN** 「エクスポートするコマンドがありません。」という情報ダイアログを表示し処理を中断する

#### Scenario: 選択なしでOK
- **WHEN** `ExportProfilesDialog` で全項目のチェックを外して OK を押す
- **THEN** 「コマンドが選択されていません。」という情報ダイアログを表示し、保存ファイルダイアログへは進まない

### Requirement: .ammprofiles 形式での保存
選択されたプロファイルは `ProfilesExportFile`（`version: 1`, `profiles: SessionProfile[]`）としてJSONシリアライズし、`SaveFileDialog`（フィルタ `*.ammprofiles`/`*.amm`/`*.*`、既定ファイル名 `profiles-export-{yyyy-MM-dd}.ammprofiles`）で選択したパスへ `AtomicFileWriter` 経由で書き込まなければならない (SHALL)。

#### Scenario: 保存成功
- **WHEN** プロファイルを選択し保存先ファイルを指定してエクスポートを実行する
- **THEN** `{"version":1,"profiles":[...]}` 形式のJSONが指定パスへ書き込まれ、「{件数} 件のコマンドをエクスポートしました。」という完了ダイアログが表示される

#### Scenario: 書き込み失敗時のエラー表示
- **WHEN** ファイル書き込み中に例外が発生する
- **THEN** 「エクスポートに失敗しました。」というエラーダイアログに例外メッセージを添えて表示する

### Requirement: インポートファイルの読み込みと後方互換パース
「インポート...」操作は `OpenFileDialog`（フィルタ `*.ammprofiles`/`*.amm`/`*.*`）で選択したファイルをJSONとして読み込み、ルート要素が `profiles` プロパティを持つオブジェクトの場合はその配列を、ルート自体が配列の場合はそれをそのまま `SessionProfile` 配列としてデシリアライズしなければならない (SHALL)。どちらでもない場合は例外を送出しなければならない (MUST)。デシリアライズ後、各プロファイルに対して `MigrateLegacyFields()` によるレガシーフィールド移行を適用する。

#### Scenario: `.ammprofiles` 形式（{profiles:[...]}）の読み込み
- **WHEN** `{"version":1,"profiles":[...]}` 形式のファイルをインポートする
- **THEN** `profiles` 配列がデシリアライズされ、各エントリに `MigrateLegacyFields` が適用される

#### Scenario: 素の配列形式の読み込み（互換）
- **WHEN** ルートがそのまま `SessionProfile` の配列であるJSONファイルをインポートする
- **THEN** ルート配列がそのまま `SessionProfile` リストとしてデシリアライズされる

#### Scenario: 不正な形式でのパース失敗
- **WHEN** ルートが `profiles` プロパティを持たないオブジェクトである（配列でもない）
- **THEN** `InvalidDataException` を送出し、呼び出し元は「ファイルの読み込みに失敗しました。」というエラーダイアログを表示して中断する

#### Scenario: インポート対象が0件
- **WHEN** ファイルの `profiles` が空配列である
- **THEN** 「インポートするコマンドが見つかりませんでした。」という情報ダイアログを表示し中断する

### Requirement: 重複検出と選択・競合解決ダイアログ
`ImportProfilesDialog` は読み込んだ全プロファイルを `CheckedListBox`（既定で全件チェック済み）に一覧表示し、`Nickname` が既存プロファイルと大文字小文字を無視して一致する行には `⚠ 重複` マークを付けなければならない (SHALL)。競合解決方針は「スキップ（既定）」「リネーム」「上書き」のラジオボタンから選択させる。

#### Scenario: 重複マークの表示
- **WHEN** インポートファイル内のプロファイルの `Nickname` が現在ロード中のいずれかの `Nickname` と大文字小文字を無視して一致する
- **THEN** 一覧のその行に `⚠ 重複` が付加される

### Requirement: インポート実行時の競合解決
OK 押下後、選択された各プロファイルについて既存プロファイルと `Nickname`（大文字小文字無視）が一致する場合、選択中の競合解決方針を適用しなければならない (SHALL): 「スキップ」は追加せずスキップ件数を加算、「リネーム」は `JsonClone` した上で `Nickname` に `_2`, `_3`, ... の連番サフィックスを付けて新規追加、「上書き」は既存エントリの値を差し替える。一致しないプロファイルは常にクローンして新規追加する。実行後、追加/上書き/スキップの各件数を含む完了ダイアログを表示する。

#### Scenario: スキップモードでの重複処理
- **WHEN** 競合解決方針が「スキップ」で、選択したプロファイルの `Nickname` が既存と一致する
- **THEN** そのプロファイルは追加されずスキップ件数としてカウントされる

#### Scenario: リネームモードでの重複処理
- **WHEN** 競合解決方針が「リネーム」で、`Nickname` が `foo` の既存プロファイルと衝突する
- **THEN** インポート側のクローンの `Nickname` が `foo_2`（さらに衝突する場合は `foo_3`, ...）に変更されて新規追加される

#### Scenario: 上書きモードでの重複処理
- **WHEN** 競合解決方針が「上書き」で、`Nickname` が一致する既存エントリがある
- **THEN** 既存エントリの内容がインポート側の値で置き換えられる

### Requirement: 反映はメモリ上のみ、ファイルへの永続化は明示保存に委ねる
`CommandManagerDialog` のインポート・エクスポート操作は、ダイアログが保持する `SessionProfile` の一覧（メモリ上の `_entries` / 呼び出し元の `_profiles` 配列）のみを更新し、`.amm` ファイルへの書き込みは行わない (MUST NOT)。`.amm` ファイルへの反映は、`CommandManagerDialog` を OK で閉じた後にユーザーが別途「ファイル → 上書き保存」を実行したときにのみ行われる。

#### Scenario: インポート直後は.ammファイルへ書き込まれない
- **WHEN** インポートを実行し `CommandManagerDialog` を OK で閉じる
- **THEN** アプリ内のプロファイル一覧（`_profiles`）は更新されるが、`.amm` ファイル自体はまだ変更されていない

#### Scenario: 上書き保存で初めて永続化される
- **WHEN** インポート後、ユーザーが「ファイル → 上書き保存」を実行する
- **THEN** その時点で初めてインポートされたプロファイルが `.amm` ファイルへ書き込まれる

