// Pane lifecycle: createPane (spawn + xterm.js wiring), the git-guard
// commit/push confirmation flow, and closePane. Split out of app.js
// (2026-07-26 architecture-bloat cleanup). No behavior change, pure move.

async function createPane(rect, opts) {
  const displayId = rect?.displayId ?? nextDisplayId++;
  nextDisplayId = Math.max(nextDisplayId, displayId + 1);

  const el = document.createElement('div');
  el.className = 'pane';
  el.style.left = (rect?.x ?? 20 + (displayId * 20)) + 'px';
  el.style.top = (rect?.y ?? 20 + (displayId * 20)) + 'px';
  el.style.width = (rect?.w ?? 480) + 'px';
  el.style.height = (rect?.h ?? 300) + 'px';
  el.style.zIndex = ++zTop;
  // spec: profile-schema's titleBarColor - set as a CSS custom property on
  // the pane container so it cascades down to .pane-title (which falls back
  // to the default color via var(--pane-title-bg, #2d2d34) when unset).
  // state-waiting/state-attention keep their own fixed colors regardless
  // (higher-specificity class selectors, see style.css).
  if (opts?.titleBarColor) el.style.setProperty('--pane-title-bg', opts.titleBarColor);
  el.innerHTML = `
    <div class="pane-title">
      <span class="pane-title-text"></span>
      <span class="pane-tool-icon pane-rename" title="名前変更">✎</span>
      <span class="pane-tool-icon pane-font" title="フォントサイズ">A</span>
      <span class="pane-tool-icon pane-editor" title="エディタ連携">📝</span>
      <span class="pane-tool-icon pane-editor-copy" title="エディタ連携ファイルパスをコピー">📋</span>
      <span class="pane-menu" title="メニュー">⋮</span>
      <span class="pane-zoom" title="全画面表示を切り替え">⛶</span>
      <span class="pane-close" title="閉じる">✕</span>
    </div>
    <div class="pane-body"><div class="pane-term"></div></div>
  `;
  desk.appendChild(el);

  const term = new Terminal({
    fontFamily: 'Cascadia Code, Consolas, monospace',
    // spec: profile-schema's fontSize - the launching profile's own default,
    // falling back to the existing 13px default for profile-less panes or
    // profiles that don't set one.
    fontSize: opts?.fontSize ?? 13,
    theme: { background: '#1e1e1e', foreground: '#d4d4d4' },
    allowProposedApi: true,
  });
  const fitAddon = new FitAddon.FitAddon();
  term.loadAddon(fitAddon);
  term.loadAddon(new Unicode11Addon.Unicode11Addon());
  term.unicode.activeVersion = '11';
  term.open(el.querySelector('.pane-term'));
  fitAddon.fit();

  const pane = {
    displayId, el, term, fitAddon,
    paneId: null,
    label: opts?.label ?? `ペイン #${displayId}`,
    state: 'Unknown',
    attention: false,
    idleTimer: null,
  };
  panes.push(pane);

  // spec: quick-command-register's コピー/クイック送信に登録メニュー - cache
  // the most recent non-empty selection here rather than ever calling
  // pane.term.getSelection() reactively from the context-menu code. Found
  // via live testing (2026-08-04, traced with an instrumented
  // getSelection() plus a live on-screen probe overlay): Claude Code's own
  // TUI auto-copies a fresh selection via its own OSC escape-sequence
  // protocol ("copied N chars to clipboard") and then clears xterm's
  // selection almost immediately afterward as part of that same exchange -
  // independent of any mousedown/focus/right-click handling on amm's side.
  // A plain drag-select with zero further interaction already reads back
  // empty within ~100ms, so any later read (even one captured "as early as
  // possible" in a mousedown/contextmenu handler) is already too late.
  // Only ever overwriting the cache on a *non-empty* change means it keeps
  // "the last thing you actually selected" available for these menu
  // actions instead of racing Claude Code's own auto-clear.
  pane.lastSelectionText = '';
  term.onSelectionChange(() => {
    const s = term.getSelection();
    if (s) pane.lastSelectionText = s;
  });

  // MCP's pane/open (spec: pane-management) already spawned the pty on the
  // Rust side before emitting amm-pane-opened - reuse that pane_id instead
  // of spawning a second, orphaned pty.
  pane.paneId = opts?.existingPaneId ?? (await invoke('pty_spawn'));
  panesById.set(pane.paneId, pane);
  pane.el.dataset.paneId = pane.paneId;
  await invoke('pty_resize', { paneId: pane.paneId, cols: term.cols, rows: term.rows }).catch((err) => console.error('pty_resize failed:', err));
  setPaneState(pane, 'Running');
  updatePaneVisual(pane);

  term.onData((data) => {
    invoke('pty_write', { paneId: pane.paneId, data }).catch((err) => console.error('pty_write failed:', err));
  });

  // spec: wait-detection's new "OSC 9 ターミナル通知による attention 検知"
  // requirement - Codex CLI has no hook-based signaling the way Claude
  // Code's Stop/PermissionRequest hooks do (its `[tui]` config instead sets
  // notification_method="osc9", see hook_cli.rs), so this is its only
  // approval-hub channel. A payload mentioning approval/permission is
  // treated as attention, anything else as a plain idle/turn-complete signal
  // (mirrors TerminalChildForm.cs's osc_notify case).
  term.parser.registerOscHandler(9, (data) => {
    const state = /approval|permission/i.test(data) ? 'attention' : 'idle';
    invoke('notify_pane_state', { paneId: pane.paneId, state }).catch(() => {});
    return true;
  });

  // spec: quick-command-register's 右クリックコンテキストメニュー. Capture
  // phase (found via live CDP testing, phase 8.2): xterm.js attaches its own
  // "contextmenu" listener directly on its internal root element and stops
  // propagation there (for its own right-click-selection handling), so a
  // bubble-phase listener on .pane-term never sees the event at all when the
  // click lands inside the terminal viewport. Capturing higher up the tree
  // intercepts it before xterm's own handler runs.
  el.querySelector('.pane-term').addEventListener('contextmenu', (e) => {
    e.preventDefault();
    e.stopPropagation();
    // spec: quick-command-register's コピー/クイック送信に登録メニュー - use
    // the onSelectionChange-fed cache above (pane.lastSelectionText), not a
    // live pane.term.getSelection() call here - see that hook's comment for
    // why a live read is unreliable (Claude Code's own auto-clear races any
    // handler on amm's side, including this one).
    bringToFront(pane);
    setActivePane(pane);
    showPaneContextMenu(e.clientX, e.clientY, pane, { fromTerminal: true, selectionAtOpen: pane.lastSelectionText });
  }, { capture: true });

  el.addEventListener('mousedown', () => {
    bringToFront(pane);
    setActivePane(pane);
  }, true);

  const title = el.querySelector('.pane-title');
  // Drag-to-reorder: dragging the title bar onto another pane swaps their
  // grid slots. Requires DRAG_THRESHOLD_PX of movement before it arms, so a
  // plain click (mousedown+mouseup with only sub-pixel jitter in between)
  // is never misread as a completed drag-swap - see the comment on
  // DRAG_THRESHOLD_PX above for why this matters.
  title.addEventListener('mousedown', (e) => {
    if (e.target.classList.contains('pane-close') || e.target.classList.contains('pane-zoom') || e.target.classList.contains('pane-menu') || e.target.classList.contains('pane-tool-icon')) return;
    bringToFront(pane);
    setActivePane(pane);
    const startX = e.clientX, startY = e.clientY;
    let dragging = false;
    let dockTarget = null;
    const onMove = (ev) => {
      if (!dragging) {
        if (Math.hypot(ev.clientX - startX, ev.clientY - startY) < DRAG_THRESHOLD_PX) return;
        dragging = true;
      }
      const target = findPaneUnderPoint(ev.clientX, ev.clientY, pane);
      if (target !== dockTarget) {
        dockTarget = target;
        if (target) showDockOverlay(target); else hideDockOverlay();
      }
    };
    const onUp = () => {
      document.removeEventListener('mousemove', onMove);
      document.removeEventListener('mouseup', onUp);
      hideDockOverlay();
      if (dragging && dockTarget) {
        swapPaneOrder(pane, dockTarget);
        layoutTiles();
        updateQuickSwitchBar();
        saveLayout();
      }
    };
    document.addEventListener('mousemove', onMove);
    document.addEventListener('mouseup', onUp);
  });

  // spec: pane-management - per-pane title bar menu (tasks.md 8.6.17-b-2),
  // acting on this specific pane directly instead of the OS window's single
  // guessing-based system menu. Mirrors that menu's own item set (see
  // runSystemMenuAction) rather than duplicating it. Available both via
  // right-click on the title bar and the "⋮" icon button (added on user
  // request - right-click alone isn't discoverable), sharing this one item
  // list so they can never drift apart.
  // spec: pane-management - "ペイン設定"系(コマンド設定/現在の配置を記憶)をここへ
  // 集約し、showPaneContextMenu側(ターミナル本体・クイック切替バー)は送信系の
  // 「操作」メニューに専念させる(ユーザー方針、旧.NET版はこの2系統を全メニュー
  // へ重複掲載していたが、この移植ではタイトルバー側だけに一本化する)。
  // 名前変更/フォントサイズ/エディタ連携/エディタ連携ファイルパスをコピーは、
  // ここ(⋮ドロップダウン)からタイトルバー直接のアイコンボタン
  // (.pane-rename/.pane-font/.pane-editor/.pane-editor-copy、下記のクリック
  // ハンドラ参照)へ移設した(ユーザー要望: 頻用操作をドロップダウンの奥に
  // 隠さず一目で見えるようにしたい)。runSystemMenuAction経由の実装自体は
  // 共有したまま、呼び出し口だけをアイコンに変えている。チャット記録・統計情報
  // 機能自体は削除済み(ユーザー要望)。
  const paneTitleMenuItems = (p) => [
    { label: 'コマンド設定...', onClick: () => runSystemMenuAction('settings', p) },
  ];
  title.addEventListener('contextmenu', (e) => {
    e.preventDefault();
    // found live-testing (tasks.md 8.6.17-b-2): without this, the same
    // contextmenu event bubbles up to document's own listener (line ~737)
    // right after showCtxMenu below runs, and that listener immediately
    // closes any menu whose opening click wasn't inside .ctx-menu - which
    // is exactly this one, since it was just created.
    e.stopPropagation();
    bringToFront(pane);
    setActivePane(pane);
    showCtxMenu(e.clientX, e.clientY, paneTitleMenuItems(pane));
  });

  // spec: pane-management - タイトルバー直接のツールアイコン(ユーザー要望:
  // 名前変更/フォントサイズ/エディタ連携/エディタ連携ファイルパスコピーを
  // ⋮メニューの奥に隠さず、一目でクリックできる位置に配置)。実装自体は
  // 既存のrunSystemMenuAction(OSシステムメニュー/Ctrl+E/⋮メニューと共通)を
  // そのまま呼ぶだけで、重複実装にはしない。
  el.querySelector('.pane-rename').addEventListener('click', (e) => {
    e.stopPropagation();
    bringToFront(pane);
    setActivePane(pane);
    runSystemMenuAction('rename', pane);
  });

  el.querySelector('.pane-font').addEventListener('click', (e) => {
    e.stopPropagation();
    bringToFront(pane);
    setActivePane(pane);
    const r = e.currentTarget.getBoundingClientRect();
    showCtxMenu(r.left, r.bottom, Object.keys(FONT_SIZES).map((action) => ({
      label: `${FONT_SIZES[action]}pt`,
      onClick: () => runSystemMenuAction(action, pane),
    })));
  });

  el.querySelector('.pane-editor').addEventListener('click', (e) => {
    e.stopPropagation();
    bringToFront(pane);
    setActivePane(pane);
    runSystemMenuAction('editor-integration', pane);
  });

  el.querySelector('.pane-editor-copy').addEventListener('click', (e) => {
    e.stopPropagation();
    bringToFront(pane);
    setActivePane(pane);
    runSystemMenuAction('copy-editor-path', pane);
  });

  el.querySelector('.pane-menu').addEventListener('click', (e) => {
    // spec: same document 'click' -> closeCtxMenu race as btn-file-menu
    // (see its own comment) - this handler is synchronous, so it needs
    // stopPropagation() too.
    e.stopPropagation();
    bringToFront(pane);
    setActivePane(pane);
    const r = e.currentTarget.getBoundingClientRect();
    showCtxMenu(r.left, r.bottom, paneTitleMenuItems(pane));
  });

  el.querySelector('.pane-close').addEventListener('click', (e) => {
    e.stopPropagation();
    closePane(pane, { shiftHeld: e.shiftKey });
  });

  el.querySelector('.pane-zoom').addEventListener('click', (e) => {
    e.stopPropagation();
    toggleZoom(pane);
  });

  updateQuickSwitchBar();
  updateStatusLine();
  return pane;
}

