## REMOVED Requirements

### Requirement: mdi/open による MDI ウィンドウのプログラム的オープン
**Reason**: MDI(複数子ウィンドウ)方式を廃止し単一ウィンドウ内ペイン管理方式へ移行するため(`UDR-amm-20260713T0447-98f`)。
**Migration**: `pane-management` capability の「pane/open によるペインのプログラム的オープン」requirement へ、ツール名を `mdi/open` → `pane/open`、RPC名を `amm.openWindow` → `amm.openPane` にリネームした上で移行する。パラメータ意味論・戻り値構造(`session_id`)は変更しない。

### Requirement: mdi/close による MDI ウィンドウのプログラム的クローズ
**Reason**: 同上。
**Migration**: `pane-management` capability の「pane/close によるペインのプログラム的クローズ」requirement へ、ツール名を `mdi/close` → `pane/close`、RPC名を `amm.closeWindow` → `amm.closePane` にリネームした上で移行する。

### Requirement: mdi/wait_state によるブロッキング状態待機
**Reason**: 同上。
**Migration**: `pane-management` capability の「pane/wait_state によるブロッキング状態待機」requirement へ、ツール名を `mdi/wait_state` → `pane/wait_state` にリネームした上で移行する(`amm.waitState` RPC名は変更なし)。

### Requirement: WaitBroker によるセッション単位の待機管理
**Reason**: 同上。
**Migration**: `pane-management` capability の「WaitBroker によるペイン単位の待機管理」requirement へ、内容を変更せず移行する。

### Requirement: MCP ツールと PowerShell cmdlet の意味論一致
**Reason**: 同上。
**Migration**: `pane-management` capability の同名 requirement へ、参照する RPC 名を `amm.openWindow`/`amm.closeWindow` → `amm.openPane`/`amm.closePane` に更新した上で移行する。`Amm.PowerShell` の cmdlet 名・パラメータ自体は変更しない(内部で呼ぶ RPC 名のみ更新)。
