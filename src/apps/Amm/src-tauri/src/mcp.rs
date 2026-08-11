// Named Pipe MCP server (spec: mcp-server). Rust re-implementation of
// src/apps/Amm/Core/Mcp/{McpPipeServer,MessageDispatcher,WaitBroker,MessageQueue}.cs,
// mirroring the exact wire protocol (method names, JSON-RPC error codes,
// response shapes) so the existing .NET Amm.Mcp.exe stdio<->pipe bridge and
// Amm.PowerShell module can connect unchanged. Tool names are renamed per
// UDR-amm-20260713T0447-98f: mdi/open -> pane/open, mdi/close -> pane/close,
// mdi/wait_state -> pane/wait_state, amm.openWindow -> amm.openPane,
// amm.closeWindow -> amm.closePane (amm.waitState keeps its name).
use crate::native_ui;
use crate::profile;
use serde_json::{json, Value};
use std::collections::{HashMap, VecDeque};
use std::sync::atomic::{AtomicU64, Ordering};
use tauri::{AppHandle, Emitter, Manager};
use tokio::io::{AsyncBufReadExt, AsyncWriteExt, BufReader};
#[cfg(windows)]
use tokio::net::windows::named_pipe::ServerOptions;
use tokio::sync::{oneshot, Mutex};

pub const PROTOCOL_VERSION: &str = "2024-11-05";
const SERVER_NAME: &str = "amm-operator";
const SERVER_VERSION: &str = "0.3.0";
const MAX_QUEUE_PER_NICKNAME: usize = 100;
const DEFAULT_TIMEOUT_MS: u64 = 300_000;
const MAX_LINE_BYTES: usize = 1024 * 1024;

// Field names/casing intentionally mirror McpPipeServer.cs's list_participants
// output exactly (session_id stays snake_case, is_waiting is routing-only and
// was never exposed in the original JSON) - Amm.PowerShell's cmdlets read
// participants["session_id"] and never read isWaiting at all.
#[derive(Clone, serde::Serialize)]
pub struct Participant {
  pub nickname: String,
  pub profile: String,
  pub instance: u32,
  pub state: String,
  #[serde(skip)]
  pub is_waiting: bool,
  pub session_id: String,
}

struct WaitEntry {
  target_state: String,
  tx: oneshot::Sender<(String, u128)>,
}

#[derive(Default)]
pub struct McpState {
  // session_id (== pane_id) -> participant. Only panes opened with a
  // profile_name get an entry (no profile-schema yet, phase 5.1, so
  // profile_name is used directly as the nickname in the meantime).
  participants: Mutex<HashMap<String, Participant>>,
  queues: Mutex<HashMap<String, VecDeque<String>>>,
  waiters: Mutex<HashMap<String, Vec<(u64, WaitEntry)>>>,
  next_waiter_id: AtomicU64,
}

impl McpState {
  pub async fn register_participant(&self, session_id: &str, nickname: &str, profile: &str) {
    let mut participants = self.participants.lock().await;
    let instance = participants.values().filter(|p| p.nickname.eq_ignore_ascii_case(nickname)).count() as u32 + 1;
    participants.insert(
      session_id.to_string(),
      Participant {
        nickname: nickname.to_string(),
        profile: profile.to_string(),
        instance,
        state: "running".into(),
        is_waiting: false,
        session_id: session_id.to_string(),
      },
    );
  }

  // spec: pane-management - ペインタイトルバーの「名前変更」がペインの
  // NickName(MCP送信先名)そのものを変更するようになった(ユーザー要望、
  // 2026-08-03: 「ペイン切り替えボタンやペインタイトルの名前は起動時に確定
  // したNickNameとする。よってペインの名前変更ボタンはNickNameを変更する
  // ボタンとなる」)。register_participant同様、新しいnicknameを共有する
  // 既存パーティシパント数からinstanceを再計算する(この時点で旧nicknameの
  // instanceだった枠を1つ空けるため、旧nicknameの他パーティシパントの
  // instance再採番はしない - register_participant自体も新規登録時にしか
  // 採番し直さない設計と揃える)。
  pub async fn rename_participant(&self, session_id: &str, new_nickname: &str) -> bool {
    let mut participants = self.participants.lock().await;
    if !participants.contains_key(session_id) {
      return false;
    }
    let instance = participants
      .values()
      .filter(|p| p.session_id != session_id && p.nickname.eq_ignore_ascii_case(new_nickname))
      .count() as u32
      + 1;
    if let Some(p) = participants.get_mut(session_id) {
      p.nickname = new_nickname.to_string();
      p.instance = instance;
    }
    true
  }

  pub async fn unregister_participant(&self, session_id: &str) {
    self.participants.lock().await.remove(session_id);
    self.resolve_by_session(session_id, "timeout").await;
  }

  pub async fn report_state(&self, session_id: &str, state: &str, is_waiting: bool) {
    if let Some(p) = self.participants.lock().await.get_mut(session_id) {
      p.state = state.to_string();
      p.is_waiting = is_waiting;
    }
  }

  async fn list_participants(&self) -> Vec<Participant> {
    self.participants.lock().await.values().cloned().collect()
  }

  fn resolve_targets(participants: &[Participant], recipient: Option<&str>, mode: &str) -> Vec<Participant> {
    let Some(recipient) = recipient.filter(|r| !r.is_empty()) else {
      return participants.to_vec();
    };
    let mut matches: Vec<Participant> = participants
      .iter()
      .filter(|p| p.nickname.eq_ignore_ascii_case(recipient))
      .cloned()
      .collect();
    if matches.is_empty() {
      return matches;
    }
    if mode.eq_ignore_ascii_case("all") {
      return matches;
    }
    // correctness: code-review 2026-07-26 finding. `participants` (and thus
    // `matches`) ultimately derives from list_participants()'s
    // `HashMap::values()`, whose iteration order is randomized per-process
    // (SipHash-based) and carries no relation to launch order. Sorting by
    // `instance` (the per-nickname launch-order counter assigned in
    // register_participant) before falling back to "the first one" is what
    // actually makes that fallback mean "the oldest/first-launched
    // instance", matching mcp-server/spec.md's "起動順の先頭" requirement -
    // without this, the previous `matches[0]` picked a process-random
    // instance instead.
    matches.sort_by_key(|p| p.instance);
    matches
      .iter()
      .find(|p| p.is_waiting)
      .cloned()
      .map(|p| vec![p])
      .unwrap_or_else(|| vec![matches[0].clone()])
  }

  async fn enqueue(&self, nickname: &str, message: &str) {
    let mut queues = self.queues.lock().await;
    let q = queues.entry(nickname.to_string()).or_default();
    q.push_back(message.to_string());
    while q.len() > MAX_QUEUE_PER_NICKNAME {
      q.pop_front();
    }
  }

  async fn peek_queue(&self, recipient: Option<&str>) -> HashMap<String, Vec<String>> {
    let queues = self.queues.lock().await;
    match recipient.filter(|r| !r.is_empty()) {
      Some(r) => {
        let mut out = HashMap::new();
        out.insert(r.to_string(), queues.get(r).map(|q| q.iter().cloned().collect()).unwrap_or_default());
        out
      }
      None => queues.iter().map(|(k, v)| (k.clone(), v.iter().cloned().collect())).collect(),
    }
  }

  async fn register_wait(&self, session_id: &str, target_state: &str, timeout_ms: u64) -> (String, u128) {
    let (tx, rx) = oneshot::channel();
    let waiter_id = self.next_waiter_id.fetch_add(1, Ordering::Relaxed);
    {
      let mut waiters = self.waiters.lock().await;
      waiters.entry(session_id.to_string()).or_default().push((
        waiter_id,
        WaitEntry { target_state: target_state.to_string(), tx },
      ));
    }
    let started = std::time::Instant::now();
    match tokio::time::timeout(std::time::Duration::from_millis(timeout_ms), rx).await {
      Ok(Ok((state, elapsed))) => (state, elapsed),
      _ => {
        let mut waiters = self.waiters.lock().await;
        if let Some(list) = waiters.get_mut(session_id) {
          list.retain(|(id, _)| *id != waiter_id);
        }
        ("timeout".to_string(), started.elapsed().as_millis())
      }
    }
  }

