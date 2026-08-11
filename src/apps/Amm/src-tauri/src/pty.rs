// PTY (ConPTY) pane lifecycle: spawn/write/resize/close, the reader
// thread (wait-detection feed-through), Job Object hard-kill-on-close
// protection, and the small per-pane accessor commands - split out of
// lib.rs (2026-07-26 architecture-bloat cleanup). No behavior change,
// pure move.
use portable_pty::{native_pty_system, CommandBuilder, MasterPty, PtySize};
use serde::Serialize;
use std::collections::HashMap;
use std::io::{Read, Write};
use std::sync::{Arc, Mutex};
use tauri::{AppHandle, Emitter, Manager, State};
use uuid::Uuid;
use crate::{mcp, profile, wait_detect};

// Native ConPTY integration via the `portable-pty` crate (no node-pty/Node.js
// involved), keyed by pane id so a single window can host multiple
// independent terminal sessions (spec: pane-management "単一ウィンドウ内ペイン構成").
struct PtyEntry {
  master: Box<dyn MasterPty + Send>,
  writer: Box<dyn Write + Send>,
  detector: Arc<Mutex<wait_detect::WaitPatternDetector>>,
  working_dir: std::path::PathBuf,
  // spec: profile-schema's windowGeometry - "同 profile の生存数+1" index
  // resolution needs to know which alive panes belong to which profile,
  // which nothing tracked before this (found missing in the phase 8.1
  // parity audit). None for ad-hoc/UI-created panes with no profile.
  profile_name: Option<String>,
  // security: code-review 2026-07-26 finding H-5. Previously the Child
  // returned by spawn_command() was discarded entirely (no handle, no PID
  // retained anywhere), so a pane's CLI agent process - and any
  // grandchildren it spawns (e.g. `npx`-launched MCP dev servers) - had no
  // way to be force-terminated: ConPTY's soft-close only works if the
  // child honors CTRL_CLOSE_EVENT-equivalent signaling, which detached or
  // separately-consoled grandchildren often don't. gateway.rs already does
  // this for externally-managed MCP server processes
  // (assign_kill_on_close_job); this mirrors that for the pane's own
  // process tree. None if job-object creation failed (logged, not fatal -
  // ConPTY's own close still runs as a fallback).
  job_handle: Option<isize>,
}

#[derive(Default)]
pub struct PtyState {
  panes: Mutex<HashMap<String, PtyEntry>>,
}

impl PtyState {
  pub(crate) fn alive_count_for_profile(&self, profile_name: &str) -> u32 {
    self.panes.lock().unwrap_or_else(|e| e.into_inner()).values().filter(|e| e.profile_name.as_deref() == Some(profile_name)).count() as u32
  }

  // spec: editor-integration - the temp-file name embeds the launching
  // profile's name (falls back to the bare pane id for ad-hoc/UI-created
  // panes with no profile, matching how PtyEntry itself already tolerates
  // profile_name: None elsewhere in this module).
  pub(crate) fn profile_name_for(&self, pane_id: &str) -> Option<String> {
    self.panes.lock().unwrap_or_else(|e| e.into_inner()).get(pane_id)?.profile_name.clone()
  }

  pub(crate) fn wait_state(&self, pane_id: &str) -> Option<(&'static str, bool)> {
    let detector = self.panes.lock().unwrap_or_else(|e| e.into_inner()).get(pane_id)?.detector.clone();
    let d = detector.lock().unwrap_or_else(|e| e.into_inner());
    Some((d.state.as_str(), d.has_attention))
  }

  // hook-driven forced transition (spec: wait-detection "hook駆動の外部状態通知").
  // Returns Some(changed) if the pane exists, None if not found.
  pub(crate) fn force_state(&self, pane_id: &str, target: &str) -> Option<bool> {
    let detector = {
      let panes = self.panes.lock().unwrap_or_else(|e| e.into_inner());
      panes.get(pane_id)?.detector.clone()
    };
    let changed = detector.lock().unwrap_or_else(|e| e.into_inner()).force_state(target);
    Some(changed)
  }
}

