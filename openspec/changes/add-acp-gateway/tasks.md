## 1. 前提確認・設計判断

- [ ] 1.1 ACP呼び出しのツール形状(`tools/call`への単純ラップ vs 専用 `agent/session_*` ツール群)を決定する
- [ ] 1.2 使用するACPの具体的なプロトコルバージョンを固定し記録する(ACP v1 を想定)

## 2. ACPエージェント基盤(MVP)

- [ ] 2.1 ACPエージェント設定スキーマ(`name`/`command`/`args[]`/`env{}`/`protocolVersion`/`autoStart`)を実装する
- [ ] 2.2 `AcpAgentManager`(子プロセス起動・`initialize`・`session/new`ハンドシェイク・ライフサイクル管理)を実装する
- [ ] 2.3 単一ACPエージェントに対する `session/prompt` 往復のE2E疎通を確認する(実機、対象CLIは1〜2種で可)
- [ ] 2.4 パーミッション要求コールバックの中継を実装する
- [ ] 2.5 クラッシュ検出・プロセス破棄(`mcp-gateway`の`MaxRestarts`相当の要否を含め)を実装する

## 3. 既存MCP surfaceへの統合

- [ ] 3.1 `tools/list` にACPエージェント由来のツール(`agent/{agentName}/*`)を集約する
- [ ] 3.2 `tools/call` からACP `session/prompt` への転送を実装する(タスク2.2〜2.4の成果を接続)
- [ ] 3.3 `mcp-gateway`/`mcp-server` の既存ツール(`send_message`等)が無変更で動作することを回帰確認する

## 4. 設定UI・複数エージェント対応

- [ ] 4.1 ACPエージェントの登録・編集・削除・並べ替えUIを実装する(`McpGatewayDialog`相当の別ダイアログ、またはタブ追加)
- [ ] 4.2 管理エージェントのステータス表示(✓/⏳/✗/○)を実装する
- [ ] 4.3 グローバル/ファイル固有の2階層設定の並存に対応する
- [ ] 4.4 複数ACPエージェントの同時運用を確認する

## 5. パリティ・回帰確認

- [ ] 5.1 `openspec/specs/acp-gateway/spec.md`(archive後)の全requirementに対する動作確認を実機で行う
- [ ] 5.2 `amm-mcp.exe`のCLIインターフェース(`send`/`list`/`notify`/`approve`等)が無変更であることを確認する
- [ ] 5.3 `Amm.PowerShell`(`Open-AmmWindow`等)からの疎通に影響がないことを確認する