  pub async fn resolve_by_session(&self, session_id: &str, state: &str) {
    let mut waiters = self.waiters.lock().await;
    if let Some(list) = waiters.get_mut(session_id) {
      let mut remaining = Vec::new();
      for (id, entry) in list.drain(..) {
        if entry.target_state.eq_ignore_ascii_case(state) {
          let _ = entry.tx.send((state.to_string(), 0));
        } else {
          remaining.push((id, entry));
        }
      }
      *list = remaining;
    }
  }
}

#[cfg(test)]
mod resolve_targets_tests {
  use super::*;

  fn participant(nickname: &str, instance: u32, session_id: &str, is_waiting: bool) -> Participant {
    Participant {
      nickname: nickname.to_string(),
      profile: "TestProfile".to_string(),
      instance,
      state: if is_waiting { "waiting".into() } else { "running".into() },
      is_waiting,
      session_id: session_id.to_string(),
    }
  }

  #[test]
  fn falls_back_to_oldest_instance_deterministically_when_none_are_waiting() {
    // correctness: regression test for code-review 2026-07-26 finding -
    // previously the fallback picked participants[0] from whatever order
    // HashMap::values() happened to yield (process-random), not
    // necessarily the oldest instance. Feed instances out of order to
    // catch a reintroduced reliance on input order.
    let participants = vec![
      participant("claude", 3, "session-3", false),
      participant("claude", 1, "session-1", false),
      participant("claude", 2, "session-2", false),
    ];
    let targets = McpState::resolve_targets(&participants, Some("claude"), "first");
    assert_eq!(targets.len(), 1);
    assert_eq!(targets[0].instance, 1, "must resolve to the oldest (instance 1) participant, not input order");
    assert_eq!(targets[0].session_id, "session-1");
  }

  #[test]
  fn prefers_a_waiting_instance_over_the_oldest_when_present() {
    let participants = vec![
      participant("claude", 1, "session-1", false),
      participant("claude", 2, "session-2", true),
      participant("claude", 3, "session-3", false),
    ];
    let targets = McpState::resolve_targets(&participants, Some("claude"), "first");
    assert_eq!(targets.len(), 1);
    assert_eq!(targets[0].session_id, "session-2", "a waiting participant must win over instance order");
  }

  #[test]
  fn mode_all_returns_every_match_regardless_of_waiting_state() {
    let participants = vec![participant("claude", 2, "session-2", false), participant("claude", 1, "session-1", true)];
    let targets = McpState::resolve_targets(&participants, Some("claude"), "all");
    assert_eq!(targets.len(), 2);
  }
}

fn make_result(id: &Value, result: Value) -> Value {
  json!({ "jsonrpc": "2.0", "id": id, "result": result })
}

fn make_error(id: &Value, code: i32, message: &str) -> Value {
  json!({ "jsonrpc": "2.0", "id": id, "error": { "code": code, "message": message } })
}

fn make_tool_result(id: &Value, structured_content: Value) -> Value {
  let text = structured_content.to_string();
  make_result(
    id,
    json!({
      "content": [{ "type": "text", "text": text }],
      "isError": false,
      "structuredContent": structured_content,
    }),
  )
}

fn schema_prop(ty: &str, description: &str) -> Value {
  json!({ "type": ty, "description": description })
}

fn build_tool_def(name: &str, description: &str, input_schema: Value) -> Value {
  json!({ "name": name, "description": description, "inputSchema": input_schema })
}

fn tools_list_result() -> Value {
  json!([
    build_tool_def(
      "send_message",
      "Send a message to one or more panes that have a registered nickname. If recipient is omitted, broadcasts to all eligible. mode='first' (default) picks the input-waiting one, falling back to launch order. mode='all' targets every pane sharing the nickname.",
      json!({
        "type": "object",
        "properties": {
          "recipient": schema_prop("string", "Nickname of the target pane. Omit to broadcast."),
          "mode": schema_prop("string", "'first' or 'all'. Default 'first'."),
          "message": schema_prop("string", "Text to inject. Newlines are sent as-is."),
        },
        "required": ["message"],
      }),
    ),
    build_tool_def(
      "list_participants",
      "List all panes that have a registered nickname.",
      json!({ "type": "object", "properties": {} }),
    ),
    build_tool_def(
      "peek_queue",
      "Inspect (without dequeuing) messages waiting for delivery. Optionally filter by recipient nickname.",
      json!({
        "type": "object",
        "properties": { "recipient": schema_prop("string", "Nickname to filter on. Omit to see all queues.") },
      }),
    ),
    build_tool_def(
      "pane/open",
      "Open a new pane and return its session_id for subsequent operations. Specify either 'command' (ephemeral) or 'profile_name'.",
      json!({
        "type": "object",
        "properties": {
          "command": schema_prop("string", "Executable to launch (e.g. 'claude', 'cmd.exe'). Required when profile_name is omitted."),
          "profile_name": schema_prop("string", "Nickname/profile label for this pane. Required when command is omitted."),
          "args": { "type": "array", "items": { "type": "string" }, "description": "Command-line arguments." },
          "title": schema_prop("string", "Pane title override."),
          "workingDirectory": schema_prop("string", "Working directory override."),
        },
      }),
    ),
    build_tool_def(
      "pane/close",
      "Close the pane identified by session_id.",
      json!({
        "type": "object",
        "properties": {
          "session_id": schema_prop("string", "session_id returned by pane/open."),
          "force": { "type": "boolean", "description": "Skip confirmation dialog." },
        },
        "required": ["session_id"],
      }),
    ),
    build_tool_def(
      "pane/wait_state",
      "Block until the specified pane reaches the target state or times out. target_state: 'idle' = waiting for user input, 'attention' = awaiting permission approval.",
      json!({
        "type": "object",
        "properties": {
          "session_id": schema_prop("string", "session_id returned by pane/open."),
          "target_state": schema_prop("string", "'idle' or 'attention'."),
          "timeout_ms": { "type": "integer", "description": "Timeout in milliseconds (default 300000 = 5 min)." },
        },
        "required": ["session_id", "target_state"],
      }),
    ),
  ])
}

struct Ctx<'a> {
  app: &'a AppHandle,
  mcp: &'a McpState,
}

