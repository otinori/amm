// Profile/MCP-server-config dialogs: command import/export, the MCP
// gateway management dialog, CLI MCP-registration dialog, and the
// command-template/command-manager/AMM-settings dialogs. Split out
// of app.js (2026-07-26 architecture-bloat cleanup). No behavior
// change, pure move.

// ---- command-import-export (spec: command-import-export) ----
function formatProfileRow(p) {
  const nickname = p.nickname ?? '';
  const args = (p.args ?? []).join(' ');
  return `${p.name}  [${nickname}]  (${p.commandType})  —  ${p.executable} ${args}`;
}

// spec: command-import-export - both operate on 「コマンドを管理...」の作業
// コピー(まだOKでコミットされていないローカル配列)であって、ライブの
// ProfilesStateには一切触れない。ダイアログがキャンセルされれば選択も
// インポートも他の編集もろとも破棄される(旧.NET版CommandManagerDialogの
// _entriesと同じ「作業コピー」設計 - 以前はトップレベルのツールバーボタンに
// なっていたためライブ状態に直接触れていたが、それだと「開く」で管理ダイア
// ログの外から差し替えたアクティブファイルとの役割衝突が紛らわしいという
// フィードバックを受けて元の配置へ戻した)。
function openManagerExportDialog(profiles) {
  if (profiles.length === 0) {
    alert('エクスポートするコマンドがありません。');
    return;
  }
  const back = () => renderCommandManagerDialog(profiles);
  const box = openModal('コマンドのエクスポート', back);
  const list = document.createElement('div');
  list.className = 'modal-list';
  const checkboxes = [];
  profiles.forEach((p, i) => {
    const row = document.createElement('div');
    row.className = 'modal-list-item';
    const cb = document.createElement('input');
    cb.type = 'checkbox';
    cb.checked = true;
    cb.dataset.index = String(i);
    const label = document.createElement('label');
    label.textContent = formatProfileRow(p);
    row.appendChild(cb);
    row.appendChild(label);
    list.appendChild(row);
    checkboxes.push(cb);
  });
  box.appendChild(list);

  const selectRow = document.createElement('div');
  selectRow.className = 'modal-select-row';
  const selectAllBtn = document.createElement('button');
  selectAllBtn.textContent = 'すべて選択';
  selectAllBtn.addEventListener('click', () => checkboxes.forEach((c) => { c.checked = true; }));
  const deselectAllBtn = document.createElement('button');
  deselectAllBtn.textContent = 'すべて解除';
  deselectAllBtn.addEventListener('click', () => checkboxes.forEach((c) => { c.checked = false; }));
  selectRow.appendChild(selectAllBtn);
  selectRow.appendChild(deselectAllBtn);
  box.appendChild(selectRow);

  addModalActions(box, [
    { label: 'キャンセル', onClick: back },
    {
      label: 'OK', primary: true,
      onClick: async () => {
        const selected = checkboxes.filter((c) => c.checked).map((c) => profiles[Number(c.dataset.index)]);
        if (selected.length === 0) {
          // spec: "選択なしでOK" - stays open, doesn't proceed to the save dialog.
          alert('コマンドが選択されていません。');
          return;
        }
        const defaultFileName = `profiles-export-${new Date().toISOString().slice(0, 10)}.ammprofiles`;
        const path = await invoke('pick_export_save_path', { defaultFileName }).catch(() => null);
        if (!path) return; // user cancelled the native save dialog - keep the checklist open
        try {
          const count = await invoke('export_profiles_list_to_path', { path, profiles: selected });
          alert(`${count} 件のコマンドをエクスポートしました。`);
          back();
        } catch (err) {
          alert(`エクスポートに失敗しました。\n\n${err}`);
        }
      },
    },
  ]);
}

function openManagerImportDialog(profiles) {
  const back = () => renderCommandManagerDialog(profiles);
  (async () => {
    const path = await invoke('pick_import_open_path').catch(() => null);
    if (!path) return;

    let imported;
    try {
      imported = await invoke('preview_import_profiles', { path });
    } catch (err) {
      alert(`ファイルの読み込みに失敗しました。\n\n${err}`);
      return;
    }

    const existingNicknames = new Set(profiles.map((p) => (p.nickname ?? '').toLowerCase()).filter((n) => n));

    const box = openModal('コマンドのインポート', back);
    const list = document.createElement('div');
    list.className = 'modal-list';
    const checkboxes = [];
    imported.forEach((p, i) => {
      const row = document.createElement('div');
      row.className = 'modal-list-item';
      const cb = document.createElement('input');
      cb.type = 'checkbox';
      cb.checked = true;
      cb.dataset.index = String(i);
      const label = document.createElement('label');
      const nick = (p.nickname ?? '').toLowerCase();
      // spec: 重複マークの表示 - Nickname が既存のいずれかと大文字小文字を無視して
      // 一致する場合のみ(両者ともnicknameを持つ場合)。
      const dup = nick !== '' && existingNicknames.has(nick);
      label.textContent = formatProfileRow(p) + (dup ? '  ⚠ 重複' : '');
      row.appendChild(cb);
      row.appendChild(label);
      list.appendChild(row);
      checkboxes.push(cb);
    });
    box.appendChild(list);

    const policyField = document.createElement('div');
    policyField.className = 'modal-field';
    const policyLabel = document.createElement('label');
    policyLabel.textContent = '重複時の扱い';
    policyField.appendChild(policyLabel);
    const policyRow = document.createElement('div');
    policyRow.className = 'modal-radio-row';
    let selectedPolicy = 'skip';
    for (const [value, text] of [['skip', 'スキップ'], ['rename', 'リネーム'], ['overwrite', '上書き']]) {
      const wrap = document.createElement('label');
      const radio = document.createElement('input');
      radio.type = 'radio';
      radio.name = 'import-policy';
      radio.value = value;
      radio.checked = value === 'skip';
      radio.addEventListener('change', () => { selectedPolicy = value; });
      wrap.appendChild(radio);
      wrap.appendChild(document.createTextNode(text));
      policyRow.appendChild(wrap);
    }
    policyField.appendChild(policyRow);
    box.appendChild(policyField);

    addModalActions(box, [
      { label: 'キャンセル', onClick: back },
      {
        label: 'OK', primary: true,
        onClick: async () => {
          const selected = checkboxes.filter((c) => c.checked).map((c) => imported[Number(c.dataset.index)]);
          const [merged, summary] = await invoke('merge_profiles_into_list', { existing: profiles, imported: selected, policy: selectedPolicy });
          alert(`${summary.added} 件追加、${summary.overwritten} 件上書き、${summary.skipped} 件スキップしました。`);
          renderCommandManagerDialog(merged);
        },
      },
    ]);
  })();
}

// ---- mcp-gateway management dialog (spec: mcp-gateway's "管理サーバーの
// ステータス表示") ----
function statusIconFor(status) {
  switch (status) {
    case 'running': return '✓';
    case 'starting': return '⏳';
    case 'error': return '✗';
    case 'stopped': return '○';
    default: return '?';
  }
}

function joinArgsDisplay(args) {
  if (!args || args.length === 0) return '';
  return ' ' + args.map((a) => (a.includes(' ') ? `"${a}"` : a)).join(' ');
}

function splitArgsInput(text) {
  if (!text || !text.trim()) return [];
  const result = [];
  let sb = '';
  let inQuote = false;
  for (const c of text.trim()) {
    if (c === '"') { inQuote = !inQuote; continue; }
    if (c === ' ' && !inQuote) {
      if (sb.length > 0) { result.push(sb); sb = ''; }
      continue;
    }
    sb += c;
  }
  if (sb.length > 0) result.push(sb);
  return result;
}

function joinEnvDisplay(env) {
  if (!env) return '';
  return Object.entries(env).map(([k, v]) => `${k}=${v}`).join('\n');
}

function parseEnvInput(text) {
  if (!text || !text.trim()) return null;
  const dict = {};
  for (const rawLine of text.split('\n')) {
    const l = rawLine.trim();
    if (!l || l.startsWith('#')) continue;
    const eq = l.indexOf('=');
    if (eq <= 0) continue;
    dict[l.slice(0, eq).trim()] = l.slice(eq + 1);
  }
  return Object.keys(dict).length > 0 ? dict : null;
}

