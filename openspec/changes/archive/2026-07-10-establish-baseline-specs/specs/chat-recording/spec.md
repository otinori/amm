## ADDED Requirements

### Requirement: コマンド送信を起点としたチャット記録の開始
MDI 子ウィンドウ（`TerminalChildForm`）は、`ChatRecordEnabled` が true の場合、コマンド送信を検知するたびに `StartChatRecording` を呼び出して新しい `ChatRecorder` を生成しなければならない (SHALL)。送信検知は「下部共通入力欄からの送信 (`DispatchSendAsync`)」と「ターミナル画面へ直接タイプして Enter を押す操作 (xterm.js からの生キー入力を行バッファリングし `\r`/`\n` を検知する `TrackTypedInputForRecording`)」の 2 経路を等しく起点として扱う。`ChatRecordEnabled` の初期値はプロファイルの `ChatRecord`（既定 false）を継承するが、MDI 右クリックメニューでの切り替えは `profiles.amm` へ永続化されない実行時限りのトグルである。

#### Scenario: 下部入力欄からの送信
- **WHEN** チャット記録が有効な MDI に対しユーザーが下部共通入力欄からコマンドを送信する
- **THEN** amm は `StartChatRecording(command)` を呼び、未完了の前回記録があれば先に `Complete()` してから新しい `ChatRecorder` を生成する

#### Scenario: ターミナルへの直接タイプ
- **WHEN** チャット記録が有効な MDI のターミナル画面へ直接文字を入力し Enter を押す（下部入力欄を経由しない）
- **THEN** amm は `TrackTypedInputForRecording` で行バッファ（ANSI エスケープを `AnsiStripper` で除去した best-effort 文字列、上限 `TypedLineBufMaxChars`=20000 文字）から確定した行を command として `StartChatRecording` を呼ぶ

#### Scenario: トグルは再起動で既定値に戻る
- **WHEN** ユーザーが MDI 右クリックメニューの「チャット記録(&C)」チェックを実行時にオンにする
- **THEN** 変更は `profiles.amm` に書き戻されず、amm 再起動後は当該プロファイルの `ChatRecord` 既定値（false）に戻る

### Requirement: 応答テキストのローリングバッファ保持
`ChatRecorder.Feed` は ConPTY 読み取りスレッドから呼ばれ、受け取った生 ANSI データを `AnsiStripper.Strip` でプレーンテキスト化した上でバッファに追記し、バッファ長が `tailChars` の 2 倍を超えた場合は末尾 `tailChars` 文字のみを残して先頭側を破棄しなければならない (SHALL)。`tailChars` はプロファイルの `ChatRecordTailChars`（既定 2000）から取得し、0 以下の場合は 2000 にフォールバックする。

#### Scenario: バッファが上限の2倍を超える
- **WHEN** 応答出力が蓄積し `_buf.Length` が `tailChars * 2` を超える
- **THEN** amm はバッファを末尾 `tailChars` 文字だけ残すよう切り詰め、以降の `Feed` はそこに追記を続ける

### Requirement: 出力静穏によるチャット記録の確定と JSON 書き出し
出力が `ChatRecordQuietMs`（1200ミリ秒）の間途絶えたことをもって「応答完了」とみなし、amm は UI スレッド上で `ChatRecorder.Complete()` を呼び出して 1 交換分の記録を `<workDir>/.amm/logs/<yyyyMMdd>/<プロファイル名(サニタイズ済み)>-<yyyyMMddTHHmmss>-<4桁16進乱数>.json` として書き出さなければならない (SHALL)。`yyyyMMdd` フォルダ名は送信時刻 (`SentAt`) のローカル日付を用いる。JSON には `id`/`profile`/`mdi_name`/`sent_at`/`responded_at`/`duration_ms`/`command`/`response_tail`/`follow_up_questions`（将来拡張用の空配列）を含める。

#### Scenario: 出力が静穏になり記録が確定する
- **WHEN** ConPTY からの出力が 1200ms 途絶える
- **THEN** amm はスレッドプールのタイマコールバックから `BeginInvoke` 経由で UI スレッドに戻り、`_activeRecorder` を `Interlocked.Exchange` で取り出して `Complete()` を呼ぶ
- **AND** バッファ長が `tailChars` 以下ならバッファ全体を、超えていれば末尾 `tailChars` 文字を `response_tail` として書き出す

#### Scenario: 次コマンド送信時に前回記録が未完了
- **WHEN** 前回の記録がまだ `Complete()` されていない状態で新しいコマンドが送信される
- **THEN** amm は新しい `ChatRecorder` を生成する前に、既存の `_activeRecorder` に対して `Complete()` を呼び出して確定させる

