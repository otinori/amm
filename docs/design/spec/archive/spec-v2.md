# amm 機能拡張 v2 — 変更指示書

ステータス: ドラフト / 起票 2026-04-27 / 起票者 project maintainer

ユーザー指示原文を整理した実装指示書。Phase 単位で UDR を起票し、順次対話で具体化しつつ実装する (CLAUDE.md / AGENTS.md の運用ポリシーに従う)。

---

## 全体像

| Phase | テーマ | 主な変更領域 | 対応 UDR (予定) |
|---|---|---|---|
| 1 | AMM ファイルスキーマ v2 + per-command 設定 | `SessionProfile` 拡張, `MdiParentForm` 起動制御, `TerminalChildForm` 送信前処理, システムメニュー hook | **UDR-amm-20260427T0055-2c1** (des) |
| 2 | メニュー再構成 + 入力待ち通知 | `MdiParentForm` メニュー全面見直し, クイック切替バー視覚状態, AMM コマンドテンプレート | **UDR-amm-20260427T0159-d4e** (des) |
| 3 | MCP サーバ機能 | 新規 MCP host モジュール, nickname 名前空間, キュー、`TerminalChildForm` 受信フック | **UDR-amm-20260427T0225-7a3** (arc) |
| 4 | 補助機能 | システムメニュー「フルパスをコピー」, CMD stdout リダイレクト経由送信 (CLI 同梱) | **UDR-amm-20260427T0238-fb5** (des) |

---

## Phase 1: AMM ファイルスキーマ v2 + per-command 設定

### 1.1 設定項目 (per-command, AMM ファイル指定可 + システムメニュー指定可)

AMM ファイル (現 `profiles.amm`) の各 profile に追加するキーと既定値:

| キー | 型 | 既定値 | 意味 |
|---|---|---|---|
| `autoStartCount` | int | `0` | アプリ起動時にこの profile を何個自動起動するか (0 = しない) |
| `closeProhibited` | bool | `false` | `true` のとき MDI ウィンドウのクローズを禁止 (×ボタン / Ctrl+W / システムメニュー閉じる の全てを抑止) |
| `collapseBlankLines` | bool | `true` | マルチライン送信時、2 行以上連続する空行を 1 行にまとめる (※既定挙動と一致) |
| `commentPrefixes` | string[] | `["'", "//"]` | 行頭がこのいずれかで始まる行はコメントとしてスキップ (空配列でフィルタ無効) |
| `windowGeometry` | object[] | `[]` (＝最大化) | 起動 N 個目の MDI 位置・サイズ。後述構造 |

`windowGeometry` 構造:

```json
"windowGeometry": [
  { "index": 1, "x": 0,    "y": 0,   "w": 640, "h": 480 },
  { "index": 2, "x": 640,  "y": 0,   "w": 640, "h": 480 }
]
```

仕様:

- `index` は 1 始まり。同 profile 内で N 個目の起動に適用される。
- 該当 `index` のエントリが無い場合は **最大化** (現挙動と同じ)。
- MDI を全て閉じてもう一度同じコマンドを起動した場合、**カウンタはリセットされる** (= 1 個目から数え直し)。
  - 「現在その profile で生きている MDI 数 + 1」が次の index。閉じれば index は減る。
- 座標は MDI クライアント領域の相対座標 (px)。

### 1.2 システムメニュー (per MDI window) 指定

各 MDI 子ウィンドウのシステムメニュー (タイトルバー左上アイコン / Alt+Space) に「**AMM 設定…**」を追加し、ダイアログで以下を編集可能に:

- クローズ禁止 (toggle)
- 連続空行を 1 行にまとめる (toggle)
- コメント記号 (CSV 編集)
- (autoStartCount / windowGeometry は per-window では編集不可。AMM ファイル/Phase 2 のコマンド編集経由でのみ設定可)

ダイアログで変更した内容は **AMM ファイルへ書き戻すか** ユーザーに確認 (`MessageBox`)。書き戻さない場合はそのセッションのみ。

### 1.3 既定値の方針

