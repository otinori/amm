// Generic modal-dialog / context-menu shells, plus the quick-command-
// register dialog, pane right-click context menu, and chat-stats
// dialog. Split out of app.js (2026-07-26 architecture-bloat cleanup).
// No behavior change, pure move.

// ---- Generic modal dialog shell (spec: quick-command-register /
// command-import-export / mcp-gateway / profile-schema dialogs). All
// dynamic/user-controlled text is set via .value/.textContent, never
// interpolated into innerHTML, matching this file's existing convention
// elsewhere (e.g. updatePaneVisual's textContent usage).
let activeModalEscHandler = null;
function closeModal() {
  document.getElementById('modal-overlay')?.remove();
  if (activeModalEscHandler) document.removeEventListener('keydown', activeModalEscHandler);
  activeModalEscHandler = null;
}

// onClose fires for both Esc and backdrop-click, defaulting to the plain
// closeModal. Callers that nest a sub-dialog inside another modal-shell
// dialog (e.g. CommandTemplateDialog opened from CommandManagerDialog) pass
// their own "return to the parent dialog" callback instead, so dismissing
// via Esc/backdrop behaves the same as their explicit Cancel button
// (found needed via live CDP testing, phase 8.2 - see openCommandTemplateDialog).
function openModal(titleText, onClose = closeModal) {
  closeModal();
  const overlay = document.createElement('div');
  overlay.id = 'modal-overlay';
  overlay.className = 'modal-overlay';
  const box = document.createElement('div');
  box.className = 'modal-box';
  const title = document.createElement('div');
  title.className = 'modal-title';
  title.textContent = titleText;
  box.appendChild(title);
  overlay.appendChild(box);
  overlay.addEventListener('mousedown', (e) => { if (e.target === overlay) onClose(); });
  document.body.appendChild(overlay);
  activeModalEscHandler = (e) => { if (e.key === 'Escape') onClose(); };
  document.addEventListener('keydown', activeModalEscHandler);
  return box;
}

// Returns the wrapper `.modal-field` div (not inputEl) so callers that need
// to toggle a field's visibility (e.g. openMcpServerEditDialog's stdio/http
// transport switch) can add/remove the .hidden class on it directly.
function addModalField(box, labelText, inputEl) {
  const field = document.createElement('div');
  field.className = 'modal-field';
  const label = document.createElement('label');
  label.textContent = labelText;
  field.appendChild(label);
  field.appendChild(inputEl);
  box.appendChild(field);
  return field;
}

// spec: pane-management - 数字だけを入力する項目(起動時自動生成数・遅延
// 秒数等)にaddModalField(ラベル上・入力欄下の2行)を使うと1項目で1行まるまる
// 占有して勿体ない、との指摘(ユーザー報告)。ラベルと入力欄を横並びにした
// コンパクト版。返り値は<input type=number>要素そのもの(addModalFieldが
// ラッパーdivを返すのと非対称だが、呼び出し側は常に.valueだけを読むため
// 実害はない)。
function addModalNumberField(box, labelText, value, { min = 0, max } = {}) {
  const field = document.createElement('div');
  field.className = 'modal-field modal-field-inline';
  const label = document.createElement('label');
  label.textContent = labelText;
  const input = document.createElement('input');
  input.type = 'number';
  input.min = String(min);
  if (max != null) input.max = String(max);
  input.value = String(value);
  field.appendChild(label);
  field.appendChild(input);
  box.appendChild(field);
  return input;
}

function addModalActions(box, buttons) {
  const actions = document.createElement('div');
  actions.className = 'modal-actions';
  for (const b of buttons) {
    const btn = document.createElement('button');
    btn.textContent = b.label;
    if (b.primary) btn.classList.add('primary');
    btn.disabled = !!b.disabled;
    btn.addEventListener('click', b.onClick);
    actions.appendChild(btn);
    if (b.ref) b.ref(btn);
  }
  box.appendChild(actions);
  return actions;
}

