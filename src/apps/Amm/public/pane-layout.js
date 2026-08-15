// Pane layout/tiling/docking core: grid computation, positioning,
// drag-to-dock overlay, tray-session sync, status line, quick-switch
// bar, and saved-layout persistence primitives. Split out of app.js
// (2026-07-26 architecture-bloat cleanup, review finding: app.js was
// a single 3429-line file with no internal module boundaries).
// No behavior change, pure move - classic (non-module) scripts
// sharing one global scope, loaded in the same relative order as
// the original single file (see index.html).

const { invoke } = window.__TAURI__.core;
const { listen } = window.__TAURI__.event;
const { getCurrentWindow } = window.__TAURI__.window;

const desk = document.getElementById('desk');
const promptInput = document.getElementById('prompt-input');
const statusLine = document.getElementById('status-line');
const quickSwitchBar = document.getElementById('quick-switch-bar');

const STATE_PREFIX = { Running: '▶', WaitingForInput: '●', Unknown: '?', Stopped: '■' };

let panes = [];
const panesById = new Map();
let nextDisplayId = 1;
let zTop = 1;

// spec: profile-schema's コマンド追加ダイアログ - Windows専用プリセット
// (Cmd/PowerShell)をmacOSでは出さないための判定材料(ユーザー報告)。boot時に
// events-integration.js の起動IIFEが get_platform で解決するまでは既定値
// 'windows' のまま(このコードベースの他のグローバル共有state同様、複数の
// classic scriptファイルにまたがって参照される)。
let currentPlatform = 'windows';
// spec: pane-management's「コマンドタイプ」タブ - Rust側(profile.rs::
// load_command_type_presets)から取得したコマンドタイププリセットのキャッシュ
// (`CommandTypePreset[]`)。boot時のevents-integration.js起動IIFEで解決される
// まではnull(commandTypePresets()/commandTypeLabels()側でその間は空扱いに
// フォールバックする)。
let cachedCommandTypePresets = null;
let activePane = null;
let lastActivePane = null;
let mainWindowFocused = true;

// Pane layout: a deterministic grid computed from the count of alive panes
// and their order in `panes` (replaces the old BSP tiling tree - no
// per-pane ratio/position is persisted anymore, only the order). Order
// changes only via drag-to-swap (title bar dropped onto another pane),
// creation (appended) or close (removed). rows is picked via floor(sqrt(N))
// rather than ceil so cols ends up >= rows: today's displays are mostly
// widescreen, so more columns than rows reads better than a square-ish grid.
let zoomedPane = null; // at most one pane "zoomed" to fill the whole desk at a time

function aliveOrdered() {
  return panes.filter((p) => p.state !== 'Stopped');
}

function computeGridDims(n) {
  const rows = Math.max(1, Math.floor(Math.sqrt(n)));
  const cols = Math.ceil(n / rows);
  return { rows, cols };
}

function positionPane(pane, x, y, w, h) {
  pane.el.style.left = x + 'px';
  pane.el.style.top = y + 'px';
  pane.el.style.width = w + 'px';
  pane.el.style.height = h + 'px';
  pane.fitAddon.fit();
  // FitAddon.fit() clears the render service's cache before resizing
  // whenever cols/rows actually change (xterm.js addon-fit internals) - an
  // explicit refresh forces the renderer to immediately repaint the full
  // buffer at the new size instead of relying on resize() to schedule it,
  // as a defensive measure against a reported blank-pane-after-resize
  // symptom (user report, unverified fix - see retro-pending).
  pane.term.refresh(0, pane.term.rows - 1);
  invoke('pty_resize', { paneId: pane.paneId, cols: pane.term.cols, rows: pane.term.rows }).catch((err) => console.error('pty_resize failed:', err));
}

