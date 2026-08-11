## ADDED Requirements

### Requirement: プロファイルファイルスキーマ
`.amm` 拡張子のプロファイルファイル（既定ファイル名 `profiles.amm`）は `{ "profiles": [...], "mcpServers": [...] }` 形式の JSON でなければならず (SHALL)、各 profile エントリは `SessionProfile` の全フィールド (name / commandType / executable / args / resumeOnStart / newlineMode / outputEncoding / autoChcp / waitPatterns / workingDirectory / ctrlCCopyOnSelection / initialCommands / sessionLog / chatRecord / chatRecordTailChars / stats / theme / closeOnExit / autoStartCount / closeProhibited / collapseBlankLines / commentPrefixes / windowGeometry / nickname / sendLineByLine / useBracketedPaste / selectWorkingDirOnStart / promptNewNameOnCommandAdd / quickPrompts / autoSendOnIdle / fontSize) を、未指定時は既定値で解決可能でなければならない。

#### Scenario: 既定値でのロード
- **WHEN** JSON に v2 拡張キー (autoStartCount 等) が存在しない旧形式ファイルをロードする
- **THEN** `autoStartCount=0` / `closeProhibited=false` / `collapseBlankLines=true` / `commentPrefixes=["'", "//"]` / `windowGeometry=[]` / `selectWorkingDirOnStart=false` / `promptNewNameOnCommandAdd=false` / `quickPrompts=[]` として動作する

#### Scenario: 不正 JSON のエラーハンドリング
- **WHEN** プロファイルファイルの JSON 構文が壊れている
- **THEN** `InvalidDataException` をファイルパスを含むメッセージで送出し、呼び出し元 (GUI) は既定の CMD プロファイル 1 個へフォールバックしたうえで警告ダイアログを表示する

#### Scenario: 存在しないファイル
- **WHEN** 指定されたプロファイルファイルが存在しない
- **THEN** 例外を出さずに既定の CMD プロファイル 1 個 (`waitPatterns=["[>]\\s*$"]`) を返す

### Requirement: プロファイルファイルの解決順序
起動時に読み込むプロファイルファイルのパスは、コマンドライン引数を最優先し、次に実行ファイルと同じディレクトリの固定ファイル名で解決しなければならない (SHALL)。

#### Scenario: 位置引数指定
- **WHEN** `amm.exe <path>` のように位置引数でファイルパスを 1 つ指定する
- **THEN** 相対パスは起動時カレントディレクトリを基準に絶対化し、そのパスを `HasExplicitFile=true` として使用する

#### Scenario: 既定パス
- **WHEN** 引数を指定せずに起動する
- **THEN** 実行ファイルと同じディレクトリ (`AppPaths.BaseDirectory`、プロセスのメインモジュールパスから解決) の `profiles.amm` を使用する (`profiles.json` という代替ファイル名は探索しない)
- **AND** 位置引数を 2 個以上指定した場合、または `--` で始まる未知のオプションを指定した場合は `ArgumentException` を送出する

### Requirement: プロファイルファイルのホットリロード
実行中に監視対象のプロファイルファイルが変更されたとき、GUI はプロセス再起動なしに設定を再読込しなければならない (SHALL)。ただし既存の起動済み MDI 子には影響しない。

#### Scenario: 外部エディタでの保存
- **WHEN** `FileSystemWatcher` がプロファイルファイルの `Changed` / `Created` / `Renamed` を検知する
- **THEN** 300ms のデバウンス後に再読込し (多くのエディタが save-temp-rename で複数イベントを発火するため)、以後新規に起動する MDI 子にのみ新しい設定が反映される
- **AND** 再読込中に JSON が壊れていた場合は例外を握りつぶし現在の設定を維持する

### Requirement: ウィンドウ位置・名前・作業ディレクトリの記憶 (windowGeometry)
`windowGeometry` は同一プロファイルの起動 N 番目 (1 始まり、同プロファイルの生存数+1 を index として参照) の MDI に適用する位置・サイズ・表示名・最大化状態・作業ディレクトリを保持できなければならない (SHALL)。

#### Scenario: index の欠落は最大化
- **WHEN** 現在の起動順に対応する `index` のエントリが存在しない、またはエントリの `w`/`h` が 0
- **THEN** 位置・サイズは適用されず、そのウィンドウは最大化 (既定の 1 個目起動時の挙動) または通常の自動配置ロジックに委ねられる

#### Scenario: 全閉じ後のインデックスリセット
- **WHEN** あるプロファイルの MDI 子を全て閉じてから再度同じプロファイルを起動する
- **THEN** カウンタは 1 から数え直され、穴埋め (欠番の再利用) は行われない

#### Scenario: name-only / maximized エントリ
- **WHEN** エントリが `w=0, h=0` かつ `name` または `maximized=true` のみを持つ
- **THEN** 位置・サイズの復元は行わないが、表示名の適用と最大化状態の復元は独立して行う

