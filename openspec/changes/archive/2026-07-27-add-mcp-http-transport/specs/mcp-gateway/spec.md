## MODIFIED Requirements

### Requirement: 外部 MCP サーバー設定
amm は「AMM 共通 (グローバル)」と「このファイル固有」の 2 グループで外部 MCP サーバー定義を保持し、`McpGatewayDialog` から追加・編集・削除・並べ替えできなければならない (SHALL)。各エントリは `name` と transport 種別 (`type`、既定値 `stdio`) を持つ。`type=stdio` のエントリは `command`, `args[]`, `env{}`, `autoStart`, `maxRestarts` を持ち、`maxRestarts` の既定値は 3、`autoStart` の既定値は true とする。`type=http` のエントリは `url`, `headers{}` (任意), `skipTlsVerify` (既定値 false), `autoStart` (既定値 true) を持つ。

#### Scenario: グローバルとファイル固有の並存
- **WHEN** グローバル設定とファイル固有設定の双方に MCP サーバーエントリが存在する
- **THEN** `GatewayManager` はその両方 (stdio・http 双方の transport を含む) を管理対象として起動・集約する

#### Scenario: type 省略時は stdio 扱い
- **WHEN** 既存の `mcp-servers.json` / `profiles.amm` のように `type` フィールドを持たないエントリを読み込む
- **THEN** `type=stdio` として扱われ、従来通り `command` で子プロセスが起動される

#### Scenario: http エントリの追加
- **WHEN** `McpGatewayDialog` でサーバーを新規作成し transport に「HTTP」を選ぶ
- **THEN** フォームは `command`/`args`/`env` の代わりに `url`・ヘッダー一覧・「証明書検証をスキップ」チェックボックスを表示する

#### Scenario: 保存後は再起動が必要である旨の通知
- **WHEN** `McpGatewayDialog` で(stdio・http いずれかを問わず)サーバー定義の追加・編集・削除・並べ替えを行い OK を押す
- **THEN** 設定はただちに保存される(グローバルはファイルへ即時書き込み、ファイル固有はメモリ上)が、実行中の `GatewayManager` は再構築されないため、amm.exe の再起動が必要である旨がダイアログを閉じた後に通知される

### Requirement: 自動起動サーバーの一括起動
`GatewayManager` の起動処理は `autoStart=true` のサーバーのみを並行して起動・接続しなければならない (SHALL)。`autoStart=false` のサーバーは自動起動されない。stdio エントリは子プロセスを起動し、http エントリは `initialize`→`tools/list` の疎通確認を行う。

#### Scenario: amm 起動時の自動起動
- **WHEN** amm GUI が起動しゲートウェイの起動処理が呼ばれる
- **THEN** `autoStart=true` の全エントリが並行して起動・接続され (stdio は子プロセス起動、http は疎通確認)、`autoStart=false` のエントリは何もしない

### Requirement: 外部プロセスの起動ハンドシェイクとライフサイクル
`type=stdio` のサーバーについて、`ManagedMcpProcess` は子プロセスを stdio (stdin/stdout) の JSON-RPC で起動し、`initialize` に続けて `tools/list` を送信してツールマニフェストをキャッシュしなければならない (SHALL)。amm 終了時は全子プロセスを `Kill(entireProcessTree: true)` する。この要件は `type=http` のサーバーには適用されない (HTTP transport のハンドシェイクは別要件で定義する)。

#### Scenario: 起動シーケンス
- **WHEN** `type=stdio` のサーバーで `StartAsync` が呼ばれる
- **THEN** プロセスは `Starting` → (initialize 成功後) `Running` の順に遷移し、`Tools` に `tools/list` の結果がキャッシュされる

#### Scenario: 起動失敗
- **WHEN** `initialize` が応答しない、またはプロセス起動自体が失敗する
- **THEN** ステータスは `Error` になり `LastError` に理由が記録され、プロセスは破棄される