// spec: McpServerEditDialog - single-entry add/edit sub-dialog.
function openMcpServerEditDialog(existing, onSave, onCancel) {
  // spec: same nested-dialog-cancel fix as openCommandTemplateDialog (found
  // via live CDP testing, phase 8.2) - without this, Cancel/Esc/backdrop-
  // click here would close the whole modal shell instead of returning to
  // openMcpGatewayDialog's list.
  const cancel = onCancel ?? closeModal;
  const box = openModal(existing ? 'MCP サーバを編集' : 'MCP サーバを追加', cancel);

  const nameInput = document.createElement('input');
  nameInput.type = 'text';
  nameInput.value = existing?.name ?? '';
  addModalField(box, '識別名 (*)', nameInput);

  // spec: add-mcp-http-transport's「http エントリの追加」- transport 選択で
  // 以下の stdio/http 各フィールド群の表示を切り替える。
  const transportSelect = document.createElement('select');
  for (const [value, label] of [['stdio', 'stdio (ローカルでコマンドを起動)'], ['http', 'HTTP (リモート/常駐サーバーへ接続)']]) {
    const opt = document.createElement('option');
    opt.value = value;
    opt.textContent = label;
    transportSelect.appendChild(opt);
  }
  transportSelect.value = existing?.type === 'http' ? 'http' : 'stdio';
  addModalField(box, '種別', transportSelect);

  // --- stdio 用フィールド ---
  const commandInput = document.createElement('input');
  commandInput.type = 'text';
  commandInput.value = existing?.command ?? '';
  const commandField = addModalField(box, 'コマンド (*)', commandInput);

  const argsInput = document.createElement('input');
  argsInput.type = 'text';
  argsInput.value = joinArgsDisplay(existing?.args).trim();
  const argsField = addModalField(box, '引数', argsInput);

  const envInput = document.createElement('textarea');
  envInput.style.minHeight = '70px';
  envInput.value = joinEnvDisplay(existing?.env);
  const envField = addModalField(box, '環境変数 (KEY=VALUE 形式、1行1変数)', envInput);

  const maxRestartsInput = document.createElement('input');
  maxRestartsInput.type = 'text';
  maxRestartsInput.value = String(existing?.maxRestarts ?? 3);
  const maxRestartsField = addModalField(box, '最大再起動回数 (0 = 再起動なし)', maxRestartsInput);

  // --- http 用フィールド ---
  const urlInput = document.createElement('input');
  urlInput.type = 'text';
  urlInput.placeholder = 'https://127.0.0.1:27124/mcp/';
  urlInput.value = existing?.url ?? '';
  const urlField = addModalField(box, 'URL (*)', urlInput);

  const headersInput = document.createElement('textarea');
  headersInput.style.minHeight = '70px';
  headersInput.value = joinEnvDisplay(existing?.headers);
  const headersField = addModalField(box, 'ヘッダー (KEY=VALUE 形式、1行1ヘッダー。例: Authorization=Bearer xxx)', headersInput);

  const skipTlsField = document.createElement('div');
  const skipTlsWrap = document.createElement('label');
  skipTlsWrap.className = 'modal-radio-row';
  skipTlsWrap.style.marginBottom = '4px';
  const skipTlsCb = document.createElement('input');
  skipTlsCb.type = 'checkbox';
  skipTlsCb.checked = existing?.skipTlsVerify ?? false;
  skipTlsWrap.appendChild(skipTlsCb);
  skipTlsWrap.appendChild(document.createTextNode('証明書検証をスキップする'));
  const skipTlsHint = document.createElement('div');
  skipTlsHint.className = 'modal-hint';
  skipTlsHint.style.marginBottom = '10px';
  skipTlsHint.textContent = '⚠ 自己署名証明書を使うローカルサーバー (127.0.0.1 等) 向け。信頼できないネットワーク越しの接続には使わないこと。';
  skipTlsField.appendChild(skipTlsWrap);
  skipTlsField.appendChild(skipTlsHint);
  box.appendChild(skipTlsField);

  // autoStart は transport 非依存 (spec: 「自動起動サーバーの一括起動」)
  const autoStartWrap = document.createElement('label');
  autoStartWrap.className = 'modal-radio-row';
  autoStartWrap.style.marginBottom = '10px';
  const autoStartCb = document.createElement('input');
  autoStartCb.type = 'checkbox';
  autoStartCb.checked = existing?.autoStart ?? true;
  autoStartWrap.appendChild(autoStartCb);
  autoStartWrap.appendChild(document.createTextNode('amm 起動時に自動起動 (http は接続) する'));
  box.appendChild(autoStartWrap);

  const updateFieldVisibility = () => {
    const isHttp = transportSelect.value === 'http';
    for (const f of [commandField, argsField, envField, maxRestartsField]) f.classList.toggle('hidden', isHttp);
    for (const f of [urlField, headersField, skipTlsField]) f.classList.toggle('hidden', !isHttp);
  };
  transportSelect.addEventListener('change', updateFieldVisibility);
  updateFieldVisibility();

  addModalActions(box, [
    { label: 'キャンセル', onClick: cancel },
    {
      label: 'OK', primary: true,
      onClick: () => {
        const name = nameInput.value.trim();
        if (!name) {
          alert('識別名は必須です。');
          return;
        }
        if (transportSelect.value === 'http') {
          const url = urlInput.value.trim();
          if (!url) {
            alert('URL は必須です。');
            return;
          }
          closeModal();
          onSave({
            name, type: 'http', command: '', args: [], env: null,
            autoStart: autoStartCb.checked, maxRestarts: 3,
            url, headers: parseEnvInput(headersInput.value), skipTlsVerify: skipTlsCb.checked,
          });
        } else {
          const command = commandInput.value.trim();
          if (!command) {
            alert('コマンドは必須です。');
            return;
          }
          const maxRestarts = Math.max(0, parseInt(maxRestartsInput.value, 10) || 0);
          closeModal();
          onSave({
            name, type: 'stdio', command, args: splitArgsInput(argsInput.value), env: parseEnvInput(envInput.value),
            autoStart: autoStartCb.checked, maxRestarts,
            url: null, headers: null, skipTlsVerify: false,
          });
        }
      },
    },
  ]);
}

// spec: McpGatewayDialog. Operates on local copies of the global/file-local
// lists (only committed on OK, matching the .NET original's Cancel-discards
// semantics) - re-rendered via recursion after every add/edit/remove/move
// since this port's modal shell is a single-overlay singleton rather than
// something that supports nested/stacked dialogs.
async function openMcpGatewayDialog(globalServers, fileServers) {
  if (!globalServers || !fileServers) {
    [globalServers, fileServers] = await Promise.all([
      invoke('list_global_mcp_servers').catch(() => []),
      invoke('list_file_mcp_servers').catch(() => []),
    ]);
  }
  // spec: ステータス表示は app 起動時点の GatewayManager のライブスナップ
  // ショットを参照する。このダイアログ内で追加・編集した(まだ保存していない)
  // エントリは対応する info が無いため常に "●" (未設定) になる - 実行中
  // ゲートウェイのライブ再起動は本パスでは未実装(PARITY-AUDIT.md参照)。
  const infos = await invoke('gateway_server_infos').catch(() => []);

  const box = openModal('MCP ゲートウェイ設定');

  const buildGroup = (title, hint, servers, onChange) => {
    const heading = document.createElement('div');
    heading.className = 'modal-group-title';
    heading.textContent = `${title}  (${hint})`;
    box.appendChild(heading);

    const list = document.createElement('div');
    list.className = 'modal-list';
    servers.forEach((cfg, i) => {
      const info = infos.find((x) => x.name.toLowerCase() === cfg.name.toLowerCase());
      const icon = info ? statusIconFor(info.status) : '●';
      const toolCount = info && info.status === 'running' ? ` (${info.toolCount} ツール)` : '';
      const row = document.createElement('div');
      row.className = 'modal-list-item';
      const detail = cfg.type === 'http' ? (cfg.url ?? '') : `${cfg.command}${joinArgsDisplay(cfg.args)}`;
      const text = document.createElement('label');
      text.style.flex = '1';
      text.textContent = `${icon}  [${cfg.name}]  ${detail}${toolCount}`;
      row.appendChild(text);

      const mkBtn = (label, onClick) => {
        const b = document.createElement('button');
        b.textContent = label;
        b.addEventListener('click', onClick);
        row.appendChild(b);
      };
      mkBtn('編集...', () => {
        closeModal();
        openMcpServerEditDialog(cfg, (updated) => {
          const next = servers.slice();
          next[i] = updated;
          onChange(next);
        }, () => openMcpGatewayDialog(globalServers, fileServers));
      });
      mkBtn('削除', async () => {
        if (!(await confirmDialog(`「${cfg.name}」を削除しますか？`))) return;
        const next = servers.slice();
        next.splice(i, 1);
        onChange(next);
      });
      mkBtn('↑', () => {
        if (i === 0) return;
        const next = servers.slice();
        [next[i - 1], next[i]] = [next[i], next[i - 1]];
        onChange(next);
      });
      mkBtn('↓', () => {
        if (i === servers.length - 1) return;
        const next = servers.slice();
        [next[i], next[i + 1]] = [next[i + 1], next[i]];
        onChange(next);
      });
      list.appendChild(row);
    });
    box.appendChild(list);

    const addBtn = document.createElement('button');
    addBtn.className = 'modal-btn';
    addBtn.textContent = '追加...';
    addBtn.addEventListener('click', () => {
      closeModal();
      openMcpServerEditDialog(null, (created) => onChange([...servers, created]), () => openMcpGatewayDialog(globalServers, fileServers));
    });
    box.appendChild(addBtn);
  };

  buildGroup('AMM 共通', '全ワークスペースで読み込まれる', globalServers, (next) => openMcpGatewayDialog(next, fileServers));

  // spec: mcp-gateway's「AMM共通」グループのインポート/エクスポート -
  // command-import-exportと同じUXをここにも用意する(ユーザー要望)。ファイル
  // 固有グループは元々このファイルのprofiles.amm自体をエクスポート/インポート
  // すれば足りるため対象外。
  const globalIoRow = document.createElement('div');
  globalIoRow.className = 'modal-select-row';
  globalIoRow.style.marginTop = '-6px';
  globalIoRow.style.marginBottom = '14px';
  const exportGlobalBtn = document.createElement('button');
  exportGlobalBtn.textContent = 'AMM 共通をエクスポート...';
  exportGlobalBtn.addEventListener('click', () => { closeModal(); openMcpServerExportDialog(globalServers, fileServers); });
  const importGlobalBtn = document.createElement('button');
  importGlobalBtn.textContent = 'AMM 共通へインポート...';
  importGlobalBtn.addEventListener('click', () => { closeModal(); openMcpServerImportDialog(globalServers, fileServers); });
  globalIoRow.appendChild(exportGlobalBtn);
  globalIoRow.appendChild(importGlobalBtn);
  box.appendChild(globalIoRow);

  buildGroup('このファイル固有', '現在開いている profiles.amm にのみ適用', fileServers, (next) => openMcpGatewayDialog(globalServers, next));

  const legend = document.createElement('div');
  legend.className = 'modal-hint';
  legend.style.marginTop = '10px';
  legend.textContent = '✓ 実行中  ⏳ 起動中  ✗ エラー  ○ 停止  ● 未設定 (ゲートウェイ未反映)';
  box.appendChild(legend);

  addModalActions(box, [
    { label: 'キャンセル', onClick: closeModal },
    {
      label: 'OK', primary: true,
      onClick: async () => {
        // spec: グローバル側は即時保存、ファイル固有側はメモリ上のみ
        // (command-import-export と同じ "反映はメモリ上のみ" 規約)。
        try {
          await invoke('save_global_mcp_servers', { servers: globalServers });
        } catch (err) {
          alert(`グローバル MCP 設定の保存に失敗しました。\n\n${err}`);
        }
        await invoke('save_file_mcp_servers', { servers: fileServers }).catch(() => {});
        closeModal();
        // spec: GatewayManager は amm 起動時に一度だけ構築され、この保存操作
        // では再起動しない(lib.rsのlist_global_mcp_servers手前のコメント参照
        // - 意図的な簡略化)。保存内容がすぐ反映されたと誤解されないよう告知する。
        alert('MCP サーバー設定を保存しました。変更を反映するには amm の再起動が必要です。');
      },
    },
  ]);
}

