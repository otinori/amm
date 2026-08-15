# pane-management Specification

## Purpose
amm の GUI 本体（Tauri版、`src/apps/Amm/`）における単一ウィンドウ内ペイン管理方式の仕様。旧 `mdi-window-control`/`mdi-window-management`（MDI方式、レガシー.NET版 `src/apps/Amm.net/`）を `migrate-to-tauri` change（2026-07-26 archive）で置き換えたもの。経緯: UDR-amm-20260713T0447-98f。

## Requirements
### Requirement: 単一ウィンドウ内ペイン構成
amm の GUI は単一のネイティブウィンドウ(Tauri `WebviewWindow`)から構成され、その内部に 0〜N 個のペイン(独立したターミナルセッション、各々が固有の ConPTY 接続を持つ)を HTML/CSS(`position:absolute` + `z-index`)で配置しなければならない(SHALL)。ウィンドウはメニューバー相当の UI・ペイン表示領域・下部入力パネル(クイック切替バー行/複数行入力欄行/送信先ステータス行の3行)を持つ。

#### Scenario: 起動直後のウィンドウ
- **WHEN** amm を起動する
- **THEN** タイトル `amm`、初期サイズ 1400×900(前回終了時レイアウトが永続化ファイルにあればそれを復元)、画面中央または復元位置でウィンドウが表示される
- **AND** 下部パネルは既定で高さ180px、ペイン表示領域との間に手動リサイズ用スプリッタ(6px帯)を持つ

#### Scenario: ペインの生成順序管理
- **WHEN** プロファイルから新規ターミナルを起動する
- **THEN** 生成順を保持するリストに追加され、クイック切替バーのボタン順・Ctrl+1..9の割当順として使われる(DOM上のz-orderとは独立)

### Requirement: 入力欄キーボードショートカット
下部パネルの複数行入力欄は、フォーカスがある状態で以下のキー入力を専用コマンドとして解釈しなければならない(SHALL)。各送信系ショートカットは、入力欄に選択範囲があればその選択範囲のみを、なければ入力欄全文を送信対象とする。

#### Scenario: Ctrl+S でアクティブペインへ送信
- **WHEN** 入力欄にフォーカスがある状態でCtrl+Sを押す
- **THEN** 設定「Ctrl+Sでプロンプト送信」がONの場合のみ、解決された送信先(アクティブペイン → 直近アクティブ → 唯一の生存ペインの順にフォールバック)へ送信する
- **AND** OFFの場合は何もせず、ログにのみ記録する

#### Scenario: Ctrl+Shift+S で全ペイン同時送信
- **WHEN** 入力欄にフォーカスがある状態でCtrl+Shift+Sを押す
- **THEN** 生存する全ペインへ単発ブロードキャスト送信する

#### Scenario: Ctrl+1〜Ctrl+9 で番号指定送信
- **WHEN** 入力欄にフォーカスがある状態でCtrl+1〜Ctrl+9(テンキーも可)を押す
- **THEN** クイック切替バーの表示順で該当番号のペインへ送信する

#### Scenario: Ctrl+H / Ctrl+E / Enter
- **WHEN** Ctrl+Hを押す
- **THEN** 送信履歴ポップアップ(既定最大500件、起動間永続化)を開く
- **WHEN** Ctrl+Eを押す
- **THEN** アクティブペイン向けのエディタ連携を起動する
- **WHEN** 装飾なしのEnterを押す
- **THEN** 送信せず改行のみを挿入する(誤送信防止のためEnter単独送信・Ctrl+Enter送信は提供しない)

### Requirement: 常時タイリングによるペイン整列
ペイン表示領域は自由配置(フリーフォーム)を提供せず、生存する全ペインを常に隙間なく埋め尽くす決定的なグリッドで整列しなければならない(SHALL)。手動でのカスタム配置・角のリサイズハンドル・カスケード整列は提供しない。

#### Scenario: グリッド整列
- **WHEN** ペインの生成・クローズ・ウィンドウリサイズ(デバウンス後)のいずれかが発生する
- **THEN** 生存ペインを配列順(初期値は生成順、ドラッグ入れ替えで変化しうる)で `rows = max(1, floor(sqrt(N)))`, `cols = ceil(N/rows)` のグリッドに均等割り配置し、最終行は端数を吸収して余白なく埋める

#### Scenario: タイトルバードラッグでの並び替え
- **WHEN** ペインのタイトルバーを4px以上ドラッグし、別の生存ペインの矩形上でマウスを離す(ドラッグ中はドロップ先候補の矩形をオーバーレイ表示する)
- **THEN** ドラッグ元とドロップ先の2ペインがグリッド上の位置(配列内のインデックス)を入れ替える
- **AND** 4px未満の移動(クリック相当)はドラッグとみなさず、何も起きない

