# amm 仕様書 (リバース生成 v1)

**ステータス**: 現行実装 (`AMMOperator/`) からのリバース生成 — 自動同期はしない。実装変更時は本書を手動更新する。
**生成日**: 2026-05-07 / **対象 commit**: main

本書は `AMMOperator/Amm/` (.NET 9 WinForms 本体) と `AMMOperator/Amm.Mcp/` (CLI) の実装から、利用者・運用者・拡張開発者向けに仕様を抽出したもの。設計の意図 (なぜこの形か) は `input/design_doc.md` (方式設計書 v3) と `.udr/records/` の UDR を参照。

---

## 1. 概要・スコープ

amm は、Windows ネイティブの **AI / CLI マルチプレクサ** である。Claude Code / GitHub Copilot CLI / OpenAI Codex CLI / Gemini CLI / cmd / PowerShell 等を MDI 子ウィンドウとしてタイル状に並走させ、**共通の入力パネル** と **MCP 経由のメッセージ注入** から制御する。

スコープ:

- 含む: マルチセッションのライフサイクル管理 / 入力集約・分配 / 入力待ち検出 / オフライン動作 / single exe 配布
- 含まない: ワークスペース / アーティファクト永続化 / レンダラ / アノテーション (これらは親プロジェクト Companion 側、`docs/amm-companion-boundary.md` 参照)

---

## 2. 全体アーキテクチャ

| レイヤー | 採用技術 | 主要モジュール |
|---|---|---|
| ホスト | .NET 9 Windows Forms | `Program.cs`, `Forms/MdiParentForm`, `Forms/TerminalChildForm` |
| Terminal UI | xterm.js v5 + xterm-addon-fit + xterm-addon-search (ローカル同梱) | `Resources/terminal.html`, `Resources/js/` |
| ブラウザエンジン | Microsoft.Web.WebView2 (NuGet) | `Forms/TerminalChildForm` |
| PTY | Windows ConPTY (P/Invoke 直書き、NuGet ラッパー非依存) | `Core/ConPtyWrapper.cs`, `Core/NativeMethods.cs` |
| 入力待ち検出 | 正規表現マッチ + 500ms 無出力タイマ | `Core/WaitPatternDetector.cs` |
| 設定モデル | System.Text.Json | `Core/SessionProfile.cs`, `Core/AppLaunchOptions.cs` |
| MCP サーバ | Named Pipe + JSON-RPC 2.0 (NDJSON) | `Core/Mcp/McpPipeServer.cs`, `MessageDispatcher.cs`, `MessageQueue.cs` |
| MCP CLI | stdio MCP bridge / REPL / send / list の単一 exe | `Amm.Mcp/Program.cs` (= `amm-mcp.exe`) |

主要 namespace:

- `amm.Forms` — MDI parent / child フォーム
- `amm.Core` — profile / 入力待ち / drop / system menu / Win32 など共通ヘルパ
- `amm.Core.Mcp` — Named Pipe MCP サーバとキュー / ディスパッチャ
- `Amm.Mcp` — `amm-mcp.exe` (別 csproj)

クラス間相関 (主要パス):

```
MdiParentForm (IsMdiContainer=true / IMcpHost 実装)
  ├─ MenuStrip (ファイル/コマンド/表示/設定/ヘルプ)
  ├─ MdiClient (子フォームの収納)
  │    └─ TerminalChildForm × N
  │         ├─ WebView2  (xterm.js を読み込む)
  │         ├─ ConPtyWrapper  (PseudoConsole + 子プロセス)
  │         ├─ WaitPatternDetector  (出力を Feed → state 通知)
  │         └─ SystemMenu hook (AMM 設定 / エディタパスコピー)
  ├─ 下部 Panel
  │    ├─ FlowLayoutPanel (MDI クイック切替バー)
  │    └─ Multiline TextBox (_inputBox)
  ├─ FileSystemWatcher (profiles.* hot reload, 300ms debounce)
  └─ Mcp.McpPipeServer  (Named Pipe accept loop)
        └─ MessageDispatcher → MessageQueue (per-nickname FIFO, 100 件/ニックネーム)
```

---

## 3. UI 仕様

### 3.1 ウィンドウ構成

