# デプロイ / 配布

各 EXE/DLL の配布単位・同梱関係をまとめる。

## 成果物の配布単位
| 成果物 | 配布形態 |
|---|---|
| Amm.exe（本体） | インストーラに同梱 |
| amm-mcp.exe（MCP サーバ） | 本体に同梱（MCP クライアントから参照） |
| WebView2 ランタイム | 前提条件（ブートストラップで同梱検討） |

## パッケージング
- MSI（WiX）: `src/installer/wix/`（常駐・フック等で自由度が高い方式）。
- 最終配布物の出力先: `artifacts/packages/`（経緯: MSI 出力先移動 — git log 参照）。

## 署名
- 発行物 → パッケージの順に Authenticode 署名（`tools/` のスクリプトで一括化予定）。

> 詳細なビルド前提は `docs/build.md` を参照。
