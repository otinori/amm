# CLAUDE.md — Claude Code 向け作業ガイド

**最初に `AGENTS.md` を必ず読むこと。** 本リポジトリの開発ループ（設計→製造→テスト→振り返り）、GitHub Actions 規約等のマスタは `AGENTS.md` に集約されている。本書は Claude Code 固有の追加指示と、作業に必要なコードベース早見表を扱う。

---

## 1. プロジェクト概要

**amm** は Windows ネイティブの複数 AI エージェント集約・実行層。Claude Code / GitHub Copilot CLI / OpenAI Codex CLI / Gemini CLI 等の対話型 CLI を単一ウィンドウ内のペインに並走させ、共通入力欄から制御する。詳細な背景・機能一覧は [`README.md`](README.md) を参照。

**配布物**:

| ファイル | 役割 |
|---|---|
| `amm.exe` | GUI 本体（Tauri v2(Rust) + WebView2 + xterm.js + ConPTY） |
| `amm-mcp.exe` | MCP stdio サーバ / CLI / REPL（GUI 内蔵 Named Pipe サーバへの薄いブリッジ） |
| `Amm.PowerShell` 他 | PowerShell 連携モジュール（`.psm1`、`Open-AmmWindow` / `Send-AmmMessage` 等） |

対応プラットフォームは **Windows 10/11**（tmux 代替として Windows ネイティブで作る、が当初の開発動機）。2026-07-29に開発フォーカスをMacへ移し、**macOS対応を実行フェーズで進行中**（`openspec/changes/add-macos-support/`、Apple Silicon実機で主要機能を確認済み、`.dmg`配布・全capability棚卸しは未完了）。Linux対応は未着手（`docs/design/cross-platform-feasibility.md`参照）。

---

## 2. リポジトリ構成

> ℹ️ **Tauri版が唯一の実装**: `openspec/changes/archive/2026-07-26-migrate-to-tauri/`（phase 1〜8、実機パリティ検証含む）を経てTauri(Rust)実装（`src/apps/Amm/`）へcutoverした後、公開準備の一環として旧.NET WinForms(MDI)実装（`src/apps/Amm.net/`等）を撤去した。詳細は `openspec/specs/`（capability別の正本仕様）を参照。

```
amm/
├── src/
│   ├── apps/Amm/                  … GUI本体+MCP 一式（Rust/Tauri v2）
│   │   ├── src-tauri/src/         … main.rs/lib.rs（Tauriコマンド）・mcp.rs・profile.rs・gateway.rs・approval.rs・hook_cli.rs 等
│   │   ├── src-tauri/src/bin/amm-mcp/ … amm-mcp.exe（同上、Rust実装）
│   │   └── public/                … フロントエンド（index.html/style.css、xterm.js同梱。JSは責務別7ファイルに分割: pane-layout.js/pane-lifecycle.js/send-helpers.js/dialogs-quick-stats.js/dialogs-profile-mcp.js/input-history.js/events-integration.js、index.htmlの記載順=旧app.jsの元の並び順を維持）
│   ├── modules/Amm.PowerShell/    … PowerShellスクリプトモジュール（.psm1、コンパイル不要）
│   ├── libs/                      … 将来の共有クレート分離用（現状ほぼ空）
├── tests/e2e-tauri/                … Playwright CDP経由の実機GUI検証方法一式（README.md参照）
├── tools/                         … publish-tauri.cmd/build-installer-tauri.cmd（NSIS+MSI）、publish-tauri-macos.sh/build-installer-tauri-macos.sh
├── docs/
│   ├── build.md                   … ビルド/テスト/publish/プロジェクト構成の正本（開発者向け）
│   ├── design/architecture.md     … 現状構成
│   ├── design/spec/archive/       … 旧仕様文書（spec.md 等、openspec/specs/ へ統合済み。経緯保持用）
│   └── manual/user-guide/usage.md … エンドユーザー向け使い方ガイド（Tauri版基準）
├── tasks/                         … TASKS.md（進行中） / backlog.md / done/ / pending-real-machine-verification.md（実機検証記録）
├── records/                       … 会議・レビュー・テスト報告の正式証跡
├── openspec/                      … OpenSpec。specs/ は capability 別の正本仕様（16件、Tauri版準拠）、changes/ は変更提案・archive（migrate-to-tauriを含む）
├── reference/                     … プロトタイプ・外部参照（src には混ぜない）
└── artifacts/                     … ビルド成果物（gitignore）。target(Rust、.cargo/config.toml) の
                                       ビルド中間ファイルと、publish/packages配下の配布物出力を集約（publish|packages/{tauri-windows,
                                       tauri-macos}/、詳細はdocs/build.md参照）
```

