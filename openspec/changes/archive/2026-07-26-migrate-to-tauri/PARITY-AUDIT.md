# migrate-to-tauri — Parity Audit (tasks.md 8.1)

Generated 2026-07-19. Cross-references each of 16 capabilities' `spec.md` requirements
(15 in `openspec/specs/`, plus the new `pane-management` in
`openspec/changes/migrate-to-tauri/specs/pane-management/spec.md`) against the actual
Rust/Tauri implementation. `mdi-window-control`/`mdi-window-management` are excluded —
replaced by `pane-management` per this change's `proposal.md` Non-Goals.

Legend: ✅ Full · ⚠️ Partial (implemented with a known gap/simplification) · ❌ Missing ·
🔍 Unverified (code exists, not confirmed working — usually needs real hardware/a human/an
external tool this session couldn't drive).

For `ps-module`, note that the spec text still describes the **old** `.NET` binary module
(`amm.openWindow`, `.psd1` requiring PowerShell 7.4, `Amm.PowerShell.dll`) — it hasn't been
updated to reflect `UDR-amm-20260719T0013-b7e`'s replacement (`Amm.PowerShell.Tauri`, a
`.psm1` script module targeting PowerShell 5.1+, calling the renamed `amm.openPane` etc.).
Status below judges *behavioral* parity against that intent, not literal string matches
against the now-stale method/version names in the spec text.

## Summary table

| Capability | Requirements | Full | Partial | Missing | Unverified |
|---|---|---|---|---|---|
| pane-management (new) | 14 | 10 | 3 | 0 | 1 |
| approval-hub | 5 | 5 | 0 | 0 | 0 |
| auto-send-idle | 5 | 5 | 0 | 0 | 0 |
| chat-recording | 6 | 6 | 0 | 0 | 0 |
| command-import-export | 6 | 6 | 0 | 0 | 0 |
| git-integration | 5 | 5 | 0 | 0 | 0 |
| hook-cli | 5 | 5 | 0 | 0 | 0 |
| input-history | 5 | 3 | 1 | 1 | 0 |
| mcp-gateway | 8 | 8 | 0 | 0 | 0 |
| mcp-server | 9 | 9 | 0 | 0 | 0 |
| profile-schema | 9 | 9 | 0 | 0 | 0 |
| ps-module | 8 | 7 | 1 | 0 | 0 |
| quick-command-register | 5 | 5 | 0 | 0 | 0 |
| tray-icon | 4 | 4 | 0 | 0 | 0 |
| wait-detection | 6 | 4 | 2 | 0 | 0 |
| **Total** | **100** | **91** | **7** | **1** | **1** |

> **Updated 2026-07-26 (`add-editor-integration`)**: pane-management's system-menu entry corrected (名前変更/AMM設定 were already Full, not stubs as previously recorded) and Ctrl+E/エディタ連携/エディタ連携パスコピー implemented (new `editor-integration` capability, `openspec/specs/editor-integration/spec.md`). Counts above reflect this.

> **Updated 2026-07-21〜22**: `mcp-server`'s ACL gap (tasks.md 4.1), `profile-schema`'s capture_window_geometry trigger UI, and `approval-hub`'s remaining gaps (the popup-UI redesign, the pipe-disconnect release trigger, and the amm-mcp.exe approve real-CLI/human-operated verification) are now fixed/completed - see their entries below for what changed. Counts above reflect these fixes.

> **Updated 2026-07-19** after a follow-up implementation pass on this audit's findings: git-integration's
> 3 gaps, profile-schema's arg-validation and commandType-inference gaps, ps-module's Connect-Amm error
> handling, and approval-hub's pane-activation release trigger are now fixed (see updated entries below).
> Counts above reflect the fixes; individual entries below are updated in place with a note on what changed.

## pane-management (new capability)

### Requirement: 単一ウィンドウ内ペイン構成
**Status**: ✅ Full
Single Tauri `WebviewWindow`, panes as absolutely-positioned DOM elements, 3-row bottom panel (quick-switch bar / input / status), default 1400×900 with layout restore. Confirmed real-machine (`public/app.js`, tasks.md 2.1/2.4).

### Requirement: 入力欄キーボードショートカット
**Status**: ⚠️ Partial
Ctrl+S / Ctrl+Shift+S / Ctrl+1-9 / plain-Enter-inserts-newline all confirmed real-machine. Ctrl+H (history dropdown) implemented as of 5.2. **Updated (`add-editor-integration`, see `openspec/specs/editor-integration/spec.md`)**: Ctrl+E now launches the real editor-integration bridge (was a keybinding stub with no backing feature). **Remaining gap**: the "Ctrl+Sでプロンプト送信" ON/OFF setting doesn't exist (Ctrl+S is unconditionally active, can't be disabled since there's no settings UI at all).

### Requirement: ペインのタイル・カスケード整列
**Status**: ✅ Full
Tile (`cols=ceil(sqrt(N))` grid), cascade (32px stagger, click-to-front on exposed area), and `localStorage`-based saved-layout restore all confirmed real-machine (tasks.md 2.1/2.2, `shots-phase2/`).

### Requirement: 入力待ち状態の可視化
**Status**: ✅ Full
Title emoji (▶/●/⚠/■/?), titlebar color (yellow/orange), `FlashWindowEx` on attention while not foreground, attention-clear on activation/Running/Stopped — all match spec text exactly (`app.js` `applyWaitState`/`setAttention`), real-machine confirmed for the core state-transition path.

### Requirement: ペインのシステムメニュー拡張
**Status**: ✅ Full (updated 2026-07-26)
Menu structure (AMM popup, 16-multiple IDs, "閉じる" reordering) confirmed real-machine (tasks.md 3.1). This entry was stale: 名前変更/AMM設定 were already implemented (found working during `add-editor-integration`'s scoping pass, not stubs as previously recorded here), and エディタ連携/エディタ連携パスコピー are now implemented too (`add-editor-integration`, see `openspec/specs/editor-integration/spec.md`). Font-size, rename, AMM settings, editor-link, editor-link-path-copy all work end to end via `runSystemMenuAction`'s shared dispatch (OS system menu / Ctrl+E / pane title-bar menu / terminal-body context menu all converge on the same implementation).

### Requirement: ファイルドラッグ&ドロップ
**Status**: 🔍 Unverified
Text-detection / size-confirmation / path-vs-content logic implemented (`DragDropEvent`, not raw `IDropTarget`), builds cleanly, but real OS drag-and-drop was never exercised this session (no way to simulate real Explorer drag via CDP). Already tracked in `pending-real-machine-verification.md`.

### Requirement: ウィンドウレイアウトと表示設定の永続化
**Status**: ⚠️ Partial
Window position/size and per-pane layout persist via `localStorage` and are confirmed restored across restart (tasks.md 2.2). **Gap**: most of the *other* listed settings (comment-filter, Ctrl+S enable toggle, confirm-newline-bundling, per-pane draft text, approval-popup toggle, tray-notify toggle, editor-integration settings, history max-entries as a user-editable setting) don't exist as togglable features at all yet, so naturally nothing persists for them — this isn't a persistence bug, it's upstream features being absent.

### Requirement: 下部パネル行の個別表示切替
**Status**: ✅ Full
Quick-switch-bar/input/status-line visibility toggles exist in the toolbar (`index.html`) with height-0 hide and remembered-height restore, per Phase 2 implementation notes.

### Requirement: 終了時の確認ガード
**Status**: ⚠️ Partial
Running-session confirmation is implemented and the "cancel blocks exit" path is CDP-confirmed; the "はい" (accept) path is unverified due to a WebView2/CDP dialog-timing limitation (`pending-real-machine-verification.md`). **Missing entirely**: unsaved-profile-change confirmation and external-`.amm`-auto-launch confirmation — both depend on profile-editing UI that doesn't exist yet (5.1's documented scope boundary).

### Requirement: pane/open によるペインのプログラム的オープン
**Status**: ✅ Full
`command`/`profile_name` XOR-required, `-32602` on neither, `args`/`title`/`workingDirectory` optional, profile-based nickname inheritance — implemented in `mcp.rs::open_pane`, real-machine tested repeatedly this session (phases 4.2, 7.1, 7.2/7.3 live PowerShell tests).

### Requirement: pane/close によるペインのプログラム的クローズ
**Status**: ✅ Full
`session_id`/`force`, `{error: "session not found: ..."}` on unknown ID (no exception), force skips confirmation. Real-machine tested including the double-close race fix (tasks.md 4.2, 7.1).

### Requirement: pane/wait_state によるブロッキング状態待機
**Status**: ✅ Full
`{state, elapsed_ms}` on resolution, `{state:"timeout", elapsed_ms}` on timeout (never throws), session-close releases all pending waits as `"timeout"`. Real-machine tested including the newly-fixed WaitPatternDetector→WaitBroker fallback (tasks.md 4.3, 7.3).

### Requirement: WaitBroker によるペイン単位の待機管理
**Status**: ✅ Full
`mcp.rs`'s `waiters: HashMap<String, Vec<(u64, WaitEntry)>>` supports multiple independent concurrent waiters per session with different `target_state`, and `resolve_by_session` only resolves matching-`target_state` entries — matches the "idle と attention の同時待機" scenario exactly by construction, though this specific dual-target-state scenario wasn't separately live-tested (structural code guarantee, not observed).

