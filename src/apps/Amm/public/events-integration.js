// Tauri event-listener wiring (pty-data/pane-opened/pane-close-
// requested/pty-exit/wait-state/tray-jump/system-menu/files-dropped),
// the approval-hub overlay, system-menu action dispatch, drag & drop,
// window-resize debounce, remaining small dialogs (askNewCommandName
// etc.), and the app's startup bootstrap. Split out of app.js
// (2026-07-26 architecture-bloat cleanup). No behavior change, pure
// move.

listen('pty-data', (event) => {
  const pane = panesById.get(event.payload.paneId);
  if (pane) pane.term.write(event.payload.data);
});

// MCP pane/open (spec: pane-management, mcp-server): the pty already exists
// on the Rust side by the time this fires - just materialize the DOM pane.
listen('amm-pane-opened', (event) => {
  const { paneId, label, autoSendOnIdle, geometry, maximized, profileName, fontSize, sendLineByLine, titleBarColor, nickname } = event.payload;
  // spec: profile-schema's windowGeometry "index の欠落は最大化" / "name-only
  // / maximized エントリ" - Rust already resolved which of the two applies
  // (or neither, for a plain untracked pane); this just performs it.
  const rect = geometry ? { x: geometry.x, y: geometry.y, w: geometry.w, h: geometry.h } : null;
  createPane(rect, { existingPaneId: paneId, label, fontSize: fontSize ?? undefined, titleBarColor: titleBarColor ?? undefined }).then((pane) => {
    pane.autoSendOnIdle = autoSendOnIdle ?? null;
    // spec: pane-management - ペインタイトル/クイック切替バーの表示名は起動時
    // に確定したNickNameとする(ユーザー要望)。Rust側のlabel解決が既に
    // nicknameを優先しているため実質pane.labelと同じ値になるが、「名前変更」
    // ボタンがNickNameそのものをrename_pane_nickname経由で変更できるように、
    // 対象があるかどうか(ad-hocペインはnull)を別途保持しておく。
    pane.nickname = nickname ?? null;
    // spec: profile-schema's sendLineByLine - false for ad-hoc panes with no
    // profile, matching every other profile-derived per-pane setting here.
    pane.sendLineByLine = sendLineByLine ?? false;
    // spec: profile-schema - which profile this pane belongs to, for
    // profile-specific UI (コマンド設定 dialog等)。quickPromptsはアプリ全体設定
    // へ変更済み(ユーザー要望2026-08-04)でこの値には依存しない。null for
    // ad-hoc panes with no profile.
    pane.profileName = profileName ?? null;
    // Grid layout replaces free positioning: geometry's x/y/w/h (if any) is
    // no longer used to place the pane - it's simply appended to `panes`
    // (already done inside createPane()) and the grid recomputes for the
    // new count. geometry.maximized still applies, now as a zoom toggle.
    layoutTiles();
    if (maximized) toggleZoom(pane);
    else setActivePane(pane);
  });
});

// MCP pane/close: reuse the same confirm-then-close path as the UI's own
// close button, except force=true (from the tool call) skips the dialog.
listen('amm-pane-close-requested', (event) => {
  const { paneId, force } = event.payload;
  const pane = panesById.get(paneId);
  if (pane) closePane(pane, { force });
});

// MCP send_message direct-inject path (target pane is WaitingForInput).
listen('amm-inject-message', (event) => {
  const { sessionId, message } = event.payload;
  const pane = panesById.get(sessionId);
  if (pane) sendToPane(pane, message);
});

listen('pty-exit', (event) => {
  const pane = panesById.get(event.payload.paneId);
  if (pane) setPaneState(pane, 'Stopped');
});

// spec: pane-management's new "外部 .amm ファイルの自動起動確認" requirement -
// computed once by Rust's .setup() instead of auto-starting mcpServers
// immediately when the opened profiles.amm was given explicitly (CLI arg /
// file association) and hasn't been approved before.
//
// Pulled via check_pending_untrusted_autostart rather than pushed via an
// event: a plain app.emit() from .setup() was found (real-machine check,
// 2026-07-21, tasks.md 8.6.1) to fire before this script has even loaded -
// .setup() runs while the webview is still navigating to index.html, so
// listen() below would never have been registered in time and the event
// was silently dropped on every launch. The confirmation dialog never
// appeared and the untrusted profile's mcpServers just stayed stopped
// forever with no way to approve them. Calling this once here, near the
// top of the script's own synchronous execution, means it can never lose
// that race - .setup() already ran to completion before Tauri's IPC layer
// was even available to answer this invoke() call.
//
// Uses openModal rather than window.confirm(), unlike every other
// confirm() call in this file: those all run inside a click handler (a
// real user gesture), while this one fires automatically at startup with
// no preceding gesture at all. Found via the same real-machine check that
// window.confirm() called that way is unreliable here - Chromium/WebView2
// dialog APIs invoked without a user gesture can be silently suppressed
// (confirmed via CDP: an un-gestured confirm() call errored immediately
// with "dialog.confirm not allowed", never reaching a human at all) - so
// a real native dialog cannot be trusted to actually appear for this one
// call site. openModal sidesteps that entirely since it's just DOM, not a
// browser dialog API.
(async () => {
  const pending = await invoke('check_pending_untrusted_autostart').catch(() => null);
  if (!pending) return;
  const { path, commands } = pending;
  const box = openModal('未承認のプロファイルファイル', () => {
    closeModal();
    invoke('confirm_untrusted_autostart', { approve: false, path }).catch(() => {});
  });
  const msg = document.createElement('div');
  msg.className = 'modal-hint';
  msg.style.marginBottom = '10px';
  msg.textContent = `${path}\n\n以下のコマンドが自動起動されます:\n${commands.join('\n')}\n\n自動起動を許可しますか? (このファイルは以後承認済みとして扱われます)`;
  msg.style.whiteSpace = 'pre-wrap';
  box.appendChild(msg);
  addModalActions(box, [
    { label: '拒否', onClick: () => { closeModal(); invoke('confirm_untrusted_autostart', { approve: false, path }).catch(() => {}); } },
    { label: '許可', primary: true, onClick: () => { closeModal(); invoke('confirm_untrusted_autostart', { approve: true, path }).catch(() => {}); } },
  ]);
})();