// spec: mcp-gateway's「AMM共通」インポート/エクスポート - command-import-
// exportのopenManagerExportDialog/openManagerImportDialogと同じ形。両方とも
// openMcpGatewayDialogの作業コピー(globalServers)だけを操作し、ライブの
// グローバル設定ファイルには一切触れない(OK/キャンセルいずれでもopenMcp
// GatewayDialogへ戻り、そこで改めて保存するかを選べる)。
function openMcpServerExportDialog(globalServers, fileServers) {
  if (globalServers.length === 0) {
    alert('エクスポートするサーバーがありません。');
    return;
  }
  const back = () => openMcpGatewayDialog(globalServers, fileServers);
  const box = openModal('AMM 共通 MCP サーバーのエクスポート', back);
  const list = document.createElement('div');
  list.className = 'modal-list';
  const checkboxes = [];
  globalServers.forEach((cfg, i) => {
    const row = document.createElement('div');
    row.className = 'modal-list-item';
    const cb = document.createElement('input');
    cb.type = 'checkbox';
    cb.checked = true;
    cb.dataset.index = String(i);
    const label = document.createElement('label');
    label.textContent = `[${cfg.name}]  ${cfg.command}${joinArgsDisplay(cfg.args)}`;
    row.appendChild(cb);
    row.appendChild(label);
    list.appendChild(row);
    checkboxes.push(cb);
  });
  box.appendChild(list);

  const selectRow = document.createElement('div');
  selectRow.className = 'modal-select-row';
  const selectAllBtn = document.createElement('button');
  selectAllBtn.textContent = 'すべて選択';
  selectAllBtn.addEventListener('click', () => checkboxes.forEach((c) => { c.checked = true; }));
  const deselectAllBtn = document.createElement('button');
  deselectAllBtn.textContent = 'すべて解除';
  deselectAllBtn.addEventListener('click', () => checkboxes.forEach((c) => { c.checked = false; }));
  selectRow.appendChild(selectAllBtn);
  selectRow.appendChild(deselectAllBtn);
  box.appendChild(selectRow);

  addModalActions(box, [
    { label: 'キャンセル', onClick: back },
    {
      label: 'OK', primary: true,
      onClick: async () => {
        const selected = checkboxes.filter((c) => c.checked).map((c) => globalServers[Number(c.dataset.index)]);
        if (selected.length === 0) {
          alert('サーバーが選択されていません。');
          return;
        }
        const defaultFileName = `mcp-servers-export-${new Date().toISOString().slice(0, 10)}.json`;
        const path = await invoke('pick_export_mcp_servers_path', { defaultFileName }).catch(() => null);
        if (!path) return;
        try {
          const count = await invoke('export_mcp_servers_to_path', { path, servers: selected });
          alert(`${count} 件のサーバーをエクスポートしました。`);
          back();
        } catch (err) {
          alert(`エクスポートに失敗しました。\n\n${err}`);
        }
      },
    },
  ]);
}

function openMcpServerImportDialog(globalServers, fileServers) {
  const back = () => openMcpGatewayDialog(globalServers, fileServers);
  (async () => {
    const path = await invoke('pick_import_mcp_servers_path').catch(() => null);
    if (!path) return;

    let imported;
    try {
      imported = await invoke('preview_import_mcp_servers', { path });
    } catch (err) {
      alert(`ファイルの読み込みに失敗しました。\n\n${err}`);
      return;
    }

    const existingNames = new Set(globalServers.map((s) => s.name.toLowerCase()));

    const box = openModal('AMM 共通 MCP サーバーのインポート', back);
    const list = document.createElement('div');
    list.className = 'modal-list';
    const checkboxes = [];
    imported.forEach((s, i) => {
      const row = document.createElement('div');
      row.className = 'modal-list-item';
      const cb = document.createElement('input');
      cb.type = 'checkbox';
      cb.checked = true;
      cb.dataset.index = String(i);
      const label = document.createElement('label');
      const dup = existingNames.has(s.name.toLowerCase());
      label.textContent = `[${s.name}]  ${s.command}${joinArgsDisplay(s.args)}` + (dup ? '  ⚠ 重複' : '');
      row.appendChild(cb);
      row.appendChild(label);
      list.appendChild(row);
      checkboxes.push(cb);
    });
    box.appendChild(list);

    const policyField = document.createElement('div');
    policyField.className = 'modal-field';
    const policyLabel = document.createElement('label');
    policyLabel.textContent = '重複時の扱い';
    policyField.appendChild(policyLabel);
    const policyRow = document.createElement('div');
    policyRow.className = 'modal-radio-row';
    let selectedPolicy = 'skip';
    for (const [value, text] of [['skip', 'スキップ'], ['rename', 'リネーム'], ['overwrite', '上書き']]) {
      const wrap = document.createElement('label');
      const radio = document.createElement('input');
      radio.type = 'radio';
      radio.name = 'mcp-import-policy';
      radio.value = value;
      radio.checked = value === 'skip';
      radio.addEventListener('change', () => { selectedPolicy = value; });
      wrap.appendChild(radio);
      wrap.appendChild(document.createTextNode(text));
      policyRow.appendChild(wrap);
    }
    policyField.appendChild(policyRow);
    box.appendChild(policyField);

    addModalActions(box, [
      { label: 'キャンセル', onClick: back },
      {
        label: 'OK', primary: true,
        onClick: async () => {
          const selected = checkboxes.filter((c) => c.checked).map((c) => imported[Number(c.dataset.index)]);
          const [merged, summary] = await invoke('merge_mcp_servers_list', { existing: globalServers, imported: selected, policy: selectedPolicy });
          alert(`${summary.added} 件追加、${summary.overwritten} 件上書き、${summary.skipped} 件スキップしました。`);
          openMcpGatewayDialog(merged, fileServers);
        },
      },
    ]);
  })();
}

// ---- mcp-server's "CLI設定ファイルへのMCPサーバ登録" UI (spec: McpRegistrationDialog
// port) - found entirely missing in the source-diff parity audit (tasks.md
// 8.6.17-b-2): hook_register/mcp_register/hook_unregister/mcp_unregister
// were fully implemented and unit-tested, but unreachable from the GUI -
// only a direct invoke() call (e.g. from a test script) could ever call
// them, so a user could not actually register amm-mcp.exe with any AI CLI
// without hand-editing that CLI's config file. Simplified vs. the .NET
// original: no per-row config-file-path display (would need 4 more
// command_path-style commands with little practical value over just
// showing registered/not), otherwise the same checkbox-per-CLI-per-kind
// (MCP tool server + hook) diff-and-apply flow.
const CLI_KINDS = [
  { kind: 'claudeCode', label: 'Claude Code' },
  { kind: 'codex', label: 'Codex' },
  { kind: 'copilotCli', label: 'Copilot CLI' },
  { kind: 'antigravity', label: 'Antigravity' },
];

async function openCliRegistrationDialog() {
  const exeInfo = await invoke('get_mcp_exe_path').catch(() => ({ path: '(unknown)', exists: false }));
  const [mcpStates, hookStates] = await Promise.all([
    Promise.all(CLI_KINDS.map((c) => invoke('mcp_registered_command', { kind: c.kind }).catch(() => null))),
    Promise.all(CLI_KINDS.map((c) => invoke('hook_registered_command', { kind: c.kind }).catch(() => null))),
  ]);

  const box = openModal('CLI への MCP / フック登録');

  const exeLabel = document.createElement('div');
  exeLabel.className = 'modal-hint';
  exeLabel.style.marginBottom = '10px';
  exeLabel.textContent = `amm-mcp.exe: ${exeInfo.path}${exeInfo.exists ? '' : '  (見つかりません)'}`;
  if (!exeInfo.exists) exeLabel.style.color = '#e57373';
  box.appendChild(exeLabel);

  function statusText(registeredCommand) {
    if (!registeredCommand) return '未登録';
    return registeredCommand.toLowerCase() === exeInfo.path.toLowerCase() ? '登録済' : '登録済 (パスが異なるため適用時に更新)';
  }

  const mcpTitle = document.createElement('div');
  mcpTitle.className = 'modal-group-title';
  mcpTitle.textContent = 'MCP サーバ登録 (AI からのツール呼び出し)';
  box.appendChild(mcpTitle);
  const mcpChecks = CLI_KINDS.map((c, i) => {
    const wrap = document.createElement('label');
    wrap.className = 'modal-radio-row';
    const cb = document.createElement('input');
    cb.type = 'checkbox';
    cb.checked = !!mcpStates[i];
    wrap.appendChild(cb);
    wrap.appendChild(document.createTextNode(`${c.label}  [${statusText(mcpStates[i])}]`));
    box.appendChild(wrap);
    return cb;
  });

  const hookTitle = document.createElement('div');
  hookTitle.className = 'modal-group-title';
  hookTitle.style.marginTop = '10px';
  hookTitle.textContent = 'フック登録 (応答完了・許可待ちの通知)';
  box.appendChild(hookTitle);
  const hookChecks = CLI_KINDS.map((c, i) => {
    const wrap = document.createElement('label');
    wrap.className = 'modal-radio-row';
    const cb = document.createElement('input');
    cb.type = 'checkbox';
    cb.checked = !!hookStates[i];
    wrap.appendChild(cb);
    wrap.appendChild(document.createTextNode(`${c.label} フック  [${statusText(hookStates[i])}]`));
    box.appendChild(wrap);
    return cb;
  });

  const hint = document.createElement('div');
  hint.className = 'modal-hint';
  hint.style.marginTop = '10px';
  hint.textContent = 'チェック = 登録 / チェック外し = 削除。対象はユーザー単位の設定ファイルです(プロジェクト単位ではありません)。CLI起動中の変更は次回起動から有効です。';
  box.appendChild(hint);

  addModalActions(box, [
    { label: 'キャンセル', onClick: closeModal },
    {
      label: '適用', primary: true,
      onClick: async () => {
        const results = [];
        for (let i = 0; i < CLI_KINDS.length; i++) {
          const { kind, label } = CLI_KINDS[i];
          const wasRegistered = !!mcpStates[i];
          const needsUpdate = wasRegistered && mcpStates[i].toLowerCase() !== exeInfo.path.toLowerCase();
          try {
            if (mcpChecks[i].checked) {
              if (!wasRegistered || needsUpdate) {
                await invoke('mcp_register', { kind });
                results.push(`${label}: ${wasRegistered ? 'パスを更新しました' : '登録しました'}`);
              }
            } else if (wasRegistered) {
              await invoke('mcp_unregister', { kind });
              results.push(`${label}: 削除しました`);
            }
          } catch (err) {
            results.push(`${label}: 失敗 — ${err}`);
          }
        }
        for (let i = 0; i < CLI_KINDS.length; i++) {
          const { kind, label } = CLI_KINDS[i];
          const wasRegistered = !!hookStates[i];
          const needsUpdate = wasRegistered && hookStates[i].toLowerCase() !== exeInfo.path.toLowerCase();
          try {
            if (hookChecks[i].checked) {
              if (!wasRegistered || needsUpdate) {
                await invoke('hook_register', { kind });
                results.push(`${label} フック: ${wasRegistered ? 'パスを更新しました' : '登録しました'}`);
              }
            } else if (wasRegistered) {
              await invoke('hook_unregister', { kind });
              results.push(`${label} フック: 削除しました`);
            }
          } catch (err) {
            results.push(`${label} フック: 失敗 — ${err}`);
          }
        }
        closeModal();
        alert(results.length > 0 ? results.join('\n') : '変更はありません。');
      },
    },
  ]);
}