### Requirement: 旧フィールドの後方互換マイグレーション
プロファイルロード時、旧フィールド名・旧既定値で書かれたファイルは新フィールドへ 1 度だけ移行し、以後の再シリアライズで旧キーを書き戻してはならない (SHALL NOT)。

#### Scenario: promptRenameOnStart の移行
- **WHEN** JSON に旧キー `promptRenameOnStart: true` が含まれる
- **THEN** `PromptNewNameOnCommandAdd` に値をコピーし、`ExtraProperties` から `promptRenameOnStart` を削除する (以後の Serialize 結果に `promptRenameOnStart` は出現しない)

#### Scenario: commentPrefixes 旧既定の移行
- **WHEN** `commentPrefixes` が旧既定値 `["'", "//", "#"]` と完全一致する
- **THEN** 新既定 `["'", "//"]` へ書き換える (`#` は Markdown 見出し `#`/`##` と衝突するため既定から除外。ユーザーが意図して `#` を含む別構成にした場合は変更しない)

#### Scenario: commandType の推測補完
- **WHEN** `commandType` が未設定 (既定 `Other`) の旧ファイルをロードする
- **THEN** `nickname` (claude/codex/copilot) を最優先に、次に `executable`/`args` のトークン一致 (パス部分一致は除外) で `ClaudeCode`/`Codex`/`CopilotCli`/`PowerShell`/`Cmd` を推測して設定する

### Requirement: 起動コマンドラインの解決とセキュリティ
実行ファイル名の解決は、表示・ログ用の文字列と、実際に `CreateProcess` へ渡す文字列とで異なる規則を適用しなければならない (SHALL)。

#### Scenario: 表示用 CommandLine
- **WHEN** `SessionProfile.CommandLine` を参照する
- **THEN** 実行ファイル名は解決もクォートもされない環境変数展開後の生文字列を返す (人が読む用途)

#### Scenario: 起動用 BuildLaunchCommandLine
- **WHEN** `BuildLaunchCommandLine()` を呼ぶ
- **THEN** 裸の実行ファイル名 (パス区切りを含まない) は System32 を優先した安全な PATH 探索で絶対パスへ解決し (カレント/相対ディレクトリ由来の PATH 成分は探索対象から除外し、実行ファイルハイジャックを防ぐ)、空白を含む解決後パスはダブルクォートで囲む
- **AND** PATH 上に見つからない場合は元の文字列のままフォールバックする (従来動作を壊さない)

### Requirement: 送信前のテキスト整形 (per-command)
マルチライン送信時、`collapseBlankLines` と `commentPrefixes` に基づき送信対象行を絞り込まなければならない (SHALL)。

#### Scenario: 連続空行の縮約とコメント除去
- **WHEN** `CollapseBlankLines=true` (既定) かつ `CommentPrefixes=["'", "//"]` (既定) の状態で、コメント行・連続空行を含む複数行テキストを送信する
- **THEN** 行頭が `'` または `//` で始まる行 (前後の空白除去はしない) を除去し、直前行も空行であった連続空行は 1 行に縮約してから送信する
- **AND** `CommentPrefixes` が空配列の場合はコメント除去を行わない

### Requirement: コマンド追加テンプレートと per-command 編集ダイアログ
「コマンド追加」操作は静的テンプレート一覧から選択でき、選択後は全フィールドを自由編集できなければならない (SHALL)。

#### Scenario: テンプレート一覧
- **WHEN** コマンド追加ダイアログ (`CommandTemplateDialog`, isAddMode=true) を開く
- **THEN** CMD / PowerShell / Claude Code / Codex / Copilot / Antigravity / (空テンプレート) の順でテンプレートを選択でき、選択した瞬間に実行ファイル・引数・waitPatterns・改行モード・bracketed paste・作業ディレクトリ選択・クイック送信初期値などのプリセット値がフォームへ流し込まれる

#### Scenario: per-window 設定ダイアログ
- **WHEN** システムメニュー「AMM 設定…」を開く (`AmmSettingsDialog`)
- **THEN** `CloseProhibited` / `CollapseBlankLines` / `CommentPrefixes` (カンマ区切りテキスト) の 3 項目を編集でき、確定後は AMM ファイルへ書き戻すかどうかを確認する

### Requirement: セッション復帰トークンの付加
`ResumeOnStart=true` のとき、実際の起動引数には `CommandType` に応じたセッション復帰トークンを末尾付加しなければならない (SHALL)。

#### Scenario: CLI 種別ごとのトークン
- **WHEN** `CommandType` が `ClaudeCode` または `CopilotCli` で `ResumeOnStart=true`
- **THEN** `Args` の末尾に `--resume` を付加した引数配列を起動に使う
- **WHEN** `CommandType` が `Codex` で `ResumeOnStart=true`
- **THEN** `Args` の末尾に `resume` を付加する (codex はサブコマンドだが `cmd /c codex.cmd` 形式の末尾付加で成立する)
- **AND** `Cmd` / `PowerShell` / `Other` では `ResumeOnStart` の値に関わらずトークンを付加しない
