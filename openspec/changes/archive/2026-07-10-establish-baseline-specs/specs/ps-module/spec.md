## ADDED Requirements

### Requirement: バイナリモジュールとしてのパッケージング
`Amm.PowerShell` は .NET 9 クラスライブラリとしてビルドされる `Amm.PowerShell.dll` を `RootModule` とする PowerShell バイナリモジュールでなければならない (SHALL)。モジュールマニフェスト（`Amm.PowerShell.psd1`）は `PowerShellVersion = '7.4'` を要求し、`CmdletsToExport` に `Connect-Amm`, `Disconnect-Amm`, `Open-AmmWindow`, `Close-AmmWindow`, `Send-AmmMessage`, `Get-AmmSession`, `Wait-AmmIdle` の7個のみを列挙し、`FunctionsToExport` / `VariablesToExport` / `AliasesToExport` は空でなければならない (SHALL)。

#### Scenario: モジュールインポートで7cmdletが公開される
- **WHEN** `Import-Module Amm.PowerShell` を実行する
- **THEN** `Connect-Amm` / `Disconnect-Amm` / `Open-AmmWindow` / `Close-AmmWindow` / `Send-AmmMessage` / `Get-AmmSession` / `Wait-AmmIdle` の7cmdletのみが利用可能になる

### Requirement: Named Pipe を介した使い捨て接続によるJSON-RPC通信
各 cmdlet は `AmmPipeClient` を用いて処理1回ごとに新規の `NamedPipeClientStream` 接続を確立し、処理完了後に破棄しなければならない (SHALL)（1リクエスト=1接続、コネクションプールや常駐接続は行わない）。パイプ名は環境変数 `AMM_MCP_PIPE_NAME` が設定されていればその値、なければ `amm-mcp-{Environment.UserName}` でなければならない (SHALL)。リクエストは `{jsonrpc:"2.0", id, method, params}` 形式で1行JSONとして送信し、`params` のプロパティ名は snake_case に変換しなければならない (SHALL)。

#### Scenario: パイプ名の既定値
- **WHEN** `AMM_MCP_PIPE_NAME` 環境変数が未設定の状態で任意の cmdlet を実行する
- **THEN** `\\.\pipe\amm-mcp-{現在のWindowsユーザー名}` へ接続を試みる

#### Scenario: 環境変数によるパイプ名上書き
- **WHEN** `AMM_MCP_PIPE_NAME` が設定されている
- **THEN** その値をパイプ名として接続する

#### Scenario: エラーレスポンスは例外化される
- **WHEN** JSON-RPC レスポンスに `error` オブジェクトが含まれる
- **THEN** `AmmPipeClient.SendRequest` は `error.message` を含む `InvalidOperationException` を送出する

### Requirement: Connect-Amm / Disconnect-Amm は接続確認と no-op
`Connect-Amm` は指定（または既定）のパイプへ接続を試みるだけの疎通確認cmdletでなければならず (SHALL)、接続に成功した場合は特に永続状態を保持しない。接続がタイムアウトした場合は `AmmNotRunning` エラーIDで終了エラー（`ThrowTerminatingError`）を発生させなければならない (SHALL)。`Disconnect-Amm` は、各 cmdlet が使い捨て接続を個別管理する現行実装においては実質的に no-op であり、詳細メッセージを `WriteVerbose` で出力するのみでなければならない (SHALL)。

#### Scenario: amm未起動時のConnect-Amm
- **WHEN** amm.exe が起動しておらず Named Pipe への接続がタイムアウトする
- **THEN** `Connect-Amm` は `AmmNotRunning` カテゴリの終了エラーを送出し、「amm GUI に接続できませんでした」旨のメッセージを表示する

#### Scenario: Disconnect-Ammは状態を変更しない
- **WHEN** `Disconnect-Amm` を実行する
- **THEN** 接続の切断やセッション状態の変更は行われず、verboseメッセージのみが出力される

### Requirement: Open-AmmWindow によるMDIウィンドウ起動とAmmSession出力
`Open-AmmWindow` は `ByCommand`（`-Command` 必須、`-Args` 任意）と `ByProfile`（`-ProfileName` 必須、既存プロファイルを名前指定して起動）の2つのパラメータセットを持たなければならない (SHALL)。`-Title` / `-WorkingDirectory` はどちらのセットでも指定可能とする。Named Pipe メソッド `amm.openWindow` を呼び出し、応答の `session_id` を用いて `AmmSession` オブジェクトを `WriteObject` で出力しなければならない (SHALL)。`session_id` が空、または `result` 自体が得られない場合は終了エラーとする。

#### Scenario: コマンド直接指定での起動
- **WHEN** `Open-AmmWindow -Command "claude"` を実行する
- **THEN** `amm.openWindow` に `command="claude"` を渡して呼び出し、返された `session_id` を持つ `AmmSession` オブジェクトを出力する

#### Scenario: 既存プロファイル名での起動
- **WHEN** `Open-AmmWindow -ProfileName "agent-a"` を実行する
- **THEN** `amm.openWindow` に `profile_name="agent-a"` を渡して呼び出す（`command` は渡さない）

#### Scenario: session_id欠落時は終了エラー
- **WHEN** `amm.openWindow` の応答に `session_id` が含まれない、または空文字である
- **THEN** `OpenAmmWindowFailed` の終了エラーを送出する