// spec: wait-detection's new "OSC 9 ターミナル通知による attention 検知"
// requirement - the frontend's xterm.js OSC9 handler calls this to drive the
// same force_state path amm/notify (mcp.rs) uses for hook-driven state
// notifications, so an OSC9-detected attention/idle/busy signal is
// indistinguishable from a hook-driven one downstream.
#[tauri::command]
pub(crate) fn notify_pane_state(pane_id: String, state: String, pty: State<PtyState>, app: AppHandle) {
  if let Some(changed) = pty.force_state(&pane_id, &state) {
    if changed {
      if let Some((new_state, has_attention)) = pty.wait_state(&pane_id) {
        let _ = app.emit("amm-pane-wait-state", serde_json::json!({ "paneId": pane_id, "state": new_state, "hasAttention": has_attention }));
      }
    }
  }
}

#[tauri::command]
pub(crate) fn get_pane_working_dir(pane_id: String, state: State<PtyState>) -> Option<String> {
  state.panes.lock().unwrap_or_else(|e| e.into_inner()).get(&pane_id).map(|e| e.working_dir.display().to_string())
}

// spec: 送信前のテキスト整形 - アプリ全体設定(profile::FormatSettingsFile、
// コマンドごとの設定から変更、ユーザー要望2026-08-04)を使い、\nで分割・
// フィルタ適用後に再結合する。適用範囲は呼び出し元(共通入力欄の送信経路
// のみ、see send-helpers.js)側で絞り込む - このコマンド自体は無条件に
// フィルタする。
#[tauri::command]
pub(crate) fn filter_text_for_send(text: String) -> String {
  let settings = profile::load_format_settings();
  let raw_lines: Vec<String> = text.split('\n').map(String::from).collect();
  profile::filter_lines_for_send(&raw_lines, settings.collapse_blank_lines, &settings.comment_prefixes).join("\n")
}

impl PtyState {
  pub(crate) fn contains(&self, pane_id: &str) -> bool {
    self.panes.lock().unwrap_or_else(|e| e.into_inner()).contains_key(pane_id)
  }

  pub(crate) fn remove(&self, pane_id: &str) {
    // Dropping the entry closes the writer/master handles, which tears down
    // the ConPTY and lets the reader thread's blocking read return Ok(0).
    let entry = self.panes.lock().unwrap_or_else(|e| e.into_inner()).remove(pane_id);
    // security: H-5 - closing the last handle to a
    // JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE job terminates the whole process
    // tree it was assigned to, as a forced backstop alongside ConPTY's
    // (best-effort, cooperative) soft close above.
    if let Some(entry) = entry {
      if let Some(handle) = entry.job_handle {
        close_job_handle(handle);
      }
    }
  }
}

#[cfg(windows)]
fn assign_kill_on_close_job(child: &dyn portable_pty::Child) -> Option<isize> {
  use windows::Win32::Foundation::{CloseHandle, HANDLE};
  use windows::Win32::System::JobObjects::{
    AssignProcessToJobObject, CreateJobObjectW, JobObjectExtendedLimitInformation, JOBOBJECT_EXTENDED_LIMIT_INFORMATION, JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE,
  };

  let raw_handle = child.as_raw_handle()?;
  unsafe {
    let job = CreateJobObjectW(None, windows::core::PCWSTR::null()).ok()?;
    let mut info = JOBOBJECT_EXTENDED_LIMIT_INFORMATION::default();
    info.BasicLimitInformation.LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE;
    let set_ok = windows::Win32::System::JobObjects::SetInformationJobObject(
      job,
      JobObjectExtendedLimitInformation,
      &info as *const _ as *const core::ffi::c_void,
      std::mem::size_of::<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>() as u32,
    );
    if set_ok.is_err() {
      let _ = CloseHandle(job);
      return None;
    }
    let process_handle = HANDLE(raw_handle as *mut core::ffi::c_void);
    if AssignProcessToJobObject(job, process_handle).is_err() {
      let _ = CloseHandle(job);
      return None;
    }
    Some(job.0 as isize)
  }
}

#[cfg(windows)]
fn close_job_handle(handle: isize) {
  use windows::Win32::Foundation::{CloseHandle, HANDLE};
  unsafe {
    let _ = CloseHandle(HANDLE(handle as *mut core::ffi::c_void));
  }
}