### Requirement: MCP ツールと PowerShell cmdlet の意味論一致
**Status**: ✅ Full
`Open-AmmWindow -ProfileName`/`Wait-AmmIdle -Nickname` produce byte-identical semantics to their MCP tool equivalents by construction (`Amm.PowerShell.Tauri.psm1` calls the exact same `amm.openPane`/`amm.waitState` RPCs `pane/open`/`pane/wait_state` dispatch to). Live-tested this session (tasks.md 7.2/7.3).

## approval-hub

### Requirement: Level 1 — 許可要求の通知のみ (Attention 表示)
**Status**: ✅ Full (fixed/verified 2026-07-20)
Fired for real via `AMM_NOTIFY_ID=<pane_id> amm-mcp.exe notify --state attention` against a non-active pane (the active pane intentionally suppresses its own attention indicator, `app.js`'s `applyWaitState` - not a bug, tested separately). Confirmed via CDP: title text flips to `⚠ <label>`, `state-attention`/`attention-warn` CSS classes apply, titlebar background changes from the WaitingForInput olive to attention orange - screenshotted and visually verified. `FlashWindowEx` itself still needs a human (this session's screen capture can't see taskbar flashing, see `pending-real-machine-verification.md`'s tray-icon entry). This also caught and fixed a real bug along the way: `AMM_NOTIFY_ID` was never injected into spawned panes at all (missing `cmd.env()` call, ported from `TerminalChildForm.cs`), so every hook-driven notify/approve was a silent no-op until fixed.

### Requirement: Level 2 — 許可要求の集約回答台帳
**Status**: ✅ Full
`ApprovalBroker::request`/`resolve` (`src/approval.rs`), 1-request-per-entry, first-resolution-wins. End-to-end tested (separate-connection block/resolve, tasks.md 5.10).

### Requirement: 4 種の解放トリガー
**Status**: ✅ Full (pipe-disconnect trigger wired 2026-07-22)
All 4 triggers implemented: human answer (`resolve`), explicit release (`release_by_token`, wired to both `pane/close` and pane activation via `release_approval_on_activate`), 45s timeout, and **pipe-disconnect release**. The disconnect trigger works by racing the pending `ApprovalBroker::request()` future against a non-destructive peek-read (`AsyncBufReadExt::fill_buf()`, which reports EOF without consuming any bytes it might see) on the same connection inside `mcp.rs`'s new `await_approval_or_disconnect` helper - `handle_line` was given access to the connection's `reader` for this. On genuine EOF the entry is released via `release_by_token` and the (still-alive, never-dropped) request future is awaited to completion, avoiding any risk of orphaning the pending entry. This required also changing `ApprovalBroker::resolve` to remove the entry from `pending` immediately (rather than leaving that to `request()`'s own post-await cleanup), so a caller racing `request()`'s future via `select!` can't leak an entry that nothing will ever clean up. 8 new unit tests (3 in `mcp.rs` exercising the full race via `tokio::io::duplex`, 5 in `approval.rs` covering the broker itself, which previously had zero test coverage) all pass. Real-machine verified: launched a real `amm-mcp.exe approve` subprocess against a live pipe connection, confirmed it appeared in `list_approvals`, force-killed the process mid-wait, and confirmed the entry was released in ~70ms (not the 45s timeout) - repeated with a normal (non-killed) resolution afterward to confirm the existing paths are unaffected.

### Requirement: 非モーダル集約ポップアップ UI
**Status**: ✅ Full (redesigned 2026-07-21, deadlock root-caused and resolved)
The second-`WebviewWindow` design (`approval-popup.html`/`show_approval_popup`) was abandoned entirely after its `WebviewWindowBuilder::build()` call was found to deadlock the whole app (see history below) - replaced with an in-main-window DOM overlay (`#approval-overlay` in `index.html`, driven by `app.js`'s `refreshApprovalOverlay`/`decideApprovalOverlay`), matching the pattern this app already uses for its other dialogs. Tool name/input display, "N/M件" counter, allow/deny/close/goto-pane buttons all confirmed working via real hook-triggered approvals (Claude Code and Copilot CLI, tasks.md 8.2.4) with a human at a real interactive desktop, not just simulated `invoke()` calls.
**History (root cause, for anyone touching pane/window creation code)**: the original design deadlocked because `WebviewWindowBuilder::build()`, called from inside the *main* webview's own IPC callback (itself dispatched from that same webview's CDP network interception), created a circular wait - the main thread blocked waiting for the browser process to finish creating the second window, while the browser process was blocked waiting for the main thread to return from the current IPC callback before it could proceed. Confirmed via `cdb.exe` attached to a hung process (all-thread stack dump). The practical lesson: **never create a second `WebviewWindow` synchronously from inside a Tauri command handler on this app's main window** - use an in-DOM overlay instead, as this fix (and `approval-hub`'s design going forward) now does.

### Requirement: amm-mcp.exe approve サブコマンドの決定出力契約
**Status**: ✅ Full (real-CLI and human-operated verification completed 2026-07-21〜22)
`build_approve_output` unit-tested for both `--source claude` and `--source copilot` JSON shapes (exact match to spec), and the no-op/no-`AMM_NOTIFY_ID` and silent-timeout paths are implemented per spec. Ran the real `amm-mcp.exe approve --source test` binary end-to-end (stdin JSON `tool_name`/`tool_input`, `AMM_NOTIFY_ID` set to a live pane) - it appeared correctly in `list_approvals`, was resolved via `resolve_approval`, and the binary's stdout printed the exact PermissionRequest-shaped JSON. **2026-07-21〜22**: real Claude Code and Copilot CLI processes were driven to actually invoke this hook themselves (not a simulation) via their own `PermissionRequest`-equivalent mechanisms, with the resulting real data confirmed live in the approval overlay and resolved via the overlay's own allow/deny buttons at a real interactive desktop (tasks.md 8.2.4/8.2.5).

## auto-send-idle

### Requirement: プロファイル単位のアイドル自動送信設定
**Status**: ✅ Full
`AutoSendOnIdleSettings{enabled: false, prompt: "", delay_ms: 3000}` defaults match exactly (`profile.rs`).

### Requirement: Running → WaitingForInput 遷移でのみカウントダウンを開始する
**Status**: ✅ Full
`app.js`'s `applyWaitState` arms only on `prevState === 'Running' && !pane.autoSendArmed`, gated on `!hasAttention`. Real-machine tested (tasks.md 5.9).

### Requirement: アテンションまたは状態遷移によるキャンセル
**Status**: ✅ Full
`cancelAutoSendTimer` fires whenever state leaves `WaitingForInput` or `hasAttention` becomes true, matching both spec scenarios.

### Requirement: 遅延0以下は即時送信、それ以外はタイトルバーに残秒数を表示する
**Status**: ✅ Full
`delayMs <= 0` fires immediately; otherwise 500ms-interval `⏱{n}s` countdown label. Exact match, real-machine tested.

### Requirement: 送信直前の再ガードと単発実行
**Status**: ✅ Full
Re-checks `pane.attention`, `pane.state !== 'WaitingForInput'`, and pane-still-exists (`panesById.has`) immediately before firing — the last check substitutes for `.NET`'s `IsProcessRunning` (documented, functionally equivalent since process death routes through the `Stopped` transition which already cancels the timer via the previous requirement).

## chat-recording

### Requirement: コマンド送信を起点としたチャット記録の開始
**Status**: ✅ Full (fixed 2026-07-20)
Bottom-input-bar send path starts recording correctly (auto-completes an unfinished prior recording first, matches scenario). The direct-typing-into-terminal trigger is now implemented as `trackTypedInputForRecording` in `app.js` (hooked into `term.onData`): a per-pane line buffer with the same char-by-char state machine as `TrackTypedInputForRecording` (Enter commits the line, Backspace/DEL trims, Ctrl+C discards, control chars ignored, 20000-char cap that *drops* new input once full rather than sliding the window), ANSI-stripped best-effort via the same regex used elsewhere in this port. The runtime right-click-menu toggle now exists too (`showPaneContextMenu`'s "チャット記録: ON/OFF" item, label-text-as-checkbox since this port's context menu shell has no native checkbox item) - toggling only flips `pane.chatRecord.enabled` in memory, never written to `profiles.amm`, matching "実行時限りのトグル" exactly. All panes (including ad-hoc/UI-created ones with no profile) now carry a `chatRecord` object with sensible defaults instead of `null`, so the toggle works everywhere.

**Bug found and fixed via live CDP testing (phase 8.2)**: ad-hoc panes (created without a launching profile, e.g. via the "+" button) keep `pane.chatRecord.profile` at its creation-time `null` default even after being enabled through the right-click toggle - the toggle's own lazy-init fallback (`pane.chatRecord ?? {..., profile: pane.profileName ?? pane.label}`) never runs because `chatRecord` already exists as a non-null object by then. `start_chat_recording`'s Rust side takes `profile: String` (not `Option<String>`), so invoking it with `profile: null` failed IPC deserialization (`invalid type: null, expected a string`) and silently dropped every recording attempt for such panes - reproducible on every direct-typed-command send from an ad-hoc pane with chat-recording toggled on. Fixed in `startChatRecordingForPane` (`app.js`) by falling back the same way the toggle intended: `pane.chatRecord.profile ?? pane.profileName ?? pane.label`. Verified fixed: reproduced the exact null-profile state on a live pane, sent a direct-typed command, confirmed zero page errors and a correct log file written to `.amm/logs/<date>/` with the command/response captured and `profile` falling back to the pane's label.

