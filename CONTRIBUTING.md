# Contributing

> 本リポジトリは Apache License 2.0（[LICENSE](LICENSE)）で配布されています。

## 最初に読むもの
- `CLAUDE.md` / `AGENTS.md` — AI への指示・リポジトリ作法。

## 開発環境（前提）
- Rust ツールチェイン、Windows 11 または macOS。
- Windows: WebView2 ランタイム。
- 詳細は `docs/build.md`。

## ビルド・テスト
```powershell
cargo build --manifest-path src\apps\Amm\src-tauri\Cargo.toml
cargo test  --manifest-path src\apps\Amm\src-tauri\Cargo.toml
```
配布物の発行・インストーラは `tools/publish-tauri.cmd` / `tools/build-installer-tauri.cmd`（macOSは`tools/publish-tauri-macos.sh` / `tools/build-installer-tauri-macos.sh`）。

## ブランチ / コミット / PR
- 作業ブランチ: `claude/<task>`（→ `main` へ最終 PR、CI が動く唯一のポイント）。設計・製造・テストのフェーズ別サブブランチで開発ループを回す（詳細・省略可否は `AGENTS.md §3`）。
- コミットはフェーズプレフィックス必須: `design:` / `impl:` / `test:` / `retro:`（Conventional Commits ではない。詳細は `AGENTS.md §3.3`）。
- Git タグ: `v<SemVer>`（例: `v0.1.3`）。

## 意思決定・課題の記録
- **仕様（as-is 挙動）は `openspec/specs/`**（capability 別、OpenSpec 形式）。変更提案は `openspec/changes/`。
- **作業課題は `tasks/`**（`TASKS.md` / `backlog.md` / `done/`）。
- レビュー/議事録/QA の正式証跡は `records/`。

## 命名規則
- フォルダ: 小文字（必要なら kebab）。
- 日付は `YYYY-MM-DD`、SPEC は `SPEC-<4桁>-<kebab>.md`。詳細は構成案ドキュメント参照。

## 秘密情報
- 署名鍵・接続文字列・`config.yaml` 実体はコミットしない（`*.example.*` のみ管理）。