// macOS/Unix delta (design.md D3, openspec/changes/add-macos-support/):
// found while verifying editor-integration/pane-management on macOS that
// this pair was still the old "kept cfg-symmetric, does nothing"
// Windows-only stub (H-5's security fix - force-terminating a pane's
// process tree, e.g. a CLI agent that spawned an npx-launched MCP dev
// server - was silently absent on non-Windows). Unlike gateway.rs's
// counterpart, no explicit process_group(0) is needed here: a
// portable_pty-spawned child is already its own session/process-group
// leader on Unix (confirmed empirically - pgid == pid - since becoming a
// session leader is required for the pty's own job control to work at
// all), so assign_kill_on_close_job only needs to capture the pid.
#[cfg(unix)]
fn assign_kill_on_close_job(child: &dyn portable_pty::Child) -> Option<isize> {
  child.process_id().map(|pid| pid as isize)
}

#[cfg(unix)]
fn close_job_handle(handle: isize) {
  let pgid = handle as libc::pid_t;
  unsafe {
    libc::killpg(pgid, libc::SIGTERM);
  }
  // Escalate to SIGKILL after a grace period for stragglers that ignore
  // SIGTERM - mirrors gateway.rs's identical escalation and rationale.
  tauri::async_runtime::spawn(async move {
    tokio::time::sleep(std::time::Duration::from_millis(500)).await;
    unsafe {
      libc::killpg(pgid, libc::SIGKILL);
    }
  });
}

// True fallback for any target that is neither Windows nor Unix (kept
// cfg-symmetric so the module still type-checks there, though no such
// target is actually built in practice).
#[cfg(not(any(windows, unix)))]
fn assign_kill_on_close_job(_child: &dyn portable_pty::Child) -> Option<isize> {
  None
}
#[cfg(not(any(windows, unix)))]
fn close_job_handle(_handle: isize) {}

#[derive(Clone, Serialize)]
struct PtyDataEvent {
  #[serde(rename = "paneId")]
  pane_id: String,
  data: String,
}

#[derive(Clone, Serialize)]
struct PtyExitEvent {
  #[serde(rename = "paneId")]
  pane_id: String,
}

#[derive(Clone, Serialize)]
struct PaneWaitStateEvent {
  #[serde(rename = "paneId")]
  pane_id: String,
  state: String,
  #[serde(rename = "hasAttention")]
  has_attention: bool,
}

fn emit_wait_state(app: &AppHandle, pane_id: &str, state: &str, has_attention: bool) {
  let _ = app.emit(
    "amm-pane-wait-state",
    PaneWaitStateEvent { pane_id: pane_id.to_string(), state: state.to_string(), has_attention },
  );
  // Keeps the MCP participant registry's is_waiting flag in sync directly
  // from the real detector, rather than depending on the frontend's old
  // report_pane_state round-trip (spec: wait-detection feeds mcp-server's
  // send_message mode="first" targeting).
  let is_waiting = state == "WaitingForInput";
  let pane_id_owned = pane_id.to_string();
  let state_owned = state.to_string();
  let app_owned = app.clone();
  tauri::async_runtime::spawn(async move {
    let mcp_state = app_owned.state::<mcp::McpState>();
    mcp_state.report_state(&pane_id_owned, &state_owned, is_waiting).await;
    // Rust port of MdiParentForm.cs's WaitBroker fallback ("WaitPatternDetector
    // による入力待ち検知でも "idle" wait を解放 (amm/notify が来ない場合の
    // フォールバック)"): resolves any pending pane/wait_state / amm.waitState
    // waiter targeting "idle" even for panes with no hook-cli integration at
    // all (plain cmd.exe/PowerShell), not just AI CLIs that call amm/notify.
    if is_waiting {
      mcp_state.resolve_by_session(&pane_id_owned, "idle").await;
    }
  });
}

pub(crate) fn spawn_pty_for_pane(
  app: &AppHandle,
  state: &PtyState,
  pane_id: String,
  command: Option<String>,
  args: Vec<String>,
  working_directory: Option<String>,
) -> Result<(), String> {
  spawn_pty_for_pane_with_patterns(
    app,
    state,
    pane_id,
    command,
    args,
    working_directory,
    &[],
    None,
    false,
    None,
  )
}

