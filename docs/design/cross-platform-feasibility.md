# Mac / Linux 版 実現可能性検討（メモ）

> **状態**: 更新（2026-07-20）。移植先フレームワークに Tauri を採用する判断（UDR-amm-20260713T1037-ff3）が確定し、`migrate-to-tauri` change による WinForms→Tauri 移植が完了した後の実態調査に基づき全面書き換え。旧版（WinForms 前提、Avalonia/Photino.NET 検討）はもう実態と乖離しているため置き換えた。
> 結論が出たら決定として記録し、決定後は本ファイルをアーカイブするか要約に置き換える。
>
> **2026-07-30 追記**: 本メモの調査結果（Windows依存は下記5箇所に限定）を根拠に、開発フォーカスをMacへ移す判断（UDR-amm-20260729T0158-a37）を経て `openspec/changes/add-macos-support/` として実行フェーズへ移行済み。下記5箇所はすべて実装・Apple Silicon実機で動作確認済み（IPC/プロセス管理/システムメニュー代替/Dockバウンス/フォーカス前面化）。`.dmg`配布・全16 capability棚卸し・notarization要否等は未完了（`openspec/changes/add-macos-support/tasks.md`参照）。本ファイルは変更完了後にアーカイブ予定、それまでは背景資料として残す。

## 要求

- 現状 amm は Windows ネイティブのみ（`README.md` 記載）。
- Mac / Linux 版を作れないか検討したい。
- **機能は網羅**（feature parity 必須）。
- **UI/UX は変えてよい**（見た目・操作感の作り直しは許容）。

## 前提: Tauri 版の土台はもともとクロスプラットフォーム対応

旧メモの検討時点（WinForms + WebView2 + ConPTY 版）では GUI シェルそのものが Windows 専用だったが、`migrate-to-tauri` で採用した以下のコンポーネントは**追加実装なしでそのまま Mac/Linux でも動く**:

| コンポーネント | 役割 | クロスプラットフォーム対応の根拠 |
|---|---|---|
| Tauri 本体 | GUI シェル | Windows/Mac/Linux 公式サポート。Mac では自動的に `WKWebView`（WebView2 相当）に切り替わる |
| `portable-pty` crate | 疑似端末バックエンド | Windows は ConPTY、Mac/Linux は `forkpty` を内部で吸収。`src-tauri/Cargo.toml` の無条件依存（`cfg(windows)` 配下ではない） |
| Tauri `tray-icon` 機能 | トレイアイコン | Tauri 本体機能としてクロスプラットフォーム対応 |
| `tauri-plugin-notification` | トースト通知 | 同上 |
| xterm.js | ターミナル描画 | もともと Web 技術のため OS 非依存 |

つまり Mac 版で追加実装が要るのは、**Win32 API を直接叩いている箇所だけ**に限定される。

## Windows 依存の実体（2026-07-20 時点、`src/apps/Amm.Tauri/src-tauri`(2026-07-26に`src/apps/Amm/src-tauri`へリネーム済み) 実コード調査）

`Cargo.toml` の `[target.'cfg(windows)'.dependencies]` に `windows` crate（features: `Win32_UI_WindowsAndMessaging`, `Win32_Foundation`, `Win32_UI_Shell`, `Win32_System_JobObjects`, `Win32_System_Threading`, `Win32_Security`, `Win32_System_Time`）が切られており、これを直接使っている箇所が Windows 専用実装の実体。

| 箇所 | 何をしているか | Mac での対応方針 |
|---|---|---|
| `mcp.rs:643,699` / `bin/amm-mcp/pipe_client.rs:9,32` | `amm.exe`⇔`amm-mcp.exe` 間 IPC に **Named Pipe**（`\\.\pipe\amm-mcp-<user>`）を使用 | Unix domain socket に差し替え。`tokio::net::UnixListener`/`UnixStream` が標準提供されており、技術的には比較的軽い |
| `gateway.rs:176,280-313` | MCP サーバ（外部プロセス）を **Windows Job Object** にひも付け、プロセスツリーごと一括終了（`JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE`） | Mac はプロセスグループ（`setpgid`）+ シグナル（`SIGTERM`/`SIGKILL`）で同等の「子孫プロセスごと後始末」が可能。今回のセッションで `tokio::spawn` クラッシュを見つけた箇所でもあり、丁寧な移植・実機検証が必要 |
| `lib.rs:1104` `install_amm_system_menu` | タイトルバー左上のシステムメニューに「フォントサイズ」「AMM 設定」等を `GetSystemMenu`/`AppendMenuW` で直接追加 | **Mac にはシステムメニューという概念自体がない**（メニューはウィンドウ上部ではなく画面最上部のメニューバー = `NSMenu`）。UI 設計からやり直しが必要な、最も手離れの悪い箇所 |
| `lib.rs:814` `flash_taskbar_icon` | `FlashWindowEx` でタスクバーアイコンを点滅させ、attention 状態をユーザーに知らせる | Mac 相当機能なし。Dock アイコンのバウンス通知（`NSApplication.requestUserAttention`、Tauri 経由で叩けるか要調査）等に置き換え検討 |
| `chat_recording.rs:177-178` `local_civil`（今回セッションで追加） | ローカル日付変換に `SystemTimeToTzSpecificLocalTime`（Win32、DST 考慮）を使用 | 影響小。`chrono` crate の `Local`、または `time` crate の tz 機能などクロスプラットフォームなタイムゾーン処理に差し替えるだけ |

