## ADDED Requirements

### Requirement: MDI ウィンドウ構成
amm の GUI は単一の MDI 親フォーム (`MdiParentForm`) と、その配下に生成される 0〜N 個の MDI 子フォーム (`TerminalChildForm`) から構成されなければならない (SHALL)。親フォームはメニューバー・MDI クライアント領域・下部入力パネル (MDI クイック切替バー行 / 複数行入力欄行 / 送信先ステータス行の 3 行) を持ち、各子フォームは WebView2 をフィル配置して xterm.js 端末を表示する。

#### Scenario: 起動直後のウィンドウ
- **WHEN** amm.exe を起動する
- **THEN** タイトル `amm`、初期サイズ 1400×900 (前回終了時レイアウトが `%LOCALAPPDATA%\amm\layout.json` にあればそれを復元)、画面中央または復元位置で親フォームが表示される
- **AND** 下部パネルは既定で高さ 180px、MDI クライアント領域との間に手動リサイズ用スプリッタ (6px 帯) を持つ

#### Scenario: MDI 子の生成順序管理
- **WHEN** プロファイルから新規ターミナルを起動する (`OpenTerminal`)
- **THEN** 生成順を保持する独自リスト (`_childOrder`) に追加され、MDI クイック切替バーのボタン順・Ctrl+1..9 の割当順として使われる (WinForms 標準の z-order とは独立)

### Requirement: 入力欄キーボードショートカット
下部パネルの複数行入力欄 (`_inputBox`) は、フォーカスがある状態で以下のキー入力を専用コマンドとして解釈しなければならない (SHALL)。各送信系ショートカットは、入力欄に選択範囲があればその選択範囲のみを、なければ入力欄全文を送信対象とする。

#### Scenario: Ctrl+S でアクティブ子へ送信
- **WHEN** 入力欄にフォーカスがある状態で Ctrl+S を押す
- **THEN** 設定メニュー「Ctrl+S でプロンプト送信」が ON の場合のみ、解決された送信先 (アクティブ MDI → 直近アクティブ → 唯一の生存子の順にフォールバック) へ送信する
- **AND** OFF の場合は何もせず (エディタの保存グセによる誤送信防止)、ログにのみ記録する

#### Scenario: Ctrl+Shift+S で全ペイン同時送信
- **WHEN** 入力欄にフォーカスがある状態で Ctrl+Shift+S を押す
- **THEN** 生存する全 MDI 子へ単発ブロードキャスト送信する (モードレスな単発操作であり、トグルモードではない)

#### Scenario: Ctrl+1〜Ctrl+9 で番号指定送信
- **WHEN** 入力欄にフォーカスがある状態で Ctrl+1〜Ctrl+9 (テンキーも可) を押す
- **THEN** MDI クイック切替バーの表示順で該当番号の MDI 子へ送信する

#### Scenario: Ctrl+H / Ctrl+E / Enter
- **WHEN** Ctrl+H を押す
- **THEN** 送信履歴ポップアップ (`InputHistory`、既定最大 500 件、`history.json` で起動間永続化) を開く
- **WHEN** Ctrl+E を押す
- **THEN** アクティブ MDI 向けのエディタ連携を起動する
- **WHEN** 装飾なしの Enter を押す
- **THEN** 送信せず改行のみを挿入する (誤送信防止のため Enter 単独送信・Ctrl+Enter 送信は提供しない)

### Requirement: MDI タイル整列
表示メニューは、開いた順序に基づく決定的な整列と、手動配置の記憶・復元を提供しなければならない (SHALL)。

#### Scenario: タイル Ｚ (グリッド整列)
- **WHEN** 「タイル Ｚ」を実行する
- **THEN** 最小化中でない生存子を `_childOrder` の順序で `cols = ceil(sqrt(N))`, `rows = ceil(N/cols)` のグリッドに均等割り配置し、最終行は端数を吸収して余白なく埋める
- **AND** 以後の親ウィンドウのリサイズ (約100ms デバウンス後) で同じ整列が自動再適用される