// Lays out every alive pane into a rows x cols grid (computeGridDims),
// left-to-right then top-to-bottom, in `panes` array order. The last row
// absorbs any remainder - evenly re-split across just that row's panes -
// instead of leaving a gap where missing trailing cells would otherwise be.
function layoutTiles() {
  const alive = aliveOrdered();
  if (alive.length === 0) return;
  if (zoomedPane && alive.includes(zoomedPane)) {
    for (const p of alive) p.el.style.display = p === zoomedPane ? '' : 'none';
    positionPane(zoomedPane, 0, 0, desk.clientWidth, desk.clientHeight);
    return;
  }
  for (const p of alive) p.el.style.display = '';
  const { rows, cols } = computeGridDims(alive.length);
  const areaW = desk.clientWidth, areaH = desk.clientHeight;
  const cellH = Math.floor(areaH / rows);
  const lastRowStart = cols * (rows - 1);
  const lastRowCount = alive.length - lastRowStart;
  alive.forEach((pane, i) => {
    const row = Math.floor(i / cols);
    const isLastRow = row === rows - 1;
    const y = row * cellH;
    const h = isLastRow ? areaH - y : cellH;
    let x, w;
    if (isLastRow) {
      const rowCellW = Math.floor(areaW / lastRowCount);
      const rowCol = i - lastRowStart;
      x = rowCol * rowCellW;
      w = (rowCol === lastRowCount - 1) ? areaW - x : rowCellW;
    } else {
      const cellW = Math.floor(areaW / cols);
      const col = i % cols;
      x = col * cellW;
      w = (col === cols - 1) ? areaW - x : cellW;
    }
    positionPane(pane, x, y, w, h);
  });
}

// Toggles zoomedPane. Trigger is a dedicated title-bar icon (not
// double-click, to avoid ambiguity with the title bar's drag-to-swap
// mousedown handler).
function toggleZoom(pane) {
  zoomedPane = (zoomedPane === pane) ? null : pane;
  if (zoomedPane) { bringToFront(pane); setActivePane(pane); }
  layoutTiles();
}

// Reverts `panes` to launch order (displayId order - assigned once per
// pane at creation and never touched by dragging or restores, so it always
// reflects "the order these panes first came up in", even across restarts),
// undoing any manual drag-reordering, then tops up any autostart profile
// that's short of its configured count (the same top-up startup already
// does - this is its on-demand equivalent, replacing the old dedicated
// "記憶した配置で表示" button now that order has no separate save step to
// distinguish it from - see the button's own history for why it merged
// here instead of just being dropped).
async function resetPaneOrder() {
  zoomedPane = null;
  panes.sort((a, b) => a.displayId - b.displayId);
  layoutTiles();
  updateQuickSwitchBar();
  await topUpAutoStartPanes();
}

// Swaps two panes' slots in the shared order array - the only effect a
// title-bar drag-and-drop has now (the grid is always recomputed from
// count, so there is nothing else to move).
function swapPaneOrder(paneA, paneB) {
  const ia = panes.indexOf(paneA), ib = panes.indexOf(paneB);
  if (ia === -1 || ib === -1 || ia === ib) return;
  [panes[ia], panes[ib]] = [panes[ib], panes[ia]];
}

// --- Drag-to-reorder (title-bar drag) ------------------------------------
// Dragging a pane's title bar onto another pane swaps their grid slots.
// DRAG_THRESHOLD_PX of movement is required before this arms, so a plain
// click (mousedown+mouseup with only sub-pixel pointer jitter in between -
// which happens routinely) is never misread as a completed drag-swap.
const DRAG_THRESHOLD_PX = 4;
let dockOverlayEl = null;

function showDockOverlay(targetPane) {
  hideDockOverlay();
  const rect = targetPane.el.getBoundingClientRect();
  const el = document.createElement('div');
  el.className = 'dock-overlay';
  el.style.left = rect.left + 'px';
  el.style.top = rect.top + 'px';
  el.style.width = rect.width + 'px';
  el.style.height = rect.height + 'px';
  document.body.appendChild(el);
  dockOverlayEl = el;
}

function hideDockOverlay() {
  dockOverlayEl?.remove();
  dockOverlayEl = null;
}

function findPaneUnderPoint(clientX, clientY, excludePane) {
  for (const p of aliveOrdered()) {
    if (p === excludePane) continue;
    const r = p.el.getBoundingClientRect();
    if (clientX >= r.left && clientX <= r.right && clientY >= r.top && clientY <= r.bottom) return p;
  }
  return null;
}