- **`collapseBlankLines = true`** (= 既定で連続空行を 1 行送信)。ユーザー指示原文「既定値は、連続改行は1行のみ送信する」に合致。
- **`commentPrefixes = ["'", "//"]`** (＝既定でコメント送信しない)。
  - 既存 UI の「コメント行を送信しない」チェック (`_commentFilterEnabled`) は **per-window 設定の override として残す** か、**廃止して per-command に統合**するかは Phase 1 実装時に対話確認する。
- **クローズ禁止は false** (現挙動)。

### 1.4 影響範囲

- `Core/SessionProfile.cs` ... 5 キー追加 + バリデーション
- `Forms/MdiParentForm.cs` ... 起動時 autoStartCount ループ, geometry 適用, クイック切替バーから閉じる経路の禁止判定
- `Forms/TerminalChildForm.cs` ... `FormClosing` で禁止フラグ確認, システムメニュー追加 (Win32 `GetSystemMenu`/`AppendMenu`/`WM_SYSCOMMAND` 受信), 送信前処理に collapseBlankLines / commentPrefixes 適用
- 新規 `Core/AmmSettingsDialog.cs` (per-window 設定ダイアログ)

### 1.5 確定した論点 (Phase 1 着手時の対話結果)

- Q1: 既存「コメント行を送信しない」全体 toggle は **per-command 上の override として残す** (ON 時に legacy TrimStart-based ApplyCommentFilter を per-command フィルタに上乗せ)
- Q2: 「全ペイン送信モード」と autoStartCount → 最後起動 MDI を active、broadcast 設定はそのまま継承
- Q3: AMM ファイル書き戻しは **System.Text.Json round-trip + WriteIndented** のみ (コメント・並び保持なし、`DefaultIgnoreCondition.WhenWritingDefault` で既定値キーは省略)
- Q4: `windowGeometry` 座標は **MDI client 相対 (px)**
- 緊急脱出: closeProhibited の MDI は **Shift+× で強制クローズ可能** (UserClosing のみ抑止)
- 起動 N 個目カウンタ: **生存数+1、穴埋めなし**

### 1.6 実装結果サマリ (2026-04-27)

| Step | 変更ファイル | 内容 |
|---|---|---|
| 1-A | `Core/SessionProfile.cs`, `Amm.Tests/SessionProfileTests.cs` | 5 キー追加 (autoStartCount/closeProhibited/collapseBlankLines/commentPrefixes/windowGeometry)、`WindowGeometryEntry`、`TryGetGeometryForIndex`、`IsCommentLine`、`FilterLinesForSend`。テスト 11 件追加 (合計 23/23 PASS) |
| 1-B | `Forms/MdiParentForm.cs` | `OnShown` に autoStartCount ループ、`OpenTerminal` で生存数+1 → geometry 適用 → hit 時 Manual + Normal、miss 時 Maximized |
| 1-C | `Forms/MdiParentForm.cs`, `Forms/TerminalChildForm.cs` | `TerminalChildForm.Profile` を public 公開、`ApplyPerCommandFilter` 単一窓口を新設、`SendInput` / `SendToIndexed` / `EditorBridge` を per-target 前処理に変更 (broadcast は target ごとに filter) |
| 1-D | `Core/Win32SystemMenu.cs` (新規), `Core/AmmSettingsDialog.cs` (新規), `Forms/TerminalChildForm.cs`, `Forms/MdiParentForm.cs` | システムメニュー「AMM 設定…」追加 (WM_SYSCOMMAND hook)、3 項目編集ダイアログ、`SaveProfilesToAmmFile` で profiles.amm 書き戻し (FileSystemWatcher 一時停止)、closeProhibited で UserClosing 抑止 + Shift で緊急脱出 |

検証: `dotnet test` 48/48 PASS、`dotnet build` 0 警告 0 エラー。実機動作確認は次セクションで実施。

---

## Phase 2: メニュー再構成 + 入力待ち通知

### 2.1 メニュー構成 (現状 → 新)

現状: `新規 / 整列 / 設定 / ヘルプ`

新:

```
ファイル(&F)
  名前を付けて保存(&A)...    AMM ファイル保存
  上書き保存(&S)              AMM ファイル保存
  コマンド追加(&N)...         AMM ファイル編集 (テンプレート選択 or 自由入力)
  コマンド編集(&E)...         AMM ファイル編集
  閉じる(&X)
コマンド(&C)
  <profile 1>                 起動
  <profile 2>                 起動
  ...                         (現「新規」メニューを移動)
表示(&V)
  <MDI 1 タイトル>            選択でフォーカス
  <MDI 2 タイトル>            選択でフォーカス
  ---
  タイル縦(&V)
  タイル横(&H)
  カスケード(&C)              (現「整列」を移動)
設定(&O)                      現状維持 (コメント行/下部パネル/全ペイン送信/送信後クリア)
ヘルプ(&H)                    現状維持
```

### 2.2 コマンド追加テンプレート

「コマンド追加」で以下のテンプレートをドロップダウンから選択できる。選択後はテキストフィールドで自由編集可:

- CMD (cmd.exe)
- PowerShell (pwsh / powershell.exe)
- Claude Code
- Codex
- Copilot
- Gemini
- カスタム (空)

(現 `profiles.amm` の内容と同じ。テンプレート定義は `Core/SessionProfileLoader` 内の static array としてハードコード予定)

### 2.3 入力待ち通知

クイック切替バーの各 MDI ボタンに対し、**`WaitPatternDetector` が wait 状態と判定したとき** ボタン背景色を変更する (案: 黄色系 / 既存の "送信先" ハイライトと衝突しない色)。

- 入力欄に文字入力中の MDI と入力待ちの MDI が別々に区別できる必要あり (色を 2 種使い分け)
- 詳細色 / アニメ有無は実装時対話。

### 2.4 影響範囲

- `Forms/MdiParentForm.cs` ... メニュー全面再構成, ボタン色変更ロジック
- `Forms/TerminalChildForm.cs` ... wait 状態変化を親に通知する event 追加
- 新規 `Core/CommandTemplateDialog.cs`

### 2.5 実装結果サマリ (2026-04-27)

| 変更 | 内容 |
|---|---|
| `Core/SessionProfile.cs` | `SessionProfileLoader.CommandTemplates` 静的 array 追加 (CMD/PowerShell/Claude/Codex/Copilot/Gemini/空 の 7 件) |
| `Core/CommandTemplateDialog.cs` (新規) | コマンド追加・編集モーダル。13 フィールド (8 共通 + 5 v2) 編集可、追加モードはテンプレ ComboBox で初期値投入。入力エラーは MessageBox |
| `Core/AppLaunchOptions.cs` | `ProfilesPath` を `init` → `set` に緩和 (Save As 経由でのパス切替対応) |
| `Forms/MdiParentForm.cs` | メニュー全面再構成 (ファイル/コマンド/表示/設定/ヘルプ)、`_commandMenu` `_viewMenu` を field 化、`OnFileSave/OnFileSaveAs/OnCommandAdd/OnCommandEdit/RebuildCommandMenu/RefreshViewMenu` を追加。`RefreshMdiButtonBar` のボタン色決定にアクティブ青>入力待ち薄黄の優先順位を導入 (RGB 255,240,180) |

検証: `dotnet test` 48/48 PASS、`dotnet build` 0 警告 0 エラー。実機検証は Phase 1 と一括で実施予定。

---

## Phase 3: MCP サーバ機能 (要事前討議)

### 3.1 機能要件 (ユーザー指示)

- MDI 内の CLI 型 AI エージェントに対し、**MCP 経由で**メッセージ送信できるエンドポイントを amm が提供する。
- メソッド:
  1. `send_message` — 送信
     - params: `recipient` (nickname / 空=全員ブロードキャスト), `mode` ("first" | "all", 同 nickname 複数起動時の挙動), `message` (string, single/multi line)
     - response: `delivered_count` (受け取れた人数)
  2. `list_participants` — 参加者リスト取得
  3. `peek_queue` — キューに溜まったメッセージの確認
- AMM ファイルで profile 毎に **`nickname` キー** を設定。設定無しの profile はメッセージ受信不可 (送信もできない)。
- 受信時:
  - 該当 MDI が **入力待ち** → そのまま MDI に流し込む (= terminal stdin に書く)
  - そうでない → **キューに保存**。次回入力待ちになったタイミングで自動的に流し込む or `peek_queue` で取り出し。

### 3.2 要決定事項 (UDR-amm-P3 で決める)

