# amm — ビルド / 開発ガイド

Windows / macOS ネイティブ マルチターミナル `amm.exe` / MCP bridge `amm-mcp.exe` の **実装プロジェクト**。

- **UI**: Rust (Tauri v2) + WebView2 (Windows) / WKWebView (macOS) + xterm.js、単一ウィンドウ内ペイン管理（UDR-amm-20260713T0447-98f）
- **PTY**: `portable-pty` クレート
- **オフライン**: xterm.js / addon はローカル同梱、外部 CDN 不要
- **配布**: self-contained single exe/app（`tools\publish-tauri.cmd` / `tools/publish-tauri-macos.sh`）、Rust ランタイム同梱不要
- **MCP 連携**: GUI 内蔵の MCP / JSON-RPC サーバ + 同梱 `amm-mcp.exe`（Rust実装）

エンドユーザー向けの使い方は [**`manual/user-guide/usage.md`**](manual/user-guide/usage.md)。本書は開発者向け (ビルド・テスト・publish・プロジェクト構成) のみ扱う。

---

## 必要環境

- Windows 10/11 x64
- Rust ツールチェイン（`rustup` 経由、MSVC target）
- `cargo install tauri-cli --version ^2` （NSIS/MSIインストーラビルドに必要。初回コンパイルに30分前後かかる）
- WebView2 Runtime（Win10/11 では通常プリインストール済）
## ビルドと起動

```cmd
cargo build -j 2 --manifest-path src\apps\Amm\src-tauri\Cargo.toml
artifacts\target\debug\amm.exe
```

