## ADDED Requirements

### Requirement: Named Pipe トランスポート
amm GUI は `\\.\pipe\amm-mcp-{Environment.UserName}` という既定名の Named Pipe 上に MCP/JSON-RPC サーバを常駐させなければならない (SHALL)。パイプ ACL は現在の Windows ユーザーにのみ `FullControl` を付与し、同一ログオンユーザーのプロセスのみが接続できるようにしなければならない (SHALL)。フレーミングは NDJSON (1 行 = 1 JSON-RPC メッセージ) とする。

#### Scenario: 同一ユーザーの複数プロセスが同時接続する
- **WHEN** 複数の `amm-mcp.exe` プロセスが同時に同じ named pipe へ接続する
- **THEN** サーバは接続ごとに独立したセッション (`HandleSessionAsync`) を非同期に処理し、1 セッションのブロッキング処理 (例: 許可待ち) が他のセッションの応答を妨げない

#### Scenario: 改行なしの巨大ペイロード
- **WHEN** クライアントが改行区切りなしで 1 MiB を超えるデータを送信する
- **THEN** サーバはその行を破棄しセッションを切断する (メモリ枯渇 DoS を防ぐ)

### Requirement: MCP ハンドシェイクと標準メソッド
サーバは MCP プロトコルバージョン `2024-11-05` を実装し、`initialize` / `notifications/initialized` / `tools/list` / `tools/call` / `ping` の標準メソッドに応答しなければならない (SHALL)。`serverInfo` は `{ name: "amm-operator", version: "0.3.0" }` を返す。

#### Scenario: initialize 呼び出し
- **WHEN** クライアントが `initialize` を送る
- **THEN** サーバは `protocolVersion`, `capabilities.tools`, `serverInfo` を含む result を返す

#### Scenario: 未知のメソッド
- **WHEN** クライアントが登録されていない method 名を送る (かつ id 付き)
- **THEN** サーバは JSON-RPC エラーコード `-32601` (Method not found) を返す

### Requirement: 組み込みツールの一覧
`tools/list` は少なくとも `send_message` / `list_participants` / `peek_queue` / `mdi/open` / `mdi/close` / `mdi/wait_state` の 6 ツールを含めなければならない (SHALL)。ゲートウェイが構成されている場合は外部サーバのツールを末尾に追加する。

#### Scenario: ゲートウェイ未設定時の tools/list
- **WHEN** ゲートウェイ (`GatewayManager`) が構成されていない状態で `tools/list` を呼ぶ
- **THEN** 応答には上記 6 ツールのみが含まれる

### Requirement: send_message の配信意味論
`send_message` ツールは `message` (必須) と任意の `recipient` / `mode` (既定 `"first"`) を受け取り、対象 MDI が入力待ちなら直接注入、そうでなければ nickname ごとのキューに退避しなければならない (SHALL)。`recipient` 省略時は nickname を持つ全 MDI へブロードキャストする。

#### Scenario: mode=first で入力待ちを優先
- **WHEN** 同一 nickname の複数 MDI のうち 1 つが入力待ちで `mode` 省略 (既定 first) で送信する
- **THEN** 入力待ちの MDI に直接注入され、`delivered_count=1`、入力待ちが無ければ起動順の先頭がキューに積まれる

#### Scenario: mode=all で全インスタンスへ配信
- **WHEN** `mode="all"` を指定して同一 nickname 宛に送信する
- **THEN** 同 nickname の全 MDI インスタンスへ、入力待ちのものは注入・そうでないものはキューされる

#### Scenario: nickname 未設定 profile は対象外
- **WHEN** ある MDI の profile に `nickname` が設定されていない
- **THEN** その MDI は `list_participants` にも `send_message` の対象にも現れない

### Requirement: list_participants と peek_queue
`list_participants` は引数なしで登録済み全参加者のスナップショット (`nickname`, `profile`, `instance`, `state`, `session_id`) を返し、`peek_queue` は指定 (または全) nickname の配信待ちメッセージを非破壊的に閲覧できなければならない (SHALL)。