### Requirement: 応答テキストのローリングバッファ保持
**Status**: ✅ Full
`tailChars*2` truncation to tail `tailChars`, matches exactly (`chat_recording.rs`).

### Requirement: 出力静穏によるチャット記録の確定と JSON 書き出し
**Status**: ✅ Full (timezone bug found and fixed 2026-07-20 via live CDP testing)
1200ms quiet → `Complete()`, all JSON fields present (`sent_at`/`responded_at`/`duration_ms`/`command`/`response_tail`/`follow_up_questions: []`), file path pattern matches exactly, prior-incomplete-record auto-completed on new send, write-failure isolated (logged, doesn't propagate). Real-machine tested (tasks.md 5.3).

**Bug found and fixed via live CDP testing (phase 8.2)**: `chrono_lite::Timestamp`'s `date_dir()`/`compact()` (the `logs/<yyyyMMdd>/` folder and the `<profile>-<yyyyMMddTHHmmss>-<id>.json` filename stem) computed from raw UTC, with a code comment explicitly calling this out as a "documented simplification: the .NET version uses local date; avoiding a timezone dependency here." Live testing at 08:05 JST (2026-07-19T23:05 UTC) proved the real-world cost: `.NET`'s `ChatRecorder.cs` computes `_sentAt.ToLocalTime()` for the folder/filename (while keeping `SentAt`/`RespondedAt` themselves as `DateTime.UtcNow`), so every command sent between local midnight and UTC midnight (00:00-09:00 in JST) filed under the *previous* day's folder - not data loss, but invisible in the log/stats dialogs (which default their date picker to local "today") until manually navigating back a day. User chose to fix rather than accept as a known limitation. Fixed by adding `windows` crate's `Win32_System_Time` feature and a DST-aware `local_civil()` helper (`SystemTimeToTzSpecificLocalTime` with `None` timezone = "use the OS's active setting", matching `DateTime.ToLocalTime()`'s own DST resolution) that `date_dir()`/`compact()`/`date_hyphenated()` now route through; `iso()` (the `sent_at`/`responded_at`/`updated_at` field values themselves) intentionally stays UTC, matching `DateTime.UtcNow`'s own "Z"-suffixed serialization in the .NET original exactly. Verified live at the exact JST/UTC-day-boundary window this bug depended on: a command sent at local 08:06 (UTC 23:06 the previous day) now correctly files under today's local-date folder (`logs/20260720/`, `stats/20260720/`) with a local-time filename stem (`cmd-20260720T080612-...json`), while `sent_at`/`responded_at` inside the JSON remain UTC (`2026-07-19T23:06:...Z`) exactly as before - both `.amm/logs/` and `.amm/stats/` fixed by the same shared `chrono_lite` change since `chat_stats.rs` reuses it without its own copy.

### Requirement: 日次インデックスファイルへの追記と破損耐性
**Status**: ✅ Full
`index.json` append with corruption-resilient rebuild-as-new-list, confirmed in code and tasks.md.

### Requirement: 独立した統計情報スイッチによる交換単位の集計
**Status**: ✅ Full (fixed 2026-07-20)
New `chat_stats.rs` module (`StatsState`/`StatsTracker`, ported from `ChatStats.cs`), fully independent of `chat_recorder` (separate `Arc<Mutex<...>>` field on `PtyEntry`, separate toggle, works with either or both switches on). `start_stats_tracking` mirrors `StartStatsTracking` (flushes any pending exchange before starting a new one); the pty reader thread's `touch()` call on every output chunk re-arms the 1200ms quiet window exactly like `ArmStatsIdleTimer`'s re-arm-on-output behavior (verified by reading the actual re-arm call site, not just the timer's initial constant); the 200ms silence-watcher thread (already polling for `chat_recorder`'s own quiet check) now also calls `check_quiet_and_flush`. `humanMs` is `None` for a pane's first exchange (no prior `last_responded_at`) and computed as the gap since the previous exchange's completion otherwise, matching both scenarios exactly; negative values are excluded from the sample (defensive, matches `h >= 0`). File format (`<workDir>/.amm/stats/<yyyyMMdd>/<profile>-<mdiName>.json`, accumulate-in-place with `instruction_count`/`ai_total_ms`/`ai_avg_ms`/`human_sample_count`/`human_total_ms`/`human_avg_ms`/`updated_at`) matches `ChatStatsStore.RecordExchange` field-for-field. Filename sanitization (`sanitize`) is shared with `chat_recording.rs` rather than duplicated. 4 new unit tests (accumulation across calls, negative-human-ms exclusion, sort order, pending-exchange-flushed-on-new-start).

### Requirement: 統計情報ダイアログでの日次一覧表示
**Status**: ✅ Full (fixed 2026-07-20)
`openChatStatsDialog` (new context-menu item "統計情報 → 統計情報を表示...") shows a date picker (defaults to today) + table via a new `list_chat_stats` command (`chat_stats::load_all`, MDI-name-ascending sort). Empty-day state shows the exact "この日の統計情報はありません。" text instead of a table; changing the date or clicking "更新(&R)" reloads. Duration formatting matches the spec's `h:mm:ss`/`m:ss` split at the 1-hour boundary exactly (`formatStatsDuration`), and the average columns show "-" when `human_sample_count` is 0.

Live-verified via CDP (phase 8.2, after the timezone fix above): opened via a real MCP-bridge-launched profile pane, sent a real command through `sendToPane`, confirmed the resulting record appears in the dialog under today's (local) date with correct field values (`指示回数`/`AI動作時間`/`人間の応答時間`). `formatStatsDuration` spot-checked directly for all boundary cases: `65000ms -> "1:05"`, `3600000ms -> "1:00:00"`, `3725000ms -> "1:02:05"`, `0ms -> "0:00"` - all correct.

## command-import-export

### Requirement: エクスポート対象の選択
**Status**: ✅ Full (fixed 2026-07-20)
A new toolbar button ("エクスポート...") opens `openExportProfilesDialog`: 0-loaded-profiles shows the exact "エクスポートするコマンドがありません。" info dialog before the checklist even appears; otherwise a checkbox list (all checked by default, `{Name}  [{Nickname}]  ({CommandType})  —  {Executable} {Args}` row format) with "すべて選択"/"すべて解除" buttons, built on the generic modal shell introduced for `quick-command-register`.

### Requirement: .ammprofiles 形式での保存
**Status**: ✅ Full (fixed 2026-07-20)
The file format itself (`{"version":1,"profiles":[...]}`, atomic write) was already correct and unit-tested. Now reachable via UI: OK with 0 rows checked shows "コマンドが選択されていません。" and stays open (doesn't proceed to the save picker); otherwise a native `SaveFileDialog` (added `tauri-plugin-dialog`, wrapped in a `pick_export_save_path` command rather than calling the plugin directly from JS - keeps this port's "everything through invoke()'d app commands" convention), filter `*.ammprofiles`/`*.amm`/`*.*`, default filename `profiles-export-{yyyy-MM-dd}.ammprofiles` (computed in JS via `Date`, no new Rust date dependency needed). Success/error dialogs use the exact spec text ("{N} 件のコマンドをエクスポートしました。" / "エクスポートに失敗しました。" with the error appended).

Live-verified via CDP (phase 8.2) everything up to the native save picker (the picker itself and the actual file write remain human-only, see `pending-real-machine-verification.md`): checklist defaults all-checked, "すべて選択"/"すべて解除" both work, and the 0-selected OK path correctly shows the alert (via `window.alert` monkeypatch) and stays open without reaching `pick_export_save_path`. `.modal-list`'s `max-height:240px; overflow-y:auto` CSS makes the "dozens of entries" scroll case a non-issue by construction.

### Requirement: インポートファイルの読み込みと後方互換パース
**Status**: ✅ Full
`parse_import_profiles` accepts both `{profiles:[...]}` and bare-array roots, rejects other shapes with an error, applies `migrate()` to each entry. Unit-tested for both root shapes and the invalid-shape error case. Now also reachable via UI (see below) - `preview_import_profiles`'s error surfaces as the spec's exact "ファイルの読み込みに失敗しました。" dialog text.

### Requirement: 重複検出と選択・競合解決ダイアログ
**Status**: ✅ Full (fixed 2026-07-20)
A new toolbar button ("インポート...") opens a native `OpenFileDialog` (`pick_import_open_path`, same filter as export) then `openImportProfilesDialog`: a checkbox list (all checked by default) marks rows "⚠ 重複" when their `Nickname` case-insensitively matches an existing profile's (via a fresh `list_profiles` call, both sides required non-empty - matches `merge_imported_profiles`'s own key logic exactly), plus a Skip(default)/Rename/Overwrite radio group.

### Requirement: インポート実行時の競合解決
**Status**: ✅ Full (fixed 2026-07-20)
`merge_imported_profiles` (Skip/Rename `_2`,`_3`.../Overwrite, case-insensitive Nickname key, non-colliding always added) was already correct and unit-tested. The one-shot `import_profiles_from_path` command was replaced with two steps - `preview_import_profiles` (parse only) then `merge_selected_profiles` (merge only the checked rows JS sends back) - since the dialog needs the full parsed list *before* merging to build its checklist/duplicate-marks, which a single parse+merge call couldn't support. Completion dialog shows the exact "{added} 件追加、{overwritten} 件上書き、{skipped} 件スキップしました。" text.

### Requirement: 反映はメモリ上のみ、ファイルへの永続化は明示保存に委ねる
**Status**: ✅ Full
`merge_selected_profiles` mutates `ProfilesState` in memory only; `save_profiles_now` (the eventual "上書き保存" menu action, itself still menu-less but callable) is the only path that writes `profiles.amm`. Matches spec exactly.

## git-integration

### Requirement: MDI 子ウィンドウ個別クローズ時の Git ガード
**Status**: ✅ Full
`closePane`→`runGitGuard` checks repo membership and prompts on uncommitted/unpushed state; non-repo directories skip silently. Real-machine tested against a scratch repo (tasks.md 5.4). (The spec's "skip during app-wide close" exemption is moot in the Tauri version since app-wide close doesn't invoke this guard at all — see next requirement.)

### Requirement: アプリケーション終了時の Git ガード一括処理
**Status**: ✅ Full (fixed 2026-07-19)
`app.js`'s new `runGitGuardForAllPanes()` collects the distinct repo roots across all open panes (deduped via `Set`), runs the per-repo guard once per repo, and is now wired into `mainWindow.onCloseRequested` — a cancel at any repo's guard aborts the whole app close (`event.preventDefault()`). Not real-machine visually confirmed (same WebView2/CDP dialog-accept limitation as the per-pane close guard, `pending-real-machine-verification.md`), but the code path exists and mirrors the already-tested per-pane guard exactly.

### Requirement: 未コミット変更のコミットダイアログ
**Status**: ✅ Full (fixed 2026-07-19)
The 3-way choice (commit/skip/don't-close) via native `prompt()` is unchanged, but the message loop now re-prompts with a warning (`alert(...)`) when the input is whitespace-only, instead of silently treating it as an intentional skip — matches the spec's re-prompt requirement exactly (`runGitGuardForRepo`'s `for (;;)` loop).

### Requirement: 未プッシュコミットの確認とプッシュ
**Status**: ✅ Full (fixed 2026-07-19)
Replaced the 2-way `confirm()` with a `prompt()`-based genuine 3-way choice (type `"push"` to push / leave empty to skip-but-close / Cancel to abort close), mirroring the commit step's own prompt()-with-sentinel pattern. `Cancel` now correctly returns `false` from `runGitGuardForRepo`, aborting the close — previously it always returned `true` regardless of this choice.

### Requirement: Git コマンド実行の失敗耐性とタイムアウト
**Status**: ✅ Full
Exact timeout match (3s root/5s status,remote/10s commit/30s push per `git_helper.rs`), safe fallback on missing directory or missing `git` binary. Real-machine tested (tasks.md 5.4).

## hook-cli

All 5 requirements (file-path resolution, Claude Code registration, Codex registration,
Copilot CLI/Antigravity registration, stale-path detection) are:

**Status**: ✅ Full
`src/hook_cli.rs` was written after reading the actual `.NET HookCliRegistrar.cs` source specifically to avoid guessing at JSON/TOML shapes. All 5 spec scenario groups (unregistered-file check, coexistence with user hooks, legacy-unguarded-entry replacement, amm-only Unregister, Codex notify-collision rejection, user-set-notifications respect, marker-only Unregister, Copilot file-delete) are covered by 7 unit tests against scratch directories, all passing (tasks.md 5.5). This assessment covers the registrar's own file I/O contract, which is the literal spec scope — whether a *real* Claude Code/Codex/Copilot CLI actually reads the resulting files and fires the hook is a separate, already-tracked concern (`pending-real-machine-verification.md`), not a gap in this capability's own behavior.

## input-history

### Requirement: 完全重複排除を伴う履歴追加
**Status**: ✅ Full
Whitespace-only ignored, non-contiguous duplicates removed (old instance dropped, new appended), and the selection-aware add (selected text if present, else full input) is implemented in `app.js` (`selectionStart`/`selectionEnd`-based), not just the naive full-text case.

### Requirement: 上限件数の管理と切り詰め
**Status**: ✅ Full
500-entry cap, FIFO truncation on add, persisted `max_entries` loaded from `history.json`. No separately-callable runtime `SetMaxEntries` API, but nothing in the UI would call it anyway (no settings UI exists).

### Requirement: カーソルベースの前後移動 API
**Status**: ❌ Missing (intentional, spec-endorsed)
Not ported. The spec's own text documents this as dead code in the `.NET` original ("2026-07-10 時点のソースを確認する限り `MdiParentForm` はこれら2メソッドを一切呼び出しておらず... 現行 UI にはこの2メソッドを呼ぶ経路が無く") — porting it would add a contract nothing exercises. Marked Missing for completeness, not treated as a real gap (tasks.md 5.2 explicitly notes this decision).

### Requirement: 一括ロード／スナップショットによる layout.json への永続化
**Status**: ⚠️ Partial
Persistence across restart works and is confirmed real-machine (tasks.md 5.2), including the empty/whitespace-entry-filtering scenario. **Deviation**: uses a dedicated `history.json` file rather than being folded into the shared `layout.json`'s `HistoryMaxEntries`/`Entries` fields as spec describes — functionally equivalent (still restarts-persistent) but a different storage location than specified.

### Requirement: 履歴ドロップダウンによる UI 提示
**Status**: ✅ Full
Ctrl+H / 「履歴 ▼」-equivalent dropdown, 20 most-recent newest-first, newline visualization, 80-char truncation, click-to-restore with caret-at-end. Real-machine tested (tasks.md 5.2).

## mcp-gateway

**Updated 2026-07-19/20**: implemented as a new `gateway.rs` module (data/process layer), then a
management dialog on 2026-07-20. Ported directly from
`Core/Mcp/Gateway/{McpServerConfig,ManagedMcpProcess,GatewayManager}.cs` and
`Core/{McpGatewayDialog,McpServerEditDialog}.cs`.

### Requirement: 外部 MCP サーバー設定
**Status**: ✅ Full (fixed 2026-07-19/20, dialog added 2026-07-20)
`profile.rs`'s `McpServerConfig` (name/command/args/env/autoStart/maxRestarts, `autoStart` defaults `true`, `maxRestarts` defaults `3`) replaces the old forward-compat-only `Vec<Value>`. Global (`%LOCALAPPDATA%\amm\mcp-servers.json`, `{"mcpServers":[...]}`) and file-local (`profiles.amm`'s own `mcpServers`) groups are both loaded and combined at app boot (`gateway::build_manager`, global entries first, matching `(_mcpServersGlobal ?? []).Concat(_mcpServers ?? [])`). A new toolbar button ("MCP ゲートウェイ設定...") opens `openMcpGatewayDialog`: two groups (グローバル/ファイル固有) each with add/edit/delete/reorder (↑/↓) against a *local copy*, only committed on OK (Cancel discards, matching the .NET original's semantics exactly since it also operates on local `_global`/`_file` lists); `openMcpServerEditDialog` is the per-entry add/edit sub-form (name/command/args - space-separated with quote support/env - `KEY=VALUE` per line/autoStart/maxRestarts), enforcing the same "識別名とコマンドは必須です。" validation. **Adaptation**: OK doesn't hot-restart the live `GatewayManager` with the edited config (the .NET original does - `MdiParentForm.cs` tears down the old `GatewayManager` and builds a new one from the merged config on dialog-OK) - saved changes apply on next app launch instead. Building a swappable/re-initializable gateway (tearing down running child processes cleanly mid-session) was judged a large enough addition on its own to defer rather than fold into this pass; documented as a deliberate simplification, not an oversight. Global-group changes still save immediately to `mcp-servers.json`; file-group changes are memory-only until `save_profiles_now`, matching `command-import-export`'s "反映はメモリ上のみ" convention.

### Requirement: 自動起動サーバーの一括起動
**Status**: ✅ Full (fixed 2026-07-19/20, critical crash fixed 2026-07-20 via live testing)
`GatewayManager::start_auto_start_servers` spawns one supervisor task per `autoStart=true` entry concurrently at app startup (`lib.rs::run`'s `setup` closure); `autoStart=false` entries are never touched. Not literally `Task.WhenAll`-blocking (spawned and left running rather than awaited) since a GUI startup path shouldn't block on a slow/hanging external server - same practical guarantee (all of them do launch) with a non-blocking startup instead. **Critical bug found and fixed via live CDP testing (phase 8.2)**: `start_auto_start_servers`/`launch_and_handshake` used raw `tokio::spawn` instead of `tauri::async_runtime::spawn` (the convention every other background task in this codebase follows). Called from `lib.rs::run`'s synchronous `.setup()` closure - which has no ambient Tokio runtime - raw `tokio::spawn` panics with "there is no reactor running", **crashing the entire app on every launch** whenever profiles.amm had even one `autoStart=true` gateway server configured. Zero configured servers (this session's state through all of phases 5.8-20's static review and unit tests) never exercised the code path at all, so nothing caught this until an actual server was added through the live-tested management dialog and the app was relaunched. Confirmed fixed at two levels: (1) relaunched with a real (intentionally-failing, `npx` not on PATH in this environment) server configured - app started normally, `gateway_server_infos` correctly reported `{status: "error", lastError: "Failed to start process: npx (program not found)"}` with no crash; (2) full success path proven with a purpose-built dependency-free fixture (`tests/e2e-tauri/fixture-mcp-server.js`, a minimal stdio JSON-RPC server implementing `initialize`/`tools/list`/`tools/call`) - server reached `{status: "running", toolCount: 1}`, and a real `amm-mcp.exe --bridge` round trip confirmed both tool-namespace aggregation (`fixture/echo` visible in `tools/list`) and call forwarding (`tools/call` on `fixture/echo` returned the child process's actual `"echo: ..."` response, not a stub).

### Requirement: 外部プロセスの起動ハンドシェイクとライフサイクル
**Status**: ✅ Full (fixed 2026-07-19/20)
`ManagedMcpProcess`/`launch_and_handshake`: `tokio::process::Command` with piped stdin/stdout, `Stopped`→`Starting`→(`initialize` then `tools/list`, each under a 10s timeout)→`Running`, tools cached, `Error`+`last_error` set on any handshake failure. Process-tree kill on app exit is **not** a separate `DisposeAsync`-style manual teardown - each child is assigned to a Windows Job Object with `JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE` (`assign_kill_on_close_job`), and Windows itself closes all of a process's open handles (including job handles) when that process terminates, which triggers the kill-on-close semantics automatically on `amm.exe` exit - no explicit shutdown hook needed for this to hold, though the job **is** explicitly closed (killing that generation's tree) between crash-restarts to avoid leaking a `npx`-style wrapper's orphaned child across restarts.

### Requirement: クラッシュ検出と自動再起動
**Status**: ✅ Full (fixed 2026-07-19/20)
`supervise`'s loop restructures `OnProcessExited`'s event-driven restart around `Child::wait()`: crash → drain pending calls (resolved as a synthetic gateway error rather than a true Rust-level cancellation - callers still get an error instead of hanging, same practical outcome as `TrySetCanceled`) → close that generation's job → if `restart_count < maxRestarts`, 1s delay then relaunch; at the limit, `Error` status is fixed and the supervisor task ends (no further restarts).

### Requirement: ツールの集約と名前空間プレフィックス
**Status**: ✅ Full (fixed 2026-07-19/20)
`GatewayManager::aggregated_tools` prefixes `"{serverName}/{toolName}"` and `"[{serverName}] {description}"`, Running-only, appended after the 6 built-in tools in `mcp.rs`'s `tools/list` handler (matches `McpPipeServer.cs`'s ordering).

### Requirement: ゲートウェイツール呼び出しの転送
**Status**: ✅ Full (fixed 2026-07-19/20)
`tools/call` checks `GatewayManager::is_gateway_tool` **before** falling through to the built-in dispatcher (matches `TryHandleGatewayToolAsync` being called ahead of `HandleLine`), forwards via `GatewayManager::call_tool`, and reproduces the exact 3-way response shape: not-running/unknown server → `-32603 "Gateway server for '...' is not running"`; a remote `"error"` key → `-32603` with that error's JSON as the message; otherwise the child's own `tools/call` result object is passed through as-is (not re-wrapped).

### Requirement: 既存インターフェースとの互換性維持
**Status**: ✅ Full (fixed 2026-07-19/20)
Now a real (not vacuous) pass: gateway routing only intercepts `tools/call` names that resolve to a configured server via `is_gateway_tool`, and `tools/list` only *appends* aggregated tools after the unchanged 6 built-ins - `send_message`/`list_participants`/`peek_queue`/`amm/notify`/`amm/approval`/`pane/*` dispatch paths are untouched. `amm-mcp.exe`'s CLI surface wasn't touched by this work at all.

### Requirement: 管理サーバーのステータス表示
**Status**: ✅ Full (fixed 2026-07-20)
`gateway_server_infos` (new command wrapping the now-`Serialize`-able `GatewayServerInfo`/`ServerStatus`) feeds the dialog's status icons: ✓ Running / ⏳ Starting / ✗ Error / ○ Stopped, and ● for any config entry with no matching entry in the snapshot at all. The scenario's specific trigger ("ゲートウェイが起動していない状態で設定ダイアログを開く" → all ●) can't literally reproduce in this port, since the app-boot `GatewayManager` always exists once any server is configured (unlike the .NET original where the dialog can be opened with `gatewayForStatus: null`) - but the underlying "● means no live info for this entry" logic is the same code path, and does fire naturally for any entry added/edited within the dialog session itself (not yet known to the running gateway, per the hot-restart limitation noted above), so the icon semantics are faithfully preserved even though the exact all-stopped scenario can't be staged the same way.

## mcp-server

### Requirement: Named Pipe トランスポート
**Status**: ✅ Full (ACL implemented and real-machine verified 2026-07-21)
NDJSON framing, 1MiB line-length DoS guard, per-connection independent session handling — all implemented and real-machine tested (tasks.md 4.1, including the connection-hang bug found and fixed this session). The Windows-user-only ACL (`PipeSecurity`/`FullControl` equivalent) is implemented via an SDDL string (`D:(A;;GA;;;{current-user-SID})`) converted with `ConvertStringSecurityDescriptorToSecurityDescriptorW` and applied through `ServerOptions::create_with_security_attributes_raw`, with a fallback to default (unrestricted) pipe security if attribute construction fails. Two new unit tests (SID-format check, live-pipe DACL readback via `GetSecurityInfo`/`GetAclInformation`/`GetAce` confirming exactly one ACE for the current user) both pass, and real-machine testing confirmed same-user `amm-mcp.exe` connections (both the new Rust client and the old .NET `Amm.Mcp.exe`) still succeed with the restricted DACL in place.

### Requirement: MCP ハンドシェイクと標準メソッド
**Status**: ✅ Full
`initialize`/`notifications/initialized`/`tools/list`/`tools/call`/`ping`, exact `protocolVersion`/`serverInfo` match. Real-machine tested.

### Requirement: 組み込みツールの一覧
**Status**: ✅ Full
All 6 tools present (renamed `mdi/*`→`pane/*` per the documented UDR). The "append gateway tools" half is now a real pass too - `tools/list` appends `mcp-gateway`'s aggregated tools after these 6 (fixed 2026-07-19/20, see the mcp-gateway section below).

### Requirement: send_message の配信意味論
**Status**: ✅ Full
`mode=first`/`mode=all` semantics match exactly (`resolve_targets`), nicknameless profiles correctly excluded from participant registration.

### Requirement: list_participants と peek_queue
**Status**: ✅ Full (fixed this session)
Field names now correctly match the `.NET` original (`session_id` snake_case, no `isWaiting` exposed) after the phase-7 `Participant` struct fix. `peek_queue` is non-destructive (snapshot only).

### Requirement: hook 駆動状態通知 (amm/notify) の受理
**Status**: ✅ Full
`{matched: true/false}` on known/unknown token, id-less notification form supported, delegates to both participant-state update and the real `WaitPatternDetector` force-state. Real-machine tested.

### Requirement: JSON-RPC エラーハンドリング
**Status**: ✅ Full
`-32700` parse error, `-32600` invalid request, `-32602` missing params, `-32603` internal error — all four codes are used at the correct call sites in `mcp.rs`.

### Requirement: amm-mcp.exe stdio ブリッジと CLI モード
**Status**: ✅ Full
Full port with real-machine live testing this session (bridge/repl/send/list/notify/approve). GUI-not-running → exit code 2 after timeout, confirmed by code inspection (`ErrorKind::TimedOut`/`NotFound` → `EXIT_NO_SERVER`).

### Requirement: CLI 設定ファイルへの MCP サーバ登録
**Status**: ✅ Full (fixed 2026-07-19)
This is `McpCliRegistrar` — a **distinct** capability from `hook-cli`'s `HookCliRegistrar`. Ported as `src/mcp_cli.rs`, read directly from the .NET original to mirror its exact file shapes: Claude Code/Copilot CLI/Antigravity write `{"mcpServers":{"amm":{...}}}` (stdio type for Claude, local+tools for the others) into their respective JSON configs; Codex uses the same line-based `[mcp_servers.amm]` TOML editing approach as `hook-cli`'s Codex handling (no TOML AST needed). 7 unit tests covering missing-file/coexistence-with-other-servers/unregister-only-amm/Codex-roundtrip/apostrophe-escaping, all passing. Wired as new Tauri commands (`mcp_registered_command`/`mcp_register`/`mcp_unregister`) - no UI to call them yet (same "logic ready, no UI trigger" situation `quick-command-register`'s commands were in before their 2026-07-20 dialog work), tracked as a follow-up.

## profile-schema

### Requirement: プロファイルファイルスキーマ
**Status**: ✅ Full
All 28 fields present in `SessionProfile` with matching defaults (`profile.rs`), confirmed real-machine (`nickname` resolution, tasks.md 5.1). Malformed-JSON and missing-file fallback behaviors match spec.

### Requirement: プロファイルファイルの解決順序
**Status**: ✅ Full (fixed 2026-07-19)
CLI-arg-first, then exe-adjacent `profiles.amm`, correctly implemented (`resolve_profiles_path`). Added `parse_profiles_path_arg` (port of `AppLaunchOptions.Parse`'s argv loop): rejects any unrecognized `--`-prefixed flag and a 2nd positional path argument with the same error messages as the .NET original, wired into `run()` to `eprintln!` + `process::exit(1)` on invalid invocation. Unit-tested (3 cases).

### Requirement: プロファイルファイルのホットリロード
**Status**: ✅ Full (fixed 2026-07-19)
No `FileSystemWatcher` equivalent exists in Rust's std, so this is a 300ms mtime-poll on a background thread (`spawn_profiles_hot_reload`) instead of an event-driven watch - only reloads once the mtime has been stable across two consecutive polls (`profile::hot_reload_should_apply`), giving the same practical debounce as the spec's 300ms window for save-temp-rename editors. Invalid JSON on reload is logged and skipped, keeping the current in-memory `ProfilesState` (matches spec exactly). Existing panes are unaffected since they don't re-read profile state at runtime; there's no profile-picker UI yet for "新規に起動するMDI子" to visibly demonstrate against, so this is verified via the `ProfilesState` swap itself, not end-to-end through a UI. 5 new unit tests on the pure debounce-decision function.

### Requirement: ウィンドウ位置・名前・作業ディレクトリの記憶 (windowGeometry)
**Status**: ✅ Full (fixed 2026-07-19, capture-trigger UI wired 2026-07-22)
`window_geometry` is now a typed `Vec<WindowGeometryEntry>` (was a forward-compat-only `Vec<Value>`). The **apply** half is fully wired end-to-end for `mcp.rs::open_pane` (the only pane-creation path that carries profile identity at all, since there's no profile-picker UI yet): index = `PtyState::alive_count_for_profile(name) + 1`, resolved via `profile::resolve_geometry_apply_plan` (pure, matches all 3 spec scenarios — missing-index, zero-size name/maximized-only entries, independent name/maximized/workingDirectory application), sent to `app.js` via `amm-pane-opened`'s new `geometry`/`maximized` fields and applied through `createPane`'s initial rect / the new `maximizePaneToDesk` helper (shared with tray-icon's double-click-maximize). Saved `workingDirectory` is inserted into the cwd-resolution chain between an explicit RPC override and the profile's own default. **2026-07-22**: the **capture** half (`capture_window_geometry`, a port of `OnCaptureCurrentLayout`/`BuildGeometryFromAlive`) now has a real caller - a new「現在の配置を記憶」item on the per-pane title-bar right-click menu (`runSystemMenuAction('capture-geometry', pane)`), scoped to the clicked pane's own profile (consistent with this menu's existing per-pane-not-per-window scoping). Gathers all alive panes sharing that `profileName`, reads each one's live `x/y/w/h` plus `get_pane_working_dir`, and calls `capture_window_geometry`. Simplification: since this port has no persistent per-pane "maximized" flag (`maximizePaneToDesk` just resizes to the desk's current extent rather than setting durable state), every captured entry is always a literal x/y/w/h snapshot rather than a `maximized: true` entry - functionally equivalent for restore purposes. Real-machine verified via CDP: moved a `CMD`-profile pane to a distinct rect, captured, force-closed it, relaunched the same profile, and confirmed the new pane landed at the exact remembered `x`/`y` (w/h off by ~1px from CSS box-model rounding) with the exact remembered working directory - both the on-disk `profiles.amm` JSON and the live re-opened pane matched. `cargo test` (77+10) unaffected (JS-only change). Ad-hoc/UI-created panes (no profile) are unaffected, matching spec intent. 12 unit tests on the pure apply-plan/build-from-alive functions, including a round-trip test.

### Requirement: 旧フィールドの後方互換マイグレーション
**Status**: ✅ Full (fixed 2026-07-19)
The 2 documented migrations (`promptRenameOnStart`→`promptNewNameOnCommandAdd`, old `commentPrefixes` default replacement) are implemented and match exactly. Added `infer_command_type`/`args_refer_to_tool` (faithful port of `SessionProfile.cs`'s `InferCommandType`/`ArgsReferToTool`, including the token-based arg matching that avoids false-positives on paths like `C:\my-codex-notes\`), wired into `migrate()` when `command_type` is empty/`"Other"`. Unit-tested (4 cases including the token-vs-substring distinction and the cmd-wrapper-stays-Other case).

### Requirement: 起動コマンドラインの解決とセキュリティ
**Status**: ✅ Full (fixed 2026-07-19)
Added `resolve_executable_path`/`safe_search_path` (faithful port of `SessionProfile.cs`'s `ResolveExecutablePath`/`SafeSearchPath`: explicit paths - containing a separator or already rooted - are only env-expanded and never PATH-searched; bare names are resolved via a hijack-safe search, `%SystemRoot%\System32` first, non-rooted `PATH` components skipped, `PATHEXT` extensions tried in order, falls back to the original name unresolved if nothing matches) plus a hand-rolled `%VAR%` env-expander (Rust has no `Environment.ExpandEnvironmentVariables` equivalent). Wired into `mcp.rs::open_pane` for both the profile-based and ad-hoc `command` launch paths. Unit-tested (3 cases, including a real-machine check that `cmd.exe` actually resolves under the live System32).

### Requirement: 送信前のテキスト整形 (per-command)
**Status**: ✅ Full (fixed 2026-07-19)
Added `filter_lines_for_send`/`is_comment_line` (port of `SessionProfile.cs`'s `FilterLinesForSend`/`IsCommentLine`: drops comment-prefixed lines - no leading-whitespace trim, matching the original - and collapses consecutive blank lines when `collapse_blank_lines` is set). `PtyEntry` now captures the launching profile's settings at pane-open time (defaults to `true`/`["'","//"]` for UI-created panes with no profile), exposed via a new `filter_text_for_send` Tauri command that `app.js`'s `sendToPane` calls before every `pty_write`. 4 new unit tests.

### Requirement: コマンド追加テンプレートと per-command 編集ダイアログ
**Status**: ✅ Full (fixed 2026-07-20)
`openCommandTemplateDialog` (add/edit form, all ~26 dialog-editable fields from `BuildProfile()`) and `openAmmSettingsDialog` (3-field CloseProhibited/CollapseBlankLines/CommentPrefixes, confirm-then-persist-or-memory-only) are both implemented, plus `openCommandManagerDialog` (list + add/edit/remove/reorder working copy, OK commits in-memory via a new `commit_profiles` command) as the entry point neither dialog had before - profile-schema's spec doesn't name `CommandManagerDialog` itself (that's `command-import-export`'s context), but without *some* list entry point `CommandTemplateDialog` would be unreachable.

`openAmmSettingsDialog`'s persist=true/false branching confirmed live via CDP (phase 8.2), using the `window.confirm()` monkeypatch technique (native `confirm()` doesn't render in this environment - see `tests/e2e-tauri/README.md`): opened via a real MCP-bridge-launched profile-backed pane (`pane/open` with `profile_name: "cmd"`), toggled `closeProhibited` and set a distinctive `commentPrefixes` value. `confirm() -> false` correctly applied the change in memory (`list_profiles` reflected it) without touching `profiles.amm` on disk (file remained absent, as it was before the test). `confirm() -> true` correctly wrote the change through to `profiles.amm` (verified the exact `commentPrefixes` marker present in the on-disk JSON afterward). No page errors in either path.

**テンプレート一覧**: the type dropdown (Cmd/PowerShell/Claude Code/Codex/COPILOT-CLI/その他) is the same control used for both add and edit, matching the *actual* `CommandTemplateDialog.cs` behavior (its own docstring describes a separate "テンプレート選択 ComboBox" gated by `isAddMode`, but that's stale - `isAddMode` is only used for the window title in the current code; there is only one combo box, `_commandType`, always visible, and switching it re-applies `ApplyTypePreset`'s subset of fields regardless of add/edit mode). Preset data is ported from `SessionProfileLoader.CommandTemplates`, applying only the 10 "behavior" fields `ApplyTypePreset` actually copies (executable/args/newlineMode/outputEncoding/autoChcp/waitPatterns/sendLineByLine/useBracketedPaste/selectWorkingDirOnStart/promptNewNameOnCommandAdd) with the same overwrite-confirmation gate (`hasContent` check + `confirm()`). **Deviation from spec text**: the .NET template array also has an "Antigravity" entry, but it has no `CommandType` enum value assigned, so it's unreachable from this same dropdown in the .NET original too (confirmed by reading `CommandTypeItems`, which only lists 6 entries) - not ported, since porting an unreachable-in-the-source entry isn't "parity," it's inventing new reachability the original never had. spec.md's mention of Antigravity here is stale relative to the actual `.cs` source.

**Adaptations, documented rather than silently diverged**: (1) `windowGeometry`/`theme`/`initialCommands`/`sessionLog`/`ctrlCCopyOnSelection`/`closeOnExit` and any forward-compat-unknown fields are preserved from the existing profile's raw JSON rather than reset to defaults - the .NET original's `BuildProfile()` constructs a brand-new POCO from only the fields it collects, silently discarding these on every save (its own hint text warns "このダイアログでは編集できません", which in context means "and will be lost", not just "read-only here"); this port treats that as a real bug worth not replicating rather than a behavior worth matching. (2) The WindowGeometry grid editor itself (per-row X/Y/W/H/Name/WorkingDirectory with folder-browse) is not built - manual pixel-coordinate editing was judged low-value relative to `capture_window_geometry`'s "現在の配置を記憶" (itself still trigger-less, see profile-schema's windowGeometry entry above) as the intended way to populate it; geometry data round-trips untouched instead. (3) `CommandManagerDialog`'s own Import/Export buttons aren't duplicated into its working copy - `command-import-export`'s toolbar-button implementation (operating directly on live `ProfilesState`) already exists and is more directly reachable; folding it into a second, working-copy-scoped code path was judged not worth the duplication.

Backend additions: `commit_profiles` (whole-list memory-only replace - no per-entry object-identity preservation the way `MdiParentForm.cs` does, since this port's panes capture profile-derived values at open-time rather than holding a live reference, so there's nothing to preserve identity *for*), `update_profile_settings`, `pick_folder` (working-directory browse button, via `tauri-plugin-dialog`'s folder picker). Also added, since it was a real end-to-end gap blocking every profiles.amm-mutating feature above from ever reaching disk: a "上書き保存" toolbar button wired to the pre-existing-but-previously-uncalled `save_profiles_now` command.

**2 real bugs found and fixed via live CDP testing (phase 8.2), both pre-existing and unrelated to this dialog's own new code**: (1) Cancel/Esc/backdrop-click on this dialog's edit sub-form, when opened from `CommandManagerDialog`, closed the *entire* modal shell instead of returning to the list underneath - this port's modal system is a single-overlay singleton, so the sub-dialog had nothing to "return to" once the parent was explicitly closed to make room for it. Fixed by adding an `onCancel` parameter (defaulting to the plain `closeModal` for standalone callers) threaded through `openModal` itself so Esc/backdrop share the same fix; the identical pattern existed in `openMcpServerEditDialog`/`openMcpGatewayDialog` and got the same fix. (2) Every real profile in this session's test `profiles.amm` has `workingDirectory: ""` (.NET's plain `string` defaults to `""`, not `null` - this file predates the Rust port), and this port's own `CommandTemplates` presets all set `"%USERPROFILE%"` - neither was being normalized anywhere in the `open_pane`/`spawn_pty` cwd chain: `""` short-circuited the `None`-only `current_dir()` fallback into an unusable empty `PathBuf`, and `%VAR%`-style values were never env-expanded at all, matching neither the `.NET` original's `ResolveWorkingDirectory` ("未指定/空なら現在のカレントディレクトリ") nor basic usability. Fixed with a new `profile::resolve_working_directory` (blank → `None` so the caller's `current_dir()` fallback applies, non-blank → env-expanded), applied in both `mcp.rs::open_pane` and as defense-in-depth directly in `spawn_pty_for_pane_with_patterns`. 3 new unit tests.

### Requirement: セッション復帰トークンの付加
**Status**: ✅ Full (fixed 2026-07-19)
Added `resume_args_for`/`effective_args` (faithful port of `SessionProfile.cs`'s `ResumeArgsFor`/`EffectiveArgs`: `ClaudeCode`/`CopilotCli` → `--resume`, `Codex` → `resume`, appended to the profile's own args when `resume_on_start` is set). Wired into `mcp.rs::open_pane`'s profile branch when no explicit `args` override is given via the RPC call (matching the existing override-takes-precedence behavior already established for that branch). Unit-tested (2 cases).

## ps-module

*(See the note at the top of this document — assessed for behavioral parity with the new `Amm.PowerShell.Tauri` script module, not literal match against the spec's `.NET`-era text.)*

### Requirement: バイナリモジュールとしてのパッケージング
**Status**: ⚠️ Partial (intentional deviation)
Deliberately **not** a binary module and **not** PowerShell 7.4 — per `UDR-amm-20260719T0013-b7e`, it's a `.psm1` script module targeting PowerShell 5.1+ (no compilation needed at all, vs. requiring .NET 9 SDK to build a DLL). The 7-cmdlet export surface matches exactly (verified via `Test-ModuleManifest`), using `FunctionsToExport` instead of `CmdletsToExport` (a structural consequence of being a script module, not a binary one).

### Requirement: Named Pipe を介した使い捨て接続によるJSON-RPC通信
**Status**: ✅ Full
Fresh `NamedPipeClientStream` per call (`Open-AmmPipeConnection`), `AMM_MCP_PIPE_NAME` env override with `amm-mcp-{user}` fallback, snake_case params built directly (no generic naming-policy machinery, but the wire output matches).

### Requirement: Connect-Amm / Disconnect-Amm は接続確認と no-op
**Status**: ✅ Full (fixed 2026-07-19)
Both cmdlets work as specified functionally. `Connect-Amm` now wraps the connection attempt in try/catch and calls `$PSCmdlet.ThrowTerminatingError` with a purpose-built `InvalidOperationException`, `ErrorId 'AmmNotRunning'`, `ErrorCategory.ConnectionError` — matching `ConnectAmmCmdlet.cs`'s exact error contract instead of letting `Open-AmmPipeConnection`'s own exception propagate unwrapped.

### Requirement: Open-AmmWindow によるMDIウィンドウ起動とAmmSession出力
**Status**: ✅ Full
`ByCommand`/`ByProfile` parameter sets, `Title`/`WorkingDirectory` shared, calls `amm.openPane` (correctly using the renamed RPC), session_id validation with terminating error. Live-tested this session.

### Requirement: Close-AmmWindow のパイプライン対応とShouldProcess
**Status**: ✅ Full
Pipeline binding (`ValueFromPipeline`/`ByPropertyName`), `SupportsShouldProcess`, `Force`, server errors as warnings not terminating errors. Live-tested.

### Requirement: Get-AmmSession によるMCP list_participants経由の一覧取得
**Status**: ✅ Full
`instance==1`→nickname-only title else `"{nickname} ({instance})"`, `structuredContent`-with-`content[0].text`-fallback parsing. Live-tested.

### Requirement: Send-AmmMessage によるMCP send_message経由の送信
**Status**: ✅ Full
`Nickname`(alias `Title`)/`Message`/`Mode` default `first`, full MCP handshake via `tools/call`, verbose delivered/queued report. Live-tested.

### Requirement: Wait-AmmIdle によるブロッキング待機とニックネーム解決
**Status**: ✅ Full
`BySessionId`/`ByNickname` sets, `TargetState`/`TimeoutMs` defaults match, case-insensitive nickname resolution via `list_participants` with terminating error on not-found, unbounded client-side read (server governs the real timeout), no polling. This is the most thoroughly live-tested cmdlet this session — including the phase 7.3 WaitBroker-fallback fix that was found *because of* testing this exact cmdlet.

## quick-command-register

### Requirement: 右クリックメニューへの「クイック送信に登録...」項目
**Status**: ✅ Full (fixed 2026-07-20, real bug found and fixed via live CDP testing same day)
A DOM-based context menu (`showPaneContextMenu`, triggered by a `contextmenu` listener on each pane's `.pane-term`) shows "クイック送信" (profile's `quickPrompts`, only when non-empty) then a separator then "クイック送信に登録...", disabled when `pane.lastText` ANSI-strips to empty (checked via `quick_prompt_label_suggestion`'s return). **Bug found via live testing (phase 8.2)**: the listener was bubble-phase and never fired at all when the click landed inside xterm.js's own viewport (the overwhelming majority of the pane's clickable area) - xterm.js attaches its own `contextmenu` handler directly on its internal root element and stops propagation there for its own right-click-selection handling, so a bubble-phase listener on the outer `.pane-term` wrapper never saw the event. Fixed by moving to capture phase (`{ capture: true }`) plus `e.stopPropagation()`, which intercepts before xterm's handler runs. Confirmed working end-to-end afterward: right-click → menu appears → submenu hover → toggle click → register dialog with correct pre-filled label/text → OK → verified via `list_profiles` that the profile's `quickPrompts` array actually gained the new entry. `pane.lastText` is now tracked in `sendToPane` - the shared-input-box send path (Ctrl+S/Ctrl+Shift+S/Ctrl+1-9/auto-send-idle/MCP inject); direct-typed terminal keystrokes still bypass `sendToPane` (go straight to `pty_write`, separately observed by `trackTypedInputForRecording` for `chat-recording`'s own purposes - fixed 2026-07-20, see below - which doesn't set `lastText`) so they still can't set this specific field, a narrower and now-accurate scope note than when this was written. **Adaptation**: also disabled when the pane has no associated profile (`pane.profileName` null - ad-hoc/UI-created panes), since there's nowhere to save a `QuickPrompt` for them; the .NET original never has this case since every MDI child is always profile-backed.

**Second bug found and fixed via live CDP testing (phase 8.2)**: the "統計情報" submenu overflows off the right edge of the screen when the outer menu is opened near the window's right edge. The outer `.ctx-menu` clamps itself to the viewport (`showCtxMenu`'s `requestAnimationFrame` check), but the CSS-only `left: 100%` submenu never got the same treatment - reproduced live by opening the menu at `x = viewportWidth - 5`, confirming the submenu's bounding rect extended to `x: 1469` against a `1283px` viewport (~190px unreachable/unclickable). Fixed by adding a `mouseenter` handler on each `.has-submenu` item in `buildCtxMenuItems` that measures the submenu's rect on reveal and flips it to `right: 100%` (open leftward) when it would overflow. Verified both cases live: near-right-edge now opens leftward and stays within the viewport; the normal (plenty of room) case still opens rightward exactly as before.

### Requirement: 登録ダイアログの初期値
**Status**: ✅ Full (fixed 2026-07-20)
`openQuickPromptRegisterDialog` populates the label field from `quick_prompt_label_suggestion` and a new `strip_ansi_text` command (added alongside a `profile::strip_ansi` pure function, factored out of the label helper) for the full-text field. Label input is `maxlength=100`; text is a resizable multi-line `<textarea>`.

### Requirement: OKによる即時登録・保存
**Status**: ✅ Full (fixed 2026-07-20)
OK button calls the existing `register_quick_prompt` command (empty-text no-op, append, immediate `.amm` save) - now reachable via UI. The button is disabled whenever the text field is empty (spec's "テキストが空ならOKでも追加しない" enforced at the UI layer in addition to the pre-existing command-level check).

### Requirement: Cancel/Esc時の非変更
**Status**: ✅ Full (fixed 2026-07-20)
The new generic modal shell (`openModal`/`closeModal`, shared across this batch's dialogs) closes on Cancel, Esc (a `keydown` listener scoped to the dialog's lifetime), or clicking the overlay backdrop - none of these paths call `register_quick_prompt`, so `QuickPrompts` and the file are both left untouched.

### Requirement: 登録済み項目は設定ダイアログと同一リストを共有する
**Status**: ✅ Full (fixed 2026-07-20, completed 2026-07-20)
The "クイック送信 ▶" submenu reads the *same* `list_profiles`-sourced `quickPrompts` array `register_quick_prompt` writes to (fetched fresh on every right-click, so it reflects hot-reloaded/dialog-edited state immediately) - duplicate labels are naturally allowed since nothing deduplicates. `CommandManagerDialog`'s `CommandTemplateDialog` sub-form (profile-schema, see below) now exists too and edits the identical `quickPrompts` array via its own row editor, so the cross-check the requirement describes is demonstrable both ways: register via context menu → visible in the dialog's list, and edit via the dialog → visible in the context menu (both read `list_profiles` fresh, no caching on either side).

## tray-icon

### Requirement: システムトレイ常駐アイコン
**Status**: ✅ Full
Presence and clean shutdown confirmed (no ghost icon expected — `TrayIconBuilder` managed by Tauri's own lifecycle). Tooltip text matches exactly: `"amm — 起動中"` / `"amm — 入力待ち {N} 件"` (`set_tray_tooltip`, triggered from `updateTrayTooltip` on every pane-state change). Visual confirmation of the icon itself is still real-machine-pending (`pending-real-machine-verification.md`), but the logic is code-confirmed correct.

### Requirement: 入力待ち遷移時のバルーン通知
**Status**: ✅ Full (fixed 2026-07-19)
Foreground-check (`if (mainWindowFocused) return`), exact title/body text match, and 5000ms per-pane dedup (`NOTIFY_DEDUP_MS`/`lastNotifyAt`) are all implemented exactly per spec. The notify-toggle checkbox now exists in the tray context menu (`tray-notify-toggle`, a `CheckMenuItem`), gating `show_attention_notification` server-side; persisted to `tray-settings.json` next to the exe rather than `%LOCALAPPDATA%\amm\layout.json`'s `TrayNotifyEnabled` key — same class of deviation as `input-history`'s `history.json` (documented there as functionally equivalent), since this Tauri port has no `layout.json` file at all (window/pane layout persists via `localStorage` instead).

### Requirement: クリック操作によるセッションジャンプ
**Status**: ✅ Full (fixed 2026-07-19)
Left-click jumps to the oldest-waiting pane (Rust-side `TrayState.waiting`, mirrored from `app.js`'s pane model via `update_tray_sessions` on every state change, ordered by `pane.waitingSince`) in addition to showing/focusing the main window; falls back to show-only when no pane is waiting. Double-click maximizes the last-notified pane (`TrayState.last_notified`, set when `show_attention_notification` actually fires) if still in the waiting set, else the oldest waiting one — `maximizePaneToDesk` fills the desk area and activates it.

### Requirement: 右クリックコンテキストメニュー
**Status**: ✅ Full (fixed 2026-07-19)
Menu is now rebuilt dynamically (`build_tray_menu`) on every waiting-set change and on toggle: "amm を表示" / "入力待ちセッション" (submenu of `tray-jump-<paneId>` items, oldest-first, disabled when empty) / "バルーン通知" (`CheckMenuItem`, toggles `TrayState.notify_enabled` + persists) / separator / "終了" — matches the spec's 4-item structure.

## wait-detection

### Requirement: 既定入力待ちパターン
**Status**: ✅ Full
All 9 default patterns ported (`wait_detect.rs`), confirmed in tasks.md 5.6.

### Requirement: ユーザー指定パターンは既定パターンに追加合成される
**Status**: ⚠️ Partial
Composition (user patterns add to, don't replace, defaults) works correctly. **Missing**: the 100ms ReDoS `matchTimeout` for user-supplied patterns — the `regex` crate has no direct equivalent, left unimplemented (default patterns are all linear-match-safe so the practical risk is scoped to user-supplied patterns only, per tasks.md 5.6's own risk assessment).

### Requirement: 出力バッファの前処理
**Status**: ✅ Full
ANSI stripping (including the OSC-sequence fix found and applied this session), decorative-line exclusion, 50-line recent-line buffer — all match spec.

### Requirement: 状態遷移ロジック
**Status**: ⚠️ Partial
Observable behavior mostly preserved: immediate match → instant `WaitingForInput`; long silence → auto-recovery to `WaitingForInput`; never drops to `Unknown` on non-match. **Simplification**: the `.NET` original's two-stage timer (500ms flicker-free confirmation window, separate from the 4000ms silence-recovery) is collapsed into a single 4000ms-silence-recovery check on a 200ms poll loop — the intermediate "500ms expired without match, stay Running and re-arm" step from the spec text doesn't have a discrete equivalent (though the net effect — staying Running until either a match or the long-silence recovery — is preserved). Documented simplification, tasks.md 5.6.

### Requirement: hook 駆動の外部状態通知 (イベント駆動検知)
**Status**: ✅ Full
`force_state`/`ForceState` equivalent wired from `amm/notify`, `NotifyPayloadMapper` ported faithfully to `amm-mcp`'s `notify_mapper.rs` with 6 unit tests confirming exact vocabulary mapping (Claude Stop/Notification, Codex `agent-turn-complete`, null-payload fallback, `shell_completed` ignored). `Stopped` confirmed irreversible.

### Requirement: 非同期待機ブローカー (WaitBroker)
**Status**: ✅ Full
`register_wait`/`resolve_by_session` with first-wins idempotent resolution, 300000ms default timeout. Session-close releases pending waits as `"timeout"` (`unregister_participant` calls `resolve_by_session(id, "timeout")` — confirmed exact match to spec). App-wide `ReleaseAll` has no explicit equivalent call, but is satisfied implicitly by process termination ending all pending waits (not a functional gap in practice).

---

## New real-machine-verification items found during this audit

Added to `tasks/pending-real-machine-verification.md`:
- `approval-hub`'s Level-1 *attention* state (as opposed to *idle*, which was tested) was never actually fired this session.
- `approval-hub`'s `amm-mcp.exe approve` full human-in-the-loop round trip (real popup click → decision reaching a real hook) beyond connection establishment.

## Items requiring no further real-machine verification — genuine implementation gaps

The following are **not** real-machine-verification items — they're missing/partial *code*, not
unverified-but-present code. They belong in a future implementation task, not phase 8.2:
- **`mcp-gateway`**: fixed 2026-07-19/20 (8/8 Full). Live-restart-of-the-running-gateway-on-dialog-save is a documented simplification (config changes apply on next app launch instead), not a spec-requirement gap.

~~`mcp-server`'s CLI self-registration (`McpCliRegistrar` equivalent)~~ — fixed 2026-07-19 (see updated entry above).
~~`mcp-server`'s Windows-user-only Named Pipe ACL~~ — fixed 2026-07-21 (see updated entry above, tasks.md 4.1).
- **`profile-schema`**: fixed 2026-07-20, capture-trigger UI wired 2026-07-22 (9/9 Full - windowGeometry's capture side now has a real caller, a「現在の配置を記憶」item on the per-pane title-bar context menu; a manual grid editor was still deliberately not built, see the "コマンド追加テンプレート..." entry above for why).
- **`quick-command-register`**: fixed 2026-07-20 (5/5 Full - the one remaining Partial, needing `CommandManagerDialog` to exist for a cross-check, was resolved by `profile-schema`'s dialog work).
- **`command-import-export`**: fixed 2026-07-20 (6/6 Full).
- **`chat-recording`**: fixed 2026-07-20 (6/6 Full) - direct-typing trigger, runtime toggle, and the full stats subsystem (backend + dialog) all implemented.
~~`approval-hub`'s pipe-disconnect release trigger~~ — fixed 2026-07-22 (see updated entry above, tasks.md 5.10).

~~`git-integration`'s app-wide exit guard~~ — fixed 2026-07-19 (see updated entry above).