| 論点 | 案 A | 案 B | 案 C |
|---|---|---|---|
| トランスポート | stdio (子プロセスとして起動される MCP) | HTTP/SSE (別プロセスから接続) | 両対応 |
| 常駐プロセス | amm 本体に in-process ホスト | 同梱の小さな bridge exe | OS サービス化 |
| 認証 | 無し (ローカルのみ) | トークン (AMM ファイル / 環境変数) | OS ユーザー一致のみ |
| キュー永続化 | メモリのみ | `%LocalAppData%\amm\queue\` に JSON | SQLite |
| nickname 衝突 | 起動順で 1, 2, … サフィックス自動 | エラーで起動拒否 | "first/all" モードに依存 |

**現時点の推奨**: stdio + in-process + 認証なし + メモリキュー + サフィックス自動 (= 最小構成)。
ただし MCP は通常別プロセスから来るため、最終的には **SSE/HTTP の loopback** が現実的という判断もあり、Phase 3 着手時に MCP クライアント側のユースケース (どの AI から何を呼ぶ想定か) を確認してから決定する。

### 3.3 影響範囲

- 新規 `Core/Mcp/` 配下一式 (host, dispatcher, queue)
- `Core/SessionProfile.cs` に `nickname` 追加
- `Forms/TerminalChildForm.cs` に受信注入 API 追加
- `Amm.csproj` に MCP SDK / JSON-RPC 依存追加

### 3.4 実装結果サマリ (2026-04-27)

| Step | 変更ファイル | 内容 |
|---|---|---|
| 3-A | `Core/SessionProfile.cs`, テスト | `Nickname` (string?) 追加。テスト 3 件 (51 PASS) |
| 3-B | `Core/Mcp/MessageQueue.cs` (新), `Core/Mcp/MessageDispatcher.cs` (新), `Core/Mcp/McpPipeServer.cs` (新), `Forms/MdiParentForm.cs` | キュー (100 件 cap, FIFO drop), ディスパッチャ (broadcast / first / all + 入力待ち優先), Named Pipe + JSON-RPC + MCP プロトコル (initialize/tools/list/tools/call), MdiParentForm が IMcpHost 実装 + WaitState 遷移時 flush |
| 3-C | `Amm.Mcp/Amm.Mcp.csproj` (新), `Amm.Mcp/Program.cs` (新), `Amm.sln` | bridge `amm-mcp.exe`。stdin/stdout ↔ Named Pipe を `Stream.CopyToAsync` で双方向リレー。引数 `--pipe-name` / 環境変数 `AMM_MCP_PIPE_NAME` / 既定 `amm-mcp-{user}`。GUI 未起動時はタイムアウト 5 秒で exit 2 |
| 3-D | `AMMOperator/Amm/Amm.csproj` (InternalsVisibleTo), `Amm.Tests/McpTests.cs` (新) | キュー / ディスパッチャ / プロトコルの単体テスト 21 件 (72/72 PASS) |

検証: `dotnet test` 72/72 PASS、3 csproj とも 0 警告 0 エラー。

### 3.5 利用方法

#### Claude Desktop (claude_desktop_config.json) / Claude Code 設定例

```jsonc
{
  "mcpServers": {
    "amm-operator": {
      "command": "C:\\path\\to\\amm-mcp.exe"
    }
  }
}
```

引数オプション (任意):
- `--pipe-name <name>` — パイプ名を上書き (例: 別ユーザーの GUI に接続したい時)
- `--connect-timeout <ms>` — 接続タイムアウト (既定 5000ms、0 で無制限)

環境変数:
- `AMM_MCP_PIPE_NAME` — 同上 (引数より優先度低)

#### AMM ファイル (profiles.amm) で nickname を設定

```jsonc
{
  "profiles": [
    {
      "name": "Claude Code",
      "executable": "claude.exe",
      "nickname": "claude",     // ← MCP 受信用の名前
      "newlineMode": "LF",
      "waitPatterns": [">\\s*$"]
    }
  ]
}
```

`nickname` 未設定の profile は MCP からは見えない。

#### 実行例 (Claude Code から)

```text
> use the amm-operator MCP to send "ls -la" to nickname "claude"
→ tools/call send_message {"recipient":"claude","message":"ls -la"}
→ delivered_count: 1
```

---

## Phase 4: 補助機能

### 4.1 システムメニュー「エディタ連携ファイルのフルパスをコピー」

各 MDI のシステムメニューに「**エディタ連携ファイルパスをコピー**」を追加。クリック時、現在エディタ連携で開いているテキストファイルのフルパスをクリップボードに格納する。エディタ連携が未設定なら disabled。

### 4.2 CMD stdout リダイレクト経由メッセージ送信 (feasibility 結論)

**実装可能。Phase 3 の MCP インフラを再利用するため、Phase 3 完了が前提。**

採用案: **`amm-send` CLI 同梱**

```
echo "hello" | amm-send <nickname>
amm-send <nickname> --file payload.txt
amm-send --broadcast --message "hi"
```

- `amm-send` は loopback で amm の MCP サーバに JSON-RPC を投げる小さな実行ファイル
- amm のインストール先 (実行ファイル隣) に配置 + PATH を設定するか、ユーザーが手動で配置
- AMM ファイル側に `--mcp-port` のような hint を書く必要があるかは Phase 3 のトランスポート決定次第

### 4.3 オープン論点 (確定)

- amm-send は **`amm-mcp.exe` の `send` サブコマンド** として統合 (新 csproj 不要)
- port hint は不要 (Named Pipe 名で識別)

### 4.4 実装結果サマリ (2026-04-27)

| 変更ファイル | 内容 |
|---|---|
| `Core/Win32SystemMenu.cs` | `SC_AMM_COPY_EDITOR_PATH = 0x1020` 追加、`RegisterAmmSettings` のシグネチャを 2 メニュー対応に拡張 |
| `Forms/TerminalChildForm.cs` | `EditorPathCopyRequested` イベント追加、WndProc で SC_AMM_COPY_EDITOR_PATH を中継 |
| `Forms/MdiParentForm.cs` | `OnEditorPathCopyRequested` ハンドラ追加 (`_editorBridges` 逆引き → `Clipboard.SetText`)、`EditorBridge` に `Target` / `FilePath` / `IsActive` プロパティ追加 |
| `Amm.Mcp/Program.cs` | サブコマンド分岐 (引数最初の語) を実装。`bridge` (既定) / `send <nickname> [msg]` / `send --broadcast` / `list`。共通オプション `--pipe-name` `--connect-timeout` は両モードで共有。stdin パイプ入力対応 |

検証: 4 csproj 全て 0 警告 0 エラー、テスト 72/72 PASS。

### 4.5 amm-mcp 使い方 (Phase 4 で増えたモード)

```
# 1. MCP bridge (既定): Claude Desktop の "command" にこれを書く
amm-mcp.exe