// ---- profile-schema's CommandTemplateDialog / CommandManagerDialog /
// AmmSettingsDialog (spec: profile-schema's "コマンド追加テンプレートと
// per-command 編集ダイアログ") ----

// Port of SessionProfileLoader.CommandTemplates - only the "behavior"
// fields ApplyTypePreset actually copies (executable/args/outputEncoding/
// autoChcp/waitPatterns/sendLineByLine/selectWorkingDirOnStart/
// promptNewNameOnCommandAdd). name/nickname/workingDirectory/quickPrompts
// are deliberately NOT part of a preset (matches the .NET original's own
// comment: "名前 / Nickname / 作業ディレクトリ / クイック送信は保持します").
// newlineMode/useBracketedPaste were dropped entirely (fix-unwired-profile-
// settings): newlineMode was dead in the .NET original too (no reader
// found anywhere), and bracketed-paste sending was never implemented in
// this port, so the field only ever recorded a setting nothing acted on.
// Note: the .NET template array also has an "Antigravity" entry, but it
// has no CommandType assigned so it's unreachable from this same dropdown
// in the .NET original too (the dropdown is keyed by the CommandType enum,
// which has no Antigravity value) - not ported here for that reason,
// tracked in PARITY-AUDIT.md.
// titleBarColor defaults let panes for different CLI tools be told apart at
// a glance when several are tiled at once (user request) - Cmd/PowerShell
// keep the neutral default (null = no override).
//
// spec: pane-management's「コマンドタイプ」タブ(ユーザー要望、2026-08-03) -
// 型プリセット自体はもうJSにハードコードせず、Rust側(profile.rs::
// load_command_type_presets、File>開くで切り替わるprofiles.ammとは独立した
// アプリ全体設定)から取得したcachedCommandTypePresets(events-integration.js
// の起動IIFEでget_command_type_presetsにより解決、pane-layout.js側で宣言)を
// そのまま使う。Rust側は起動時プラットフォーム(#[cfg(windows)]/#[cfg(not
// (windows))])に応じた既定値を、ファイル未作成なら返す。
function commandTypePresets() {
  const presets = {};
  for (const p of cachedCommandTypePresets ?? []) presets[p.key] = p;
  return presets;
}
function commandTypeLabels() {
  const labels = (cachedCommandTypePresets ?? []).map((p) => [p.key, p.label]);
  labels.push(['Other', 'その他']);
  return labels;
}
const FONT_SIZE_OPTIONS = [[null, '(既定)'], [20, '極大 (20px)'], [16, '大 (16px)'], [13, '中 (13px)'], [11, '小 (11px)'], [9, '極小 (9px)']];

function defaultSessionProfile() {
  return {
    name: '', commandType: 'Other', executable: '', args: [], resumeOnStart: false,
    outputEncoding: 'UTF-8', autoChcp: false, waitPatterns: ['[>]\\s*$'], workingDirectory: null,
    ctrlCCopyOnSelection: false, initialCommands: [], sessionLog: false,
    theme: null, closeOnExit: false, autoStartCount: 0, closeProhibited: false,
    windowGeometry: [], nickname: null, sendLineByLine: false,
    selectWorkingDirOnStart: false, promptNewNameOnCommandAdd: false,
    autoSendOnIdle: { enabled: false, prompt: '', delayMs: 3000 }, fontSize: null, titleBarColor: null,
  };
}

// spec: pane-management - 「コマンド追加」は最初にコマンドタイプだけを選ばせ
// てから編集画面へ進む2段階フロー(ユーザー要望: 種類を選ぶ前から全項目が
// 見えていると分かりにくい)。選んだ型のプリセットを事前適用した状態で
// openCommandTemplateDialog(isAddMode=true)へ渡すので、以降の編集画面自体は
// 「型変更時にプリセットを適用する」既存ロジックと完全に同じ土台を使う
// (二重実装にしない)。
// spec: pane-management - 選んだ型の初期値をdefaultSessionProfile()へ適用する
// 共通ロジック(次へ/編集ボタン・追加ボタンの両方で使う、二重実装にしない)。
function applyCommandTypePreset(profile, typeValue) {
  const preset = commandTypePresets()[typeValue];
  profile.commandType = typeValue;
  if (preset) {
    profile.executable = preset.executable;
    profile.args = preset.args;
    profile.outputEncoding = preset.outputEncoding;
    profile.autoChcp = preset.autoChcp;
    profile.waitPatterns = preset.waitPatterns;
    profile.sendLineByLine = preset.sendLineByLine;
    profile.selectWorkingDirOnStart = preset.selectWorkingDirOnStart;
    profile.promptNewNameOnCommandAdd = preset.promptNewNameOnCommandAdd;
    profile.titleBarColor = preset.titleBarColor;
  }
  return profile;
}

// spec: pane-management - 直前に選んだコマンドタイプを記憶し、次回この画面を
// 開いたときに選択済みの状態にする(ユーザー要望)。
const LAST_COMMAND_TYPE_STORAGE_KEY = 'amm-last-command-type';

// spec: pane-management - 「コマンド追加」フロー(ユーザー要望、2026-08-04):
// 以前は [種類を選択] → (「追加」クリック後に) [作業ディレクトリ選択] →
// [新しい名前/タイトルバー色] の3画面構成だったが、「種類を選ぶ前から全項目
// が見えていると分かりにくい」との当初方針とは逆に、今度は「作業ディレクトリ
// を選んでから種類・名前・タイトルバー色をまとめて確認したい」との要望を
// 受け、作業ディレクトリ選択を必ず最初に行い、そのあとに種類・名前・タイトル
// バー色を1画面に統合して表示する構成へ変更した。フォルダ選択をキャンセルし
// た場合はまだ何もモーダルを開いていないので、呼び出し元のcancel(コマンド
// 管理ダイアログへ戻る等)をそのまま呼んで抜ける。「編集...」ボタンは維持し、
// 実行ファイル/引数/waitパターン等まで調整したい場合の従来のフル編集画面
// (openCommandTemplateDialog)へ、選んだ種類・名前・タイトルバー色・作業
// ディレクトリを引き継いだ状態で遷移する。
async function openCommandAddDialog(onSave, onCancel, checkNameConflict) {
  const cancel = onCancel ?? closeModal;
  const picked = await invoke('pick_folder').catch(() => null);
  if (!picked) { cancel(); return; }
  const existing = await invoke('list_profiles').catch(() => []);

  const box = openModal('コマンド追加', cancel);
  // spec: pane-management - 項目が種類/名前/タイトルバー色の3つだけなので、
  // 全項目入りの編集ダイアログと同じ幅は横に長すぎるとの指摘(ユーザー報告)。
  box.classList.add('modal-box-narrow');
  const hint = document.createElement('div');
  hint.className = 'modal-hint';
  hint.style.marginBottom = '10px';
  hint.textContent = '種類を選ぶと、実行ファイルや wait パターンなどの初期値が自動入力されます(タイトルバー色もその種類の既定色に更新されます)。「編集...」で内容を確認・調整してから追加、「追加」でこのまま追加してすぐに起動できます。';
  box.appendChild(hint);

  const typeSelect = document.createElement('select');
  for (const [value, label] of commandTypeLabels()) {
    const opt = document.createElement('option');
    opt.value = value;
    opt.textContent = label;
    typeSelect.appendChild(opt);
  }
  const lastType = localStorage.getItem(LAST_COMMAND_TYPE_STORAGE_KEY);
  if (lastType && commandTypeLabels().some(([v]) => v === lastType)) typeSelect.value = lastType;
  addModalField(box, 'コマンドタイプ', typeSelect);

  const nameInput = document.createElement('input');
  nameInput.type = 'text';
  const typeLabelFor = (value) => commandTypeLabels().find(([v]) => v === value)?.[1] ?? value;
  nameInput.value = basenameOfPath(picked) || typeLabelFor(typeSelect.value);
  addModalField(box, '名前', nameInput);

  // spec: pane-management's titleBarColor - askNewCommandName と同じ
  // パターン(色ピッカー + 既定に戻すボタン)。種類変更のたびにその種類の
  // 既定色へ更新する(ユーザーが手で選んだ色を上書きしてしまう懸念より、
  // 「種類を選ぶと初期値が自動入力される」という一貫した挙動を優先)。
  const titleColorWrap = document.createElement('div');
  titleColorWrap.style.cssText = 'display:flex; gap:6px; align-items:center;';
  const titleColorInput = document.createElement('input');
  titleColorInput.type = 'color';
  let titleBarColorValue = commandTypePresets()[typeSelect.value]?.titleBarColor ?? null;
  titleColorInput.value = titleBarColorValue ?? '#2d2d34';
  titleColorInput.addEventListener('input', () => { titleBarColorValue = titleColorInput.value; });
  const titleColorResetBtn = document.createElement('button');
  titleColorResetBtn.type = 'button';
  titleColorResetBtn.className = 'modal-btn';
  titleColorResetBtn.style.marginTop = '0';
  titleColorResetBtn.textContent = '既定に戻す';
  titleColorResetBtn.addEventListener('click', () => {
    titleBarColorValue = commandTypePresets()[typeSelect.value]?.titleBarColor ?? null;
    titleColorInput.value = titleBarColorValue ?? '#2d2d34';
  });
  titleColorWrap.appendChild(titleColorInput);
  titleColorWrap.appendChild(titleColorResetBtn);
  addModalField(box, 'タイトルバー色', titleColorWrap);

  typeSelect.addEventListener('change', () => {
    localStorage.setItem(LAST_COMMAND_TYPE_STORAGE_KEY, typeSelect.value);
    titleBarColorValue = commandTypePresets()[typeSelect.value]?.titleBarColor ?? null;
    titleColorInput.value = titleBarColorValue ?? '#2d2d34';
  });

  const warn = document.createElement('div');
  warn.style.color = '#d18616';
  warn.style.marginTop = '-6px';
  warn.style.marginBottom = '10px';
  warn.style.display = 'none';
  box.appendChild(warn);

  const buildProfile = () => {
    const name = nameInput.value.trim();
    const profile = applyCommandTypePreset(defaultSessionProfile(), typeSelect.value);
    profile.name = name;
    profile.nickname = normalizeNickname(name);
    profile.workingDirectory = picked;
    profile.titleBarColor = titleBarColorValue;
    // spec: launchProfileFromMenuの複製ロジックと同じ規約 - 「テンプレートから
    // 生成された側」はもうテンプレートではないので両フラグをfalseにする。
    profile.selectWorkingDirOnStart = false;
    profile.promptNewNameOnCommandAdd = false;
    return { name, profile };
  };

  // spec: pane-management - ボタン配置は右から「追加」「編集...」「キャンセ
  // ル」、「追加」を primary(青色)にする(ユーザー要望、従来のボタン順序を
  // 維持)。addModalActionsは配列の並び順どおり左から描画するので、配列は
  // 左から キャンセル/編集.../追加 の順。
  addModalActions(box, [
    { label: 'キャンセル', onClick: cancel },
    {
      label: '編集...',
      onClick: () => {
        localStorage.setItem(LAST_COMMAND_TYPE_STORAGE_KEY, typeSelect.value);
        const { profile } = buildProfile();
        openCommandTemplateDialog(profile, true, onSave, onCancel, checkNameConflict);
      },
    },
    {
      label: '追加', primary: true,
      onClick: () => {
        localStorage.setItem(LAST_COMMAND_TYPE_STORAGE_KEY, typeSelect.value);
        const { name, profile } = buildProfile();
        if (!name) { warn.textContent = '名前を入力してください。'; warn.style.display = ''; return; }
        if (existing.some((p) => p.name.toLowerCase() === name.toLowerCase()) || (checkNameConflict && checkNameConflict(name))) {
          warn.textContent = `同名のコマンドが既に存在します: ${name}`;
          warn.style.display = '';
          return;
        }
        // spec: pane-management - 「追加」ボタンでの追加は、その場ですぐ使い
        // たいという意図が明確なため、追加に成功したらそのまま起動する
        // (ユーザー要望)。「編集...」ボタン経由(openCommandTemplateDialog
        // のOK)はこのフラグを渡さない - 内容を確認・調整してから追加する
        // 意図の方なので、従来通り追加のみ。
        closeModal();
        onSave(profile, { launch: true });
      },
    },
  ]);
  nameInput.focus();
  nameInput.select();
}

