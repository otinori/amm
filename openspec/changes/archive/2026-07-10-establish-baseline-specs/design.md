## Context

amm は現在 Windows ネイティブ（.NET 9 WinForms + WebView2 + ConPTY）でのみ実装されており、`docs/design/spec/spec.md`（リバース生成v1、2026年時点）・`spec-v2.md`（Phase別変更指示書）・8本の `req-*.md` に仕様情報が分散している。加えて Git 連携・チャット記録・入力履歴・hook CLI 登録の4機能はコードのみが正で、文書が一切存在しない。

将来の別言語への全面移植に向け、移植元となる正確な as-is 仕様を OpenSpec の Requirement/Scenario 形式で確立する必要がある。`openspec/specs/` は現状空のため、本 change は「変更」ではなく「ベースライン確立」であり、全 capability を `## ADDED Requirements` として起票する。

## Goals / Non-Goals

**Goals:**
- 現行実装（`src/apps/Amm`, `src/apps/Amm.Mcp`, `src/modules/Amm.PowerShell`）の外部から観測可能な振る舞いを、16 capability に分割して OpenSpec 形式で記述する
- 既存文書（spec.md/spec-v2.md/req-*.md）を情報源として活用しつつ、実装コードと突き合わせて正確性を検証する（"Draft" ステータスの req は特に要検証）
- 未文書化の4機能（git-integration/chat-recording/input-history/hook-cli）をソースコードとテストコードから新規に起票する
- 移植プロジェクトが参照可能な粒度（機能単位でテスト可能な Scenario）に落とし込む

**Non-Goals:**
- 新機能の提案・要件変更は行わない（本 change に `## MODIFIED Requirements` は存在しない）
- 移植先言語・アーキテクチャの選定は行わない（本 change はあくまで移植元の記録）
- `docs/design/spec/` 配下の既存ファイルの削除・書き換えは行わない（統合後の扱いは別タスク）
- Amm.Desktop（Mac/Avalonia の実験、ソース欠落・git 管理外）はスコープ外

## Decisions

- **capability 粒度は 16 分割**: 既存の12機能（spec.md の章立て4つ + req-*.md 8本）＋未文書化4機能をそのまま capability 境界とする。1機能=1ファイルとし、将来の移植でモジュール単位の対応付けをしやすくする（粗い統合案は却下: 1ファイルが肥大化し差分追跡が困難になるため）
- **1つの baseline change で一括確立**: capability グループ毎に change を分割する案もあったが、全体像を1回のレビューで見渡せる方を優先した
- **既存ドキュメントの再構成 vs 新規調査**: 文書化済み12機能は既存文書（spec.md/req-*.md）を主情報源としつつ、必ずソースコード・既存テスト（`tests/apps/Amm.Tests/*Tests.cs`）と突き合わせて検証する。未文書化4機能はソースコード・テストコードのみを情報源とする
- **並列リサーチ**: 16 capability を関連ソースファイル群で4グループ（MDI基盤系/MCP系/補助機能系/未文書化系）に分け、並列に調査・起票させることで作業量に対応した

## Risks / Trade-offs

- [Risk] 既存 req-*.md は "Draft" ステータスのものが多く、実装と乖離している可能性がある → Mitigation: 各 capability 起票時に該当ソースコード・テストコードで裏取りし、乖離があれば実装を正とする
- [Risk] 未文書化4機能は静的読解のみで動作を確定できない箇所が残る可能性がある（実機 UI 挙動に依存する部分など） → Mitigation: 判読できた事実のみを SHALL として記述し、不明点は本 change のレビュー時にユーザーへ明示する。実機（Windows環境）での確認が必要な箇所は別途フラグを立てる
- [Risk] 16ファイル並列生成のため、capability 間で用語・粒度の一貫性がぶれる可能性がある → Mitigation: 最終レビューパスで全ファイルの形式（`####` Scenario, SHALL/MUST, 用語統一）を横断チェックする
- [Risk] `docs/design/spec/` の既存ファイルと `openspec/specs/` が並存し二重管理になる → Mitigation: 本 change のスコープでは既存ファイルを残すのみとし、二重管理の解消（アーカイブ/廃止）は別タスクとして issue 化する

## Migration Plan

1. 本 change（`establish-baseline-specs`）を `openspec/changes/` 配下で作成・レビュー
2. `openspec archive establish-baseline-specs` で `openspec/specs/<capability>/spec.md` として確立（16ファイル新規作成、`openspec/specs/` は現状空のため既存ファイルとの衝突なし）
3. 以降の移植作業（別言語での再実装）は `openspec/specs/` を正として進める
4. `docs/design/spec/` の棚卸し（アーカイブ or 廃止判断）は別 change として起票する

## Open Questions

- `docs/design/spec/spec.md` / `spec-v2.md` / `req-*.md` は統合後どう扱うか（アーカイブ／README からのリンク削除／完全削除）は未決定。ユーザーとの別途合意が必要
- 未文書化4機能のうち実機確認が必要な箇所が見つかった場合、Windows 環境での検証をどのタイミングで挟むかは未定
