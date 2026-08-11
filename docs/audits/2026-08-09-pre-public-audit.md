---
title: "amm — 2026-08-09"
type: audit
verdict: "🟡 条件付き可"
repo: "otinori/amm"
repo_url: "https://github.com/otinori/amm"
audit_date: 2026-08-09
commit_sha: "6fa38cd6"
must_count: 0
should_count: 1
optional_count: 0
human_gate: "未確認あり"
skill_version: "2.3"
criteria_version: "2.22"
tags: [audit, pre-public-audit]
---

# 公開前監査レポート — amm

> このファイルはローカル記録用 `PRE_PUBLIC_AUDIT_REPORT.md` を、リポジトリへコミットしても安全な形に一般化したコピーです（ローカル絶対パスをリポジトリ名/URLへ置換）。詳細版は各自のローカル環境で `pre-public-audit` skill を再実行して生成してください。

## 監査メタ情報（再現性）
- 監査対象 commit SHA: `6fa38cd6`（origin/main）
- 実施日時: 2026-08-09
- 使用ツール: gitleaks v8.30.1（全ブランチ履歴、--redact）、trufflehog v3.95.9（--only-verified）、gh CLI（Issue/PR/Release/fork/dangling commit実在確認）
- skill version: 2.3 / criteria version: 2.22
- 対象規模: 245コミット / 590ファイル

## 意思決定者向けサマリ
- 公開して良いか: 条件付き可
- 最大のリスクは何か: リポジトリ直下に、実際に開発機で動かした際の設定ファイルが1つ誤って紛れ込んでいた（秘密情報や他人の個人情報ではなく、ローカルの作業フォルダパスが少し見える程度）
- 公開前に人が必ずやること: (1) 修正PR(#16)のマージ、(2) IP・ライセンス互換性・輸出管理・特許ゲートの最終確認

## 総合判定
- 技術判定: 🟡（Must 0件・Should 1件）
- 事業/法務判定: 🟡（人間確認ゲート一部未確認）
- **最終判定**: 🟡 条件付き公開可

## 🟡 Should — 対応推奨

### [S-1] `profiles.amm`（リポジトリ直下）が実開発機のライブ実行時状態
- **種別**: F(構成/不要ファイル混入) + C(ローカル絶対パス露出)
- **場所**: `profiles.amm`（コミット `c5926a9`「WIP: 作業を保存」、2026-07-29）
- **内容**: 配布用の既定プロファイルは `src/apps/Amm/profiles.amm` が正本だが、それとは別にリポジトリ直下に413行のプロファイルファイルが存在。中身は実際に amm.exe を動かした際の自動保存データで、実際の作業ディレクトリパスとウィンドウ座標などが記録されていた。
- **なぜ問題か**: 秘密情報・他人のPIIではないため深刻ではないが、(a) ローカル環境のフォルダ構成が公開される、(b) クローンした人が「これが本物の既定設定？」と誤解しうる純粋な混入物。
- **修正案**: `git rm profiles.amm` + `.gitignore` に `/profiles.amm` を追記。
- **対応状況**: 対応済み（[PR #16](https://github.com/otinori/amm/pull/16) として修正・push済み。マージは未実施）

## 人間確認ゲート
チェック済み（`- [x]`）は実際に AskUserQuestion（TUI）で本人に確認・回答を得た項目のみ。AIの推測だけでは未確認のまま残す。

- [x] 公開承認: 個人アカウント(otinori)の私物リポジトリと確認
- [x] 契約・NDA・開発機材の私物性: 完全に私物環境（業務時間外・私物PC）と本人回答済み → 職務著作リスクなし
- [ ] IP・著作権帰属: 未確認（技術監査上はLICENSE=Apache-2.0・Copyright="The amm authors"で整合しているが、本人には未確認）
- [ ] 依存ライセンス互換性: 未確認（専用ツール未使用、簡易確認のみ。npm/NuGet/Cargo依存はMIT/Apache-2.0/BSD-3-Clause/ISC中心でコピーレフト混入は未検出）
- [ ] 輸出管理: 未確認（該当性低いと判断したが本人確認は未実施）
- [ ] 特許: 未確認（該当性低いと判断したが本人確認は未実施）

## 良好点
- 秘密情報・認証情報は0件（gitleaks全ブランチ+trufflehog+手動パターンで確認）。gitleaks 8件ヒットは全てxterm.js等の第三者ライブラリのminify変数名による誤検知。
- ローカルに188件のdangling commitを検出したが、全件GitHub側に一度も存在しないことをAPI照合で確認済み（design/impl/testサブブランチのsquash-merge運用の副産物、公開リスクなし）。
- コミットメタデータは全て`@users.noreply.github.com`/`noreply@anthropic.com`のみ、実名・実メールの露出なし。
- Issue 0件、PR 15件は全てmerged/closed、fork 0件。
- LICENSE=Apache-2.0、Copyright="The amm authors"で個人/組織名の不一致なし。NOTICE/THIRD-PARTY-NOTICES.mdによる第三者ライブラリ帰属表示も整備済み。
- TODO/FIXME/HACK、卑語・不適切表現ともに0件。

## 検出漏れの可能性 / 限界
- Release添付インストーラーバイナリ（NSIS/MSI）自体は未展開確認。
- 画像アセットのEXIFはexiftool未導入のためサンプル確認のみ。
- 依存ライセンスの専用ツール（license-checker等）による棚卸しは未実施。
- Branch protection設定はprivateリポジトリのためAPI取得不可（公開後に手動設定推奨）。

## 承認記録（公開 GO の証跡）
- 公開承認者（氏名/役職）: otinori（個人リポジトリオーナー）/ 承認日: ______
- 残存 Must: 0 件 / Should（受容含む）: 1 件（PR #16 マージ待ち）
- 法務確認: 未（個人開発のため対象外の可能性、要最終判断）
- 条件付き承認の条件: PR #16 のマージ、および上記未確認ゲート項目の最終確認

## 関連
- PR: https://github.com/otinori/amm/pull/16