## 提案の方向性

1. **IPC (Named Pipe → Unix domain socket)**: `cfg(windows)` / `cfg(unix)` で実装を分岐させる。プロトコル（JSON-RPC over stdio 相当のフレーミング）自体は変更不要、トランスポート層のみの差し替え。
2. **プロセスツリー管理 (Job Object → プロセスグループ)**: `portable-pty` や `std::process::Command` 経由での `setpgid`/シグナル送信に置き換え。ここは実装よりも「本当に孫プロセスまで確実に後始末できるか」の実機検証コストが大きい。
3. **システムメニュー (`install_amm_system_menu` → NSMenu 相当)**: Mac 版では「フォントサイズ変更」「AMM 設定」等の導線をシステムメニューではなく、ウィンドウ内の別 UI（ペインのコンテキストメニュー・ツールバー等、今回のセッションで実装したものと同様の DOM ベース UI）に載せ替える方が自然。UI/UX 変更許容の要求とも整合する。
4. **タスクバー点滅 (`FlashWindowEx` → Dock バウンス)**: Tauri の `WebviewWindow` API で Dock バウンスに相当する呼び出しがあるか要調査（無ければ `windows` crate と同様に `cfg(target_os = "macos")` 配下で Cocoa API を直接叩く必要がある可能性）。
5. **ローカル時刻変換**: 優先度低・作業量小。`chrono`（または既存の自前 `chrono_lite` モジュールに Unix 側の実装を追加）で対応。

**トレードオフ**: Tauri 移植前のメモにあった「GUI 本体が大部分新規実装」という前提はもう成立せず、実際に追加実装が要るのは上記 5 箇所（IPC・プロセス管理・システムメニュー・タスクバー通知・時刻変換）に限定される。中でも工数・設計判断が重いのはシステムメニューの UI 再設計とプロセスツリー管理の実機検証。

## 未検証・次の投資判断ポイント

- [ ] macOS の Dock バウンス通知を Tauri 経由 or Cocoa 直叩きどちらで実現するか
- [ ] Unix domain socket 版 IPC のパーミッション設計（Named Pipe 版で本番投入前に必要とされていた ACL 実装 `tasks/pending-real-machine-verification.md` と同種の検討が Unix 側にも要るか）
- [ ] プロセスグループでの子孫プロセス一括終了が、Job Object と同等の確実性を持つか（デタッチされた孫プロセスの取りこぼし等）の実機検証
- [ ] macOS 版の配布形式（`.pkg`/`.dmg`）と Tauri のクロスビルド・署名（notarization）フロー
- [ ] IME 二重送信ガード等、Windows 固有の考慮（`HANDOVER.md` 記載）が Mac でどう扱われるべきか（WKWebView + xterm.js の組み合わせでの実機確認が必要）
- [ ] 段階移行 vs 一括対応の工数比較、最小 PoC の範囲定義（`cfg(windows)`/`cfg(unix)` の実装を先に両対応にしてから配布まで持っていくか、まず macOS ビルドが通ることだけを PoC にするか）

## 参照

- `README.md`（現状の対応プラットフォーム記載）
- `docs/design/architecture.md`
- `docs/design/amm-companion-boundary.md`
- `openspec/changes/archive/2026-07-26-migrate-to-tauri/`（WinForms→Tauri 移植の変更提案・PARITY-AUDIT.md、2026-07-26 archive済み）
- `HANDOVER.md`（申し送り）
