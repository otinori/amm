## Why

現行の amm(.NET 9 / WinForms + WebView2 + xterm.js + ConPTY)は Windows 専用実装であり、UI 層のネイティブ
コントロール(WinForms `Form`/`TextBox` 等)と WebView2 ホストのターミナル表示という「ネイティブ+Web の
二重構成」が、IME 二重送信ガード等の繊細な調整コストを生んでいる。また将来の Mac/Linux 展開の土台としても
現行スタックは適さない。

`reference/poc-tauri-terminal/` での実機検証(Windows 10 Pro および macOS 実機)により、Tauri(Rust +
WebView2/WKWebView + xterm.js + `portable-pty`)で同等以上の機能を実現できることを確認済みで、移植先
フレームワークは `UDR-amm-20260713T1037-ff3` で Tauri に決定している(判断基準: 操作性の快適さ)。

本変更は、この決定を受けて amm の GUI 本体を Tauri ベースへ全面移植する計画を具体化するものである。

## What Changes

- GUI 本体(`src/apps/Amm`)を .NET/WinForms から Tauri(Rust バックエンド + WebView2/WKWebView フロント
  エンド)へ全面書き換え
- **BREAKING**: MDI(複数子ウィンドウ)によるウィンドウ管理を廃止し、単一ウィンドウ内でのペイン管理方式
  (タイル/カスケード切替、ドラッグ移動、リサイズ、配置の記憶・復元)へ移行する(`UDR-amm-20260713T0447-98f`)
- ConPTY 統合を `node-pty`(Node.js 経由)から Rust ネイティブの `portable-pty` crate へ置き換え
  (Node.js への依存を完全に排除)
- システムメニュー拡張(「AMM ▶」サブメニュー)・タスクバー点滅(`FlashWindowEx`)・非フォーカス奪取の
  常時最前面ポップアップ(承認ハブ相当)を、WinForms の Win32 相互運用から Rust(`windows` crate)経由の
  Win32 API 呼び出しへ置き換え
- ターミナル表示自体は xterm.js を継続利用(WebView2 ホスト内で動作するため、レンダリング層は現行版と
  同一ライブラリ)
- IME 二重送信ガードのための特別な回避コードを廃止(単一 WebView 構成により、ネイティブ TextBox と
  WebView2 の 2 つの IME 合成コンテキストが競合する根本原因が構造的に解消されるため)
- **既知の制限として据え置き**: Windows 実機での ConPTY 起因の絵文字描画欠落(xterm.js/xterm.dart 双方に
  共通する Windows/xterm.js エコシステム側の未解決問題、amm 側での対応は不要と決定済み)

## Capabilities

### New Capabilities
- `pane-management`: 単一ウィンドウ内での複数ペイン管理(タイル配置/カスケード配置/ドラッグ移動/
  リサイズ/配置の記憶・復元)。`mdi-window-control` / `mdi-window-management` が担っていた要件を置き換える

### Modified Capabilities
- `mdi-window-control`: MDI 子ウィンドウ単位の個別操作(移動・リサイズ・最大化・名前変更等)という要件が、
  `pane-management` のペイン単位操作要件に置き換わる。既存要件は撤廃し `pane-management` へ統合する
- `mdi-window-management`: MDI 全体のウィンドウ管理(タイル/カスケード整列、配置の記憶・復元、
  `--all`/自動起動等)という要件が、`pane-management` の単一ウィンドウ内レイアウト要件に置き換わる

他の 14 capability(`approval-hub` / `auto-send-idle` / `chat-recording` / `command-import-export` /
`git-integration` / `hook-cli` / `input-history` / `mcp-gateway` / `mcp-server` / `profile-schema` /
`ps-module` / `quick-command-register` / `tray-icon` / `wait-detection`)は、**要件レベルでは現行仕様との
挙動パリティを目標とし、本変更でのスペック差分(delta)は作成しない**。実装をどう Rust/Tauri 側へ移すかは
`design.md` / `tasks.md` で計画する(例: `tray-icon` の `NotifyIcon` → Tauri のトレイ機能、`approval-hub` の
`FlashWindowEx` 呼び出し元が C# から Rust に変わる、等はいずれも実装詳細でありこの変更のスペック差分には
含めない)。実装過程で挙動パリティを保てないと判明した要件があれば、その時点で追加のスペック差分を起票する。

## Impact

- **`src/apps/Amm`**: 全面書き換え(WinForms プロジェクト自体を廃止し、新規 Tauri プロジェクト
  `src-tauri/` + フロントエンド資産に置き換え。`.csproj` ベースのビルドから `cargo`/`tauri-cli` ベースの
  ビルドへ移行)
- **`src/apps/Amm.Mcp`**: 現行は GUI 内蔵 Named Pipe サーバへの薄い stdio ブリッジ(net9.0、Windows 非依存)。
  Named Pipe プロトコル自体が変わらなければ影響は小さいが、GUI 側の実装言語が変わることに伴う疎通確認が必要
- **`src/modules/Amm.PowerShell`**: 同上、Named Pipe 経由の疎通確認が必要
- **`tests/apps/Amm.Tests`**: xUnit(.NET)ベースのテストは Rust 側のロジックに対応できないため、
  Rust 側のテスト体制(`cargo test` 等)を新設する必要がある。既存テストの何を引き継ぎ・何を廃止するかは
  `design.md` で計画する
- **`installer/wix`**: WiX ベースの MSI インストーラは Tauri の bundler(NSIS/MSI 生成機能)への置き換えを
  検討する必要がある
- **`tools/publish.cmd` / `build-installer.cmd`**: `dotnet publish` ベースのビルドスクリプトを
  `cargo tauri build` ベースへ置き換える必要がある
- **`.github/workflows/ci.yml`**: .NET SDK セットアップから Rust ツールチェイン(+ Visual Studio Build
  Tools 相当)セットアップへの置き換えが必要
- **`Directory.Build.props` / `build/version.props` 等の .NET 一元管理設定**: Rust 側は `Cargo.toml`
  ワークスペースでのバージョン管理に置き換わるため、対応方針の検討が必要
- **利用者への影響**: MDI(複数子ウィンドウ)に慣れたユーザーには操作体系の変更(**BREAKING**)。
  移行ガイド・告知が必要