function openCommandTemplateDialog(existing, isAddMode, onSave, onCancel, checkNameConflict) {
  // spec: found via live CDP testing (phase 8.2) - Cancel used to always
  // fall through to the generic closeModal, which loses the caller's own
  // modal (e.g. CommandManagerDialog's list) entirely rather than
  // returning to it, since this port's modal shell is a single-overlay
  // singleton. Defaults to closeModal for standalone callers.
  const cancel = onCancel ?? closeModal;
  const initial = existing ? JSON.parse(JSON.stringify(existing)) : defaultSessionProfile();
  const box = openModal(isAddMode ? 'コマンド追加' : 'コマンド編集', cancel);

  const typeSelect = document.createElement('select');
  for (const [value, label] of commandTypeLabels()) {
    const opt = document.createElement('option');
    opt.value = value;
    opt.textContent = label;
    typeSelect.appendChild(opt);
  }
  typeSelect.value = commandTypePresets()[initial.commandType] ? initial.commandType : 'Other';

  // spec: pane-management - 設定項目を意味のあるまとまり(基本情報/起動時の
  // 挙動/入出力/見た目/クイック送信/アイドル時自動送信)へグループ化
  // (ユーザー要望: 「設定項目の並び順がよくわからない」)。
  const basicHeading = document.createElement('div');
  basicHeading.className = 'modal-group-title';
  basicHeading.textContent = '基本情報';
  box.appendChild(basicHeading);
  addModalField(box, 'コマンドタイプ', typeSelect);

  const nameInput = document.createElement('input');
  nameInput.type = 'text';
  nameInput.value = initial.name;
  addModalField(box, '名前 (Name) *', nameInput);

  // spec: 「名前(Name)」はamm内でこのコマンドを識別・表示するための名前
  // (プロファイル一覧・各種メニュー・profiles.amm上のキー、重複不可)。
  // 「Nickname」はMCP経由でこのコマンドから起動したペインを送信先として
  // 指定する際の宛先名(mcp.rsのregister_participant/send_message)で、
  // 未設定ならName、重複してもよい(同じNicknameのペインが複数生存している
  // 場合はインスタンス番号で区別される) - Nameのすぐ後ろへ移動
  // (ユーザー要望)。
  const nicknameInput = document.createElement('input');
  nicknameInput.type = 'text';
  nicknameInput.value = initial.nickname ?? '';
  addModalField(box, 'Nickname (MCP 送信先名、任意)', nicknameInput);

  const executableInput = document.createElement('input');
  executableInput.type = 'text';
  executableInput.value = initial.executable;
  addModalField(box, '実行ファイル (Executable) *', executableInput);

  const argsInput = document.createElement('input');
  argsInput.type = 'text';
  argsInput.value = (initial.args ?? []).join(' ');
  addModalField(box, '引数 (スペース区切り)', argsInput);

  const workingDirWrap = document.createElement('div');
  workingDirWrap.style.cssText = 'display:flex; gap:6px;';
  const workingDirInput = document.createElement('input');
  workingDirInput.type = 'text';
  workingDirInput.value = initial.workingDirectory ?? '';
  workingDirInput.style.flex = '1';
  const browseBtn = document.createElement('button');
  browseBtn.type = 'button';
  browseBtn.className = 'modal-btn';
  browseBtn.style.marginTop = '0';
  browseBtn.textContent = '...';
  browseBtn.addEventListener('click', async () => {
    const picked = await invoke('pick_folder').catch(() => null);
    if (picked) workingDirInput.value = picked;
  });
  workingDirWrap.appendChild(workingDirInput);
  workingDirWrap.appendChild(browseBtn);
  addModalField(box, '作業ディレクトリ', workingDirWrap);

  // spec: 「起動時に作業ディレクトリを選択する」(selectWorkingDirOnStart)と
  // 「コマンド追加時に新しい名前を入力する」(promptNewNameOnCommandAdd)は
  // 全プリセットで常に同じ値のペアとして扱われていた(ClaudeCode/Codex/
  // CopilotCliは両方true、Cmd/PowerShell/Zshは両方false)ため、1つのチェック
  // ボックスへ統合(ユーザー要望)。「テンプレートから新規追加」する起動時に
  // 作業ディレクトリを聞き、続けて新しい名前を聞いてこのプロファイルを複製
  // する、という一連の挙動を1つの意味単位として扱う(launchProfileFromMenu
  // 参照)。Rust側のスキーマ自体は2フィールドのまま(他のコード経路が個別に
  // 参照するため)、このダイアログでの入力欄だけを統合する。
  const launchHeading = document.createElement('div');
  launchHeading.className = 'modal-group-title';
  launchHeading.textContent = '起動時の挙動';
  box.appendChild(launchHeading);
  const templateCb = addModalCheckbox(box, 'この設定をテンプレートに新しいコマンドを追加する (起動時に作業ディレクトリ・新しい名前を確認)', initial.selectWorkingDirOnStart || initial.promptNewNameOnCommandAdd);
  const resumeOnStartCb = addModalCheckbox(box, '起動時にセッションを復帰する (claude/copilot=--resume, codex=resume)', initial.resumeOnStart);
  const autoStartCountInput = addModalNumberField(box, '起動時自動生成数', initial.autoStartCount ?? 0, { min: 0, max: 16 });
  const closeProhibitedCb = addModalCheckbox(box, 'クローズ禁止', initial.closeProhibited);

  const ioHeading = document.createElement('div');
  ioHeading.className = 'modal-group-title';
  ioHeading.textContent = '入出力';
  box.appendChild(ioHeading);

  const outputEncodingInput = document.createElement('input');
  outputEncodingInput.type = 'text';
  outputEncodingInput.value = initial.outputEncoding ?? 'UTF-8';
  addModalField(box, '出力エンコーディング', outputEncodingInput);

  const autoChcpCb = addModalCheckbox(box, 'AutoChcp (起動直後に chcp 65001)', initial.autoChcp);

  const waitPatternsArea = document.createElement('textarea');
  waitPatternsArea.style.minHeight = '60px';
  waitPatternsArea.value = (initial.waitPatterns ?? []).join('\n');
  addModalField(box, 'wait パターン (1行1正規表現)', waitPatternsArea);

  const sendLineByLineCb = addModalCheckbox(box, 'マルチライン送信時、行ごとに個別の改行を送る (AI CLI 向け)', initial.sendLineByLine);
  // spec: 連続空行をまとめる/コメント記号は、コマンドごとの設定からアプリ
  // 全体設定へ変更(ユーザー要望、2026-08-04) - 「設定▶全般設定...」へ移動。

  const appearanceHeading = document.createElement('div');
  appearanceHeading.className = 'modal-group-title';
  appearanceHeading.textContent = '見た目';
  box.appendChild(appearanceHeading);

  const fontSizeSelect = document.createElement('select');
  for (const [value, label] of FONT_SIZE_OPTIONS) {
    const opt = document.createElement('option');
    opt.value = value === null ? '' : String(value);
    opt.textContent = label;
    fontSizeSelect.appendChild(opt);
  }
  fontSizeSelect.value = initial.fontSize == null ? '' : String(initial.fontSize);
  addModalField(box, 'フォントサイズ', fontSizeSelect);

  // spec: pane-management's titleBarColor - <input type=color> always has
  // *some* value (browsers default to black), so "no override" is tracked
  // separately in titleBarColorValue rather than inferred from the input.
  const titleColorWrap = document.createElement('div');
  titleColorWrap.style.cssText = 'display:flex; gap:6px; align-items:center;';
  const titleColorInput = document.createElement('input');
  titleColorInput.type = 'color';
  let titleBarColorValue = initial.titleBarColor ?? null;
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

  // spec: クイック送信 (右クリックメニュー) - コマンドごとの設定からアプリ
  // 全体設定へ変更(ユーザー要望、2026-08-04) - 「設定▶全般設定...」へ移動。

  const autoSendHeading = document.createElement('div');
  autoSendHeading.className = 'modal-group-title';
  autoSendHeading.textContent = 'アイドル時自動送信 (AI 向け)';
  box.appendChild(autoSendHeading);
  const autoSendEnabledCb = addModalCheckbox(box, 'アイドル時(ToolUse なし)に自動送信する', initial.autoSendOnIdle?.enabled);
  const autoSendPromptArea = document.createElement('textarea');
  autoSendPromptArea.style.minHeight = '60px';
  autoSendPromptArea.value = initial.autoSendOnIdle?.prompt ?? '';
  addModalField(box, '送信プロンプト', autoSendPromptArea);
  const autoSendDelayInput = addModalNumberField(box, '遅延時間(秒、0=即時)', Math.round((initial.autoSendOnIdle?.delayMs ?? 3000) / 1000), { min: 0, max: 60 });

  const hint = document.createElement('div');
  hint.className = 'modal-hint';
  hint.textContent = '※ Theme / InitialCommands / SessionLog / CtrlCCopyOnSelection / CloseOnExit / WindowGeometry はこのダイアログでは編集できません。必要なら profiles.amm を直接編集してください。';
  box.appendChild(hint);

  let appliedType = typeSelect.value;
  typeSelect.addEventListener('change', async () => {
    const preset = commandTypePresets()[typeSelect.value];
    if (!preset) { appliedType = typeSelect.value; return; } // その他: プリセットなし
    const hasContent = (executableInput.value.trim() && executableInput.value.trim() !== preset.executable) || argsInput.value.trim() || waitPatternsArea.value.trim();
    if (hasContent) {
      const label = commandTypeLabels().find(([v]) => v === typeSelect.value)?.[1] ?? typeSelect.value;
      const ok = await confirmDialog(`「${label}」の既定設定を適用します。\n実行ファイル / 引数 / wait パターンなどが上書きされます。\n(名前 / Nickname / 作業ディレクトリ / クイック送信は保持します)\n\n続行しますか？`);
      if (!ok) { typeSelect.value = appliedType; return; }
    }
    executableInput.value = preset.executable;
    argsInput.value = preset.args.join(' ');
    outputEncodingInput.value = preset.outputEncoding;
    autoChcpCb.checked = preset.autoChcp;
    waitPatternsArea.value = preset.waitPatterns.join('\n');
    sendLineByLineCb.checked = preset.sendLineByLine;
    templateCb.checked = preset.selectWorkingDirOnStart || preset.promptNewNameOnCommandAdd;
    titleBarColorValue = preset.titleBarColor ?? null;
    titleColorInput.value = titleBarColorValue ?? '#2d2d34';
    appliedType = typeSelect.value;
  });

  addModalActions(box, [
    { label: 'キャンセル', onClick: cancel },
    {
      label: 'OK', primary: true,
      onClick: () => {
        if (!nameInput.value.trim()) { alert('名前 (Name) は必須です。'); return; }
        if (!executableInput.value.trim()) { alert('実行ファイル (Executable) は必須です。'); return; }
        // spec: profile-schema「コマンド追加・編集ダイアログでの名前重複時の
        // 入力保持」- 重複チェックは(呼び出し元のonSave内ではなく)ここで
        // closeModal より前に行う。入力済みドラフトを破棄せずこのダイアログに
        // 留まるため。
        if (checkNameConflict && checkNameConflict(nameInput.value.trim())) {
          alert(`同名のコマンドが既に存在します: ${nameInput.value.trim()}`);
          return;
        }
        const autoStartCount = Math.min(16, Math.max(0, parseInt(autoStartCountInput.value, 10) || 0));
        const delaySeconds = Math.min(60, Math.max(0, parseInt(autoSendDelayInput.value, 10) || 0));
        const result = {
          // spec: フィールドを収集しないこのダイアログ経由の保存で、profiles.amm
          // の他フィールド(将来拡張分含む)を失わないよう、defaultSessionProfile()
          // ではなく既存プロファイルの生JSON(initial)を土台にする(.NET原本は
          // BuildProfile()で新規POCOを組み立てるため未収集フィールドを既定値へ
          // 戻してしまう既知の挙動があるが、意図的な改善として保持する側を
          // 採用 - PARITY-AUDIT.md参照)。追加モードでは initial は
          // defaultSessionProfile() 相当なので同じ式で両モードをカバーできる。
          ...initial,
          name: nameInput.value.trim(),
          commandType: typeSelect.value,
          executable: executableInput.value.trim(),
          args: splitArgsInput(argsInput.value),
          workingDirectory: workingDirInput.value.trim() || null,
          outputEncoding: outputEncodingInput.value.trim() || 'UTF-8',
          autoChcp: autoChcpCb.checked,
          waitPatterns: waitPatternsArea.value.split(/\r?\n/).map((s) => s.trim()).filter((s) => s.length > 0),
          autoStartCount,
          closeProhibited: closeProhibitedCb.checked,
          // spec: Nickname 正規化 (EscapeNickname) - #/空白/制御文字を '_' へ、
          // 連続圧縮・前後除去、空なら null。SessionProfile 側は入力をそのまま
          // 保持するだけなので、正規化はここで簡易実装する。
          nickname: normalizeNickname(nicknameInput.value),
          sendLineByLine: sendLineByLineCb.checked,
          selectWorkingDirOnStart: templateCb.checked,
          resumeOnStart: resumeOnStartCb.checked,
          promptNewNameOnCommandAdd: templateCb.checked,
          fontSize: fontSizeSelect.value === '' ? null : parseInt(fontSizeSelect.value, 10),
          titleBarColor: titleBarColorValue,
          // windowGeometry/theme/initialCommands/sessionLog/ctrlCCopyOnSelection/
          // closeOnExit and any unknown forward-compat keys all come from the
          // ...initial spread above unchanged, since this dialog doesn't collect them.
          // quickPromptsはコマンドごとの設定からアプリ全体設定へ変更済み
          // (ユーザー要望2026-08-04) - ここでは扱わない。
          autoSendOnIdle: { enabled: autoSendEnabledCb.checked, prompt: autoSendPromptArea.value, delayMs: delaySeconds * 1000 },
        };
        closeModal();
        onSave(result);
      },
    },
  ]);
}