- **MDI parent**: 通常ウィンドウ。タイトル `amm`、初期サイズ 1400×900、画面中央起動 (前回レイアウトがあれば復元)
- **MDI child** = `TerminalChildForm` × N: WebView2 をフィル配置、xterm.js を表示
- **下部入力パネル** (`_bottomPanel`, 既定 180px、Splitter で可変):
  - 行 1: MDI クイック切替バー (固定 `[履歴 ▼]` `[エディタ連携]` + 各 MDI ボタン `[Ctrl+N] <state> <profile>`)
  - 行 2: 複数行 TextBox `_inputBox` (Multiline=true, ScrollBars=Vertical)
  - 行 3: 送信先ラベル (アクティブ子の名前 / 全ペイン送信モード時はカウント)

### 3.2 メニュー (UDR-amm-20260427T0159-d4e)

```
ファイル(&F)
  名前を付けて保存(&A)...      AMM ファイル保存先を変更
  上書き保存(&S)               AMM ファイル保存
  ──
  コマンド追加(&N)...          テンプレ選択 → CommandTemplateDialog
  コマンド編集(&E)...          既存 profile を field-level mutation で編集 (起動中の MDI に即時反映)
  ──
  閉じる(&X)
コマンド(&C)
  <profile 1>                  クリックで子ウィンドウ起動
  <profile 2>
  ...                          (旧「新規」を移動)
表示(&V)
  <MDI 1 タイトル>             選択でフォーカス
  <MDI 2 タイトル>
  ──
  タイル縦(&V) / タイル横(&H)   開いた順 (Ctrl+1..9 順) に左上→右下グリッド配置
  カスケード(&C)                z-order 順に重ね配置
  ──
  記憶した配置で表示(&L)        記憶/ロード時の geometry・name を適用 + 不足分を起動
  現在の配置を記憶(&M)          生存 MDI から autoStartCount/windowGeometry を再構築 (in-memory)
  記憶した配置をクリア(&R)      記憶した autoStartCount/windowGeometry を消去 (確認あり・窓は閉じない)
設定(&O)
  コメント行を送信しない (' または // で始まる行)
  下部入力パネルを表示(&I)
  全ペイン送信モード(&B)
  送信後に入力欄をクリア(&L)
ヘルプ(&H)
  バージョン情報(&A)
```

メニューバー高さは `MenuStrip.AutoSize=false` / `Height=24` で固定。MDI 子最大化時に WinForms が先頭に merge するシステムアイコン項目 (OS 既定 `SmallIconSize` で挿入され高 DPI で 20-32 px になることがある) は、`MenuStrip.ItemAdded` / `Layout` / `Paint` の 3 経路で `Tag` が `"own"` でない `ToolStripMenuItem` を走査し、`Image` を 16×16 へダウンスケール (`InterpolationMode.HighQualityBicubic`) + `ImageScaling=None` に固定。子切替や DPI 変動で Image が後追いセットされるケースも belt-and-suspenders で取りこぼさない。

### 3.3 キーバインド

#### 入力欄 (`_inputBox`) で有効

| キー | 動作 |
|---|---|
| Ctrl+S | アクティブ子へ送信。**入力欄に選択範囲があれば選択分のみ、無ければ全文** (Ctrl+1..9 / 全ペイン送信モードでも同じ規則を適用) |
| Ctrl+1 〜 Ctrl+9 / NumPad1 〜 NumPad9 | 指定番号の MDI へ送信 (アクティブ化と入力欄フォーカスは保持) |
| Ctrl+H | 送信履歴ポップアップ (`InputHistory`、既定 最大 500 件、完全重複排除、`history.json` で起動間永続化) |
| Ctrl+E | エディタ連携: 一時ファイル `.md` を関連付けアプリで開き、保存のたびに固定 child へ送信 (`EditorBridge`) |
| Enter | 改行挿入 (送信しない。誤送信防止のため Ctrl+Enter / Enter 送信は廃止 — UDR-amm-20260424T1058-0e9) |

#### 子ターミナル (xterm.js / TerminalChildForm) で有効