listen('amm-pane-wait-state', (event) => {
  const pane = panesById.get(event.payload.paneId);
  if (pane) applyWaitState(pane, event.payload.state, event.payload.hasAttention);
});

// spec: approval-hub Level 2's "非モーダル集約ポップアップ UI".
//
// Originally a second tauri::WebviewWindow (approval-popup.html), built
// on demand by the show_approval_popup command. Root-caused 2026-07-21
// via cdb.exe attached to a live hung process (see
// tasks/pending-real-machine-verification.md item 3.4 and the removed
// show_approval_popup's history in lib.rs for the full stack trace):
// Tauri's IPC on Windows/WebView2 is delivered through the main
// webview's own CDP network interception, so that command always ran on
// the main UI thread nested inside the main webview's own IPC callback.
// Building a second WebviewWindow from there requires a synchronous
// nested wait for a second CoreWebView2Controller whose async creation
// needs a round-trip through the WebView2 browser process - which can't
// respond until the main thread returns from the callback it's already
// stuck inside. Circular wait, no timeout, permanent hang. No amount of
// window-option tweaking could fix this: the deadlock is inherent to
// creating a second WebView2 controller from inside the first one's own
// IPC callback, on the same thread.
//
// Fixed by dropping the second WebviewWindow entirely and rendering the
// approval panel as an in-main-window DOM overlay instead (like every
// other dialog in this app) - see showApprovalOverlay/hideApprovalOverlay
// below and the .approval-overlay rules in style.css. This can never hit
// the deadlock above since it never creates a second WebView2 controller.
// Trade-off accepted: unlike a real always-on-top OS window, this overlay
// is only visible while the amm window itself is visible/foregrounded -
// judged an acceptable cost for an approval UI that actually works, vs.
// a spec-perfect design that permanently hangs the app on first use.
const approvalOverlay = document.getElementById('approval-overlay');
let approvalOverlayCurrent = null;
let approvalOverlayUnlockTimer = null;

async function refreshApprovalOverlay() {
  const entries = await invoke('list_approvals').catch(() => []);
  if (entries.length === 0) {
    approvalOverlayCurrent = null;
    clearTimeout(approvalOverlayUnlockTimer);
    approvalOverlay.classList.add('hidden');
    refreshAttentionOverlay();
    return;
  }
  approvalOverlayCurrent = entries[0];
  approvalOverlay.classList.remove('hidden');
  document.getElementById('approval-count').textContent = `⚠ (1/${entries.length} 件)`;
  document.getElementById('approval-tool').textContent = approvalOverlayCurrent.toolName;
  document.getElementById('approval-input').textContent = approvalOverlayCurrent.toolInput;
  refreshAttentionOverlay();

  // spec: 表示直後500msは誤操作防止のためボタン無効化
  const allowBtn = document.getElementById('approval-allow');
  const gotoBtn = document.getElementById('approval-goto-pane');
  allowBtn.disabled = true;
  gotoBtn.disabled = true;
  clearTimeout(approvalOverlayUnlockTimer);
  approvalOverlayUnlockTimer = setTimeout(() => {
    if (approvalOverlayCurrent) {
      allowBtn.disabled = false;
      gotoBtn.disabled = false;
    }
  }, 500);
}

async function decideApprovalOverlay(decision) {
  if (!approvalOverlayCurrent) return;
  await invoke('resolve_approval', { id: approvalOverlayCurrent.id, decision }).catch(() => {});
  await refreshApprovalOverlay();
}

// spec: approval-hub - 2026-07-27 UX見直し(ユーザー指示)により「許可」
// (即allow)と「確認」(ペインへ移動)の2択のみに簡略化。「拒否」「閉じる」は
// 撤去し、拒否したい場合は「確認」でペインへ移動してCLI本体のネイティブ
// プロンプトで直接操作する導線に一本化した(setActivePane側の
// release_approval_on_activateが「無回答」相当でamm/approvalを解放するため、
// 明示的なresolve_approval(decision: null)呼び出しは不要)。
document.getElementById('approval-allow').addEventListener('click', () => decideApprovalOverlay('allow'));
// spec: approval-hub - "ペインへ移動"(「確認」に改称)。
// ApprovalEntry.token は要求元ペインのid(mcp.rsのamm/approvalのtoken引数と対応)。
// setActivePaneの自動解放は「非アクティブ→アクティブ」の遷移時のみ発火する
// よう変更した(2026-07-28、通常のクリックでの誤解放を防ぐ修正)ため、対象
// ペインが偶然既にアクティブだった場合に備えて、ここで明示的にも解放する
// (「確認」ボタン押下自体が明確な「見た/対応する」の意思表示のため)。
document.getElementById('approval-goto-pane').addEventListener('click', () => {
  if (!approvalOverlayCurrent) return;
  const pane = panesById.get(approvalOverlayCurrent.token);
  invoke('release_approval_on_activate', { paneId: approvalOverlayCurrent.token }).then(refreshApprovalOverlay).catch(() => {});
  if (pane) { bringToFront(pane); setActivePane(pane); }
});

listen('amm-approval-requested', refreshApprovalOverlay);