// spec: quick-command-register/pane-management 等の各種削除・上書き確認 -
// openModal ベースの window.confirm() 代替。Found via live testing
// (2026-08-04): tauri-plugin-dialog 2.7.2 の注入初期化スクリプトが
// window.confirm を invoke('plugin:dialog|confirm', ...) にシムするが、
// 対応する Rust コマンドはそのプラグインバージョンで既に削除済み
// (open/save/message のみ generate_handler! に残る) - confirm() は必ず
// rejected Promise を返す。呼び出し側の多くは `if (!confirm(...)) return;`
// という同期コードだったため、Promise は常に truthy と評価され、
// キャンセル時のガードが一切効かず確認なしで処理が進んでしまっていた
// (削除・上書き系の操作が無防備になる、実質的な確認バイパスバグ)。
function confirmDialog(message) {
  return new Promise((resolve) => {
    const box = openModal('確認', () => { closeModal(); resolve(false); });
    const text = document.createElement('div');
    text.style.cssText = 'white-space:pre-wrap; margin-bottom:14px;';
    text.textContent = message;
    box.appendChild(text);
    addModalActions(box, [
      { label: 'キャンセル', onClick: () => { closeModal(); resolve(false); } },
      { label: 'OK', primary: true, onClick: () => { closeModal(); resolve(true); } },
    ]);
  });
}

function addModalCheckbox(box, labelText, checked) {
  const wrap = document.createElement('label');
  wrap.className = 'modal-radio-row';
  wrap.style.marginBottom = '10px';
  const cb = document.createElement('input');
  cb.type = 'checkbox';
  cb.checked = !!checked;
  wrap.appendChild(cb);
  wrap.appendChild(document.createTextNode(labelText));
  box.appendChild(wrap);
  return cb;
}

// ---- Generic right-click context menu shell ----
function closeCtxMenu() {
  document.getElementById('ctx-menu')?.remove();
}
// spec: pane-management - macOS の Control+トラックパッドクリックは、右
// マウスボタンの物理クリックと違い、contextmenu イベントに加えて通常の
// click イベント(button=0)も同じジェスチャから発火する(WebView2/Chromium
// の物理右クリックでは click は発火しない)。この「おまけ」の click が
// document の click->closeCtxMenu を叩き、開いたばかりのメニューを一瞬で
// 消してしまっていた(ユーザー報告)。showCtxMenu 直後の短い猶予時間内は
// document 由来の自動クローズ(click/contextmenu どちらも)を無視すること
// で吸収する。showCtxMenu 自身が明示的に古いメニューを閉じる際はこの猶予
// を無視して常に閉じる(呼び出し元を closeCtxMenu 直呼びのままにしている
// 理由)。
let ctxMenuOpenedAt = 0;
const CTX_MENU_CLOSE_GRACE_MS = 300;
function closeCtxMenuUnlessJustOpened() {
  if (Date.now() - ctxMenuOpenedAt < CTX_MENU_CLOSE_GRACE_MS) return;
  closeCtxMenu();
}
document.addEventListener('click', closeCtxMenuUnlessJustOpened);
// spec: pane-management - ペイン本体/タイトルバー/クイック切替バーなど、
// 独自のコンテキストメニューを持つ要素はcapture phaseでpreventDefault済み
// (このハンドラまで届かない)。それ以外の場所(ツールバーボタン・空きデスク
// 領域・共通入力欄・ステータス行等)で右クリックすると、ここでpreventDefault
// していなかったためWKWebViewのネイティブメニュー(「再読み込み」「要素の
// 検証」等、アプリのUIと無関係な項目)がそのまま出てしまっていた(ユーザー
// 報告2026-08-05: 「ボタン以外の場所やメニューで右クリックすると謎のメニュー
// が表示される」)。既定で常にpreventDefaultし、ネイティブメニューはどこでも
// 出さない。
document.addEventListener('contextmenu', (e) => {
  e.preventDefault();
  if (!e.target.closest('.ctx-menu')) closeCtxMenuUnlessJustOpened();
});

