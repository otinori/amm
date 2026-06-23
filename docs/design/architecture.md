# アーキテクチャ（現状の構成）

> 本書は **現状（いまどう出来ているか）** のみを述べる。採否の **理由** は書かず、`.udr/` の UDR-ID をリンクする。

## 成果物一覧

| 成果物 | 種別 | プロジェクト | 役割 |
|---|---|---|---|
| Amm | EXE | `src/apps/Amm/` | MDI ターミナルマルチプレクサ本体（WinForms + WebView2 + ConPTY + xterm.js） |
| amm-mcp | EXE | `src/apps/Amm.Mcp/` | stdio MCP サーバ（GUI 内 Named Pipe ブリッジ経由でメッセージ送受信・通知・承認） |

共有ロジックは現状 `src/apps/Amm/Core/` に集約しており、独立した共有 DLL（`src/libs/`）は未分離。

## 依存方向

```
apps/Amm        ─┐
                 ├─→ (将来) libs/*      ※ apps → libs の一方向のみ
apps/Amm.Mcp    ─┘
```

- `Amm.Mcp` は GUI 本体の Named Pipe サーバ（`\\.\pipe\AMMOperator-MCP-{user}`, current user ACL）に接続する。
  経緯: UDR-amm-20260427T0225-7a3。

## 主要サブシステム（src/apps/Amm/Core/Mcp/）

- `McpPipeServer` / `MessageDispatcher` / `MessageQueue` — メッセージ送受信とキュー
- `ApprovalBroker` — ToolUse 許可の集約回答（経緯: UDR-amm-20260605T1124-9c4）
- `HookCliRegistrar` / `McpCliRegistrar` — CLI フック登録（経緯: UDR-amm-20260605T1251-4e2）
- `AtomicFileWriter` — 設定の原子的書き込み（経緯: UDR-amm-20260608T1102-7f2）

## 図

構成図・シーケンス図は `docs/design/diagrams/` に置く（drawio / puml）。

---

*この一覧は構成変更時に上書き改訂する。決定の経緯は必ず UDR を参照すること。*