#[allow(clippy::too_many_arguments)]
pub(crate) fn spawn_pty_for_pane_with_patterns(
  app: &AppHandle,
  state: &PtyState,
  pane_id: String,
  command: Option<String>,
  args: Vec<String>,
  working_directory: Option<String>,
  wait_patterns: &[String],
  profile_name: Option<String>,
  auto_chcp: bool,
  output_encoding: Option<String>,
) -> Result<(), String> {
  let pty_system = native_pty_system();
  let pair = pty_system
    .openpty(PtySize {
      rows: 30,
      cols: 100,
      pixel_width: 0,
      pixel_height: 0,
    })
    .map_err(|e| e.to_string())?;

  // macOS/Unix delta (add-macos-support, found via the same audit that
  // caught the LOCALAPPDATA/USERPROFILE copies): COMSPEC doesn't exist
  // there, so an ad-hoc pane with no explicit command/profile (command:
  // None, e.g. a bare "+ Pane" click) would previously fall through to
  // the literal string "powershell.exe" - not just wrong, a binary that
  // doesn't exist on macOS at all, so the pty spawn would fail outright.
  #[cfg(windows)]
  let default_shell = || std::env::var("COMSPEC").unwrap_or_else(|_| "powershell.exe".into());
  #[cfg(not(windows))]
  let default_shell = || std::env::var("SHELL").unwrap_or_else(|_| "/bin/zsh".into());
  let shell = command.unwrap_or_else(default_shell);
  // spec: profile-schema's autoChcp - wraps the real shell/args in a
  // "chcp 65001 > nul && ..." cmd.exe invocation instead of launching them
  // directly, matching ConPtyWrapper.cs's technique.
  //
  // macOS/Unix delta (add-macos-support): chcp and cmd.exe are Windows
  // console-codepage concepts with no equivalent on Unix - found via live
  // testing that an imported/edited profile keeping autoChcp=true (the
  // default on the Cmd/PowerShell presets) unconditionally wrapped the
  // real shell in a literal "cmd.exe /d /s /c ..." invocation even here,
  // failing every launch with a "cmd.exe not found" error unrelated to
  // whatever the user actually configured. UTF-8 is already the default
  // locale encoding on macOS/Linux, so there is nothing to wrap.
  let auto_chcp = auto_chcp && cfg!(windows);
  let (spawn_shell, spawn_args) =
    if auto_chcp { profile::build_chcp_wrapped_command(&shell, &args)? } else { (shell, args) };
  let mut cmd = CommandBuilder::new(spawn_shell);
  for a in spawn_args {
    cmd.arg(a);
  }
  // macOS/Unix delta (add-macos-support, found via live testing: the
  // .app-launched Claude Code TUI rendered a visibly simpler screen than
  // the same binary run from a Terminal.app shell). portable-pty's
  // CommandBuilder only inherits the parent process's own env
  // (get_base_env() = std::env::vars_os(), no TERM fallback), and a
  // Finder/LaunchServices-launched .app has no TERM at all - launchd
  // doesn't set one the way an interactive shell does. Ink/React-based
  // CLI TUIs (claude, copilot) detect that as a minimal/no-color terminal
  // and degrade their rendering accordingly. A Terminal-launched raw
  // binary already has a real TERM in its env, so this only fills the gap
  // rather than overriding a legitimate value.
  #[cfg(not(windows))]
  if std::env::var_os("TERM").is_none() {
    cmd.env("TERM", "xterm-256color");
  }
  // spec: wait-detection's "hook駆動の外部状態通知" - port of
  // TerminalChildForm.cs's ConPTY env injection (UDR-amm-20260605T0523-7e1).
  // Without this, hook-cli's registered `amm-mcp.exe notify`/`approve`
  // commands read an empty AMM_NOTIFY_ID and silently no-op (main.rs:422-423,
  // 497-498), so the whole hook-driven notification/approval path is dead
  // for real AI CLI tools even though the pipe protocol itself works (found
  // via live testing, phase 8.2: %AMM_NOTIFY_ID% echoed back unexpanded in a
  // spawned pane's cmd.exe).
  cmd.env("AMM_NOTIFY_ID", &pane_id);
  // spec: profile-schema's ResolveWorkingDirectory semantics - defense in
  // depth alongside mcp.rs::open_pane's own normalization (found via live
  // CDP testing, phase 8.2): a blank string must fall through to
  // current_dir() the same as None, not short-circuit .map() into an empty,
  // unusable PathBuf.
  let resolved_dir = working_directory
    .filter(|s| !s.trim().is_empty())
    .map(std::path::PathBuf::from)
    .or_else(|| std::env::current_dir().ok())
    .unwrap_or_default();
  cmd.cwd(&resolved_dir);
  let pty_child = pair.slave.spawn_command(cmd).map_err(|e| e.to_string())?;
  // security: H-5 - assign a kill-on-close Job Object so this pane's
  // process tree can actually be force-terminated (see PtyEntry::job_handle
  // doc comment). Best-effort: failure just means no Job Object protection
  // for this pane, not a launch failure.
  let job_handle = assign_kill_on_close_job(pty_child.as_ref());
  if job_handle.is_none() {
    log::warn!("pane {pane_id}: failed to assign a kill-on-close Job Object; force-close will fall back to ConPTY's soft close only");
  }
  // The Child itself doesn't need to be retained: the Job Object handle
  // (closed in PtyState::remove) is what lets us force-terminate the tree,
  // and portable_pty's ConPTY slave already owns the OS-level process
  // association for normal (graceful) teardown.
  drop(pty_child);

  let mut reader = pair.master.try_clone_reader().map_err(|e| e.to_string())?;
  let writer = pair.master.take_writer().map_err(|e| e.to_string())?;
  let detector = Arc::new(Mutex::new(wait_detect::WaitPatternDetector::new(wait_patterns)));

  state.panes.lock().unwrap_or_else(|e| e.into_inner()).insert(
    pane_id.clone(),
    PtyEntry {
      master: pair.master,
      writer,
      detector: detector.clone(),
      working_dir: resolved_dir,
      profile_name,
      job_handle,
    },
  );

  let reader_pane_id = pane_id.clone();
  let app_handle = app.clone();
  let reader_detector = detector.clone();
  // spec: profile-schema's outputEncoding - resolved once per pane (not
  // per-chunk) and fed through a stateful Decoder so a multi-byte character
  // split across two reads still decodes correctly, matching the previous
  // String::from_utf8_lossy behavior's per-chunk-independence for UTF-8 but
  // extending it to other encodings too.
  let encoding = profile::resolve_output_encoding(output_encoding.as_deref());
  std::thread::spawn(move || {
    let mut decoder = encoding.new_decoder();
    let mut buf = [0u8; 4096];
    loop {
      match reader.read(&mut buf) {
        Ok(0) => break,
        Ok(n) => {
          let mut chunk = String::new();
          if let Some(cap) = decoder.max_utf8_buffer_length(n) {
            chunk.reserve(cap);
          }
          let _ = decoder.decode_to_string(&buf[..n], &mut chunk, false);
          let changed = {
            let mut d = reader_detector.lock().unwrap_or_else(|e| e.into_inner());
            let changed = d.feed(&chunk);
            (changed, d.state.as_str(), d.has_attention)
          };
          if changed.0 {
            emit_wait_state(&app_handle, &reader_pane_id, changed.1, changed.2);
          }
          let payload = PtyDataEvent {
            pane_id: reader_pane_id.clone(),
            data: chunk,
          };
          if app_handle.emit("pty-data", payload).is_err() {
            break;
          }
        }
        Err(_) => break,
      }
    }
    {
      let mut d = reader_detector.lock().unwrap_or_else(|e| e.into_inner());
      d.notify_exit();
    }
    emit_wait_state(&app_handle, &reader_pane_id, "Stopped", false);
    let _ = app_handle.emit(
      "pty-exit",
      PtyExitEvent {
        pane_id: reader_pane_id.clone(),
      },
    );
  });

  // Silence-watcher companion thread: promotes Running -> WaitingForInput
  // after 4000ms of no new output (spec: "長時間沈黙時の自力回復"), which the
  // blocking reader loop above can't detect on its own since it only wakes
  // up when there *is* new data.
  let watcher_pane_id = pane_id.clone();
  let watcher_app = app.clone();
  std::thread::spawn(move || loop {
    std::thread::sleep(std::time::Duration::from_millis(200));
    let state = watcher_app.state::<PtyState>();
    let still_exists = state.panes.lock().unwrap_or_else(|e| e.into_inner()).contains_key(&watcher_pane_id);
    if !still_exists {
      break;
    }
    let changed = {
      let mut d = detector.lock().unwrap_or_else(|e| e.into_inner());
      let changed = d.check_silence();
      (changed, d.state.as_str(), d.has_attention)
    };
    if changed.0 {
      emit_wait_state(&watcher_app, &watcher_pane_id, changed.1, changed.2);
    }
  });

  Ok(())
}