// spec: pane-management - Ctrl+数字送信の対象番号(=panes配列上の1始まり
// インデックス、クイック切替バーの番号と同じ基準)をタイトルバーにも表示する
// (ユーザー要望)。idxを渡さない呼び出し元(updatePaneVisual)ではpanes.
// indexOfで都度求める。並び順自体が変わる操作(ドラッグ入れ替え・並び順
// リセット・ペイン追加/削除)は全て最終的にupdateQuickSwitchBar()を呼ぶので、
// そちらのループで全ペイン分を明示的に再計算し、個々のペインの状態変化
// だけの場合はupdatePaneVisual単体でそのペイン分だけ更新する。
function updatePaneTitleText(pane, idx) {
  const textEl = pane.el.querySelector('.pane-title-text');
  const number = idx ?? panes.indexOf(pane) + 1;
  let prefix = STATE_PREFIX[pane.state] ?? '?';
  if (pane.state === 'WaitingForInput' && pane.attention) prefix = '⚠';
  textEl.textContent = `${number}: ${prefix} ${pane.label}`;
}

function updatePaneVisual(pane) {
  const titleEl = pane.el.querySelector('.pane-title');
  updatePaneTitleText(pane);
  titleEl.classList.remove('state-waiting', 'state-attention');
  pane.el.classList.remove('attention-warn');
  if (pane.state === 'WaitingForInput') {
    titleEl.classList.add(pane.attention ? 'state-attention' : 'state-waiting');
    if (pane.attention) pane.el.classList.add('attention-warn');
  }
  updateQuickSwitchBar();
  updateTrayTooltip();
  updateTraySessions();
}

const NOTIFY_DEDUP_MS = 5000;

function updateTrayTooltip() {
  const waitingCount = panes.filter((p) => p.state === 'WaitingForInput').length;
  invoke('set_tray_tooltip', { waitingCount }).catch(() => {});
}

// spec: tray-icon's 右クリックコンテキストメニュー「入力待ちセッション」/
// クリック操作によるセッションジャンプ - pushes the current waiting set
// (oldest-transitioned-first) to the Rust-side tray so its submenu and
// click/double-click targeting stay in sync. Session bookkeeping (label,
// wait-order) only exists here in the JS pane model, so this is a one-way
// mirror rather than something Rust can compute on its own.
function updateTraySessions() {
  const waiting = panes
    .filter((p) => p.state === 'WaitingForInput')
    .sort((a, b) => (a.waitingSince ?? 0) - (b.waitingSince ?? 0))
    .map((p) => ({ paneId: p.paneId, label: p.label }));
  invoke('update_tray_sessions', { waiting }).catch(() => {});
}

function maybeNotify(pane) {
  if (mainWindowFocused) return;
  const now = Date.now();
  if (pane.lastNotifyAt && now - pane.lastNotifyAt < NOTIFY_DEDUP_MS) return;
  pane.lastNotifyAt = now;
  invoke('show_attention_notification', {
    paneId: pane.paneId,
    title: 'amm: 入力待ち',
    body: `${pane.label} が入力待ちです`,
  }).catch(() => {});
}

function setPaneState(pane, state) {
  if (pane.state === state) return;
  pane.state = state;
  if (state === 'Running' || state === 'Stopped') {
    pane.attention = false;
  }
  if (state === 'WaitingForInput') {
    pane.waitingSince = Date.now();
    maybeNotify(pane);
  }
  updatePaneVisual(pane);
}

function setAttention(pane, attention) {
  if (pane.attention === attention) return;
  pane.attention = attention;
  updatePaneVisual(pane);
  if (attention && !mainWindowFocused) {
    invoke('flash_window').catch(() => {});
  }
  refreshAttentionOverlay();
}

// Real detection (spec: wait-detection) now runs Rust-side, fed from the pty
// reader thread itself (regex prompt matching + a 4000ms silence-recovery
// watcher) - this just applies whatever amm-pane-wait-state reports. The
// phase-2 placeholder (a client-side "no keystrokes for 1200ms" timer) is
// gone; activating a pane still clears its attention flag locally per the
// spec's "アテンションの解除" scenario, which is a UI-only concern the
// backend doesn't need to know about.
function applyWaitState(pane, state, hasAttention) {
  const prevState = pane.state;
  setPaneState(pane, state);
  if (pane === activePane) {
    setAttention(pane, false);
  } else if (hasAttention) {
    setAttention(pane, true);
  }

  // spec: auto-send-idle. Only the Running -> WaitingForInput edge arms a
  // countdown; any other transition (including WaitingForInput -> WaitingForInput,
  // which can't happen since setPaneState no-ops on same-state) cancels one
  // if armed. hasAttention here is the value amm-pane-wait-state carried,
  // not pane.attention post-setActivePane override - matches the spec's
  // "その時点でHasAttention==falseならアーム" (attention wins even for the
  // active pane, since arming should be gated on the real hook/detector
  // signal, not the local "active pane always looks calm" UI convenience).
  if (state !== 'WaitingForInput' || hasAttention) {
    cancelAutoSendTimer(pane);
  } else if (prevState === 'Running' && !pane.autoSendArmed) {
    armAutoSendTimer(pane);
  }
}

