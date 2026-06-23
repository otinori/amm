# Backlog — 未着手の積み残み・要望プール

> 着手が決まったら `TASKS.md` へ昇格する。

## 要望 / アイデア

### アイドル時自動送信

**概要**: ToolUse / 許可待ちではない純粋な入力待ち (`WaitingForInput && !HasAttention`) に遷移したとき、
コマンド設定にあらかじめ登録したプロンプトを指定遅延後に自動送信する機能。
AI が自律的に次のタスクを開始するオートパイロット用途や、定型確認コマンド（`/status` 等）の自動実行に使える。

**Spec**: [`docs/design/spec/req-20260622-auto-send-idle.md`](../docs/design/spec/req-20260622-auto-send-idle.md)  
**状態**: Draft 置き  
**前提**: `SessionProfile.AutoSendOnIdle` 設定追加 + `TerminalChildForm` の WaitState 遷移監視。

---

### コマンド設定インポート・エクスポート

**概要**: コマンド設定（`SessionProfile`）を複数選択して `.ammprofiles` ファイルへエクスポートし、
別の AMM 環境へインポートする機能。重複時はスキップ / リネーム / 上書きを選択できる。
`.amm` ファイルからの直接インポートにも対応。

**Spec**: [`docs/design/spec/req-20260622-command-import-export.md`](../docs/design/spec/req-20260622-command-import-export.md)  
**状態**: Draft 置き  
**前提**: `CommandTemplateDialog` へボタン追加、`ExportProfilesDialog` / `ImportProfilesDialog` 新規作成。

---

### 右クリックからのクイック送信コマンド登録

**概要**: 端末の右クリックメニューから「クイック送信に登録...」を選択し、
直前に送信したプロンプトをフォーカス中の MDI ペインのクイック送信一覧に追加できる機能。

**Spec**: [`docs/design/spec/req-20260622-quick-command-register.md`](../docs/design/spec/req-20260622-quick-command-register.md)  
**状態**: Draft 置き（PR #5 で追加）  
**前提**: JS 側 `lastForward` の postMessage + `QuickSendRegisterDialog` 新規作成。

---

### システムトレイ常駐アイコン

**概要**: AMM 起動中にシステムトレイにアイコンを常駐させ、入力待ちセッションが発生したらバルーン通知する。
アイコンクリックで入力待ち MDI ペインへフォーカスを移動する。WinForms `NotifyIcon` で実装（WinRT 不要）。

**Spec**: [`docs/design/spec/req-20260622-tray-icon.md`](../docs/design/spec/req-20260622-tray-icon.md)  
**状態**: Draft 置き（PR #5 で追加）  
**前提**: `TrayIconManager.cs` 新規作成。既存 `FlashWindowEx`（UDR-3af）を補完する形で共存。

---

### MDI ウィンドウ制御（オートパイロット強化）

**概要**: `mdi/open` / `mdi/close` を amm-mcp MCP ツールおよび PowerShell cmdlet として公開し、
AI エージェントや PS スクリプトが MDI ウィンドウをプログラムから開閉できるようにする。
「エージェントが自分でセッションを生やして並列タスクを割り当て、完了後に閉じる」オートパイロット動作を実現。

**Spec**: [`docs/design/spec/req-20260622-mdi-window-control.md`](../docs/design/spec/req-20260622-mdi-window-control.md)  
**状態**: Draft 置き（Pattern D PS モジュールと同時着手の想定）  
**前提**: Named Pipe プロトコル拡張 + `McpPipeServer.cs` / `MdiParentForm.cs` の改修が必要。

---

### PowerShell モジュール（Pattern D）

**概要**: `Amm.PowerShell.dll` バイナリモジュールとして amm の機能を PS cmdlet に公開する。
`Open-AmmWindow`, `Close-AmmWindow`, `Send-AmmMessage`, `Get-AmmSession` 等を提供し、
PowerShell スクリプトや CI パイプラインから amm を駆動できるようにする。

**Spec**: [`docs/design/spec/req-20260622-ps-module.md`](../docs/design/spec/req-20260622-ps-module.md)  
**状態**: Draft 置き（MDI ウィンドウ制御 spec と同時着手の想定）  
**前提**: Named Pipe クライアント側の実装。amm-mcp.exe の逆方向接続。

---

### MCP ゲートウェイ（外部 MCP サーバー集約）

**概要**: amm を MCP マルチプレクサ化し、外部 MCP サーバー（filesystem / browser / DB 等）を
amm が一括管理して MDI pane の全エージェントに透過的に公開する。

**Spec**: [`docs/design/spec/req-20260622-mcp-gateway.md`](../docs/design/spec/req-20260622-mcp-gateway.md)  
**状態**: Draft 置き（ユースケースが固まってから着手）  
**前提**: 既存 `McpPipeServer` / `McpCliRegistrar` の拡張として実装可能。

---

- （その他の要望はここに溜める）

## 既知の残課題

- TSF（Text Services Framework）由来の IME 二重送信の真因根治（現状は受信側 dedup/coalescer で緩和）

## セキュリティ／コードレビュー残課題 (2026-06-18 レビュー)

全ソースのセキュリティ＋シニアコードレビュー結果のうち、未対応分。`✅ 実装済` は当日修正で対応済。