// spec: 承認要求(amm/approval)を伴わない、より広いTUI選択状態全般
// (Notification hookのpermission_prompt/elicitation_dialog、OSC9由来のattention
// 等)向けの軽量な「確認」ポップアップ。承認オーバーレイと違い個別のtool_name/
// tool_inputは無い(Notification hookのペイロードに含まれないため)ので、
// 対象ペイン数のカウントとラベル表示・単一の「確認」ボタン(ペインへ移動する
// だけで、setActivePaneが呼ぶ既存のsetAttention(false)がそのまま解除を兼ねる)
// のみで構成する。
const attentionOverlay = document.getElementById('attention-overlay');

function refreshAttentionOverlay() {
  // 承認オーバーレイ対象のペイン(構造化されたamm/approval経由)は、そちらの
  // 「確認」導線と重複させないためここでは除外する。
  const approvalPaneId = approvalOverlayCurrent ? approvalOverlayCurrent.token : null;
  const attentionPanes = panes.filter((p) => p.attention && p.paneId !== approvalPaneId);
  if (attentionPanes.length === 0) {
    attentionOverlay.classList.add('hidden');
    return;
  }
  const first = attentionPanes[0];
  attentionOverlay.classList.remove('hidden');
  document.getElementById('attention-count').textContent = `⚠ (${attentionPanes.length} 件)`;
  document.getElementById('attention-tool').textContent = first.label;
}

document.getElementById('attention-confirm').addEventListener('click', () => {
  const approvalPaneId = approvalOverlayCurrent ? approvalOverlayCurrent.token : null;
  const target = panes.find((p) => p.attention && p.paneId !== approvalPaneId);
  if (target) { bringToFront(target); setActivePane(target); }
});

// spec: tray-icon's クリック操作によるセッションジャンプ. Rust picks the
// target pane (oldest waiting / last notified) since session-order
// bookkeeping is mirrored there via updateTraySessions; this side just
// performs the actual DOM focus/bring-to-front the click implies.
listen('tray-jump-pane', (event) => {
  const pane = panesById.get(event.payload.paneId);
  if (pane) { bringToFront(pane); setActivePane(pane); }
});

listen('tray-jump-pane-maximize', (event) => {
  const pane = panesById.get(event.payload.paneId);
  if (pane && zoomedPane !== pane) toggleZoom(pane);
});

const FONT_SIZES = { 'font-huge': 20, 'font-large': 16, 'font-medium': 13, 'font-small': 11, 'font-tiny': 9 };
const STUB_ACTIONS = {};

// spec: editor-integration「エディタ連携の起動」- Ctrl+E/ペインメニュー/OS
// システムメニューの3経路すべてがrunSystemMenuAction経由でここに集約される
// (下のrunSystemMenuAction本体を参照)。共有入力欄の現在値を初期本文として渡す。
async function openEditorLink(pane) {
  if (!pane) return;
  await invoke('editor_link_open', { paneId: pane.paneId, initialContent: promptInput.value || null }).catch((e) => {
    statusLine.textContent = `エディタ連携の起動に失敗しました: ${e}`;
  });
}

async function copyEditorLinkPath(pane) {
  if (!pane) return;
  const path = await invoke('editor_link_copy_path', { paneId: pane.paneId }).catch((e) => {
    statusLine.textContent = `エディタ連携ファイルパスをコピーできませんでした: ${e}`;
    return null;
  });
  if (!path) return;
  await navigator.clipboard.writeText(path).catch(() => {});
  statusLine.textContent = `エディタ連携ファイルパスをコピーしました: ${path}`;
}

// spec: editor-integration「保存監視による自動送信」- Rust側のポーリング
// スレッドがフィルタ適用後に空でないと判定した保存を通知してくる。実際の
// フィルタ適用・送信はsendToPaneFiltered()に委ねる(filter_lines_for_sendは
// 冪等なので二重適用しても結果は変わらない)。エディタ連携はフィルタ適用
// 対象の2経路のうちの1つ(もう1つは共通入力欄、ユーザー要望2026-08-04)。
listen('amm-editor-bridge-send', async (event) => {
  const { paneId, text, postSendAction } = event.payload;
  const pane = panesById.get(paneId);
  if (!pane) return;
  await sendToPaneFiltered(pane, text);
  if (postSendAction === 'Maximize') {
    if (zoomedPane !== pane) toggleZoom(pane);
    bringToFront(pane);
    setActivePane(pane);
    pane.term.focus();
  } else if (postSendAction === 'Focus') {
    bringToFront(pane);
    setActivePane(pane);
    pane.term.focus();
  }
});

