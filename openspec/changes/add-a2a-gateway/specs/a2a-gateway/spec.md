## ADDED Requirements

### Requirement: A2A エージェント設定
amm は A2A(Agent2Agent Protocol)エージェントの定義(`name`, `endpoint` (Agent Card 取得元 URL), `authConfig`)を保持し、専用の設定 UI から追加・編集・削除できなければならない (SHALL)。`authConfig` の実体(APIキー等)は `config.yaml` と同様にリポジトリへコミットしない運用とする。

#### Scenario: 新規A2Aエージェント登録
- **WHEN** `endpoint` を指定してA2Aエージェントを新規登録する
- **THEN** 登録内容が保存され、後続の Agent Card 取得の対象となる

### Requirement: A2A Agent Card の取得とキャッシュ
amm は登録済み A2A エージェントの `endpoint` から Agent Card を取得し、能力情報(サポートする skill・認証要件)をキャッシュしなければならない (SHALL)。Agent Card 取得に失敗したエージェントは呼び出し不能として扱う。

#### Scenario: Agent Card取得成功
- **WHEN** 登録済みエージェントの Agent Card 取得を行う
- **THEN** 能力情報がキャッシュされ、そのエージェントは呼び出し可能な状態になる

#### Scenario: Agent Card取得失敗
- **WHEN** `endpoint` へのアクセスが失敗する、または Agent Card の形式が不正である
- **THEN** そのエージェントはステータス `Error` として扱われ、呼び出しは拒否される

### Requirement: A2A Task の作成と状態取得の転送
amm の MCP surface(`tools/call`)で A2A エージェント宛のツール呼び出し(`"agent/{agentName}/task"` 形式)が指定された場合、amm は対応する A2A エンドポイントへ Task 作成リクエストを送信し、Task ID を返さなければならない (SHALL)。amm は別途 `"agent/{agentName}/task_status"` 呼び出しにより、SSE 購読(対応エージェントの場合)またはポーリングのいずれかで Task の最新状態(`pending`/`running`/`completed`/`failed` 等)を取得できなければならない (SHALL)。

#### Scenario: Task作成
- **WHEN** `tools/call` で `name="agent/remote-agent/task"` を Running な A2A エージェントへ呼ぶ
- **THEN** A2Aエンドポイントへ Task 作成リクエストが送信され、Task ID が呼び出し元へ返る

#### Scenario: SSE対応エージェントでの状態購読
- **WHEN** Task作成後、対象エージェントが SSE をサポートしている
- **THEN** amm は SSE ストリームを購読し、状態変化を逐次キャッシュする

#### Scenario: SSE非対応エージェントでのポーリング
- **WHEN** 対象エージェントが SSE をサポートしていない
- **THEN** amm は一定間隔でTask状態をポーリングしキャッシュを更新する

### Requirement: 既存インターフェースとの独立性維持
`a2a-gateway` capability の追加は、既存の `mcp-gateway` / `mcp-server` capability が提供する全ツール(`send_message` / `list_participants` / `peek_queue` / `amm/notify` / `amm/approval` / `"{server}/{tool}"` 形式のMCPゲートウェイツール等)の動作を変更してはならない (SHALL)。`amm-mcp.exe` のコマンドラインインターフェースも変更しない。

#### Scenario: a2a-gateway有効時の既存ツール呼び出し
- **WHEN** A2Aエージェントが構成された状態で `send_message` や `"{server}/{tool}"` 形式のMCPゲートウェイツールを呼ぶ
- **THEN** `a2a-gateway` 導入前と同じ動作をする

### Requirement: 管理エージェントのステータス表示
専用の設定 UI は、管理下の各 A2A エージェントについて 接続中 (✓) / 接続試行中 (⏳) / エラー (✗) / 停止 (○) のステータスアイコンを一覧表示しなければならない (SHALL)。判定根拠は直近の Agent Card 取得・通信結果とする。

#### Scenario: 全エージェント未接続時の表示
- **WHEN** A2Aエージェントがいずれも接続されていない状態で設定UIを開く
- **THEN** 全エントリが「○」(停止) として表示される
