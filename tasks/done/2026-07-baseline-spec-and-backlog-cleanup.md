# 2026-07 ベースラインSPEC確立 + backlog棚卸し

## 背景

別言語への全面移植の準備として、現行 Windows 版 amm（WinForms + Amm.Mcp + Amm.PowerShell）の
as-is 仕様を OpenSpec 形式でリバース起票した。`openspec/specs/` は当時空だったため、全16
capability を `ADDED Requirements` として一括確立する change
`openspec/changes/archive/2026-07-10-establish-baseline-specs/` を作成・archive した。

## やったこと

1. **ベースラインSPEC確立**: `openspec/specs/` に16 capability・計99 requirement を新規作成
   （mdi-window-management / profile-schema / wait-detection / tray-icon / mcp-server /
   approval-hub / mcp-gateway / mdi-window-control / hook-cli / auto-send-idle /
   command-import-export / quick-command-register / ps-module / git-integration /
   chat-recording / input-history）。うち git-integration / chat-recording / input-history /
   hook-cli の4つは既存文書が一切なく、ソースコード（+一部テストコード）のみから起票した。

2. **重大な発見 — backlog.md の実態乖離**: `tasks/backlog.md` に「未着手」として残っていた
   7項目（アイドル時自動送信・コマンド設定インポート/エクスポート・右クリッククイック送信登録・
   システムトレイ常駐アイコン・MDIウィンドウ制御・PowerShellモジュール・MCPゲートウェイ）は、
   元 `docs/design/spec/req-*.md` が "Draft" 表記のままだったことに引きずられて backlog 上も
   未着手扱いだったが、**実装コードを確認した結果すべて実装済み**であることが判明した。
   `tasks/backlog.md` から該当7項目を撤去した。

3. **docs/design/spec/ の棚卸し**: `spec.md`（リバース生成v1）・`spec-v2.md`（Phase別変更指示書）・
   `req-*.md` ×8・`template.md` を `docs/design/spec/archive/` へ移動（`git mv` で履歴保持）。
   `docs/design/spec/README.md` を新設し、現行仕様は `openspec/specs/` が正本である旨を明記。
   `README.md`・`CLAUDE.md` の該当リンク・リポジトリ構成図を `openspec/specs/` 参照に更新。

## 未解決 / 別途対応

- 元 `req-*.md`（現 `docs/design/spec/archive/`）の "Draft" ステータス表記自体は、ユーザー判断により
  今回は修正していない（過去の経緯記録として archive 内にそのまま残る）。
- 別言語への移植計画そのもの（移植先言語・アーキテクチャの選定）は未着手。今回確立した
  `openspec/specs/` を土台に、別途 `/opsx:explore` 等で検討する。

## 参照

- `openspec/changes/archive/2026-07-10-establish-baseline-specs/`（proposal.md / design.md / tasks.md）
- `openspec/specs/`（現行正本仕様、16 capability）
- `docs/design/spec/archive/`（旧仕様文書、経緯保持用）
