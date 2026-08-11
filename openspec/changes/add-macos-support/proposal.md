## Why

amm は Tauri 採用時点（`UDR-amm-20260713T1037-ff3`）からクロスプラットフォーム対応を見据えていたが、これまで実質的に Windows 実機での検証・実装のみが進み、Mac/Linux は「検討中・未実装」のままだった（`UDR-amm-20260729T0158-a37` で開発フォーカスをWindowsからMacへ移す判断が確定済み）。`docs/design/cross-platform-feasibility.md`（2026-07-20更新）の実態調査により、Windows依存はTauri実装のうちごく一部（5箇所）に限定されることが判明しており、Mac版を実際に作り切る実行フェーズに移る。

## What Changes

- Tauri実装(`src/apps/Amm/src-tauri`)のWindows専用実装5箇所を、`cfg(windows)`/`cfg(target_os = "macos")`等でプラットフォーム分岐させ、Mac相当の実装を追加する:
  1. MCP IPCトランスポート: Named Pipe → macOSはUnix domain socket
  2. MCPゲートウェイの子プロセスツリー終了: Windows Job Object → macOSはプロセスグループ+シグナル(SIGTERM/SIGKILL)
  3. ペインのシステムメニュー拡張(`GetSystemMenu`/`AppendMenuW`): macOSにはシステムメニュー概念が無いため、ウィンドウ内UI(ペインのコンテキストメニュー等)への作り替え(**BREAKING**: Mac版のみ、Windows版のシステムメニューは変更しない)
  4. attention状態のタスクバー点滅(`FlashWindowEx`): macOSはDockアイコンのバウンス通知(`request_user_attention`相当)
  5. トレイ/前面化操作でのフォーカス奪取(`SetForegroundWindow`/`Window.Activate()`/`set_focus()`): macOSはフォーカススティール防止のため`osascript activate`(Apple Events経由)を使う実装に切り替え(過去のAvalonia版PoCで実機確認済みの制約、`reference/mac-avalonia-poc-lessons/README.md`参照)
- cargo-tauriのmacOSバンドル機能で `.app`/`.dmg` を生成するビルド・配布フローを新設する(Windows版の `tools/publish-tauri.cmd`/`tools/build-installer-tauri.cmd` に相当するMac版ツール一式)。
- 上記変更はすべてMac版のみに適用し、既存のWindows版のコードパス・動作・配布物は変更しない(ユーザーが後日Windows実機で再検証する前提のため)。
- Linux版は今回のスコープ外(将来の拡張として `cfg(unix)` 側で共有できる形は意識するが、Linux実機での検証・配布は行わない)。

## Capabilities

### New Capabilities
(なし。既存capabilityの要件をプラットフォーム分岐で拡張する形で対応する)

### Modified Capabilities
- `pane-management`: 「ペインのシステムメニュー拡張」要件をWindows専用の代替導線と位置付け、macOSでは同等の設定項目(名前変更/エディタ連携/フォントサイズ/AMM設定)をウィンドウ内UIで提供する要件を追加。「attentionのタスクバー点滅」要件にmacOS(Dockバウンス)の代替動作を追加。
- `tray-icon`: クリック操作による前面化(`SetForegroundWindow`+`Activate()`)にmacOS版の代替実装(`osascript activate`)を追加。
- `approval-hub`: Level 1 attention表示の「タスクバー点滅」要件にmacOS(Dockバウンス)の代替動作を追加。
- `mcp-server`: 「Named Pipe トランスポート」要件をプラットフォーム分岐させ、macOS(Unix domain socket)でも同一のJSON-RPCフレーミング・ハンドシェイクが成立する要件を追加。

`ps-module`(Amm.PowerShell)は対象外(2026-07-30ユーザー判断: Windows版のみの機能と割り切る、下記Impact参照)。

## Impact

- 影響コード: `src/apps/Amm/src-tauri/src/{mcp.rs, gateway.rs, lib.rs, native_ui.rs, editor_bridge.rs, bin/amm-mcp/pipe_client.rs}`。`src/modules/Amm.PowerShell/*.psm1`は対象外(Windows版のみの機能と割り切る)。
- 新規ビルドツール: Mac版インストーラ生成スクリプト(`tools/`配下、Windows版`*.cmd`に相当するシェルスクリプト)
- 依存: `cargo-tauri`のmacOSクロスビルド/バンドル機能、Apple Developer署名・notarization要否の確認(配布形態次第)
- ドキュメント: `docs/design/cross-platform-feasibility.md`(結論を反映しアーカイブ化検討)、`docs/build.md`、`CLAUDE.md`/`README.md`の対応プラットフォーム記載
- 既存のWindows版コードパス・`openspec/specs/`のWindows向け記述は変更しない(delta specでプラットフォーム分岐の要件を追加するのみ)