function normalizeNickname(raw) {
  if (!raw || !raw.trim()) return null;
  let result = '';
  let pendingUnderscore = false;
  for (const ch of raw.trim()) {
    if (ch === '#' || /\s/.test(ch)) {
      pendingUnderscore = result.length > 0;
      continue;
    }
    if (pendingUnderscore) { result += '_'; pendingUnderscore = false; }
    result += ch;
  }
  return result.length === 0 ? null : result;
}

// spec: pane-management - 「コマンド」ボタンのメニュー(btn-command-menu、
// events-integration.js)から直接コマンドを追加する導線(ユーザー要望: 「コマ
// ンドを管理...」を経由しないと新規追加できないのが分かりにくい)。ダイアログ
// 自体はCommandManagerDialogの「追加...」ボタンと同じopenCommandTemplateDialog
// を再利用し、保存はadd_profile(反映はメモリ上のみ、他のプロファイル追加系
// コマンドと同じ convention - 永続化は別途「上書き保存」で行う)。
async function openQuickAddCommandDialog() {
  const existing = await invoke('list_profiles').catch(() => []);
  openCommandAddDialog(async (created, opts) => {
    try {
      await invoke('add_profile', { profile: created });
    } catch (err) {
      alert(`コマンドの追加に失敗しました。\n\n${err}`);
      return;
    }
    statusLine.textContent = `コマンド「${created.name}」を追加しました(「上書き保存」で .amm ファイルへ反映されます)。`;
    // spec: pane-management - 種類を選択画面の「追加」から来た場合は、追加した
    // コマンドをそのまま起動する(ユーザー要望)。
    if (opts?.launch) await launchProfilePane(created.name, created.workingDirectory);
  }, closeModal, (name) => existing.some((p) => p.name.toLowerCase() === name.toLowerCase()));
}

// spec: pane-management's「コマンドタイプ」タブ(ユーザー要望) - 1件のコマンド
// タイププリセットの追加・編集フォーム。openCommandTemplateDialogと似た構造
// だが対象は型プリセットのフィールドのみ(名前/Nickname/作業ディレクトリ/
// クイック送信/アイドル時自動送信等は「コマンド」インスタンス固有の情報で
// プリセットには含まれない)。keyは既存プリセット編集時は変更不可(他の
// コード(profile.rsのinfer_command_type/default_title_bar_color_for_type等)
// やprofiles.amm上の既存コマンドのcommandTypeがこのkey文字列を参照している
// ため、リネームすると既存の紐付けが壊れる)。
function openCommandTypeEditDialog(existing, isAddMode, onSave, onCancel, checkKeyConflict) {
  const cancel = onCancel ?? closeModal;
  const initial = existing ? JSON.parse(JSON.stringify(existing)) : {
    key: '', label: '', executable: '', args: [], outputEncoding: 'UTF-8', autoChcp: false,
    waitPatterns: ['[>]\\s*$'], sendLineByLine: false, selectWorkingDirOnStart: false,
    promptNewNameOnCommandAdd: false, titleBarColor: null,
  };
  const box = openModal(isAddMode ? 'コマンドタイプ追加' : 'コマンドタイプ編集', cancel);

  const keyInput = document.createElement('input');
  keyInput.type = 'text';
  keyInput.value = initial.key;
  keyInput.disabled = !isAddMode;
  addModalField(box, 'キー (内部識別子、既存プリセットは変更不可) *', keyInput);

  const labelInput = document.createElement('input');
  labelInput.type = 'text';
  labelInput.value = initial.label;
  addModalField(box, '表示名 (Label) *', labelInput);

  const executableInput = document.createElement('input');
  executableInput.type = 'text';
  executableInput.value = initial.executable;
  addModalField(box, '実行ファイル (Executable) *', executableInput);

  const argsInput = document.createElement('input');
  argsInput.type = 'text';
  argsInput.value = (initial.args ?? []).join(' ');
  addModalField(box, '引数 (スペース区切り)', argsInput);

  const outputEncodingInput = document.createElement('input');
  outputEncodingInput.type = 'text';
  outputEncodingInput.value = initial.outputEncoding ?? 'UTF-8';
  addModalField(box, '出力エンコーディング', outputEncodingInput);

  const autoChcpCb = addModalCheckbox(box, 'AutoChcp (起動直後に chcp 65001)', initial.autoChcp);

  const waitPatternsArea = document.createElement('textarea');
  waitPatternsArea.style.minHeight = '60px';
  waitPatternsArea.value = (initial.waitPatterns ?? []).join('\n');
  addModalField(box, 'wait パターン (1行1正規表現)', waitPatternsArea);

  const sendLineByLineCb = addModalCheckbox(box, 'マルチライン送信時、行ごとに個別の改行を送る (AI CLI 向け)', initial.sendLineByLine);
  const templateCb = addModalCheckbox(box, 'この設定をテンプレートに新しいコマンドを追加する (起動時に作業ディレクトリ・新しい名前を確認)', initial.selectWorkingDirOnStart || initial.promptNewNameOnCommandAdd);

  const titleColorWrap = document.createElement('div');
  titleColorWrap.style.cssText = 'display:flex; gap:6px; align-items:center;';
  const titleColorInput = document.createElement('input');
  titleColorInput.type = 'color';
  let titleBarColorValue = initial.titleBarColor ?? null;
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

  addModalActions(box, [
    { label: 'キャンセル', onClick: cancel },
    {
      label: 'OK', primary: true,
      onClick: () => {
        const key = keyInput.value.trim();
        if (!key) { alert('キーは必須です。'); return; }
        if (!labelInput.value.trim()) { alert('表示名は必須です。'); return; }
        if (!executableInput.value.trim()) { alert('実行ファイルは必須です。'); return; }
        if (isAddMode && checkKeyConflict && checkKeyConflict(key)) {
          alert(`同じキーのコマンドタイプが既に存在します: ${key}`);
          return;
        }
        onSave({
          key,
          label: labelInput.value.trim(),
          executable: executableInput.value.trim(),
          args: splitArgsInput(argsInput.value),
          outputEncoding: outputEncodingInput.value.trim() || 'UTF-8',
          autoChcp: autoChcpCb.checked,
          waitPatterns: waitPatternsArea.value.split(/\r?\n/).map((s) => s.trim()).filter((s) => s.length > 0),
          sendLineByLine: sendLineByLineCb.checked,
          selectWorkingDirOnStart: templateCb.checked,
          promptNewNameOnCommandAdd: templateCb.checked,
          titleBarColor: titleBarColorValue,
        });
      },
    },
  ]);
}