| キー | 動作 | 備考 |
|---|---|---|
| Ctrl+Shift+C / Ctrl+Insert | 選択をコピー | xterm.js から C# クリップボードへ |
| Ctrl+C | profile.ctrlCCopyOnSelection=true かつ選択あり → コピー、なし → 子プロセスへ ^C | Windows Terminal 流 |
| Ctrl+V / Shift+Insert | 貼り付け (マルチライン時は確認ダイアログ) | xterm.js のデフォルト paste は C# 経由に置換 |
| Ctrl+F / F3 / Shift+F3 | 検索バー表示 / 次 / 前 | xterm-addon-search |
| Ctrl + + / Ctrl + - / Ctrl + 0 | フォントサイズ拡大 / 縮小 / 既定 (13 / 範囲 8〜32) | terminal.html 内 |
| 右クリック | 改行送信 (先頭、Enter `\r` を 1 つ送信) / プロンプト再送信 (↑ `\x1b[A` → 300ms → Enter `\r` で直前履歴を再実行) / クイック送信 ▶ / プロンプト送信 / エディタ連携 / 名前変更… / フォントサイズ変更 ▶ / コピー / 貼り付け / 全選択 / 画面クリア | MDI 切替バー右クリックとミラー |

### 3.4 設定メニュー 4 項目 (永続化)

| 項目 | 既定 | 効果 |
|---|---|---|
| コメント行を送信しない | OFF | ON で送信時に行頭が `'` `//` で始まる行を除去 (履歴は原文)。per-command の `commentPrefixes` の上にかかる override |
| 下部入力パネルを表示 | ON | OFF で MDI 描画領域を最大化 |
| 全ペイン送信モード (broadcast) | OFF | ON で Ctrl+S / Ctrl+1..9 が全生存子へ同時送信 |
| 送信後に入力欄をクリア | ON | OFF で送信しても入力欄に内容が残る |

これらのトグル + 下部パネル高さ + エディタ設定 + 履歴上限は `%LOCALAPPDATA%\amm\layout.json` に保存・復元。

### 3.5 ファイル drop

| 操作 | 動作 |
|---|---|
| 入力欄へドロップ | 全ファイルがテキストなら「内容 / パス」選択ダイアログ → カーソル位置に挿入 (パス) または入力欄を置き換え (内容)。非テキストはパス挿入 |
| 子 MDI へドロップ | 全ファイルがテキストなら「内容を送信 / パスを送信 / キャンセル」選択 (内容連結 >1MB で確認)。非テキストは確認なしでパス送信 |

テキスト判定: 拡張子ベース (`md/json/yaml/toml/xml/csv/ini/conf/env/html/css/js/ts/py/rb/go/rs/java/cs/sh/ps1/bat/sql/tex` ほか) + `Makefile` `Dockerfile` `LICENSE` 等の拡張子なし + ドット始まり (`.gitignore` `.env` 等)。詳細は `Core/FileDropHelper.cs`。

### 3.6 入力待ち状態の可視化

| 状態 (`WaitState`) | タイトル | 背景色 |
|---|---|---|
| Running | `⚙ name` | グレー |
| WaitingForInput | `● name` | 薄緑 |
| WaitingForInput + HasAttention | `⚠ name` | オレンジ |
| Unknown | `? name` | 薄黄 |
| Stopped | `✗ name (exited)` | 薄赤 |

クイック切替バーのボタン色は **許可・確認待ちオレンジ > 入力待ち薄黄 > アクティブ青 > 通常** の優先順 (UDR-amm-20260427T0159-d4e, UDR-amm-20260605T1043-3af)。

**attention (許可・確認待ち, Approval Hub Level 1, UDR-amm-20260605T1043-3af)**:
hook の `permission_prompt` / `elicitation_dialog` 通知を `TerminalChildForm.HasAttention`
フラグで保持 (`WaitState` enum は拡張しない — attention 中も入力可能なため waiting の
付加情報として扱う)。タイトル ⚠ / タイトルバー・切替ボタンのオレンジで表示し、
amm が非フォアグラウンドなら `FlashWindowEx` (FLASHW_TRAY | TIMERNOFG) でタスクバーを
点滅させる。解除はペインのアクティブ化 / Running・Stopped 遷移 / idle・busy 通知。

**許可の集約回答 (Approval Hub Level 2, UDR-amm-20260605T1124-9c4)**:
Claude Code の `PermissionRequest` hook (許可ダイアログ表示時のみ・対話 TUI 専用) から
`amm-mcp.exe approve` → 既存 Pipe の独自メソッド `amm/approval` で要求が届き、
`Core/Mcp/ApprovalBroker` (id → TaskCompletionSource 台帳) が人間の回答を仲介する。

```
PermissionRequest hook ─ amm-mcp approve ─ amm/approval ─▶ ApprovalBroker 台帳
                                                              │ PendingChanged
                                       ApprovalPopupForm (非モーダル + TopMost +
                                       WS_EX_NOACTIVATE、キュー表示、500ms ガード)
                                                              │ [許可]/[拒否]
                       hook へ decision JSON ◀── Resolve ─────┘
```

