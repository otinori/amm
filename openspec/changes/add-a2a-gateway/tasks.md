## 1. 前提確認・設計判断

- [ ] 1.1 A2A Taskの非同期性のUX(通知ポップアップ転用 vs 専用ペイン vs ポーリングのみ)を決定する
- [ ] 1.2 A2A認証情報(APIキー等)の保管方式を決定する(`config.yaml`実体非コミット方針との整合)
- [ ] 1.3 使用するA2Aの具体的なプロトコルバージョンを固定し記録する(A2A v1.0 を想定)

## 2. A2Aエージェント基盤(MVP)

- [ ] 2.1 A2Aエージェント設定スキーマ(`name`/`endpoint`/`authConfig`)を実装する
- [ ] 2.2 Agent Card取得・キャッシュを実装する
- [ ] 2.3 Task作成(HTTP JSON-RPC)を実装する
- [ ] 2.4 Task状態取得(SSE購読、非対応時はポーリングfallback)を実装する
- [ ] 2.5 単一A2Aエージェントに対するTask作成→完了確認のE2E疎通を確認する(実在の公開A2Aエージェントまたはモックサーバーで可)

## 3. 既存MCP surfaceへの統合

- [ ] 3.1 `tools/list` にA2Aエージェント由来のツール(`agent/{agentName}/*`)を集約する
- [ ] 3.2 `tools/call` からA2A `task`/`task_status` への転送を実装する(タスク2.2〜2.4の成果を接続)
- [ ] 3.3 `mcp-gateway`/`mcp-server` の既存ツール(`send_message`等)が無変更で動作することを回帰確認する

## 4. 設定UI・複数エージェント対応

- [ ] 4.1 A2Aエージェントの登録・編集・削除UIを実装する(`McpGatewayDialog`相当の別ダイアログ、またはタブ追加)
- [ ] 4.2 管理エージェントのステータス表示(✓/⏳/✗/○)を実装する
- [ ] 4.3 複数A2Aエージェントの同時運用を確認する

## 5. パリティ・回帰確認

- [ ] 5.1 `openspec/specs/a2a-gateway/spec.md`(archive後)の全requirementに対する動作確認を実機で行う
- [ ] 5.2 `amm-mcp.exe`のCLIインターフェース(`send`/`list`/`notify`/`approve`等)が無変更であることを確認する
- [ ] 5.3 `Amm.PowerShell`(`Open-AmmWindow`等)からの疎通に影響がないことを確認する