async fn open_pane(ctx: &Ctx<'_>, args: &Value) -> Value {
  let command = args.get("command").and_then(|v| v.as_str());
  let profile_name = args.get("profile_name").and_then(|v| v.as_str());
  if command.is_none() && profile_name.is_none() {
    return json!({ "error": "command or profile_name is required" });
  }

  // profile_name resolves against profiles.amm (spec: profile-schema) when a
  // matching entry exists; otherwise falls back to using the name directly
  // as a nickname stand-in (pre-5.1 behavior) so callers aren't broken by an
  // unrecognized name.
  let profiles_state = ctx.app.state::<crate::ProfilesState>();
  let resolved_profile = profile_name.and_then(|n| profiles_state.find_by_name(n));

  // spec: profile-schema's windowGeometry - "同 profile の生存数+1" を index
  // として参照する (found missing entirely in the phase 8.1 parity audit).
  // Resolved before effective_cwd/label below since a saved entry's
  // workingDirectory/name take priority there, same as MdiParentForm.cs's
  // OpenTerminal.
  let state = ctx.app.state::<crate::PtyState>();
  let geometry_plan = resolved_profile
    .as_ref()
    .map(|p| profile::resolve_geometry_apply_plan(&p.window_geometry, state.alive_count_for_profile(&p.name) + 1))
    .unwrap_or_default();

  // spec: 起動コマンドラインの解決とセキュリティ / セッション復帰トークンの付加
  // (found missing entirely in the phase 8.1 parity audit) - both apply
  // whenever a profile provides the executable/args, matching the .NET
  // original's BuildLaunchCommandLine/EffectiveArgs.
  let (effective_command, effective_args, effective_cwd, nickname) = if let Some(p) = &resolved_profile {
    let cli_args: Vec<String> = args
      .get("args")
      .and_then(|v| v.as_array())
      .map(|a| a.iter().filter_map(|v| v.as_str().map(String::from)).collect())
      .unwrap_or_else(|| profile::effective_args(p));
    let cwd = args
      .get("workingDirectory")
      .and_then(|v| v.as_str())
      .map(String::from)
      .or_else(|| geometry_plan.working_directory.clone())
      .or_else(|| p.working_directory.clone());
    // spec: profile-schema's ResolveWorkingDirectory ("未指定/空なら現在の
    // カレントディレクトリを返す") - found via live CDP testing (phase 8.2)
    // that this normalization was missing entirely: profiles.amm's
    // workingDirectory="" (.NET's plain `string` default, not null) was
    // short-circuiting spawn_pty's None-only current_dir() fallback into an
    // unusable empty cwd, and "%USERPROFILE%"-style values (the
    // CommandTemplates presets' own default) were never env-expanded at all.
    let cwd = profile::resolve_working_directory(cwd.as_deref());
    (Some(profile::resolve_executable_path(&p.executable)), cli_args, cwd, p.nickname.clone().unwrap_or_else(|| p.name.clone()))
  } else {
    let cli_args: Vec<String> = args
      .get("args")
      .and_then(|v| v.as_array())
      .map(|a| a.iter().filter_map(|v| v.as_str().map(String::from)).collect())
      .unwrap_or_default();
    let cwd = args.get("workingDirectory").and_then(|v| v.as_str());
    let cwd = profile::resolve_working_directory(cwd);
    (command.map(profile::resolve_executable_path), cli_args, cwd, profile_name.unwrap_or_default().to_string())
  };

  let wait_patterns = resolved_profile.as_ref().map(|p| p.wait_patterns.clone()).unwrap_or_default();
  let auto_chcp = resolved_profile.as_ref().map(|p| p.auto_chcp).unwrap_or(false);
  let output_encoding = resolved_profile.as_ref().and_then(|p| p.output_encoding.clone());
  let pane_id = uuid::Uuid::new_v4().to_string();
  if let Err(e) = crate::spawn_pty_for_pane_with_patterns(
    ctx.app,
    &state,
    pane_id.clone(),
    effective_command,
    effective_args,
    effective_cwd,
    &wait_patterns,
    resolved_profile.as_ref().map(|p| p.name.clone()),
    auto_chcp,
    output_encoding,
  )
  {
    return json!({ "error": e });
  }

  if profile_name.is_some() {
    ctx.mcp.register_participant(&pane_id, &nickname, profile_name.unwrap()).await;
  }

  // spec: pane-management - ペインタイトル/クイック切替バーの表示名は
  // 「起動時に確定したNickName」とする(ユーザー要望、2026-08-03)。nickname
  // は既にprofile.nickname(未設定ならprofile.name)へフォールバック済みの
  // 値のため、明示的なtitle引数・記憶済みgeometry名という「意図的な上書き」
  // より優先度を下げつつ、旧来のprofile_name直接表示より優先する。command
  // のみ指定(profile_name無し)のad-hocペインはnicknameが空文字列のため
  // ここでは使われず、従来通りcommand名へフォールバックする。
  let label = args
    .get("title")
    .and_then(|v| v.as_str())
    .map(String::from)
    .or_else(|| geometry_plan.name.clone())
    .or_else(|| (!nickname.is_empty()).then(|| nickname.clone()))
    .or_else(|| profile_name.map(String::from))
    .or_else(|| command.map(String::from))
    .unwrap_or_else(|| "pane".to_string());
  let auto_send_on_idle = resolved_profile.as_ref().map(|p| {
    json!({ "enabled": p.auto_send_on_idle.enabled, "prompt": p.auto_send_on_idle.prompt, "delayMs": p.auto_send_on_idle.delay_ms })
  });
  let geometry_rect = geometry_plan.rect.map(|(x, y, w, h)| json!({ "x": x, "y": y, "w": w, "h": h }));
  // spec: profile-schema's AmmSettingsDialog等、プロファイル固有設定を
  // 引き続き参照するpaneはこのcanonical profile nameを使う。quickPrompts
  // 自体はアプリ全体設定へ変更済み(ユーザー要望2026-08-04)でこの値には
  // もう依存しない。
  let profile_name_for_pane = resolved_profile.as_ref().map(|p| p.name.clone());
  // spec: profile-schema's fontSize - the profile's own default initial
  // terminal font size (None -> frontend's own 13px default). A live
  // per-session change from the pane's own right-click menu is unrelated
  // and doesn't feed back into the profile.
  let font_size = resolved_profile.as_ref().and_then(|p| p.font_size);
  // spec: profile-schema's sendLineByLine - captured at open time like
  // autoSendOnIdle above so sendToPane can look it up per pane.
  let send_line_by_line = resolved_profile.as_ref().map(|p| p.send_line_by_line).unwrap_or(false);
  // spec: profile-schema's titleBarColor - purely a rendering hint, applied
  // once at pane creation the same way fontSize is. Falls back to a
  // command-type default when the profile itself has none set (see
  // default_title_bar_color_for_type's own doc comment).
  let title_bar_color = resolved_profile.as_ref().and_then(|p| {
    p.title_bar_color.clone().or_else(|| profile::default_title_bar_color_for_type(&p.command_type).map(String::from))
  });
  // spec: pane-management - フロントエンドの「名前変更」ボタンがNickName
  // (MCP送信先名)をrename_pane_nickname経由で変更する際、対象のnicknameが
  // 空文字列(command直接指定のad-hocペイン)だと空文字列宛にrename APIを
  // 呼んでしまうため、そのケースはnullとして「NickName変更不可」を明示する。
  let nickname_for_pane = (!nickname.is_empty()).then_some(nickname);
  let _ = ctx.app.emit(
    "amm-pane-opened",
    json!({
      "paneId": pane_id, "label": label, "autoSendOnIdle": auto_send_on_idle,
      "geometry": geometry_rect, "maximized": geometry_plan.maximized, "profileName": profile_name_for_pane,
      "fontSize": font_size, "sendLineByLine": send_line_by_line,
      "titleBarColor": title_bar_color, "nickname": nickname_for_pane,
    }),
  );

  json!({ "session_id": pane_id })
}

// spec: pane-management's new "コマンドメニューによるプロファイルのペイン起動"
// requirement - a GUI entry point that launches a profiles.amm-defined
// profile as a pane, without going through an external MCP/PowerShell
// client. Reuses open_pane's exact resolution/spawn/participant-registration/
// event-emission path (same as pane/open with profile_name) rather than
// duplicating that logic, so a GUI-launched pane behaves identically to an
// MCP-launched one.
pub async fn open_pane_for_gui(
  app: &AppHandle,
  mcp: &McpState,
  profile_name: &str,
  working_directory: Option<String>,
) -> Result<String, String> {
  let ctx = Ctx { app, mcp };
  let mut args = json!({ "profile_name": profile_name });
  // spec: 旧.NET版のSelectWorkingDirOnStart(「コマンド」メニューからの起動時に
  // 毎回作業ディレクトリを尋ねる) - open_pane's既存の"workingDirectory"引数は
  // geometry_plan/profile自身のworking_directoryより優先されるので、ここに
  // 差し込むだけでこのインスタンスだけの上書きになる(プロファイル自体の設定は
  // 変更しない)。呼び出し元(open_profiles_fileコマンドの隣、lib.rs側)がダイ
  // アログ表示の是非を判断し、選ばれたパスだけをここへ渡す。
  if let Some(dir) = working_directory {
    args["workingDirectory"] = json!(dir);
  }
  let result = open_pane(&ctx, &args).await;
  match result.get("session_id").and_then(|v| v.as_str()) {
    Some(id) => Ok(id.to_string()),
    None => Err(result.get("error").and_then(|v| v.as_str()).unwrap_or("unknown error").to_string()),
  }
}