#### Scenario: JSON 書き出し失敗時の扱い
- **WHEN** `Complete()` 内でディレクトリ作成やファイル書き込みが例外を送出する
- **THEN** amm は `AppLogger.Error` に記録した上で日次インデックス更新処理をスキップし、例外を上位に伝播させない

### Requirement: 日次インデックスファイルへの追記と破損耐性
チャット記録 JSON の書き出しに成功するたびに、amm は同じ `logs/<yyyyMMdd>/` フォルダ内の `index.json` へ `sent_at`／コマンド1行目 (`FirstLine`)／対応するファイル名の 1 エントリを追記しなければならない (SHALL)。この処理は会話 JSON 本体の書き出しとは独立した失敗として扱い、`index.json` が破損（JSON パース不能）していても新規リストとして扱いを継続する。

#### Scenario: index.json が正常
- **WHEN** 同フォルダに既存の `index.json` があり正常にパースできる
- **THEN** amm は既存エントリのリストへ新エントリを追加し、全体を再シリアライズして上書き保存する

#### Scenario: index.json が壊れている
- **WHEN** `index.json` の内容が JSON として不正で `JsonException` が発生する
- **THEN** amm はエントリリストを空から再構築し、新規エントリのみを含む `index.json` として書き直す（会話 JSON 本体は既に書き出し済みのため失われない）

### Requirement: 独立した統計情報スイッチによる交換単位の集計
`StatsEnabled` が true の場合、amm はチャット記録とは独立に、コマンド送信〜出力静穏（`StatsQuietMs`=1200ミリ秒）までを 1 交換として計測し、`ChatStatsStore.RecordExchange` により `<workDir>/.amm/stats/<yyyyMMdd>/<プロファイル名(サニタイズ済み)>-<MDI名(サニタイズ済み)>.json` に累積記録しなければならない (SHALL)。1 ファイルは対象 MDI・当日分の累計 1 レコードであり、交換ごとに新規ファイルを作るのではなく既存レコードを読み込んで加算し上書きする。人間の応答時間 (`humanMs`) は直前の交換の応答完了時刻からこの送信までの経過時間とし、その MDI でのその日最初の送信など直前の応答完了時刻が無い場合は null（集計対象外）とする。

#### Scenario: 初回送信では人間の応答時間を計測しない
- **WHEN** amm 起動後、ある MDI でその日最初のコマンドが送信される
- **THEN** `_statsLastRespondedAtUtc` が null のため `humanMs` は null として渡され、`HumanSampleCount`/`HumanTotalMs` は加算されない

#### Scenario: 2回目以降の送信で人間の応答時間が加算される
- **WHEN** 同一 MDI で応答完了後に次のコマンドが送信される
- **THEN** amm は前回応答完了時刻から今回送信時刻までの経過ミリ秒を `HumanTotalMs`/`HumanSampleCount` に加算し、`HumanAvgMs` を再計算する

#### Scenario: チャット記録がOFFでも統計のみ動作する
- **WHEN** `ChatRecordEnabled` が false で `StatsEnabled` のみ true の MDI でコマンドを送信する
- **THEN** amm は `ChatRecorder` を一切生成せず、`ChatStatsStore.RecordExchange` による集計のみを実行する

### Requirement: 統計情報ダイアログでの日次一覧表示
amm は `ChatStatsDialog` を通じて、指定した作業ディレクトリ配下 `.amm/stats/<yyyyMMdd>/` にある全 MDI 分の集計ファイルを日付ピッカー（既定値は本日）で切り替えながら一覧表示しなければならない (SHALL)。一覧は MDI 名昇順（序数比較）でソートし、各行に指示回数・AI動作時間（累計/平均）・人間の応答時間（累計/平均、サンプル0件なら平均欄に "-"）を、1時間以上は `h:mm:ss`、未満は `m:ss` 形式で整形して表示する。

#### Scenario: 該当日の統計ファイルが存在しない
- **WHEN** 選択した日付に対応する `.amm/stats/<yyyyMMdd>/` フォルダが存在しない、またはファイルが1件もない
- **THEN** amm はグリッドを非表示にし「この日の統計情報はありません。」という空状態ラベルを表示する

#### Scenario: 日付ピッカー変更で再読み込み
- **WHEN** ユーザーが日付ピッカーの値を変更する、または「更新(&R)」ボタンを押す
- **THEN** amm はグリッドをクリアし、新しい日付の `LoadAll` 結果で再構築する
