# 個別要求: ToolUse 確認要求の集約通知・集約回答 (Approval Hub)

**ステータス**: Level 1 / Level 2 とも実装済み (2026-06-05, UDR-amm-20260605T1043-3af / UDR-amm-20260605T1124-9c4) — 本要求はクローズ
**起票日**: 2026-06-04
**起票者**: maintainer (Claude Code との対話から記録)
**更新**: 2026-06-05 — hook 駆動入力待ち検知 (UDR-amm-20260605T0523-7e1) の実装を受け、受け口を「専用 Pipe 新設」から「既存 MCP Pipe への独自メソッド同居」に変更

---

## 1. 要求

各 MDI ペインで動く AI CLI (Claude Code / Codex CLI / Copilot CLI) がツール実行の許可確認を出したとき:

1. **トリガー通知**: 「AI から確認依頼が来た」ことを amm が検知し、人間に通知する
2. **回答の集約**: 回答する場所を個々の MDI 子ウィンドウではなく **1 か所に集約** したい

## 2. 実現可能性 (2026-06 調査済み)

3 CLI とも ToolUse をフックする機構を持ち、**通知も回答の集約も可能**。

| CLI | 使うイベント | 回答の外部化 |
|---|---|---|
| Claude Code | `Notification` (許可要求時発火) / `PreToolUse` | `PreToolUse` フックは同期ブロックで `permissionDecision: allow/deny/ask` を返せる |
| Codex CLI | `PermissionRequest` | auto-approve / deny を返せる |
| Copilot CLI | `permissionRequest` | 同上。**http ハンドラー**対応 (ローカル HTTP へ POST、スクリプト不要) |

設定場所: `~/.claude/settings.json` / `~/.codex/hooks.json` (or `config.toml`) / `~/.copilot/hooks/*.json`

出典:
- https://code.claude.com/docs/en/hooks
- https://developers.openai.com/codex/hooks
- https://docs.github.com/en/copilot/reference/hooks-configuration

## 3. アーキテクチャ案

```
[各ペインの CLI] --hook(同期ブロック)--> [既存 Pipe amm-mcp-{user} の amm/approval メソッド]
                                              │
                                   集約パネル/トーストに表示
                                   「Claude(pane3) が Bash 実行許可を要求」
                                              │
                        人間が [許可] [拒否] [そのペインへ移動] をクリック
                                              │
                  hook へ allow/deny を返却 ──┘ (CLI 側のプロンプトには触れず解決)
```

- **受け口**: 既存 `\\.\pipe\amm-mcp-{user}` に独自 JSON-RPC メソッド `amm/approval` を追加 (**専用 Pipe は新設しない**)。`amm/notify` (UDR-7e1) で同居方式の前例を確立済み。McpPipeServer は接続ごとに独立セッションなので、確認待ちの長時間ブロックが MCP 通信や notify を妨げない。必要改修は (1) 該当メソッドの async 化 (人間の回答を TaskCompletionSource で await)、(2) `amm-mcp.exe approve` サブコマンド (要求送信 → 回答待ち → `permissionDecision` を stdout 返却、タイムアウト時は "ask" でペイン内プロンプトにフォールバック) の 2 点
- **ペイン特定**: 実装済みの `AMM_NOTIFY_ID` (ConPTY 起動時注入、UDR-7e1) をそのまま使う → 集約パネルに発信元表示 + 「そのペインへ移動」ボタン
- **フック登録**: 実装済みの `HookCliRegistrar` (UDR-7e1、自己ガード形式) の対象イベントに `PreToolUse` を追加するだけ。ダイアログのチェックボックスは「Claude Code フック」1 つのまま両機能をカバー
- **Copilot の http ハンドラー直結**: Pipe を 2 本にする理由にはならない (HTTP リスナーを足すかどうかの別論点)。MVP は ps1 ラッパー経由で Pipe 1 本に統一し、http 直結は必要になってから検討

## 4. 段階導入案

- **Level 1 (低リスク)**: 通知のみ。トースト + クリックでペインにジャンプ、回答はペイン内で行う。回答ロジックを持たないためタイムアウト問題はほぼ無視できる
  → **実装済み (2026-06-05, UDR-amm-20260605T1043-3af)**: `HasAttention` フラグ + タイトル ⚠ + ボタン/タイトルバーのオレンジ + 非フォアグラウンド時 FlashWindowEx 点滅。解除はペインアクティブ化 / Running 遷移 / idle 通知。トーストは WinRT 依存過剰のため不採用
- **Level 2**: 集約パネルで allow/deny まで返す (フックが人間の回答を待ってから返却)
  → **実装済み (2026-06-05, UDR-amm-20260605T1124-9c4)**: PermissionRequest hook +
  `amm-mcp.exe approve` + `amm/approval` (既存 Pipe) + `ApprovalBroker` 台帳 +
  非モーダル NOACTIVATE ポップアップ。無回答 45 秒 / ペインアクティブ化 / 切断は
  すべてペイン内プロンプトへフォールバック。表示メニューに即時トグル。
  MVP は Claude Code のみ (Codex / Copilot は §5 のリスクにより見送り)

## 5. 注意点・リスク

- **タイムアウト**: フックは同期ブロック。離席時に CLI 側がタイムアウトする (Claude 既定 60 秒・設定可 / Copilot 既定 30 秒で `preToolUse` はフェイルクローズ=拒否)。タイムアウト時は Claude は `permissionDecision: "ask"` で通常のペイン内プロンプトへフォールバック可能 → この設計が安全
- **Codex のカバレッジ穴**: シェルコマンドは確実にフック発火するが、`apply_patch` や一部 MCP ツールで発火しない報告あり。Codex だけ「シェル確認の集約」に留まる可能性
- **信頼要件**: Codex は信頼されていないプロジェクトローカルフックをスキップする

## 6. 実装着手時の TODO

- [ ] Level 1 / 2 のどちらから始めるか決定 (→ UDR 候補)
- [x] 受け口: 既存 MCP Pipe に `amm/approval` メソッド同居で決定 (2026-06-05、§3 参照。専用 Pipe / HTTP は不採用)
- [x] フックの同梱形態: `amm-mcp.exe` のサブコマンド方式で決定 (notify の前例を踏襲、approve サブコマンドを追加)