async fn close_pane(ctx: &Ctx<'_>, session_id: &str, force: bool) -> Value {
  let state = ctx.app.state::<crate::PtyState>();
  if !state.contains(session_id) {
    return json!({ "error": format!("session not found: {session_id}") });
  }
  // Tear down the pty here rather than only asking the frontend to do it -
  // otherwise a second close_pane call racing the frontend's async event
  // handling can still see the pane as "found" (confirmed by testing).
  // NOTE: force=false does not yet block on the UI's running-session
  // confirmation dialog the way the WinForms host's synchronous CloseWindow
  // did - that needs a proper async request/response bridge to the
  // frontend, deferred (see retro-pending.md).
  state.remove(session_id);
  let _ = ctx.app.emit("amm-pane-close-requested", json!({ "paneId": session_id, "force": force }));
  ctx.mcp.unregister_participant(session_id).await;
  // spec: approval-hub "ペイン単位の一括解放" (close trigger). Activation-
  // triggered release is a frontend concern, not wired yet (deferred).
  ctx.app.state::<crate::approval::ApprovalBroker>().release_by_token(session_id).await;
  json!({ "success": true })
}

async fn dispatch_tool_call(ctx: &Ctx<'_>, id: &Value, name: &str, args: &Value) -> Value {
  match name {
    "send_message" => {
      let recipient = args.get("recipient").and_then(|v| v.as_str());
      let mode = args.get("mode").and_then(|v| v.as_str()).unwrap_or("first");
      let Some(message) = args.get("message").and_then(|v| v.as_str()) else {
        return make_error(id, -32602, "message is required");
      };
      let participants = ctx.mcp.list_participants().await;
      let targets = McpState::resolve_targets(&participants, recipient, mode);
      let mut delivered = 0u32;
      let mut queued = 0u32;
      let mut recipients: Vec<String> = Vec::new();
      for t in &targets {
        if !recipients.iter().any(|r| r.eq_ignore_ascii_case(&t.nickname)) {
          recipients.push(t.nickname.clone());
        }
        if t.is_waiting {
          let _ = ctx.app.emit("amm-inject-message", json!({ "sessionId": t.session_id, "message": message }));
          delivered += 1;
        } else {
          ctx.mcp.enqueue(&t.nickname, message).await;
          queued += 1;
        }
      }
      make_tool_result(id, json!({ "delivered_count": delivered, "queued_count": queued, "recipients": recipients }))
    }
    "list_participants" => {
      let participants = ctx.mcp.list_participants().await;
      make_tool_result(id, json!({ "participants": participants }))
    }
    "peek_queue" => {
      let recipient = args.get("recipient").and_then(|v| v.as_str());
      let snap = ctx.mcp.peek_queue(recipient).await;
      let queues: Vec<Value> = snap
        .into_iter()
        .map(|(nickname, messages)| json!({ "nickname": nickname, "messages": messages }))
        .collect();
      make_tool_result(id, json!({ "queues": queues }))
    }
    "pane/open" => make_tool_result(id, open_pane(ctx, args).await),
    "pane/close" => {
      let Some(session_id) = args.get("session_id").and_then(|v| v.as_str()) else {
        return make_error(id, -32602, "session_id is required");
      };
      let force = args.get("force").and_then(|v| v.as_bool()).unwrap_or(false);
      make_tool_result(id, close_pane(ctx, session_id, force).await)
    }
    _ => make_error(id, -32601, &format!("Unknown tool: {name}")),
  }
}

// spec: approval-hub's 4th release trigger (pipe disconnect). Races the
// pending approval against a non-destructive peek-read on this same
// connection - fill_buf() returns an empty slice only on real EOF, and
// never consumes what it sees, so an unexpected pipelined line (not
// expected from any real client, which all wait for one response before
// sending the next) is left intact for the normal read loop to pick up
// once the approval resolves. On genuine disconnect the request future is
// *not* dropped: it's explicitly released via `release_by_token` (so
// `ApprovalBroker::resolve`'s immediate pending-map removal can't race
// against `request()`'s own cleanup) and then awaited to completion like
// any other resolution path. Free-standing (no `Ctx`/`AppHandle`) so it's
// unit-testable against a real `ApprovalBroker` + `tokio::io::duplex` pair
// without needing a mock Tauri app.
async fn await_approval_or_disconnect<R: tokio::io::AsyncBufRead + Unpin>(
  broker: &crate::approval::ApprovalBroker,
  token: &str,
  reader: &mut R,
  tool_name: &str,
  tool_input: &str,
) -> Option<String> {
  let request_fut = broker.request(token, tool_name, tool_input, crate::approval::default_timeout_ms());
  tokio::pin!(request_fut);
  loop {
    tokio::select! {
      // biased: request_fut must always be polled (and thus register its
      // pending entry in the broker) before the peek branch is even
      // considered. Without this, tokio::select!'s default random branch
      // order can resolve peek's already-buffered EOF (the reader-already-
      // at-EOF-before-the-request-starts case, e.g. a client that
      // disconnects before amm even begins handling its request) without
      // request_fut ever having been polled once - release_by_token then
      // finds no matching entry (nothing registered yet) and is a no-op,
      // and the subsequent `request_fut.await` falls through to waiting
      // out the full 45s internal timeout instead of resolving promptly.
      // Found via a real (non-flaky, 100% reproducible) test failure
      // while running the suite on macOS for the first time.
      biased;
      d = &mut request_fut => break d,
      peek = reader.fill_buf() => {
        match peek {
          Ok(buf) if buf.is_empty() => {
            broker.release_by_token(token).await;
            break request_fut.await;
          }
          _ => continue,
        }
      }
    }
  }
}