function cancelAutoSendTimer(pane) {
  if (pane.autoSendCountdownInterval) clearInterval(pane.autoSendCountdownInterval);
  if (pane.autoSendTimeout) clearTimeout(pane.autoSendTimeout);
  pane.autoSendArmed = false;
  pane.autoSendCountdownInterval = null;
  pane.autoSendTimeout = null;
  if (pane.autoSendOriginalLabel != null) {
    pane.label = pane.autoSendOriginalLabel;
    pane.autoSendOriginalLabel = null;
    updatePaneVisual(pane);
  }
}

function armAutoSendTimer(pane) {
  const settings = pane.autoSendOnIdle;
  if (!settings || !settings.enabled || !settings.prompt) return;
  pane.autoSendArmed = true;

  const fire = () => {
    cancelAutoSendTimer(pane);
    if (pane.attention || pane.state !== 'WaitingForInput' || !panesById.has(pane.paneId)) return;
    sendToPane(pane, settings.prompt);
  };

  if (settings.delayMs <= 0) {
    fire();
    return;
  }

  const deadline = Date.now() + settings.delayMs;
  pane.autoSendOriginalLabel = pane.label;
  const tick = () => {
    const remaining = Math.max(0, Math.ceil((deadline - Date.now()) / 1000));
    pane.label = `${pane.autoSendOriginalLabel} ⏱${remaining}s`;
    updatePaneVisual(pane);
  };
  tick();
  pane.autoSendCountdownInterval = setInterval(tick, 500);
  pane.autoSendTimeout = setTimeout(fire, settings.delayMs);
}

function setActivePane(pane) {
  // spec: approval-hub's 4 release triggers include pane activation, but
  // this must mean a genuine *transition* into active state - not every
  // mousedown inside a pane that's already active. Found via real-machine
  // testing (2026-07-28): a user actively working inside the very pane a
  // PermissionRequest hook fires for (the common case - you're sitting
  // right there when Claude asks) triggers this function on every ordinary
  // click-to-focus, releasing the pending approval within a fraction of a
  // second, before the overlay could ever be seen. Only release when the
  // pane wasn't already the active one; explicit "I acknowledge this"
  // actions (the approval overlay's own 確認 button) call
  // release_approval_on_activate directly instead of relying on this.
  const wasAlreadyActive = !!pane && activePane === pane;
  if (activePane) activePane.el.style.outline = '';
  activePane = pane;
  if (pane) {
    lastActivePane = pane;
    pane.el.style.outline = '1px solid #7aa2ff';
    setAttention(pane, false);
    if (!wasAlreadyActive) {
      invoke('release_approval_on_activate', { paneId: pane.paneId }).then(refreshApprovalOverlay).catch(() => {});
    }
    pane.term.focus();
  }
  updateQuickSwitchBar();
  updateStatusLine();
}

function bringToFront(pane) {
  pane.el.style.zIndex = ++zTop;
}

function updateStatusLine() {
  const target = activePane ? activePane.label : '(なし)';
  statusLine.textContent = `送信先: ${target} — 生存ペイン数: ${aliveOrdered().length}`;
}

// spec: pane-management's new "クイック切替バーの状態表示と操作メニュー"
// requirement (.NET original: MdiParentForm.cs's RefreshMdiButtonBar) -
// priority order (highest first) mirrors the .NET color ladder: attention
// (orange) > active (light-blue) > waiting-for-input (yellow) > stopped
// (gray) > default. This port's panes have no OS-level "minimized" concept
// (always-visible DOM elements), so that .NET state is simply absent here.
function quickSwitchBarClassFor(pane) {
  if (pane.state === 'WaitingForInput' && pane.attention) return 'qsb-attention';
  if (pane === activePane) return 'qsb-active';
  if (pane.state === 'WaitingForInput') return 'qsb-waiting';
  if (pane.state === 'Stopped') return 'qsb-stopped';
  return '';
}

