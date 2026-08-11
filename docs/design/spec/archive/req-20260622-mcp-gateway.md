# req-20260622: MCP ゲートウェイ（外部 MCP サーバー集約）

- **ID**: req-20260622-mcp-gateway
- **状態**: Draft
- **関連 UDR**: -
- **更新日**: 2026-06-22

## 背景 / 目的

現在 amm は **MCP サーバー側**として機能し、MDI pane の CLI エージェントが
`amm-mcp.exe` 経由で `send_message` / `list_participants` 等のツールを呼ぶ。

この構造を拡張し、amm を **MCP ゲートウェイ（マルチプレクサ）** にする。
外部の MCP サーバー（ファイルシステム・ブラウザ操作・DB・コード実行等）を
amm が管理・起動し、そのツール群を MDI pane の全エージェントに透過的に公開する。

エージェントから見ると「amm というひとつの MCP サーバー」に接続するだけで、
amm が集約したすべてのツールが利用可能になる。

```
MDI pane の Claude Code / Codex
  └─ amm-mcp.exe ──Named Pipe──→ amm GUI (GatewayMcpServer)
                                       ├─ 組み込みツール (send_message 等)
                                       ├─ filesystem-mcp  (子プロセス stdio)
                                       ├─ browser-mcp     (子プロセス stdio)
                                       └─ custom-tool-X   (子プロセス stdio)
```

## 要求（What）

### R-1: ゲートウェイ設定
- `profiles.amm` またはアプリ設定 UI で「ゲートウェイ MCP サーバー」のリストを定義できる。
- 各エントリは `{ name, command, args[], env{} }` 形式（Claude Code の mcpServers 定義と同形）。
- amm 起動時に自動起動する / 任意のタイミングで手動起動するかを選択できる。

### R-2: 外部プロセスのライフサイクル管理
- amm が外部 MCP サーバープロセスを子プロセスとして起動・停止・再起動できる。
- amm 終了時にすべての子 MCP サーバープロセスを終了する。
- クラッシュ検出時に自動再起動（上限回数付き）できる。

### R-3: ツールの集約と名前空間
- 各外部 MCP サーバーの `tools/list` 結果を取得し、amm 組み込みツールと合わせて
  エージェントへの `tools/list` 応答に含める。
- ツール名衝突を避けるため、外部サーバーのツールには `<server-name>/<tool-name>` の
  プレフィックスを付ける（例: `fs/read_file`、`browser/navigate`）。
- `tools/call` を受けたら対応する子プロセスへ JSON-RPC を転送し、結果を返す。

### R-4: 既存インターフェースとの互換
- 既存の `send_message` / `list_participants` / `peek_queue` / `amm/notify` /
  `amm/approval` ツールは変更なく動作し続ける。
- `amm-mcp.exe` のコマンドラインインターフェースは変更しない。

### R-5: ステータス表示
- 管理下の MCP サーバーごとに 起動中 / 接続待ち / エラー のステータスを
  amm の UI（ステータスバー or 設定ダイアログ）で確認できる。

## 非対象（Out of scope）

- **HTTP/SSE 輸送層**: 本 spec は既存の Named Pipe 輸送を前提とする。
  HTTP ホスト化（Pattern B）は別 spec で扱う。
- **リモート MCP サーバー**（TCP/WSS 接続）: 初期実装は localhost 子プロセスのみ。
- **ツール引数の検証/サニタイズ**: 転送先 MCP サーバーに委譲する。
- **MCP サーバー間の認証**: 外部サーバーは同一ユーザー OS プロセスとして信頼する。
- **ストリーミングレスポンス** (notifications/progress): 初期実装は非対応。

## 受け入れ基準

- [ ] `profiles.amm` にゲートウェイ設定を追加し、amm 起動時に外部 MCP サーバーが起動する
- [ ] CLI エージェントから `tools/list` を呼ぶと外部 MCP サーバーのツールが列挙される
- [ ] CLI エージェントから `fs/read_file` 等を呼ぶと外部サーバーに転送され結果が返る
- [ ] 外部 MCP サーバーがクラッシュしてもエージェントへの接続が維持される
- [ ] amm 終了時に子 MCP サーバープロセスがすべて終了する
- [ ] 既存の `send_message` / `amm/approval` 等は従来どおり動作する

## 設計メモ（実装時に詳細化）

### 主要コンポーネント

```
GatewayMcpServer          既存 McpPipeServer の上位ラッパー
  ├─ BuiltinToolHandler   既存ツール群をそのまま移管
  ├─ GatewayToolRouter    tools/call をプレフィックスで振り分け
  └─ ManagedMcpProcess[]  外部サーバー 1 プロセス = 1 インスタンス
       ├─ Process (stdin/stdout stdio JSON-RPC)
       ├─ ToolManifest (tools/list のキャッシュ)
       └─ RestartPolicy
```

### 起動シーケンス
1. `GatewayMcpServer` 初期化時に設定済み外部サーバーを子プロセス起動
2. 各子プロセスに `initialize` + `tools/list` を送信してマニフェストをキャッシュ
3. Named Pipe を開放してエージェント接続を受け付ける
4. 以後の `tools/list` はキャッシュ済みマニフェストをマージして返す

### 代替案（将来検討）
- **HTTP/SSE 輸送 (Pattern B 統合)**: `amm-mcp.exe` を廃止し amm GUI 内で
  HTTP MCP サーバーを直接ホストする。`GatewayMcpServer` はそのまま流用可能。
- **MCP Proxy 標準化**: IETF/WG の MCP 仕様に proxy/composition の定義が追加されれば
  その仕様に準拠する形に移行する。

## 備考

- 現時点では **Draft 置き** とし、ユースケース（どの MCP サーバーを集約したいか）が
  固まった段階で Reviewed → 実装に入る。
- 関連: [docs/design/architecture.md](../architecture.md)、
  [src/apps/Amm/Core/Mcp/McpPipeServer.cs](../../../src/apps/Amm/Core/Mcp/McpPipeServer.cs)