async fn handle_line<R: tokio::io::AsyncBufRead + Unpin>(ctx: &Ctx<'_>, reader: &mut R, line: &str) -> Option<String> {
  let req: Value = match serde_json::from_str(line) {
    Ok(v) => v,
    Err(e) => return Some(make_error(&Value::Null, -32700, &format!("Parse error: {e}")).to_string()),
  };
  if !req.is_object() {
    return Some(make_error(&Value::Null, -32600, "Invalid Request").to_string());
  }
  let id = req.get("id").cloned().unwrap_or(Value::Null);
  let has_id = req.get("id").is_some();
  let method = req.get("method").and_then(|v| v.as_str());
  let Some(method) = method else {
    return Some(make_error(&id, -32600, "Missing method").to_string());
  };
  let params = req.get("params").cloned().unwrap_or(json!({}));

  let response = match method {
    "initialize" => Some(make_result(
      &id,
      json!({
        "protocolVersion": PROTOCOL_VERSION,
        "capabilities": { "tools": {} },
        "serverInfo": { "name": SERVER_NAME, "version": SERVER_VERSION },
      }),
    )),
    "initialized" | "notifications/initialized" => None,
    "ping" => Some(make_result(&id, json!({}))),
    "tools/list" => {
      // spec: mcp-gateway's "ツールの集約と名前空間プレフィックス" - appended
      // after the built-in tools, matching McpPipeServer.cs's ordering.
      let mut tools = tools_list_result();
      let gateway = ctx.app.state::<crate::gateway::GatewayManager>();
      if let Value::Array(arr) = &mut tools {
        arr.extend(gateway.aggregated_tools().await);
      }
      Some(make_result(&id, json!({ "tools": tools })))
    }
    "tools/call" => {
      let name = params.get("name").and_then(|v| v.as_str());
      let args = params.get("arguments").cloned().unwrap_or(json!({}));
      match name {
        Some("pane/wait_state") => {
          let Some(session_id) = args.get("session_id").and_then(|v| v.as_str()) else {
            return Some(make_error(&id, -32602, "session_id is required").to_string());
          };
          let Some(target_state) = args.get("target_state").and_then(|v| v.as_str()) else {
            return Some(make_error(&id, -32602, "target_state is required").to_string());
          };
          let timeout_ms = args.get("timeout_ms").and_then(|v| v.as_u64()).unwrap_or(DEFAULT_TIMEOUT_MS);
          let (state, elapsed_ms) = ctx.mcp.register_wait(session_id, target_state, timeout_ms).await;
          Some(make_tool_result(&id, json!({ "state": state, "elapsed_ms": elapsed_ms })))
        }
        // spec: mcp-gateway's "ゲートウェイツール呼び出しの転送" - checked
        // before the built-in dispatcher, matching McpPipeServer.cs's
        // TryHandleGatewayToolAsync priority (called ahead of HandleLine).
        Some(name) if ctx.app.state::<crate::gateway::GatewayManager>().is_gateway_tool(name) => {
          let gateway = ctx.app.state::<crate::gateway::GatewayManager>();
          Some(match gateway.call_tool(name, args).await {
            None => make_error(&id, -32603, &format!("Gateway server for '{name}' is not running")),
            Some(v) if v.get("error").is_some() => make_error(&id, -32603, &v["error"].to_string()),
            Some(v) => make_result(&id, v),
          })
        }
        Some(name) => Some(dispatch_tool_call(ctx, &id, name, &args).await),
        None => Some(make_error(&id, -32602, "Invalid params")),
      }
    }
    "amm/notify" => {
      let token = params.get("token").and_then(|v| v.as_str());
      let state = params.get("state").and_then(|v| v.as_str());
      match (token, state) {
        (Some(token), Some(state)) => {
          // No hook-cli-assigned distinct tokens yet (phase 5.5) - token ==
          // session_id (== pane_id) in the meantime.
          // "matched" reflects whether this token corresponds to a known
          // pane at all (mirrors IMcpHost.NotifyChildState), not whether
          // that pane happens to have a registered nickname.
          let pty_state = ctx.app.state::<crate::PtyState>();
          let matched = pty_state.contains(token);
          // Drives the real WaitPatternDetector's forced transition (spec:
          // wait-detection) - emits amm-pane-wait-state itself if the state
          // actually changed, in addition to resolving pane/wait_state below.
          if let Some(changed) = pty_state.force_state(token, state) {
            if changed {
              if let Some((new_state, has_attention)) = pty_state.wait_state(token) {
                let _ = ctx.app.emit(
                  "amm-pane-wait-state",
                  json!({ "paneId": token, "state": new_state, "hasAttention": has_attention }),
                );
              }
            }
          }
          ctx.mcp.resolve_by_session(token, state).await;
          if !has_id {
            None
          } else {
            Some(make_result(&id, json!({ "matched": matched })))
          }
        }
        _ => {
          if !has_id {
            None
          } else {
            Some(make_error(&id, -32602, "token and state are required"))
          }
        }
      }
    }
    "amm/approval" => {
      let token = params.get("token").and_then(|v| v.as_str());
      let tool_name = params.get("toolName").and_then(|v| v.as_str());
      match (token, tool_name) {
        (Some(token), Some(tool_name)) => {
          let tool_input = params.get("toolInput").map(|v| v.to_string()).unwrap_or_else(|| "{}".to_string());
          let broker = ctx.app.state::<crate::approval::ApprovalBroker>();
          let _ = ctx.app.emit("amm-approval-requested", json!({ "token": token }));
          // Level 1 "waiting for input" transitions already toast via
          // maybeNotify/show_attention_notification; Level 2 approval
          // requests previously had no OS-level signal at all when amm
          // wasn't foreground, only the in-window amm-approval-requested
          // overlay above. Must fire before the potentially long
          // await_approval_or_disconnect wait below, not after.
          native_ui::notify_with_activation(
            ctx.app.clone(),
            token.to_string(),
            "amm: 承認待ち".to_string(),
            format!("{tool_name} の実行に承認が必要です"),
          );
          let decision = await_approval_or_disconnect(&broker, token, reader, tool_name, &tool_input).await;
          if !has_id {
            None
          } else {
            Some(make_result(&id, json!({ "decision": decision })))
          }
        }
        _ => {
          if !has_id {
            None
          } else {
            Some(make_error(&id, -32602, "token and toolName are required"))
          }
        }
      }
    }
    "amm.openPane" => {
      let args = params.clone();
      Some(match open_pane(ctx, &args).await {
        v if v.get("error").is_some() => make_error(&id, -32603, v["error"].as_str().unwrap_or("error")),
        v => make_result(&id, v),
      })
    }
    "amm.closePane" => {
      let Some(session_id) = params.get("session_id").and_then(|v| v.as_str()) else {
        return Some(make_error(&id, -32602, "session_id is required").to_string());
      };
      let force = params.get("force").and_then(|v| v.as_bool()).unwrap_or(false);
      Some(match close_pane(ctx, session_id, force).await {
        v if v.get("error").is_some() => make_error(&id, -32602, v["error"].as_str().unwrap_or("error")),
        v => make_result(&id, v),
      })
    }
    "amm.waitState" => {
      let Some(session_id) = params.get("session_id").and_then(|v| v.as_str()) else {
        return Some(make_error(&id, -32602, "session_id is required").to_string());
      };
      let Some(target_state) = params.get("target_state").and_then(|v| v.as_str()) else {
        return Some(make_error(&id, -32602, "target_state is required").to_string());
      };
      let timeout_ms = params.get("timeout_ms").and_then(|v| v.as_u64()).unwrap_or(DEFAULT_TIMEOUT_MS);
      let (state, elapsed_ms) = ctx.mcp.register_wait(session_id, target_state, timeout_ms).await;
      Some(make_result(&id, json!({ "state": state, "elapsed_ms": elapsed_ms })))
    }
    _ => {
      if !has_id {
        None
      } else {
        Some(make_error(&id, -32601, &format!("Method not found: {method}")))
      }
    }
  };
  response.map(|v| v.to_string())
}

// Generic over the transport (Windows NamedPipeServer / Unix UnixStream) -
// both are plain AsyncRead+AsyncWrite, and this function only ever uses
// tokio::io::split + BufReader + AsyncReadExt/AsyncWriteExt on the halves,
// so one implementation serves every platform's accept loop.
async fn handle_connection<S: tokio::io::AsyncRead + tokio::io::AsyncWrite + Unpin>(pipe: S, app: AppHandle) {
  let (read_half, mut write_half) = tokio::io::split(pipe);
  let mut reader = BufReader::new(read_half);
  let mcp_state = app.state::<McpState>();
  let ctx = Ctx { app: &app, mcp: mcp_state.inner() };

  let mut line = String::new();
  loop {
    line.clear();
    let n = match read_line_bounded(&mut reader, &mut line).await {
      Ok(n) => n,
      Err(_) => break,
    };
    if n == 0 {
      break;
    }
    let trimmed = line.trim();
    if trimmed.is_empty() {
      continue;
    }
    if let Some(response) = handle_line(&ctx, &mut reader, trimmed).await {
      if write_half.write_all(response.as_bytes()).await.is_err() {
        break;
      }
      if write_half.write_all(b"\n").await.is_err() {
        break;
      }
    }
  }
}

// Like AsyncBufReadExt::read_line, but aborts a connection whose line
// exceeds MAX_LINE_BYTES instead of buffering it unboundedly (memory-DoS
// guard, matching the .NET server's ReadLineBoundedAsync).
//
// correctness: code-review 2026-07-26 finding (mcp.rs UTF-8 corruption).
// This previously pushed each raw byte to `out` via `b as char`, which
// treats every byte as an independent Latin-1-ish codepoint instead of
// decoding UTF-8 - any multibyte character (e.g. Japanese text in a
// send_message/tool_input payload) came out corrupted on the receiving
// (GUI) side, even though it round-tripped fine byte-for-byte on write
// (write_half.write_all sends raw bytes, unaffected). Fixed by buffering
// raw content bytes and decoding the whole line as UTF-8 once, instead of
// char-per-byte. Uses lossy decoding (replacement char for invalid
// sequences) rather than erroring, since a malformed line from a
// misbehaving client should not be able to abort the connection any more
// than it already could before this fix.
async fn read_line_bounded<R: tokio::io::AsyncBufRead + Unpin>(reader: &mut R, out: &mut String) -> std::io::Result<usize> {
  use tokio::io::AsyncReadExt;
  let mut byte = [0u8; 1];
  let mut raw_count = 0usize;
  let mut buf: Vec<u8> = Vec::new();
  loop {
    let n = reader.read(&mut byte).await?;
    if n == 0 {
      out.push_str(&String::from_utf8_lossy(&buf));
      return Ok(raw_count);
    }
    raw_count += 1;
    if raw_count > MAX_LINE_BYTES {
      return Err(std::io::Error::new(std::io::ErrorKind::InvalidData, "line too long"));
    }
    match byte[0] {
      b'\n' => {
        out.push_str(&String::from_utf8_lossy(&buf));
        return Ok(raw_count);
      }
      b'\r' => continue,
      b => buf.push(b),
    }
  }
}