# 2. CLI 送信
amm-mcp.exe send claude "echo hello"
amm-mcp.exe send --broadcast "全員へ通知"
echo "msg" | amm-mcp.exe send claude
amm-mcp.exe send claude --mode all "同 nickname 全員へ"

# 3. 参加者一覧
amm-mcp.exe list

# 共通オプション
amm-mcp.exe send claude --pipe-name "Custom" --connect-timeout 2000 "msg"
```

終了コード: `0`=成功 / `1`=引数不正 / `2`=GUI 未起動 / `3`=IO / `4`=MCP エラー

---

## 進め方

1. Phase 1 から順に着手。各 Phase で:
   1. UDR 起票 (`/udr-record` skill)
   2. 本書の対応 Phase 章を「実装計画 (具体)」追記で更新
   3. 実装 → ビルド → テスト → コミット
   4. UDR sync (`/udr-sync` skill)
2. Phase 1 完了時点で本書の Phase 1 章末尾に「実装結果サマリ」追記
3. 次 Phase に着手するときに本書を読み直して文脈復元

## 関連 UDR (既存)

- UDR-amm-20260424T1015-9b9 [des] amm 入力パネル拡張仕様 (v1) — Phase 1 でコメント記号扱いを per-command 化する点は本 UDR の延長
- UDR-amm-20260424T1031-116 [des] MDI 直アクセス UI — Phase 2 のクイック切替バー視覚状態追加に関連
- UDR-amm-20260424T1058-0e9 [des] 入力パネル UI 簡素化 — Phase 2 のメニュー再構成と並行
- UDR-amm-20260423T1213-6c2 [des] 起動設定の配布 UX 拡張 — Phase 1 の AMM ファイル拡張に関連
