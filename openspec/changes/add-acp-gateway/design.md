## Context

amm には既に `mcp-gateway` capability(外部 MCP サーバーを stdio JSON-RPC の子プロセスとして起動・自動再起動・ツール集約する `GatewayManager`/`ManagedMcpProcess`)と `mcp-server` capability(amm 自身が Named Pipe 上で MCP サーバーとして振る舞い、`tools/call` 等を受け付ける)がある。

外部調査(2026年7月時点)により、**ACP (Agent Client Protocol)** が MCP と並ぶエージェント連携標準として確立していることを確認した: Zed Industries が2025年8月に公開。JSON-RPC 2.0 を stdin/stdout で流し、ホスト(エディタ等)が agent をサブプロセスとして起動するモデル。2026年1月開始の ACP Registry には Claude Code / Codex CLI / GitHub Copilot CLI / Gemini CLI / OpenCode 等が登録されている。現行の安定プロトコルバージョンは 1。Apache ライセンスのオープン標準。

amm は複数 AI CLI エージェントを集約・実行する層であるため、ACP を使って外部エージェントを呼び出せることには明確な価値がある。ACP はMCPと同じ「stdioサブプロセス+JSON-RPC」という transport 形状を持つ一方、セッション/パーミッション/ファイルアクセスコールバック等 MCP の `tools/list`+`tools/call` モデルとは異なるやり取りを要求する。

実装対象は Tauri 実装(`src/apps/Amm/`)一択。amm には唯一の実装(Tauri/Rust)しか存在しないため、実装先の選択は発生しない。

## Goals / Non-Goals

**Goals:**
- amm が ACP エージェント(stdioサブプロセス)を設定・起動・呼び出しできる新規 capability `acp-gateway` を定義する
- 既存の `mcp-gateway` / `mcp-server` の要件・実装には一切影響を与えない(完全に加算的な変更)
- ACP経由の呼び出しを、既存のMCPクライアント(Claude Code等がamm-mcp.exe経由で接続する既存ワークフロー)からも到達可能にする設計の方向性を示す

**Non-Goals:**
- ペインで動作するインタラクティブCLI(ConPTY上のClaude Code等)自体をACPエージェント化すること(別タスク、`tasks/backlog.md`で追跡。本変更はあくまで「ammがACPの**クライアント**として外部エージェントを呼ぶ」側)
- amm自身をACPサーバー(セッション受理)として外部に公開すること(将来検討の余地はあるが本変更はクライアント側のみ)
- ACPプロトコルの全機能への対応(MVPとして必要最小限のサブセットに絞る。詳細はtasks.mdで段階化)
- A2A(HTTP/SSEリモートエージェント)対応。前提が大きく異なるため別変更 `add-a2a-gateway` で扱う

## Decisions

### 1. ACP統合は `mcp-gateway` の `ManagedMcpProcess` を再利用せず、独立した `AcpAgentManager` として実装する
transportの見た目(stdio + JSON-RPC)はMCPと似るが、ACPは `initialize` 後のやり取りが `session/new` → `session/prompt` → パーミッション要求コールバック → ストリーミング応答、というMCPの `tools/list`+`tools/call` とは別のメソッド体系・状態機械を持つ。`ManagedMcpProcess` に無理に統合すると、`mcp-gateway`(挙動パリティが要件化されている安定spec)の実装が汚染されるリスクが高い。

代替案: `ManagedMcpProcess` を拡張しACP/MCP両対応にする。プロセスライフサイクル管理(起動・再起動・破棄)のコードは共通化できる余地があるが、メソッド体系の違いが大きく現段階では棄却。共通化は実装が安定してから検討する。

### 2. ACP呼び出しは既存MCP surfaceに `agent/{agentName}/prompt` 形式で公開する
`mcp-gateway` の `"{server}/{tool}"` 命名パターンを参考にしつつ、`mcp-gateway` とは別の名前空間(`agent/{agentName}/prompt`)で `tools/list`/`tools/call` に統合する。ACPのセッション型のやり取りは単発の同期 `tools/call` に素直にはマッピングできないため、1セッション=1回の `tools/call` 相当にラップする方向とする(詳細はOpen Questions参照)。

代替案: Named Pipeサーバーに全く別のRPCメソッド(`acp/*`)を新設し、`tools/call` 経由の統合はしない。既存MCPクライアントからの発見性が下がるため、まずは `tools/call` 統合を優先案とし、実装困難であればこちらへ切り替える。

### 3. 設定モデルは `mcp-gateway` の「グローバル + ファイル固有」の2階層グルーピングを踏襲するが、独立したスキーマにする
`McpServerEntry`(`name`/`command`/`args[]`/`env{}`/`autoStart`/`maxRestarts`)を再利用せず、ACP用に適した独立エントリ型(`command`/`args[]`/`protocolVersion`中心)を新設する。`mcp-gateway`の設定スキーマ変更を避けるため。

## Risks / Trade-offs

- [ACPのセッション型モデルが `tools/call` の単発同期呼び出しに馴染まない] → Open Questionsで具体的なツール形状を検討してから仕様化する。安易にMVP実装を進めるとやり直しリスクが高い
- [ACPプロトコルバージョンが若く(v1)破壊的変更のリスクがある] → 実装時に使用するプロトコルバージョンを明記し固定する。バージョンアップ時の追随は `tasks/backlog.md` で管理する
- [呼び出し先CLIがネイティブACPに対応していない可能性] → 予備調査(2026-07-19)の結果、Claude Code CLIには`--acp`相当のネイティブフラグが無く、ACP対応は`claude-code-acp`等の**サードパーティ製ブリッジアダプタ**(`claude -p --output-format stream-json`をラップしJSON-RPC化)経由が実態。ACP Registryへの登録は「ACP経由で到達可能」を意味するのみで「CLIバイナリがネイティブに話す」ことは意味しない。他CLI(Codex/Gemini/Copilot)も個別確認が必要。`AcpAgentManager`が`command`に何を起動するかの設計時に、公式ネイティブ対応かサードパーティブリッジ経由かを区別し、後者を使う場合は非公式依存のサプライチェーンリスクを認識した上で採用可否を判断する(詳細は`tasks/backlog.md`)

## Migration Plan

新規capabilityのため既存データ・既存動作への移行は不要(完全加算的)。ロールアウトは以下の順序を想定する(詳細はtasks.mdで具体化):

1. 設定スキーマ(ACPエージェント登録用)の追加
2. ACPエージェント起動・呼び出しのMVP実装(単一エージェント、`session/prompt`往復のみ)
3. 既存MCP surface(`tools/call`)への統合
4. 複数エージェント対応・設定UI・エラーハンドリングの拡充

ロールバック戦略: 新規capabilityが独立しているため、問題が生じた場合は `acp-gateway` 関連コード・設定を無効化するだけで `mcp-gateway`/`mcp-server` を含む既存機能への影響なく切り戻せる。

## Open Questions

- **ACP呼び出しのツール形状**: セッション型(`session/new`→複数回`session/prompt`)を `tools/call` にどうマッピングするか(1セッション=1回のtools/call相当にラップするか、専用の `agent/session_*` ツール群を新設するか)
- **`tasks/backlog.md`の「ペインCLIのACP化」との関係**: 将来そちらが実現した場合、amm自身がホストするペインCLIも`acp-gateway`の管理対象に含めるのか、別物として扱うのか
