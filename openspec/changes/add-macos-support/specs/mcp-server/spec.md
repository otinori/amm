## MODIFIED Requirements

### Requirement: Named Pipe トランスポート
amm GUI は MCP/JSON-RPC サーバを、OSごとの既定トランスポート上に常駐させなければならない (SHALL)。Windows上では `\\.\pipe\amm-mcp-{Environment.UserName}` という既定名の Named Pipe を使い、パイプ ACL は現在の Windows ユーザーにのみ `FullControl` を付与し、同一ログオンユーザーのプロセスのみが接続できるようにしなければならない (SHALL)。macOS(および将来のUnix系)上では `$TMPDIR/amm-mcp-{uid}.sock` という既定パスの Unix domain socket を使い、ソケットファイルのパーミッションを `0600`、親ディレクトリを `0700` に設定して同一ユーザーのプロセスのみが接続できるようにしなければならない (SHALL)。トランスポートによらず、フレーミングは NDJSON (1 行 = 1 JSON-RPC メッセージ) とする (SHALL)。

#### Scenario: 同一ユーザーの複数プロセスが同時接続する
- **WHEN** 複数の `amm-mcp` プロセスが同時に同じトランスポート(Named PipeまたはUnix domain socket)へ接続する
- **THEN** サーバは接続ごとに独立したセッション (`HandleSessionAsync`) を非同期に処理し、1 セッションのブロッキング処理 (例: 許可待ち) が他のセッションの応答を妨げない

#### Scenario: 改行なしの巨大ペイロード
- **WHEN** クライアントが改行区切りなしで 1 MiB を超えるデータを送信する
- **THEN** サーバはその行を破棄しセッションを切断する (メモリ枯渇 DoS を防ぐ)

#### Scenario: macOSでのソケットファイルのパーミッション
- **WHEN** macOS上でamm GUIが起動しUnix domain socketを作成する
- **THEN** ソケットファイルのパーミッションが`0600`、親ディレクトリのパーミッションが`0700`になり、他ユーザーから接続・読み取りができない