function buildCtxMenuItems(container, items) {
  for (const item of items) {
    if (item.separator) {
      const sep = document.createElement('div');
      sep.className = 'ctx-menu-sep';
      container.appendChild(sep);
      continue;
    }
    const el = document.createElement('div');
    el.className = 'ctx-menu-item' + (item.disabled ? ' disabled' : '');
    // spec: pane-management's titleBarColor - optional swatch before the
    // label (used by「コマンド ▶」to preview which color a pane will launch
    // with, per user request). Unused by every other showCtxMenu caller
    // since item.colorSwatch is simply undefined for them.
    if (item.colorSwatch) {
      const swatch = document.createElement('span');
      swatch.className = 'ctx-menu-swatch';
      swatch.style.background = item.colorSwatch;
      el.appendChild(swatch);
    }
    const label = document.createElement('span');
    label.textContent = item.label;
    el.appendChild(label);
    if (item.submenu) {
      el.classList.add('has-submenu');
      const arrow = document.createElement('span');
      arrow.className = 'ctx-submenu-arrow';
      arrow.textContent = '▶';
      el.appendChild(arrow);
      const sub = document.createElement('div');
      sub.className = 'ctx-menu ctx-submenu';
      if (item.submenu.length === 0) {
        const empty = document.createElement('div');
        empty.className = 'ctx-submenu-empty';
        empty.textContent = '(なし)';
        sub.appendChild(empty);
      } else {
        buildCtxMenuItems(sub, item.submenu);
      }
      el.appendChild(sub);
      // spec: quick-command-register's submenu (e.g. 統計情報) screen-edge
      // overflow - found via live CDP testing (phase 8.2): the outer ctx-menu
      // clamps itself to the viewport via showCtxMenu's requestAnimationFrame
      // check below, but the CSS-only `left: 100%` submenu never got the same
      // treatment, so it opened ~190px off the right edge of the screen when
      // the parent menu was already flush against it. Flip to the left side
      // on the same overflow check, right before the hover reveal is visible.
      el.addEventListener('mouseenter', () => {
        sub.style.left = '';
        sub.style.right = '';
        const rect = sub.getBoundingClientRect();
        if (rect.right > window.innerWidth) {
          sub.style.left = 'auto';
          sub.style.right = '100%';
        }
      });
      // spec: submenu親項目(フォントサイズ/統計情報等)自体には元々onClickが
      // 無いためクリックリスナーが付いておらず、クリックすると documentの
      // 'click' -> closeCtxMenu(90行目、条件なしで発火)まで素通しでバブル
      // していた。親のラベル部分をクリックしても実際の項目を選んだわけでは
      // ないので、メニュー全体が消えるだけの不可解な挙動になっていた
      // (実機動作確認でのユーザー指摘、2026-07-29)。ここで止めるだけで
      // 十分(サブメニュー自体はCSSのhoverで既に表示されている)。
      el.addEventListener('click', (ev) => { ev.stopPropagation(); });
    } else if (!item.disabled) {
      el.addEventListener('click', (ev) => { ev.stopPropagation(); closeCtxMenu(); item.onClick(); });
    }
    container.appendChild(el);
  }
}

function showCtxMenu(x, y, items) {
  closeCtxMenu();
  const menu = document.createElement('div');
  menu.id = 'ctx-menu';
  menu.className = 'ctx-menu';
  menu.style.left = x + 'px';
  menu.style.top = y + 'px';
  buildCtxMenuItems(menu, items);
  document.body.appendChild(menu);
  ctxMenuOpenedAt = Date.now();
  requestAnimationFrame(() => {
    const rect = menu.getBoundingClientRect();
    if (rect.right > window.innerWidth) menu.style.left = Math.max(0, window.innerWidth - rect.width - 4) + 'px';
    if (rect.bottom > window.innerHeight) menu.style.top = Math.max(0, window.innerHeight - rect.height - 4) + 'px';
  });
}