// spec: CommandManagerDialog - 追加/編集/削除/並べ替えを working copy 上で
// 行い、OK で親側(ProfilesState)へコミットする(反映はメモリ上のみ - この
// 移植では command-import-export のエクスポート/インポートは既に独立した
// ツールバーボタンとして実装済みのため、CommandManagerDialog 自身の
// Import/Export ボタンは統合しない、意図的なスコープ整理として
// PARITY-AUDIT.md に記録)。
async function openCommandManagerDialog() {
  const profiles = (await invoke('list_profiles').catch(() => [])).map((p) => JSON.parse(JSON.stringify(p)));
  // spec: pane-management's「コマンドタイプ」タブ(ユーザー要望) - 編集用の
  // working copy はcachedCommandTypePresets(起動時にget_command_type_presets
  // で解決済み)のディープコピーから始める。OKで確定するまでは既存の「コマ
  // ンド」タブと同じくメモリ上のみの変更。
  const presets = JSON.parse(JSON.stringify(cachedCommandTypePresets ?? []));
  renderCommandManagerDialog(profiles, presets, 'commands');
}

// spec: pane-management - 「コマンド」(File>開くで切り替わる.ammファイルの
// 内容)と「コマンドタイプ」(アプリ全体設定、profile.rs::command_type_
// presets_file)はタブで切り替える1つのダイアログにまとめる(ユーザー要望)。
// 両タブとも working copy 上で編集し、ダイアログ下部のOKボタン1つで両方まとめて
// コミットする(コマンド: commit_profiles、コマンドタイプ: set_command_type_
// presets)。
function renderCommandManagerDialog(profiles, presets, activeTab) {
  const box = openModal('コマンドを管理');

  const tabBar = document.createElement('div');
  tabBar.className = 'modal-tab-bar';
  const tabs = [['commands', 'コマンド'], ['types', 'コマンドタイプ']];
  for (const [id, label] of tabs) {
    const tabBtn = document.createElement('button');
    tabBtn.className = 'modal-tab' + (id === activeTab ? ' active' : '');
    tabBtn.textContent = label;
    tabBtn.addEventListener('click', () => renderCommandManagerDialog(profiles, presets, id));
    tabBar.appendChild(tabBtn);
  }
  box.appendChild(tabBar);

  if (activeTab === 'types') {
    renderCommandTypesTab(box, profiles, presets);
  } else {
    renderCommandsTab(box, profiles, presets);
  }
}

function renderCommandsTab(box, profiles, presets) {
  const list = document.createElement('div');
  list.className = 'modal-list';
  profiles.forEach((p, i) => {
    const row = document.createElement('div');
    row.className = 'modal-list-item';
    const label = document.createElement('label');
    label.style.flex = '1';
    label.textContent = `[${p.commandType}]  ${p.name}  —  ${p.executable} ${(p.args ?? []).join(' ')}`;
    row.appendChild(label);
    const mkBtn = (text, onClick) => {
      const b = document.createElement('button');
      b.textContent = text;
      b.addEventListener('click', onClick);
      row.appendChild(b);
    };
    mkBtn('編集...', () => {
      closeModal();
      openCommandTemplateDialog(p, false, (updated) => {
        const next = profiles.slice();
        next[i] = updated;
        renderCommandManagerDialog(next, presets, 'commands');
      }, () => renderCommandManagerDialog(profiles, presets, 'commands'),
      (name) => profiles.some((other, j) => j !== i && other.name.toLowerCase() === name.toLowerCase()));
    });
    mkBtn('削除', async () => {
      if (!(await confirmDialog(`コマンド「${p.name}」を削除しますか?\n\n(OK 押下までは確定しません。キャンセルで削除を取り消せます。)`))) return;
      const next = profiles.slice();
      next.splice(i, 1);
      renderCommandManagerDialog(next, presets, 'commands');
    });
    mkBtn('↑', () => {
      if (i === 0) return;
      const next = profiles.slice();
      [next[i - 1], next[i]] = [next[i], next[i - 1]];
      renderCommandManagerDialog(next, presets, 'commands');
    });
    mkBtn('↓', () => {
      if (i === profiles.length - 1) return;
      const next = profiles.slice();
      [next[i], next[i + 1]] = [next[i + 1], next[i]];
      renderCommandManagerDialog(next, presets, 'commands');
    });
    list.appendChild(row);
  });
  box.appendChild(list);

  const addBtn = document.createElement('button');
  addBtn.className = 'modal-btn';
  addBtn.textContent = '追加...';
  addBtn.addEventListener('click', () => {
    closeModal();
    openCommandAddDialog((created) => {
      renderCommandManagerDialog([...profiles, created], presets, 'commands');
    }, () => renderCommandManagerDialog(profiles, presets, 'commands'),
    (name) => profiles.some((p) => p.name.toLowerCase() === name.toLowerCase()));
  });
  box.appendChild(addBtn);

  const exportBtn = document.createElement('button');
  exportBtn.className = 'modal-btn';
  exportBtn.textContent = 'エクスポート...';
  exportBtn.addEventListener('click', () => { closeModal(); openManagerExportDialog(profiles); });
  box.appendChild(exportBtn);

  const importBtn = document.createElement('button');
  importBtn.className = 'modal-btn';
  importBtn.textContent = 'インポート...';
  importBtn.addEventListener('click', () => { closeModal(); openManagerImportDialog(profiles); });
  box.appendChild(importBtn);

  renderCommandManagerFooter(box, profiles, presets);
}

// spec: pane-management's「コマンドタイプ」タブ本体(ユーザー要望) - 各プリ
// セットの編集・削除・追加、既定値への一括リセットを提供する。「コマンド」
// タブと同じ working-copy パターン(OK確定まではメモリ上のみ)。
function renderCommandTypesTab(box, profiles, presets) {
  const hint = document.createElement('div');
  hint.className = 'modal-hint';
  hint.style.marginBottom = '10px';
  hint.textContent = '「コマンド追加」画面でコマンドタイプを選んだ際に自動入力される初期値です。ここでの変更はどの「コマンド」ファイルを開いていても共通で適用されます。';
  box.appendChild(hint);

  const list = document.createElement('div');
  list.className = 'modal-list';
  presets.forEach((preset, i) => {
    const row = document.createElement('div');
    row.className = 'modal-list-item';
    const label = document.createElement('label');
    label.style.flex = '1';
    label.textContent = `[${preset.key}]  ${preset.label}  —  ${preset.executable} ${(preset.args ?? []).join(' ')}`;
    row.appendChild(label);
    const mkBtn = (text, onClick) => {
      const b = document.createElement('button');
      b.textContent = text;
      b.addEventListener('click', onClick);
      row.appendChild(b);
    };
    mkBtn('編集...', () => {
      closeModal();
      openCommandTypeEditDialog(preset, false, (updated) => {
        const next = presets.slice();
        next[i] = updated;
        renderCommandManagerDialog(profiles, next, 'types');
      }, () => renderCommandManagerDialog(profiles, presets, 'types'));
    });
    mkBtn('削除', async () => {
      if (!(await confirmDialog(`コマンドタイプ「${preset.label}」を削除しますか?\n\n(OK 押下までは確定しません。キャンセルで削除を取り消せます。)`))) return;
      const next = presets.slice();
      next.splice(i, 1);
      renderCommandManagerDialog(profiles, next, 'types');
    });
    list.appendChild(row);
  });
  box.appendChild(list);

  const addBtn = document.createElement('button');
  addBtn.className = 'modal-btn';
  addBtn.textContent = '追加...';
  addBtn.addEventListener('click', () => {
    closeModal();
    openCommandTypeEditDialog(null, true, (created) => {
      renderCommandManagerDialog(profiles, [...presets, created], 'types');
    }, () => renderCommandManagerDialog(profiles, presets, 'types'),
    (key) => presets.some((p) => p.key.toLowerCase() === key.toLowerCase()));
  });
  box.appendChild(addBtn);

  const resetBtn = document.createElement('button');
  resetBtn.className = 'modal-btn';
  resetBtn.textContent = '既定値に戻す';
  resetBtn.addEventListener('click', async () => {
    if (!(await confirmDialog('コマンドタイプを既定値に戻しますか?(現在の編集内容は失われます。OK 押下まで確定しません)'))) return;
    const defaults = await invoke('reset_command_type_presets').catch(() => presets);
    renderCommandManagerDialog(profiles, defaults, 'types');
  });
  box.appendChild(resetBtn);

  renderCommandManagerFooter(box, profiles, presets);
}