#### Scenario: タイル縦 / タイル横 (単列整列)
- **WHEN** 「タイル縦」または「タイル横」を実行する
- **THEN** 生存子を開いた順に 1 列 (フル幅・縦積み) または 1 行 (フル高・横並び) でクライアント領域を均等割りして配置する (オーバーフローさせない)

#### Scenario: 記憶した配置の復元
- **WHEN** 「記憶した配置で表示」を実行する
- **THEN** 各プロファイルの `windowGeometry` (1 始まりインデックス、同プロファイルの生存数+1 を参照、全閉じでリセット) に従って位置・サイズ・名前・最大化状態を適用し、未起動分は `autoStartCount` に従って追加起動する

### Requirement: 入力待ち状態の可視化
各 MDI 子は `WaitState` (Running / WaitingForInput / Unknown / Stopped) と、それとは独立の `HasAttention` フラグに応じて、タイトル・タイトルバー色・タスクバーで状態を可視化しなければならない (SHALL)。

#### Scenario: タイトル絵文字とタイトルバー色
- **WHEN** `WaitState` が変化する
- **THEN** タイトルは `▶` (Running) / `●` (WaitingForInput) / `⚠` (WaitingForInput かつ HasAttention) / `■` (Stopped) / `?` (Unknown) を接頭辞として更新される
- **AND** WaitingForInput のときタイトルバー背景色を `DwmSetWindowAttribute` で黄色 (RGB 255,220,90、attention 時はオレンジ RGB 255,150,50) に変更し、それ以外は OS 既定色へ戻す (Windows 11 22000+ でのみ視覚効果あり、それ以前は no-op)

#### Scenario: attention (許可・確認待ち) のタスクバー点滅
- **WHEN** MDI 子が attention 状態 (許可・確認待ち) に遷移し、かつ amm 前面ウィンドウでない
- **THEN** `FlashWindowEx` (FLASHW_TRAY | FLASHW_TIMERNOFG) でタスクバーボタンを点滅させ、amm を前面化した時点で自動的に停止する

#### Scenario: attention の解除
- **WHEN** ペインがアクティブ化される、または `WaitState` が Running / Stopped へ遷移する
- **THEN** `HasAttention` を false に戻し、タイトル・タイトルバー色を通常の WaitingForInput 表示に戻す

### Requirement: MDI 子システムメニュー拡張
各 MDI 子ウィンドウのシステムメニュー (タイトルバー左上アイコン / Alt+Space) は、Win32 API (`GetSystemMenu` / `AppendMenu` / `WM_SYSCOMMAND`) を用いて「AMM」サブメニューを追加しなければならない (SHALL)。

#### Scenario: AMM サブメニューの項目と ID
- **WHEN** MDI 子のハンドルが生成される
- **THEN** システムメニューに「名前変更…」(`SC_AMM_RENAME`=0x1030) / 「エディタ連携」(`SC_AMM_EDITOR_LINK`=0x1040) / 「エディタ連携ファイルパスをコピー」(`SC_AMM_COPY_EDITOR_PATH`=0x1020) / 「フォントサイズ」サブメニュー (極大/大/中/小/極小、`SC_AMM_FONT_XL`〜`SC_AMM_FONT_XS`=0x1050〜0x1090) / 「AMM 設定…」(`SC_AMM_SETTINGS`=0x1010) を持つ「AMM」ポップアップとして追加し、「閉じる」(`SC_CLOSE`) を一度削除してから最下段に付け直す
- **AND** カスタム ID はいずれも Windows 標準 (0xF000-0xFFF0 帯) と衝突しない 0x1000 台を使う

### Requirement: ファイルドラッグ&ドロップ
入力欄および MDI 子ウィンドウへのファイルドロップは、テキストファイル判定に基づき内容送信とパス送信を選択させなければならない (SHALL)。