// spec: git-integration. Native dialogs stand in for GitCommitDialog's
// 3-way choice (deferred, see tasks.md 5.4): prompt() OK+non-empty
// message = commit, OK+empty = skip, Cancel = don't close. A
// whitespace-only message re-prompts with a warning instead of silently
// treating it as a skip (spec requirement, fixed in the phase 8.1 parity
// audit follow-up). The push step also needed a genuine 3-way choice
// (push/skip-but-close/don't-close) that confirm()'s OK/Cancel can't
// express, so it uses the same prompt()-with-sentinel pattern as commit.
// Modal with a commit-message field and three explicit actions (commit /
// skip / cancel), replacing the old prompt()-with-sentinel pattern where
// clearing the pre-filled text and pressing OK meant "skip" - found
// confusing in practice since nothing in the dialog itself explained that
// convention.
// spec: git-integration's close-pane guard - a single dialog now covers
// both commit and push (found confusing as two separate prompts in
// sequence: committing always leaves something newly unpushed, so the
// push prompt fired right after the commit prompt on almost every use,
// reading as "asked about git twice"). "コミット&プッシュして続行" folds
// both steps into one action; plain "コミットして続行" still leaves the
// push for later. The separate no-uncommitted-changes-but-unpushed-commits
// case is intentionally no longer prompted at all - see runGitGuardForRepo.
function askCommitDecision(repoRoot, status) {
  return new Promise((resolve) => {
    const box = openModal(`未コミットの変更があります (${repoRoot})`, () => { closeModal(); resolve({ action: 'cancel' }); });
    box.style.minWidth = '560px';
    const info = document.createElement('div');
    info.className = 'modal-hint';
    info.style.whiteSpace = 'pre-wrap';
    info.style.marginBottom = '10px';
    info.textContent = status;
    box.appendChild(info);
    const input = document.createElement('textarea');
    input.style.minHeight = '90px';
    input.value = 'WIP: 作業を保存';
    addModalField(box, 'コミットメッセージ', input);
    const warn = document.createElement('div');
    warn.style.color = '#d18616';
    warn.style.marginTop = '-6px';
    warn.style.marginBottom = '10px';
    warn.style.display = 'none';
    warn.textContent = 'コミットメッセージが空です。';
    box.appendChild(warn);
    const requireMessage = (onOk) => {
      const message = input.value.trim();
      if (!message) { warn.style.display = ''; return; }
      closeModal();
      onOk(message);
    };
    addModalActions(box, [
      { label: 'キャンセル(閉じない)', onClick: () => { closeModal(); resolve({ action: 'cancel' }); } },
      { label: 'コミットせず続行', onClick: () => { closeModal(); resolve({ action: 'skip' }); } },
      {
        label: 'コミットして続行', primary: true,
        onClick: () => requireMessage((message) => resolve({ action: 'commit', message })),
      },
      {
        label: 'コミット&プッシュして続行',
        onClick: () => requireMessage((message) => resolve({ action: 'commit-and-push', message })),
      },
    ]);
    input.focus();
    input.select();
  });
}

