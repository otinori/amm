// Shared-input-box send helpers: text extraction/consumption, line-by-
// line send, and send-target resolution. Split out of app.js
// (2026-07-26 architecture-bloat cleanup). No behavior change, pure
// move.

function getSendText() {
  const { selectionStart: s, selectionEnd: e, value } = promptInput;
  if (s !== e) return { text: value.slice(s, e), isSelection: true, s, e };
  return { text: value, isSelection: false };
}

function consumeSendText(sel) {
  if (sel.isSelection) {
    promptInput.value = promptInput.value.slice(0, sel.s) + promptInput.value.slice(sel.e);
  } else {
    promptInput.value = '';
  }
}

function sleep(ms) {
  return new Promise((resolve) => setTimeout(resolve, ms));
}

// spec: profile-schema's sendLineByLine - port of TerminalChildForm.cs's
// SendTextLineByLineAsync: each line is written with its own trailing \r,
// with a short delay between writes (multi-line CLI input is sometimes
// dropped when written in one continuous burst). Always submits the final
// line too (submitLastLine=true in the .NET original), matching this port's
// existing "every send is submitted immediately" behavior.
async function sendLineByLineToPane(pane, text) {
  const lines = text.split(/\r\n|\n|\r/);
  for (let i = 0; i < lines.length; i++) {
    await invoke('pty_write', { paneId: pane.paneId, data: lines[i] + '\r' }).catch((err) => console.error('pty_write failed:', err));
    if (i < lines.length - 1) await sleep(80);
  }
}

async function sendToPane(pane, text) {
  if (!pane || !text) return;
  // spec: quick-command-register's "直前に端末へ転送されたテキスト" - the
  // shared-input-box send path.
  pane.lastText = text;
  if (pane.sendLineByLine) {
    await sendLineByLineToPane(pane, text);
  } else {
    invoke('pty_write', { paneId: pane.paneId, data: text + '\r' }).catch((err) => console.error('pty_write failed:', err));
  }
}

// spec: 送信前のテキスト整形 - 以前はsendToPane()内で全送信経路に無条件適用
// していたが、クイック送信・プロンプト再送信・MCP経由の他CLIからの送信・
// アイドル時自動送信にも意図せず適用されており分かりにくいとの指摘を受け、
// 適用範囲を共通入力欄からの送信だけに絞った(ユーザー要望、2026-08-04)。
// エディタ連携からの送信はRust側(editor_bridge.rs)で既にフィルタ適用後の
// 空文字チェックをしているのでここは経由しない。設定自体もコマンドごとで
// はなくアプリ全体設定(profile::FormatSettingsFile)に変更済み。
async function sendToPaneFiltered(pane, text) {
  if (!pane || !text) return;
  const filtered = await invoke('filter_text_for_send', { text }).catch(() => text);
  await sendToPane(pane, filtered);
}

function resolveSendTarget() {
  if (activePane) return activePane;
  if (lastActivePane && panesById.has(lastActivePane.paneId)) return lastActivePane;
  const alive = aliveOrdered();
  return alive.length === 1 ? alive[0] : null;
}

