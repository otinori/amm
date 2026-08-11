## Context

`gateway.rs` の `McpServerConfig`/`ManagedMcpProcess`/`GatewayManager` は「amm がローカルで spawn した stdio 子プロセス」を前提に設計されている(`tokio::process::Child` の起動・stdin/stdout パイプ・kill-on-close ジョブオブジェクト・`Child::wait()` を軸にした再起動ループ)。実際にユーザーが使いたいサーバー(Obsidian Local REST API 経由の MCP プラグイン)は `https://127.0.0.1:27124/mcp/` で公開される HTTP サーバーであり、amm 側から起動も終了もできない別プロセスである。この形の設定例(`{"type":"http","url":"...","headers":{"Authorization":"Bearer ..."}}`)は MCP の「Streamable HTTP」トランスポート(2025-03-26 以降の MCP 仕様。単発 JSON-RPC を `POST` で送り、サーバーが `Mcp-Session-Id` レスポンスヘッダーを返した場合は以後のリクエストに付与する)に相当する。

## Goals / Non-Goals

**Goals:**
- `McpServerConfig` に stdio/http の transport 種別を追加し、既存の stdio 専用ファイル(`profiles.amm` / `%LOCALAPPDATA%\amm\mcp-servers.json`)との後方互換を保つ。
- HTTP transport のハンドシェイク(`initialize` → `tools/list`)・`tools/call` 転送を実装する。
- サーバーごとに TLS 証明書検証スキップを選択可能にする(既定は検証する = false)。
- 既存の「ツールの集約と名前空間プレフィックス」「ゲートウェイツール呼び出しの転送」「管理サーバーのステータス表示」は transport 非依存の共通インターフェースのまま維持する(呼び出し側の `GatewayManager` の公開APIは変えない)。
- 管理ダイアログでサーバーを stdio/http のどちらとしても新規作成・編集できるようにする。

**Non-Goals:**
- SSE(サーバー→クライアントの非同期通知ストリーム)の購読は実装しない。今回のスコープは「呼び出し都度の POST」で完結する範囲(`tools/list`・`tools/call`)のみ。将来的にサーバー側 push 通知(`notifications/*`)が必要になったら別途拡張する。
- stdio 側のプロセス管理(ジョブオブジェクト・再起動カウンタ)には一切手を入れない。
- OAuth 等の動的認可フロー(MCP の Authorization 仕様)は実装しない。ヘッダーは設定ファイルに静的に書かれた値(Bearer トークン等)をそのまま付与するのみ。
- クライアント証明書(mTLS)は対象外。

## Decisions

### 1. スキーマ: `McpServerConfig` はフラット構造のまま `type` 判別フィールドを追加

```rust
#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize, Default)]
#[serde(rename_all = "lowercase")]
pub enum McpTransportKind {
  #[default]
  Stdio,
  Http,
}

pub struct McpServerConfig {
  pub name: String,
  #[serde(default, rename = "type")]
  pub transport: McpTransportKind,
  #[serde(default)]           // stdio 必須・http では無視
  pub command: String,
  #[serde(default)]
  pub args: Vec<String>,
  #[serde(default)]
  pub env: Option<HashMap<String, String>>,
  #[serde(default = "default_true")]
  pub auto_start: bool,
  #[serde(default = "default_max_restarts")]
  pub max_restarts: u32,       // http では無視(再起動ループを持たないため)
  #[serde(default)]           // http 必須
  pub url: Option<String>,
  #[serde(default)]
  pub headers: Option<HashMap<String, String>>,
  #[serde(default)]
  pub skip_tls_verify: bool,
}
```

代替案として「stdio/http を enum のバリアントに完全分離する(`#[serde(tag="type")] enum McpServerConfig { Stdio{...}, Http{...} }`)」も検討したが、`mcp_server_key`/`merge_imported_mcp_servers`/`McpServersExportFile`/フロントエンドの編集ダイアログなど `name`/`command` に直接アクセスする既存コードが多く、enum 化すると全箇所の書き換えが必要になり変更範囲が肥大化する。`command`/`url` を両方optionalなフラットフィールドとして持たせ、`transport` で意味を切り替える方が既存コードへの影響を最小化できる。ユーザーが実際に使っている JSON の `"type":"http"` ともそのまま一致する。

`command` を非 Option の `String`(デフォルト空文字)のまま残すのは、stdio 側の型シグネチャ(`gateway.rs`全体、`build_chcp_wrapped_command`等は関係ないが `ManagedMcpProcess::config.command` への直アクセス多数)を一切変えずに済ませるため。http 設定では単に空文字のまま無視される。

### 2. `GatewayManager` は enum ディスパッチで stdio/http を束ねる

```rust
enum McpServerHandle {
  Stdio(Arc<ManagedMcpProcess>),
  Http(Arc<ManagedMcpHttpServer>),
}
```

`GatewayManager.processes: Vec<Arc<ManagedMcpProcess>>` を `Vec<McpServerHandle>` に変更し、`name()`/`status()`/`tool_count()`/`last_error()`/`tools_snapshot()`/`call_tool()` を持つ薄い委譲メソッドを `McpServerHandle` に生やす。`aggregated_tools`/`call_tool`/`server_infos`/`is_gateway_tool`/`find` は現状の実装そのまま(ループ本体だけ `match` 一つ挟む)で流用できる。

`async-trait` 等の新規依存を追加してトレイトオブジェクト化する案より、2 バリアントの enum + 手書き委譲の方がこの規模では素直で依存も増えない。