async function runGitGuardForRepo(repoRoot) {
  const status = await invoke('git_status_short', { dir: repoRoot }).catch(() => '');
  if (!status || !status.trim()) return true;
  const decision = await askCommitDecision(repoRoot, status);
  if (decision.action === 'cancel') return false;
  if (decision.action === 'commit' || decision.action === 'commit-and-push') {
    const [code, out, err] = await invoke('git_commit', { dir: repoRoot, message: decision.message });
    if (code !== 0) {
      alert(`コミットに失敗しました:\n${err || out}`);
      return true;
    }
  }
  if (decision.action === 'commit-and-push') {
    const [code, out, err] = await invoke('git_push', { dir: repoRoot }).catch(() => [-1, '', '']);
    if (code !== 0) alert(`プッシュに失敗しました:\n${err || out}`);
  }
  return true;
}

async function runGitGuard(pane) {
  const workDir = await invoke('get_pane_working_dir', { paneId: pane.paneId }).catch(() => null);
  if (!workDir) return true;
  const repoRoot = await invoke('git_repo_root', { dir: workDir }).catch(() => null);
  if (!repoRoot) return true;
  return runGitGuardForRepo(repoRoot);
}

// spec: アプリケーション終了時の Git ガード一括処理. Collects the distinct
// repo roots across all open panes (dedup - two panes in the same repo
// only prompt once) and runs the guard once per repo. Previously entirely
// unwired (found in the phase 8.1 parity audit).
async function runGitGuardForAllPanes() {
  const repoRoots = new Set();
  for (const pane of panes) {
    const workDir = await invoke('get_pane_working_dir', { paneId: pane.paneId }).catch(() => null);
    if (!workDir) continue;
    const repoRoot = await invoke('git_repo_root', { dir: workDir }).catch(() => null);
    if (repoRoot) repoRoots.add(repoRoot);
  }
  for (const repoRoot of repoRoots) {
    if (!(await runGitGuardForRepo(repoRoot))) return false;
  }
  return true;
}

