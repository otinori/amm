## ADDED Requirements

### Requirement: 外部 MCP サーバー設定
amm は「AMM 共通 (グローバル)」と「このファイル固有」の 2 グループで外部 MCP サーバー定義 (`name`, `command`, `args[]`, `env{}`, `autoStart`, `maxRestarts`) を保持し、`McpGatewayDialog` から追加・編集・削除・並べ替えできなければならない (SHALL)。`maxRestarts` の既定値は 3、`autoStart` の既定値は true とする。

#### Scenario: グローバルとファイル固有の並存
- **WHEN** グローバル設定とファイル固有設定の双方に MCP サーバーエントリが存在する
- **THEN** `GatewayManager` はその両方を管理対象として起動・集約する

### Requirement: 自動起動サーバーの一括起動
`GatewayManager.StartAutoStartServersAsync` は `autoStart=true` のサーバーのみを並行して起動しなければならない (SHALL)。`autoStart=false` のサーバーは自動起動されない。

#### Scenario: amm 起動時の自動起動
- **WHEN** amm GUI が起動し `StartAutoStartServersAsync` が呼ばれる
- **THEN** `autoStart=true` の全エントリの子プロセスが並行して起動され、`autoStart=false` のエントリは起動されない

### Requirement: 外部プロセスの起動ハンドシェイクとライフサイクル
`ManagedMcpProcess` は子プロセスを stdio (stdin/stdout) の JSON-RPC で起動し、`initialize` に続けて `tools/list` を送信してツールマニフェストをキャッシュしなければならない (SHALL)。amm 終了時は `DisposeAsync` で全子プロセスを `Kill(entireProcessTree: true)` する。

#### Scenario: 起動シーケンス
- **WHEN** `StartAsync` が呼ばれる
- **THEN** プロセスは `Starting` → (initialize 成功後) `Running` の順に遷移し、`Tools` に `tools/list` の結果がキャッシュされる

#### Scenario: 起動失敗
- **WHEN** `initialize` が応答しない、またはプロセス起動自体が失敗する
- **THEN** ステータスは `Error` になり `LastError` に理由が記録され、プロセスは破棄される

### Requirement: クラッシュ検出と自動再起動
子プロセスが予期せず終了した場合、amm は `MaxRestarts` 回まで自動的に再起動しなければならない (SHALL)。上限に達した場合はステータスを `Error` とし再起動を停止する。

#### Scenario: 上限内での再起動
- **WHEN** 子プロセスがクラッシュし再起動回数が `MaxRestarts` 未満である
- **THEN** 1 秒後に自動的に再起動が試みられ、保留中だった JSON-RPC 呼び出しはすべてキャンセルされる

#### Scenario: 再起動上限到達
- **WHEN** 再起動回数が `MaxRestarts` に達した状態で再度クラッシュする
- **THEN** ステータスは `Error` に固定され、それ以上の自動再起動は行われない

### Requirement: ツールの集約と名前空間プレフィックス
amm は Running 状態の外部サーバーが持つツールを `"{serverName}/{toolName}"` の形式でプレフィックスし、組み込みツールと合わせて `tools/list` 応答に含めなければならない (SHALL)。Running でないサーバーのツールは一覧に含まれない。

#### Scenario: 複数サーバーのツール集約
- **WHEN** `fs` と `browser` という 2 つの Running な外部サーバーが登録されている
- **THEN** `tools/list` には `fs/<tool>` と `browser/<tool>` の形式で両サーバーのツールが列挙される

### Requirement: ゲートウェイツール呼び出しの転送
`tools/call` で `"{server}/{tool}"` 形式の名前が指定された場合、amm はそれをゲートウェイツールと認識し対応する子プロセスへ JSON-RPC を転送して結果を返さなければならない (SHALL)。対象サーバーが存在しないか Running でない場合は呼び出し元にエラーを返す。

#### Scenario: 正常なツール転送
- **WHEN** `tools/call` で `name="fs/read_file"` を Running な `fs` サーバーへ呼ぶ
- **THEN** 引数がそのまま `fs` プロセスの `tools/call` として転送され、結果がそのままクライアントへ返る

#### Scenario: 停止中サーバーへの呼び出し
- **WHEN** 対象サーバーが `Running` でない状態で該当プレフィックスのツールを呼ぶ
- **THEN** `-32603` のエラーで「Gateway server for '...' is not running」相当のメッセージが返る

### Requirement: 既存インターフェースとの互換性維持
ゲートウェイの追加は既存の `send_message` / `list_participants` / `peek_queue` / `amm/notify` / `amm/approval` / `mdi/*` ツールの動作を変更してはならない (SHALL)。`amm-mcp.exe` のコマンドラインインターフェースも変更しない。

#### Scenario: ゲートウェイ有効時の既存ツール呼び出し
- **WHEN** ゲートウェイが構成された状態で `send_message` を呼ぶ
- **THEN** ゲートウェイ導入前と同じ配信意味論 (直接注入 / キュー退避) で動作する

### Requirement: 管理サーバーのステータス表示
`McpGatewayDialog` は管理下の各外部 MCP サーバーについて 実行中 (✓) / 起動中 (⏳) / エラー (✗) / 停止 (○) / 未設定 (●、ゲートウェイ停止中) のステータスアイコンとツール数を一覧表示しなければならない (SHALL)。

#### Scenario: ゲートウェイ停止中のダイアログ表示
- **WHEN** ゲートウェイが起動していない状態で設定ダイアログを開く
- **THEN** 全エントリが「●」(未設定) として表示され、ツール数は表示されない
