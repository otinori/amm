## Context

amm には既に `mcp-gateway` capability(外部 MCP サーバーを stdio JSON-RPC の子プロセスとして起動・自動再起動・ツール集約する `GatewayManager`/`ManagedMcpProcess`)と `mcp-server` capability(amm 自身が Named Pipe 上で MCP サーバーとして振る舞い、`tools/call` 等を受け付ける)がある。

外部調査(2026年7月時点)により、**A2A (Agent2Agent Protocol)** が MCP と並ぶエージェント連携標準として確立していることを確認した: Google が2025年4月に公開、2025年6月に Linux Foundation へ寄贈。HTTP + Server-Sent Events + JSON-RPC 2.0 をトランスポートとし、Agent Card(能力広告)・Task(作業単位)という概念を持つ。2026年時点で v1.0 が本番グレードとなり、150以上の組織が採用。「リモート/分散環境のagent同士」を対象とする点で、ローカルサブプロセス前提の MCP/ACP とは前提が異なる。

amm は複数 AI CLI エージェントを集約・実行する層であるため、A2A を使って外部エージェントを呼び出せることには明確な価値がある。A2A はHTTPリモートで非同期 Task という、`mcp-gateway` が想定する「常駐子プロセス」モデルと大きく異なる形状を持つ。

実装対象は Tauri 実装(`src/apps/Amm/`)一択。amm には唯一の実装(Tauri/Rust)しか存在しないため、実装先の選択は発生しない。

## Goals / Non-Goals

**Goals:**
- amm が A2A エージェント(HTTPリモート、Agent Card経由で発見)を設定・呼び出しできる新規 capability `a2a-gateway` を定義する
- 既存の `mcp-gateway` / `mcp-server` の要件・実装には一切影響を与えない(完全に加算的な変更)
- A2A経由の呼び出しを、既存のMCPクライアント(Claude Code等がamm-mcp.exe経由で接続する既存ワークフロー)からも到達可能にする設計の方向性を示す

**Non-Goals:**
- amm自身をA2Aエージェント(Agent Card公開・タスク受理)として外部に公開すること(将来検討の余地はあるが本変更はクライアント側のみ)
- A2Aプロトコルの全機能への対応(MVPとして必要最小限のサブセットに絞る。詳細はtasks.mdで段階化)
- A2Aのpush通知Webhook受信(デスクトップアプリでは公開エンドポイントを持てないため、pollingまたはSSE購読のみを対象とする)
- ACP(stdio子プロセスのローカルエージェント)対応。前提が大きく異なるため別変更 `add-acp-gateway` で扱う

## Decisions

### 1. A2A統合はHTTPクライアントとして実装し、Agent Card取得→Task作成→ポーリング/SSE購読の流れを持つ
`/.well-known/agent-card.json` 相当のAgent Card取得エンドポイントから能力を発見し、Task作成はJSON-RPC over HTTP、進捗はSSE購読(対応していないagentへはpolling fallback)とする。

代替案: push通知Webhookの実装。デスクトップアプリは常時到達可能な公開URLを持たないため不採用(Non-Goals参照)。

### 2. A2A呼び出しは既存MCP surfaceに `agent/{agentName}/task` / `agent/{agentName}/task_status` 形式で公開する
`mcp-gateway` の `"{server}/{tool}"` 命名パターンを参考にしつつ、`mcp-gateway` とは別の名前空間(`agent/{agentName}/task`)で `tools/list`/`tools/call` に統合する。A2Aの非同期Taskは単発の同期 `tools/call` に素直にはマッピングできないため、Task作成呼び出しとTask状態取得呼び出しを分ける(詳細はOpen Questions参照)。

代替案: Named Pipeサーバーに全く別のRPCメソッド(`a2a/*`)を新設し、`tools/call` 経由の統合はしない。既存MCPクライアントからの発見性が下がるため、まずは `tools/call` 統合を優先案とし、実装困難であればこちらへ切り替える。

### 3. 設定モデルは `mcp-gateway` の「グローバル + ファイル固有」の2階層グルーピングを踏襲するが、独立したスキーマにする
`McpServerEntry`(`name`/`command`/`args[]`/`env{}`/`autoStart`/`maxRestarts`)を再利用せず、A2A用に適した独立エントリ型(`endpoint(URL)`/`authConfig`中心)を新設する。`mcp-gateway`の設定スキーマ変更を避けるため。

## Risks / Trade-offs

- [A2Aの非同期Taskはデスクトップアプリの同期的な操作感と相性が悪い] → `approval-hub` の通知UXパターン(承認待ちの非フォーカスポップアップ)が転用できる可能性があるが、tasks.mdで個別検討する
- [A2Aプロトコルバージョンが若く(v1.0)破壊的変更のリスクがある] → 実装時に使用するプロトコルバージョンを明記し固定する。バージョンアップ時の追随は `tasks/backlog.md` で管理する
- [A2Aはネットワーク越しにサードパーティのリモートエージェントへデータを送る経路を新設するため、情報漏洩リスクがある] → `mcp-gateway`同様、明示的なエージェント登録(オプトイン)を必須とし、既定では何も呼び出さない

## Migration Plan

新規capabilityのため既存データ・既存動作への移行は不要(完全加算的)。ロールアウトは以下の順序を想定する(詳細はtasks.mdで具体化):

1. 設定スキーマ(A2Aエージェント登録用)の追加
2. A2Aエージェント呼び出しのMVP実装(単一エージェント、Task作成→polling取得のみ)
3. 既存MCP surface(`tools/call`)への統合
4. 複数エージェント対応・設定UI・エラーハンドリングの拡充

ロールバック戦略: 新規capabilityが独立しているため、問題が生じた場合は `a2a-gateway` 関連コード・設定を無効化するだけで `mcp-gateway`/`mcp-server` を含む既存機能への影響なく切り戻せる。

## Open Questions

- **A2A Taskの非同期性のUX**: Task完了をamm側でどう可視化するか(`approval-hub`の通知ポップアップ相当を転用するか、専用のペインを設けるか)
- **A2A認証情報の保管**: Agent Cardが要求する認証(APIキー/OAuth等)をammの設定でどう扱うか(`config.yaml`実体はコミット禁止という既存方針との整合)
