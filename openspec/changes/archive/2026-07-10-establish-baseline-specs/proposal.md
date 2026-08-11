## Why

amm を別言語へ全面移植する計画がある。移植の土台となる正確な仕様書が必要だが、現状の仕様情報は `docs/design/spec/spec.md`（旧リバース生成v1）・`spec-v2.md`（Phase別変更指示書）・個別 `req-*.md` 群に分散しており、OpenSpec の Requirement/Scenario 形式にもなっていない。加えて Git 連携・チャット記録・入力履歴・hook CLI の4機能は既存文書が一切なく、コードのみが正となっている。`openspec/specs/` は現状空であり、移植元の as-is 挙動を capability 単位で確立する必要がある。

## What Changes

- 現行 Windows 版 amm（`src/apps/Amm`・`src/apps/Amm.Mcp`・`src/modules/Amm.PowerShell`）の実装を精査し、16 capability の spec.md を新規作成する（すべて `ADDED Requirements`、新機能提案ではなく as-is 文書化）
- 既存の `docs/design/spec/spec.md` / `spec-v2.md` / `req-*.md` の内容を該当 capability に統合・再構成する（これらのファイル自体は本 change では削除せず、統合完了後の扱いは別途検討）
- 未文書化だった `git-integration` / `chat-recording` / `input-history` / `hook-cli` をソースコードから新規にリバース起票する
- `Amm.Desktop`（Mac/Avalonia 実験、ソース欠落・git 管理外）はスコープ外として扱う

## Capabilities

### New Capabilities
- `mdi-window-management`: MDI 親子ウィンドウ構成・メニュー・キーバインド・ファイル drop・入力待ち可視化などターミナル基盤（`MdiParentForm`/`TerminalChildForm`/`ConPtyWrapper`/`Win32SystemMenu`）
- `profile-schema`: AMM ファイル（`profiles.json`/`.amm`）のスキーマ・解決順序・hot reload・コマンドテンプレート（`SessionProfile`/`AppPaths`）
- `wait-detection`: 入力待ち状態検出（パターンマッチ + hook 駆動イベント）（`WaitPatternDetector`/`WaitBroker`/`WaitResult`）
- `mcp-server`: amm 本体が提供する MCP サーバ機能・`amm-mcp.exe` ブリッジ（`McpPipeServer`/`MessageDispatcher`/`MessageQueue`）
- `approval-hub`: ToolUse 確認要求の集約通知・集約回答（`ApprovalBroker`/`ApprovalPopupForm`）
- `auto-send-idle`: 純粋な入力待ち遷移時の自動送信
- `command-import-export`: `SessionProfile` の複数選択エクスポート/インポート（`.ammprofiles`）
- `mcp-gateway`: 外部 MCP サーバーの集約・マルチプレクサ機能（`GatewayManager`/`ManagedMcpProcess`/`McpServerConfig`）
- `mdi-window-control`: 外部（MCP/PowerShell）から MDI ウィンドウを開閉・状態取得するオートパイロット向けインタフェース
- `ps-module`: PowerShell バイナリモジュールの cmdlet 群（`Open-AmmWindow`/`Send-AmmMessage` 等、`AmmPipeClient`/`AmmSession`）
- `quick-command-register`: 右クリックメニューからのクイック送信コマンド即時登録
- `tray-icon`: システムトレイ常駐アイコン・入力待ちトースト通知（`TrayIconManager`）
- `git-integration`: MDI ペイン向け Git コミット支援・ガード（`GitCommitDialog`/`GitGuard`/`GitHelper`、未文書化）
- `chat-recording`: セッションのチャット記録・統計表示（`ChatRecorder`/`ChatStats`/`ChatStatsDialog`、未文書化）
- `input-history`: 入力欄のコマンド履歴（`InputHistory`、未文書化）
- `hook-cli`: hook 駆動検知を CLI 側に登録する仕組み（`HookCliRegistrar`、未文書化）

### Modified Capabilities
（`openspec/specs/` は空のため既存 spec への差分はなし。すべて新規 ADDED）

## Impact

- 変更対象はドキュメントのみ（`openspec/specs/`・`openspec/changes/establish-baseline-specs/`）。アプリケーションコード（`src/`）・ビルド・テストへの影響なし
- 将来の移植プロジェクトが本 baseline spec を参照して言語変換の要件定義を行う
- `docs/design/spec/` 配下の既存ドキュメントとの重複が生じるため、統合後の棚卸し（アーカイブ or 廃止）を別タスクとして残す