#### Scenario: ズーム(全画面表示トグル)
- **WHEN** ペインのタイトルバーの「⛶」アイコンをクリックする
- **THEN** そのペインが表示領域全体を占有し、他の生存ペインは非表示になる(グリッド整列の配列順は変化しない)
- **WHEN** ズーム中のペインの「⛶」アイコンを再度クリックする、または別のペインをズームする
- **THEN** 通常のグリッド整列に戻る

#### Scenario: 並び順のリセット
- **WHEN** 「並び順をリセット」を実行する
- **THEN** 生存ペインの並び順を生成順(displayId順、ドラッグ入れ替えの影響を受けない)へ戻し、各プロファイルの自動起動数設定に従って未起動分を追加起動する

#### Scenario: 起動時のペイン自動起動
- **WHEN** amm を起動する
- **THEN** 各プロファイルの`autoStartCount`設定に従って不足分のペインを起動する。それ以外の経路(前回終了時の生存ペイン数の記憶等)でペインを自動生成することはない(profiles.ammに定義のないコマンドが起動時に無断で立ち上がることを防ぐため。並び順自体は生存ペイン間の一時的な状態であり、再起動をまたいで永続化しない)

### Requirement: 入力待ち状態の可視化
各ペインは状態(Running / WaitingForInput / Unknown / Stopped)と、それとは独立の attention フラグに応じて、タイトル・タイトルバー色・タスクバーで状態を可視化しなければならない(SHALL)。

#### Scenario: タイトル絵文字とタイトルバー色
- **WHEN** ペインの状態が変化する
- **THEN** タイトルは `▶`(Running) / `●`(WaitingForInput) / `⚠`(WaitingForInput かつ attention) / `■`(Stopped) / `?`(Unknown) を接頭辞として更新される
- **AND** WaitingForInputのときペインのタイトルバー背景色を黄色(attention時はオレンジ)に変更し、それ以外は既定色へ戻す

#### Scenario: attention(許可・確認待ち)のタスクバー点滅
- **WHEN** ペインがattention状態(許可・確認待ち)に遷移し、かつammが前面ウィンドウでない
- **THEN** `FlashWindowEx`(FLASHW_TRAY | FLASHW_TIMERNOFG)でタスクバーボタンを点滅させ、ammを前面化した時点で自動的に停止する

#### Scenario: attentionの解除
- **WHEN** ペインがアクティブ化される、または状態がRunning / Stoppedへ遷移する
- **THEN** attentionフラグをfalseに戻し、タイトル・タイトルバー色を通常のWaitingForInput表示に戻す

### Requirement: ペインのシステムメニュー拡張
ウィンドウのシステムメニュー(タイトルバー左上アイコン / Alt+Space)は、Win32 API(`GetSystemMenu` / `AppendMenuW` / `WM_SYSCOMMAND`)を用いて「AMM」サブメニューを追加しなければならない(SHALL)。

#### Scenario: AMM サブメニューの項目とID
- **WHEN** ウィンドウのハンドルが生成される
- **THEN** システムメニューに「名前変更…」/「エディタ連携」/「エディタ連携ファイルパスをコピー」/「フォントサイズ」サブメニュー(極大/大/中/小/極小)/「AMM 設定…」を持つ「AMM」ポップアップとして追加し、「閉じる」を一度削除してから最下段に付け直す
- **AND** カスタムIDはいずれもWindows標準(0xF000-0xFFF0帯)と衝突せず、かつ16の倍数(下位4bitはOSが予約するため)で採番する

### Requirement: ファイルドラッグ&ドロップ
入力欄およびペイン領域へのファイルドロップは、テキストファイル判定に基づき内容送信とパス送信を選択させなければならない(SHALL)。

#### Scenario: ペインへのドロップ
- **WHEN** ファイルをペイン(WebView領域を含む)へドロップする
- **THEN** 全ファイルがテキスト判定(拡張子ベース)のときのみ「ファイル内容を送信」「絶対パスを送信」の選択ダイアログを表示し、非テキストが1つでもあれば確認なしでパス送信に確定する
- **AND** 内容送信は合計サイズが1MBを超える場合に追加の確認ダイアログを出し、パス送信は末尾に改行を付けずシェル入力バッファへ挿入するだけで実行しない

### Requirement: ウィンドウレイアウトと表示設定の永続化
ウィンドウの位置・サイズ・下部パネル高さ・各種表示/送信トグル・各ペインの配置は永続化ファイルに保存し、次回起動時に復元しなければならない(SHALL)。