// spec: pane-management - factored out of the amm-system-menu-clicked
// listener so a specific pane can be targeted directly (see the
// .pane-title contextmenu listener below) instead of only ever guessing
// via resolveSendTarget(). The Win32 title-bar system menu is a single
// menu shared by the whole OS window (found in the source-diff parity
// audit, tasks.md 8.6.17-b-2: the .NET original had one independent
// system menu per MDI child window, so which pane it acted on was never
// in question - consolidating every pane into DOM divs under one window
// lost that, leaving resolveSendTarget()'s active/last-active/sole-pane
// guess as the only way to pick a target, with real misfire risk once
// more than one pane exists and none is "active").
// spec: profile-schema's windowGeometry「現在の配置を記憶」(capture side of
// OnCaptureCurrentLayout/BuildGeometryFromAlive, counterpart to the apply
// side wired through mcp.rs::open_pane's resolve_geometry_apply_plan).
// 2026-07-29 UX見直し(ユーザー指示): 独立したペインメニュー項目としてでは
// なく、プロファイル保存(上書き保存/名前を付けて保存)のたびに自動で全プロ
// ファイル分を記憶する形に簡略化した(記憶し忘れ・保存し忘れという2段階の
// 手間を無くすため)。ad-hocペイン(profileName無し)は対象外。
// Simplification: this port has no per-pane "maximized" flag to read back
// (toggleZoom's zoomedPane is transient UI state, not persisted per-pane),
// so every captured entry is a literal x/y/w/h snapshot rather than a
// maximized-flag entry. Since add-pane-tiling, these x/y/w/h values are
// also vestigial on the *consuming* side - amm-pane-opened no longer uses
// geometry.x/y/w/h to place a restored pane (it always docks by splitting
// the active pane instead) - captured here only for whatever informational
// value the raw numbers still have, not because anything reads them back
// into a layout.
async function captureAllPaneGeometry() {
  const profileNames = [...new Set(aliveOrdered().map((p) => p.profileName).filter(Boolean))];
  for (const profileName of profileNames) {
    const siblings = aliveOrdered().filter((p) => p.profileName === profileName);
    const panesGeometry = await Promise.all(siblings.map(async (p) => {
      const workingDirectory = await invoke('get_pane_working_dir', { paneId: p.paneId }).catch(() => null);
      return {
        x: p.el.offsetLeft, y: p.el.offsetTop, w: p.el.offsetWidth, h: p.el.offsetHeight,
        maximized: false, name: p.label || null, workingDirectory: workingDirectory || null,
      };
    }));
    await invoke('capture_window_geometry', { profileName, panes: panesGeometry }).catch(() => {});
  }
}

// spec: pane-management's rename dialog - openModal-based, not
// window.prompt(). Found via live testing (2026-08-04): tauri-plugin-
// dialog 2.7.2's injected init script still shims window.confirm() to
// invoke the now-removed 'plugin:dialog|confirm' backend command (only
// open/save/message remain registered - see the plugin's own commands.rs),
// so confirm() always rejects with "Command not found" regardless of
// capabilities granted; window.prompt() was never wired up by Tauri/
// WKWebView at all and just returns null with no dialog shown. Matches
// the "confirm()/alert()/prompt() don't actually show" limitation this
// file already documents elsewhere (see the untrusted-autostart and
// drop-action call sites above) - rename was the one call site that
// hadn't been migrated to the same openModal pattern yet.
function askRenameNickname(currentName) {
  return new Promise((resolve) => {
    const box = openModal('NickName の変更', () => { closeModal(); resolve(null); });
    const input = document.createElement('input');
    input.type = 'text';
    input.value = currentName;
    addModalField(box, '新しい NickName', input);
    addModalActions(box, [
      { label: 'キャンセル', onClick: () => { closeModal(); resolve(null); } },
      {
        label: 'OK', primary: true,
        onClick: () => {
          const name = input.value.trim();
          if (!name) { closeModal(); resolve(null); return; }
          closeModal();
          resolve(name);
        },
      },
    ]);
    input.focus();
    input.select();
  });
}

async function runSystemMenuAction(action, pane) {
  if (action === 'rename') {
    if (!pane) return;
    // spec: pane-management - 「名前変更」はペインの NickName(MCP送信先名、
    // pane.label と同じ値で起動時に確定)そのものを変更する(ユーザー要望、
    // 2026-08-03)。ad-hocペイン(profileName無しでcommand直接起動、
    // pane.nicknameがnull)にはMCP participantエントリ自体が無いため、
    // rename_pane_nicknameは呼ばずpane.labelのみローカルで変更する(従来通り)。
    const next = await askRenameNickname(pane.nickname ?? pane.label);
    if (!next) return;
    if (pane.nickname != null) {
      const ok = await invoke('rename_pane_nickname', { paneId: pane.paneId, nickname: next }).catch(() => false);
      if (!ok) { alert('NickName の変更に失敗しました。'); return; }
      pane.nickname = next;
    }
    pane.label = next;
    updatePaneVisual(pane);
    updateStatusLine();
    return;
  }

  if (action in FONT_SIZES) {
    if (!pane) return;
    pane.term.options.fontSize = FONT_SIZES[action];
    pane.fitAddon.fit();
    invoke('pty_resize', { paneId: pane.paneId, cols: pane.term.cols, rows: pane.term.rows }).catch((err) => console.error('pty_resize failed:', err));
    return;
  }

  // spec: profile-schema's AmmSettingsDialog, triggered from the system
  // menu's "コマンド設定…" item (found stubbed out entirely - no dialog
  // existed; renamed from "AMM 設定…" 2026-08-05, see openCommandSettingsDialog).
  if (action === 'settings') {
    if (!pane) return;
    openCommandSettingsDialog(pane);
    return;
  }

  // spec: editor-integration「エディタ連携の起動」/「エディタ連携ファイルパス
  // のコピー」- Ctrl+E(input-history.js)・ペインタイトルバーメニュー
  // (pane-lifecycle.js)・OSのAMMシステムメニュー(native_ui.rs)の3経路すべて
  // がこのrunSystemMenuAction経由で同じ実装に到達する。
  if (action === 'editor-integration') {
    await openEditorLink(pane);
    return;
  }
  if (action === 'copy-editor-path') {
    await copyEditorLinkPath(pane);
    return;
  }

  if (action in STUB_ACTIONS) {
    statusLine.textContent = STUB_ACTIONS[action];
  }
}

listen('amm-system-menu-clicked', (event) => {
  runSystemMenuAction(event.payload, resolveSendTarget());
});

function insertAtCursor(text) {
  const { selectionStart: s, selectionEnd: e, value } = promptInput;
  promptInput.value = value.slice(0, s) + text + value.slice(e);
  const pos = s + text.length;
  promptInput.selectionStart = promptInput.selectionEnd = pos;
  promptInput.focus();
}

function quotePathIfNeeded(p) {
  return p.includes(' ') ? `"${p}"` : p;
}

