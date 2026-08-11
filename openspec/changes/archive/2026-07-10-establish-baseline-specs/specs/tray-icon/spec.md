## ADDED Requirements

### Requirement: システムトレイ常駐アイコン
amm は起動中、システムトレイ (通知領域) にアプリアイコンを常駐表示しなければならない (SHALL)。

#### Scenario: 起動と終了
- **WHEN** amm.exe が起動する
- **THEN** `amm.ico` を使ったトレイアイコン (`NotifyIcon`) が表示される
- **WHEN** amm が終了する (`FormClosing` 処理の一環)
- **THEN** トレイアイコンを `Visible=false` にしたうえで `Dispose()` し、タスクバー通知領域に幽霊アイコンを残さない

#### Scenario: ツールチップに入力待ち件数を表示
- **WHEN** 入力待ち (`WaitingForInput`) 状態の MDI 子が 0 件のとき
- **THEN** ツールチップは `"amm — 起動中"` になる
- **WHEN** 入力待ちの MDI 子が N (>0) 件のとき
- **THEN** ツールチップは `"amm — 入力待ち N 件"` になる (Windows のツールチップ長制限に合わせ 63 文字を超える場合は切り詰める)

### Requirement: 入力待ち遷移時のバルーン通知
いずれかの MDI 子が `WaitingForInput` へ遷移したとき、amm がフォアグラウンドでなければバルーン通知を表示しなければならない (SHALL)。この遷移は正規表現ベースの `WaitPatternDetector` 判定・hook 駆動の外部状態通知のいずれが起因でも区別なく対象になる。

#### Scenario: フォアグラウンド時は通知しない
- **WHEN** MDI 子が `WaitingForInput` に遷移した時点で amm のメインウィンドウがフォアグラウンド (`GetForegroundWindow() == 親ウィンドウハンドル`) である
- **THEN** バルーン通知を表示しない

#### Scenario: バックグラウンド時の通知内容
- **WHEN** amm がフォアグラウンドでない状態で MDI 子が `WaitingForInput` に遷移する
- **THEN** タイトル `"amm: 入力待ち"`、本文 `"{DisplayName} が入力待ちです"` の `ShowBalloonTip` (表示時間 5000ms、`ToolTipIcon.Info`) を表示する

#### Scenario: 同一セッションの連続通知を抑制
- **WHEN** 同一 MDI 子について直近のバルーン表示から 5000ms 未満で再度 `WaitingForInput` への通知が来る
- **THEN** 重複バルーンを抑制する (セッションごとの最終表示時刻で dedup)

#### Scenario: 通知トグルの永続化
- **WHEN** トレイの右クリックメニュー「バルーン通知」チェックを外す
- **THEN** 以後の `WaitingForInput` 遷移でバルーンを表示しなくなり、この ON/OFF 状態は `%LOCALAPPDATA%\amm\layout.json` の `TrayNotifyEnabled` に永続化され次回起動時に復元される

### Requirement: クリック操作によるセッションジャンプ
トレイアイコンのクリック操作は、メインウィンドウを前面化したうえで該当する入力待ちセッションへフォーカスを移さなければならない (SHALL)。

#### Scenario: 左クリック / バルーンクリックで前面化
- **WHEN** トレイアイコンを左クリックする、またはバルーン通知自体をクリックする
- **THEN** `ShowWindow(SW_RESTORE)` + `SetForegroundWindow` + `Activate()` でメインウィンドウを前面化し、明示的なジャンプ対象がない場合は入力待ち中で最も古く遷移した MDI 子をアクティブ化する (入力待ちが 0 件なら前面化のみ)

#### Scenario: ダブルクリックで通知元を最大化
- **WHEN** トレイアイコンを左ダブルクリックする
- **THEN** 直近にバルーン通知を出した MDI 子 (未生存なら入力待ち中で最も古いもの) を最大化してフォーカスする

### Requirement: 右クリックコンテキストメニュー
トレイアイコンの右クリックメニューは、前面化・入力待ちセッション一覧・通知トグル・終了の操作を提供しなければならない (SHALL)。

#### Scenario: メニュー構成
- **WHEN** トレイアイコンを右クリックする
- **THEN** 「amm を表示」(前面化) / 「入力待ちセッション」(入力待ち中の各 MDI を遷移が古い順に列挙するサブメニュー、対象 0 件時はグレーアウト) / 「バルーン通知」(チェックトグル) / 区切り線 / 「終了」(`Application.Exit()`) の順で表示される
