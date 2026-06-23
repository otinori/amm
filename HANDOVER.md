# HANDOVER — 申し送り（常に最新・単一ファイル）

> **AI はセッション開始時にまず本ファイルを読む。** 「前回どこまで / 次に何を / 未解決の前提 / 触るな注意」を凝縮する。
> 長文の経緯は `.udr/`（決定履歴）・`tasks/`（課題）・PR へリンクで逃がす。

## 現在地（最終更新: 2026-06-18）
- リポジトリ構成を `docs/design/Windowsアプリ_リポジトリ構成案.md` に沿って再構築。
  - `tests/Amm.Tests/` → `tests/apps/Amm.Tests/` へ移動（`.sln`/`csproj` 更新済、ビルド検証済）。
  - 不足フォルダ（`records/` `tasks/` `dashboard/` `assets/` `samples/` `build/` `src/libs/` `src/publish/` ほか）と
    ルートメタ（本ファイル・`CHANGELOG.md`・`CONTRIBUTING.md`・`SECURITY.md`・`NOTICE`・`THIRD-PARTY-NOTICES.md` 等）を新設。

## 次にやること
- `build/version.props` / `Directory.Packages.props` の **有効化（一元管理・CPM）** は未実施（ビルド非破壊のためスキャフォルドのみ）。
  必要になったら段階導入する。
- `tasks/TASKS.md` を実運用の起点に切り替える。

## 未解決の前提 / 触るな注意
- `tools/*.cmd` は **ASCII + CRLF + REM括弧なし** 必須（崩すと cmd.exe が壊れる）。
- `config.yaml`（実体）はコミットしない。`config.example.yaml` のみ管理。
- IME 二重送信ガードは複数 UDR にまたがる繊細な調整。変更時は該当 UDR を必ず確認。

## 起点リンク
- 作法: `CONTRIBUTING.md` / 文脈: `CLAUDE.md` `AGENTS.md` / 決定履歴: `.udr/` / 課題: `tasks/`