// spec: pane-management's "ファイルドラッグ&ドロップ" - "入力欄およびペイン領域への
// ファイルドロップ". Dropping directly on a pane sends straight to that
// pane's shell input buffer (matches TerminalChildForm.cs's per-pane
// SendText), not the shared common input bar; dropping elsewhere (the
// input row itself, or outside any pane) uses the shared input bar as
// before. text/pathContent mirrors the .NET original: path mode never
// appends a newline (inserted for the user to review/edit, not executed);
// content mode does (matches SendText's default appendEnter=true).
function depositDroppedText(targetPane, text, { appendEnterInPane }) {
  if (targetPane) {
    invoke('pty_write', { paneId: targetPane.paneId, data: appendEnterInPane ? `${text}\r` : text }).catch((err) => console.error('pty_write failed:', err));
  } else {
    insertAtCursor(text);
  }
}

listen('amm-files-dropped', async (event) => {
  const { paths, allText, cssX, cssY } = event.payload;
  if (!paths || paths.length === 0) return;

  const targetPane = panesById.get(document.elementFromPoint(cssX, cssY)?.closest('.pane')?.dataset.paneId);

  if (!allText) {
    depositDroppedText(targetPane, paths.map(quotePathIfNeeded).join(' '), { appendEnterInPane: false });
    return;
  }

  // spec: ドロップ後の確認ダイアログ(内容 / パス / キャンセルの3択、
  // TerminalChildForm.cs's AskDropAction相当). Found via a real-machine
  // check (phase 8.2): browser-native confirm() called from this OS-drag-
  // drop-triggered event handler returns a Promise object instead of
  // blocking and returning a boolean in this environment (matches the
  // "confirm()/alert()/prompt() don't actually show" limitation documented
  // in tests/e2e-tauri/README.md, apparently not limited to CDP-driven
  // sessions). ask_drop_action uses a native Win32 dialog with 3 custom-
  // labeled buttons over IPC instead, both sidestepping that issue and
  // (per user feedback) restoring the .NET original's genuine 3-way
  // choice that a plain OK/Cancel confirm() can't express.
  const dropAction = await invoke('ask_drop_action', { fileCount: paths.length });
  if (dropAction === 'cancel') {
    return;
  }
  if (dropAction === 'path') {
    depositDroppedText(targetPane, paths.map(quotePathIfNeeded).join(' '), { appendEnterInPane: false });
    return;
  }

  try {
    const contents = await Promise.all(paths.map((p) => invoke('read_text_file', { path: p })));
    const totalSize = contents.reduce((sum, c) => sum + c.length, 0);
    if (totalSize > 1_000_000 && !(await window.__TAURI__.dialog.confirm(`合計${(totalSize / 1_000_000).toFixed(1)}MBあります。挿入しますか?`))) {
      return;
    }
    depositDroppedText(targetPane, contents.join('\n'), { appendEnterInPane: true });
  } catch (err) {
    statusLine.textContent = `ファイル読み込みエラー: ${err}`;
  }
});

// spec: レビュー指摘「window.resizeにデバウンスがなくドラッグ中に全ペイン分の
// pty_resizeが連打される」対応 - ウィンドウのドラッグリサイズ中は resize
// イベントが連続発火するため、最後のイベントから150ms静止してから
// layoutTiles(→各ペインのpty_resize)を実行する。
let windowResizeDebounceTimer = null;
window.addEventListener('resize', () => {
  clearTimeout(windowResizeDebounceTimer);
  windowResizeDebounceTimer = setTimeout(() => {
    layoutTiles();
  }, 150);
});

// spec: pane-management's new "コマンドメニューによるプロファイルのペイン起動"
// requirement - the .NET original's primary way for a human to start a
// Claude Code/Codex/etc pane (previously only reachable via MCP/PowerShell
// in this port, since "ペイン追加" always launches an unconfigured bare
// shell). launch_profile_pane reuses the exact same open_pane path MCP's
// pane/open uses (mcp.rs::open_pane_for_gui), so the resulting pane behaves
// identically either way.
// Non-interactive: launches at the profile's own configured/resolved cwd,
// no dialog. Used by topUpAutoStartPanes (autostart at app launch and via
// "並び順をリセット") - a folder-picker dialog firing for every auto-started
// pane on every app launch would be disruptive, so this path suppresses it
// entirely, matching the old .NET version's MCP-launched panes (which set
// SelectWorkingDirOnStart=false for the exact same reason - see
// launchProfileFromMenu below for the interactive counterpart).
async function launchProfilePane(profileName, workingDirectory) {
  try {
    await invoke('launch_profile_pane', { profileName, workingDirectory: workingDirectory ?? null });
  } catch (err) {
    alert(`「${profileName}」の起動に失敗しました。\n\n${err}`);
  }
}

function basenameOfPath(path) {
  if (!path) return '';
  const trimmed = path.replace(/[\\/]+$/, '');
  const parts = trimmed.split(/[\\/]/);
  return parts[parts.length - 1] || '';
}

