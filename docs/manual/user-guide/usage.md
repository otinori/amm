# amm 使い方ガイド

`amm.exe` / `amm`（macOS）(GUI) と `amm-mcp.exe` / `amm-mcp`（macOS）(MCP bridge / CLI / REPL) の使い方をまとめたユーザーガイド。Windows 版・macOS 版共通（差異がある箇所は明記）。

---

## 目次

- [1. amm とは](#1-amm-とは)
- [2. インストールと起動](#2-インストールと起動)
- [3. トップバーの操作](#3-トップバーの操作)
- [4. ペインタイトルバー](#4-ペインタイトルバー)
- [5. キーボードショートカット](#5-キーボードショートカット)
- [6. 入力パネル](#6-入力パネル)
- [7. ペインの並び順](#7-ペインの並び順)
  - [7.1 終了時 git commit / push ガード](#71-終了時-git-commit--push-ガード)
- [8. profiles.amm スキーマ](#8-profilesamm-スキーマ)
- [9. amm-mcp (MCP / CLI / REPL)](#9-amm-mcp-mcp--cli--repl)
- [10. Amm.PowerShell モジュール](#10-ammpowershell-モジュール)
- [11. MCP ゲートウェイ](#11-mcp-ゲートウェイ)
- [12. ファイル配置 (ユーザーごと)](#12-ファイル配置-ユーザーごと)
- [13. トラブルシュート](#13-トラブルシュート)

---

## 1. amm とは

Windows / macOS ネイティブの単一ウィンドウ内ペイン管理型マルチターミナル。CMD / PowerShell / zsh / Claude Code / GitHub Copilot CLI / OpenAI Codex CLI / Gemini CLI 等を**同時に並べて、共通の入力欄から操作**する。

- **UI**: Rust (Tauri v2) + WebView2 (Windows) / WKWebView (macOS) + xterm.js、単一ウィンドウ内のペイン（絶対座標配置のパネル）としてターミナルが並ぶ
- **PTY**: `portable-pty` クレート（Windows: ConPTY、macOS: pty）
- **配布**: self-contained single exe/app（Rust ランタイム同梱不要）
- **MCP 連携**: GUI 内蔵の MCP JSON-RPC サーバ + 同梱 `amm-mcp` で他の AI クライアント (Claude Code / Claude Desktop / Codex CLI 等) から各ペインを駆動可能

---

## 2. インストールと起動

### 2.1 配布物

| ファイル | 役割 |
|---|---|
| `amm.exe` / `amm.app` | GUI 本体 |
| `amm-mcp.exe` / `amm-mcp` | MCP stdio サーバ / CLI / REPL |
| `profiles.amm` | 既定設定ファイル (起動時に exe/app 横を探す) |

macOS では `amm-mcp` は `.app` バンドル内 `Contents/Resources/amm-mcp` に同梱される。Windows 版のようなインストーラ主導の PATH 登録が無いため、`amm-mcp` を素のコマンド名としてターミナルから直接起動することはできない。GUI の「AI設定 ▶」→「CLI への MCP 登録...」ボタンが実体パスを自動解決して登録を代行するので、通常は手動でパスを探す必要はない。

### 2.2 起動方法

```
amm.exe          # Windows
open ./amm.app    # macOS
```

起動時の挙動:
1. **CLI 引数で profiles ファイルを指定**していればそれを読む
2. なければ exe/app と同じディレクトリの **`profiles.amm`** を読む（macOS では `profiles.macos.amm` が既定でこの位置に配置される）
3. ファイルが無ければ内蔵デフォルト profile（Windows: `cmd.exe`、macOS/Linux: `$SHELL` 環境変数またはフォールバックの `/bin/zsh`）で起動
4. 各 profile の `autoStartCount` に従ってペインを自動起動

### 2.3 `.amm` ファイルの関連付けと自動起動の確認ゲート

インストーラでインストールすると `.amm` 拡張子が `amm.exe`/`amm.app` に関連付けられる。Explorer / Finder 上で任意の `.amm` ファイルをダブルクリックすると、そのファイルを読み込んで amm が起動する。

**セキュリティ上の確認ゲート**: 外部から明示的に開かれた（CLI 引数 / ファイル関連付け経由の）`.amm` プロファイルに `mcpServers` の自動起動設定（`autoStart: true`）が含まれる場合、初回は必ず確認ダイアログ（自動起動されるコマンド一覧を提示）を挟む。許可すると「パス + その時点のファイル内容の FNV-1a ハッシュ」を信頼記録として保存し、次回以降は内容が変わらない限り再確認しない。内容が変われば（ハッシュ不一致）、パスが同じでも再度確認を求める。既定の `profiles.amm`（exe/app 隣接パスからの自動読み込み）はこのゲートの対象外。

### 2.4 起動引数

```
amm.exe                                  # 既定の profiles.amm を読む
amm.exe C:\path\to\custom.amm            # 別の AMM ファイルを読む
```

位置引数 1 つで profiles ファイルのパスを差し替え可能（`.amm` / `.json` どちらも可）。`--` で始まらない位置引数は 1 つまでで、2 つ目や未知の `--` フラグを渡すとエラーになる。

---

## 3. トップバーの操作

メインウィンドウ上部のトップバーはボタン列（旧 WinForms 版が持っていたメニューバーはこの移植過程で自然発生的にボタン列へ置き換わったもので、独立した設計判断ではない）。

### 3.1 ファイル ▶

| 項目 | 動作 |
|---|---|
| 開く... | 別の AMM ファイルを読み込む（既存ペインは影響なし）。読み込み後、自動起動設定に従って未起動のコマンドを起動するか確認ダイアログが出る |
| 上書き保存 | 現在アクティブな `.amm` ファイルへ書き戻す |
| 名前を付けて保存... | 別パスへ書き出し、以降の上書き保存先もそちらへ切り替える |

### 3.2 コマンド ▶

登録済みプロファイルの一覧が動的に並び、クリックでそのプロファイルのペインを 1 個起動する。プロファイルが 0 件でも以下の項目は常に表示される:

| 項目 | 動作 |
|---|---|
| `<プロファイル一覧>` | クリックで該当プロファイルのペインを起動 |
| + コマンド追加... | テンプレートからダイアログで新規プロファイルを作成 |
| コマンドを管理... | プロファイルの追加・編集・削除 |

### 3.3 並び順をリセット

「並び順をリセット」ボタン（旧版の「タイル」「記憶した配置で表示」に相当する操作を統合したもの）は、ドラッグで変えたペインの並び順を起動順に戻し、`autoStartCount` に対して不足している自動起動プロファイルを補充する。

ペインのレイアウト自体は固定サイズ・固定位置を記憶する方式ではなく、**生存ペイン数から決定的に計算されるグリッド**（`rows = floor(sqrt(n))`、`cols = ceil(n / rows)`、左上→右下の順）を毎回敷き直す方式。個別ペインのドラッグ操作はグリッド内の「並び順」を入れ替えるだけで、座標やサイズを個別に記憶することはない（[§7](#7-ペインの並び順) 参照）。

### 3.4 設定 ▶ / AI設定 ▶

| ボタン | 項目 | 動作 |
|---|---|---|
| 設定 ▶ | 全般設定... | 送信前のテキスト整形（連続空行の圧縮・コメント行のスキップ接頭辞）、クイック送信の登録内容、エディタ連携の設定をまとめて編集する（アプリ全体設定。旧版はプロファイルごとの設定だった） |
| AI設定 ▶ | MCP ゲートウェイ設定... | 外部 stdio/HTTP MCP サーバの管理（[§11](#11-mcp-ゲートウェイ)） |
| AI設定 ▶ | CLI への MCP 登録... | Claude Code / Codex / Copilot CLI / Antigravity への MCP・フック登録（[§9.2](#92-cliclaude-code等-への登録)） |

### 3.5 承認一覧を表示 / 表示トグル

| ボタン | 動作 |
|---|---|
| 承認一覧を表示 | 保留中の許可要求があれば承認オーバーレイを開く。無ければアラート表示 |
| 切替バー表示 | 下部パネルのクイック切替バーの表示切替 |
| 入力欄表示 | 共通入力欄の表示切替 |
| ステータス表示 | 下端の送信先ステータス行の表示切替 |

---

## 4. ペインタイトルバー

各ペインのタイトルバーには直接クリックできるアイコンが並ぶ（旧 WinForms 版の「MDI 子のシステムメニュー」に相当する機能は、頻用操作をアイコンとして直接配置する形に変わった）:

| アイコン | 動作 |
|---|---|
| ✎ 名前変更 | このペインの一時表示名を変更 |
| A フォントサイズ | per-ペインのフォントサイズを選択（ランタイム上書き、保存はしない） |
| 📝 エディタ連携 | 一時 `.md` ファイルをエディタで開く（保存のたびに送信） |
| 📋 エディタ連携ファイルパスをコピー | 連携中のファイルパスをクリップボードへ |
| ⋮ | 「コマンド設定...」（このペインの起動元プロファイルを編集） |
| ✕ | ペインを閉じる（Shift 押しながらで `closeProhibited` を無視して強制終了） |

タイトルバー自体を右クリックしても ⋮ と同じ「コマンド設定...」メニューが開く。タイトルバーをドラッグして別のペインへドロップすると、グリッド内の並び順が入れ替わる（[§7](#7-ペインの並び順)）。タイトルバーの専用アイコン（ダブルクリックではない）でペインをデスク全体に一時的にズームできる。

---

## 5. キーボードショートカット

macOS でも Cmd キーではなく Windows と同じ **Ctrl** キーの組み合わせを使う。

### 5.1 共通入力欄

| キー | 動作 |
|---|---|
| Ctrl+S | アクティブペインへ送信 |
| Ctrl+Shift+S | 全ペイン（生存中すべて）へ送信 |
| Ctrl+1 〜 Ctrl+9 | 番号指定のペインへ送信（アクティブ化しない） |
| Ctrl+H | 送信履歴ドロップダウン（↑/↓で選択、Enter で確定） |
| Ctrl+E | エディタ連携 |
| Esc を 500ms 以内に 2 回 | 入力欄をクリア（1 回目だけでは何もしない。履歴ドロップダウンが開いていれば 1 回目はそれを閉じるだけで 2 回押下のカウントに入らない） |
| Enter | 常に改行のみ（送信トリガーにはならない） |

### 5.2 ターミナル (ペイン) 内

xterm.js 標準のキー操作（Ctrl+Shift+C でコピー、Ctrl+V で貼り付け等）に加え、右クリックで [§6](#6-入力パネル) と同じ操作メニューが開く。`ctrlCCopyOnSelection` を有効にしたプロファイルでは選択中の Ctrl+C はコピーに、選択なしなら子プロセスへの `^C` 送信になる。

---

## 6. 入力パネル

ウィンドウ下端の領域。3 行構成（各行はトップバーの表示トグルで個別に非表示可）:

1. **クイック切替バー**: 各ペインのボタンが起動順に並ぶ
2. **共通入力欄**: 全文・選択分を Ctrl+S / Ctrl+1..9 / Ctrl+Shift+S で送信
3. **ステータス行**: 現在の送信先など

### ペイン・切替バーボタンの右クリックメニュー（共通）

ターミナル本体・クイック切替バーのボタンどちらを右クリックしても同じ項目セットが出る（旧版の「システムメニュー」相当の設定操作はタイトルバー側に一本化され、ここは送信系の操作に専念する）:

| 項目 | 動作 |
|---|---|
| 改行送信 | 対象ペインへ Enter (`\r`) を 1 つだけ送る (AI CLI の確定・継続入力用) |
| プロンプト再送信 | 対象ペインの直前の入力履歴を再実行 |
| クイック送信 ▶ | アプリ全体で共有する定型プロンプト一覧（プロファイル単位ではない。登録は「クイック送信に登録...」または [§3.4](#34-設定--ai設定-) の全般設定から） |
| クイック送信に登録... | 直前のプロンプト / クリップボードのテキストを新しい定型プロンプトとして登録 |
| コピー / 貼り付け（ターミナル本体のみ） | 選択テキストのコピー / クリップボードからの貼り付け |
| プロンプト送信 | 共通入力欄の内容をこのペインへ送信 |
| エディタ連携 | 一時 `.md` をエディタで開く |
| すべて選択 / 画面クリア（ターミナル本体のみ） | xterm.js の選択・クリア操作 |

> **CLI 側の自動コピーに注意**: Claude Code のように選択テキストを自前の OSC エスケープシーケンスでシステムクリップボードへ自動コピーし、直後に xterm 側の選択状態を自身でクリアする CLI では、選択直後に右クリックすると「コピー」項目がグレーアウトして見えることがある（テキスト自体は CLI 側の自動コピーで既にクリップボードに入っている）。zsh 等プレーンなシェルでは通常通り動作する。

### 入力待ち検出

xterm.js 出力を監視し、profile の `waitPatterns` と無出力タイムアウトで判定する。

> **より確実な検知（推奨）**: Claude Code / Codex / Copilot CLI はフック登録（[§9.2](#92-cliclaude-code等-への登録)）すると、応答完了を CLI 自身が amm へ通知するため、正規表現に頼らずに状態遷移が確定する。

| 状態 | 表示 |
|---|---|
| 実行中 | ▶ |
| 入力待ち | ● |
| 不明 | ? |
| 停止 | ■ (exited) |

**許可・確認待ち**: フック登録済みの CLI がツール実行の許可や追加情報を求めている状態になると、ペインに attention（⚠ 相当）マークが付く。

### 承認オーバーレイ（画面右上）

フック登録済みの Claude Code / Copilot CLI がツール実行の許可を求めると、メインウィンドウ右上にオーバーレイが表示される。**別ウィンドウのポップアップではない**（実機検証で「第2 WebView2 ウィンドウの生成がアプリ全体をハングさせる」重大なデッドロックが見つかったため、メインウィンドウ内 DOM オーバーレイ方式に変更した設計上の理由がある）。

ボタンは「はい」「確認」の 2 つのみ（旧「拒否」「閉じる」は撤去）:

| ボタン | 動作 |
|---|---|
| はい | その場で許可（`allow`）して解決する |
| 確認 | 対象ペインへ移動する。拒否したい場合はここでペインへ移動し、CLI 本体のネイティブな確認プロンプトで直接操作する |

- 複数ペインから同時に要求が来ても 1 件ずつ「1/N 件」で表示
- 表示直後 0.5 秒はボタンが無効（誤クリック防止）
- 対象ペインをアクティブ化する（クリックで前面化する等）と、承認要求は「無回答のまま見た」扱いで解放され、ペイン内の CLI 本体の対話プロンプトへ処理が移る

**注意**: オーバーレイで「はい」をクリックしても、Claude Code / Copilot CLI 本体側の対話型プロンプト（「Do you want to proceed?」等）は自動的には解決されない。承認オーバーレイは「今どのペインが許可待ちか」を横断的に把握するための通知機能であり、遠隔操作機能ではない。

### 確認オーバーレイ（承認オーバーレイの下、TUI 選択状態全般向け）

Notification hook の permission_prompt/elicitation_dialog、OSC9 由来の attention 等、`amm/approval` を伴わないより広い「TUI が何らかの選択を求めている」状態向けの軽量な通知。個別のツール名・入力内容は無く、対象ペイン数と単一の「確認」ボタン（対象ペインへ移動するだけ）のみで構成される。承認オーバーレイの対象ペインとは重複しないよう除外される。

---

## 7. ペインの並び順

ペインの配置は「座標・サイズを記憶する」方式ではなく、**生存ペイン数から自動計算されるグリッドに、起動順（またはドラッグで変更した並び順）で敷き詰める**方式（旧版の BSP タイリングツリー案は不採用、決定的グリッド方式が正式実装）。

- 新規ペインは末尾に追加される
- ペインのタイトルバーを別のペインへドラッグ&ドロップすると、2 つのペインの並び順スロットが入れ替わる（座標そのものを動かすのではない）
- 「並び順をリセット」（[§3.3](#33-並び順をリセット)）で起動順（`displayId` 順）に戻せる。同時に各プロファイルの `autoStartCount` に足りない分のペインを補充する
- 並び順はブラウザの localStorage（`amm-pane-layout-v1` キー）に自動保存され、次回起動時に同じ並び順・同じ生存ペイン構成で復元を試みる

### 7.1 終了時 git commit / push ガード

ペインを閉じる際、またはアプリを終了する際に、起動ディレクトリが git リポジトリかつ変更がある場合は自動でダイアログを表示して commit / push を促す。

#### 個別ペインのクローズ
1. ペインの起動ディレクトリが git リポジトリかどうか確認
2. 未コミットの変更があれば **変更をコミット** ダイアログ表示:
   - 変更ファイル一覧 + コミットメッセージ入力欄
   - `[コミット]` — `git add -A && git commit -m <メッセージ>` を実行
   - `[スキップ]` — コミットせずクローズを続行
   - `[閉じない]` — クローズをキャンセル
3. リモートが設定済みかつ未プッシュのコミットがあれば **未プッシュ確認** ダイアログ（`[はい]`/`[いいえ]`/`[キャンセル]`）

#### アプリ終了時
全ペインの起動ディレクトリを **リポジトリ単位で集約**し、同じリポジトリを参照する複数ペインがあっても 1 回だけ確認する。実行中のセッションが残っていれば先に終了確認、続けて git ガード、最後に「プロファイルに未保存の変更があります」の保存確認（保存先パスを明示した 3 択モーダル: キャンセル / 保存せず終了 / 保存して終了）の順に確認する。

> **注意**: git がインストールされていない、またはリポジトリ外のディレクトリには何も表示されない。

---

## 8. profiles.amm スキーマ

`profiles.amm` は JSON 形式で、トップレベルに `profiles` 配列（と、任意で `mcpServers` 配列）を持つ。`profiles.json` でも同じスキーマで読み書き可能。macOS では既定で `profiles.macos.amm`（`zsh`/`claude`/`copilot` 等の bare 実行ファイル名、PATH 解決前提）が使われる。

```jsonc
{
  "profiles": [
    {
      "name": "Claude Code",
      "commandType": "ClaudeCode",
      "executable": "claude",
      "args": [],
      "workingDirectory": "",
      "resumeOnStart": false,
      "outputEncoding": "UTF-8",
      "autoChcp": false,
      "waitPatterns": ["^>"],
      "initialCommands": [],
      "ctrlCCopyOnSelection": true,
      "sessionLog": false,
      "theme": { "background": "#1e1e1e", "foreground": "#d4d4d4" },
      "closeOnExit": true,
      "autoStartCount": 0,
      "closeProhibited": false,
      "windowGeometry": [],
      "nickname": "claude",
      "sendLineByLine": false,
      "selectWorkingDirOnStart": false,
      "promptNewNameOnCommandAdd": false,
      "fontSize": null,
      "titleBarColor": null
    }
  ],
  "mcpServers": []
}
```

### 8.1 フィールド一覧

| フィールド | 型 | 既定 | 説明 |
|---|---|---|---|
| `name` | string | `""` | メニュー / タイトルバー表示名 |
| `commandType` | string | `""` | コマンドタイプ（`ClaudeCode`/`Codex`/`CopilotCli` 等のプリセット識別子）。未設定または `"Other"` の場合、`nickname`/`executable`/`args` から自動推測される |
| `executable` | string | `"cmd.exe"`（macOS/Linux は `$SHELL` 由来） | 実行ファイル。環境変数展開可 |
| `args` | string[] | `[]` | 起動引数 |
| `workingDirectory` | string? | `""` | プロセス起動時の CWD。`""` / 未指定でアプリ起動時のカレントフォルダ |
| `resumeOnStart` | bool | `false` | `true` で起動引数に CLI ごとのセッション再開トークンを追加する（Claude Code/Copilot CLI は `--resume`、Codex は `resume`） |
| `outputEncoding` | string? | `"UTF-8"` | PTY 出力エンコーディング |
| `autoChcp` | bool | Windows: `true` | 起動直後に `chcp 65001` を自動送信（Windows のみ意味を持つ） |
| `waitPatterns` | string[] | 共通デフォルト | 入力待ち判定の正規表現 |
| `initialCommands` | string[] | `[]` | PTY 起動直後に順次送信するコマンド列 |
| `ctrlCCopyOnSelection` | bool | `true` | Ctrl+C を選択時はコピー、なしなら `^C` 送信 |
| `sessionLog` | bool | `false` | セッションログを平文で追記（[§12](#12-ファイル配置-ユーザーごと)）。機微データでは無効推奨 |
| `theme` | object? | `null` | xterm.js の theme オプション |
| `closeOnExit` | bool | `true` | 子プロセス終了時にペインも自動クローズ |
| `autoStartCount` | int | `0` | アプリ起動時にこの profile を何個自動起動するか |
| `closeProhibited` | bool | `false` | `true` で ✕ 等を無効化（常駐 AI エージェント向け、Shift+✕ で強制終了可） |
| `windowGeometry` | array | `[]` | 旧スキーマ互換のため残る（現行の並び順は [§7](#7-ペインの並び順) のグリッド方式で、座標は使われない） |
| `nickname` | string? | `null` | MCP 受信時の宛先名。未設定なら MCP に登録されない |
| `sendLineByLine` | bool | `false` | マルチラインを 1 行ずつ Enter 区切りで打つ |
| `selectWorkingDirOnStart` | bool | `false` | 起動時にフォルダ選択ダイアログを表示 |
| `promptNewNameOnCommandAdd` | bool | `false` | コマンドメニューからの手動コマンド追加時、名前入力ダイアログを挟んで profile を clone する |
| `fontSize` | int? | `null` | xterm.js のフォントサイズ (px) 既定値 |
| `titleBarColor` | string? | `null` | このコマンドのペインタイトルバー色（CSS color 文字列）。`null` でコマンドタイプのプリセット色、それも無ければ既定色 |

送信前のテキスト整形（連続空行の圧縮・コメント行スキップ接頭辞）とクイック送信定型文は、**profiles.amm ではなくアプリ全体設定**に移動した（[§3.4「全般設定」](#34-設定--ai設定-)、[§12](#12-ファイル配置-ユーザーごと) の `format-settings.json` / `quick-prompts.json` 参照）。

### 8.2 mcpServers（トップレベル、ファイル固有の MCP ゲートウェイ設定）

[§11.3](#113-mcpservers-フィールド一覧) 参照。

---

## 9. amm-mcp (MCP / CLI / REPL)

GUI 起動中はログオンユーザー専用の **Named Pipe**（Windows）/ Unix domain socket（macOS/Linux）で MCP JSON-RPC サーバが常駐し、`amm-mcp` が同じ経路に接続する。複数の動作モードを 1 つの実行ファイルにまとめている:

| モード | 起動方法 | 用途 |
|---|---|---|
| **stdio bridge** | `amm-mcp` (stdin redirect) または `amm-mcp --bridge` | MCP クライアント (Claude Code / Claude Desktop 等) と amm GUI の双方向リレー |
| **REPL** | `amm-mcp` (端末から引数なし起動) | 対話モード。`list` / `send` / `peek` / `help` / `quit` を受け付ける |
| **send (CLI)** | `amm-mcp send <nickname> [msg]` | シェルから 1 ショット送信。`msg` 省略で stdin を全部読む |
| **list (CLI)** | `amm-mcp list` | 参加者 (nickname を持つペイン) を JSON 配列で stdout 出力 |
| **notify / approve (hook 用)** | CLI の hook から自動起動 | CLI の応答完了・許可要求等を amm へ通知する。手動実行は不要 ([§9.2](#92-cliclaude-code等-への登録) のフック登録で自動設定) |

### 9.1 公開ツール (MCP `tools/list`)

| ツール | 引数 | 動作 |
|---|---|---|
| `send_message` | `recipient?` `mode?` `message` | nickname 宛にテキスト注入。`recipient` 省略でブロードキャスト。`mode="first"`（既定）は入力待ち優先 → 起動順 fallback、`mode="all"` は同 nickname 全ペイン |
| `list_participants` | (なし) | nickname を持つ各ペインの情報を返す |
| `peek_queue` | `recipient?` | 配信待ちキューの中身を覗き見（デキューしない） |
| `pane/open` | `command?` `profile_name?` `args?` `title?` `workingDirectory?` | ペインを新規起動し `session_id` を返す。`command`（アドホック起動）または `profile_name`（既存プロファイルを名前で起動）のどちらかを指定 |
| `pane/close` | `session_id` `force?` | `session_id` で指定したペインを閉じる。`force=true` で `closeProhibited` を無視 |
| `pane/wait_state` | `session_id` `target_state` `timeout_ms?` | 指定ペインが `target_state`（`"idle"`=入力待ち、`"attention"`=許可待ち）になるまでブロック。既定タイムアウト 300000ms |

> 旧版で `mdi/open` / `mdi/close` / `mdi/wait_state` と呼ばれていたツールは、この移植で `pane/open` / `pane/close` / `pane/wait_state` に改称されている。

### 9.2 CLI(Claude Code等) への登録

**推奨: GUI から一括登録** — 「AI設定 ▶」→「CLI への MCP 登録...」で、Claude Code / Codex / Copilot CLI / **Antigravity** のユーザー（端末）スコープ設定ファイルへチェックボックスで登録・削除できる。書き込み先と形式:

| 項目 | CLI | ファイル | 形式 |
|---|---|---|---|
| MCP | Claude Code | `~/.claude.json` | ルート `mcpServers.amm` |
| MCP | Codex | `~/.codex/config.toml` | `[mcp_servers.amm]` セクション |
| MCP | Copilot CLI | `~/.copilot/mcp-config.json` | `mcpServers.amm` |
| MCP | Antigravity | `~/.antigravity/mcp-config.json` | `mcpServers.amm` |
| フック | Claude Code | `~/.claude/settings.json` | `hooks.Stop` / `hooks.Notification` に `amm-mcp notify --source claude`、`hooks.PermissionRequest` に `amm-mcp approve` |
| フック | Codex | `~/.codex/config.toml` | ルート `notify` キーに `amm-mcp notify --source codex` |
| フック | Copilot CLI / Antigravity | `~/.copilot/hooks/amm-hooks.json` / `~/.antigravity/hooks/amm-hooks.json` | agentStop / permissionRequest フック (amm 専有ファイル) |

既存の設定内容は保全し、`amm` エントリのみ追加・削除する。登録済みでも `command` のパスが現在の `amm-mcp` と異なる場合は適用時に更新される。CLI 起動中に変更した場合は次回起動から有効。

手動で登録する場合:

```
claude mcp add amm -- /path/to/amm-mcp
codex mcp add amm -- /path/to/amm-mcp
```

または `~/.claude.json` / プロジェクトの `.mcp.json`:

```jsonc
{
  "mcpServers": {
    "amm": { "command": "/path/to/amm-mcp" }
  }
}
```

登録後、Claude Code 内から `/mcp` で接続状態を確認できる。GUI 未起動時は接続タイムアウトし exit code 2。

### 9.3 Claude Desktop / 他の MCP クライアント

MCP 公式仕様 (`initialize` / `tools/list` / `tools/call`) 準拠なので、stdio MCP に対応するクライアントなら共通で動く:

```jsonc
{
  "mcpServers": {
    "amm": { "command": "/path/to/amm-mcp", "args": ["--connect-timeout", "10000"] }
  }
}
```

### 9.4 profiles.amm 側の準備

MCP で操作したい profile に **`nickname`** を付ける（未設定なら `list_participants` にも出ない）。同名 nickname を複数ペインに付けても良い（`mode="first"/"all"` で挙動切替）。

### 9.5 CLI 使い方

```
amm-mcp list                                   # 参加者一覧 (JSON 配列)
amm-mcp send claude "ls -la"                   # 入力待ちの "claude" ペインに 1 行送る
cat prompt.md | amm-mcp send claude            # stdin から流し込む
amm-mcp send claude --all "全 claude ペインに通知"
amm-mcp send --broadcast "session start"       # nickname 登録済みの全ペインへ送る
amm-mcp                                        # 対話 REPL
```

### 9.6 共通オプション

| オプション | 既定 | 意味 |
|---|---|---|
| `--pipe-name <name>` | `amm-mcp-{ユーザ名}` | 接続先パイプ/ソケット名を上書き |
| `--connect-timeout <ms>` | `5000` | GUI への接続タイムアウト (`0` で無制限) |
| 環境変数 `AMM_MCP_PIPE_NAME` | (未設定) | `--pipe-name` の代替 (引数優先) |

### 9.7 終了コード

| code | 意味 |
|---|---|
| 0 | 成功 |
| 1 | 引数不正 |
| 2 | GUI 未起動 (接続タイムアウト) |
| 3 | パイプ/ソケット IO エラー |
| 4 | MCP プロトコル / サーバ側エラー |

### 9.8 セキュリティ

Windows では Named Pipe の ACL を SDDL 文字列 (`D:(A;;GA;;;{現在ユーザーの SID})`) 経由で現在ログオンユーザーのみに制限している。ACL 構築に失敗した場合は例外を投げず、ログを出して既定のパイプセキュリティにフォールバックする（起動を止めない設計）。

---

## 10. Amm.PowerShell モジュール

`Amm.PowerShell` は PowerShell 5.1 以降で動く**コンパイル不要のスクリプトモジュール**（`.psm1`。旧版のようなバイナリモジュールではなく .NET SDK は一切不要）。内部で `amm-mcp` と同じ Named Pipe/Unix socket 経由の MCP JSON-RPC を直接叩く。amm GUI のペインを PowerShell スクリプトから直接制御でき、**オートパイロット**（AI エージェントへの指示投入 → 完了待ち → 次指示）をシンプルなパイプラインで記述できる。

### 10.1 インポート

```powershell
Import-Module "C:\Program Files\amm\Amm.PowerShell.psm1"
```

常に読み込む場合は `$PROFILE` に追記する。読み込まれると以下の Cmdlet が利用可能になる:

| Cmdlet | 用途 |
|---|---|
| `Connect-Amm` | 接続確認 (起動確認のみ。各 Cmdlet は暗黙的に自動接続) |
| `Disconnect-Amm` | no-op (将来の永続接続モード用予約) |
| `Open-AmmWindow` | ペインを新規起動、`Amm.Session` を返す |
| `Close-AmmWindow` | ペインを閉じる。パイプラインで受け取れる |
| `Get-AmmSession` | 現在起動中のペイン一覧を返す |
| `Send-AmmMessage` | ペインにテキストを送信 |
| `Wait-AmmIdle` | 指定セッションが目的の状態になるまでブロック |

### 10.2 共通パラメータ

全 Cmdlet に `-PipeName`（既定 `amm-mcp-{ユーザ名}`、環境変数 `AMM_MCP_PIPE_NAME` でも指定可）と `-ConnectTimeoutMs`（既定 `5000`）が存在する。

### 10.3 Open-AmmWindow

```powershell
# ByCommand: コマンドを直接指定
Open-AmmWindow -Command <string> [-Args <string[]>] [-Title <string>] [-WorkingDirectory <string>]

# ByProfile: profiles.amm の既存プロファイルを名前で起動 (設定を自動継承)
Open-AmmWindow -ProfileName <string> [-Title <string>] [-WorkingDirectory <string>]
```

`Amm.Session { SessionId; Title }` を返す。`SessionId` は `Close-AmmWindow` / `Wait-AmmIdle` に渡す識別子。

```powershell
$s = Open-AmmWindow -Command claude -Title "Agent-1" -WorkingDirectory C:\projects\myapp
$s = Open-AmmWindow -ProfileName "Claude Code"
```

### 10.4 Close-AmmWindow

```powershell
Close-AmmWindow [-SessionId] <string> [-Force] [-WhatIf] [-Confirm]
```

`-Force` で `closeProhibited` を無視して強制終了。`$s | Close-AmmWindow` のようにパイプできる。

### 10.5 Get-AmmSession

```powershell
Get-AmmSession | Format-Table Title, SessionId
```

起動中のペインを `Amm.Session` の配列で返す。`Title` は `nickname (instance)` 形式、`SessionId` には実際のセッション ID が入る。

### 10.6 Send-AmmMessage

```powershell
Send-AmmMessage [-Nickname] <string> [-Message] <string> [-Mode <string>]
```

`-Nickname` は `[Alias("Title")]` 付きで `Get-AmmSession` の戻り値をそのままパイプできる。`-Mode`: `"first"`（既定、入力待ち優先） / `"all"`（同 nickname 全ペイン）。

### 10.7 Wait-AmmIdle

```powershell
Wait-AmmIdle [-SessionId] <string> [-TargetState <string>] [-TimeoutMs <int>]
# または nickname で直接指定 (session_id を自動解決)
Wait-AmmIdle -Nickname <string> [-TargetState <string>] [-TimeoutMs <int>]
```

指定セッション（または nickname から解決したセッション）が `TargetState`（既定 `"idle"`）に到達するまでブロックする（サーバ側待機、既定タイムアウト 300000ms）。`WaitResult { State; ElapsedMs }` を返す。nickname が見つからない場合は終了エラー。

### 10.8 オートパイロット例

```powershell
Import-Module "C:\Program Files\amm\Amm.PowerShell.psm1"

$s = Open-AmmWindow -Command claude -Title "Agent-1" -WorkingDirectory C:\projects\myapp
$s | Wait-AmmIdle
Send-AmmMessage -Nickname "Agent-1" -Message (Get-Content task.md -Raw)
$r = $s | Wait-AmmIdle -TimeoutMs 600000
Write-Host "完了: state=$($r.State) elapsed=$($r.ElapsedMs)ms"
$s | Close-AmmWindow
```

並列パターン・nickname 直接指定など、他の実行例は [§10.7](#107-wait-ammidle) の `-Nickname` パラメータセットを使うとより簡潔に書ける（`Get-AmmSession` を経由しなくても `Wait-AmmIdle -Nickname claude` で直接待機可能）。

### 10.9 トラブルシュート

| 症状 | 対処 |
|---|---|
| `ConnectTimeoutMs` 超過 | `amm` が起動しているか確認。別ユーザーとして起動している場合は `-PipeName` で合わせる |
| `Wait-AmmIdle` が `State="timeout"` で即返る | フック未登録かつ `waitPatterns` が未設定。[§9.2](#92-cliclaude-code等-への登録) でフック登録するか、profile に `waitPatterns` を追記する |
| `Send-AmmMessage` で送達件数 0 | 宛先ペインが入力待ちでなく、キューに積まれた状態。入力待ちになると自動 flush される |

---

## 11. MCP ゲートウェイ

amm は外部の **MCP サーバ**（stdio 子プロセス、または HTTP エンドポイント）を管理し、そのツールを集約して自身の MCP サーバ経由で公開する **MCP ゲートウェイ**機能を持つ。Claude Code や Amm.PowerShell などの MCP クライアントは、既存の `send_message` / `pane/open` 等のツールに加えて、ゲートウェイ経由で外部サーバのツールを `<サーバ名>/<ツール名>` の形式で呼び出せる。

### 11.1 設定方法 — GUI

「AI設定 ▶」→「MCP ゲートウェイ設定...」を開くと設定ダイアログが表示される。

- **AMM 共通**: `%LOCALAPPDATA%\amm\mcp-servers.json`（macOS: `~/Library/Application Support/amm/mcp-servers.json`）に保存。全ワークスペースで読み込まれる
- **このファイル固有**: 現在の `profiles.amm` の `mcpServers` に保存

OK を押すと変更が保存され、ゲートウェイが即時ホットリロードされる（再起動不要）。

### 11.2 設定方法 — ファイル直接編集

**stdio サーバ**:

```jsonc
{
  "mcpServers": [
    { "name": "fs", "command": "npx", "args": ["-y", "@modelcontextprotocol/server-filesystem", "/Users/me/work"], "autoStart": true, "maxRestarts": 3 }
  ]
}
```

**HTTP サーバ**:

```jsonc
{
  "mcpServers": [
    { "name": "remote", "type": "http", "url": "https://example.com/mcp", "headers": { "Authorization": "Bearer ..." }, "skipTlsVerify": false }
  ]
}
```

ファイルを直接編集した場合は amm を再起動して設定を反映する。

### 11.3 mcpServers フィールド一覧

| フィールド | 型 | 既定 | 説明 |
|---|---|---|---|
| `name` | string | `""` | サーバ識別名。ツールプレフィックスに使用 (`"fs"` → `"fs/read_file"`) |
| `type` | `"stdio"` \| `"http"` | `"stdio"` | トランスポート種別。省略時は stdio 扱い（既存ファイルは無改修で動く） |
| `command` | string | `""` | (stdio) 実行コマンド |
| `args` | string[] | `[]` | (stdio) コマンド引数 |
| `env` | object? | `null` | (stdio) 追加/上書き環境変数 |
| `autoStart` | bool | `true` | amm 起動時に自動起動する (stdio) |
| `maxRestarts` | int | `3` | クラッシュ後の最大再起動回数。`0` で再起動なし (stdio) |
| `url` | string? | `null` | (http) エンドポイント URL |
| `headers` | object? | `null` | (http) 追加ヘッダー |
| `skipTlsVerify` | bool | `false` | (http) TLS 証明書検証をスキップ |

### 11.4 ツール呼び出し

MCP クライアントから `tools/list` を呼ぶと、amm 組み込みツールの後にゲートウェイツールが `[サーバ名]` プレフィックス付き説明と共に列挙される。`tools/call` には `"{name}/{toolName}"` 形式のツール名を渡す。

### 11.5 ステータスアイコン

| アイコン | 意味 |
|---|---|
| ✓ 実行中 (N ツール) | サーバ起動済み、N 個のツールを公開中 |
| ⏳ 起動中 | `initialize` / `tools/list` 待ち |
| ✗ エラー | 起動失敗または最大再起動回数超過 |
| ○ 停止 | `autoStart: false` または未起動 |
| ● 未設定 | ゲートウェイ未反映（設定ダイアログを初めて開いた時） |

### 11.6 トラブルシュート

| 症状 | 対処 |
|---|---|
| ツールが表示されない | `npx` / `uvx` / `node` が PATH に存在するか確認。「MCP ゲートウェイ...」ダイアログでエラー内容を確認 |
| `✗ エラー: Process exited and max restarts reached` | コマンドや引数が正しいか確認。`maxRestarts` を増やすか `autoStart: false` で手動管理 |
| ファイル直接編集後に反映されない | ダイアログから OK (ホットリロード) または amm を再起動 |

---

## 12. ファイル配置 (ユーザーごと)

| 種類 | Windows | macOS |
|---|---|---|
| 設定 | `profiles.amm` (実行ファイルと同じディレクトリ) | 同左 (`.app` バンドル横) |
| アプリデータのベース | `%LOCALAPPDATA%\amm\` | `~/Library/Application Support/amm/` |
| 入力履歴 | `%LOCALAPPDATA%\amm\history.json` | `~/Library/Application Support/amm/history.json` |
| 全般設定 (連続空行圧縮/コメント接頭辞) | `...\amm\format-settings.json` | 同パターン |
| クイック送信定型文 | `...\amm\quick-prompts.json` | 同パターン |
| MCP ゲートウェイ (AMM 共通) | `...\amm\mcp-servers.json` | 同パターン |
| `.amm` 信頼済みパス一覧 | `...\amm\trusted-profiles.json` | 同パターン |
| アプリログ | `...\amm\log\app.log` | 同パターン |
| セッションログ | `...\amm\sessions\YYYYMMDD-HHMMSS-<name>.log` (profile の `sessionLog: true` 時のみ、**平文保存**) | 同パターン |
| ペインの並び順 | ブラウザ localStorage (`amm-pane-layout-v1`、[§7](#7-ペインの並び順)) | 同左 |

`history.json` は旧 .NET WinForms 版とスキーマが互換（フィールド名の大文字/小文字差異は自動吸収されるため、旧版からの移行時も引き継がれる）。

Linux では `$XDG_DATA_HOME/amm/`（未設定時 `~/.local/share/amm/`）が使われる（GUI 自体は現時点で未対応、`docs/design/cross-platform-feasibility.md` 参照）。

---

## 13. トラブルシュート

### コマンドが見つからない

`executable` の実行ファイルが PATH に見つからない。`where`（Windows）/ `which`（macOS）で確認し、`profiles.amm` の `executable` をフルパスにする。

### ペインを開いても画面が真っ黒

Windows: WebView2 Runtime が未導入の可能性（Edge がインストールされた Win10/11 なら通常は自動で入る）。ログに `WebView2 init failed` があればそれ。

### macOS: `.app` を開こうとすると「壊れているため開けません」と表示される

未 notarization・ad-hoc 署名の配布物では Gatekeeper がこの警告を出すことがある。`xattr -d com.apple.quarantine /Applications/amm.app` で quarantine 属性を除去するか、右クリック→「開く」で回避できる場合がある。

### macOS: `amm-mcp` をターミナルから直接起動できない

`.app` バンドル内 (`Contents/Resources/amm-mcp`) にあり PATH から到達できない。GUI の「AI設定 ▶」→「CLI への MCP 登録...」ボタンがフルパスを自動解決するので、通常は手動でパスを探す必要はない。直接使いたい場合は同ボタンのダイアログに表示されるフルパスを使う。

### GitHub Copilot CLI への自動 submit が効かない (既知の制約)

入力パネル / MCP / エディタ連携経由で Copilot CLI に複数行を含む内容を送ると、テキストは入力欄に届くが Enter が submit と認識されず、プロンプトが実行されないまま入力欄に蓄積することがある。

- **回避策**: 送信後に Copilot CLI のペインを直接フォーカスし、手動で Enter を押す
- **Claude Code / OpenAI Codex CLI / Gemini CLI では同条件で正常に submit される**
- 受信側 (Copilot CLI の Ink TUI 実装) 起因と推定され、amm 側の追加対応では解消しない（アーキテクチャを WinForms → Tauri に変えても再現することを実機確認済み）

### MCP クライアントから "GUI に接続できませんでした"

`amm` (GUI) が起動していない、または別ユーザーセッションで起動している。GUI 起動を確認、または `--pipe-name` で接続先を明示。

---