### Requirement: クラッシュ検出と自動再起動
`type=stdio` のサーバーについて、子プロセスが予期せず終了した場合、amm は `MaxRestarts` 回まで自動的に再起動しなければならない (SHALL)。上限に達した場合はステータスを `Error` とし再起動を停止する。この要件は `type=http` のサーバーには適用されない (監視対象の子プロセスが存在しないため。HTTP transport の再接続挙動は別要件で定義する)。

#### Scenario: 上限内での再起動
- **WHEN** `type=stdio` のサーバーの子プロセスがクラッシュし再起動回数が `MaxRestarts` 未満である
- **THEN** 1 秒後に自動的に再起動が試みられ、保留中だった JSON-RPC 呼び出しはすべてキャンセルされる

#### Scenario: 再起動上限到達
- **WHEN** 再起動回数が `MaxRestarts` に達した状態で再度クラッシュする
- **THEN** ステータスは `Error` に固定され、それ以上の自動再起動は行われない

## ADDED Requirements

### Requirement: HTTP サーバーの疎通ハンドシェイク
`type=http` のサーバーについて、amm は `url` に対して MCP Streamable HTTP の JSON-RPC を `POST` で送り、`initialize` に続けて `tools/list` を送信してツールマニフェストをキャッシュしなければならない (SHALL)。`headers` に設定された値 (Authorization 等) を全リクエストに付与する。応答に `Mcp-Session-Id` ヘッダーが含まれる場合、以後の同サーバーへのリクエストにそのまま付与しなければならない (SHALL)。

#### Scenario: 疎通成功
- **WHEN** `url` へ `initialize` を送信し正常応答が返る
- **THEN** 続けて `tools/list` を送信し、ステータスは `Running` になり `Tools` に結果がキャッシュされる

#### Scenario: 疎通失敗
- **WHEN** `initialize` がタイムアウトする、または HTTP エラー応答が返る
- **THEN** ステータスは `Error` になり `LastError` に理由が記録される

#### Scenario: セッションIDの付与
- **WHEN** `initialize` の応答に `Mcp-Session-Id` ヘッダーが含まれていた
- **THEN** 同サーバーへの以後の `tools/list`/`tools/call` リクエストに同じ `Mcp-Session-Id` ヘッダーが付与される

### Requirement: HTTP サーバーへのツール呼び出しと再接続
`type=http` のサーバーへの `tools/call` 転送は、対象サーバーが `Running` でない場合にその場で疎通ハンドシェイクを再試行してから呼び出さなければならない (SHALL)。再試行にも失敗した場合はエラーを返す。stdio のような再起動回数上限・自動再起動ループは持たない。

#### Scenario: 停止していたサーバーへの呼び出しで自動復帰
- **WHEN** `Error` 状態の http サーバー宛てのゲートウェイツールが呼ばれ、再接続を試みたところ疎通に成功した
- **THEN** ステータスは `Running` になり、そのまま `tools/call` が転送されて結果が返る

#### Scenario: 再接続にも失敗
- **WHEN** `Error` 状態の http サーバー宛てのゲートウェイツールが呼ばれ、再接続にも失敗した
- **THEN** ステータスは `Error` のままエラーが呼び出し元に返る

### Requirement: TLS 証明書検証のスキップ設定
`type=http` のサーバーごとに `skipTlsVerify` (既定値 false) を設定でき、true の場合はその HTTPS 接続に限り証明書検証エラーを無視して通信しなければならない (SHALL)。他の http サーバーの証明書検証には影響しない。

#### Scenario: 自己署名証明書のローカルサーバーへの接続
- **WHEN** `skipTlsVerify=true` のサーバーが自己署名証明書を提示する
- **THEN** 証明書検証エラーにならず疎通ハンドシェイクが継続される

#### Scenario: 既定 (false) では証明書エラーで接続失敗
- **WHEN** `skipTlsVerify` を設定していない (既定 false) サーバーが不正な証明書を提示する
- **THEN** 疎通ハンドシェイクは証明書エラーとして失敗し、ステータスは `Error` になる
