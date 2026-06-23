# amm — ビルド / 開発ガイド

Windows ネイティブ MDI マルチターミナル `amm.exe` / MCP bridge `amm-mcp.exe` の **実装プロジェクト**。

- **UI**: .NET 9 Windows Forms (MDI) + WebView2 + xterm.js
- **PTY**: Windows ConPTY (P/Invoke 直書き、NuGet ラッパー非依存)
- **オフライン**: xterm.js / addon はローカル同梱、外部 CDN 不要
- **配布**: self-contained single exe (`tools\publish.cmd`)、.NET ランタイム不要
- **MCP 連携**: GUI 内蔵の MCP / JSON-RPC サーバ + 同梱 `amm-mcp.exe`

エンドユーザー向けの使い方は [**`manual/user-guide/usage.md`**](manual/user-guide/usage.md) を参照。本書は開発者向け (ビルド・テスト・publish・プロジェクト構成) のみ扱う。

---

## 必要環境

- Windows 10/11 x64
- .NET 9 SDK (`winget install Microsoft.DotNet.SDK.9`)
- WebView2 Runtime (Edge 入りの Win10/11 では通常プリインストール済)

## ビルドと起動

リポジトリ直下で:

```cmd
dotnet build Amm.sln -c Debug
src\apps\Amm\bin\Debug\net9.0-windows\amm.exe
```

## テスト

```cmd
dotnet test tests\Amm.Tests\Amm.Tests.csproj
```

xUnit。GUI を起動せずに動かせる単体テスト (キュー / ディスパッチャ / プロトコル / プロファイル / 入力履歴 / 待ち判定 / パス解決)。

## 単一実行ファイル配布

```cmd
tools\publish.cmd
```

`artifacts/publish/` に self-contained single exe が生成される (.NET ランタイム不要):

| ファイル | 役割 |
|---|---|
| `amm.exe` | GUI 本体 |
| `amm-mcp.exe` | MCP stdio サーバ / CLI / REPL |
| `profiles.amm` | 既定設定ファイル |
| `Resources/` | アイコン / xterm.js / terminal.html |

## Windows Installer (MSI) ビルド

```cmd
tools\build-installer.cmd
```

内部で `publish.cmd` → `dotnet build src\installer\wix\Amm.Installer.wixproj` を順に実行し、`artifacts\packages\amm-setup.msi` を生成する (WiX 5 SDK、グローバルツール不要。MSI 出力先は wixproj の `OutputPath` で artifacts へ集約)。