### 3. `ManagedMcpHttpServer`: プロセス監視ではなく「必要になったら繋ぎ直す」

stdio の `supervise`(`launch_and_handshake` → `Child::wait()` → 再起動ループ)に相当する「常駐タスク」は HTTP には作らない。理由: 監視対象の OS プロセスが存在しない(Obsidian 側は amm と無関係に起動・終了する)ため、「クラッシュ検出」という概念自体が成立しない。代わりに:

- `auto_start = true` のサーバーは `start_auto_start_servers` 相当のタイミングで一度だけ `connect()`(`initialize`→`tools/list`)を試みる。成功すれば `Running`、失敗すれば `Error` + `last_error` セット(stdio の「起動失敗時は再起動ループに入らない」ケースと同じ挙動)。
- `call_tool` 呼び出し時に現在の状態が `Running` でなければ、その場で `connect()` を再試行してから `tools/call` を送る(1 回のみ、バックグラウンドの再試行ループは持たない)。これにより「Obsidian 側アプリを後から起動した」ケースでも、次にツールを呼んだ時点で自然に復帰する。
- `max_restarts`/`restart_count` に相当する概念は持たない(HTTP側はこのフィールドを無視する)。

### 4. HTTP クライアント: `reqwest`(rustls-tls, 動的 danger flag)をサーバーごとに1インスタンス

- 新規依存: `reqwest = { version = "0.12", default-features = false, features = ["json", "rustls-tls"] }`。`native-tls`(OpenSSL/Schannel)ではなく `rustls-tls` を選ぶ理由は、`danger_accept_invalid_certs` を使う際に Windows 証明書ストアに依存しない自己完結ビルドにできるため、かつ本プロジェクトの他の Windows 依存(`windows` crate)と衝突しない。
- `skip_tls_verify` は `McpServerConfig` 単位の設定なので、`reqwest::Client` も `ManagedMcpHttpServer` ごとに1つ構築する(`ClientBuilder::danger_accept_invalid_certs(skip_tls_verify)`)。共有クライアントにしない。
- タイムアウトは `HANDSHAKE_TIMEOUT`(10秒、stdio と共通の定数を流用)を `initialize`/`tools/list`/`tools/call` すべてに適用する。
- リクエストヘッダー: `Content-Type: application/json`, `Accept: application/json, text/event-stream`(Streamable HTTP 仕様が要求する Accept ヘッダー)、`config.headers` の中身をそのまま追加(Authorization 等)。レスポンスの `Mcp-Session-Id` ヘッダーを受け取ったら `ManagedMcpHttpServer` に保持し、以後の全リクエストに `Mcp-Session-Id` として付与する(仕様上任意。返さないサーバーもあるため `Option<String>` で保持し、無ければ付けない)。
- レスポンスボディは `Content-Type` が `text/event-stream` の場合は SSE 1 フレーム分の `data:` 行から JSON を取り出し、それ以外は生 JSON としてそのままパースする(SSE の「ストリーム購読」自体はしない = Goals の非対応範囲だが、1レスポンスとして返る単発 SSE フレームだけは読めるようにしておく。MCP 実装によっては `tools/call` の応答も SSE で返す場合があるため)。

### 5. UI: transport 選択で表示フィールドを切り替え

`app.js` の MCP サーバー編集ダイアログ(`openMcpGatewayDialog` 内のサーバー追加/編集フォーム)に `<select>` で stdio/http を選ばせ、選択に応じて command/args/env の行と url/headers/skipTlsVerify の行を出し分ける。headers はコマンド引数編集と同様のキー・バリュー行リストUIとする。エクスポート/インポート(`McpServersExportFile`)は `McpServerConfig` をそのままシリアライズしているため追加フィールドの対応は不要(自動的に含まれる)。

## Risks / Trade-offs

- [Risk] `skip_tls_verify: true` を誤って本番向けの公開エンドポイントに設定すると中間者攻撃に対して無防備になる → Mitigation: 既定値は false(検証する)。UI 上のチェックボックスに警告文言(「自己署名証明書を使うローカルサーバー向け。信頼できないネットワーク越しの接続には使わない」)を添える。
- [Risk] `Mcp-Session-Id` を返さない/`initialize` の応答形式が微妙に異なる MCP HTTP 実装がある可能性(仕様のバージョン差) → Mitigation: セッションIDはあれば使う・無ければ使わない、程度の寛容な扱いに留め、致命的にしない。今回のスコープは Obsidian 側実装で実地確認する。
- [Risk] HTTP 側に「再起動カウンタ」的な保護がないため、疎通不能なサーバーに対して `tools/call` の度に毎回 `connect()` を試みてレイテンシが乗る → Mitigation: 許容(呼び出し頻度は低く、失敗時のみ発生する遅延であり、常駐監視タスクを持たない設計とのトレードオフとして許容する)。
- [Trade-off] `reqwest` を新規依存として追加するとバイナリサイズ・ビルド時間が増える → 許容(MCP HTTP クライアントとして手書き実装するより安全・保守コストが低い)。

## Open Questions

- Obsidian 側 MCP サーバーの実際のレスポンス形式(SSE か生 JSON か、`Mcp-Session-Id` を返すか)は実機接続でしか確認できない。実装後、ユーザー環境での動作確認が必要(このプロジェクトの制約上、Linux コンテナ環境からは検証不可)。
