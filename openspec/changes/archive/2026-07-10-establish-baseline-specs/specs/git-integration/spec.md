## ADDED Requirements

### Requirement: MDI 子ウィンドウ個別クローズ時の Git ガード
ユーザーが MDI 子ウィンドウ（`TerminalChildForm`）を個別にクローズ操作（`CloseReason.UserClosing`）した場合、amm は当該ウィンドウの起動作業ディレクトリ（`StartupWorkingDirectory`）が Git リポジトリ内かどうかを判定し、リポジトリ内であれば `GitGuard.CheckAndPrompt` によって未コミット・未プッシュの確認を行わなければならない (SHALL)。親フォーム経由の一括終了（`CloseReason.MdiFormClosing`）ではこの個別ガードをスキップし、`MdiParentForm` 側の一括処理に委ねる。

#### Scenario: 個別クローズでリポジトリ内の変更がある場合
- **WHEN** ユーザーが Git リポジトリ内で起動された MDI 子ウィンドウを閉じようとする（`UserClosing`）
- **THEN** amm は `GitHelper.GetRepoRoot` でリポジトリルートを特定し、`GitGuard.CheckAndPrompt` を呼び出して未コミット・未プッシュの確認フローを実行する
- **AND** 確認フローでユーザーがキャンセルを選んだ場合、`FormClosingEventArgs.Cancel` を true にしてクローズを中止する

#### Scenario: 作業ディレクトリが Git リポジトリ外
- **WHEN** MDI 子ウィンドウの起動ディレクトリが Git 管理下にない、またはディレクトリが存在しない
- **THEN** `GitHelper.GetRepoRoot` は null を返し、amm は確認ダイアログを一切表示せずにクローズを続行する

### Requirement: アプリケーション終了時の Git ガード一括処理
amm 全体を終了する際（`MdiParentForm` の `OnFormClosing`）、amm は全ての未破棄 MDI 子ウィンドウの起動作業ディレクトリを収集し、`GitHelper.GetRepoRoot` でリポジトリ単位に重複排除（大文字小文字を区別しない）した上で、`GitGuard.CheckAndPromptDirs` によりリポジトリごとに 1 回だけ未コミット・未プッシュ確認を行わなければならない (SHALL)。

#### Scenario: 複数の MDI が同一リポジトリを指している
- **WHEN** 2 つ以上の MDI 子ウィンドウが同じ Git リポジトリ内の異なるサブディレクトリで起動されている状態でアプリを終了する
- **THEN** amm はそれらを 1 つのリポジトリルートに集約し、確認ダイアログを重複表示せず 1 回だけ表示する

#### Scenario: いずれかのリポジトリでユーザーがキャンセル
- **WHEN** 複数リポジトリの確認を順に処理している最中に、ユーザーがいずれか 1 つでキャンセルを選択する
- **THEN** amm はそれ以降のリポジトリの確認を打ち切り、アプリケーション終了処理全体を中止する（`e.Cancel = true`）

### Requirement: 未コミット変更のコミットダイアログ
Git リポジトリに `git status --short` で検出される未コミット変更がある場合、amm は `GitCommitDialog` を表示し、リポジトリルートパスと status 出力をユーザーに提示し、コミットメッセージ入力欄（既定値 `"WIP: 作業を保存"`）を伴う「コミット」「スキップ」「閉じない」の 3 択を提供しなければならない (SHALL)。

#### Scenario: コミットを選択
- **WHEN** ユーザーがコミットメッセージを入力（または既定値のまま）して「コミット (&C)」ボタンを押す
- **THEN** amm は `GitHelper.AddAllAndCommit` を呼び出し `git add -A` に続けて `git commit -m <message>` を実行する
- **AND** コミットメッセージが空白のみの場合はダイアログを閉じずに警告メッセージボックスを表示し、`DialogResult` を `None` に戻して入力を促す

#### Scenario: スキップを選択
- **WHEN** ユーザーが「スキップ (&S)」ボタンを押す
- **THEN** amm は `DialogResult.Ignore` を返し、コミットを実行せずに未プッシュ確認ステップへ進む（クローズ処理自体は継続する）

#### Scenario: 閉じないを選択
- **WHEN** ユーザーが「閉じない (&X)」ボタンを押す、またはダイアログの `CancelButton`（Esc 相当）を操作する
- **THEN** amm は `DialogResult.Cancel` を返し、`GitGuard` はウィンドウ／アプリのクローズ処理全体を中止する

#### Scenario: git commit コマンド自体が失敗する
- **WHEN** `GitHelper.AddAllAndCommit` の `git commit` 実行が 0 以外の終了コードを返す
- **THEN** amm はコミット失敗を示す警告メッセージボックス（stderr 内容を含む）を表示するが、クローズ処理そのものは中止しない

### Requirement: 未プッシュコミットの確認とプッシュ
未コミット確認（コミット実行またはスキップ）の後、リモートが 1 件以上設定されている（`git remote` の出力が空でない）リポジトリについて、amm は `git log @{u}..HEAD --oneline` の行数から未プッシュコミット数を算出し、1 件以上あれば「今すぐプッシュしますか？」という Yes/No/Cancel 確認を行わなければならない (SHALL)。

#### Scenario: リモート未設定またはアップストリーム未設定
- **WHEN** リポジトリにリモートが 1 つも設定されていない、または `@{u}..HEAD` の解決に失敗する（upstream 未設定）
- **THEN** amm は未プッシュ確認ダイアログを表示せず、そのままクローズ処理を継続する（戻り値 false）

#### Scenario: 未プッシュコミットがあり Yes を選択
- **WHEN** 未プッシュコミットが 1 件以上あり、ユーザーが確認ダイアログで「はい」を選ぶ
- **THEN** amm は `GitHelper.Push` で `git push` を実行し、失敗時は stdout/stderr を含む警告メッセージボックスを表示する（成功・失敗いずれの場合もクローズ処理は継続する）

#### Scenario: 未プッシュ確認で Cancel を選択
- **WHEN** ユーザーが未プッシュ確認ダイアログで「キャンセル」を選ぶ
- **THEN** amm はクローズ処理（ウィンドウまたはアプリ終了）を中止する

### Requirement: Git コマンド実行の失敗耐性とタイムアウト
`GitHelper` の全ての git 呼び出しは `Process` 経由で実行され、リポジトリルート判定は 3 秒、status/remote 系は 3〜5 秒、commit は 10 秒、push は 30 秒のタイムアウトを持ち、プロセス起動失敗や git 未検出などの例外はキャッチして安全な既定値（空文字・0・false）にフォールバックしなければならない (SHALL)。

#### Scenario: 作業ディレクトリが存在しない
- **WHEN** `Run` に渡された作業ディレクトリが存在しない
- **THEN** amm はプロセスを起動せずに `(exit: -1, "", "")` を返し、呼び出し元（`GetRepoRoot` 等）は null または空文字・0・false相当の安全な結果として扱う

#### Scenario: git 実行ファイルが見つからない
- **WHEN** PATH 上に `git` コマンドが存在せず `Process.Start` が例外を送出する、または `null` を返す
- **THEN** `Run` は例外を捕捉し `(exit: -1, "", "")` を返す（アプリケーションはクラッシュしない）