// spec: profile-schema's promptNewNameOnCommandAdd「新しい名前の入力」 -
// suggests the chosen working directory's folder name (if one was picked
// this launch) else falls back to the source profile's own name, matching
// PromptNewCommandName's own suggestion logic. Re-shows inline instead of
// closing on empty/duplicate names so the typed draft isn't lost.
function askNewCommandName(sourceName, suggested, existingNames, initialColor) {
  return new Promise((resolve) => {
    const box = openModal(`「${sourceName}」の新しい名前`, () => { closeModal(); resolve(null); });
    const input = document.createElement('input');
    input.type = 'text';
    input.value = suggested;
    addModalField(box, '新しい名前', input);

    // spec: pane-management's titleBarColor - inherits the source profile's
    // color by default (user request: the new-name dialog should also let
    // you override the clone's title bar color, not just its name).
    const titleColorWrap = document.createElement('div');
    titleColorWrap.style.cssText = 'display:flex; gap:6px; align-items:center;';
    const titleColorInput = document.createElement('input');
    titleColorInput.type = 'color';
    let titleBarColorValue = initialColor ?? null;
    titleColorInput.value = titleBarColorValue ?? '#2d2d34';
    titleColorInput.addEventListener('input', () => { titleBarColorValue = titleColorInput.value; });
    const titleColorResetBtn = document.createElement('button');
    titleColorResetBtn.type = 'button';
    titleColorResetBtn.className = 'modal-btn';
    titleColorResetBtn.style.marginTop = '0';
    titleColorResetBtn.textContent = '既定に戻す';
    titleColorResetBtn.addEventListener('click', () => { titleBarColorValue = null; titleColorInput.value = '#2d2d34'; });
    titleColorWrap.appendChild(titleColorInput);
    titleColorWrap.appendChild(titleColorResetBtn);
    addModalField(box, 'タイトルバー色', titleColorWrap);

    const warn = document.createElement('div');
    warn.style.color = '#d18616';
    warn.style.marginTop = '-6px';
    warn.style.marginBottom = '10px';
    warn.style.display = 'none';
    box.appendChild(warn);
    addModalActions(box, [
      { label: 'キャンセル', onClick: () => { closeModal(); resolve(null); } },
      {
        label: 'OK', primary: true,
        onClick: () => {
          const name = input.value.trim();
          if (!name) { warn.textContent = '名前を入力してください。'; warn.style.display = ''; return; }
          if (existingNames.some((n) => n.toLowerCase() === name.toLowerCase())) {
            warn.textContent = `同名のコマンドが既に存在します: ${name}`;
            warn.style.display = '';
            return;
          }
          closeModal();
          resolve({ name, titleBarColor: titleBarColorValue });
        },
      },
    ]);
    input.focus();
    input.select();
  });
}

// spec: profile-schemaのSelectWorkingDirOnStart(「コマンド」メニューから
// 起動する都度、作業ディレクトリを尋ねる per-command設定) - 旧.NET版の
// OpenTerminalに相当。選んだフォルダはこのインスタンスだけの上書きで、
// プロファイル自身のworkingDirectoryは変更しない。ダイアログをキャンセル
// したら起動そのものを中止する(旧版と同じ)。
//
// spec: profile-schemaのpromptNewNameOnCommandAdd - selectWorkingDirOnStart
// の後段として、新しい名前を尋ね、元プロファイルを複製して新規プロファイル
// として追加してから、その複製で起動する(旧.NET版OpenTerminalの
// isCommandAdd && PromptNewNameOnCommandAdd経路と同じ順序)。複製は
// promptNewNameOnCommandAdd/selectWorkingDirOnStartを両方falseにし、
// autoStartCountを0、windowGeometryを空にして再発動しないようにする。
async function launchProfileFromMenu(profile) {
  let workingDirectory;
  if (profile.selectWorkingDirOnStart) {
    const picked = await invoke('pick_working_dir_for_launch', {
      profileName: profile.name,
      initialDir: profile.workingDirectory || null,
    }).catch(() => null);
    if (!picked) return;
    workingDirectory = picked;
  }

  let launchName = profile.name;
  if (profile.promptNewNameOnCommandAdd) {
    const existing = await invoke('list_profiles').catch(() => []);
    const suggested = (workingDirectory && basenameOfPath(workingDirectory)) || profile.name;
    const nameResult = await askNewCommandName(profile.name, suggested, existing.map((p) => p.name), profile.titleBarColor);
    if (!nameResult) return;
    const clone = {
      ...profile,
      name: nameResult.name,
      nickname: normalizeNickname(nameResult.name),
      workingDirectory: workingDirectory ?? profile.workingDirectory,
      titleBarColor: nameResult.titleBarColor,
      promptNewNameOnCommandAdd: false,
      selectWorkingDirOnStart: false,
      autoStartCount: 0,
      windowGeometry: [],
    };
    try {
      await invoke('add_profile', { profile: clone });
    } catch (err) {
      alert(`プロファイルの追加に失敗しました。\n\n${err}`);
      return;
    }
    launchName = clone.name;
  }

  await launchProfilePane(launchName, workingDirectory);
}

document.getElementById('btn-command-menu').addEventListener('click', async (e) => {
  // e.currentTarget is nulled out by the browser once the click event
  // finishes dispatching, so it must be captured before the `await` below
  // - reading it afterward throws "Cannot read properties of null" and
  // silently kills the menu (caught nowhere, so the button does nothing).
  const rect = e.currentTarget.getBoundingClientRect();
  const profiles = await invoke('list_profiles').catch(() => []);
  const items = profiles.map((p) => ({
    label: `${p.name}${p.nickname ? `  [${p.nickname}]` : ''}`,
    // spec: pane-management's titleBarColor - same resolution order as the
    // Rust-side launch-time fallback (own color, else the command type's
    // preset default, else the base default so every row still gets a
    // swatch for visual alignment).
    colorSwatch: p.titleBarColor ?? commandTypePresets()[p.commandType]?.titleBarColor ?? '#2d2d34',
    onClick: () => launchProfileFromMenu(p),
  }));
  // spec: pane-management - 「コマンド」ボタンのメニューから直接コマンドを
  // 追加できるようにする(ユーザー要望: 「コマンドを管理...」を別途開かない
  // と新規追加できないのが分かりにくい)。既存コマンドが0件の場合もこの
  // 項目だけは常に表示する(以前はここでalert()して即returnしていた)。
  if (items.length > 0) items.push({ separator: true });
  items.push({ label: '+ コマンド追加...', onClick: () => openQuickAddCommandDialog() });
  // spec: pane-management - 「コマンドを管理...」もこのメニューへ集約
  // (ユーザー要望: ツールバーの独立ボタンではなく「コマンド」メニューの
  // 「コマンド追加」の下にまとめてほしい)。
  items.push({ label: 'コマンドを管理...', onClick: () => openCommandManagerDialog() });
  showCtxMenu(rect.left, rect.bottom, items);
});

