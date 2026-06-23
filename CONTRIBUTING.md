# Contributing

> 本リポジトリは Apache License 2.0（[LICENSE](LICENSE)）で配布されています。

## 最初に読むもの
- **`HANDOVER.md`** — 作業の現在地（セッション開始時に必読）。
- `CLAUDE.md` / `AGENTS.md` — AI への指示・リポジトリ作法。

## 開発環境（前提）
- .NET SDK 9.x（`net9.0-windows`）、Windows 11、Visual Studio / VS Build Tools。
- WebView2 ランタイム。
- 詳細は `docs/build.md`。

## ビルド・テスト
```powershell
dotnet build Amm.sln -c Debug
dotnet test  Amm.sln
```
配布物の発行・インストーラは `tools/publish.cmd` / `tools/build-installer.cmd`。

## ブランチ / コミット / PR
- ブランチ名: `<type>/<kebab>`（例: `feat/approval-hub`, `fix/pipe-deadlock`）。
- コミットは Conventional Commits 準拠（例: `feat(amm): ...` / `fix(amm): ...`）。
- Git タグ: `v<SemVer>`（例: `v0.1.3`）。

## 意思決定・課題の記録
- **決定（アーキ/要件解釈/技術選定）は UDR に記録**: `/udr-record`（→ `.udr/records/`）。
- **作業課題は `tasks/`**（`TASKS.md` / `backlog.md` / `done/`）。
- レビュー/議事録/QA の正式証跡は `records/`。

## 命名規則
- フォルダ: 小文字（必要なら kebab）。.NET プロジェクト/名前空間: PascalCase + ドット階層。
- 日付は `YYYY-MM-DD`、SPEC は `SPEC-<4桁>-<kebab>.md`。詳細は構成案ドキュメント参照。

## 秘密情報
- 署名鍵・接続文字列・`config.yaml` 実体はコミットしない（`*.example.*` のみ管理）。