// ---- quick-command-register (spec: quick-command-register) ----
// spec: quick-command-register - 仕様変更(ユーザー要望、2026-08-04): 以前は
// ドラッグ選択済みテキストが無いとメニュー項目自体が無効化されていたが、
// Claude Codeでは選択状態がすぐ失われる(auto-clear、他CLI経由の送信と違う
// 挙動)ため常に無効に見えてしまっていた。ドラッグ選択が無くてもダイアログ
// 自体は常に開けるようにし(選択中ならその内容、無ければ空でテキスト欄を
// 初期化)、「直前のプロンプトを設定」「クリップボードテキストを設定」の
// 2ボタンで後からテキストを設定できるようにした。
async function openQuickPromptRegisterDialog(pane) {
  const initialText = pane.lastSelectionText || '';
  const label = initialText ? await invoke('quick_prompt_label_suggestion', { text: initialText }).catch(() => '') : '';
  const fullText = initialText ? await invoke('strip_ansi_text', { text: initialText }).catch(() => initialText) : '';

  const box = openModal('クイック送信に登録');
  const labelInput = document.createElement('input');
  labelInput.type = 'text';
  labelInput.maxLength = 100;
  labelInput.value = label;
  addModalField(box, 'ラベル (最大100文字)', labelInput);

  const textArea = document.createElement('textarea');
  textArea.value = fullText;
  addModalField(box, 'テキスト', textArea);

  let okBtn;
  const updateOkEnabled = () => { if (okBtn) okBtn.disabled = textArea.value.length === 0; };
  textArea.addEventListener('input', updateOkEnabled);

  const fillButtonsWrap = document.createElement('div');
  fillButtonsWrap.style.cssText = 'display:flex; gap:6px; margin-bottom:10px;';
  const lastPromptBtn = document.createElement('button');
  lastPromptBtn.type = 'button';
  lastPromptBtn.className = 'modal-btn';
  lastPromptBtn.style.marginTop = '0';
  lastPromptBtn.textContent = '直前のプロンプトを設定';
  lastPromptBtn.addEventListener('click', async () => {
    const text = pane.lastText ?? '';
    textArea.value = text ? await invoke('strip_ansi_text', { text }).catch(() => text) : '';
    updateOkEnabled();
  });
  const clipboardBtn = document.createElement('button');
  clipboardBtn.type = 'button';
  clipboardBtn.className = 'modal-btn';
  clipboardBtn.style.marginTop = '0';
  clipboardBtn.textContent = 'クリップボードテキストを設定';
  clipboardBtn.addEventListener('click', async () => {
    textArea.value = await navigator.clipboard.readText().catch(() => '');
    updateOkEnabled();
  });
  fillButtonsWrap.appendChild(lastPromptBtn);
  fillButtonsWrap.appendChild(clipboardBtn);
  box.appendChild(fillButtonsWrap);

  addModalActions(box, [
    { label: 'キャンセル', onClick: closeModal },
    {
      label: 'OK', primary: true, disabled: textArea.value.length === 0,
      ref: (btn) => { okBtn = btn; },
      onClick: async () => {
        // spec: "テキストが空ならOKでも追加しない" (button is disabled for
        // this already, but re-check in case of a race with input events).
        // 登録先はアプリ全体設定になったため(ユーザー要望2026-08-04)、
        // pane.profileNameの有無はもう問わない。
        if (!textArea.value) { closeModal(); return; }
        await invoke('register_quick_prompt', { label: labelInput.value, prompt: textArea.value }).catch(() => {});
        closeModal();
      },
    },
  ]);
  textArea.focus();
  textArea.select();
}

// spec: 旧.NET版TerminalChildForm.cs's SendNewline - writes a single \r with
// no other processing (no filter_text_for_send, this isn't user text).
function sendNewlineToPane(pane) {
  invoke('pty_write', { paneId: pane.paneId, data: '\r' }).catch((err) => console.error('pty_write failed:', err));
  pane.term.scrollToBottom();
}

// spec: 旧.NET版のResendPreviousPromptAsync相当。オリジナルは常に↑(CSI A,
// シェル/CLI自身の履歴機能に頼る) + 300ms後に\rだったが、実機動作確認で
// 「どのタイミングのものか分からないものが送信される」という指摘を受けた
// (2026-07-29)。CLI側の履歴カーソル位置は不定(直接入力・別の再送信操作等で
// ズレる)ため、amm自身が把握している直近送信テキスト(pane.lastText、
// sendToPaneが送信のたびに更新)があれば、それを確実に再送信する方を優先する
// (filter_text_for_send/sendLineByLine等の通常送信経路をそのまま通るため、
// 挙動も通常送信と一致する)。pane.lastTextが無い場合(amm経由で一度も送信して
// いない、ターミナルへの直接入力のみのペイン)は、従来通りCLI側の履歴機能に
// 頼るフォールバックを残す。
async function resendPreviousPromptToPane(pane) {
  if (pane.lastText) {
    await sendToPane(pane, pane.lastText);
    pane.term.scrollToBottom();
    return;
  }
  invoke('pty_write', { paneId: pane.paneId, data: '\x1b[A' }).catch((err) => console.error('pty_write failed:', err));
  await sleep(300);
  invoke('pty_write', { paneId: pane.paneId, data: '\r' }).catch((err) => console.error('pty_write failed:', err));
  pane.term.scrollToBottom();
}

// spec: 旧.NET版のSendInputToTargetAsync - sends the shared input box's
// current content (or just its selection) to an explicit pane, bypassing
// resolveSendTarget()'s active/last-active/sole-pane guess. Used by both
// the context menu's「プロンプト送信」item and the quick-switch bar's
// long-press gesture (see updateQuickSwitchBar).
function sendSharedInputToPane(pane) {
  const sel = getSendText();
  if (!sel.text) return;
  sendToPaneFiltered(pane, sel.text);
  consumeSendText(sel);
  invoke('add_to_history', { text: sel.text });
  bringToFront(pane);
  setActivePane(pane);
  pane.term.focus();
}