#### Scenario: 保存対象と健全性チェック
- **WHEN** ammを終了する
- **THEN** ウィンドウ位置・サイズ・状態・下部パネル高さ・送信後クリア/コメントフィルタ/Ctrl+S送信可否/確定改行同梱/ペインごと下書き/各行表示トグル/許可ポップアップ/トレイ通知/エディタ連携設定/入力履歴上限・各ペインの配置を永続化ファイルへ書き出す
- **WHEN** 次回起動時に永続化ファイルを読み込む
- **THEN** 保存された矩形の幅・高さが400×300未満、またはいずれの画面の作業領域とも交差しない場合は復元せず既定レイアウトにフォールバックする

#### Scenario: 下部パネル行の個別表示切替
- **WHEN** 「クイック切替バーを表示」「入力欄を表示」「送信先ステータスを表示」のいずれかをトグルする
- **THEN** 該当行を高さ0に切り替えて非表示にし、入力欄を隠す際は直前の表示時高さを記憶しておき再表示時に同じ高さへ復元する

### Requirement: 終了時の確認ガード
amm終了時(ユーザーによるクローズ)は、実行中セッション・未保存のプロファイル変更・未コミットのGit変更について確認しなければならない(SHALL)。

#### Scenario: 実行中セッションの確認
- **WHEN** ユーザー操作でメイン終了しようとし、かつ実行中のペインが1つ以上ある
- **THEN** 実行中セッション名の一覧を表示し、ユーザーが「いいえ」を選んだ場合は終了をキャンセルする(タスクマネージャ等からの強制終了時はこの確認を行わない)

#### Scenario: 未保存プロファイル変更の確認
- **WHEN** 終了時点でメモリ上のプロファイル一覧が、現在アクティブな`.amm`ファイルのディスク上の内容と異なる
- **THEN** 対象ファイルの絶対パスを明示した確認ダイアログ(「保存して終了」/「保存せず終了」/「キャンセル」の3ボタン、2026-07-27にネイティブ`prompt()`の3択文字列入力から`openModal`ベースのボタンUIへ変更 — タイトルが`tauri.localhost`のURLになる・空欄OKの意味が非自明という実機動作確認での指摘を受けた修正)を表示する
- **AND** 「保存して終了」は既存の「上書き保存」(`save_profiles_now`)と同じ、アクティブな`.amm`ファイルへの無確認上書きを実行する(名前を付けて保存のピッカーは出さない — 常にアクティブファイルへの上書きという、この移植のプロファイルファイル運用モデル全体と一貫させるため)
- **AND** 「キャンセル」(またはEscキー/背景クリック)は終了自体を中止する

#### Scenario: 外部 .amm ファイルの自動起動確認
- **WHEN** コマンドライン引数やダブルクリックで明示的に指定された`.amm`ファイルを開き、かつそのパスが未承認である
- **THEN** 実際に自動起動されるコマンド一覧(最大10件表示)を提示し、ユーザーが「いいえ」を選んだ場合は自動起動を行わない

### Requirement: pane/open によるペインのプログラム的オープン
`pane/open` MCPツール(および直接RPC `amm.openPane`)は`command`または`profile_name`のいずれか一方を必須パラメータとして受け取り、新しいペインを作成して一意な`session_id`(GUID)を返さなければならない(SHALL)。`args` / `title` / `workingDirectory`は任意で、`profile_name`指定時は既存プロファイルの設定(nickname等)を継承する。

#### Scenario: command 指定での一時起動
- **WHEN** `command="claude"`を指定して`pane/open`を呼ぶ
- **THEN** 新しいペインが起動し`{ session_id }`が返る

#### Scenario: command と profile_name がともに未指定
- **WHEN** `command`と`profile_name`のどちらも指定せずに呼ぶ
- **THEN** `-32602`(Invalid params)のエラーが返り、ペインは開かれない

### Requirement: pane/close によるペインのプログラム的クローズ
`pane/close` MCPツール(および直接RPC `amm.closePane`)は必須パラメータ`session_id`と任意パラメータ`force`を受け取り、対象ペインを閉じなければならない(SHALL)。`force=true`の場合は確認ダイアログを表示せずに閉じる。`session_id`が見つからない場合は例外を投げず`{ error }`を返す。

#### Scenario: 存在しない session_id
- **WHEN** 既に手動で閉じられた、または存在しない`session_id`で`pane/close`を呼ぶ
- **THEN** 例外は発生せず`{ error: "session not found: ..." }`相当の応答が返る

#### Scenario: force による強制クローズ
- **WHEN** `force=true`で`pane/close`を呼ぶ
- **THEN** ユーザー確認ダイアログなしに即座にペインが閉じる

### Requirement: pane/wait_state によるブロッキング状態待機
`pane/wait_state` MCPツール(および直接RPC `amm.waitState`)は`session_id`と`target_state`(`"idle"`または`"attention"`)を必須とし、`timeout_ms`(既定300000=5分)の間、対象セッションが目標状態に到達するまでレスポンスを保留しなければならない(SHALL)。到達時は`{ state, elapsed_ms }`を、タイムアウト時は`{ state: "timeout", elapsed_ms }`を返し、クラッシュしない。