`cargo build -j 2`（並列度2）を推奨: デフォルト並列度だと環境によって外部から強制終了されることがある。ビルド中間生成物(`target/`)はリポジトリ直下`.cargo\config.toml`の`target-dir`設定により`artifacts\target\`に集約される。

## テスト

```cmd
cargo test -j 2 --manifest-path src\apps\Amm\src-tauri\Cargo.toml
```

## 配布物生成・インストーラビルド

```cmd
tools\publish-tauri.cmd            REM → artifacts\publish\tauri-windows\out\ に amm.exe/amm-mcp.exe/Amm.PowerShell等を集約
tools\build-installer-tauri.cmd    REM → artifacts\packages\tauri-windows\ に NSIS(*.exe)・MSI(*.msi)を生成（内部でpublish-tauri.cmdを実行）
```

`tools\build-installer-tauri.cmd` は `cargo-tauri` 導入が前提。生成される2種のインストーラは:

| 項目 | 値 |
|---|---|
| インストール先 | `C:\Program Files\amm\`（per-machine x64、旧WiX版と同じパス） |
| 対象ファイル | `amm.exe` / `amm-mcp.exe` / `Amm.PowerShell`（.psm1、コンパイル不要） / `profiles.amm` / `usage.md` |
| `.amm` 関連付け | `.amm` 拡張子を `amm.exe` に関連付け（`fileAssociations`、旧版と同様） |
| system PATH | インストール先を末尾に追加（`wix/fragments/path-environment.wxs`(MSI) / `nsis/hooks.nsh`(NSIS)、2026-07-23実装。**⚠️ 実インストール・アンインストールでのPATH確認は未実施**、`tasks/pending-real-machine-verification.md`参照） |
| UpgradeCode | 旧WiX版とは意図的に別の値（新旧インストーラが互いをアンインストールしないため） |

実機での実インストール・アンインストール確認（PATH自動追加を除く）は完了済み（`tasks/pending-real-machine-verification.md`のtasks.md 8.2.3項目参照）。旧WiX版との同一マシン共存確認は未実施。

サイレントインストール / アンインストール:

```cmd
REM MSI
msiexec /i amm_1.2.0_x64_en-US.msi /qn
msiexec /x amm_1.2.0_x64_en-US.msi /qn
```

> **注:** アンインストールを完全サイレント(`/quiet`)で行うとUACの自動昇格プロンプトを受けられず`Error 1730`で失敗する実機実績あり。対話モード(`/i`のみ・`/quiet`無し)であればOS標準の昇格プロンプト経由で成功する。

## macOS版（開発中、`openspec/changes/add-macos-support/`）

Windows依存箇所（Named Pipe IPC・Job Objectプロセス管理・システムメニュー・タスクバー点滅・フォーカス前面化）を`cfg(windows)`/`cfg(unix)`/`cfg(target_os = "macos")`で分岐し追加。Windows版のコードパス・動作は無変更。

### 必要環境（macOS）

- macOS（Apple Silicon実機で開発・確認。Intel Macでの検証は未実施）
- Rust ツールチェイン（`rustup`、`cargo`）
- `cargo install tauri-cli --version ^2`（`.app`/`.dmg`バンドルに必要）

### ビルドと起動

```sh
cargo build -j 2 --manifest-path src/apps/Amm/src-tauri/Cargo.toml
artifacts/target/debug/amm
```

**⚠️ 実機UI検証は必ず`.app`バンドル化+`open`起動で行うこと**。生バイナリの直接execでは、macOSのフォーカス前面化(`osascript activate`)がLaunch Servicesにバンドル登録されていないため失敗する（過去のAvalonia版PoCで発見された既知の罠、`reference/mac-avalonia-poc-lessons/README.md`参照）。

### テスト

```sh
cargo test -j 2 --manifest-path src/apps/Amm/src-tauri/Cargo.toml
```

### 配布物生成・`.app`/`.dmg`ビルド

```sh
tools/publish-tauri-macos.sh            # → artifacts/publish/tauri-macos/out/ に amm/amm-mcp/Amm.PowerShell等を集約
tools/build-installer-tauri-macos.sh    # → artifacts/packages/tauri-macos/ に amm.app（ad-hoc署名済み）・生成できれば.dmgも
```

`tools/build-installer-tauri-macos.sh`は`cargo-tauri`導入が前提。

既定profileセットはWindows/macOSで別ファイル: `src/apps/Amm/profiles.amm`（Windows既定、`cmd.exe`/`powershell.exe`/`claude.exe`等）と`src/apps/Amm/profiles.macos.amm`（macOS既定、`zsh`/`claude`/`copilot`等のbare実行ファイル名、PATH解決前提）。切替はTauriのプラットフォーム別設定ファイル`tauri.macos.conf.json`（`tauri.conf.json`とマージされ、macOSビルド時のみ有効）で行い、base設定の`"../profiles.amm": "./"`エントリを`null`で無効化した上で`"../profiles.macos.amm": "./profiles.amm"`を追加している。

**✅ 根本修正済み（2026-08-03）: `cargo tauri build`がammをamm-mcpの内容で上書きする破損バグ**

`Cargo.toml`の`[package]`に`default-run = "amm"`が無かったことが原因と判明。tauri-cli 2.11.4の`RustAppSettings::get_binaries()`（`src/interface/rust.rs`）を実際にソース追跡したところ、`[[bin]]`も`default-run`も無い場合、`src/bin/*`(amm-mcp)と暗黙のsrc/main.rs(amm)の両方が`is_main=false`のまま登録され、候補が2件以上あるケースは`match binaries.len() { 0=>.., 1=>.., _=>{} }`のno-op分岐に落ちて「どちらがメインバイナリか」を一切決定しない(tauri-cli自身のエラーメッセージ「make sure you have a package > default-run in the Cargo.toml file」が指しているのがまさにこのケース)。`default-run = "amm"`を追加したところ、クリーンな状態から2回連続で正しいバイナリ(WebKit/AppKit をリンクした約60MB版)が生成されることを確認済み。以下の自動検出・自動パッチ(`deps/amm-<hash>`から最新mtimeのものを復旧)は保険として残しているが、通常はもう発火しないはず。
- `.dmg`生成のFinder装飾ステップ(AppleScript)は、macOSの「オートメーション」権限（システム設定→プライバシーとセキュリティ→オートメーション、実行プロセスによるFinder操作の許可）が無い環境（非対話的な自動化セッション等）では`AppleEventタイムアウト`で失敗する。`.app`自体の生成・署名・起動には影響しない。対話的なユーザーセッションで実行するか、該当権限を付与してから再実行すること（GitHub Actions hosted `macos-latest`ランナーでは`CI=true`環境変数によりtauri-bundlerが自動で`--skip-jenkins`を付与するため、この制約自体発生しない）。

**✅ 根本修正済み（2026-08-09）: `.dmg`内の`.app`の署名が壊れており「"amm"は壊れているため開けません」と表示される不具合**

原因は`cargo tauri build`が`.dmg`を組み立てる時点で`.app`本体が未署名だったこと。`tauri.conf.json`の`bundle.macOS`に`signingIdentity`を設定していなかったため、tauri-bundler自身の署名処理（`app::bundle_project`内の`sign::keychain(identity)`呼び出し）が丸ごとスキップされ、`.dmg`へ梱包される`.app`はRust/Cargoのリンカが実行ファイル単体に自動付与するad-hoc署名（`flags=adhoc,linker-signed`）しか持たず、バンドル全体のリソース（`Info.plist`/`Resources/`等）は未封印（`Sealed Resources=none`）のままだった。ビルドスクリプト側で事後的に行っていた`codesign --sign - --force --deep`は、既に`.dmg`が組み立て済みの**別コピー**（`artifacts/target/release/bundle/macos/amm.app`）にしか効かず、配布される`.dmg`本体は直らなかった。`codesign --verify`で実際に「code has no resources but signature indicates they must be present」を確認・再現し、`bundle.macOS.signingIdentity: "-"`を設定して`cargo tauri build`自体が`.dmg`組み立て**前**に正しい順序（ネストしたバイナリ→アプリ本体、Appleの推奨する内側から外側への署名順）でad-hoc署名するよう修正。修正後は`codesign --verify`が`valid on disk`を返すことを実機（ダウンロード相当のquarantine属性付与を含む）で確認済み。

- 上記修正後も、未notarization・ad-hoc署名であること自体は変わらないため、初回起動時にGatekeeperが「開発元を確認できません」等の警告を出すことがある。最も確実な回避策は `xattr -d com.apple.quarantine /Applications/amm.app`（quarantine属性の除去）。右クリック→「開く」でも回避できる場合があるが、macOSのバージョンによっては効かないことがあるため`xattr`コマンドを優先案内する。同様に、初回はmacOSの通知(Notification Center)権限がappごとにオフになっており、attention通知が実際の内容ではなく「"amm"の通知」という汎用の初回許可バナーのみになることがある。システム設定→通知→ammで許可すること。

### 既知の制約

- `amm-mcp`は`.app`バンドル内(`Contents/Resources/amm-mcp`)に置かれるがPATHから到達できない（Windows版のようなインストーラ主導のPATH追加が無い）。GUIの「CLIへのMCP登録...」ボタン(`get_mcp_exe_path`→`hook_cli.rs`/`mcp_cli.rs`)はこのバンドル内パスを自動解決してCLI設定ファイルへ書き込むため、通常の利用ではユーザーが手動でパスを探す必要はない（2026-08-03ユーザー報告で「amm-mcpをどこから起動すれば良いのかわからなかった」との指摘を受け、README.mdに導線を明記して解消）。ターミナルから`amm-mcp`を素のコマンド名で直接起動したい場合のみ、上記ボタンのダイアログに表示されるフルパスを使う必要がある。
- notarization（公証）の要否、配布形態（`.dmg`のみか`.pkg`も用意するか）、対応OSバージョン下限は未決定。

## プロジェクト構成（抜粋）

```
src/apps/Amm/
├── src-tauri/
│   ├── Cargo.toml
│   ├── tauri.conf.json          … bundle設定（targets/resources/fileAssociations/mainBinaryName等）
│   └── src/
│       ├── main.rs / lib.rs     … エントリポイント・Tauriコマンド定義
│       ├── mcp.rs               … Named Pipe MCPサーバ(ACL実装済み)
│       ├── profile.rs           … profiles.amm スキーマ + ローダ
│       ├── gateway.rs           … 外部MCPサーバー集約(mcp-gateway)
│       ├── approval.rs          … 承認ハブ(ApprovalBroker)
│       ├── hook_cli.rs          … Claude Code/Codex/Copilot CLI等へのフック登録
│       ├── mcp_cli.rs           … amm-mcp.exe自体のMCPサーバー自己登録
│       ├── wait_detect.rs       … 入力待ち判定(WaitPatternDetector)
│       ├── input_history.rs     … 送信履歴(history.json)
│       └── bin/amm-mcp/         … MCP stdio bridge / CLI / REPL → amm-mcp.exe
├── public/                       … フロントエンド(index.html / style.css、xterm.js同梱。JSは責務別7ファイルに分割:
│                                      pane-layout.js/pane-lifecycle.js/send-helpers.js/dialogs-quick-stats.js/
│                                      dialogs-profile-mcp.js/input-history.js/events-integration.js)
└── resources/                    … インストーラへバンドルするリソースのステージ(.gitkeep以外はgitignore)
src/modules/Amm.PowerShell/       … PowerShellスクリプトモジュール(.psm1、コンパイル不要)
tools/publish-tauri.cmd / build-installer-tauri.cmd … publish/インストーラビルドスクリプト
tests/e2e-tauri/                  … Playwright CDP経由の実機GUI検証方法一式(README.md参照)
```

## サブプロジェクトの境界

| クレート/モジュール | 出力 | 役割 | 依存 |
|---|---|---|---|
| `src/apps/Amm/src-tauri`(バイナリ`amm`) | `amm.exe` | GUI本体 + 内蔵MCPサーバ | Tauri v2 / WebView2 |
| `src/apps/Amm/src-tauri/src/bin/amm-mcp` | `amm-mcp.exe` | MCP stdio bridge / CLI / REPL | 同一Cargo.toml内、amm本体とはコード共有なし(薄いリレー、旧版と同じ設計) |
| `src/modules/Amm.PowerShell` | `.psm1`スクリプトモジュール | PowerShell連携(`Open-AmmWindow`等) | コンパイル不要(UDR-amm-20260719T0013-b7e) |

GUI内蔵のMCPサーバがプロトコル本体を解釈し、`amm-mcp.exe`は薄いリレーに徹する設計。

---

## 成果物出力先（`artifacts/`）

```
artifacts/                        … 成果物出力 (gitignore)。variant別(tauri-windows/tauri-macos/tauri-linux)に分離
    ├── target/                   … Rust (cargo) ビルド中間ファイル。.cargo/config.tomlのtarget-dirで集約。
                                       platform別サブフォルダを手動では切らない — 現状は全ビルドスクリプトが
                                       `--target <triple>`を指定しないネイティブビルドのみだが、将来的に
                                       (例: macOSからのLinuxクロスコンパイル)`--target`を明示すれば、Cargo自身が
                                       target-dir配下に`<triple>/{debug,release}/`を自動生成し、ネイティブビルドの
                                       `target/{debug,release}/`とは自動的に衝突しない(OS名だけでなくCPU
                                       アーキテクチャまで区別されるぶん、手動でos別サブフォルダを切るより正確)。
                                       そのため、この階層だけは意図的に「Cargo標準の挙動に委ねる」設計とした
    ├── publish/
    │   ├── tauri-windows/out/    … tools\publish-tauri.cmd の self-contained 出力
    │   ├── tauri-macos/out/      … tools/publish-tauri-macos.sh の self-contained 出力
    │   └── tauri-linux/          … 未実装(Linux対応自体が未着手、docs/design/cross-platform-feasibility.md参照)。
    │                                実装時はtauri-windows/tauri-macosと同じ命名規約で追加する想定の予約名
    └── packages/                 … 最終配布物
        ├── tauri-windows/        … amm_{version}_x64-setup.exe (NSIS) / amm_{version}_x64_en-US.msi
        ├── tauri-macos/          … amm.app / amm_{version}_aarch64.dmg
        └── tauri-linux/          … 未実装、予約名(上記と同様)
```

## 関連ドキュメント

- [`manual/user-guide/usage.md`](manual/user-guide/usage.md) — エンドユーザー向け使い方ガイド
- [`design/spec/archive/spec.md`](design/spec/archive/spec.md) — 仕様書 (現行実装からのリバース生成 v1、経緯保持用アーカイブ)
- [`design/spec/archive/spec-v2.md`](design/spec/archive/spec-v2.md) — Phase 1〜4 実装ガイド(経緯保持用アーカイブ)
- [`../openspec/specs/`](../openspec/specs/) — capability別の正本仕様(16件、Tauri版準拠)
- [`../openspec/changes/archive/2026-07-26-migrate-to-tauri/`](../openspec/changes/archive/2026-07-26-migrate-to-tauri/) — Tauri移植の変更提案・タスク一覧・実機検証記録（2026-07-26 archive済み）
- [`../AGENTS.md`](../AGENTS.md) — マルチエージェント協働ポリシー

## ライセンス

Apache License 2.0。ルートの [LICENSE](../LICENSE) を参照。