#### Scenario: MDI 子へのドロップ
- **WHEN** ファイルを MDI 子 (WebView2 領域を含む) へドロップする
- **THEN** 全ファイルがテキスト判定 (拡張子ベース、`FileDropHelper.IsTextFile`) のときのみ「ファイル内容を送信」「絶対パスを送信」の選択ダイアログを表示し、非テキストが 1 つでもあれば確認なしでパス送信に確定する
- **AND** 内容送信は合計サイズが 1MB を超える場合に追加の確認ダイアログを出し、パス送信は末尾に改行を付けずシェル入力バッファへ挿入するだけで実行しない

#### Scenario: WebView2 上のネイティブドロップ横取り
- **WHEN** WebView2 のロード完了後
- **THEN** WebView2 本体および再帰的に列挙した全子孫 HWND に対し、Chromium 既定の `IDropTarget` を `RevokeDragDrop` で外し、自前の `NativeDropTarget` (OLE `IDropTarget` 実装、`CF_HDROP` から絶対パス配列を抽出) を `RegisterDragDrop` で登録する (Chromium は web コンテンツへ `file://` 絶対パスを渡さないため、HWND レベルで OS ドロップを横取りする必要がある)

### Requirement: ウィンドウレイアウトと表示設定の永続化
親ウィンドウの位置・サイズ・下部パネル高さ・各種表示/送信トグルは `%LOCALAPPDATA%\amm\layout.json` に保存し、次回起動時に復元しなければならない (SHALL)。

#### Scenario: 保存対象と健全性チェック
- **WHEN** amm を終了する
- **THEN** ウィンドウ位置・サイズ・`WindowState`・下部パネル高さ・送信後クリア/コメントフィルタ/Ctrl+S 送信可否/確定改行同梱/MDIごと下書き/各行表示トグル/許可ポップアップ/トレイ通知/エディタ連携設定/入力履歴上限を JSON へ書き出す
- **WHEN** 次回起動時に `layout.json` を読み込む
- **THEN** 保存された矩形の幅・高さが 400×300 未満、またはいずれの画面の作業領域とも交差しない場合は復元せず既定レイアウトにフォールバックする

#### Scenario: 下部パネル行の個別表示切替
- **WHEN** 「MDI ボタンバーを表示」「入力欄を表示」「送信先ステータスを表示」のいずれかをトグルする
- **THEN** 該当行の `RowStyle` の高さを 0 に切り替えて非表示にし (Visible=false だけでは行領域が残るため)、入力欄を隠す際は直前の表示時高さを記憶しておき再表示時に同じ高さへ復元する

### Requirement: 終了時の確認ガード
amm 終了時 (ユーザーによるクローズ) は、実行中セッション・未保存のプロファイル変更・未コミットの Git 変更について確認しなければならない (SHALL)。

#### Scenario: 実行中セッションの確認
- **WHEN** `CloseReason.UserClosing` でメイン終了しようとし、かつ実行中の MDI 子が 1 つ以上ある
- **THEN** 実行中セッション名の一覧を表示し、ユーザーが「いいえ」を選んだ場合は終了をキャンセルする (タスクマネージャ等からの強制終了時はこの確認を行わない)

#### Scenario: 未保存プロファイル変更の確認
- **WHEN** 終了時点で `_profiles` がロード時のスナップショットと差分を持つ
- **THEN** 保存するか確認するダイアログ (はい/いいえ/キャンセル) を出し、「はい」で AMM ファイル未オープン時は名前を付けて保存フローへ、キャンセル時は終了自体を中止する

#### Scenario: 外部 .amm ファイルの自動起動確認
- **WHEN** コマンドライン引数やダブルクリックで明示的に指定された `.amm` ファイルを開き、かつそのパスが未承認である
- **THEN** 実際に自動起動されるコマンド一覧 (最大 10 件表示) を提示し、ユーザーが「いいえ」を選んだ場合は自動起動を行わない (悪意ある `.amm` ファイルによる自動 RCE を防止するため)