- 解放トリガー 4 種: ①回答 (allow/deny) ②ペインのアクティブ化・クローズ (= 回答方法を
  ペイン内へ切替) ③タイムアウト 45 秒 ④パイプ切断 (hook 消滅)。**①以外はすべて
  「決定なし」で hook を解放し、Claude Code が通常のペイン内プロンプトを表示する**
  (離席・GUI 不在でも CLI は止まらない / 勝手に deny されない)
- タイムアウト鎖: 台帳 45s < approve 読み取り上限 55s < hook 登録 timeout 60s
- hook ブロック中はペイン内プロンプト未表示のため、ポップアップとの二重回答競合は
  構造的に起きない。対象ペインがアクティブ + amm 前面なら即時解放しペイン内に直接出す
- 決定は AppLogger + ステータスバー一時表示に記録 (ペイン内に痕跡が残らない補い)
- 表示メニュー「許可要求ポップアップ」トグル (layout.json 永続化)。OFF = 即時解放 =
  Level 1 相当 (hook 登録解除は CLI 再起動が必要なため GUI 側で即時に切る)
- 対応 CLI: Claude Code (`hooks.PermissionRequest`) と Copilot CLI
  (`permissionRequest` hook、`amm-mcp.exe approve --source copilot` が
  `{"behavior": "allow"|"deny"}` 形式で応答)。Codex はブロッキング型 hook を
  持たないため Level 1 (⚠ 表示、OSC9 端末通知経由) まで

---

## 4. profile スキーマ (AMM ファイル / profiles.json)

`SessionProfile.cs` の JSON シリアライズ対象を全列挙する。AMM ファイル (`*.amm`) と `profiles.json` は同一スキーマ・同一ローダ (`SessionProfileLoader.Load`)。

```jsonc
{
  "profiles": [
    {
      "name": "Claude Code",                    // 表示名 (メニュー / タイトル)
      "executable": "claude.exe",                // 実行ファイル (環境変数展開可、.cmd は cmd.exe /c ラップ要)
      "args": [],                                // 起動引数 (環境変数展開可)
      "newlineMode": "LF",                       // "CRLF" | "LF" 入力パネル送信時の改行
      "outputEncoding": "UTF-8",                 // "UTF-8" | "Shift_JIS"
      "autoChcp": false,                         // 起動直後 chcp 65001\r\n を自動送信
      "waitPatterns": ["^>"],                    // 入力待ち判定 regex 配列 (空ならデフォルト)
      "workingDirectory": "%USERPROFILE%",       // 起動 CWD (環境変数展開可、未指定は app の CurrentDirectory)
      "ctrlCCopyOnSelection": true,              // Ctrl+C を Windows Terminal 流に
      "initialCommands": [],                     // ConPTY 起動直後に順次送信 (環境変数展開可)
      "sessionLog": false,                       // %LOCALAPPDATA%\amm\sessions\*.log に追記
      "theme": null,                             // xterm.js theme (background/foreground/cursor 等)
      "closeOnExit": true,                       // 子プロセス exit で MDI 自動クローズ
      // ---- v2: per-command 拡張 (UDR-amm-20260427T0055-2c1) ----
      "autoStartCount": 0,                       // アプリ起動時に N 個自動オープン
      "closeProhibited": false,                  // MDI クローズ禁止 (Shift+× で緊急脱出可)
      "collapseBlankLines": true,                // 連続空行を 1 行に縮約して送信
      "commentPrefixes": ["'", "//", "#"],       // 行頭マッチで送信スキップ
      "windowGeometry": [                        // 起動 N 個目 (生存数+1) に適用
        { "index": 1, "x": 0,   "y": 0, "w": 640, "h": 480 },
        { "index": 2, "x": 640, "y": 0, "w": 640, "h": 480 }
      ],
      // ---- MCP (UDR-amm-20260427T0225-7a3) ----
      "nickname": "claude"                       // MCP 受信時の宛先名 (null/空なら受信不可)
    }
  ]
}
```

### 4.1 profile 解決順序 (`AppPaths.FindProfilesPath`)

1. `AppLaunchOptions.Parse` で位置引数があればそれ (絶対パスでなければ `Environment.CurrentDirectory` 基準で正規化)
2. exe と同じディレクトリの `profiles.json`
3. exe と同じディレクトリの `profiles.amm`
4. いずれも無ければ既定の `profiles.json` パスを返す (中身は `SessionProfileLoader.CreateDefaultProfiles` = CMD のみ)