詳細なサブプロジェクト境界・依存関係は [`docs/build.md`](docs/build.md)、現状アーキテクチャは [`docs/design/architecture.md`](docs/design/architecture.md) を参照。

---

## 3. 開発ワークフロー

### ビルド・テスト — Tauri版（`src/apps/Amm/`、新規実装作業はこちら）

```cmd
cargo build -j 2 --manifest-path src\apps\Amm\src-tauri\Cargo.toml
cargo test -j 2 --manifest-path src\apps\Amm\src-tauri\Cargo.toml
artifacts\target\debug\amm.exe
```

`cargo build -j 2`（並列度2）を推奨: デフォルト並列度だと環境によって外部から強制終了されることがある（`tasks/retro-pending.md`参照）。ビルド中間生成物は`.cargo\config.toml`の`target-dir`設定により`artifacts\target\`に集約される。

### 配布物生成 — Tauri版

```cmd
tools\publish-tauri.cmd            REM → artifacts/publish/tauri-windows/out/ に self-contained 一式
tools\build-installer-tauri.cmd    REM → artifacts/packages/tauri-windows/amm_{version}_x64-setup.exe(NSIS) + amm_{version}_x64_en-US.msi(MSI)
```

`cargo-tauri`（`cargo install tauri-cli --version ^2`）導入が前提。

### ⚠️ Claude Code on the web（本セッションのような非 Windows 環境）での制約

`src/apps/Amm/src-tauri`は`windows`クレート（Win32 API直接呼び出し）に依存するため、Linuxコンテナ上では`cargo check`すら通らない箇所がある（`#[cfg(windows)]`で切り分けていない一部コードパス）。`cargo`自体は多くの場合インストール済みだが、それでもビルド・テストの実行確認はできない。

この環境で作業する場合:

- コード変更はコンパイルを通さず静的にレビューする
- 実機ビルド・テスト・UI 動作確認が必要な変更は、その旨をユーザーに明示し Windows/macOS 環境での確認を依頼する
- CI（`.github/workflows/ci.yml`）は `windows-latest` ランナーで実行され、`rust-build-and-test`ジョブがTauri版をビルド・テストする

### バージョン管理

`src/apps/Amm/src-tauri/Cargo.toml`の`version`と`tauri.conf.json`の`version`（現在ともに`1.2.0`、両ファイルを同期させて手動更新）。Git タグは `v<SemVer>` に一致させる。変更履歴は [`CHANGELOG.md`](CHANGELOG.md)（Keep a Changelog / SemVer）。

---

## 4. 主要な規約

### ブランチ・コミット（AGENTS.md §3 が正本）

本リポジトリは設計→製造→テスト→振り返りのフェーズ別サブブランチで開発ループを回す（詳細は `AGENTS.md §3`）。

- 作業ブランチ: `claude/<task>`（→ `main` へ最終 PR、CI が動く唯一のポイント）
- サブブランチ: `claude/<task>/design` / `impl` / `test`（→ 作業ブランチへ PR、CI 非トリガー）
- コミットはフェーズプレフィックス必須: `design:` / `impl:` / `test:` / `retro:`（`retro:` は 1 改善 1 コミット）
- ドキュメントのみの OpenSpec change（`src/` 変更なし）もサブブランチは省略可だが、`claude/<task>` ブランチ + `main` への PR は必須（`AGENTS.md §3.6`）

- テストフェーズの PR 作成前は `/check-pr` を必ず実行（`AGENTS.md §3.5` の完了条件ゲート）
- 新規 GitHub Actions ワークフローには `concurrency` ブロック必須（`AGENTS.md §4.1`）。`dependabot.yml` / `codeql.yml` 等、副作用の大きい設定変更は **コミット前にユーザーへ告知**（`AGENTS.md §4.2`）

### 命名・秘密情報（CONTRIBUTING.md）

- フォルダは小文字（必要なら kebab）
- 日付は `YYYY-MM-DD`、SPEC は `SPEC-<4桁>-<kebab>.md`
- 署名鍵・接続文字列・`config.yaml` 実体はコミットしない（`*.example.*` のみ管理）

### 判断記録・課題管理

- **作業課題は `tasks/TASKS.md`（進行中）/ `backlog.md`（未着手）**
- 作業中に発生した問題（ビルドエラー・やり直し・想定外の副作用等）は都度 `tasks/retro-pending.md` に追記し、`/retro` で振り返る（`AGENTS.md §3.4`）
- 機能の実装が完了したら、対応する `backlog.md` / 元 spec の "Draft"/"未着手" 等のステータス記述を同じタイミングで更新する（放置すると実態と乖離した backlog が残る）