#[cfg(test)]
mod read_line_bounded_tests {
  use super::*;

  #[tokio::test]
  async fn decodes_multibyte_utf8_correctly() {
    // correctness: regression test for code-review 2026-07-26 finding
    // (mcp.rs UTF-8 corruption) - a line containing Japanese text must
    // round-trip exactly, not come out as mojibake from per-byte `as char`.
    let (a, mut b) = tokio::io::duplex(256);
    let mut reader = BufReader::new(a);
    let payload = "\u{65e5}\u{672c}\u{8a9e}メッセージ\n"; // "日本語メッセージ\n"
    tokio::io::AsyncWriteExt::write_all(&mut b, payload.as_bytes()).await.unwrap();

    let mut out = String::new();
    let n = read_line_bounded(&mut reader, &mut out).await.unwrap();

    assert_eq!(out, "\u{65e5}\u{672c}\u{8a9e}メッセージ");
    assert_eq!(n, payload.len(), "raw byte count must reflect the actual multibyte content, not char count");
  }

  #[tokio::test]
  async fn strips_cr_and_stops_at_lf_with_multibyte_content() {
    let (a, mut b) = tokio::io::duplex(256);
    let mut reader = BufReader::new(a);
    tokio::io::AsyncWriteExt::write_all(&mut b, "こんにちは\r\nnext-line-untouched".as_bytes()).await.unwrap();

    let mut out = String::new();
    read_line_bounded(&mut reader, &mut out).await.unwrap();
    assert_eq!(out, "こんにちは");
  }
}

// spec: mcp-server's Named Pipe ACL requirement (tasks.md 4.1). Previously
// skipped over "Win32 FFI risk" concerns (see tasks/retro-pending.md and
// pending-real-machine-verification.md); implemented here 2026-07-21 via
// an SDDL string fed through ConvertStringSecurityDescriptorToSecurityDescriptorW
// rather than manually building the ACL/DACL byte structures
// (InitializeAcl/AddAccessAllowedAce/SetSecurityDescriptorDacl) - far
// fewer raw structures to get wrong, and a malformed SDDL string fails
// loudly at conversion time instead of silently producing a bad ACL.
// Mirrors McpPipeServer.cs's PipeSecurity(current user, FullControl,
// Allow) - "D:(A;;GA;;;<sid>)" is a DACL with exactly one ACE (Allow,
// Generic-All, that SID); nothing else is granted access at all, same as
// PipeSecurity.SetAccessRule replacing the whole DACL with a single rule.
#[cfg(windows)]
struct PipeSecurityAttrs(windows::Win32::Security::SECURITY_ATTRIBUTES);
// Safety: only ever read (not mutated) from the accept-loop task after
// construction; the SECURITY_DESCRIPTOR it points to is intentionally
// never freed (LocalAlloc'd once, kept alive for the server's whole
// process lifetime - the same tradeoff CreateNamedPipe callers commonly
// make for a descriptor that must outlive every pipe instance created
// from it).
#[cfg(windows)]
unsafe impl Send for PipeSecurityAttrs {}

#[cfg(windows)]
fn current_user_sid_string() -> windows::core::Result<String> {
  use windows::Win32::Foundation::{CloseHandle, LocalFree, HANDLE, HLOCAL};
  use windows::Win32::Security::Authorization::ConvertSidToStringSidW;
  use windows::Win32::Security::{GetTokenInformation, TokenUser, TOKEN_QUERY, TOKEN_USER};
  use windows::Win32::System::Threading::{GetCurrentProcess, OpenProcessToken};
  unsafe {
    let mut token = HANDLE::default();
    OpenProcessToken(GetCurrentProcess(), TOKEN_QUERY, &mut token)?;
    let mut len = 0u32;
    // First call deliberately undersized (tokeninformation: None) just to
    // learn the required buffer size; its own "buffer too small" failure
    // is expected and ignored.
    let _ = GetTokenInformation(token, TokenUser, None, 0, &mut len);
    let mut buf = vec![0u8; len as usize];
    let info_result = GetTokenInformation(token, TokenUser, Some(buf.as_mut_ptr() as *mut _), len, &mut len);
    let _ = CloseHandle(token);
    info_result?;
    let token_user = &*(buf.as_ptr() as *const TOKEN_USER);
    let mut sid_str = windows::core::PWSTR::null();
    ConvertSidToStringSidW(token_user.User.Sid, &mut sid_str)?;
    let result = sid_str.to_string();
    let _ = LocalFree(Some(HLOCAL(sid_str.0 as *mut core::ffi::c_void)));
    result.map_err(|_| windows::core::Error::from(windows::Win32::Foundation::E_FAIL))
  }
}

#[cfg(windows)]
fn build_current_user_only_security_attributes() -> windows::core::Result<PipeSecurityAttrs> {
  use windows::Win32::Security::Authorization::ConvertStringSecurityDescriptorToSecurityDescriptorW;
  use windows::Win32::Security::{PSECURITY_DESCRIPTOR, SECURITY_ATTRIBUTES};
  use windows::core::PCWSTR;

  let sid = current_user_sid_string()?;
  let sddl = format!("D:(A;;GA;;;{sid})");
  let sddl_w: Vec<u16> = sddl.encode_utf16().chain(std::iter::once(0)).collect();
  let mut psd = PSECURITY_DESCRIPTOR::default();
  unsafe {
    ConvertStringSecurityDescriptorToSecurityDescriptorW(PCWSTR(sddl_w.as_ptr()), 1, &mut psd, None)?;
  }
  Ok(PipeSecurityAttrs(SECURITY_ATTRIBUTES {
    nLength: std::mem::size_of::<SECURITY_ATTRIBUTES>() as u32,
    lpSecurityDescriptor: psd.0,
    bInheritHandle: windows::core::BOOL(0),
  }))
}

#[cfg(windows)]
pub fn spawn_server(app: AppHandle) {
  let pipe_name = format!(r"\\.\pipe\amm-mcp-{}", std::env::var("USERNAME").unwrap_or_else(|_| "unknown".into()));
  tauri::async_runtime::spawn(async move {
    let sec_attrs = match build_current_user_only_security_attributes() {
      Ok(attrs) => Some(attrs),
      Err(e) => {
        // Falls back to the (pre-existing, already-shipped) default pipe
        // security rather than refusing to start the whole MCP server over
        // an ACL we can't build - matches this port's general "degrade,
        // don't crash the app" posture elsewhere (e.g. tray icon install
        // failures only log, per lib.rs).
        eprintln!("[mcp] failed to build restricted pipe security attributes, falling back to default pipe security: {e}");
        None
      }
    };
    // spec: `create_with_security_attributes_raw`'s `attrs` param mirrors
    // CreateFile's lpSecurityAttributes: null keeps the default (unrestricted)
    // pipe security, matching this fallback exactly.
    macro_rules! create_pipe {
      ($first:expr) => {{
        let opts = ServerOptions::new().first_pipe_instance($first).clone();
        match &sec_attrs {
          Some(attrs) => unsafe {
            opts.create_with_security_attributes_raw(&pipe_name, &attrs.0 as *const _ as *mut std::ffi::c_void)
          },
          None => opts.create(&pipe_name),
        }
      }};
    }

    // First instance is created before the loop so it's already listening
    // when the loop body creates the *next* instance up front, immediately
    // after a client attaches - avoids a window where no instance is
    // listening between one client's connect and the next create().
    let mut next_server = match create_pipe!(true) {
      Ok(s) => s,
      Err(e) => {
        eprintln!("[mcp] failed to create first named pipe instance {pipe_name}: {e}");
        return;
      }
    };
    loop {
      eprintln!("[mcp] listening for a connection on {pipe_name}");
      if let Err(e) = next_server.connect().await {
        eprintln!("[mcp] pipe connect error: {e}");
        tokio::time::sleep(std::time::Duration::from_millis(500)).await;
        continue;
      }
      eprintln!("[mcp] client connected");
      let connected = next_server;
      next_server = match create_pipe!(false) {
        Ok(s) => s,
        Err(e) => {
          eprintln!("[mcp] failed to create next named pipe instance: {e}");
          break;
        }
      };
      let app_clone = app.clone();
      tauri::async_runtime::spawn(async move {
        handle_connection(connected, app_clone).await;
        eprintln!("[mcp] connection handler finished");
      });
    }
  });
}