### 4.2 hot reload

`MdiParentForm.SetupProfilesWatcher` が `FileSystemWatcher` で profile ファイルを監視し、`Changed/Created/Renamed` を 300ms デバウンスで再読込。**既存子ウィンドウには影響しない** (新規起動時のみ反映)。

### 4.3 コマンドテンプレート

「ファイル → コマンド追加」で使う `SessionProfileLoader.CommandTemplates` (静的 array、配布物に同梱):
CMD / PowerShell / Claude Code / Codex / Copilot / Gemini / (空テンプレート)

---

## 5. 入力待ち検出 (`WaitPatternDetector`)

### 5.1 既定 wait パターン

`Core/WaitPatternDetector.cs:DefaultPatterns`:

| Regex | 用途 |
|---|---|
| `[\$#>]\s*$` | bash / cmd / 一般 shell 末尾型プロンプト |
| `PS\s+\S+>\s*$` | PowerShell 行頭型 |
| `(y/n)\s*$` (ignore case) | yes/no 確認 |
| `password[:\s]*$` (ignore case) | パスワード入力 |
| `:\s*$` | 末尾コロン汎用 (less / git editor 等) |
| `\?\s*$` | 末尾クエスチョン汎用 |
| `^>(?:\s|$)` | Claude Code / Codex の box プロンプト (両端枠を剥がした後の行頭型) |
| `続行するには何かキーを押してください` | Windows 系 pause |

profile の `waitPatterns` が空でなければ既定を上書き。

### 5.2 判定ロジック

- ConPTY 出力 1 行ごとに `Feed(string)` が呼ばれる
- ANSI シーケンス除去 (`AnsiStripper`) → 装飾オンリー行 (Box drawing 全域 U+2500-U+257F + `|`) を捨てる
- 直近 50 行を `_recentLines` に保持 (Claude Code の応答末尾再描画を捕捉するため大きめ)
- `MatchesAnyPattern`: 直近行を新しい順に走査、両端のフレーム文字 + 空白を剥がしてから regex 照合
- ヒット → `WaitingForInput` に即遷移
- ミス → `Running` に遷移、500ms 無出力タイマで再判定 (タイマ満了でヒットしなければ `Running` のまま、`Unknown` には落とさない)
- プロセス exit → `Stopped`

### 5.3 状態遷移とイベント

`event Action<WaitState>? StateChanged` を `TerminalChildForm` が購読し、タイトル絵文字とクイック切替バーの色を更新。

### 5.4 hook 駆動検知 (イベント駆動、UDR-amm-20260605T0523-7e1)

正規表現スクレイピングの補完として、CLI 自身の hook から状態を push する経路を持つ:

```
amm GUI ──ConPTY 起動時に env 注入──▶ claude.exe (AMM_NOTIFY_ID=<GUID>)
                                        └─ Stop / Notification hook 発火
                                             └─ amm-mcp.exe notify (env 継承)
                                                  └─ Named Pipe amm/notify ──▶ WaitPatternDetector.ForceState
```

- `TerminalChildForm.NotifyToken` (GUID) を環境変数 `AMM_NOTIFY_ID` として子プロセスへ注入 (`ConPtyWrapper.Start` の `extraEnvironment`)
- hook は CLI の子プロセスなので env を継承。`amm-mcp.exe notify` が env を読み、Named Pipe の独自メソッド `amm/notify` (`{token, state, source?}`) で GUI へ通知
- **env 不在 (amm 外で起動した CLI) では notify は no-op** — hook 設定はユーザーグローバルでも誤通知しない
- 状態語彙: `idle` / `attention` → `WaitingForInput`、`busy` → `Running` (`Stopped` からの復活は不可)。CLI ごとのイベント名差は `Amm.Mcp/NotifyPayloadMapper` が吸収 (Claude `Stop`/`Notification`、Codex `agent-turn-complete`、Copilot `agent_idle` 等)。`attention` はさらに `HasAttention` フラグで保持し ⚠/オレンジ/タスクバー点滅で可視化 (§3.6, UDR-amm-20260605T1043-3af)
- hook 登録は [コマンド] → [CLI への MCP / フック登録...] (`Core/Mcp/HookCliRegistrar`)。3 CLI 対応:
  - **Claude Code**: `~/.claude/settings.json` の `hooks.Stop` / `hooks.Notification` (notify) + `hooks.PermissionRequest` (approve)。既存 hooks は保全し amm エントリのみ冪等に追加 / 削除
  - **Codex**: `~/.codex/config.toml` のルート `notify` キー (`['<exe>', 'notify', '--source', 'codex']`、イベント JSON は argv 末尾渡し)。`notify` は単一キーのため他プログラム設定済みなら明示エラー (黙って上書きしない)。`[tui]` の `notifications = ["agent-turn-complete", "approval-requested"]` + `notification_method = "osc9"` も追記し (ユーザー設定があれば触らない、amm 追記行は `# added by amm` マーカー)、xterm.js の OSC9 ハンドラ (`osc_notify` メッセージ) が approval を attention、それ以外を idle に解釈する
  - **Copilot CLI**: `~/.copilot/hooks/amm-hooks.json` (amm 専有ファイル、削除 = アンレジスタ) に `agentStop` → `notify --state idle --source copilot`、`permissionRequest` → `approve --source copilot` (timeoutSec 60)