// spec: pane-management - 「コマンド」「コマンドタイプ」両タブ共通のOK/
// キャンセル。OKで両working copyを同時にコミットする(コマンド:
// commit_profiles、コマンドタイプ: set_command_type_presets)。コマンドタイプ
// は保存成功後にcachedCommandTypePresets(pane-layout.js)もその場で更新し、
// 次に開くコマンド追加画面等へ再起動なしで反映する。
function renderCommandManagerFooter(box, profiles, presets) {
  addModalActions(box, [
    { label: 'キャンセル', onClick: closeModal },
    {
      label: 'OK', primary: true,
      onClick: async () => {
        await invoke('commit_profiles', { profiles }).catch(() => {});
        try {
          await invoke('set_command_type_presets', { presets });
          cachedCommandTypePresets = presets;
        } catch (err) {
          alert(`コマンドタイプの保存に失敗しました。\n\n${err}`);
        }
        closeModal();
      },
    },
  ]);
}

// spec: AmmSettingsDialog - CloseProhibited(コマンドごと)を編集し、確定後に
// AMMファイルへ書き戻すかどうか確認する。連続空行をまとめる/コメント記号
// (2026-08-04)・エディタ連携設定(2026-08-05、ユーザー要望:「エディタ連携:
// 使用するエディタ」「カスタムエディタのパス」「送信後アクション」はアプリ
// 全体で良く、ペインごとに設定しなくても良い」)はいずれもアプリ全体設定へ
// 移動済み(「設定▶全般設定...」)。残る項目(クローズ禁止)はそのペインに
// 紐づいたコマンド固有の設定そのものなので、「AMM設定」から「コマンド設定」
// へ改称した(ユーザー要望、2026-08-05)。システムメニュー「コマンド設定…」
// から呼ぶ。
async function openCommandSettingsDialog(pane) {
  if (!pane?.profileName) {
    alert('このペインには紐付いたプロファイルがありません。');
    return;
  }
  const profiles = await invoke('list_profiles').catch(() => []);
  const profile = profiles.find((p) => p.name === pane.profileName);
  if (!profile) return;

  const box = openModal(`コマンド設定 — ${profile.name}`);
  const closeProhibitedCb = addModalCheckbox(box, 'クローズ禁止', profile.closeProhibited);

  addModalActions(box, [
    { label: 'キャンセル', onClick: closeModal },
    {
      label: 'OK', primary: true,
      onClick: async () => {
        closeModal();
        const persist = await confirmDialog('AMM ファイルへ保存しますか?\n(キャンセルするとメモリ上のみの変更になります)');
        await invoke('update_profile_settings', {
          profileName: profile.name,
          closeProhibited: closeProhibitedCb.checked,
          persist,
        }).catch((err) => alert(`コマンド設定の更新に失敗しました。\n\n${err}`));
      },
    },
  ]);
}

// spec: 送信前のテキスト整形(連続空行をまとめる/コメント記号)・クイック
// 送信の登録内容・エディタ連携設定 - いずれもコマンドごとの設定からアプリ
// 全体設定へ変更(ユーザー要望: 送信前のテキスト整形は2026-08-04「共通入力欄
// ・エディタ連携のみ有効に」、クイック送信は同日「アプリ共通の設定で良い。
// ペイン毎の設定は不要とする」、エディタ連携設定は2026-08-05「アプリ全体で
// 良く、ペインごとに設定しなくても良い」)。エディタ連携設定はもともと
// design.md D2の時点でアプリ全体設定だった(get_editor_settings/
// set_editor_settings自体はプロファイル非依存)が、UI上は「AMM設定」という
// ペイン単位に見えるダイアログに置かれていて分かりにくかったため、ここへ
// 移設した。トップバーの新設「設定▶」ボタン(旧「設定▶」は「AI設定▶」に
// 改称)から呼ぶ。
async function openGeneralSettingsDialog() {
  const [settings, quickPrompts, editorSettings] = await Promise.all([
    invoke('get_format_settings').catch(() => ({ collapseBlankLines: true, commentPrefixes: ["'", '//'] })),
    invoke('get_quick_prompts').catch(() => []),
    invoke('get_editor_settings').catch(() => ({ editorMode: 'Associated', customEditorPath: '', postSendAction: 'Focus' })),
  ]);

  const box = openModal('全般設定');
  const hint = document.createElement('div');
  hint.className = 'modal-hint';
  hint.style.marginBottom = '10px';
  hint.textContent = '送信前のテキスト整形(連続空行/コメント記号)は共通入力欄・エディタ連携からの送信にのみ適用されます(クイック送信・プロンプト再送信・他CLIからのMCP経由送信・アイドル時自動送信には適用されません)。';
  box.appendChild(hint);

  const collapseBlankLinesCb = addModalCheckbox(box, '連続空行を 1 行にまとめる', settings.collapseBlankLines);
  const commentPrefixesInput = document.createElement('input');
  commentPrefixesInput.type = 'text';
  commentPrefixesInput.value = (settings.commentPrefixes ?? []).join(',');
  addModalField(box, 'コメント記号 (CSV)', commentPrefixesInput);

  const editorHeading = document.createElement('div');
  editorHeading.className = 'modal-group-title';
  editorHeading.textContent = 'エディタ連携';
  box.appendChild(editorHeading);

  const editorModeSelect = document.createElement('select');
  for (const [value, label] of [['Associated', '既定の関連付けアプリ'], ['Notepad', 'メモ帳'], ['Custom', 'カスタム...']]) {
    const opt = document.createElement('option');
    opt.value = value;
    opt.textContent = label;
    editorModeSelect.appendChild(opt);
  }
  editorModeSelect.value = editorSettings.editorMode ?? 'Associated';
  addModalField(box, '使用するエディタ', editorModeSelect);

  const customEditorWrap = document.createElement('div');
  customEditorWrap.style.cssText = 'display:flex; gap:6px;';
  const customEditorInput = document.createElement('input');
  customEditorInput.type = 'text';
  customEditorInput.value = editorSettings.customEditorPath ?? '';
  customEditorInput.style.flex = '1';
  const customEditorBrowseBtn = document.createElement('button');
  customEditorBrowseBtn.type = 'button';
  customEditorBrowseBtn.className = 'modal-btn';
  customEditorBrowseBtn.style.marginTop = '0';
  customEditorBrowseBtn.textContent = '...';
  customEditorBrowseBtn.addEventListener('click', async () => {
    const picked = await invoke('pick_editor_exe_path').catch(() => null);
    if (picked) customEditorInput.value = picked;
  });
  customEditorWrap.appendChild(customEditorInput);
  customEditorWrap.appendChild(customEditorBrowseBtn);
  addModalField(box, 'カスタムエディタのパス', customEditorWrap);

  const postSendActionSelect = document.createElement('select');
  for (const [value, label] of [['Focus', 'フォーカス'], ['Maximize', '最大化'], ['None', '何もしない']]) {
    const opt = document.createElement('option');
    opt.value = value;
    opt.textContent = label;
    postSendActionSelect.appendChild(opt);
  }
  postSendActionSelect.value = editorSettings.postSendAction ?? 'Focus';
  addModalField(box, '送信後アクション', postSendActionSelect);

  // spec: クイック送信 (右クリックメニュー) - simple add/remove-row editor,
  // 旧・コマンドごとの編集画面から移設した同じUIパターン。
  const quickPromptsHeading = document.createElement('div');
  quickPromptsHeading.className = 'modal-group-title';
  quickPromptsHeading.textContent = 'クイック送信 (右クリックメニュー、全ペイン共通)';
  box.appendChild(quickPromptsHeading);
  const quickPromptsList = document.createElement('div');
  quickPromptsList.className = 'modal-list';
  box.appendChild(quickPromptsList);
  const editableQuickPrompts = quickPrompts.map((q) => ({ label: q.label ?? '', prompt: q.prompt ?? '' }));
  function renderQuickPrompts() {
    quickPromptsList.innerHTML = '';
    editableQuickPrompts.forEach((qp, i) => {
      const row = document.createElement('div');
      row.className = 'modal-list-item';
      const labelInput = document.createElement('input');
      labelInput.type = 'text';
      labelInput.placeholder = 'Label';
      labelInput.value = qp.label;
      labelInput.style.width = '120px';
      labelInput.addEventListener('input', () => { qp.label = labelInput.value; });
      const promptInputEl = document.createElement('input');
      promptInputEl.type = 'text';
      promptInputEl.placeholder = 'Prompt';
      promptInputEl.value = qp.prompt;
      promptInputEl.style.flex = '1';
      promptInputEl.addEventListener('input', () => { qp.prompt = promptInputEl.value; });
      const removeBtn = document.createElement('button');
      removeBtn.textContent = '削除';
      removeBtn.addEventListener('click', () => { editableQuickPrompts.splice(i, 1); renderQuickPrompts(); });
      row.appendChild(labelInput);
      row.appendChild(promptInputEl);
      row.appendChild(removeBtn);
      quickPromptsList.appendChild(row);
    });
  }
  renderQuickPrompts();
  const addQuickPromptBtn = document.createElement('button');
  addQuickPromptBtn.className = 'modal-btn';
  addQuickPromptBtn.textContent = '行を追加';
  addQuickPromptBtn.addEventListener('click', () => { editableQuickPrompts.push({ label: '', prompt: '' }); renderQuickPrompts(); });
  box.appendChild(addQuickPromptBtn);

  addModalActions(box, [
    { label: 'キャンセル', onClick: closeModal },
    {
      label: 'OK', primary: true,
      onClick: async () => {
        closeModal();
        await invoke('set_format_settings', {
          settings: {
            collapseBlankLines: collapseBlankLinesCb.checked,
            commentPrefixes: commentPrefixesInput.value.split(',').map((s) => s.trim()).filter((s) => s.length > 0),
          },
        }).catch((err) => alert(`全般設定の更新に失敗しました。\n\n${err}`));
        await invoke('set_quick_prompts', {
          prompts: editableQuickPrompts.filter((q) => q.label || q.prompt),
        }).catch((err) => alert(`クイック送信の更新に失敗しました。\n\n${err}`));
        await invoke('set_editor_settings', {
          settings: {
            editorMode: editorModeSelect.value,
            customEditorPath: customEditorInput.value,
            postSendAction: postSendActionSelect.value,
          },
        }).catch((err) => alert(`エディタ連携設定の更新に失敗しました。\n\n${err}`));
      },
    },
  ]);
}