// spec: pane-management「記憶した配置の復元」の「未起動分は自動起動数設定に
// 従って追加起動する」- 各プロファイルの autoStartCount に対し、現在その
// プロファイルから起動済みの生存ペイン数が不足している分だけ launchProfilePane
// で追加起動する。
async function topUpAutoStartPanes() {
  const profiles = await invoke('list_profiles').catch(() => []);
  for (const p of profiles) {
    const want = p.autoStartCount ?? 0;
    if (want <= 0) continue;
    const alive = aliveOrdered().filter((pane) => pane.profileName === p.name).length;
    for (let i = alive; i < want; i++) {
      await launchProfilePane(p.name);
    }
  }
}

document.getElementById('btn-approval-test').onclick = async () => {
  await refreshApprovalOverlay();
  if (!approvalOverlayCurrent) alert('保留中の承認要求はありません。');
};

// spec: pane-management - ツールバー右側の独立ボタンだった「MCP ゲートウェイ
// 設定...」「CLI への MCP 登録...」を「設定」メニューへ集約(ユーザー要望:
// ツールバーの並びを整理したい)。以前は単に「設定」だったが、アプリ全体の
// 送信フォーマット設定(全般設定)用に新設した「設定▶」ボタンと区別するため
// 「AI設定▶」へ改称した(ユーザー要望、2026-08-04)。
document.getElementById('btn-settings-menu').addEventListener('click', (e) => {
  e.stopPropagation();
  const rect = e.currentTarget.getBoundingClientRect();
  showCtxMenu(rect.left, rect.bottom, [
    { label: 'MCP ゲートウェイ設定...', onClick: () => openMcpGatewayDialog() },
    { label: 'CLI への MCP 登録...', onClick: () => openCliRegistrationDialog() },
  ]);
});

// spec: 送信前のテキスト整形(連続空行をまとめる/コメント記号)がコマンド
// ごとの設定からアプリ全体設定へ変わったことに伴う新設ボタン(「AI設定▶」の
// 左、ユーザー要望2026-08-04)。将来の全般設定項目もここに追加していく想定。
document.getElementById('btn-general-settings-menu').addEventListener('click', (e) => {
  e.stopPropagation();
  const rect = e.currentTarget.getBoundingClientRect();
  showCtxMenu(rect.left, rect.bottom, [
    { label: '全般設定...', onClick: () => openGeneralSettingsDialog() },
  ]);
});

async function saveProfilesNow() {
  try {
    await captureAllPaneGeometry();
    await invoke('save_profiles_now');
    // spec: 「上書き保存」は現在アクティブな.ammファイルへ保存する(File>開く/
    // 名前を付けて保存でアクティブパスが切り替わっていることがある)。以前は
    // ここで固定文字列'profiles.amm'を表示しており、実際の保存先と食い違って
    // いた(ユーザー報告: どこに保存されたのか分からない)。
    const path = await invoke('get_active_profiles_path').catch(() => 'profiles.amm');
    statusLine.textContent = `${path} へ保存しました。`;
  } catch (err) {
    alert(`保存に失敗しました。\n\n${err}`);
  }
}

// spec: 旧.NET版のファイル→「開く」相当 - アクティブな.amm自体を切り替える
// (以後の上書き保存・ホットリロードも新パスへ向く。既存の生存ペインには一切
// 触れない)。旧版はダイアログを閉じた瞬間のShift押下で自動起動を抑止したが、
// このポートではネイティブファイルダイアログを開いている間キー状態を追跡でき
// ない(フォーカスがOSダイアログ側に移るため)ので、Shiftの隠れた規約ではなく
// 読み込み後の明示的なconfirm()に置き換えている(git guardの確認ダイアログを
// はい/いいえ/キャンセルの明示ボタンへ置き換えたのと同じ方針)。
async function openProfilesFileDialog() {
  const path = await invoke('pick_open_profiles_path').catch(() => null);
  if (!path) return;
  let profiles;
  try {
    profiles = await invoke('open_profiles_file', { path });
  } catch (err) {
    alert(`AMM ファイルの読み込みに失敗しました。\n\n${err}`);
    return;
  }
  statusLine.textContent = `${path} を開きました(${profiles.length} 件のコマンド)。`;
  if (await confirmDialog('このファイルの自動起動設定(autoStartCount)に従って、未起動のコマンドを起動しますか?')) {
    await topUpAutoStartPanes();
  }
}

// spec: 旧.NET版のファイル→「名前を付けて保存」相当 - 現在のメモリ上のプロ
// ファイル一覧を新しいパスへ書き込み、以後の上書き保存もそちらへ向くように
// アクティブパスを切り替える。
async function saveProfilesAsDialog() {
  const path = await invoke('pick_save_as_profiles_path', { defaultFileName: 'profiles.amm' }).catch(() => null);
  if (!path) return;
  try {
    await captureAllPaneGeometry();
    await invoke('save_profiles_as', { path });
    statusLine.textContent = `${path} へ保存しました。`;
  } catch (err) {
    alert(`保存に失敗しました。\n\n${err}`);
  }
}

