# アーキテクチャ（現状の構成）

> 本書は **現状（いまどう出来ているか）** のみを述べる。採否の **理由** はここには書かない。

> `openspec/changes/archive/2026-07-26-migrate-to-tauri/`（UDR-amm-20260713T1037-ff3、2026-07-26にopenspec archive済み）を経て確定したTauri実装（`src/apps/Amm/`）。

## 成果物一覧

| 成果物 | 種別 | プロジェクト | 役割 |
|---|---|---|---|
| amm | EXE | `src/apps/Amm/src-tauri/`(バイナリ`amm`) | 単一ウィンドウ内ペイン管理型ターミナルマルチプレクサ本体（Rust/Tauri v2 + WebView2 + ConPTY(`portable-pty`) + xterm.js。MDIではなくペイン管理方式、経緯: UDR-amm-20260713T0447-98f） |
| amm-mcp | EXE | `src/apps/Amm/src-tauri/src/bin/amm-mcp/` | stdio MCP サーバ（GUI 内 Named Pipe ブリッジ経由） |
| Amm.PowerShell | `.psm1` | `src/modules/Amm.PowerShell/` | PowerShell連携モジュール（コンパイル不要のスクリプトモジュール、経緯: UDR-amm-20260719T0013-b7e） |

## 依存方向

```
apps/Amm/src-tauri (amm) ─┐
                           ├─→ 独立クレート、共有ライブラリなし（単一Cargo.tomlの2バイナリ構成）
apps/Amm/src-tauri/src/bin/amm-mcp ─┘
```

- `amm-mcp` は GUI 本体の Named Pipe サーバ（`\\.\pipe\amm-mcp-{user}`, 現在のWindowsユーザーのみのDACL、SDDL文字列+`ConvertStringSecurityDescriptorToSecurityDescriptorW`で実装、tasks.md 4.1）に接続する。

## 主要サブシステム（src/apps/Amm/src-tauri/src/）

- `mcp.rs` — Named Pipeサーバ + JSON-RPCディスパッチ
- `approval.rs` — `ApprovalBroker`、ToolUse許可の集約回答。UIは第2WebviewWindowではなくメインウィンドウ内DOMオーバーレイ方式（第2ウィンドウ生成が原因のアプリ全体デッドロックを実機で発見・解消した経緯があり、意図的にこの設計を採用）
- `hook_cli.rs` / `mcp_cli.rs` — CLIフック登録
- `gateway.rs` — 外部MCPサーバーの集約・自動起動・クラッシュ再起動（`mcp-gateway` capability）
- `profile.rs` — profiles.ammスキーマ・ローダ・ホットリロード
- `wait_detect.rs` — 入力待ち判定（正規表現ベースの`WaitPatternDetector`）
- `input_history.rs` — 送信履歴（`history.json`）
- ファイル書き込みは`write_atomic`等で原子的書き込みを踏襲

## 図

構成図・シーケンス図は `docs/design/diagrams/` に置く（drawio / puml）。

---

*この一覧は構成変更時に上書き改訂する。*