async function closePane(pane, { force = false, shiftHeld = false } = {}) {
  // spec: profile-schema「クローズ禁止設定によるペインクローズガード」-
  // MCPのpane/close(force=true)以外の通常クローズは、Shift押下でなければ
  // ブロックする。
  if (!force && !shiftHeld && pane.profileName) {
    const profiles = await invoke('list_profiles').catch(() => []);
    const profile = profiles.find((p) => p.name === pane.profileName);
    if (profile?.closeProhibited) {
      alert(`${pane.label} はクローズ禁止に設定されています。閉じるには Shift キーを押しながら操作してください。`);
      return;
    }
  }
  if (!force && pane.state !== 'Stopped') {
    if (!(await confirmDialog(`${pane.label} を閉じますか?実行中のセッションが終了します。`))) return;
  }
  if (!force && !(await runGitGuard(pane))) return;
  clearTimeout(pane.idleTimer);
  // spec: editor-integration「ブリッジのクリーンアップ」
  invoke('editor_bridge_cleanup', { paneId: pane.paneId }).catch(() => {});
  await invoke('pty_close', { paneId: pane.paneId }).catch(() => {});
  panesById.delete(pane.paneId);
  panes = panes.filter((p) => p !== pane);
  refreshAttentionOverlay();
  if (zoomedPane === pane) zoomedPane = null;
  pane.term.dispose();
  pane.el.remove();
  if (activePane === pane) setActivePane(lastActivePane !== pane ? lastActivePane : panes[0] ?? null);
  layoutTiles();
  updateQuickSwitchBar();
  updateStatusLine();
  updateTraySessions();
  saveLayout();
}

