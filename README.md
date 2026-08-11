# amm

[![CI](https://github.com/otinori/amm/actions/workflows/ci.yml/badge.svg)](https://github.com/otinori/amm/actions/workflows/ci.yml)

Windows ネイティブの **複数 AI エージェント集約・実行層**。Claude Code / GitHub Copilot CLI / OpenAI Codex CLI / Gemini CLI 等の対話型 CLI を単一ウィンドウ内のペインへ並走させ、共通入力欄から制御できる。

**License**: Apache 2.0

> ℹ️ 本実装は `openspec/changes/archive/2026-07-26-migrate-to-tauri/` の移植を経た **Tauri(Rust)版（`src/apps/Amm/`）**。単一ウィンドウ内でのペイン管理方式（MDIではない）。詳細は[`docs/design/architecture.md`](docs/design/architecture.md)を参照。

---

## 概要

### 開発動機

AI エージェント CLI を同時並行かつ同期しながら動かしている人の記事をインターネットで見て、「これやってみたい！！」と思いつつも、tmuxがWindowsだとWSL使わないと動かせないため、いっそのことWindowsネイティブで作ってしまえ！！と思ったことがきっかけ。

### ゴール

複数の AI エージェント CLI をペインに並べて、実行状況を確認、個別指示するだけでなく、起動したAIエージェント同士が互いにコミュニケーションをとりながら目的に向かって動作できる様にする。そのために、MCPやCLI経由で各CLIへ指示や応答を受け取る仕組みを実装する。

### 何ができるのか

- 単一ウィンドウ内の複数ペインに複数のターミナル（エージェント CLI）を並走させる
- 共通入力欄から選択中または全ターミナルへ一斉送信
- ペインと紐付けた指定テキストエディタからのプロンプト送信（上書き保存でプロンプト送信する）
- `amm-mcp.exe` で MCP クライアント（Claude Code / Claude Desktop 等）から各ペインを外部制御
- **MCP ゲートウェイ**: 外部 stdio MCP サーバを子プロセスとして管理し、ツールを `{サーバ名}/{ツール名}` 形式で集約公開
- **Amm.PowerShell**: `Open-AmmWindow` / `Send-AmmMessage` 等のコマンドレットで PowerShell スクリプトから amm を制御
- `profiles.amm` でターミナル構成・起動コマンド・レイアウトをプロファイル管理
- IME 対応・ホイールスクロール正規化・許可ダイアログの集約（Approval Hub）
- ウィンドウクローズ時の git commit / push 確認ガード

**配布物**:

| ファイル | 役割 |
|---|---|
| `amm.exe` | GUI 本体（Tauri v2(Rust) + WebView2 + xterm.js + ConPTY、単一ウィンドウ内ペイン管理） |
| `amm-mcp.exe` | MCP stdio サーバ / CLI / REPL |
| `Amm.PowerShell` 他 | PowerShell 連携モジュール（`.psm1`スクリプトモジュール、コンパイル不要） |

---

## 動作環境

### 前提条件

- WebView2 Runtime（Edge 入りの Win10/11 では通常プリインストール済）
- ビルドする場合は Rust ツールチェイン（配布済みバイナリを使う場合は不要）

### 対応プラットフォーム

- **Windows 10 / Windows 11** — 正式対応（開発の起点、tmux代わりに作成した経緯からWindowsネイティブを主眼に置いている）
- **macOS** — 対応作業中（`openspec/changes/add-macos-support/`、Apple Silicon実機で主要機能を確認済み。`.dmg`配布・全capability棚卸しは未完了）
- Linux — 未対応（将来検討、`docs/design/cross-platform-feasibility.md`参照）

---

## クイックスタート

### ビルドして起動

```cmd
cargo build -j 2 --manifest-path src\apps\Amm\src-tauri\Cargo.toml
artifacts\target\debug\amm.exe
```

### 配布物・インストーラーを作る

```cmd
tools\publish-tauri.cmd            REM → artifacts/publish/tauri-windows/out/
tools\build-installer-tauri.cmd    REM → artifacts/packages/tauri-windows/amm_{version}_x64-setup.exe(NSIS) + .msi
```

詳細は [`docs/build.md`](docs/build.md) を参照。

---

## 主な使い方

起動後、`profiles.amm` に定義したエージェント CLI がペインとして自動起動する。共通入力欄から送信先を選んでコマンドを入力する。

MCP 経由で外部から制御する場合は `amm-mcp.exe` を MCP クライアントに登録する。手動で場所を探す必要はなく、
amm 本体のツールバーの「CLI への MCP 登録...」ボタンが `amm-mcp` の実体パスを自動解決して表示し、
Claude Code / Codex / Copilot CLI 等への登録も代行する。
（macOS: `amm-mcp` は `.app` バンドル内 `Contents/Resources/amm-mcp` に同梱されるが、Windows 版のような
インストーラ主導の PATH 登録が無いため `amm-mcp` を素のコマンド名としてターミナルから直接起動することはできない。
上記ボタン経由の登録を使うか、ターミナルから直接使いたい場合は同ボタンのダイアログに表示されるフルパスを使うこと。）

詳しい操作方法・`profiles.amm` スキーマ・MCP 連携設定は [`docs/manual/user-guide/usage.md`](docs/manual/user-guide/usage.md) を参照。

---

## 構成

```
amm/
├── src/
│   ├── apps/Amm/                  # GUI本体+amm-mcp 一式（Rust/Tauri v2）
│   └── modules/Amm.PowerShell/    # PowerShellスクリプトモジュール
├── tools/
│   └── publish-tauri.cmd / build-installer-tauri.cmd # self-contained配布物/NSIS+MSI生成
├── docs/
│   ├── build.md                  # ビルド・テスト・publish ガイド
│   └── manual/user-guide/        # 利用者向けドキュメント
└── openspec/                     # specs/(仕様書、capability別) / changes/archive/2026-07-26-migrate-to-tauri/(移植の変更提案・タスク、archive済み)
```

詳しいビルド手順は [`docs/build.md`](docs/build.md) を参照。

---

## ドキュメント

| ドキュメント | 内容 |
|---|---|
| [`docs/manual/user-guide/usage.md`](docs/manual/user-guide/usage.md) | 使い方ガイド（メニュー / キーボード / profiles.amm / MCP 連携） |
| [`docs/build.md`](docs/build.md) | ビルド・テスト・publish・プロジェクト構成 |
| [`openspec/specs/`](openspec/specs/) | 仕様書（capability 別、OpenSpec 形式） |
| [`docs/design/amm-companion-boundary.md`](docs/design/amm-companion-boundary.md) | amm / Companion の責務境界 |

---

## マルチエージェント協働

本リポジトリでは Claude Code / Codex CLI / GitHub Copilot 等の AI エージェントが共通ポリシーで作業する。

| ファイル | 内容 |
|---|---|
| [`AGENTS.md`](AGENTS.md) | 全エージェント共通ポリシー（最初に読む） |
| [`CLAUDE.md`](CLAUDE.md) | Claude Code 固有指示 |

---

## ライセンス

Apache License 2.0 — [LICENSE](LICENSE) を参照。