// spec: mcp-server's IPC transport requirement, macOS/Unix delta
// (openspec/changes/add-macos-support/specs/mcp-server/spec.md). Unix
// domain sockets don't have Named Pipe's "one instance per connection"
// model - a single bound listener already queues and serves multiple
// concurrent connections - so this accept loop is simpler than the
// Windows one above (no next-instance-before-handoff dance needed).
#[cfg(unix)]
fn unix_socket_dir() -> std::path::PathBuf {
  // uid-scoped dir (not just the socket file) so the 0700 permission wall
  // covers path traversal too, not only the socket file's own mode.
  let uid = unsafe { libc::getuid() };
  std::env::temp_dir().join(format!("amm-mcp-{uid}"))
}

#[cfg(unix)]
fn unix_socket_path() -> std::path::PathBuf {
  unix_socket_dir().join("mcp.sock")
}

// Split out of spawn_server so unix_socket_tests can exercise the exact
// same dir/permission/bind/stale-cleanup logic the real server uses,
// rather than a reimplementation that could silently drift from it.
#[cfg(unix)]
fn bind_unix_socket(dir: &std::path::Path, sock_path: &std::path::Path) -> std::io::Result<tokio::net::UnixListener> {
  use std::os::unix::fs::PermissionsExt;

  std::fs::create_dir_all(dir)?;
  std::fs::set_permissions(dir, std::fs::Permissions::from_mode(0o700))?;

  // Stale socket file from a prior crashed instance - bind() fails with
  // AddrInUse otherwise, even though nothing is actually listening.
  let _ = std::fs::remove_file(sock_path);

  let listener = tokio::net::UnixListener::bind(sock_path)?;
  std::fs::set_permissions(sock_path, std::fs::Permissions::from_mode(0o600))?;
  Ok(listener)
}

#[cfg(unix)]
pub fn spawn_server(app: AppHandle) {
  tauri::async_runtime::spawn(async move {
    let dir = unix_socket_dir();
    let sock_path = unix_socket_path();
    let listener = match bind_unix_socket(&dir, &sock_path) {
      Ok(l) => l,
      Err(e) => {
        eprintln!("[mcp] failed to bind unix socket {}: {e}", sock_path.display());
        return;
      }
    };

    loop {
      eprintln!("[mcp] listening for a connection on {}", sock_path.display());
      let stream = match listener.accept().await {
        Ok((stream, _addr)) => stream,
        Err(e) => {
          eprintln!("[mcp] unix socket accept error: {e}");
          tokio::time::sleep(std::time::Duration::from_millis(500)).await;
          continue;
        }
      };
      eprintln!("[mcp] client connected");
      let app_clone = app.clone();
      tauri::async_runtime::spawn(async move {
        handle_connection(stream, app_clone).await;
        eprintln!("[mcp] connection handler finished");
      });
    }
  });
}

// spec: approval-hub's 4th release trigger (pipe disconnect, tasks.md
// 5.10's remaining gap). Exercises await_approval_or_disconnect directly
// against a real ApprovalBroker + tokio::io::duplex pair - no Ctx/AppHandle
// needed since this crate has no mock-Tauri-app test infra set up, and the
// disconnect-detection mechanism itself doesn't touch either.
#[cfg(test)]
mod approval_disconnect_tests {
  use super::*;
  use crate::approval::ApprovalBroker;

  #[tokio::test]
  async fn releases_promptly_when_reader_already_at_eof() {
    let broker = ApprovalBroker::default();
    let (a, b) = tokio::io::duplex(64);
    drop(b); // client already gone before the approval is even requested
    let mut reader = BufReader::new(a);

    let start = std::time::Instant::now();
    let decision = await_approval_or_disconnect(&broker, "tok-eof", &mut reader, "Bash", "{}").await;

    assert_eq!(decision, None, "disconnect must resolve as a release, not a real decision");
    assert!(
      start.elapsed() < std::time::Duration::from_secs(30),
      "must release immediately on disconnect, not wait out the 45s default timeout"
    );
    assert!(broker.list().await.is_empty(), "pending entry must be removed, not just resolved-but-leaked");
  }

  #[tokio::test]
  async fn releases_promptly_when_client_drops_mid_wait() {
    let broker = ApprovalBroker::default();
    let (a, b) = tokio::io::duplex(64);
    let mut reader = BufReader::new(a);

    let dropper = async {
      tokio::time::sleep(std::time::Duration::from_millis(50)).await;
      drop(b);
    };
    let start = std::time::Instant::now();
    let (decision, _) =
      tokio::join!(await_approval_or_disconnect(&broker, "tok-mid", &mut reader, "Bash", "{}"), dropper);

    assert_eq!(decision, None);
    assert!(start.elapsed() < std::time::Duration::from_secs(30));
    assert!(broker.list().await.is_empty());
  }

  #[tokio::test]
  async fn resolves_normally_when_answered_before_any_disconnect() {
    let broker = ApprovalBroker::default();
    let (a, _b) = tokio::io::duplex(64); // kept alive: fill_buf() must never see EOF here
    let mut reader = BufReader::new(a);

    let responder = async {
      loop {
        if let Some(entry) = broker.list().await.into_iter().next() {
          assert!(broker.resolve(&entry.id, Some("allow".to_string())).await);
          break;
        }
        tokio::time::sleep(std::time::Duration::from_millis(5)).await;
      }
    };
    let (decision, _) =
      tokio::join!(await_approval_or_disconnect(&broker, "tok-normal", &mut reader, "Bash", "{}"), responder);

    assert_eq!(decision, Some("allow".to_string()));
    assert!(broker.list().await.is_empty());
  }
}

// spec: mcp-server's Named Pipe ACL (tasks.md 4.1). Verifies the actual
// DACL applied to a real pipe instance, not just that the SDDL-building
// helpers return Ok - reading it back through the same `windows` crate
// (via GetSecurityInfo on the live pipe's raw handle) is far more
// trustworthy than trying to inspect it externally (tried via PowerShell
// P/Invoke first: CreateFileW+GetSecurityInfo hit repeated .NET
// Framework 5.1 marshalling friction with GENERIC_READ/READ_CONTROL
// access masks and ultimately ERROR_PATH_NOT_FOUND on a plain
// READ_CONTROL-only open - inconclusive, abandoned in favor of this).
#[cfg(all(test, windows))]
mod acl_tests {
  use super::*;
  use windows::Win32::Security::Authorization::{GetSecurityInfo, SE_KERNEL_OBJECT};
  use windows::Win32::Security::{
    GetAce, GetAclInformation, ACCESS_ALLOWED_ACE, ACL, ACL_SIZE_INFORMATION, AclSizeInformation, DACL_SECURITY_INFORMATION,
  };
  use windows::Win32::Foundation::HANDLE;
  use std::os::windows::io::AsRawHandle;

  #[test]
  fn current_user_sid_string_looks_like_a_sid() {
    let sid = current_user_sid_string().expect("must resolve the current process token's user SID");
    // Every real Windows account SID (local or domain) starts "S-1-5-21-"
    // (SECURITY_NT_AUTHORITY, non-unique domain/local-machine identifier)
    // - well-known SIDs (SYSTEM, Everyone, etc.) use other short prefixes
    // this process should never actually be running as.
    assert!(sid.starts_with("S-1-5-21-"), "unexpected SID shape: {sid}");
  }