### セッション開始時の必読順序

1. `HANDOVER.md`（存在すれば） — 現在地・次にやること・触るな注意（最優先）。`.gitignore`対象の私的な作業ノートのため、このリポジトリのクローン元によっては存在しない場合がある
2. `AGENTS.md` — マルチエージェント共通ポリシー（本書の親）
3. 本書（`CLAUDE.md`） — Claude Code 固有の追加指示

---

## 5. Claude Code 固有の運用

### skill の使い方

リポジトリ内蔵の skill（`.claude/skills/`）は以下:

| コマンド | 用途 |
|---|---|
| `/check-pr` | PR 作成前チェック（branch / concurrency / 副作用告知 / version 確認）。製造→テスト移行ゲート |
| `/retro` | 振り返り対話フロー。問題をカテゴリ分類し AGENTS.md / skills へ改善を反映 |
| `/opsx:propose` `/opsx:apply` `/opsx:explore` `/opsx:archive` | OpenSpec ワークフロー（`openspec/config.yaml` 参照） |

### OpenSpec ドキュメントオンリー change

`src/` に変更がない OpenSpec change（仕様のリバース起票・棚卸し等）では、`tasks.md` を「実装チェックリスト」ではなく「レビュー確認事項のチェックリスト」として運用してよい。ブランチ運用は `AGENTS.md §3.6` を参照（サブブランチ省略可、`claude/<task>` ブランチ + `main` PR は必須）。

### amm 自体を MCP CLI 経由でテストする

amm 自体が MCP サーバーであり、`amm-mcp` という CLI クライアントを標準で持っている。GUI 操作の自動化（cliclick 等、座標ズレに弱い）に頼る前に、`amm-mcp --bridge` を自分自身へ疎通確認用のダミーサーバーとして登録したり、`pane/open`・`notify`/`approve`（`AMM_NOTIFY_ID` 環境変数経由）を CLI から直接呼び出したりすることで、対象機能がバックエンドロジックの問題か UI 描画の問題かを切り分けやすく、再現性も高い。

### バックグラウンド並列エージェント利用時の注意

大規模なドキュメント生成・調査タスクを複数のバックグラウンドエージェントに分割する際は、並列数を上げすぎない（同時にセッション上限へ達し全滅した実例あり）。目安として同時実行は 3〜4 程度に留め、必要なら順次バッチに分ける。

### `run_in_background` の "killed" 誤報告

Bash ツールの `run_in_background` 経由で起動した `cargo build`/`cargo test` 等が、実際には成功しているにもかかわらず "killed" として報告されることがある（複数回の再現を確認済み）。`run_in_background` の監視機構側の誤検知の可能性が高く、コンパイラ自体の問題ではない。"killed" と報告されても即座にコードや環境を疑わず、まずフォアグラウンド実行（`&` + `wait` でのポーリングループ等）に切り替えて再現するか確認する。

### Codex / Gemini CLI との共存

Gemini CLI（`.gemini/`）向けにも `opsx` 系コマンドが並行整備されている（`.gemini/commands/opsx/*.toml`）。

---

*最終更新: 2026-08-10 / 公開準備に伴いUDR自動検知・記録の仕組み一式（`.udr/`・UDRサマリ・関連skill/plugin）を撤去*

<!-- low-noise-response:start -->
## 応答スタイル (low-noise-response)

- **前置き禁止**: 「承知いたしました」「おっしゃる通りですね」等のクッション言葉・社交辞令・挨拶は書かない。一文字目から結論・判断材料に入る。
- **結論ファースト**: 結論・判断要点を冒頭に置く。根拠や補足は必要な分だけ後に続ける。
- **密度優先**: ダラダラした段落より箇条書き・短文を優先する。当たり前の前提知識や一般論の解説は原則省く。
- **既出の再説明禁止**: 会話内で一度使われた・前提として提示された専門用語や概念を、後から初心者向けに言い換えたり再解説したりしない。ユーザーの専門度に応じて説明密度を変える。
- **確認は絞る**: 結果に大差ない些細な選択（フォーマットや細部の仕様等）はデフォルトを選んで進め、逐一確認しない。一方、判断に不可欠な前提が欠けている場合は、勝手に推測せず端的に質問する。

より厳格な確認プロトコルや専門度推定を伴う深い作業（曖昧な要件の仕様化、決定事項の整理など）には `low-noise-response` skill が別途トリガーされる。
<!-- low-noise-response:end -->