### Requirement: Close-AmmWindow のパイプライン対応とShouldProcess
`Close-AmmWindow` は `-SessionId`（`ValueFromPipeline` / `ValueFromPipelineByPropertyName` 対応）を必須パラメータとし、`SupportsShouldProcess` を有効にしなければならない (SHALL)。`-Force` スイッチを `amm.closeWindow` の `force` パラメータへ渡す。パイプ経由で得たエラーは終了エラーにせず `WriteWarning` として通知する。

#### Scenario: Get-AmmSessionからのパイプライン連携
- **WHEN** `Get-AmmSession | Where-Object Title -like "Agent-*" | Close-AmmWindow -Force` を実行する
- **THEN** 各セッションの `SessionId` プロパティがパイプラインバインドされ、`amm.closeWindow` が `force=true` で呼ばれる

#### Scenario: サーバー側エラーは警告として扱う
- **WHEN** `amm.closeWindow` の応答に `error` 文字列が含まれる
- **THEN** cmdlet は終了エラーにはせず `WriteWarning` でエラー内容を表示する

### Requirement: Get-AmmSession によるMCP list_participants経由の一覧取得
`Get-AmmSession` は amm-mcp と同じ MCP JSON-RPC ハンドシェイク（`initialize` 呼び出し後に `tools/call` で `list_participants` ツールを呼ぶ）経由でセッション一覧を取得しなければならない (SHALL)。各参加者は `nickname` と `instance` を持ち、`instance == 1` の場合は `nickname` のみ、それ以外は `"{nickname} ({instance})"` を `AmmSession.Title` として `WriteObject` しなければならない (SHALL)。

#### Scenario: 単一インスタンスのタイトル
- **WHEN** `list_participants` の応答に `instance=1` の参加者が含まれる
- **THEN** その `AmmSession.Title` は `nickname` そのものになる

#### Scenario: 複数インスタンスのタイトル
- **WHEN** 同一 `nickname` で `instance=2` の参加者が含まれる
- **THEN** その `AmmSession.Title` は `"{nickname} (2)"` になる

#### Scenario: structuredContentが無い場合はtext内JSONへフォールバック
- **WHEN** `tools/call` の応答に `structuredContent` が含まれず `content[0].text` にJSON文字列として結果が入っている
- **THEN** そのテキストをパースして `participants` を取得する

### Requirement: Send-AmmMessage によるMCP send_message経由の送信
`Send-AmmMessage` は `-Nickname`（エイリアス `-Title`、`ValueFromPipeline`/`ValueFromPipelineByPropertyName` 対応）と `-Message` を必須パラメータとし、`-Mode` は既定値 `"first"` としなければならない (SHALL)。amm-mcp と同じMCPハンドシェイク経由で `tools/call` の `send_message` ツール（`recipient`, `message`, `mode` 引数）を呼び出し、応答の `delivered_count` / `queued_count` を `WriteVerbose` で報告しなければならない (SHALL)。本cmdletは `Send-AmmMessage` という名前だが、amm独自の `amm.send` メソッドではなくMCPツール呼び出しを経由する。

#### Scenario: パイプラインからの送信
- **WHEN** `Get-AmmSession | Send-AmmMessage -Message "done?"` を実行する
- **THEN** 各セッションの `Nickname`（または `Title`）がバインドされ、それぞれに対して `send_message` ツールが呼ばれる

#### Scenario: 配信結果のverbose出力
- **WHEN** 応答に `structuredContent.delivered_count` と `queued_count` が含まれる
- **THEN** `Send-AmmMessage: delivered={delivered}, queued={queued}` がverboseストリームに出力される

### Requirement: Wait-AmmIdle によるブロッキング待機とニックネーム解決
`Wait-AmmIdle` は `BySessionId`（既定、`-SessionId`）と `ByNickname`（`-Nickname`）の2つのパラメータセットを持ち、`-TargetState`（既定 `"idle"`）と `-TimeoutMs`（既定 `300000`）を受け付けなければならない (SHALL)。`ByNickname` 指定時は `list_participants` を呼び出して大文字小文字を無視した一致でセッションIDを解決し、見つからない場合は終了エラーとしなければならない (MUST)。解決後、`amm.waitState` を読み取りタイムアウト無制限（サーバー側が目標状態到達まで応答を保留）で呼び出し、応答の `state` と `elapsed_ms` から `WaitResult` を出力しなければならない (SHALL)。ポーリングは行わない。

#### Scenario: SessionId指定でのブロッキング待機
- **WHEN** `Wait-AmmIdle -SessionId "abc123"` を実行し、対象セッションがアイドル状態になるまでフック通知が届かない
- **THEN** cmdlet はブロックし続け、`amm.waitState` の応答が届いた時点で `WaitResult{ State, ElapsedMs }` を出力する

#### Scenario: Nickname解決に失敗した場合
- **WHEN** `-Nickname` で指定した名前が `list_participants` の結果に存在しない
- **THEN** `WaitAmmIdleNicknameNotFound` の終了エラーを送出し `amm.waitState` は呼ばれない

#### Scenario: サーバー側タイムアウトでのtimeout状態返却
- **WHEN** `-TimeoutMs 5000` を指定し、5秒経過してもセッションがアイドル状態に到達しない
- **THEN** `amm.waitState` は `state: "timeout"` を返し、`Wait-AmmIdle` はそれをそのまま `WaitResult` として出力する
