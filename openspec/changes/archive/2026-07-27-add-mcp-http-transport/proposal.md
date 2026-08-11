## Why

amm の MCPゲートウェイは外部MCPサーバーをローカルの stdio 子プロセスとしてのみ起動・管理できる。しかし Obsidian の MCP プラグイン等、HTTP(Streamable HTTP)でリモート(または別プロセスとして常駐済み)公開される MCP サーバーが実在し、現状これらを一切登録できない。ユーザーが実際に運用している設定例(`https://127.0.0.1:27124/mcp/` + Bearer トークン)を機に、HTTP/リモート型 MCP サーバーへの接続をサポートする。

## What Changes

- `McpServerConfig` に transport 種別を追加(`stdio`(既定・後方互換)/`http`)。`http` は `url` / `headers`(任意ヘッダーのマップ、Authorization 等) / `skipTlsVerify`(既定 false、サーバーごとに証明書検証をスキップ可能) を持つ。
- ゲートウェイに HTTP MCP クライアント(`ManagedMcpHttpServer` 相当)を追加。プロセス起動・ジョブオブジェクト・再起動カウンタは不要。代わりに `initialize` → `tools/list` の HTTP POST ハンドシェイクで疎通確認し、以後は `tools/call` を都度 HTTP POST で転送する(MCP Streamable HTTP: 1リクエスト1レスポンスのJSON-RPC POSTを基本とし、`Mcp-Session-Id` レスポンスヘッダーが返された場合は後続リクエストに付与する)。
- 接続失敗・タイムアウト時は該当サーバーを `Error` 状態にする。stdio 側のような子プロセスクラッシュ検知/自動再起動の概念はないため、「クラッシュ検出と自動再起動」要件は stdio 限定であることを明記し、HTTP 側は簡易的なリトライ(次回 `tools/call` 時に未接続なら再ハンドシェイクを試みる)のみとする。
- ツールの集約(`{serverName}/{toolName}` 名前空間)・ゲートウェイツール呼び出しの転送・管理ダイアログでのステータス表示は transport 種別によらず共通のインターフェースで扱う(既存要件を維持、内部実装のみ分岐)。
- 管理UI(「MCPゲートウェイ設定」ダイアログのサーバー編集フォーム)に transport 選択(stdio/HTTP)を追加。HTTP 選択時は command/args/env の代わりに URL・ヘッダー(キーバリューリスト)・証明書検証スキップチェックボックスを表示する。
- HTTP クライアントに `reqwest`(rustls-tls, 非同期)を新規依存として追加。

## Capabilities

### New Capabilities

(なし — 既存 capability の拡張のみ)

### Modified Capabilities

- `mcp-gateway`: 外部MCPサーバー設定に transport 種別(stdio/http)を追加。起動ハンドシェイクとライフサイクル、クラッシュ検出と自動再起動の各要件を transport ごとに分岐させ、HTTP 版の疎通確認・エラー処理・(再接続の考え方)を新設する。ツール集約/呼び出し転送/ステータス表示は transport 非依存であることを明記する。

## Impact

- `src/apps/Amm/src-tauri/src/profile.rs`: `McpServerConfig` にフィールド追加(後方互換、`#[serde(default)]`)。
- `src/apps/Amm/src-tauri/src/gateway.rs`: HTTP transport 用の疎通・呼び出しロジック追加、`ManagedMcpProcess` を stdio/http 両対応にするか並行する構造体を設けるかは design.md で決定。
- `src/apps/Amm/src-tauri/Cargo.toml`: `reqwest` 依存追加。
- `src/apps/Amm/public/app.js`: MCPサーバー編集ダイアログに transport 選択UIを追加。
- `openspec/specs/mcp-gateway/spec.md`: 上記の要件差分。
- 旧 .NET 版(`src/apps/Amm`)は変更しない(参照専用、対象外)。