#[tauri::command]
pub(crate) fn pty_spawn(app: AppHandle, state: State<PtyState>) -> Result<String, String> {
  let pane_id = Uuid::new_v4().to_string();
  spawn_pty_for_pane(&app, &state, pane_id.clone(), None, Vec::new(), None)?;
  Ok(pane_id)
}

#[tauri::command]
pub(crate) fn pty_write(pane_id: String, data: String, state: State<PtyState>) -> Result<(), String> {
  let mut panes = state.panes.lock().unwrap_or_else(|e| e.into_inner());
  if let Some(entry) = panes.get_mut(&pane_id) {
    entry.writer.write_all(data.as_bytes()).map_err(|e| e.to_string())?;
  }
  Ok(())
}

#[tauri::command]
pub(crate) fn pty_resize(pane_id: String, cols: u16, rows: u16, state: State<PtyState>) -> Result<(), String> {
  let panes = state.panes.lock().unwrap_or_else(|e| e.into_inner());
  if let Some(entry) = panes.get(&pane_id) {
    entry
      .master
      .resize(PtySize {
        rows,
        cols,
        pixel_width: 0,
        pixel_height: 0,
      })
      .map_err(|e| e.to_string())?;
  }
  Ok(())
}

#[tauri::command]
pub(crate) fn pty_close(pane_id: String, state: State<PtyState>) -> Result<(), String> {
  state.remove(&pane_id);
  Ok(())
}

