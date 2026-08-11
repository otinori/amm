# TASKS — アクティブな課題一覧

> 現在進行中の作業の正本（上書き更新）。完了したら `done/YYYY-MM-<kebab>.md` へアーカイブする。
> 未着手の積み残し・要望は `backlog.md` へ。

## 進行中

（現在進行中のタスクなし）

## 次にやる

（現在アクティブな次タスクなし）

## 完了（2026-07-27、直近3件）

- [x] ~~別言語への全面移植計画の検討~~ → 2026-07-26訂正: 「全面移植」は.NET→Tauri移植そのものを指すと判明。PARITY-AUDIT.mdの再点検で「Tauri版完成」までの残タスクを特定
- [x] `fix-unwired-profile-settings`の残り4件（実機確認+`/check-pr`）→ アーカイブ済み
- [x] `add-mcp-http-transport`の残り2件（実機確認、実際のObsidianサーバーで検証、実環境設定バグも発見・修正）→ アーカイブ済み
- [x] `add-editor-integration`（実装+実機確認+`/check-pr`）→ アーカイブ済み

**2026-08-10 追記**: 公開準備の一環でレガシー.NET版・UDR運用一式は削除済み。`add-agent-protocol-gateway`はACP/A2Aで`add-acp-gateway`/`add-a2a-gateway`に分割し、spec確定・validate pass済み（実装は未着手）。ユーザー方針: spec確定→公開→実装着手の順で進める。次のアクションは公開作業（コミット・レビュー）。

---

*運用: AI が主に読み書きし、人間が承認する。長文の経緯は PR へリンク。*
