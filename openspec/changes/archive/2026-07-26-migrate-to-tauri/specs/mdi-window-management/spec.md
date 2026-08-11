## REMOVED Requirements

### Requirement: MDI ウィンドウ構成
**Reason**: MDI(複数子ウィンドウ)方式を廃止し単一ウィンドウ内ペイン管理方式へ移行するため(`UDR-amm-20260713T0447-98f`)。MDIは子ウィンドウの配置がユーザ任せになり全画面を有効に使えない場合があるため。
**Migration**: `pane-management` capability の「単一ウィンドウ内ペイン構成」requirement へ移行する。`MdiParentForm`/`TerminalChildForm`という WinForms 固有のクラス構成は廃止し、単一の Tauri `WebviewWindow` 内で HTML/CSS によるペイン配置に置き換える。

### Requirement: 入力欄キーボードショートカット
**Reason**: 同上(ショートカット自体の意味論は変えないが、送信先の解決対象が「MDI子」から「ペイン」に変わるため)。
**Migration**: `pane-management` capability の同名 requirement へ、「MDI子」を「ペイン」に置き換えた上で移行する。キーバインド・意味論(Ctrl+S/Ctrl+Shift+S/Ctrl+1-9/Ctrl+H/Ctrl+E/Enter)は変更しない。

### Requirement: MDI タイル整列
**Reason**: MDI方式の廃止に伴い、整列対象がMDI子ウィンドウからペインに変わるため。
**Migration**: `pane-management` capability の「ペインのタイル・カスケード整列」requirement へ移行する。「タイル縦」「タイル横」の単列整列は廃止し、`reference/poc-tauri-terminal`で実機検証済みの「タイル(グリッド)」「カスケード」の2方式に整理する。

### Requirement: 入力待ち状態の可視化
**Reason**: 同上(可視化対象がMDI子からペインに変わるため、可視化の仕組み自体は変更しない)。
**Migration**: `pane-management` capability の同名 requirement へ、「MDI子」を「ペイン」に置き換えた上で移行する。タイトル絵文字・タイトルバー色・`FlashWindowEx`によるタスクバー点滅の仕様は変更しない(`reference/poc-tauri-terminal`で`FlashWindowEx`呼び出し自体は実機確認済み)。

### Requirement: MDI 子システムメニュー拡張
**Reason**: 同上(拡張対象がMDI子ウィンドウからペイン/メインウィンドウに変わるため)。
**Migration**: `pane-management` capability の「ペインのシステムメニュー拡張」requirement へ移行する。Win32 API呼び出し自体は WinForms の P/Invoke から Rust の `windows` crate 経由に実装が変わるが、要件(メニュー項目・ID帯)は変更しない。`reference/poc-tauri-terminal`で同等のシステムメニュー拡張(`GetSystemMenu`+`AppendMenuW`+`WM_SYSCOMMAND`フック)を実機検証済み。

### Requirement: ファイルドラッグ&ドロップ
**Reason**: 同上(ドロップ先がMDI子ウィンドウからペインに変わるため)。
**Migration**: `pane-management` capability の同名 requirement へ、「MDI子」を「ペイン」に置き換えた上で移行する。WebView2上のネイティブドロップ横取り(`NativeDropTarget`)は、Tauri/WebView2でも同様の実装(HWNDレベルでの`IDropTarget`差し替え)が必要になる可能性が高く、実装時に個別検証が必要(`design.md`のOpen Questionsに未収載のため、実装フェーズで追加調査すること)。

### Requirement: ウィンドウレイアウトと表示設定の永続化
**Reason**: 同上(永続化対象がMDI子の配置からペインの配置に変わるため)。
**Migration**: `pane-management` capability の同名 requirement へ移行する。永続化先は`%LOCALAPPDATA%\amm\layout.json`という同一パスを維持するか、`localStorage`(`reference/poc-tauri-terminal/public/mdi-prototype.html`で実機検証済みの方式)を使うかは実装フェーズで決定する。

### Requirement: 終了時の確認ガード
**Reason**: 同上(確認対象がMDI子からペインに変わるため、確認ロジック自体は変更しない)。
**Migration**: `pane-management` capability の同名 requirement へ、「MDI子」を「ペイン」に置き換えた上で移行する。
