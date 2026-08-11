## 1. スキーマ拡張 (profile.rs)

- [x] 1.1 `McpTransportKind` enum (`Stdio`(既定)/`Http`, `#[serde(rename_all="lowercase")]`) を追加
- [x] 1.2 `McpServerConfig` に `transport`(`#[serde(default, rename="type")]`), `url: Option<String>`, `headers: Option<HashMap<String,String>>`, `skip_tls_verify: bool`(`#[serde(default)]`) を追加。`command`/`max_restarts` は既存のまま(http では無視)
- [x] 1.3 `mcp_server_key`/`merge_imported_mcp_servers`/`McpServersExportFile` が transport 追加後も正しく動くことをユニットテストで確認(既存テストの調整のみで新規ロジックは不要のはず)
- [x] 1.4 stdio/http 双方の設定を含む JSON を読み込むユニットテストを追加(`type`省略時stdio扱い、`type:"http"`でurl/headers/skipTlsVerifyが読めること)
- [x] 1.5 `cargo build` / `cargo test` で確認

## 2. HTTP MCP クライアント (gateway.rs)

- [x] 2.1 `Cargo.toml` に `reqwest = { version = "0.12", default-features = false, features = ["json", "rustls-tls"] }` を追加
- [x] 2.2 `ManagedMcpHttpServer` 構造体を追加(`name`, `config`, `status`, `tools`, `last_error`, `session_id: Mutex<Option<String>>`, `client: reqwest::Client`)。`skip_tls_verify` に応じた `reqwest::Client` を構築するコンストラクタを実装
- [x] 2.3 `initialize`→`tools/list` の HTTP POST ハンドシェイク(`connect_http()`)を実装。`headers` 付与・`HANDSHAKE_TIMEOUT` でのタイムアウト・`Mcp-Session-Id` レスポンスヘッダーの保存・`Content-Type: text/event-stream` 応答時の SSE 1フレームパース(`data:` 行抽出)を含む
- [x] 2.4 `GatewayManager::call_tool` の http 分岐に実装: `Running` でなければ `connect_http()` を1回再試行してから `tools/call` を POST。失敗時はエラーを返す(自動再起動ループなし)
- [x] 2.5 `status()`/`tool_count()`/`last_error()` を `ManagedMcpProcess` と同じシグネチャで実装

## 3. GatewayManager の統合

- [x] 3.1 `McpServerHandle` enum(`Stdio(Arc<ManagedMcpProcess>)`/`Http(Arc<ManagedMcpHttpServer>)`)を追加し、`name`/`status`/`tool_count`/`last_error`/`tools_snapshot`/`auto_start` を委譲する薄いメソッド群を実装
- [x] 3.2 `GatewayManager.processes` の型を `Vec<McpServerHandle>` に変更、`GatewayManager::new` で `config.transport` に応じて `Stdio`/`Http` を振り分けて構築
- [x] 3.3 `start_auto_start_servers` で http エントリは `connect_http()` を(stdio と同様に)`tauri::async_runtime::spawn` 経由で並行実行するよう分岐
- [x] 3.4 `aggregated_tools`/`call_tool`/`server_infos`/`is_gateway_tool`/`find` を `McpServerHandle` 経由に書き換え(公開シグネチャは変えない)
- [x] 3.5 既存の `gateway.rs` 内ユニットテスト(`config()`ヘルパー等)を新スキーマに追従させ、http サーバーを含むテストケース(`is_gateway_tool`、`call_tool`が到達不能な http サーバーで None を返すこと)を追加
- [x] 3.6 `cargo build` / `cargo test` で確認(98/98 pass)

## 4. UI (MCPゲートウェイ設定ダイアログ)

- [x] 4.1 サーバー追加/編集フォームに transport 選択(`<select>`: stdio/http)を追加し、選択に応じて command/args/env 行 と url/headers/skipTlsVerify 行を出し分ける
- [x] 4.2 headers のキー・バリュー行編集UI(env 編集UIと同じ `joinEnvDisplay`/`parseEnvInput` の KEY=VALUE テキストエリアを流用)を実装
- [x] 4.3 「証明書検証をスキップ」チェックボックスに警告文言(自己署名証明書のローカルサーバー向け、信頼できないネットワークでは使わない旨)を添える
- [x] 4.4 一覧表示(ステータスアイコン・ツール数)が http エントリでも正しく表示されることを確認(`cfg.type === 'http'` で command/args の代わりに url を表示するよう分岐)
- [x] 4.5 `node --check public/app.js` で構文確認
- [x] 4.6 `openMcpGatewayDialog` の OK 押下後(保存成功後)に「amm.exe の再起動が必要」である旨を通知する(既存の GatewayManager は保存後も再構築されない仕様のため。stdio/http いずれのエントリの追加・編集・削除・並べ替えでも共通)

## 5. 動作確認

- [x] 5.1 `cargo build -j 2` / `cargo test -j 2` が全て通ることを確認(98/98 pass)
- [x] 5.2 実機確認(2026-07-27完了、Windows実機・実際のObsidian Local REST APIサーバーに接続): `amm.exe`を実際に起動し、実機の`%LOCALAPPDATA%\amm\mcp-servers.json`(`tests/sample_mcp.json`相当、`type:"http"`)経由でObsidianへ疎通・`gateway_server_infos`で`status:"running", toolCount:16`を確認・`amm-mcp.exe --bridge`経由で`tools/list`(16件の`obsidian/*`ツールが正しい名前空間プレフィックスで集約表示)・`tools/call`(`obsidian/vault_list`を実行し実際のvaultファイル一覧が返る)まで実データで確認。
  **⚠️ 副次的に発見・修正した実環境バグ**: 実機の`%LOCALAPPDATA%\amm\mcp-servers.json`に保存されていたObsidianエントリのURLが`https://127.0.0.1:27123/mcp/`だったが、Obsidian Local REST APIプラグインの規約上27123は平文HTTPポート・27124が暗号化HTTPSポートであり、`https://`スキームと27123の組み合わせは接続不能(TLSハンドシェイク自体が失敗)。amm自体のバグではなく設定ファイルの記載ミスだったが、これが原因でこの機能はおそらく一度も実際に接続成功したことがなかったと推測される。
  **訂正(ユーザー指摘)**: 初回対応時は`https://127.0.0.1:27124/mcp/`(暗号化ポート+`skipTlsVerify:true`)に修正して疎通確認したが、ユーザーから「27123のHTTP接続で繋がるはず、HTTPを評価していない」との指摘を受け再検証。`http://127.0.0.1:27123/mcp/`(平文HTTP、TLS不要)への直接POSTでも同一のMCPエンドポイントが正常応答することを確認し、こちらへ最終修正した(`skipTlsVerify`も不要になったため`false`に戻した)。ループバック接続のみでTLSハンドシェイクのオーバーヘッドも無く、こちらがより単純な構成。amm側の実装(`gateway.rs`)は`reqwest`が両スキームを標準サポートするため、http/https いずれのURLでも変更なく動作することも確認できた。
- [x] 5.3 実機確認(2026-07-27): 上記と同時に、`tests/e2e-tauri/fixture-mcp-server.js`(依存なしのstdio JSON-RPCフィクスチャ)を`mcp-servers.json`へ`type:"stdio"`で並記して起動、`gateway_server_infos`でhttp(obsidian)/stdio(fixture)双方が同時に`running`であることを確認、stdioサーバーの`echo`ツールも`tools/call`で正常応答することを確認(混在構成での回帰なし)。検証用のstdioエントリは確認後に`mcp-servers.json`から削除済み(obsidianエントリのポート修正のみ実環境に残した)。