### 裁定済 — 受容 / 延期 (UDR-amm-20260618T1310-b7e)
- **A-1 引数のシェル注入** → **受容**。`.amm` 作者は元々コマンド全体を指定でき autoChcp は能力を増やさない (権限境界の越境ではない)。
  信頼されない .amm の自動起動は確認ダイアログでゲート済 (UDR-7f2)。一律クォートは UDR-7f2 で棄却済のため行わない。
- **A-3 approval/notify token の接続バインド** → **受容**。公式モデルは「同一ユーザー=信頼 (OS 認証のみ)」(UDR-7a3/9c4)。
  MDI ペイン分離は UX であり境界ではない。境界化するなら別判断点で GetNamedPipeClientProcessId 検証を起こす。
- **A-4 WebView2 CSP** → **延期**。インライン script の nonce 化 + 実機 WebView2 検証が必要 (ブラインド適用は破損リスク)。実機検証可能なセッションで実施。

### 未着手 (安全。優先度低・順次)
- **A-5** Named Pipe 行読みの 1 文字 ReadAsync (`McpPipeServer.cs:151-166`) → **優先度低に再評価**。StreamReader が内部バッファを持つため per-char I/O syscall は発生せず大半が同期完了。DoS 価値は 1MiB 上限内のループ overhead に留まる。バッファ読み化は leftover 跨ぎ管理のリスクに見合わないため保留。
- **A-8** osc_notify のレート制限 → **見送り検討中**。単純な間隔ゲートは最終 idle を取りこぼし「処理中固着」を再発させる恐れ。最新状態を保持する設計が要る。
- **A-9** QuotePath/arg の内部 `"` エスケープ (A-1 を受容したため優先度低)。
- **B-4** Pipe の Contains 事前判定+二重パースを単一ディスパッチへ (`McpPipeServer.cs:176`、軽微)。
- **B-5** CreatePopupMenu の未 attach 経路で DestroyMenu (`Win32SystemMenu.cs:87`、極小・据え置き)。
- **B-10** AppLogger の ACL 適用を失敗時リトライ可に (`AppLogger.cs:105`、per-flush の I/O 増に注意)。

### ✅ 実装済 (2026-06-18)
- **B-1** profiles.amm を AtomicFileWriter 経由の原子的書込へ。
- **A-10** AtomicFileWriter の tmp をランダム名 + 例外時 best-effort 削除へ。
- **A-7** ユーザー定義 waitPatterns に matchTimeout(100ms) 付与 + RegexMatchTimeoutException 吸収 (ReDoS)。
- **A-6** TomlEscape 導入。Codex config.toml のパスに `'` が含まれても壊れない (HookCliRegistrar/McpCliRegistrar)。
- **B-7** 死コード ToInt 削除。**B-8** JsonClone 失敗時に AppLogger.Warn。
- **A-2** OSC52 クリップボード書込を部分 hardening: JS で base64 上限(128KiB) + `source:'osc52'` タグ、C# で OSC52 由来のみ 64KiB 上限 + 長さのみ監査ログ。ユーザー選択コピーは従来どおり。(完全な ON/OFF トグルは将来課題)
- **B-9** OnWebMessageReceived の malformed を例外種別のみログ (本文は出さない)。
- **B-11** WorkingDirectory 保存時の trim 漏れ是正。(safeName はタイムスタンプ前置+.log 接尾で予約名/末尾ドットが無効化されるため変更不要)
- **Info** FrameNavigationStarting も同ガードで遮断 (将来の iframe 混入対策)。
- **B-2** async void イベント経路の UI クラッシュ防止: 共通ガード RunGuarded 導入。SendInput/BroadcastInput/SendToIndexed をラッパ化し、共有経路 SendInputToTargetAsync と EditorBridge.OnDebounceElapsed の送信を try/catch で保護。
- **B-6** InferCommandType を ArgsReferToTool でトークン単位照合へ。パスに "codex"/"copilot" を部分的に含む旧 .amm の誤判定を防止。
- (B-3 は Dispose が watcher を join 後にハンドルを閉じるため実レースでないと判断し対象外)

## クローズ時の最終判断 (2026-06-18, リポジトリ ZIP アーカイブ予定)

「今回で修正終了」前提での判断: **残項目は意図的に未適用のままクローズするのが妥当。**
理由 (= 最終アーカイブ状態を壊さない方を優先):
- **A-4 (CSP)**: 実機 WebView2 で terminal.html の描画 (インライン script / xterm.js の eval 有無) を確認できない。
  ブラインド適用で端末描画が壊れると、修正できる「次回」が無いアーカイブ状態を毀損する。適用しない方がリスクが低い。
- **A-9 (QuotePath の " エスケープ)**: Windows ではファイル名に `"` を含められない (無効文字) ため実質 moot。
- **B-4 / B-5 / B-10**: それぞれ二重パース(無害)・到達ほぼ不能なメニュー生成失敗経路・per-flush I/O 増の懸念ありで、
  価値が極小。クローズ間際にセキュリティ/ログ系へ変更を入れる方がリスクが高い。
判断: セキュリティ上・安定性上の実害ある指摘は全て解消済。残りは「触らない」ことが最善。