| 項目 | 値 |
|---|---|
| インストール先 | `C:\Program Files\amm\` (per-machine x64) |
| ショートカット | スタートメニュー > amm |
| system PATH | インストール先を末尾に追加 (新しい cmd.exe / Explorer から `amm` 直接起動可) |
| `.amm` 関連付け | `.amm` 拡張子を `amm.exe` に関連付け (Explorer でダブルクリックすると当該ファイルを読み込んで起動)。ProgId `amm.ProfilesFile.1`、アンインストールで自動除去 |
| UI | なし (silent / minimal) — ダブルクリックで即インストール |
| 旧バージョン | MajorUpgrade で自動アンインストール |

> **注:** PATH 変更は既に開いている cmd.exe / PowerShell には反映されない (Windows 仕様)。新しいウィンドウから利用すること。Explorer のアドレスバー入力 (`amm` Enter) は `WM_SETTINGCHANGE` ブロードキャストで概ね即反映される。

バージョン上書き:

```cmd
tools\build-installer.cmd 0.2.0.0
```

サイレントインストール / アンインストール:

```cmd
msiexec /i amm-setup.msi /qn
msiexec /x amm-setup.msi /qn
```

## プロジェクト構成

```
amm/                              … リポジトリ直下
├── Amm.sln                       … ソリューション (src/apps・tests を束ねる)
├── src/
│   ├── apps/                     … EXE を生成するプロジェクト群
│   │   ├── Amm/                  … GUI 本体プロジェクト → amm.exe
│   │   │   ├── Amm.csproj
│   │   │   ├── Program.cs        … エントリポイント
│   │   │   ├── Forms/
│   │   │   │   ├── MdiParentForm.cs      … メインフォーム (MDI 親、メニュー、入力パネル)
│   │   │   │   └── TerminalChildForm.cs  … MDI 子 (WebView2 + xterm.js + ConPTY)
│   │   │   ├── Core/
│   │   │   │   ├── AppLaunchOptions.cs   … CLI 引数パース + Shift 起動抑止
│   │   │   │   ├── AppPaths.cs           … 実行ファイル横のパス解決
│   │   │   │   ├── AppLogger.cs          … %LOCALAPPDATA%\amm\log\app.log (有界キュー)
│   │   │   │   ├── SessionProfile.cs     … profiles.amm スキーマ + ローダ
│   │   │   │   ├── ConPtyWrapper.cs      … Windows ConPTY P/Invoke
│   │   │   │   ├── WaitPatternDetector.cs … 入力待ち判定 (正規表現 + 500ms timeout)
│   │   │   │   ├── InputHistory.cs       … 送信履歴 (完全重複排除、既定 500 件)
│   │   │   │   ├── Win32SystemMenu.cs    … MDI 子のシステムメニュー拡張
│   │   │   │   ├── AmmSettingsDialog.cs  … 「AMM 設定」ダイアログ
│   │   │   │   ├── CommandTemplateDialog.cs … 「コマンド追加・編集」ダイアログ
│   │   │   │   ├── EditorBridge.cs       … Ctrl+E でエディタ連携
│   │   │   │   ├── FileDropHelper.cs     … 入力欄 / 子へのファイル drop 処理
│   │   │   │   ├── AnsiStripper.cs       … sessionLog 向け ANSI 除去
│   │   │   │   ├── NativeMethods.cs      … その他 Win32 P/Invoke
│   │   │   │   └── Mcp/
│   │   │   │       ├── McpPipeServer.cs       … Named Pipe + JSON-RPC サーバ
│   │   │   │       ├── MessageDispatcher.cs   … nickname 解決 + 送信ルーティング
│   │   │   │       └── MessageQueue.cs        … 入力待ち未到達分のキュー
│   │   │   ├── Resources/
│   │   │   │   ├── amm.ico
│   │   │   │   ├── terminal.html  … xterm.js 統合 HTML (IME 二重送信コアレッサ)
│   │   │   │   └── js/            … xterm.js + addon
│   │   │   └── profiles.amm       … 配布用既定 profile セット
│   │   └── Amm.Mcp/              … MCP bridge / CLI / REPL → amm-mcp.exe
│   │       ├── Amm.Mcp.csproj
│   │       └── Program.cs        … stdio bridge / list / send / REPL の 4 モード統合
│   └── installer/
│       └── wix/                  … MSI (WiX 5)
│           ├── Amm.Installer.wixproj  … WiX 5 SDK スタイル wixproj
│           └── Package.wxs       … MSI コンポーネント定義
├── tests/
│   └── Amm.Tests/                … xUnit テスト (src/apps をミラー)
│       ├── Amm.Tests.csproj
│       ├── AppLaunchOptionsTests.cs / AppPathsTests.cs / InputHistoryTests.cs
│       ├── McpTests.cs / SessionProfileTests.cs / WaitPatternDetectorTests.cs
├── tools/
│   ├── publish.cmd               … self-contained 単一 exe 配布 (→ artifacts/publish)
│   └── build-installer.cmd       … publish → WiX で MSI (amm-setup.msi) 生成
├── docs/
│   ├── build.md                  … 本書 (ビルド/開発ガイド)
│   ├── design/spec/              … 仕様書 (spec.md / spec-v2.md)
│   ├── design/amm-companion-boundary.md
│   └── manual/user-guide/usage.md … エンドユーザー向け使い方ガイド
├── .udr/records/                 … UDR (判断記録)
├── reference/                    … プロトタイプ・外部参照 (src には混ぜない)
└── artifacts/                    … 成果物出力 (gitignore)
    ├── publish/                  … tools\publish.cmd の self-contained 出力
    └── packages/                 … 最終配布物 (amm-setup.msi)
```

## サブプロジェクトの境界

| csproj | 出力 | 役割 | 依存 |
|---|---|---|---|
| `src/apps/Amm/Amm.csproj` | `amm.exe` (WinExe, net9.0-windows) | GUI 本体 + 内蔵 MCP サーバ | WebView2 / WinForms |
| `src/apps/Amm.Mcp/Amm.Mcp.csproj` | `amm-mcp.exe` (Exe, net9.0) | MCP stdio bridge / CLI / REPL | (なし、薄いリレー) |
| `tests/Amm.Tests/Amm.Tests.csproj` | テストアセンブリ (net9.0-windows) | 単体テスト | `Amm.csproj` (InternalsVisibleTo) |

GUI 内蔵の MCP サーバが MCP プロトコル本体を解釈し、`amm-mcp.exe` は薄い stdio ↔ Named Pipe リレーに徹する。判断記録: [`../.udr/records/UDR-amm-20260427T0225-7a3.yaml`](../.udr/records/UDR-amm-20260427T0225-7a3.yaml)

## 関連ドキュメント

- [`manual/user-guide/usage.md`](manual/user-guide/usage.md) — エンドユーザー向け使い方ガイド
- [`design/spec/spec.md`](design/spec/spec.md) — 仕様書 (現行実装からのリバース生成 v1)
- [`design/spec/spec-v2.md`](design/spec/spec-v2.md) — Phase 1〜4 実装ガイド
- [`../AGENTS.md`](../AGENTS.md) — マルチエージェント協働ポリシー (UDR 自動検知)
- [`../.udr/records/`](../.udr/records/) — UDR (判断記録) 一式

## ライセンス

Apache License 2.0。ルートの [LICENSE](../LICENSE) を参照。