document.getElementById('btn-file-menu').addEventListener('click', (e) => {
  // spec: document's own 'click' -> closeCtxMenu listener (below) has no
  // target exclusion and runs on every click's bubble phase - btn-command-
  // menu's handler is async and only calls showCtxMenu() after an awaited
  // invoke(), so by the time it runs this click has already finished
  // bubbling and closeCtxMenu can't touch it. This handler has no such
  // await, so without stopPropagation() the menu it just created would get
  // removed by that same document listener before the user ever sees it
  // (found via user report: menu never appeared to open at all).
  e.stopPropagation();
  const rect = e.currentTarget.getBoundingClientRect();
  showCtxMenu(rect.left, rect.bottom, [
    { label: '開く...', onClick: openProfilesFileDialog },
    { label: '上書き保存', onClick: saveProfilesNow },
    { label: '名前を付けて保存...', onClick: saveProfilesAsDialog },
  ]);
});
document.getElementById('btn-tile').onclick = resetPaneOrder;

for (const rowId of ['quick-switch-bar', 'input-row', 'status-line']) {
  const toggle = document.getElementById(`toggle-${rowId}`);
  if (!toggle) continue;
  toggle.addEventListener('click', () => {
    document.getElementById(rowId).classList.toggle('hidden');
    toggle.classList.toggle('toggled-off');
  });
}

const mainWindow = getCurrentWindow();
mainWindow.onFocusChanged(({ payload: focused }) => { mainWindowFocused = focused; });

mainWindow.onCloseRequested(async (event) => {
  const running = panes.filter((p) => p.state !== 'Stopped');
  if (running.length > 0) {
    const names = running.map((p) => p.label).join(', ');
    const ok = await confirmDialog(`実行中のセッションがあります: ${names}\n終了しますか?`);
    if (!ok) {
      event.preventDefault();
      return;
    }
  }
  if (!(await runGitGuardForAllPanes())) {
    event.preventDefault();
    return;
  }
  // spec: pane-management「終了時の確認ガード」の「未保存プロファイル変更の
  // 確認」。2026-07-27: 実機の人間動作確認で、旧prompt()ベースの3択
  // (「はい」と入力/空欄/キャンセル)が分かりにくい(ネイティブダイアログの
  // タイトルが"tauri.localhostの内容"という無意味な表示になる、空欄OKの
  // 意味が非自明、保存先が示されない)との指摘を受け、他のダイアログと同じ
  // openModal ベースの3ボタンモーダルへ置き換えた。保存先は既存の「上書き
  // 保存」(save_profiles_now)と同じ、現在アクティブな.ammファイルへの
  // 無確認上書きのまま変更していない(保存先ピッカーは出さない、この挙動は
  // ツールバーの「上書き保存」ボタンと一貫させるため) - パスをダイアログ
  // 本文に明示することで「どこに保存されるか分からない」点だけを解消する。
  if (await invoke('has_unsaved_profile_changes').catch(() => false)) {
    const path = await invoke('get_active_profiles_path').catch(() => 'profiles.amm');
    const choice = await confirmUnsavedProfileChanges(path);
    if (choice === 'cancel') {
      event.preventDefault();
      return;
    }
    if (choice === 'save') {
      try {
        await invoke('save_profiles_now');
      } catch (err) {
        alert(`保存に失敗しました。\n\n${err}`);
      }
    }
  }
});

function confirmUnsavedProfileChanges(path) {
  return new Promise((resolve) => {
    const box = openModal('プロファイルに未保存の変更があります', () => { closeModal(); resolve('cancel'); });
    const msg = document.createElement('div');
    msg.style.cssText = 'font-size:13px; line-height:1.6; margin-bottom:10px; white-space:pre-wrap;';
    msg.textContent = `以下のファイルへの変更が保存されていません:\n${path}\n\n終了する前に保存しますか?`;
    box.appendChild(msg);
    addModalActions(box, [
      { label: 'キャンセル', onClick: () => { closeModal(); resolve('cancel'); } },
      { label: '保存せず終了', onClick: () => { closeModal(); resolve('discard'); } },
      { label: '保存して終了', primary: true, onClick: () => { closeModal(); resolve('save'); } },
    ]);
  });
}

(async () => {
  // spec: profile-schema's コマンド追加ダイアログ - Cmd/PowerShellプリセット
  // をmacOSで出さないための判定材料を、他の起動処理より先に解決しておく
  // (ユーザー報告)。
  currentPlatform = await invoke('get_platform').catch(() => 'windows');
  // spec: pane-management's「コマンドタイプ」タブ(ユーザー要望) - 型プリセット
  // はRust側(profile.rs::load_command_type_presets)がFile>開くとは独立した
  // アプリ全体設定として保持する。起動時に一度取得してキャッシュし、
  // commandTypePresets()/commandTypeLabels()(dialogs-profile-mcp.js)へ同期的に
  // 提供する(全呼び出し元が同期前提のため)。コマンドタイプ編集画面でOKした
  // 際もこのキャッシュをその場で更新し、再起動なしに反映する。
  cachedCommandTypePresets = await invoke('get_command_type_presets').catch(() => []);
  // spec: pane-management「起動時のペイン自動起動」- 起動時に生成するペインは
  // 各プロファイルの autoStartCount 設定のみに従う(profiles.amm を経由しない
  // 起動は一切行わない)。旧実装はブラウザ localStorage に保存した前回終了時の
  // 生存ペイン数を、プロファイルと無関係な素のシェル(cmd.exe/PowerShell)で
  // 機械的に復元しており、profiles.amm を一度も開いていない/未設定の状態でも
  // インストーラの再実行・再インストール時にlocalStorageだけが残っていると
  // コマンドが自動起動して見える不具合報告があった(ユーザー報告)。復元される
  // のは表示上の生存数だけでどのプロファイル/コマンドだったかは元々保存して
  // おらず、常に無関係な空シェルにしかならない機能だったため、この経路自体を
  // 廃止し autoStartCount 一本化した。
  await topUpAutoStartPanes();
  if (panes.length > 0) setActivePane(panes[0]);
  layoutTiles();
})();