// security: H-5 process-tree teardown, macOS/Unix delta (design.md D3,
// openspec/changes/add-macos-support/). gateway.rs's unix_process_group_tests
// already proves killpg reaches a grandchild thoroughly (via
// tokio::process::Command + explicit process_group(0)); this module
// instead focuses on what's specific to *this* spawn path: confirming the
// "portable_pty children are already their own process-group leader, no
// explicit process_group(0) needed" assumption the doc comment on
// assign_kill_on_close_job makes, against a real pty-spawned child.
#[cfg(all(test, unix))]
mod unix_process_group_tests {
  use super::*;

  fn test_pty_size() -> PtySize {
    PtySize { rows: 24, cols: 80, pixel_width: 0, pixel_height: 0 }
  }

  #[test]
  fn portable_pty_child_is_already_its_own_process_group_leader() {
    let pty_system = native_pty_system();
    let pair = pty_system.openpty(test_pty_size()).expect("openpty must succeed");
    let mut cmd = CommandBuilder::new("/bin/sleep");
    cmd.arg("5");
    let pty_child = pair.slave.spawn_command(cmd).expect("spawn_command must succeed");
    let pid = pty_child.process_id().expect("pty child must have a pid") as libc::pid_t;
    let pgid = unsafe { libc::getpgid(pid) };
    assert_eq!(
      pgid, pid,
      "a portable_pty-spawned child must already be its own process-group leader (required for the pty's own job control to work at all) - this is why assign_kill_on_close_job doesn't need an explicit process_group(0) the way gateway.rs's tokio::process::Command-based spawn does"
    );
  }

  #[test]
  fn assign_and_close_job_handle_terminates_the_pty_child() {
    let pty_system = native_pty_system();
    let pair = pty_system.openpty(test_pty_size()).expect("openpty must succeed");
    let mut cmd = CommandBuilder::new("/bin/sleep");
    cmd.arg("300");
    let mut pty_child = pair.slave.spawn_command(cmd).expect("spawn_command must succeed");

    // the real functions under test, not a reimplementation.
    let handle = assign_kill_on_close_job(pty_child.as_ref()).expect("assign_kill_on_close_job must capture a pid on unix");
    close_job_handle(handle);

    let status = pty_child.wait().expect("wait() must not itself error");
    assert!(!status.success(), "pty child must have been terminated (by SIGTERM via killpg), not exited cleanly on its own");
  }
}
