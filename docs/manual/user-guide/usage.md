# amm 使い方ガイド

`amm.exe` (GUI) と `amm-mcp.exe` (MCP bridge / CLI / REPL) の使い方をまとめたユーザーガイド。

---

## 目次

- [1. amm とは](#1-amm-とは)
- [2. インストールと起動](#2-インストールと起動)
- [3. メニュー操作](#3-メニュー操作)
- [4. システムメニュー (MDI 子)](#4-システムメニュー-mdi-子)
- [5. キーボードショートカット](#5-キーボードショートカット)
- [6. 入力パネル](#6-入力パネル)
- [7. 配置の記憶と復元](#7-配置の記憶と復元)
  - [7.6 クローズ時 git commit / push ガード](#76-クローズ時-git-commit--push-ガード)
- [8. profiles.amm スキーマ](#8-profilesamm-スキーマ)
- [9. amm-mcp.exe (MCP / CLI / REPL)](#9-amm-mcpexe-mcp--cli--repl)
- [10. Amm.PowerShell モジュール](#10-ammpowershell-モジュール)
- [11. MCP ゲートウェイ](#11-mcp-ゲートウェイ)
- [12. ファイル配置 (ユーザーごと)](#12-ファイル配置-ユーザーごと)
- [13. トラブルシュート](#13-トラブルシュート)

---

## 1. amm とは

Windows ネイティブの MDI (Multiple Document Interface) 型マルチターミナル。CMD / PowerShell / Claude Code / GitHub Copilot CLI / OpenAI Codex CLI / Gemini CLI 等を**同時に並べて、共通の入力欄から操作**する。

- **UI**: .NET 9 Windows Forms (MDI) + WebView2 + xterm.js
- **PTY**: Windows ConPTY (NuGet ラッパー非依存の P/Invoke 直書き)
- **配布**: self-contained single exe (.NET ランタイム不要)
- **MCP 連携**: GUI 内蔵の MCP JSON-RPC サーバ + 同梱 `amm-mcp.exe` で他の AI クライアント (Claude Code / Claude Desktop / Codex CLI 等) から各 MDI を駆動可能

---

## 2. インストールと起動

### 2.1 配布物

| ファイル | 役割 |
|---|---|
| `amm.exe` | GUI 本体 |
| `amm-mcp.exe` | MCP stdio サーバ / CLI / REPL |
| `profiles.amm` | 既定設定ファイル (起動時に exe 横を探す) |
| `Resources/` | アイコン / xterm.js / terminal.html |

### 2.2 起動方法

```cmd
amm.exe
```

ダブルクリックでも OK。起動時の挙動:
1. **CLI 引数で profiles ファイルを指定**していればそれを読む
2. なければ exe と同じディレクトリの **`profiles.amm`** を読む
3. ファイルが無ければ内蔵デフォルト profile (CMD のみ、`workingDirectory=""`) で起動
4. 各 profile の `autoStartCount` に従って MDI 子を自動起動
   - **起動時に Shift を押していれば自動起動を抑止**し、空のウィンドウだけ表示する (`.amm` を Shift+ダブルクリックで開いたとき等。`--start-all` 指定時も抑止。起動後にコマンドメニューから手動で開ける)。OS がプロセスを起こした直後の修飾キー状態を見るので、起動が始まるまで Shift を押し続けること

### 2.3 `.amm` ファイルの関連付け (MSI インストール時)

MSI でインストールすると `.amm` 拡張子が `amm.exe` に関連付けられる。Explorer 上で任意の `.amm` ファイルを**ダブルクリックすると、そのファイルを読み込んで `amm.exe` が起動**する (= `amm.exe "<ダブルクリックした .amm のフルパス>"` と等価)。以降の「上書き保存」「名前を付けて保存」もそのファイルを基準に動く ([§7.3](#73-ファイルに保存))。

> アンインストール時に関連付けレジストリも自動除去される。手動 publish (MSI を経由しない) 配布では関連付けは登録されないので、CLI 引数で `.amm` パスを渡すこと。

> **Shift+ダブルクリックで「読み込むだけ」**: `.amm` を Shift を押しながらダブルクリックすると、ファイルは読み込むが `autoStartCount` による自動起動と信頼確認ダイアログを行わない (空のウィンドウだけ表示)。中身を確認してからコマンドメニューで手動起動したいときに使う。

### 2.4 起動引数

```cmd
amm.exe                                  :: 既定の profiles.amm を読む
amm.exe C:\path\to\custom.amm            :: 別の AMM ファイルを読む
amm.exe --start-all                      :: autoStartCount に関係なく全 profile を起動
amm.exe .\profiles.team.amm --start-all  :: 組み合わせ
```

- 位置引数 1 つで profiles ファイルのパスを差し替え (`.amm` / `.json` どちらも可)
- `--start-all`: 全 profile を 1 インスタンスずつ起動 (autoStartCount を無視)

---

## 3. メニュー操作

メインウィンドウのメニューバーは **5 系統**:

### 3.1 ファイル(F)

| 項目 | 動作 |
|---|---|
| AMM を開く(O)... | 別の AMM ファイルを読み込み (既存 MDI 子は影響なし、Shift キー併用で autoStartCount による自動起動を抑止) |
| 上書き保存(S) | 現在の `_profiles` を読み込み元パスへ書き戻す。**まだ AMM ファイルを開いていない場合は「名前を付けて保存」と同じダイアログを表示** (保存先未確定のため。Program Files 配下への誤書込みを防ぐ) |
| 名前を付けて保存(A)... | 別パスへ書き出し + 以降の上書き保存先を切替。**初期フォルダは、AMM ファイルを開いていればそのフォルダ、未だなら「マイドキュメント」** |
| 終了(X) | 終了 (未保存変更があれば確認ダイアログ) |

### 3.2 コマンド(C)

| 項目 | 動作 |
|---|---|
| `<profile 一覧 (動的)>` | クリックで該当 profile の MDI 子を 1 個起動 |
| コマンドを追加(N)... | テンプレート 7 種 (CMD / PowerShell / Claude / Codex / Copilot / Gemini / 空) からダイアログで新 profile を作成 |
| コマンドを編集(E)... | profile 選択 → ダイアログで設定変更 |
| エディタ連携で使うエディタ(D)... | 関連付け / メモ帳 / 任意 exe からエディタを選択 (`layout.json` に保存) |

### 3.3 表示(V)

| 項目 | 動作 |
|---|---|
| `<MDI 一覧 (動的)>` | 各 MDI 子へ番号付きでジャンプ。アクティブは ✓ |
| タイル Ｚ(Z) / タイル縦(V) / タイル横(H) | **開いた順 (Ctrl+1..9 の番号順) に左上 → 右下のグリッド/列/行**で整列。z-order ではなく生成順で並ぶので、アクティブ切替後も並びが安定する。**タイル整列後は親ウィンドウのリサイズ/最大化に追従して子も再フィットする** (約0.1秒デバウンス) |
| カスケード(C) | MDI を重ねて整列 (OS 標準の z-order 順)。カスケードにするとリサイズ追従は解除 (手動配置を尊重) |
| 記憶した配置で表示(L) | 記憶 (または AMM ファイルロード時) の geometry / name を MDI 子に適用 + 不足分を起動 |
| 現在の配置を記憶(M) | 各 profile の `autoStartCount` / `windowGeometry` (位置・サイズ・表示名) を「現在開いている MDI 子」から in-memory で再構築 ([§ 配置の記憶と復元](#7-配置の記憶と復元)) |
| 記憶した配置をクリア(R) | 記憶した `autoStartCount` / `windowGeometry` を in-memory で消去 (確認ダイアログあり)。**開いているウィンドウは閉じない** (記憶メタデータのみ消す)。ファイルへは保存時に反映。消すものが無ければ disabled |
| MDI ボタンバーを表示(B) | 入力欄上のクイック切替バーの表示切替 |
| 入力欄を表示(I) | 中央の入力 TextBox の表示切替 |
| 送信先ステータスを表示(T) | 下端の送信先ラベルの表示切替 |
| MCP ゲートウェイ(&G)... | 外部 stdio MCP サーバの設定ダイアログを開く ([§11 MCP ゲートウェイ](#11-mcp-ゲートウェイ)) |

### 3.4 送信(S)

| 項目 | 動作 |
|---|---|
| Ctrl+S でプロンプト送信(S) | ON (既定) で入力欄の Ctrl+S 送信が有効。OFF にすると Ctrl+S を無視する (エディタの保存癖による誤送信防止)。Ctrl+1..9 / Ctrl+Shift+S には影響しない |
| 確定改行も一緒に送信(N) | ON (既定) で送信末尾に確定 Enter を打って submit まで行う。OFF にするとテキストだけ届け、確定 (Enter) はターミナル側で人間が行う (誤った MDI への送信対策)。入力欄からの送信 (Ctrl+S / Ctrl+1..9 / Ctrl+Shift+S / MDI ボタンの長押し送信) に適用 |
| 送信後に入力欄をクリア(L) | ON (既定) で送信後に入力欄をクリア。OFF で残す (再送・微調整向け) |
| コメント行を送信しない(C) | ON で送信時に `'` / `//` で始まる行を除去 (履歴の原文は保持) |
| エディタ連携の送信後(E) | エディタ保存 → 送信直後の動作を 対象MDIにフォーカス / 対象MDIを全画面 / フォーカスはあてない から選択 |

※ 旧「全ペイン送信モード」トグルは廃止済み。全ペイン送信は Ctrl+Shift+S の単発操作。

### 3.5 ヘルプ(H)

| 項目 | 動作 |
|---|---|
| バージョン情報(A) | バージョン / .NET ランタイム / OS を表示 |

---

## 4. システムメニュー (MDI 子)

MDI 子ウィンドウ左上のアイコンクリック / Alt+Space で開くシステムメニュー。OS 既定項目 (元のサイズに戻す / 移動 / サイズ / 最小化 / 最大化) と「閉じる」の間に **AMM ▶** サブメニューを挿入:

```
AMM ▶
  ├ 名前変更…                  : この MDI の一時表示名を変更
  ├ ─
  ├ エディタ連携               : 一時 .md ファイルをエディタで開く (保存のたびに送信)
  ├ エディタ連携ファイルパスをコピー : 連携中のファイルパスをクリップボードへ
  ├ ─
  ├ フォントサイズ ▶ (極大/大/中/小/極小) : per-MDI ランタイム上書き (保存しない)
  └ AMM 設定…                  : この MDI の profile を編集 + 書き戻し確認
```

---

## 5. キーボードショートカット

### 5.1 メインウィンドウ

| キー | 動作 |
|---|---|
| Ctrl+S | アクティブ MDI 子へ送信 (選択範囲があれば選択分、無ければ全文) |
| Ctrl+1 〜 Ctrl+9 | 番号指定の MDI 子へ送信 (アクティブ化しない、入力欄フォーカス保持) |
| Ctrl+H | 送信履歴ポップアップ (既定 最大 500 件、完全重複排除)。**アプリ終了時に `%LOCALAPPDATA%\amm\history.json` へ保存し、次回起動で復元** ([§11](#11-ファイル配置-ユーザーごと))。上限は `layout.json` の `historyMaxEntries` で変更可 |
| Ctrl+E | エディタ連携 (一時 .md をエディタで開き、保存のたびに送信) |

### 5.2 ターミナル (MDI 子) 内

| キー | 動作 |
|---|---|
| Ctrl+Shift+C / Ctrl+Insert | 選択テキストをコピー |
| Ctrl+C | 選択ありならコピー、なしなら子プロセスへ ^C 送信 (`ctrlCCopyOnSelection` で切替) |
| Ctrl+V / Shift+Insert | 貼り付け (マルチラインは確認ダイアログ) |
| Ctrl+F / F3 / Shift+F3 | 検索バー表示 / 次 / 前 |
| Ctrl + / Ctrl - / Ctrl 0 | フォントサイズ拡大 / 縮小 / 既定 (13) |
| 右クリック | 改行送信 (先頭、Enter `\r` を 1 つ送信) / プロンプト再送信 (↑ → Enter で直前履歴を再実行) / クイック送信 ▶ (`quickPrompts` 設定時) / プロンプト送信 / エディタ連携 / 名前変更… / フォントサイズ変更 ▶ / コピー / 貼り付け / 全選択 / 画面クリア |

---

## 6. 入力パネル

ウィンドウ下端の領域。3 行構成 (各行は表示メニューで個別に非表示可):

1. **クイック切替バー**: 左端に `[履歴 ▼]` `[エディタ連携 ▼]` `[整列]`、続けて各 MDI 子のボタン `[Ctrl+<n>] <状態> <表示名>` が生成順に並ぶ。ボタン数が多い場合は折り返して 2 行以上になる。
   - 左クリック (短押し): 対象 MDI をアクティブ化し、最大化 ⇄ 通常サイズをトグル
   - 長押し (約 0.6 秒): 入力欄の内容を対象 MDI に送信 (ダブルクリックと同じ送信)。長押し成立時は最大化トグルを伴わない
   - ダブルクリック: 入力欄の内容を対象 MDI に送信
   - 右クリック: 対象 MDI 向けの操作メニュー (`改行送信` (先頭) / `プロンプト再送信` / `クイック送信 ▶` (`quickPrompts` 設定時) / `プロンプト送信` / `エディタ連携` / `名前変更…` / `フォントサイズ変更 ▶` (現在値にチェック)) をカーソル位置に表示。`改行送信` は対象 MDI へ Enter (`\r`) を 1 つだけ送る (AI CLI の確定・継続入力用)。`プロンプト再送信` は ↑ → Enter を順に送り、対象 MDI の直前の入力履歴を再実行する。アクティブ MDI 切替は伴わない (MDI ルールによる意図しない最大化を避けるため)
   - 「整列」ボタン: 「記憶した配置」があればその配置を適用、無ければ開いた順のタイル縦
2. **入力 TextBox**: 全文・選択分を Ctrl+S / Ctrl+1..9 で送信
3. **送信先ステータス**: 現在の送信先 (アクティブ MDI / 全ペイン)、エディタ連携状態、ショートカット一覧

### 入力欄のキー

| キー / 操作 | 動作 |
|---|---|
| Ctrl+S | アクティブ MDI 子へ送信 (`AcceptsReturn=true` なので Enter は改行挿入) |
| Ctrl+1..9 | 番号指定の MDI 子へ送信 (アクティブ非変更) |
| ↑ / ↓ | キャレット移動 (履歴ナビは Ctrl+H に集約済み) |
| ファイル drop | 全ファイルがテキスト形式なら「内容 / パス」選択ダイアログ。非テキストは自動でパス挿入 |

### MDI 子へのファイル drop

全ファイルがテキスト形式なら「ファイル内容を送信 / 絶対パスを送信 / キャンセル」を選択 (内容連結が 1MB 超なら追加確認)。非テキストは確認なしでパス送信。

テキスト判定: `.md` `.txt` `.json` `.yaml` `.toml` `.xml` `.csv` `.ini` `.env` `.html` `.css` `.js` `.ts` `.py` `.go` `.rs` `.java` `.cs` `.sh` `.ps1` `.bat` `.sql` `.tex` 等の幅広い言語 / 設定ファイル + 拡張子なし (`Makefile` / `Dockerfile` / `LICENSE`) + ドット始まり (`.gitignore` / `.env`)。詳細は `src/apps/Amm/Core/FileDropHelper.cs`。

### 入力待ち検出

xterm.js 出力を監視し、profile の `waitPatterns` と 500ms 無出力タイムアウトで判定。クイック切替バーのボタン色と MDI タイトルが遷移:

> **より確実な検知 (推奨)**: Claude Code / Codex / Copilot CLI はフック登録 (§9.2)
> すると、応答完了を CLI 自身が amm へ通知するため、正規表現に頼らずに ● 遷移が
> 確定する。`waitPatterns` はフック未登録の CLI / 素のシェル用にそのまま機能する。

| 状態 | タイトル | ボタン色 |
|---|---|---|
| 実行中 | `⚙ name` | グレー |
| 入力待ち | `● name` | 薄黄 (アクティブは薄青優先) |
| **許可・確認待ち** | `⚠ name` | **オレンジ** (薄黄より優先) |
| 不明 | `? name` | 薄黄 |
| 停止 | `✗ name (exited)` | 薄赤 |

**許可・確認待ち (⚠)**: フック登録済みの CLI がツール実行の許可や追加情報を
求めている状態。amm が背面にあるときは**タスクバーが点滅**して知らせる
(amm を前面にすると点滅は止まる)。⚠ のペインをクリックして開くと表示は
● に戻る。

**許可要求ポップアップ (集約回答)**: フック登録済みの Claude Code / Copilot CLI が
ツール実行の許可を求めると、画面右下に常に手前のポップアップが出て、ペインに
行かずに [許可] / [拒否] で回答できる (Codex はブロッキング型 hook を持たないため
⚠ 表示までの対応)。

- 複数ペインから同時に要求が来ても 1 枚に「1/N 件」で順に表示
- **無回答のまま 45 秒経つ / [閉じる] / 対象ペインを開く** — いずれもポップアップが
  引っ込み、従来どおり**ペイン内に許可プロンプトが出る** (離席していても安全)
- ポップアップはフォーカスを奪わない (他アプリでタイピング中に出ても誤入力しない)。
  出現直後 0.5 秒はボタンが無効 (誤クリック防止)
- 回答した内容は下部ステータスバーに数秒表示され、ログにも記録される
- [表示] → [許可要求ポップアップ] でいつでも OFF にできる (OFF = 全部ペイン内で回答)。
  フック登録の解除は不要で即時に切り替わる

---

## 7. 配置の記憶と復元

MDI 子のレイアウト管理は **記憶 → 表示 → 保存** の 3 段階で完結する (不要になったら **クリア** で消せる):

### 7.1 現在の配置を記憶 (表示メニュー)
- 各 profile の `autoStartCount` を現在の生存 MDI 数に、`windowGeometry` を各インスタンスの位置・サイズ + 表示名 (`name`) + 作業ディレクトリ (`workingDirectory`) に in-memory で更新
- 最大化中の子で `CustomDisplayName` または cwd 上書きが付いていれば `W=H=0` の name/cwd-only エントリとして残す
- 復元時 (`記憶した配置で表示` または `autoStartCount` での自動起動) は `workingDirectory` が入っているエントリの起動については `selectWorkingDirOnStart` のフォルダ選択ダイアログをスキップして直接適用する (= 記憶した cwd でそのまま起動)
- 派生 clone (`promptNewNameOnCommandAdd` 経由でユーザが追加した profile) も対象。次回起動でも保持したいなら [現在の配置を記憶] → [ファイル → 上書き保存] を併用する (clone 自体は in-memory のため保存しないと失われる)
- **ファイル書き出しはしない** (アプリ内の一時保持)
- 即時実行、確認ダイアログなし

### 7.2 記憶した配置で表示 (表示メニュー)
- 各 profile について `target = max(autoStartCount, windowGeometry 件数)` を計算
- 既存 MDI 子 (生成順、target 件目まで) の位置を `windowGeometry[i]` に合わせて移動、`name` があれば表示名も復元
- 不足分 (`target - alive`) は `OpenTerminal` で新規起動 (起動時にも `name` が適用される)
- **target を超える生存子はそのまま残す** (記憶対象外として保護)
- profile.AutoStartCount = 0 かつ windowGeometry が空ならメニュー項目は disabled

### 7.3 記憶した配置をクリア (表示メニュー)
- 全 profile の `autoStartCount` を 0、`windowGeometry` を空に in-memory でリセット (「現在の配置を記憶」の逆操作)
- **開いている MDI 子は閉じない** — 記憶メタデータのみ消去するので、「記憶した配置で表示」が disabled になり、以後の保存で配置情報を書き出さない状態に戻る
- 破壊的操作のため Yes/No 確認ダイアログを表示。消すもの (autoStartCount>0 または windowGeometry) が無ければメニュー項目は disabled
- **ファイル書き出しはしない** — クリア結果を残すには [ファイル → 上書き保存] を併用する

### 7.4 ファイルに保存
- 「ファイル → 上書き保存(S)」: 現在の `_profiles` を読み込み元 AMM ファイルへ書き出す。まだ AMM ファイルを開いていない (起動時に exe 横の `profiles.amm` を自動検出しただけの) 状態では「名前を付けて保存」と同じダイアログを表示する
- 「ファイル → 名前を付けて保存(A)」: 別パスへ書き出し + 以降の上書き保存先を切替。初期フォルダは、AMM ファイルを開いていればそのフォルダ、未だなら「マイドキュメント」
- 保存しないとアプリ終了で記憶した配置は失われる (in-memory のみ)

### 7.5 終了時の未保存確認
- アプリ終了時に `_profiles` が直近の保存 / ロード時点から変化していると確認ダイアログ
- 変更内容を profile 単位で要約 (自動起動数 / ウィンドウ位置 / 名前 / その他設定)
- `[はい]` で保存、`[いいえ]` で破棄、`[キャンセル]` で終了中止

### 7.6 クローズ時 git commit / push ガード
MDI 子ウィンドウを閉じる際、またはアプリを終了する際に、起動ディレクトリが git リポジトリかつ変更がある場合は自動でダイアログを表示して commit / push を促す。

#### 個別ウィンドウのクローズ
1. ウィンドウの起動ディレクトリが git リポジトリかどうか確認
2. 未コミットの変更があれば **変更をコミット** ダイアログ表示:
   - 変更ファイル一覧 + コミットメッセージ入力欄 (既定: `WIP: 作業を保存`)
   - `[コミット]` — `git add -A && git commit -m <メッセージ>` を実行
   - `[スキップ]` — コミットせずクローズを続行
   - `[閉じない]` — クローズをキャンセル
3. リモートが設定済みかつ未プッシュのコミットがあれば **未プッシュ確認** ダイアログ:
   - `[はい]` — `git push` を実行
   - `[いいえ]` — プッシュせずクローズを続行
   - `[キャンセル]` — クローズをキャンセル

#### アプリ終了時
全子ウィンドウの起動ディレクトリを **リポジトリ単位で集約** し、同じリポジトリを参照する複数ウィンドウがあっても 1 回だけ確認する。

> **注意**: git がインストールされていない、またはリポジトリ外のディレクトリには何も表示されない。

---

## 8. profiles.amm スキーマ

`profiles.amm` は JSON 形式で、トップレベルに `profiles` 配列を持つ。`profiles.json` でも同じスキーマで読み書き可能 (拡張子のみ違い)。

```jsonc
{
  "profiles": [
    {
      "name": "Claude Code",
      "executable": "claude.exe",
      "args": [],
      "workingDirectory": "",
      "newlineMode": "LF",
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
      "collapseBlankLines": true,
      "commentPrefixes": ["'", "//"],
      "windowGeometry": [
        { "index": 1, "x": 0,   "y": 0, "w": 800, "h": 600, "name": "main" },
        { "index": 2, "x": 800, "y": 0, "w": 800, "h": 600 },
        { "index": 3, "name": "max-view", "maximized": true }
      ],
      "nickname": "claude",
      "sendLineByLine": false,
      "useBracketedPaste": true,
      "selectWorkingDirOnStart": false
    }
  ]
}
```

### 8.1 フィールド一覧

| フィールド | 型 | 既定 | 説明 |
|---|---|---|---|
| `name` | string | `""` | メニュー / タイトルバー表示名 |
| `executable` | string | `"cmd.exe"` | 実行ファイル。環境変数展開可。`.cmd`/`.bat` は `cmd.exe /c path.cmd` 形式で |
| `args` | string[] | `[]` | 起動引数 (環境変数展開可) |
| `workingDirectory` | string? | `""` | プロセス起動時の CWD。環境変数展開可。`""` / 未指定でアプリ起動時のカレントフォルダ |
| `newlineMode` | `"CRLF"` \| `"LF"` | `"CRLF"` | 一括送信時の改行変換 |
| `outputEncoding` | `"UTF-8"` \| `"Shift_JIS"` | `"UTF-8"` | ConPTY 出力エンコーディング |
| `autoChcp` | bool | `true` | 起動直後に `chcp 65001\r\n` を自動送信 |
| `waitPatterns` | string[] | `[]` | 入力待ち判定の正規表現 (空なら共通デフォルト) |
| `initialCommands` | string[] | `[]` | ConPTY 起動直後に順次送信するコマンド列 |
| `ctrlCCopyOnSelection` | bool | `true` | Ctrl+C を選択時はコピー、なしなら ^C 送信 |
| `sessionLog` | bool | `false` | `%LOCALAPPDATA%\amm\sessions\YYYYMMDD-HHMMSS-<name>.log` に ANSI 除去済みプレーンテキストを追記。プロンプト・応答・貼り付け文字列も平文で残るため共有端末や機微データでは無効推奨 |
| `theme` | object? | `null` | xterm.js の theme オプション (`background` / `foreground` / `cursor` 等) |
| `closeOnExit` | bool | `true` | 子プロセス終了時に MDI も自動クローズ。`false` で `✗ name (exited)` のまま残す |
| `autoStartCount` | int | `0` | アプリ起動時にこの profile を何個自動起動するか |
| `closeProhibited` | bool | `false` | `true` で × / Ctrl+W / 「閉じる」を無効化 (常駐 AI エージェント向け、Shift+× で強制終了可) |
| `collapseBlankLines` | bool | `true` | マルチライン送信時、連続空行を 1 行に縮約 |
| `commentPrefixes` | string[] | `["'", "//"]` | 行頭がこの接頭辞で始まる行を送信時にスキップ。インデント付きコメントは対象外。旧既定の `"#"` は Markdown 見出し (`##` 等) が送信から抜け落ちるため除外 — 旧既定そのまま (`["'", "//", "#"]`) の AMM ファイルは Load 時に `"#"` 抜きへ自動移行、それ以外の `"#"` 入り構成は保持 |
| `windowGeometry` | array | `[]` | 起動 N 個目の MDI の位置・サイズ・表示名・最大化状態・作業ディレクトリ。エントリ無しの index は最大化フォールバック (旧挙動)。各エントリは `{ index, x, y, w, h, name?, maximized?, workingDirectory? }` (index は 1 始まり、座標は MDI クライアント領域相対 px、`name` は省略可で右クリック「名前変更…」で設定した一時表示名、`maximized` は記憶時に最大化中だったかを示すフラグ、`workingDirectory` はその MDI で使われていた cwd 上書き値 — 値が入ったエントリの復元時は `selectWorkingDirOnStart` のフォルダ選択ダイアログをスキップする。いずれも [現在の配置を記憶] で取り込んで永続化) |
| `nickname` | string? | `null` | MCP 受信時の宛先名。未設定なら MCP に登録されない |
| `sendLineByLine` | bool | `false` | マルチラインを 1 行ずつ Enter 区切りで打つ |
| `useBracketedPaste` | bool | `false` | 入力を bracketed paste mode (`\x1b[200~ … \x1b[201~`) で囲んで送る (Copilot CLI 等の Ink ベース TUI 向け) |
| `selectWorkingDirOnStart` | bool | `false` | 起動時にフォルダ選択ダイアログを表示 (選択値はそのインスタンスのみ)。`windowGeometry[i].workingDirectory` が指定されているエントリの復元時はスキップ |
| `promptNewNameOnCommandAdd` | bool | `false` | チェックボックス UI 名称: 「コマンド追加時に新しい名前を入力する」。**コマンドメニューからの手動コマンド追加時のみ** 発動。`selectWorkingDirOnStart` と併設のときはフォルダ選択 → 名前ダイアログの順で出し、入力された名前 (cwd 選択時は初期値がフォルダ名) で profile を clone して `_profiles` に追加 (= テンプレ → ユーザ固有コマンドの派生; clone は `nickname=null`)。AMM ファイル永続化は [上書き保存] / [名前を付けて保存] が担う。自動起動経路 (`autoStartCount` / `--all` / `記憶した配置で表示`) では発動しない。旧フィールド名 `promptRenameOnStart` は Load 時に自動移行 |
| `quickPrompts` | array | `[]` | ターミナル本体および MDI 切替ボタンの右クリックメニュー「クイック送信 ▶」に並べる定型プロンプト。各要素 `{ label, prompt }`。0 件ならサブメニュー非表示 |
| `fontSize` | int? | `null` | xterm.js のフォントサイズ (px) 既定値。`null` で terminal.html 既定 (13)。コマンド追加・編集ダイアログの ComboBox から選択 |

---

## 9. amm-mcp.exe (MCP / CLI / REPL)

GUI 起動中はログオンユーザー専用の **Named Pipe** で MCP JSON-RPC サーバが常駐し、`amm-mcp.exe` が同パイプに接続する。5 つの動作モードを 1 つの exe にまとめている:

| モード | 起動方法 | 用途 |
|---|---|---|
| **stdio bridge** | `amm-mcp.exe`<br>(stdin redirect) または `amm-mcp.exe --bridge` | MCP クライアント (Claude Code / Claude Desktop 等) と amm GUI の双方向リレー |
| **REPL** | `amm-mcp.exe` (端末から引数なし起動) | 対話モード。`list` / `send` / `peek` / `help` / `quit` を受け付ける |
| **send (CLI)** | `amm-mcp.exe send <nickname> [msg]` | シェルから 1 ショット送信。`msg` 省略で stdin を全部読む |
| **list (CLI)** | `amm-mcp.exe list` | 参加者 (nickname を持つ MDI 子) を JSON 配列で stdout 出力 |
| **notify (hook 用)** | `amm-mcp.exe notify` (CLI の hook から自動起動) | CLI の応答完了等を amm へ通知し、入力待ち表示 (●) をイベント駆動で確定する。手動実行は不要 (§9.2 のフック登録で自動設定) |

### 9.1 公開ツール (MCP `tools/list`)

| ツール | 引数 | 動作 |
|---|---|---|
| `send_message` | `recipient?` `mode?` `message` | nickname 宛にテキスト注入。`recipient` 省略でブロードキャスト。`mode=first` (既定) は入力待ち優先 → 起動順 fallback、`mode=all` は同 nickname 全 MDI。入力待ちでなければ nickname 別キュー (1 nickname 100 件・古い順 drop) に積み、入力待ち遷移時に自動 flush |
| `list_participants` | (なし) | 各 MDI の `nickname` / `profile` / `instance` / `state` を返す |
| `peek_queue` | `recipient?` | 配信待ちキューの中身を覗き見 (デキューしない) |
| `mdi/open` | `command` `args?` `title?` `working_directory?` | MDI ターミナルを新規起動し `session_id` (GUID) を返す。`session_id` は `mdi/close` / `mdi/wait_state` に渡す |
| `mdi/close` | `session_id` `force?` | `session_id` で指定した MDI を閉じる。`force=true` で `closeProhibited` を無視 |
| `mdi/wait_state` | `session_id` `target_state` `timeout_ms?` | 指定セッションが `target_state` (現在 `"idle"` のみ) になるまでブロック。`elapsed_ms` と到達 `state` を返す。タイムアウト時は `state="timeout"` |

### 9.2 CLI(Claude Code等) への登録

**推奨: GUI から一括登録** — [コマンド] → [CLI への MCP / フック登録...] で、
Claude Code / Codex / Copilot CLI のユーザー (端末) スコープ設定ファイルへ
チェックボックスで登録 / 削除できる。書き込み先と形式:

| 項目 | CLI | ファイル | 形式 |
|---|---|---|---|
| MCP | Claude Code | `~/.claude.json` | ルート `mcpServers.amm` (`type: stdio`) |
| MCP | Codex | `~/.codex/config.toml` | `[mcp_servers.amm]` セクション |
| MCP | Copilot CLI | `~/.copilot/mcp-config.json` | `mcpServers.amm` (`type: local`, `tools: ["*"]`) |
| フック | Claude Code | `~/.claude/settings.json` | `hooks.Stop` / `hooks.Notification` に `amm-mcp.exe notify`、`hooks.PermissionRequest` に `amm-mcp.exe approve` |
| フック | Codex | `~/.codex/config.toml` | ルート `notify` キーに `amm-mcp.exe notify --source codex` + `[tui]` の `notifications` (OSC9 端末通知) |
| フック | Copilot CLI | `~/.copilot/hooks/amm-hooks.json` | `agentStop` に `notify --state idle`、`permissionRequest` に `approve --source copilot` (amm 専有ファイル) |

既存の設定内容は保全し、`amm` エントリのみ追加 / 削除する。登録済みでも
`command` のパスが現在の `amm-mcp.exe` と異なる場合は適用時に更新される。
CLI 起動中に変更した場合は次回起動から有効。

**フック (入力待ち通知) とは**: CLI が応答を終えた瞬間に hook が
`amm-mcp.exe notify` を起動し、amm の入力待ち表示 (●) を正規表現に頼らず
確定させる。amm が起動した CLI だけが対象 (環境変数で識別) なので、
amm 外で普通に使う CLI には影響しない。MCP とは独立に機能する (nickname 不要)。

CLI ごとの対応範囲:

| CLI | 入力待ち (●) | 許可待ち (⚠) | ポップアップ回答 |
|---|---|---|---|
| Claude Code | ✅ Stop hook | ✅ Notification hook | ✅ PermissionRequest hook |
| Codex | ✅ notify (agent-turn-complete) | ✅ OSC9 端末通知 (approval-requested) | ❌ (ブロッキング型 hook なし) |
| Copilot CLI | ✅ agentStop hook | ✅ permissionRequest hook | ✅ permissionRequest hook |

注意 (Codex): `notify` は config.toml の単一キーのため、既に他プログラムを設定
している場合は登録できない (明示エラー。手動での切替が必要)。`[tui]` の
`notifications` も、ユーザー自身の設定がある場合は触らない。amm が追記した行は
`# added by amm` マーカー付きで、解除時にその行だけ取り除かれる。

フックは `if exist` 付きで登録されるため、**amm をアンインストールしても
解除し忘れによるエラーは出ない** (exe が無ければ静かに何もしない)。残った
エントリが気になる場合は、再インストール後にこのダイアログから解除できる。

手動で登録する場合:

```cmd
claude mcp add amm -- "C:\Program Files\amm\amm-mcp.exe"
codex mcp add amm -- "C:\Program Files\amm\amm-mcp.exe"
```

または `~/.claude.json` / プロジェクトの `.mcp.json`:

```jsonc
{
  "mcpServers": {
    "amm": {
      "command": "C:\\Program Files\\amm\\amm-mcp.exe"
    }
  }
}
```

登録後、Claude Code 内から `/mcp` で接続状態を確認できる。GUI 未起動時は 5 秒で接続タイムアウトし exit code 2。

### 9.3 Claude Desktop / 他の MCP クライアント

MCP 公式仕様 (`initialize` / `tools/list` / `tools/call`、protocol `2024-11-05`) 準拠なので、stdio MCP に対応するクライアントなら共通で動く:

```jsonc
{
  "mcpServers": {
    "amm": {
      "command": "C:\\Program Files\\amm\\amm-mcp.exe",
      "args": ["--connect-timeout", "10000"]
    }
  }
}
```

### 9.4 profiles.amm 側の準備

MCP で操作したい profile に **`nickname`** を付ける (未設定なら `list_participants` にも出ない)。同名 nickname を複数 MDI に付けても良い (`mode=first/all` で挙動切替):

```jsonc
{
  "profiles": [
    { "name": "Claude Code", "executable": "claude.exe",  "nickname": "claude" },
    { "name": "Copilot CLI", "executable": "copilot.cmd", "nickname": "copilot" },
    { "name": "Codex CLI",   "executable": "codex.cmd",   "nickname": "codex" },
    { "name": "Gemini CLI",  "executable": "gemini.cmd",  "nickname": "gemini" }
  ]
}
```

### 9.5 CLI 使い方

```cmd
:: 参加者一覧 (JSON 配列)
amm-mcp.exe list

:: 入力待ちの "claude" MDI に 1 行送る
amm-mcp.exe send claude "ls -la"

:: stdin から流し込む
type prompt.md | amm-mcp.exe send claude
git diff | amm-mcp.exe send codex --mode all

:: 同 nickname の全インスタンスに送る
amm-mcp.exe send claude --all "全 claude MDI に通知"

:: nickname 登録済みの全 MDI へ送る
amm-mcp.exe send --broadcast "session start"

:: 対話 REPL
amm-mcp.exe
```

### 9.6 共通オプション

| オプション | 既定 | 意味 |
|---|---|---|
| `--pipe-name <name>` | `amm-mcp-{ユーザ名}` | パイプ名を上書き |
| `--connect-timeout <ms>` | `5000` | GUI への接続タイムアウト (`0` で無制限) |
| 環境変数 `AMM_MCP_PIPE_NAME` | (未設定) | `--pipe-name` の代替 (引数優先) |

### 9.7 終了コード

| code | 意味 |
|---|---|
| 0 | 成功 |
| 1 | 引数不正 |
| 2 | GUI 未起動 (パイプ接続タイムアウト) |
| 3 | パイプ IO エラー |
| 4 | MCP プロトコル / サーバ側エラー |

### 9.8 動作モデル

```
┌──────────────────┐  stdio    ┌──────────────┐  Named Pipe   ┌─────────────┐
│ Claude Code /    │◀─────────▶│ amm-mcp.exe  │◀─────────────▶│  amm.exe    │
│ Claude Desktop / │  JSON-RPC │  (bridge)    │  JSON-RPC     │ (MCP server)│
│ Codex CLI 等     │           └──────────────┘               │  ┌─────────┐│
└──────────────────┘                                          │  │MDI[0..n]││
                                                              │  └─────────┘│
シェル / スクリプト ─▶ amm-mcp.exe send/list ─▶ 同パイプ ──▶ └─────────────┘
```

- パイプ ACL は current user の SID に明示付与 (同一ログオンユーザーのプロセスのみ接続可)
- GUI 側はメモリ上のキューのみ管理。GUI 終了でキューも消える (永続化なし)
- 入力待ち未到達でも即時送信したい場合は profile 側の `waitPatterns` を整備する

---

## 10. Amm.PowerShell モジュール

`Amm.PowerShell.dll` は PowerShell 7.4 以降向けのバイナリモジュール。
amm GUI の MDI ウィンドウを PowerShell スクリプトから直接制御でき、
**オートパイロット** (AI エージェントへの指示投入 → 完了待ち → 次指示) を
シンプルなパイプラインで記述できる。

### 10.1 インポート

```powershell
Import-Module "C:\Program Files\amm\Amm.PowerShell.dll"
```

常に読み込む場合は `$PROFILE` に追記する。モジュールが読み込まれると以下の
Cmdlet が利用可能になる:

| Cmdlet | 動詞 | 用途 |
|---|---|---|
| `Connect-Amm` | Connect | 接続確認 (起動確認のみ。各 Cmdlet は暗黙的に自動接続) |
| `Disconnect-Amm` | Disconnect | no-op (将来の永続接続モード用予約) |
| `Open-AmmWindow` | Open | MDI ターミナルを新規起動、`AmmSession` を返す |
| `Close-AmmWindow` | Close | MDI を閉じる。パイプラインで `Open-AmmWindow` / `Get-AmmSession` を受け取れる |
| `Get-AmmSession` | Get | 現在起動中の MDI 一覧を `AmmSession[]` で返す |
| `Send-AmmMessage` | Send | MDI にテキストを送信。パイプラインで `Get-AmmSession` を受け取れる |
| `Wait-AmmIdle` | Wait | 指定セッションが入力待ちになるまでブロック、`WaitResult` を返す |

### 10.2 共通パラメータ

全 Cmdlet に以下のパラメータが存在する (既定値は下表通り):

| パラメータ | 型 | 既定 | 説明 |
|---|---|---|---|
| `-PipeName` | string | `amm-mcp-{ユーザ名}` | 接続先 Named Pipe 名を上書き。環境変数 `AMM_MCP_PIPE_NAME` でも指定可 |
| `-ConnectTimeoutMs` | int | `5000` | GUI への接続タイムアウト (ms) |

### 10.3 Connect-Amm

```powershell
Connect-Amm [-PipeName <string>] [-ConnectTimeoutMs <int>]
```

amm GUI への接続を確認する。接続できない場合は終端エラー (`ErrorCategory.ConnectionError`)。
各 Cmdlet は自動接続するため事前実行は任意。スクリプト冒頭での起動確認に使う。

```powershell
Connect-Amm -Verbose
# VERBOSE: amm に接続しました (pipe=amm-mcp-MYUSER)。
```

### 10.4 Open-AmmWindow

```powershell
# ByCommand: コマンドを直接指定 (一時プロファイル)
Open-AmmWindow -Command <string> [-Args <string[]>] [-Title <string>]
               [-WorkingDirectory <string>] [-PipeName <string>] [-ConnectTimeoutMs <int>]

# ByProfile: profiles.amm の既存プロファイルを名前で起動 (設定を自動継承)
Open-AmmWindow -ProfileName <string> [-Title <string>]
               [-WorkingDirectory <string>] [-PipeName <string>] [-ConnectTimeoutMs <int>]
```

`AmmSession { SessionId; Title }` オブジェクトを返す。`SessionId` は GUID 文字列で
`Close-AmmWindow` / `Wait-AmmIdle` に渡す識別子になる。

| パラメータ | セット | 必須 | 説明 |
|---|---|---|---|
| `-Command` | ByCommand | ✅ | 実行ファイル名またはパス (`claude`, `powershell`, etc.) |
| `-ProfileName` | ByProfile | ✅ | `profiles.amm` 上のプロファイル名 (大小文字不問)。`nickname` / `waitPatterns` 等の設定を継承 |
| `-Args` | ByCommand | | 起動引数 (配列) |
| `-Title` | 両方 | | MDI タイトル表示名の上書き |
| `-WorkingDirectory` | 両方 | | 起動時の CWD。ByProfile では省略するとプロファイル設定値を使う |

```powershell
# ByCommand: コマンド直接指定 (workingDirectory は必須ではないが省略すると amm の CWD を継承)
$s = Open-AmmWindow -Command claude -Title "Agent-1" -WorkingDirectory C:\projects\myapp

# ByProfile: profiles.amm の "Claude Code" を起動 — nickname / waitPatterns をそのまま引き継ぐ
$s = Open-AmmWindow -ProfileName "Claude Code"

# ByProfile + WorkingDirectory 上書き (プロファイルの他の設定は維持)
$s = Open-AmmWindow -ProfileName "Claude Code" -WorkingDirectory C:\projects\myapp

$s.SessionId  # => "d4f7a2b1-..."
```

### 10.5 Close-AmmWindow

```powershell
Close-AmmWindow [-SessionId] <string> [-Force] [-WhatIf] [-Confirm]
                [-PipeName <string>] [-ConnectTimeoutMs <int>]
```

`-SessionId` は `ValueFromPipeline` / `ValueFromPipelineByPropertyName` 対応。
`-Force` を付けると profile の `closeProhibited` を無視して強制終了する。

```powershell
# Open-AmmWindow の戻り値をそのまま閉じる
$s | Close-AmmWindow

# 特定セッション ID を指定
Close-AmmWindow -SessionId "d4f7a2b1-..." -Force

# タイトルに "tmp-" を含む全セッションを閉じる
Get-AmmSession | Where-Object Title -like "tmp-*" | Close-AmmWindow
```

> **注意**: `Get-AmmSession` が返す `AmmSession` の `SessionId` は現状空文字列のため、
> `Close-AmmWindow` にパイプする場合は `Open-AmmWindow` の戻り値を使うこと。

### 10.6 Get-AmmSession

```powershell
Get-AmmSession [-PipeName <string>] [-ConnectTimeoutMs <int>]
```

起動中の MDI を `AmmSession` の配列で返す。`Title` は `nickname (instance)` 形式。
`SessionId` は現在空文字列 (実装制限: `list_participants` が session_id を返さないため)。

```powershell
Get-AmmSession | Format-Table Title
# Title
# -----
# claude
# codex (2)
# powershell
```

### 10.7 Send-AmmMessage

```powershell
Send-AmmMessage [-Nickname] <string> [-Message] <string> [-Mode <string>]
                [-PipeName <string>] [-ConnectTimeoutMs <int>]
```

`-Nickname` は `[Alias("Title")]` 付きで `ValueFromPipeline` / `ValueFromPipelineByPropertyName` 対応。
`Get-AmmSession` の戻り値を直接パイプできる。

| パラメータ | 必須 | 既定 | 説明 |
|---|---|---|---|
| `-Nickname` | ✅ | | 宛先の nickname (MDI タイトル) |
| `-Message` | ✅ | | 送信するテキスト |
| `-Mode` | | `"first"` | `"first"` で入力待ち優先、`"all"` で同 nickname 全 MDI |

```powershell
# nickname を直接指定
Send-AmmMessage -Nickname claude -Message "次のタスクを実行してください"

# Get-AmmSession からパイプ (Title が Nickname の Alias)
Get-AmmSession | Where-Object Title -eq "claude" | Send-AmmMessage -Message "status?"
```

### 10.8 Wait-AmmIdle

```powershell
Wait-AmmIdle [-SessionId] <string> [-TargetState <string>] [-TimeoutMs <int>]
             [-PipeName <string>] [-ConnectTimeoutMs <int>]
```

指定セッションが `TargetState` に到達するまでパイプを保持してブロックする (サーバ側待機)。
`WaitResult { State; ElapsedMs }` を返す。

| パラメータ | 既定 | 説明 |
|---|---|---|
| `-SessionId` | (必須) | `Open-AmmWindow` が返した `SessionId` |
| `-TargetState` | `"idle"` | 待機する状態。現在は `"idle"` のみ対応 |
| `-TimeoutMs` | `300000` | サーバ側タイムアウト (ms)。超過で `State="timeout"` が返る |

`-SessionId` は `ValueFromPipeline` / `ValueFromPipelineByPropertyName` 対応なので
`Open-AmmWindow | Wait-AmmIdle` と繋げられる。

```powershell
$r = $s | Wait-AmmIdle -TimeoutMs 120000
$r.State     # "idle" または "timeout"
$r.ElapsedMs # 経過時間 (ms)
```

### 10.9 オートパイロット例

#### 基本パターン: 1 タスク待機

```powershell
Import-Module "C:\Program Files\amm\Amm.PowerShell.dll"

# MDI を開く → idle 待ち → 指示送信 → 完了待ち → 閉じる
$s = Open-AmmWindow -Command claude -Title "Agent-1" -WorkingDirectory C:\projects\myapp
$s | Wait-AmmIdle                                         # 起動後の最初の入力待ちまで待つ
Send-AmmMessage -Nickname "Agent-1" -Message (Get-Content task.md -Raw)
$r = $s | Wait-AmmIdle -TimeoutMs 600000                  # 最大 10 分待つ
Write-Host "完了: state=$($r.State) elapsed=$($r.ElapsedMs)ms"
$s | Close-AmmWindow
```

#### 並列パターン: 複数 MDI へ同時配布

```powershell
$agents = 1..3 | ForEach-Object {
    Open-AmmWindow -Command claude -Title "Agent-$_"
}

# 全エージェントが起動するまで並列待機
$agents | ForEach-Object -Parallel {
    $_ | Wait-AmmIdle -TimeoutMs 30000
} -ThrottleLimit 3

# 個別タスクを投入
$agents | ForEach-Object -Parallel {
    $n = $_.Title
    Send-AmmMessage -Nickname $n -Message (Get-Content "tasks\$n.md" -Raw)
    $_ | Wait-AmmIdle -TimeoutMs 600000
} -ThrottleLimit 3

$agents | Close-AmmWindow
```

#### 既存セッションへの送信

```powershell
# 起動中の "claude" MDI を検索して送信 (Get-AmmSession は SessionId が空のため Wait-AmmIdle 不可)
Get-AmmSession | Where-Object Title -eq "claude" | Send-AmmMessage -Message "status check"
```

### 10.10 トラブルシュート

| 症状 | 対処 |
|---|---|
| `GetAmmSessionFailed` / `ConnectTimeoutMs` 超過 | `amm.exe` が起動しているか確認。別ユーザーとして起動している場合は `-PipeName` で合わせる |
| `Wait-AmmIdle` が `State="timeout"` で即返る | フック未登録かつ `waitPatterns` が未設定。§9.2 でフック登録するか、profile に `waitPatterns` を追記する |
| `Close-AmmWindow: session not found` | `Get-AmmSession` からパイプした場合 (`SessionId` が空)。`Open-AmmWindow` の戻り値を使うこと |
| `Send-AmmMessage` で `delivered_count=0` | 宛先 MDI が入力待ちでなく、キューに積まれた状態。入力待ちになると自動 flush される |

---

## 11. MCP ゲートウェイ

amm は外部の **stdio MCP サーバ**を子プロセスとして管理し、そのツールを集約して自身の MCP サーバ経由で公開する **MCP ゲートウェイ**機能を持つ。Claude Code や Amm.PowerShell などの MCP クライアントは、既存の `send_message` / `mdi/open` 等のツールに加えて、ゲートウェイ経由で外部サーバのツールを `<サーバ名>/<ツール名>` の形式で呼び出せる。

### 11.1 設定方法 — GUI

`amm.exe` メニューの **「表示 → MCP ゲートウェイ...」** を開くと設定ダイアログが表示される。

```
┌─ MCP ゲートウェイ設定 ────────────────────────────────────────────────┐
│ ┌─ AMM 共通  (全ワークスペース共通) ─────────────────────────────── ┐ │
│ │  ✓  [fs]     npx -y @mcp/server-filesystem C:\work  (3 ツール)  │ │
│ │  ⏳  [fetch]  uvx mcp-server-fetch                               │ │
│ └──────────────────────────────────────────────────────────────────┘ │
│                       [追加...]  [編集...]  [削除]  [↑]  [↓]        │
│                                                                       │
│ ┌─ このファイル固有  (現在の profiles.amm にのみ適用) ──────────── ┐ │
│ │  ✗  [local]  node ./local-server.js                              │ │
│ └──────────────────────────────────────────────────────────────────┘ │
│                       [追加...]  [編集...]  [削除]  [↑]  [↓]        │
│                                              [OK]  [キャンセル]      │
└───────────────────────────────────────────────────────────────────────┘
```

- **AMM 共通**: `%LOCALAPPDATA%\amm\mcp-servers.json` に保存。全ワークスペースで読み込まれる
- **このファイル固有**: 現在の `profiles.amm` の `mcpServers` に保存

OK を押すと変更が保存され、ゲートウェイが即時ホットリロードされる (amm.exe の再起動不要)。

### 11.2 設定方法 — ファイル直接編集

GUI を使わず JSON で直接書くことも可能。

**AMM 共通** (`%LOCALAPPDATA%\amm\mcp-servers.json`):

```jsonc
{
  "mcpServers": [
    {
      "name": "fs",
      "command": "npx",
      "args": ["-y", "@modelcontextprotocol/server-filesystem", "C:\\Users\\user\\work"],
      "autoStart": true,
      "maxRestarts": 3
    }
  ]
}
```

**ファイル固有** (`profiles.amm` のトップレベルに追記):

```jsonc
{
  "profiles": [ ... ],
  "mcpServers": [
    {
      "name": "fetch",
      "command": "uvx",
      "args": ["mcp-server-fetch"],
      "env": { "HTTP_PROXY": "http://proxy.example.com:8080" }
    }
  ]
}
```

ファイルを直接編集した場合は amm.exe を再起動して設定を反映する。

### 11.3 mcpServers フィールド一覧

| フィールド | 型 | 既定 | 説明 |
|---|---|---|---|
| `name` | string | `""` | サーバ識別名。ツールプレフィックスに使用 (`"fs"` → `"fs/read_file"`) |
| `command` | string | `""` | 実行コマンド (`"npx"`, `"node"`, `"python"` 等) |
| `args` | string[] | `[]` | コマンド引数 |
| `env` | object? | `null` | 追加/上書き環境変数。`null` で親プロセスから継承のみ |
| `autoStart` | bool | `true` | amm 起動時に自動起動する |
| `maxRestarts` | int | `3` | クラッシュ後の最大再起動回数。`0` で再起動なし |

### 11.4 ツール呼び出し

MCP クライアント (Claude Code / Amm.PowerShell 等) から `tools/list` を呼ぶと、amm 組み込みツールの後にゲートウェイツールが `[サーバ名]` プレフィックス付き説明と共に列挙される。`tools/call` には `"{name}/{toolName}"` 形式のツール名を渡す。

```jsonc
// Claude Desktop / claude_desktop_config.json
{
  "mcpServers": {
    "amm": {
      "command": "C:\\amm\\amm-mcp.exe"
    }
  }
}
```

Claude Code がこの設定で amm に接続すると、例えば `fs/read_file`, `fs/write_file` 等が直接利用可能になる。

### 11.5 ステータスアイコン

| アイコン | 意味 |
|---|---|
| ✓ 実行中 (N ツール) | サーバ起動済み、N 個のツールを公開中 |
| ⏳ 起動中 | `initialize` / `tools/list` 待ち |
| ✗ エラー | 起動失敗または最大再起動回数超過 |
| ○ 停止 | `autoStart: false` または未起動 |
| ● 未設定 | ゲートウェイが停止中 (設定ダイアログを初めて開いた時) |

### 11.6 トラブルシュート

| 症状 | 対処 |
|---|---|
| ツールが表示されない | `npx` / `uvx` / `node` が PATH に存在するか確認。「MCP ゲートウェイ...」ダイアログでエラー内容を確認 |
| `✗ エラー: Process exited and max restarts reached` | コマンドや引数が正しいか確認。`maxRestarts` を増やすか `autoStart: false` で手動管理 |
| ゲートウェイツール呼び出しが `not running` エラー | 設定ダイアログを OK すると即時ホットリロードされる。または amm.exe を再起動 |
| ファイル直接編集後に反映されない | ダイアログから OK (ホットリロード) または amm.exe を再起動 |

---

## 12. ファイル配置 (ユーザーごと)

| 種類 | パス |
|---|---|
| 設定 | `profiles.amm` (`amm.exe` と同じディレクトリ。CLI 引数で別パス指定可) |
| レイアウト | `%LOCALAPPDATA%\amm\layout.json` (ウィンドウ位置・サイズ・下部パネル高さ・送信/表示トグル・エディタ設定・履歴上限 `historyMaxEntries`) |
| 入力履歴 | `%LOCALAPPDATA%\amm\history.json` (Ctrl+H の送信履歴。終了時に保存、起動時に復元。既定 最大 500 件、`layout.json` の `historyMaxEntries` で変更可) |
| アプリログ | `%LOCALAPPDATA%\amm\log\app.log` (1MB ローテーション、`.1` を 1 世代保持) |
| セッションログ | `%LOCALAPPDATA%\amm\sessions\YYYYMMDD-HHMMSS-<name>.log` (profile の `sessionLog: true` 時のみ。**平文保存**、機微データ用途では無効推奨) |
| WebView2 UserData | `amm.exe` 横の `WebViewShared/` (全セッション共有) |

---

## 13. トラブルシュート

### `CreateProcess failed: 2 (ERROR_FILE_NOT_FOUND)`

`executable` の exe が PATH に見つからない。`where.exe <name>` で確認し、`profiles.amm` の `executable` をフルパスにする。`.cmd` は `cmd.exe /c` でラップ必要。

### 子ウィンドウを開いても画面が真っ黒

WebView2 Runtime が未導入の可能性。Edge がインストールされた Win10/11 なら通常は自動で入る。`%LOCALAPPDATA%\amm\log\app.log` に `WebView2 init failed` があればそれ。

### Shift_JIS プロファイルで `NotSupportedException`

.NET 9 は既定で CP932 未登録だが、本アプリは `CodePagesEncodingProvider` を自動登録済。それでも出るなら .NET ランタイムの問題なので `dotnet --list-runtimes` で確認。

### GitHub Copilot CLI への自動 submit が効かない (制限事項)

入力パネル / MCP / エディタ連携経由で Copilot CLI (`copilot.cmd`) に複数行を含む内容を送ると、テキストは入力欄に届くが **Enter (`\r`) が submit と認識されず、プロンプトが実行されないまま入力欄に蓄積する**。

- **回避策**: 送信後に Copilot CLI MDI を直接フォーカスし、手動で Enter を押す
- **Claude Code / OpenAI Codex CLI / Gemini CLI では同条件で正常に submit される**
- 本体は `useBracketedPaste: true` + bracketed paste sequence + 確定 `\r` × 2 段で送信しており、Copilot CLI の Ink TUI 実装が paste 直後の `\r` を入力モード後処理に吸収していると推定 (再現確認済、追加 `\r` 段数を増やしても改善せず Claude/Codex の空 submit 副作用が増える)
- 受信側 (Copilot CLI) の挙動が変わらない限り根本解消は困難。詳細判断: [`.udr/records/UDR-amm-20260508T2200-cp1.yaml`](.udr/records/UDR-amm-20260508T2200-cp1.yaml)

### MCP クライアントから "GUI に接続できませんでした"

`amm.exe` (GUI) が起動していない、または別ユーザーセッションで起動している。GUI 起動を確認、または `--pipe-name` で接続先パイプを明示。

---
