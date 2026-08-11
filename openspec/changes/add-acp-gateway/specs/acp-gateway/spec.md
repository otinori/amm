## ADDED Requirements

### Requirement: ACP エージェント設定
amm は ACP(Agent Client Protocol)エージェントの定義(`name`, `command`, `args[]`, `env{}`, `protocolVersion`, `autoStart`)を「AMM 共通 (グローバル)」と「このファイル固有」の 2 グループで保持し、専用の設定 UI から追加・編集・削除・並べ替えできなければならない (SHALL)。`autoStart` の既定値は false とする(`mcp-gateway` の MCP サーバーとは異なり、ACP エージェントは明示的な利用意図がある場合のみ起動する既定とする)。

#### Scenario: 新規ACPエージェント登録
- **WHEN** `command`/`args[]` を指定してACPエージェントを新規登録する
- **THEN** 登録内容がグローバルまたはファイル固有の設定として保存され、`autoStart=false` が既定値として設定される

### Requirement: ACP エージェントの起動ハンドシェイクとセッション確立
`AcpAgentManager` は ACP エージェントを子プロセスとして stdio (stdin/stdout) の JSON-RPC で起動し、ACP プロトコルバージョン 1 の `initialize` に続けて `session/new` を送信し、確立したセッション ID を保持しなければならない (SHALL)。amm 終了時は全子プロセスを `Kill(entireProcessTree: true)` する。

#### Scenario: 起動シーケンス
- **WHEN** ACPエージェントの `StartAsync` が呼ばれる
- **THEN** プロセスは `Starting` → (`initialize` 成功後) `Ready` → (`session/new` 成功後) `Running` の順に遷移し、セッションIDが記録される

#### Scenario: 起動失敗
- **WHEN** `initialize` または `session/new` が応答しない、またはプロセス起動自体が失敗する
- **THEN** ステータスは `Error` になり `LastError` に理由が記録され、プロセスは破棄される

### Requirement: ACP session/prompt 呼び出しの転送
amm の MCP surface(`tools/call`)で ACP エージェント宛のツール呼び出し(`"agent/{agentName}/prompt"` 形式)が指定された場合、amm は対応する確立済みセッションへ ACP の `session/prompt` を転送し、エージェントからの応答(テキスト・パーミッション要求含む)を呼び出し元へ中継しなければならない (SHALL)。対象エージェントが `Running` でない場合は呼び出し元にエラーを返す。

#### Scenario: 正常なプロンプト転送
- **WHEN** `tools/call` で `name="agent/claude-code/prompt"` を `Running` な ACP エージェントへ呼ぶ
- **THEN** 引数が `session/prompt` として転送され、エージェントの応答がそのままクライアントへ返る

#### Scenario: パーミッション要求の中継
- **WHEN** ACP エージェントが処理中に(ファイルアクセス等の)パーミッション要求をホストへ送り返す
- **THEN** amm はその要求を呼び出し元へ中継し、応答が得られるまで当該 `session/prompt` の完了を保留する

#### Scenario: 停止中エージェントへの呼び出し
- **WHEN** 対象エージェントが `Running` でない状態で `agent/{agentName}/prompt` を呼ぶ
- **THEN** `-32603` のエラーで「Agent '...' is not running」相当のメッセージが返る

### Requirement: 既存インターフェースとの独立性維持
`acp-gateway` capability の追加は、既存の `mcp-gateway` / `mcp-server` capability が提供する全ツール(`send_message` / `list_participants` / `peek_queue` / `amm/notify` / `amm/approval` / `"{server}/{tool}"` 形式のMCPゲートウェイツール等)の動作を変更してはならない (SHALL)。`amm-mcp.exe` のコマンドラインインターフェースも変更しない。

#### Scenario: acp-gateway有効時の既存ツール呼び出し
- **WHEN** ACPエージェントが構成された状態で `send_message` や `"{server}/{tool}"` 形式のMCPゲートウェイツールを呼ぶ
- **THEN** `acp-gateway` 導入前と同じ動作をする

### Requirement: 管理エージェントのステータス表示
専用の設定 UI は、管理下の各 ACP エージェントについて 実行中 (✓) / 起動中 (⏳) / エラー (✗) / 停止 (○) のステータスアイコンを一覧表示しなければならない (SHALL)。判定根拠はプロセス状態とする。

#### Scenario: 全エージェント未起動時の表示
- **WHEN** ACPエージェントがいずれも起動されていない状態で設定UIを開く
- **THEN** 全エントリが「○」(停止) として表示される
