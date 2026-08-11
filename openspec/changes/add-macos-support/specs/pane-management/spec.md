## MODIFIED Requirements

### Requirement: 入力待ち状態の可視化
各ペインは状態(Running / WaitingForInput / Unknown / Stopped)と、それとは独立の attention フラグに応じて、タイトル・タイトルバー色・タスクバー/Dockで状態を可視化しなければならない(SHALL)。

#### Scenario: タイトル絵文字とタイトルバー色
- **WHEN** ペインの状態が変化する
- **THEN** タイトルは `▶`(Running) / `●`(WaitingForInput) / `⚠`(WaitingForInput かつ attention) / `■`(Stopped) / `?`(Unknown) を接頭辞として更新される
- **AND** WaitingForInputのときペインのタイトルバー背景色を黄色(attention時はオレンジ)に変更し、それ以外は既定色へ戻す

#### Scenario: attention(許可・確認待ち)のタスクバー点滅(Windows)
- **WHEN** ペインがattention状態(許可・確認待ち)に遷移し、かつammが前面ウィンドウでなく、かつWindows上で動作している
- **THEN** `FlashWindowEx`(FLASHW_TRAY | FLASHW_TIMERNOFG)でタスクバーボタンを点滅させ、ammを前面化した時点で自動的に停止する

#### Scenario: attention(許可・確認待ち)のDockバウンス(macOS)
- **WHEN** ペインがattention状態(許可・確認待ち)に遷移し、かつammが前面ウィンドウでなく、かつmacOS上で動作している
- **THEN** `request_user_attention(Informational)`相当のTauri APIでDockアイコンをバウンスさせ、ammを前面化した時点で自動的に停止する

#### Scenario: attentionの解除
- **WHEN** ペインがアクティブ化される、または状態がRunning / Stoppedへ遷移する
- **THEN** attentionフラグをfalseに戻し、タイトル・タイトルバー色を通常のWaitingForInput表示に戻す

### Requirement: ペインのシステムメニュー拡張
Windows上で動作する場合、ウィンドウのシステムメニュー(タイトルバー左上アイコン / Alt+Space)は、Win32 API(`GetSystemMenu` / `AppendMenuW` / `WM_SYSCOMMAND`)を用いて「AMM」サブメニューを追加しなければならない(SHALL)。この要件はWindows専用であり、macOSでの同等機能は「ペイン設定へのアクセス導線(macOS)」要件を参照。

#### Scenario: AMM サブメニューの項目とID
- **WHEN** Windows上でウィンドウのハンドルが生成される
- **THEN** システムメニューに「名前変更…」/「エディタ連携」/「エディタ連携ファイルパスをコピー」/「フォントサイズ」サブメニュー(極大/大/中/小/極小)/「AMM 設定…」を持つ「AMM」ポップアップとして追加し、「閉じる」を一度削除してから最下段に付け直す
- **AND** カスタムIDはいずれもWindows標準(0xF000-0xFFF0帯)と衝突せず、かつ16の倍数(下位4bitはOSが予約するため)で採番する

## ADDED Requirements

### Requirement: ペイン設定へのアクセス導線(macOS)
macOSにはWindowsのシステムメニューに相当する概念が無いため、macOS上で動作する場合は「名前変更」「エディタ連携」「エディタ連携ファイルパスをコピー」「フォントサイズ」「AMM設定」の各項目を、ペインタイトルバーの右クリックコンテキストメニューから提供しなければならない(SHALL)。項目の集合・挙動はWindows版のシステムメニューと同等でなければならない(SHALL)。

#### Scenario: macOSでのコンテキストメニュー項目
- **WHEN** macOS上でペインタイトルバーを右クリックする
- **THEN** 「名前変更…」/「エディタ連携」/「エディタ連携ファイルパスをコピー」/「フォントサイズ」サブメニュー(極大/大/中/小/極小)/「AMM 設定…」がコンテキストメニューに表示される