#### Scenario: idle 到達での解放
- **WHEN** 対象セッションがhook(amm/notify)経由でidle状態へ遷移する
- **THEN** 保留中の`pane/wait_state`呼び出しが即座に`{ state: "idle", elapsed_ms }`で解決される

#### Scenario: タイムアウト
- **WHEN** `timeout_ms`経過しても目標状態に到達しない
- **THEN** `{ state: "timeout", elapsed_ms }`が返り、待機は正常終了する(例外にならない)

#### Scenario: セッションクローズによる解放
- **WHEN** 待機中に対象セッションがクローズされる(ユーザー操作またはプログラムからの`pane/close`)
- **THEN** 保留中の全waitが`"timeout"`扱いで解放される

### Requirement: WaitBroker によるペイン単位の待機管理
`WaitBroker`は`session_id`とhook通知用`notifyToken`の対応関係を保持し、同一セッションに対する複数の並行`RegisterWait`呼び出しをそれぞれ独立に解放できなければならない(SHALL)。`ResolveByToken`は`target_state`が一致するpendingのみを解決する。

#### Scenario: 目標状態が異なる複数 wait の共存
- **WHEN** 同一セッションに対し`target_state="idle"`と`target_state="attention"`のwaitが同時に登録されている
- **THEN** `attention`への遷移通知が来た場合、`attention`を待つwaitのみが解決され`idle`を待つwaitは保留され続ける

### Requirement: MCP ツールと PowerShell cmdlet の意味論一致
Amm.PowerShellモジュールの`Open-AmmWindow` / `Close-AmmWindow` / `Wait-AmmIdle` cmdletは、同名のNamed Pipe直接RPC(`amm.openPane` / `amm.closePane` / `amm.waitState`)を呼び出し、MCPツール版と同一のパラメータ意味論・戻り値構造を提供しなければならない(SHALL)。

#### Scenario: プロファイル名によるオープン
- **WHEN** PowerShellから`Open-AmmWindow -ProfileName "Claude Code"`を実行する
- **THEN** MCP経由の`pane/open`に`profile_name`を渡した場合と同じ`AmmSession`(session_idを含む)が返る

#### Scenario: nickname 指定での待機
- **WHEN** `Wait-AmmIdle -Nickname claude`を実行する
- **THEN** cmdletは内部で`list_participants`によりnicknameから`session_id`を解決してから`amm.waitState`を呼ぶ

### Requirement: コマンドメニューによるプロファイルのペイン起動
GUIは、`profiles.amm`に定義された各プロファイルを一覧表示し、ユーザーがクリック等の単純な操作で選択したプロファイルを新規ペインとして直接起動できるメニュー(コマンドメニュー相当)を提供しなければならない(SHALL)。この経路はMCP/PowerShellのような外部プログラムを介さず、GUI単体で完結しなければならない(SHALL)。

#### Scenario: メニューからのプロファイル起動
- **WHEN** ユーザーがコマンドメニューに列挙されたプロファイルの1つを選択する
- **THEN** そのプロファイルの設定(実行ファイル・引数・nickname等)を継承した新規ペインが起動する

#### Scenario: プロファイル一覧の追従
- **WHEN** プロファイルファイルがホットリロードされ、プロファイルの追加・削除・名称変更が反映される
- **THEN** コマンドメニューの内容も次回開いたときには最新のプロファイル一覧を反映する

### Requirement: クイック切替バーの状態表示と操作メニュー
クイック切替バーの各ボタンは、対応するペインの状態(Running / WaitingForInput / Unknown / Stopped)およびattentionフラグに応じた背景色を表示しなければならず(SHALL)、右クリックで当該ペインに対する操作メニュー(クイック送信・チャット記録トグル・改行付き送信・再送信・並び替え等、入力欄やペイン右クリックメニューが提供する操作のうち妥当なもの)を提供しなければならない(SHALL)。

#### Scenario: 状態に応じた背景色
- **WHEN** ペインの状態またはattentionフラグが変化する
- **THEN** クイック切替バー上の対応するボタンの背景色が、最小化/attention/入力待ち/アクティブ等の状態を視覚的に区別できる形で更新される(ペインのタイトルバー色と整合する情報を提供する)

#### Scenario: 右クリックによる操作メニュー
- **WHEN** クイック切替バーのボタンを右クリックする
- **THEN** 当該ペインを対象とした操作メニュー(クイック送信・チャット記録トグル等)が表示され、選択した操作は明示的にそのペインに対して実行される(アクティブペインの推測に依存しない)