function updateQuickSwitchBar() {
  quickSwitchBar.innerHTML = '';
  panes.forEach((pane, i) => {
    // spec: 並び順が変わる操作は必ずここを通るので、番号表示の全体再計算は
    // このループにまとめる(updatePaneTitleTextの冒頭コメント参照)。
    updatePaneTitleText(pane, i + 1);
    const btn = document.createElement('button');
    const prefix = STATE_PREFIX[pane.state] ?? '?';
    const attentionGlyph = pane.state === 'WaitingForInput' && pane.attention ? '⚠ ' : '';
    btn.textContent = `${i + 1}: ${attentionGlyph}${prefix} ${pane.label}`;
    // spec: pane-management - クイック切替バーのボタンは番号・状態記号・
    // ペイン名しか見えず、クリック/右クリック/長押しでそれぞれ何が起きるか
    // 一見して分からないとの指摘(ユーザー報告)を受け、ホバー時のネイティブ
    // ツールチップ(title属性)で説明する。他のペインアイコン(.pane-menu等)
    // も同じ title 属性のパターンに揃えている。
    btn.title = `クリック: このペインに切り替え\n右クリック: 操作メニュー\n長押し(0.6秒): 共通入力欄の内容をこのペインへ直接送信`;
    if (pane === activePane) btn.classList.add('active');
    const stateClass = quickSwitchBarClassFor(pane);
    if (stateClass) btn.classList.add(stateClass);

    // spec: 旧.NET版のMDI切替バー長押し(600ms)ジェスチャ - あえて通常の
    // クリック(ペイン切替)とは別口で、共通入力欄の内容をこのペインへ直接
    // 送信する「ちょっとした遊び心」の操作(ユーザー方針により意図的なUX
    // 要素として維持)。長押しが成立したら、直後に発火するclick(ペイン
    // 切替)を1回だけ抑止する。
    const LONG_PRESS_MS = 600;
    let longPressTimer = null;
    let suppressNextClick = false;
    btn.addEventListener('mousedown', (e) => {
      if (e.button !== 0) {
        // spec: pane-management - macOS(WKWebView)は<button>要素上での右
        // クリックのmousedown/mouseupを、それ自体はコンテキストメニューを
        // 呼ぶだけのはずなのに、既定の"ボタン活性化"扱いとして合成'click'
        // イベントも追加発火することがある(WebView2/Chromiumでは起きない
        // 挙動)。この合成clickがdocumentの'click'->closeCtxMenu(87行目
        // 付近)を叩き、直後にshowPaneContextMenuで開いたばかりのメニューを
        // 即座に閉じてしまっていた(ユーザー報告: 右クリックメニューが
        // 表示しようとするとすぐに消える)。preventDefault()でこの既定の
        // ボタン活性化そのものを抑止する。
        e.preventDefault();
        return;
      }
      longPressTimer = setTimeout(() => {
        longPressTimer = null;
        suppressNextClick = true;
        sendSharedInputToPane(pane);
      }, LONG_PRESS_MS);
    });
    const cancelLongPress = () => {
      if (longPressTimer) { clearTimeout(longPressTimer); longPressTimer = null; }
    };
    btn.addEventListener('mouseup', cancelLongPress);
    btn.addEventListener('mouseleave', cancelLongPress);
    btn.addEventListener('click', () => {
      if (suppressNextClick) { suppressNextClick = false; return; }
      bringToFront(pane);
      setActivePane(pane);
    });
    // spec: 右クリックで当該ペインの操作メニュー(showPaneContextMenuと同一)。
    btn.addEventListener('contextmenu', (e) => {
      e.preventDefault();
      // spec: same document 'contextmenu' -> closeCtxMenu race as the pane
      // title's own contextmenu handler (see its comment). showPaneContextMenu
      // is async but its first await is conditional (pane.profileName /
      // pane.lastText) - a pane with neither yet set (no profile, never sent
      // anything) resolves the whole function synchronously, so without
      // stopPropagation() the menu it creates got removed by this same event
      // before ever rendering (found via user report: right-click silently
      // did nothing until *something* had been sent through that pane once).
      e.stopPropagation();
      showPaneContextMenu(e.clientX, e.clientY, pane);
    });
    quickSwitchBar.appendChild(btn);
  });
}