- 登録コマンドは自己ガード形式 `cmd /c if exist "<exe>" "<exe>" notify --source claude` — amm のアンインストール / 移動で exe が消えても hook は静かに no-op し、Claude Code 側にエラーが出ない (per-machine MSI からは per-user 設定を全ユーザー分解除できないため、「残っても無害」を登録時に作り込む)。旧形式 (ガードなし) のエントリも amm エントリとして認識し再登録時に置換
- waitPatterns 検知は fallback としてそのまま併走 (素のシェル / hook 未登録 CLI 用)

---

## 6. MCP サーバ

UDR-amm-20260427T0225-7a3 の決定に従い、amm GUI が **Named Pipe で MCP サーバを常駐**、外部 MCP クライアントは `amm-mcp.exe` (stdio bridge) を介してアクセスする。

### 6.1 サーバ仕様

| 項目 | 値 |
|---|---|
| パイプ名 | `\\.\pipe\amm-mcp-{Environment.UserName}` (デフォルト DACL = ローカルユーザに開放) |
| プロトコル | MCP `2024-11-05` |
| 認証 | OS 認証 (Named Pipe DACL) のみ |
| フレーミング | NDJSON (1 行 = 1 JSON-RPC 2.0) |
| serverInfo | `{ name: "amm-operator", version: "0.3.0" }` |

### 6.2 対応メソッド

| メソッド | 用途 |
|---|---|
| `initialize` | プロトコル / capabilities / serverInfo 返却 |
| `notifications/initialized` (notification) | クライアント完了通知 (応答なし) |
| `tools/list` | 下表 3 ツールを返却 |
| `tools/call` | ツール呼び出し |
| `ping` | 空 result 返却 |

### 6.3 ツール

#### `send_message`

| 引数 | 型 | 必須 | 意味 |
|---|---|---|---|
| `message` | string | 必須 | 注入テキスト (改行はそのまま) |
| `recipient` | string? | 任意 | nickname。省略で全 nickname にブロードキャスト |
| `mode` | "first" \| "all" | 任意 (既定 "first") | 同 nickname 複数時、`first` は入力待ち優先 → 起動順、`all` は全インスタンス |

応答: `{ delivered_count, queued_count, recipients[] }`

#### `list_participants`

引数なし。`{ participants: [{ nickname, profile, instance, state, isWaiting }] }`

#### `peek_queue`

| 引数 | 型 | 必須 | 意味 |
|---|---|---|---|
| `recipient` | string? | 任意 | フィルタ用 nickname。省略で全キュー |

応答: `{ queues: [{ nickname, messages[] }] }`

### 6.4 配信動作

- nickname 未設定 profile の MDI は受信不可、`list_participants` にも出ない
- 入力待ち状態の MDI には **直接注入** (`IMcpHost.Inject`)
- 入力待ちでない MDI 向けには **キューに退避** (`MessageQueue`、nickname ごと最大 100 件、超過分は古い順 drop)
- 後続で wait 状態になったら **キュードレイン** (`MessageDispatcher.OnWaitStateChanged`)
- `mode=first` 時の優先順位: 入力待ちのうち最も古い起動順 → 入力待ちが居なければ起動順 1 番目

### 6.5 amm-mcp.exe (CLI)

`AMMOperator/Amm.Mcp/Program.cs` の単一 exe。引数最初の語でモード分岐:

