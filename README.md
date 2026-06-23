# amm

[![CI](https://github.com/otinori/amm/actions/workflows/ci.yml/badge.svg)](https://github.com/otinori/amm/actions/workflows/ci.yml)

Windows ネイティブの **複数 AI エージェント集約・実行層**。Claude Code / GitHub Copilot CLI / OpenAI Codex CLI / Gemini CLI 等の対話型 CLI を MDI 1 画面に並走させ、共通入力欄から制御できる。

**License**: Apache 2.0

---

## 概要

### 開発動機

AI エージェント CLI を同時並行かつ同期しながら動かしている人の記事をインターネットで見て、「これやってみたい！！」と思いつつも、tmuxがWindowsだとWSL使わないと動かせないため、いっそのことWindowsネイティブで作ってしまえ！！と思ったことがきっかけ。

### ゴール

複数の AI エージェント CLI を MDI 画面に並べて、実行状況を確認、個別指示するだけでなく、起動したAIエージェント同士が互いにコミュニケーションをとりながら目的に向かって動作できる様にする。そのために、MCPやCLI経由で各CLIへ指示や応答を受け取る仕組みを実装する。

### 何ができるのか

- MDI 画面に複数のターミナル（エージェント CLI）を並走させる
- 共通入力欄から選択中または全ターミナルへ一斉送信
- MDI 画面と紐付けた指定テキストエディタからのプロンプト送信（上書き保存でプロンプト送信する）
- `amm-mcp.exe` で MCP クライアント（Claude Code / Claude Desktop 等）から各 MDI を外部制御
- **MCP ゲートウェイ**: 外部 stdio MCP サーバを子プロセスとして管理し、ツールを `{サーバ名}/{ツール名}` 形式で集約公開
- **Amm.PowerShell**: `Open-AmmWindow` / `Send-AmmMessage` 等のコマンドレットで PowerShell スクリプトから amm を制御
- `profiles.amm` でターミナル構成・起動コマンド・レイアウトをプロファイル管理
- IME 対応・ホイールスクロール正規化・許可ダイアログの集約（Approval Hub）
- ウィンドウクローズ時の git commit / push 確認ガード

**配布物**:

| ファイル | 役割 |
|---|---|
| `amm.exe` | GUI 本体（.NET 9 WinForms + WebView2 + xterm.js + ConPTY） |
| `amm-mcp.exe` | MCP stdio サーバ / CLI / REPL |
| `Amm.PowerShell.dll` 他 | PowerShell バイナリモジュール（MSI インストール時は自動配置） |

---

## 動作環境

### 前提条件

- .NET 9.0（self-contained ビルドの場合は不要）

### 対応プラットフォーム

Windows 10 / Windows 11

※tmux代わりで作成したので、今のところWindowsネイティブのみを想定

---

## クイックスタート

### ビルドして起動

```cmd
dotnet build Amm.sln -c Debug
src\apps\Amm\bin\Debug\net9.0-windows\amm.exe
```

### 単一 exe を配布する（.NET ランタイム不要）

```cmd
tools\publish.cmd
```

`artifacts/publish/` に `amm.exe` / `amm-mcp.exe` / `profiles.amm` が生成される。

### MSI インストーラーを作る

```cmd
tools\build-installer.cmd
```

`artifacts/packages/amm-setup-{version}.msi` が生成される。詳細は [`docs/build.md`](docs/build.md) を参照。

---

## 主な使い方

起動後、`profiles.amm` に定義したエージェント CLI が MDI ペインとして自動起動する。共通入力欄から送信先を選んでコマンドを入力する。

MCP 経由で外部から制御する場合は `amm-mcp.exe` を MCP クライアントに登録する。

詳しい操作方法・`profiles.amm` スキーマ・MCP 連携設定は [`docs/manual/user-guide/usage.md`](docs/manual/user-guide/usage.md) を参照。

---

## 構成

```
amm/
├── src/
│   ├── apps/Amm/                 # GUI 本体（WinForms + WebView2）
│   ├── apps/Amm.Mcp/             # amm-mcp.exe（MCP サーバ）
│   └── modules/Amm.PowerShell/   # PowerShell バイナリモジュール
├── tools/
│   ├── publish.cmd               # self-contained 配布物生成
│   └── build-installer.cmd       # MSI 生成
├── docs/
│   ├── build.md                  # ビルド・テスト・publish ガイド
│   ├── design/spec/spec.md       # 仕様書
│   └── manual/user-guide/        # 利用者向けドキュメント
└── Amm.sln
```

---

## ドキュメント

| ドキュメント | 内容 |
|---|---|
| [`docs/manual/user-guide/usage.md`](docs/manual/user-guide/usage.md) | 使い方ガイド（メニュー / キーボード / profiles.amm / MCP 連携） |
| [`docs/build.md`](docs/build.md) | ビルド・テスト・publish・プロジェクト構成 |
| [`docs/design/spec/spec.md`](docs/design/spec/spec.md) | 仕様書 |
| [`docs/design/amm-companion-boundary.md`](docs/design/amm-companion-boundary.md) | amm / Companion の責務境界 |

---

## マルチエージェント協働

本リポジトリでは Claude Code / Codex CLI / GitHub Copilot 等の AI エージェントが共通ポリシーで作業する。判断記録は UDR で構造化する。

| ファイル | 内容 |
|---|---|
| [`AGENTS.md`](AGENTS.md) | 全エージェント共通ポリシー（最初に読む） |
| [`CLAUDE.md`](CLAUDE.md) | Claude Code 固有指示 |
| [`.udr/records/`](.udr/records/) | 判断記録（1 判断 = 1 YAML） |

---

## ライセンス

Apache License 2.0 — [LICENSE](LICENSE) を参照。
