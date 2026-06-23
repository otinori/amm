# records/ — 管理記録（証跡・監査証拠）

**性質: 原則 追記のみ（残す）。** 一度確定した記録は上書きしない。

| サブフォルダ | 用途 |
|---|---|
| `reviews/design-review/` | 設計レビュー記録（対象 `SPEC-ID` を明記） |
| `reviews/code-review/` | コードレビュー記録（PR で完結しない、出荷監査・トレーサビリティ用の正式記録のみ） |
| `reviews/release-review/` | リリース判定 / 出荷判定記録 |
| `meetings/` | 議事録 |
| `test-reports/` | テスト結果・QA エビデンス |

## ルール
- ファイル名は日付プレフィックス `YYYY-MM-DD-<kebab>.md`（例: `2026-06-11-release-review.md`）。
- **意思決定記録（ADR/UDR）はここに置かない。** 決定は `.udr/` に集約する。
- GitHub の PR/Issue で足りるものを二重管理しない。正式な証跡が要るものだけを残す。
