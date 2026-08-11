## Why

amm は `mcp-gateway` capability により、外部 MCP サーバー(stdio JSON-RPC で起動する子プロセス)をゲートウェイとして集約し、`"{server}/{tool}"` 形式で amm 自身の MCP ツール一覧に統合できる。しかし近年、HTTP/SSE 越しにエージェント同士がタスクを委譲し合う **A2A (Agent2Agent Protocol)**(Google発、2025年6月に Linux Foundation へ寄贈。2026年時点で v1.0・150+組織が採用)が、MCP(ツール呼び出し)と並ぶエージェント間連携の標準として普及している。

amm は複数 AI CLI を集約・実行する層という性質上、MCP だけでなく A2A 経由でも外部エージェントを呼び出せることには明確な価値がある(例: A2A Agent Card を持つリモートエージェントにタスクを投げる等)。本変更はこの能力を新規 capability として追加する。

なお、stdio子プロセスで駆動するローカルエージェント連携である ACP (Agent Client Protocol) は前提(常駐子プロセス vs リモートHTTPエンドポイント)が大きく異なるため、別変更 `add-acp-gateway` として独立に提案する(元は `add-agent-protocol-gateway` として一体で起票していたが、実装単位を分けるため分割した)。

## What Changes

- 新規 capability `a2a-gateway` を追加する: amm が A2A エージェント(HTTP/SSE リモートエンドポイント)を設定・呼び出しできるようにする
- A2A エージェントは HTTP(+SSE)クライアントとして Agent Card を取得し、Task を作成・ポーリング/購読できるようにする(stdioサブプロセスモデルとは前提が異なるため、`mcp-gateway`/ACPとは別の起動・ライフサイクル管理を持つ)
- `amm-mcp.exe` / MCPクライアント視点からは、A2A 経由の呼び出しも `tools/call`(`"agent/{agentName}/task"` / `"agent/{agentName}/task_status"` 形式)を通じて既存の MCP ワークフローに統合される(詳細は design.md)

## Capabilities

### New Capabilities
- `a2a-gateway`: amm が A2A(HTTP/SSE リモート)の外部エージェントを設定・呼び出し・集約するための capability。既存の `mcp-gateway`(MCPサーバー専用)とは独立した capability とし、`mcp-gateway` の既存要件・実装には影響を与えない

### Modified Capabilities
(なし。`mcp-gateway` / `mcp-server` の既存要件は変更しない)

## Impact

- **`src/apps/Amm/src-tauri/src/gateway.rs`**: `GatewayManager` とは別系統の新規コンポーネント(A2A版、HTTPクライアント)が必要になる可能性がある。実装対象は Tauri 実装(`src/apps/Amm/`)一択(唯一の実装のため選択の余地なし)
- **`openspec/specs/mcp-gateway/spec.md` / `openspec/specs/mcp-server/spec.md`**: 参照するのみで変更しない
- **`McpGatewayDialog`相当のUI**: A2Aエージェント設定用の新規UI(または既存ダイアログの拡張)が必要になる可能性
- **ペインでCLIを起動する既存の仕組み(ConPTY)**: 本変更の直接のスコープ外

## Status

**2026-08-10時点: 提案・設計のみ。実装は未着手。** ユーザー方針により、公開リポジトリ化の前段階としてまず本proposal/design/specsを実装着手可能な状態まで確定させ、実際の実装は別セッションで着手する。
