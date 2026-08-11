# profile-schema Specification

## Purpose
TBD - created by archiving change establish-baseline-specs. Update Purpose after archive.
## Requirements
### Requirement: プロファイルファイルスキーマ
`.amm` 拡張子のプロファイルファイル（既定ファイル名 `profiles.amm`）は `{ "profiles": [...], "mcpServers": [...] }` 形式の JSON でなければならず (SHALL)、各 profile エントリは `SessionProfile` の全フィールド (name / commandType / executable / args / resumeOnStart / outputEncoding / autoChcp / waitPatterns / workingDirectory / ctrlCCopyOnSelection / initialCommands / sessionLog / theme / closeOnExit / autoStartCount / closeProhibited / collapseBlankLines / commentPrefixes / windowGeometry / nickname / sendLineByLine / selectWorkingDirOnStart / promptNewNameOnCommandAdd / quickPrompts / autoSendOnIdle / fontSize) を、未指定時は既定値で解決可能でなければならない。`newlineMode` / `useBracketedPaste` / `chatRecord` / `chatRecordTailChars` / `stats` は廃止済みフィールドであり（チャット記録・統計情報機能自体を削除、2026-08-03）、読み込み時に存在しても無視しなければならない (SHALL)。`theme` は文字列ではなく `{ [key: string]: string }` 形式のオブジェクト（例: `{"background":"#000000","foreground":"#00ff00"}`）でなければならない (SHALL) — 実装は `theme` を単一プロファイルの型不整合のためにファイル全体の読み込みを失敗させてはならない (SHALL NOT)。1件のプロファイルの型エラーは当該プロファイルのみ既定値へフォールバックし、他のプロファイルの読み込みに影響してはならない (SHALL NOT)。

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

#### Scenario: 廃止フィールドを含むファイルの読み込み
- **WHEN** JSON に `newlineMode` または `useBracketedPaste` キーを含むプロファイルエントリをロードする
- **THEN** エラーにはならず、両キーの値は単に読み捨てられる (以後の再保存でも書き戻されない)

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
`windowGeometry` は同一プロファイルの起動 N 番目 (1 始まり、同プロファイルの生存数+1 を index として参照) の MDI に適用する位置・サイズ・表示名・最大化状態・作業ディレクトリを保持できなければならない (SHALL)。このポート(Tauri版)の常時グリッドレイアウトでは位置・サイズ(x/y/w/h)自体はペイン配置に反映されない(グリッドが常に決定する)ため、実質的に意味を持つのは表示名・作業ディレクトリ・最大化状態(ズームトグルとして適用)の3点である。

**2026-07-29 UX見直し(ユーザー指示)**: 記憶のトリガーは、独立したペインメニュー項目(「現在の配置を記憶」)ではなく、**プロファイル保存操作(上書き保存/名前を付けて保存)のたびに、生存中の全プロファイルについて自動的に記憶してから保存する**形に簡略化した(「記憶してから保存する」という2段階の手間を無くすため)。ad-hocペイン(プロファイルに紐付かないペイン)は対象外。

#### Scenario: プロファイル保存時の自動記憶
- **WHEN** 「上書き保存」または「名前を付けて保存」を実行する
- **THEN** その時点で生存している全ペインについて、プロファイルごとに現在の表示名・作業ディレクトリ・最大化状態を`windowGeometry`へ記憶してから、ファイルへの書き込みを行う

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
- **WHEN** コマンド追加ダイアログ (isAddMode=true) を開く
- **THEN** CMD / PowerShell / Claude Code / Codex / Copilot / (空テンプレート) の順でテンプレートを選択でき、選択した瞬間に実行ファイル・引数・waitPatterns・AutoChcp・出力エンコーディング・作業ディレクトリ選択・コマンド追加時の新規名入力・クイック送信初期値などのプリセット値がフォームへ流し込まれる

#### Scenario: per-window 設定ダイアログ
- **WHEN** システムメニュー「AMM 設定…」を開く
- **THEN** `CloseProhibited` / `CollapseBlankLines` / `CommentPrefixes` (カンマ区切りテキスト) の 3 項目を編集でき、確定後は AMM ファイルへ書き戻すかどうかを確認する

### Requirement: セッション復帰トークンの付加
`ResumeOnStart=true` のとき、実際の起動引数には `CommandType` に応じたセッション復帰トークンを末尾付加しなければならない (SHALL)。

#### Scenario: CLI 種別ごとのトークン
- **WHEN** `CommandType` が `ClaudeCode` または `CopilotCli` で `ResumeOnStart=true`
- **THEN** `Args` の末尾に `--resume` を付加した引数配列を起動に使う
- **WHEN** `CommandType` が `Codex` で `ResumeOnStart=true`
- **THEN** `Args` の末尾に `resume` を付加する (codex はサブコマンドだが `cmd /c codex.cmd` 形式の末尾付加で成立する)
- **AND** `Cmd` / `PowerShell` / `Other` では `ResumeOnStart` の値に関わらずトークンを付加しない

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