| モード | 起動例 | 用途 |
|---|---|---|
| bridge (既定) | `amm-mcp.exe` または `amm-mcp.exe --bridge` | stdio MCP bridge: stdin → pipe / pipe → stdout を双方向コピー |
| repl | `amm-mcp.exe` を対話 console 起動した時 | 対話 REPL (`list` / `send` / `peek` / `help` / `quit`) |
| send | `amm-mcp.exe send <nickname> [msg]` / `amm-mcp.exe send --broadcast [msg]` | message 引数省略時は stdin |
| list | `amm-mcp.exe list` | list_participants の JSON を stdout |
| notify | `amm-mcp.exe notify [--state <s>] [--source <l>]` | CLI hook 用 (§5.4)。env `AMM_NOTIFY_ID` 必須 (無ければ no-op)。payload は stdin / argv 末尾 JSON を自動判別、常に exit 0 (CLI を妨げない)。接続タイムアウト既定 2000ms |
| approve | `amm-mcp.exe approve` | PermissionRequest hook 用 (§3.6 Level 2)。許可要求をポップアップへ転送し回答を待つ。allow/deny は決定 JSON を stdout に、無回答は出力なしで exit 0 (= ペイン内プロンプトへ) |

共通オプション: `--pipe-name <name>` (既定 `amm-mcp-{user}`) / `--connect-timeout <ms>` (既定 5000ms)

終了コード: 0=成功 / 1=引数不正 / 2=GUI 未起動 / 3=IO エラー / 4=MCP エラー

---

## 7. システムメニュー拡張 (UDR-amm-20260427T0238-fb5)

各 MDI 子ウィンドウのシステムメニュー (タイトルバー左上アイコン / Alt+Space) に Win32 P/Invoke (`Core/Win32SystemMenu.cs`) で項目を追加し、`WM_SYSCOMMAND` を hook する。

| ID (定数) | 値 | 表示 | 動作 |
|---|---|---|---|
| `SC_AMM_SETTINGS` | 0x1010 | AMM 設定… | `Core/AmmSettingsDialog` 起動。closeProhibited / collapseBlankLines / commentPrefixes を編集、profiles.amm への書き戻しを確認 |
| `SC_AMM_COPY_EDITOR_PATH` | 0x1020 | エディタ連携ファイルパスをコピー | 該当 child の EditorBridge から temp ファイルパスをクリップボードへ。未連携時は MessageBox |

カスタム ID は Windows 既定システムコマンド (0xF000-0xFFFF) と衝突しないよう 0x1000 台に配置。

---

## 8. ファイル配置 / 永続化