#### Scenario: peek_queue で recipient 省略
- **WHEN** `peek_queue` を引数なしで呼ぶ
- **THEN** 全 nickname 分のキュー内容がスナップショットとして返り、キューの中身は変化しない (dequeue しない)

### Requirement: hook 駆動状態通知 (amm/notify) の受理
サーバは amm 独自拡張メソッド `amm/notify` を受理し、`{token, state, source?}` を受け取って `IMcpHost.NotifyChildState` へ委譲し、`WaitBroker` の該当 pending wait を解放しなければならない (SHALL)。id なしの notification としても呼び出し可能とする。

#### Scenario: 既知トークンでの通知
- **WHEN** `amm/notify` に既知の `token` と `state` を渡す (id 付き)
- **THEN** `{ matched: true }` を返し、対応する MDI の状態が更新される

#### Scenario: 未知トークンでの通知
- **WHEN** amm 配下ではない CLI 等、未知の `token` で `amm/notify` を呼ぶ
- **THEN** `{ matched: false }` を返し、例外にはならない

### Requirement: JSON-RPC エラーハンドリング
サーバはパース不能な入力に `-32700`、`method` 欠落等の不正リクエストに `-32600`、必須パラメータ欠落に `-32602`、ハンドラ内例外に `-32603` (内部詳細は露出せず汎用メッセージのみ) を返さなければならない (SHALL)。

#### Scenario: 不正 JSON
- **WHEN** パース不能な文字列を 1 行として受信する
- **THEN** `{ error: { code: -32700 } }` を id なしで返す

#### Scenario: ハンドラ内で例外発生
- **WHEN** ツール実行中に想定外の例外が送出される
- **THEN** クライアントには `-32603` と汎用メッセージのみが返り、例外詳細 (パス等) はアプリログにのみ記録される

### Requirement: amm-mcp.exe stdio ブリッジと CLI モード
`amm-mcp.exe` は引数なし (stdin redirect 時) または `--bridge` で MCP stdio ブリッジとして動作し、stdin↔pipe / pipe↔stdout を双方向コピーしなければならない (SHALL)。加えて `repl` / `send` / `list` / `notify` / `approve` の各サブコマンドモードを提供する。

#### Scenario: GUI 未起動時のブリッジ起動
- **WHEN** amm GUI が起動していない状態で `amm-mcp.exe` を bridge モードで起動する
- **THEN** 既定 5000ms のタイムアウト後に終了コード `2` (GUI 未起動) で終了する

#### Scenario: send サブコマンドでの送信
- **WHEN** `amm-mcp.exe send <nickname> <message>` を実行する
- **THEN** `send_message` ツールが呼ばれ、`delivered_count` / `queued_count` / `recipients` が標準エラー出力に要約表示される

### Requirement: CLI 設定ファイルへの MCP サーバ登録
`McpCliRegistrar` は Claude Code (`~/.claude.json`) / Codex (`~/.codex/config.toml`) / Copilot CLI (`~/.copilot/mcp-config.json`) / Antigravity (`~/.antigravity/mcp-config.json`) の各設定ファイルに `amm-mcp.exe` を MCP サーバとして登録・削除できなければならない (SHALL)。既存の設定内容は保全し、同一ディレクトリの一時ファイル経由でアトミックに書き込む。

#### Scenario: 未登録ファイルへの新規登録
- **WHEN** 設定ファイルが存在しない状態で `Register` を呼ぶ
- **THEN** ファイルが新規作成され `mcpServers.amm` (または Codex は `[mcp_servers.amm]`) エントリが追加される

#### Scenario: 登録解除は amm エントリのみ除去
- **WHEN** 他の MCP サーバエントリと共存した状態で `Unregister` を呼ぶ
- **THEN** amm のエントリのみが削除され、他のエントリや設定値は保全される
