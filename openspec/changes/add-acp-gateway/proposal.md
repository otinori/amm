## Why

amm は `mcp-gateway` capability により、外部 MCP サーバー(stdio JSON-RPC で起動する子プロセス)をゲートウェイとして集約し、`"{server}/{tool}"` 形式で amm 自身の MCP ツール一覧に統合できる。しかし近年、エディタ/ホストがコーディングエージェントをサブプロセス起動して JSON-RPC で駆動する **ACP (Agent Client Protocol)**(Zed Industries が2025年8月公開。Claude Code / Codex CLI / GitHub Copilot CLI / Gemini CLI など主要 CLI が 2026年1月開始の ACP Registry に登録済み)が、MCP(ツール呼び出し)と並ぶエージェント間連携の標準として普及している。

amm は複数 AI CLI を集約・実行する層という性質上、MCP だけでなく ACP 経由でも外部エージェントを呼び出せることには明確な価値がある(例: ACP対応の別ホストが管理するエージェントに作業を委譲する等)。本変更はこの能力を新規 capability として追加する。

なお、HTTP/SSE越しのリモートエージェント連携である A2A (Agent2Agent Protocol) は前提(常駐子プロセス vs リモートHTTPエンドポイント)が大きく異なるため、別変更 `add-a2a-gateway` として独立に提案する(元は `add-agent-protocol-gateway` として一体で起票していたが、実装単位を分けるため分割した)。

## What Changes

- 新規 capability `acp-gateway` を追加する: amm が ACP エージェント(stdio JSON-RPC サブプロセス)を設定・起動・呼び出しできるようにする
- ACP エージェントは `mcp-gateway` の `ManagedMcpProcess` に近い「子プロセスをstdioで起動してJSON-RPCを話す」モデルを踏襲するが、ACPプロトコル固有のハンドシェイク(`initialize`のcapability交換、`session/new`のセッション確立等)に対応する
- `amm-mcp.exe` / MCPクライアント視点からは、ACP 経由の呼び出しも `tools/call`(`"agent/{agentName}/prompt"` 形式)を通じて既存の MCP ワークフローに統合される(詳細は design.md)

## Capabilities

### New Capabilities
- `acp-gateway`: amm が ACP(stdio JSON-RPC サブプロセス)の外部エージェントを設定・起動・呼び出し・集約するための capability。既存の `mcp-gateway`(MCPサーバー専用)とは独立した capability とし、`mcp-gateway` の既存要件・実装には影響を与えない

### Modified Capabilities
(なし。`mcp-gateway` / `mcp-server` の既存要件は変更しない)

## Impact

- **`src/apps/Amm/src-tauri/src/gateway.rs`**: `GatewayManager` に相当する新規コンポーネント(ACP版、`AcpAgentManager`)が必要になる可能性がある。実装対象は Tauri 実装(`src/apps/Amm/`)一択(唯一の実装のため選択の余地なし)
- **`openspec/specs/mcp-gateway/spec.md` / `openspec/specs/mcp-server/spec.md`**: 参照するのみで変更しない
- **`McpGatewayDialog`相当のUI**: ACPエージェント設定用の新規UI(または既存ダイアログの拡張)が必要になる可能性
- **ペインでCLIを起動する既存の仕組み(ConPTY)**: 本変更の直接のスコープ外(ペインCLI自体をACPエージェント化する話は別タスクとして `tasks/backlog.md` で追跡)

## Status

**2026-08-10時点: 提案・設計のみ。実装は未着手。** ユーザー方針により、公開リポジトリ化の前段階としてまず本proposal/design/specsを実装着手可能な状態まで確定させ、実際の実装は別セッションで着手する。