| パス | 用途 |
|---|---|
| `<exe dir>\profiles.json` または `profiles.amm` | profile (位置引数で差し替え可) |
| `<exe dir>\WebViewShared\` | WebView2 UserData (全セッション共有) |
| `<exe dir>\Resources\amm.ico` | multi-size .ico |
| `<exe dir>\Resources\terminal.html` | xterm.js 起動 HTML |
| `<exe dir>\Resources\js\xterm.js` ほか | xterm.js / addon (オフライン同梱) |
| `%LOCALAPPDATA%\amm\layout.json` | ウィンドウ位置 / サイズ / 下部パネル高さ / 各種トグル / エディタ設定 / 履歴上限 |
| `%LOCALAPPDATA%\amm\history.json` | 入力履歴 (Ctrl+H)。終了時保存・起動時復元、既定 最大 500 件 |
| `%LOCALAPPDATA%\amm\log\app.log` | アプリログ (1MB ローテ、`.1` を 1 世代保持) |
| `%LOCALAPPDATA%\amm\sessions\YYYYMMDD-HHMMSS-<name>.log` | profile.sessionLog=true 時の ANSI 除去済プレーンログ |

`layout.json` のスキーマ (`MdiParentForm.LayoutState`):

```json
{
  "X": 0, "Y": 0, "Width": 1400, "Height": 900,
  "WindowStateCode": 0,
  "BottomPanelHeight": 180,
  "ClearAfterSend": true,
  "CommentFilter": false,
  "ShowButtonBar": true,
  "ShowInputBox": true,
  "ShowStatusBar": true,
  "EditorMode": "Associated",
  "CustomEditorPath": "",
  "EditorPostSendAction": "Focus",
  "HistoryMaxEntries": 500
}
```

---

## 9. 起動引数 (`AppLaunchOptions`)

```
amm.exe [<profile path>] [--start-all]
```

| 引数 | 意味 |
|---|---|
| `<profile path>` (位置引数 1 個) | 読み込む profile ファイル (`*.json` / `*.amm`)。相対パスは CurrentDirectory 基準で絶対化 |
| `--start-all` | 読み込んだ全 profile を起動時に各 1 個オープン (per-profile の `autoStartCount` より優先) |

未対応の `--*` は `ArgumentException`。位置引数 2 個以上は `ArgumentException`。

起動時に **Shift** を押していれば `AppLaunchOptions.SuppressAutoStart=true` (`Program.Main` で `Control.ModifierKeys` を判定) となり、`autoStartCount` / `--start-all` による自動起動と信頼確認ダイアログを抑止する (`.amm` を Shift+ダブルクリックしたとき等。空のウィンドウのみ表示)。「ファイル → 開く」での Shift 抑止 (ダイアログ後のキー状態を判定) と対をなす。

---

## 10. テスト構成

`AMMOperator/Amm.Tests/` (xUnit):

| テストファイル | 件数 (Fact) | 対象 |
|---|---|---|
| `AppLaunchOptionsTests.cs` | 6 | 引数パース / 不正引数 |
| `AppPathsTests.cs` | 4 | profile path 解決 |
| `InputHistoryTests.cs` | 16 | 履歴サイクル / 重複排除 / 上限 / 容量変更 / 永続化 round-trip |
| `McpTests.cs` | 21 | initialize / tools/list / send_message / list_participants / peek_queue / キュー上限 |
| `SessionProfileTests.cs` | 26 | デシリアライズ / 既定値 / IsCommentLine / FilterLinesForSend / TryGetGeometryForIndex / コマンドテンプレ |
| `WaitPatternDetectorTests.cs` | 11 | デフォルトパターン / Box drawing 剥がし / 状態遷移 / Stopped |

実行:

```cmd
dotnet test AMMOperator/Amm.Tests/Amm.Tests.csproj
```

UI / WebView2 / ConPTY を伴う統合テストは未整備 (実機での手動検証)。

---

## 11. 配布

### 11.1 開発ビルド

```cmd
dotnet build AMMOperator/Amm.sln -c Debug
```

### 11.2 single exe 配布

```cmd
AMMOperator\scripts\publish.cmd
```

`AMMOperator/publish/` に self-contained single exe 一式 (.NET ランタイム不要) が出る。同フォルダに `Resources/` `profiles.json` `amm-mcp.exe` も同梱。

### 11.3 サブプロジェクト

| csproj | 出力 | 役割 |
|---|---|---|
| `AMMOperator/Amm/Amm.csproj` | `amm.exe` (WinExe, net9.0-windows) | 本体 |
| `AMMOperator/Amm.Mcp/Amm.Mcp.csproj` | `amm-mcp.exe` (Exe, net9.0) | MCP CLI |
| `AMMOperator/Amm.Tests/Amm.Tests.csproj` | テスト (net9.0) | xUnit |

---

## 12. 既知の制約 / TODO

- WebView2 Fixed Version Runtime 同梱は未着手 (現在は Edge 同梱の Evergreen Runtime を期待)
- Companion 連携は未着手 (`docs/amm-companion-boundary.md` の API は仕様提案のみ)
- ライセンス未設定 (親 Companion は Apache License 2.0 採用方針)
- UI / 統合テスト基盤なし (手動検証のみ)
- 同 nickname の複数インスタンス間の親密度評価 (`mode=first` 内の入力待ち優先) は単純な起動順タイブレーク

---

## 13. 参照

- [`README.md`](../README.md) — リポジトリ概要
- [`AMMOperator/README.md`](../AMMOperator/README.md) — ユーザ向け運用手順
- [`AMMOperator/docs/spec-v2.md`](../AMMOperator/docs/spec-v2.md) — Phase 1〜4 実装ガイド (履歴ベース)
- [`input/design_doc.md`](../input/design_doc.md) — 方式設計書 v3 (WinForms MDI)
- [`docs/amm-companion-boundary.md`](amm-companion-boundary.md) — amm / Companion の責務境界
- [`AGENTS.md`](../AGENTS.md) / [`CLAUDE.md`](../CLAUDE.md) — マルチエージェント運用ポリシー
- [`.udr/records/`](../.udr/records/) — 設計判断記録 (本書の根拠 UDR は本文中で個別参照)
