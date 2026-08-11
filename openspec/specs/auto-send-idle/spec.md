# auto-send-idle Specification

## Purpose
TBD - created by archiving change establish-baseline-specs. Update Purpose after archive.
## Requirements
### Requirement: プロファイル単位のアイドル自動送信設定
`SessionProfile` は `AutoSendOnIdle`（`AutoSendOnIdleSettings`: `Enabled` bool 既定 `false` / `Prompt` string 既定 `""` / `DelayMs` int 既定 `3000`）を JSON プロパティ `autoSendOnIdle` として保持しなければならない (SHALL)。この設定はプロファイル単位であり、アプリ全体の既定値は存在しない。

#### Scenario: 既定プロファイルは無効
- **WHEN** `autoSendOnIdle` キーを含まないプロファイルを読み込む
- **THEN** `Enabled=false`, `Prompt=""`, `DelayMs=3000` として復元され、自動送信は発火しない

### Requirement: Running → WaitingForInput 遷移でのみカウントダウンを開始する
`TerminalChildForm` は ConPTY の待機状態が `Running` から `WaitingForInput` へ遷移し、かつその時点で `HasAttention == false` かつ `AutoSendOnIdle.Enabled == true` かつ `Prompt` が空でない場合に限り、`DelayMs` ミリ秒のカウントダウンタイマーを開始しなければならない (SHALL)。同一アイドル滞在中に既にカウントダウン中（`_autoSendArmed == true`）であれば再度開始してはならない (MUST NOT)。

#### Scenario: 初回のRunning→Idle遷移でタイマー開始
- **WHEN** プロファイルの `AutoSendOnIdle.Enabled` が `true` かつ `Prompt` が非空の状態で、待機状態が `Running` から `WaitingForInput` に遷移し `HasAttention` が `false` である
- **THEN** `_autoSendArmed` が `true` になり `DelayMs` 後に送信するカウントダウンタイマーが開始される

#### Scenario: 設定が無効または空プロンプトなら発火しない
- **WHEN** `AutoSendOnIdle.Enabled` が `false`、または `Prompt` が空文字のまま `Running → WaitingForInput` に遷移する
- **THEN** カウントダウンタイマーは開始されない

#### Scenario: 同一アイドル中は再発火しない
- **WHEN** カウントダウン中またはタイマー満了で送信済みの状態のまま、`WaitState` が `WaitingForInput` に留まり続ける
- **THEN** 次に `Running` へ遷移するまでタイマーは再度開始されない

### Requirement: アテンションまたは状態遷移によるキャンセル
カウントダウン中に `HasAttention` が `true` になった場合、または待機状態が `Running` もしくは `Stopped` に変化した場合、amm はカウントダウンタイマーを破棄し送信を行ってはならない (MUST NOT)。

#### Scenario: ToolUse承認待ちでキャンセル
- **WHEN** カウントダウン中に `HasAttention` が `true` に変化する（ToolUse / 許可待ち発生）
- **THEN** `CancelAutoSendTimer` が呼ばれタイマーが破棄され、タイトルは通常表示に戻る

#### Scenario: 出力再開でキャンセル
- **WHEN** カウントダウン中に待機状態が `Running` または `Stopped` に変化する
- **THEN** `_autoSendArmed` が `false` に戻りタイマーが破棄される

### Requirement: 遅延0以下は即時送信、それ以外はタイトルバーに残秒数を表示する
`DelayMs` が0以下の場合、amm はカウントダウン表示を行わず即座にプロンプトを送信しなければならない (SHALL)。`DelayMs` が正の場合、amm は 500ms 間隔でタイトルバーに `⏱{残秒数}s`（切り上げ秒）をオーバーレイ表示し続けなければならない (SHALL)。本実装にはカウントダウンを中断するユーザー操作可能な `[✕]` ボタン等の明示的キャンセル UI は存在しない。

#### Scenario: 遅延0で即時送信
- **WHEN** `AutoSendOnIdle.DelayMs` が `0`（またはそれ以下）に設定されている
- **THEN** `Running → WaitingForInput` 遷移直後に `ExecuteAutoSend` が呼ばれ、カウントダウン表示なしにプロンプトが送信される

#### Scenario: 残秒数表示の更新
- **WHEN** `DelayMs` が `3000` でカウントダウン中である
- **THEN** タイトルバーが500ms毎に更新され `⏱3s`, `⏱2s`, `⏱1s` のように残秒数（切り上げ）が表示される

### Requirement: 送信直前の再ガードと単発実行
タイマー満了時、amm は送信実行直前に `HasAttention` と `IsProcessRunning` を再チェックし、いずれかが不成立であれば送信を行わずに終了しなければならない (SHALL)。送信は `SendText(prompt, appendEnter: true)` として ConPTY へ書き込み、その後 `ScrollToBottom()` を呼び出す。送信後は `_autoSendArmed` を `false` に戻し、次の `Running` 遷移まで再送信してはならない (MUST NOT)。

#### Scenario: 満了時にプロセスが終了していれば送信しない
- **WHEN** カウントダウン満了時点で ConPTY プロセスが既に終了している（`IsProcessRunning == false`）
- **THEN** `ExecuteAutoSend` はプロンプトを送信せずに `_autoSendArmed` のみ `false` に戻す

#### Scenario: 満了時に正常送信
- **WHEN** カウントダウンが満了し `HasAttention == false` かつ `IsProcessRunning == true` である
- **THEN** `Prompt` に改行を付加して ConPTY へ書き込み、ターミナル表示を末尾へスクロールする