// spec: 旧.NET版のShowContextMenu(ターミナル本体)/BuildMdiButtonContextMenuStrip
// (クイック切替バー)がほぼ同一の項目セットだった(コピー/貼り付け/すべて選択/
// 画面クリアだけがターミナル側限定)のに合わせて共通化。名前変更/フォントサイズ/
// チャット記録/統計情報は「ペイン設定」としてタイトルバーの⋮メニュー
// (paneTitleMenuItems)側に集約したためここには含めない(ユーザー方針)。
// エディタ連携は元々ここに「導線が抜けていた」ための暫定プレースホルダとして
// あったが(旧コメント参照)、実装完了後もペインへの送信系操作という性質が
// 強いため、このメニューにも残す(paneTitleMenuItems側にも別途追加、
// runSystemMenuAction経由で同じ実装を共有するため重複実装ではない)。
async function showPaneContextMenu(x, y, pane, { fromTerminal = false, selectionAtOpen = null } = {}) {
  const items = [];
  items.push({ label: '改行送信', onClick: () => sendNewlineToPane(pane) });
  items.push({ label: 'プロンプト再送信', onClick: () => resendPreviousPromptToPane(pane) });
  items.push({ separator: true });
  // spec: quick-command-register - 仕様変更(ユーザー要望、2026-08-04:
  // 「クイック送信は、アプリ共通の設定で良い。ペイン毎の設定は不要とする」)。
  // 以前はプロファイルごとのquickPromptsだったため、プロファイル未紐付けの
  // ad-hocペインではメニュー自体が出せなかった。アプリ全体で共有する単一
  // リスト(profile::QuickPrompt、quick-prompts.json)に変更したので、
  // pane.profileNameの有無を問わず常に利用できる。
  const quickPrompts = await invoke('get_quick_prompts').catch(() => []);
  if (quickPrompts.length > 0) {
    items.push({
      label: 'クイック送信',
      submenu: quickPrompts.map((qp) => ({ label: qp.label, onClick: () => sendToPane(pane, qp.prompt) })),
    });
    items.push({ separator: true });
  }
  // spec: quick-command-register - 仕様変更(ユーザー要望、2026-08-04):
  // 以前はドラッグ選択済みテキスト(pane.lastText/lastSelectionText)が無い
  // とメニュー項目自体が無効化されていたが、Claude Codeでは選択状態が自動で
  // すぐ失われるため常に無効に見えてしまっていた(agy/Codexでは問題なかった
  // が、CLIによって挙動が割れるのは望ましくない)。ダイアログ自体は常に開け
  // るようにし、テキストの有無はダイアログ側(openQuickPromptRegisterDialog
  // の「直前のプロンプトを設定」「クリップボードテキストを設定」ボタン)で
  // 後から埋められるようにした。登録先も上記の通りアプリ全体設定になった
  // ため、pane.profileNameの有無による無効化はもう不要。
  items.push({
    label: 'クイック送信に登録...',
    onClick: () => openQuickPromptRegisterDialog(pane),
  });
  items.push({ separator: true });
  if (fromTerminal) {
    // spec: quick-command-register - use the onSelectionChange-fed cache
    // the caller passes in (pane.lastSelectionText, see pane-lifecycle.js),
    // not a fresh pane.term.getSelection() here: Claude Code's own TUI
    // auto-clears xterm's selection almost immediately after auto-copying
    // it, well before any handler on amm's side (even one that reacts as
    // early as possible) can read it live.
    const selection = selectionAtOpen != null ? selectionAtOpen : pane.term.getSelection();
    items.push({
      label: 'コピー',
      disabled: !selection,
      onClick: () => { navigator.clipboard.writeText(selection).catch(() => {}); },
    });
    items.push({
      label: '貼り付け',
      onClick: async () => {
        const text = await navigator.clipboard.readText().catch(() => '');
        if (text) invoke('pty_write', { paneId: pane.paneId, data: text }).catch((err) => console.error('pty_write failed:', err));
      },
    });
    items.push({ separator: true });
  }
  items.push({ label: 'プロンプト送信', onClick: () => sendSharedInputToPane(pane) });
  // spec: editor-integration - 実装済み。runSystemMenuAction経由でCtrl+E/
  // ペインタイトルバーメニューと同じ実装に到達する(重複実装を避ける)。
  items.push({ label: 'エディタ連携', onClick: () => runSystemMenuAction('editor-integration', pane) });
  if (fromTerminal) {
    items.push({ separator: true });
    items.push({ label: 'すべて選択', onClick: () => pane.term.selectAll() });
    items.push({ label: '画面クリア', onClick: () => pane.term.clear() });
  }
  showCtxMenu(x, y, items);
}