  #[tokio::test]
  async fn pipe_dacl_grants_exactly_one_ace_to_the_current_user() {
    let attrs = build_current_user_only_security_attributes().expect("SDDL-based security attributes must build successfully");
    let pipe_name = format!(r"\\.\pipe\amm-acl-test-{}", std::process::id());
    let server = unsafe {
      ServerOptions::new()
        .first_pipe_instance(true)
        .create_with_security_attributes_raw(&pipe_name, &attrs.0 as *const _ as *mut std::ffi::c_void)
        .expect("creating a test pipe instance with the restricted security attributes must succeed")
    };

    let handle = HANDLE(server.as_raw_handle());
    let mut dacl_ptr: *mut ACL = std::ptr::null_mut();
    let mut sd = windows::Win32::Security::PSECURITY_DESCRIPTOR::default();
    unsafe {
      GetSecurityInfo(
        handle,
        SE_KERNEL_OBJECT, // named pipe handles are kernel objects, not filesystem SE_FILE_OBJECTs
        DACL_SECURITY_INFORMATION,
        None,
        None,
        Some(&mut dacl_ptr),
        None,
        Some(&mut sd),
      )
      .ok()
      .expect("GetSecurityInfo must succeed on our own just-created pipe handle");
    }
    assert!(!dacl_ptr.is_null(), "pipe must have an explicit DACL, not a null (unrestricted) one");

    let mut acl_size = ACL_SIZE_INFORMATION::default();
    unsafe {
      GetAclInformation(
        dacl_ptr,
        &mut acl_size as *mut _ as *mut std::ffi::c_void,
        std::mem::size_of::<ACL_SIZE_INFORMATION>() as u32,
        AclSizeInformation,
      )
      .expect("GetAclInformation must succeed on the pipe's DACL");
    }
    assert_eq!(acl_size.AceCount, 1, "expected exactly one ACE (current-user-only), found {}", acl_size.AceCount);

    let mut ace_ptr: *mut std::ffi::c_void = std::ptr::null_mut();
    unsafe {
      GetAce(dacl_ptr, 0, &mut ace_ptr).expect("GetAce(0) must succeed for the single ACE this DACL has");
    }
    let ace = unsafe { &*(ace_ptr as *const ACCESS_ALLOWED_ACE) };
    // ACCESS_ALLOWED_ACE_TYPE = 0
    assert_eq!(ace.Header.AceType, 0, "the sole ACE must be an Allow ACE, not Deny/other");
    let ace_sid_ptr = windows::Win32::Security::PSID((&ace.SidStart) as *const u32 as *mut std::ffi::c_void);
    let mut ace_sid_str = windows::core::PWSTR::null();
    unsafe {
      windows::Win32::Security::Authorization::ConvertSidToStringSidW(ace_sid_ptr, &mut ace_sid_str)
        .expect("the ACE's SID must itself be well-formed enough to convert back to a string");
    }
    let ace_sid = unsafe { ace_sid_str.to_string() }.expect("ACE SID string must be valid UTF-16");
    let expected_sid = current_user_sid_string().unwrap();
    assert_eq!(ace_sid, expected_sid, "the ACE must grant access to the current user's SID specifically, not some other principal");

    drop(server);
  }
}

// spec: mcp-server's macOS/Unix IPC transport delta
// (openspec/changes/add-macos-support/specs/mcp-server/spec.md). macOS/Unix
// counterpart of acl_tests above - exercises bind_unix_socket directly
// (the same function spawn_server calls) rather than reimplementing the
// bind/permission logic, so a regression there would fail these tests too.
#[cfg(all(test, unix))]
mod unix_socket_tests {
  use super::*;
  use std::os::unix::fs::PermissionsExt;

  fn unique_test_dir(label: &str) -> std::path::PathBuf {
    std::env::temp_dir().join(format!("amm-mcp-test-{label}-{}", std::process::id()))
  }

  #[tokio::test]
  async fn bind_creates_dir_0700_and_socket_0600() {
    let dir = unique_test_dir("perms");
    let sock_path = dir.join("mcp.sock");
    let _ = std::fs::remove_dir_all(&dir);

    let listener = bind_unix_socket(&dir, &sock_path).expect("bind_unix_socket must succeed against a fresh temp dir");

    let dir_mode = std::fs::metadata(&dir).unwrap().permissions().mode() & 0o777;
    assert_eq!(dir_mode, 0o700, "socket dir must be owner-only (0700), got {dir_mode:o}");
    let sock_mode = std::fs::metadata(&sock_path).unwrap().permissions().mode() & 0o777;
    assert_eq!(sock_mode, 0o600, "socket file must be owner-only (0600), got {sock_mode:o}");

    drop(listener);
    let _ = std::fs::remove_dir_all(&dir);
  }

  #[tokio::test]
  async fn bind_removes_a_stale_socket_file_from_a_prior_crashed_instance() {
    let dir = unique_test_dir("stale");
    let sock_path = dir.join("mcp.sock");
    let _ = std::fs::remove_dir_all(&dir);
    std::fs::create_dir_all(&dir).unwrap();
    // A plain file at the socket path (not an actual socket) simulates the
    // leftover after a crash - bind() would fail with AddrInUse-equivalent
    // if this isn't removed first.
    std::fs::write(&sock_path, b"not a socket").unwrap();

    let listener = bind_unix_socket(&dir, &sock_path)
      .expect("bind_unix_socket must clean up and succeed even when a non-socket file already occupies the path");

    drop(listener);
    let _ = std::fs::remove_dir_all(&dir);
  }

  #[tokio::test]
  async fn multiple_concurrent_connections_are_each_served_independently() {
    // spec scenario: "同一ユーザーの複数プロセスが同時接続する" - the Unix
    // counterpart of the Named Pipe multi-instance test, but simpler since
    // a single bound listener already queues concurrent connections (no
    // next-instance-before-handoff dance needed, per bind_unix_socket's
    // doc comment).
    let dir = unique_test_dir("concurrent");
    let sock_path = dir.join("mcp.sock");
    let _ = std::fs::remove_dir_all(&dir);
    let listener = bind_unix_socket(&dir, &sock_path).unwrap();

    let accept_task = tokio::spawn(async move {
      let mut lines = Vec::new();
      for _ in 0..2 {
        let (stream, _) = listener.accept().await.unwrap();
        let mut reader = BufReader::new(stream);
        let mut line = String::new();
        reader.read_line(&mut line).await.unwrap();
        lines.push(line);
      }
      lines
    });

    let mut client_a = tokio::net::UnixStream::connect(&sock_path).await.unwrap();
    let mut client_b = tokio::net::UnixStream::connect(&sock_path).await.unwrap();
    client_a.write_all(b"from-a\n").await.unwrap();
    client_b.write_all(b"from-b\n").await.unwrap();

    let mut lines = accept_task.await.unwrap();
    lines.sort();
    assert_eq!(lines, vec!["from-a\n".to_string(), "from-b\n".to_string()], "both concurrent connections must be accepted and readable independently");

    let _ = std::fs::remove_dir_all(&dir);
  }

  #[tokio::test]
  async fn oversized_line_is_disconnected_not_buffered_unboundedly() {
    // Exercises the same read_line_bounded used by handle_connection,
    // over a real Unix domain socket pair rather than an in-memory duplex
    // - confirms the MAX_LINE_BYTES guard (memory-DoS protection) applies
    // identically over this transport.
    let dir = unique_test_dir("oversized");
    let sock_path = dir.join("mcp.sock");
    let _ = std::fs::remove_dir_all(&dir);
    let listener = bind_unix_socket(&dir, &sock_path).unwrap();

    let server_task = tokio::spawn(async move {
      let (stream, _) = listener.accept().await.unwrap();
      let mut reader = BufReader::new(stream);
      let mut line = String::new();
      read_line_bounded(&mut reader, &mut line).await
    });

    let mut client = tokio::net::UnixStream::connect(&sock_path).await.unwrap();
    let oversized = vec![b'x'; MAX_LINE_BYTES + 1];
    client.write_all(&oversized).await.unwrap();
    client.write_all(b"\n").await.unwrap();

    let result = server_task.await.unwrap();
    assert!(result.is_err(), "a line exceeding MAX_LINE_BYTES must be rejected, not buffered unboundedly");

    let _ = std::fs::remove_dir_all(&dir);
  }
}
