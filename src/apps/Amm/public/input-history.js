// Send-history dropdown (keyboard-navigable) and the shared input
// box's keydown handling (Ctrl+S/Ctrl+Shift+S/Ctrl+digit send,
// double-Esc clear, Ctrl+H history). Split out of app.js (2026-07-26
// architecture-bloat cleanup). No behavior change, pure move.

const historyDropdown = document.getElementById('history-dropdown');
// spec: 送信履歴のキーボードのみでの選択(ユーザー要望) - Up/Downで
// ハイライトを移動、Enterで確定。マウスクリックとは独立した状態として
// 追跡する(historyItems()が.disabledのプレースホルダを除外するので、
// 履歴0件時はハイライト対象が無く何も起きない)。
let historyHighlightIndex = -1;

function historyItems() {
  return Array.from(historyDropdown.querySelectorAll('.history-item:not(.disabled)'));
}

function setHistoryHighlight(index) {
  const items = historyItems();
  items.forEach((el) => el.classList.remove('highlighted'));
  if (items.length === 0) { historyHighlightIndex = -1; return; }
  historyHighlightIndex = Math.max(0, Math.min(index, items.length - 1));
  const el = items[historyHighlightIndex];
  el.classList.add('highlighted');
  el.scrollIntoView({ block: 'nearest' });
}

function selectHighlightedHistoryItem() {
  const items = historyItems();
  if (historyHighlightIndex < 0 || historyHighlightIndex >= items.length) return false;
  items[historyHighlightIndex].click();
  return true;
}

function hideHistoryDropdown() {
  historyDropdown.classList.add('hidden');
  historyDropdown.innerHTML = '';
  historyHighlightIndex = -1;
}

async function showHistoryDropdown() {
  const recent = await invoke('get_recent_history', { n: 20 });
  historyDropdown.innerHTML = '';
  if (recent.length === 0) {
    const item = document.createElement('div');
    item.className = 'history-item disabled';
    item.textContent = '(履歴なし)';
    historyDropdown.appendChild(item);
  } else {
    for (const entry of recent) {
      const item = document.createElement('div');
      item.className = 'history-item';
      const visible = entry.replace(/\r\n|\r|\n/g, '↵');
      item.textContent = visible.length > 80 ? visible.slice(0, 80) + '…' : visible;
      item.title = entry;
      item.addEventListener('click', () => {
        promptInput.value = entry;
        promptInput.selectionStart = promptInput.selectionEnd = promptInput.value.length;
        hideHistoryDropdown();
        promptInput.focus();
      });
      historyDropdown.appendChild(item);
    }
  }
  historyDropdown.classList.remove('hidden');
  setHistoryHighlight(0);
}

document.addEventListener('click', (e) => {
  if (!historyDropdown.contains(e.target) && e.target.id !== 'prompt-input') hideHistoryDropdown();
});

// spec: Ctrl+S / Ctrl+数字(ペイン指定送信)は「送信後に共通入力欄を消すか」
// を同じ1つのフラグで判断する(ユーザー要望 - 個別に決めると片方だけ直し
// 忘れて食い違う)。Ctrl+Shift+S(全ペイン一斉送信)は対象外、常に消去する
// (ブロードキャストは元々挙動が別)。
const CLEAR_INPUT_AFTER_TARGETED_SEND = false;

// spec: Escを共通入力欄内で既定時間内に2回押すと入力欄全体を消去する
// (ユーザー要望 - 従来Ctrl+Xの2度押しで行っていたクリア手段をEscへ変更。
// 意図しない誤クリアを避けるため、履歴ドロップダウンを閉じるだけのEsc押下は
// 2度押しの1回目としてカウントしない)。
let lastEmptyEscAt = 0;
const DOUBLE_ESC_MS = 500;

promptInput.addEventListener('keydown', (e) => {
  if (e.isComposing) return;

  if (e.key === 'Escape') {
    if (!historyDropdown.classList.contains('hidden')) {
      e.preventDefault();
      hideHistoryDropdown();
      return;
    }
    e.preventDefault();
    const now = Date.now();
    if (now - lastEmptyEscAt < DOUBLE_ESC_MS) {
      promptInput.value = '';
      lastEmptyEscAt = 0;
    } else {
      lastEmptyEscAt = now;
    }
    return;
  }

  // spec: 送信履歴のキーボードのみでの選択(ユーザー要望) - ドロップダウンが
  // 開いている間だけUp/Down/Enterを奪う(通常のテキスト編集を妨げないため)。
  if (!historyDropdown.classList.contains('hidden')) {
    if (e.key === 'ArrowDown') {
      e.preventDefault();
      setHistoryHighlight(historyHighlightIndex + 1);
      return;
    }
    if (e.key === 'ArrowUp') {
      e.preventDefault();
      setHistoryHighlight(historyHighlightIndex - 1);
      return;
    }
    if (e.key === 'Enter' && !e.shiftKey && selectHighlightedHistoryItem()) {
      e.preventDefault();
      return;
    }
  }

  // spec: 常時Ctrl+Sで送信(旧「Ctrl+Sで送信」トグルは廃止 - 共通入力欄に
  // フォーカスがある時だけ発火するので、他のキー割り当てと衝突しない)。
  if (e.key === 's' && e.ctrlKey && !e.shiftKey) {
    e.preventDefault();
    const target = resolveSendTarget();
    if (!target) return;
    const sel = getSendText();
    sendToPaneFiltered(target, sel.text);
    if (CLEAR_INPUT_AFTER_TARGETED_SEND) consumeSendText(sel);
    invoke('add_to_history', { text: sel.text });
    return;
  }

  if (e.key === 'S' && e.ctrlKey && e.shiftKey) {
    e.preventDefault();
    const sel = getSendText();
    aliveOrdered().forEach((p) => sendToPaneFiltered(p, sel.text));
    consumeSendText(sel);
    invoke('add_to_history', { text: sel.text });
    return;
  }

  const digitMatch = /^Digit([1-9])$|^Numpad([1-9])$/.exec(e.code);
  if (digitMatch && e.ctrlKey) {
    e.preventDefault();
    const n = parseInt(digitMatch[1] ?? digitMatch[2], 10);
    const target = panes[n - 1];
    if (!target) return;
    const sel = getSendText();
    sendToPaneFiltered(target, sel.text);
    if (CLEAR_INPUT_AFTER_TARGETED_SEND) consumeSendText(sel);
    invoke('add_to_history', { text: sel.text });
    return;
  }

  if (e.key.toLowerCase() === 'h' && e.ctrlKey) {
    e.preventDefault();
    showHistoryDropdown();
    return;
  }

  if (e.key.toLowerCase() === 'e' && e.ctrlKey) {
    e.preventDefault();
    runSystemMenuAction('editor-integration', resolveSendTarget());
    return;
  }
  // Plain Enter (any modifier) falls through to the textarea's default
  // newline behavior — intentionally no submit-on-Enter, to avoid the
  // misfire class of bugs the spec explicitly rules out.
});

