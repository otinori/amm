## ADDED Requirements

### Requirement: mdi/open による MDI ウィンドウのプログラム的オープン
`mdi/open` MCP ツール (および直接 RPC `amm.openWindow`) は `command` または `profile_name` のいずれか一方を必須パラメータとして受け取り、新しい TerminalChildForm を作成して一意な `session_id` (GUID) を返さなければならない (SHALL)。`args` / `title` / `workingDirectory` は任意で、`profile_name` 指定時は既存プロファイルの設定 (nickname 等) を継承する。

#### Scenario: command 指定での一時起動
- **WHEN** `command="claude"` を指定して `mdi/open` を呼ぶ
- **THEN** 新しい TerminalChildForm が起動し `{ session_id }` が返る

#### Scenario: command と profile_name がともに未指定
- **WHEN** `command` と `profile_name` のどちらも指定せずに呼ぶ
- **THEN** `-32602` (Invalid params) のエラーが返り、ウィンドウは開かれない

### Requirement: mdi/close による MDI ウィンドウのプログラム的クローズ
`mdi/close` MCP ツール (および直接 RPC `amm.closeWindow`) は必須パラメータ `session_id` と任意パラメータ `force` を受け取り、対象ウィンドウを閉じなければならない (SHALL)。`force=true` の場合は確認ダイアログを表示せずに閉じる。`session_id` が見つからない場合は例外を投げず `{ error }` を返す。

#### Scenario: 存在しない session_id
- **WHEN** 既に手動で閉じられた、または存在しない `session_id` で `mdi/close` を呼ぶ
- **THEN** 例外は発生せず `{ error: "session not found: ..." }` 相当の応答が返る

#### Scenario: force による強制クローズ
- **WHEN** `force=true` で `mdi/close` を呼ぶ
- **THEN** ユーザー確認ダイアログなしに即座にウィンドウが閉じる

### Requirement: mdi/wait_state によるブロッキング状態待機
`mdi/wait_state` MCP ツール (および直接 RPC `amm.waitState`) は `session_id` と `target_state` (`"idle"` または `"attention"`) を必須とし、`timeout_ms` (既定 300000 = 5 分) の間、対象セッションが目標状態に到達するまでレスポンスを保留しなければならない (SHALL)。到達時は `{ state, elapsed_ms }` を、タイムアウト時は `{ state: "timeout", elapsed_ms }` を返し、クラッシュしない。

#### Scenario: idle 到達での解放
- **WHEN** 対象セッションが `hook` (amm/notify) 経由で `idle` 状態へ遷移する
- **THEN** 保留中の `mdi/wait_state` 呼び出しが即座に `{ state: "idle", elapsed_ms }` で解決される

#### Scenario: タイムアウト
- **WHEN** `timeout_ms` 経過しても目標状態に到達しない
- **THEN** `{ state: "timeout", elapsed_ms }` が返り、待機は正常終了する (例外にならない)

#### Scenario: セッションクローズによる解放
- **WHEN** 待機中に対象セッションがクローズされる (ユーザー操作またはプログラムからの `mdi/close`)
- **THEN** `WaitBroker.ReleaseBySession` により保留中の全 wait が `"timeout"` 扱いで解放される

### Requirement: WaitBroker によるセッション単位の待機管理
`WaitBroker` は `session_id` と hook 通知用 `notifyToken` の対応関係を保持し、同一セッションに対する複数の並行 `RegisterWait` 呼び出しをそれぞれ独立に解放できなければならない (SHALL)。`ResolveByToken` は `target_state` が一致する pending のみを解決する。

#### Scenario: 目標状態が異なる複数 wait の共存
- **WHEN** 同一セッションに対し `target_state="idle"` と `target_state="attention"` の wait が同時に登録されている
- **THEN** `attention` への遷移通知が来た場合、`attention` を待つ wait のみが解決され `idle` を待つ wait は保留され続ける

### Requirement: MCP ツールと PowerShell cmdlet の意味論一致
Amm.PowerShell モジュールの `Open-AmmWindow` / `Close-AmmWindow` / `Wait-AmmIdle` cmdlet は、同名の Named Pipe 直接 RPC (`amm.openWindow` / `amm.closeWindow` / `amm.waitState`) を呼び出し、MCP ツール版と同一のパラメータ意味論・戻り値構造を提供しなければならない (SHALL)。

#### Scenario: プロファイル名によるオープン
- **WHEN** PowerShell から `Open-AmmWindow -ProfileName "Claude Code"` を実行する
- **THEN** MCP 経由の `mdi/open` に `profile_name` を渡した場合と同じ `AmmSession` (session_id を含む) が返る

#### Scenario: nickname 指定での待機
- **WHEN** `Wait-AmmIdle -Nickname claude` を実行する
- **THEN** cmdlet は内部で `list_participants` により nickname から `session_id` を解決してから `amm.waitState` を呼ぶ
