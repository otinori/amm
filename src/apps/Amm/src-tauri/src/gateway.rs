// MCP gateway (spec: mcp-gateway). Rust port of
// src/apps/Amm/Core/Mcp/Gateway/{McpServerConfig,ManagedMcpProcess,GatewayManager}.cs:
// amm manages external stdio MCP server child processes, aggregates their
// tools under a "{serverName}/{toolName}" namespace, and forwards tools/call
// to the right one. Management-dialog UI (McpGatewayDialog) is out of scope
// for this pass - see PARITY-AUDIT.md; server_infos() exists for it ahead of
// time, same as hook_cli/mcp_cli's is_registered functions with no caller yet.
//
// spec: add-mcp-http-transport - a second, HTTP-based server kind
// (ManagedMcpHttpServer) lives alongside the original stdio one
// (ManagedMcpProcess, untouched). There is no OS process to supervise for an
// HTTP server (it's owned by whatever remote/local service exposes it), so
// it has no restart loop - see connect()'s doc comment for the "reconnect
// on demand" model that replaces it. GatewayManager dispatches between the
// two via the McpServerHandle enum so aggregation/forwarding/status-listing
// stay transport-agnostic, matching design.md decision 2.
use crate::profile::{McpServerConfig, McpTransportKind};
use serde_json::{json, Value};
use std::collections::HashMap;
use std::sync::atomic::{AtomicU64, Ordering};
use std::sync::Arc;
use std::time::Duration;
use tokio::io::{AsyncBufReadExt, AsyncWriteExt};
use tokio::sync::{oneshot, Mutex};

const HANDSHAKE_TIMEOUT: Duration = Duration::from_secs(10);
const RESTART_DELAY: Duration = Duration::from_secs(1);

#[derive(Debug, Clone, Copy, PartialEq, Eq, serde::Serialize)]
#[serde(rename_all = "lowercase")]
pub enum ServerStatus {
  Stopped,
  Starting,
  Running,
  Error,
}

pub struct ManagedMcpProcess {
  pub name: String,
  config: McpServerConfig,
  status: Mutex<ServerStatus>,
  tools: Mutex<Vec<Value>>,
  last_error: Mutex<Option<String>>,
  stdin: Mutex<Option<tokio::process::ChildStdin>>,
  pending: Mutex<HashMap<u64, oneshot::Sender<Value>>>,
  next_id: AtomicU64,
  restart_count: Mutex<u32>,
  // Windows Job Object handle (as isize, since raw HANDLE pointers aren't
  // Send/Sync) - spec: "amm 終了時は DisposeAsync で全子プロセスを
  // Kill(entireProcessTree: true) する". tokio's Child::kill only signals
  // the immediate process, which would leak wrapper-launched children (e.g.
  // `npx` -> node) on restart/exit; a job object with
  // JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE kills the whole tree when closed.
  job_handle: Mutex<Option<isize>>,
}

impl ManagedMcpProcess {
  fn new(config: McpServerConfig) -> Self {
    ManagedMcpProcess {
      name: config.name.clone(),
      config,
      status: Mutex::new(ServerStatus::Stopped),
      tools: Mutex::new(Vec::new()),
      last_error: Mutex::new(None),
      stdin: Mutex::new(None),
      pending: Mutex::new(HashMap::new()),
      next_id: AtomicU64::new(1),
      restart_count: Mutex::new(0),
      job_handle: Mutex::new(None),
    }
  }

  pub async fn status(&self) -> ServerStatus {
    *self.status.lock().await
  }

  pub async fn tool_count(&self) -> usize {
    self.tools.lock().await.len()
  }

  pub async fn last_error(&self) -> Option<String> {
    self.last_error.lock().await.clone()
  }

  async fn drain_pending(&self) {
    let mut pending = self.pending.lock().await;
    for (_, tx) in pending.drain() {
      let _ = tx.send(json!({ "error": { "message": "process restarted" } }));
    }
  }

  async fn close_job(&self) {
    if let Some(handle) = self.job_handle.lock().await.take() {
      close_job_handle(handle);
    }
  }

  async fn handle_incoming_line(&self, line: &str) {
    let Ok(node) = serde_json::from_str::<Value>(line) else { return };
    let Some(obj) = node.as_object() else { return };
    // No "id" -> notification, ignore (matches ProcessIncomingLine).
    let Some(id) = obj.get("id").and_then(|v| v.as_u64()) else { return };
    let Some(tx) = self.pending.lock().await.remove(&id) else { return };
    let resolved = if let Some(err) = obj.get("error") {
      json!({ "error": err.clone() })
    } else {
      obj.get("result").cloned().unwrap_or(Value::Null)
    };
    let _ = tx.send(resolved);
  }

  // Port of SendRequestAsync: writes a JSON-RPC request line to stdin and
  // awaits the matching response by id. No internal timeout - callers that
  // need one (the initialize/tools/list handshake) wrap this in
  // tokio::time::timeout themselves, matching how the .NET original relies
  // on an external CancellationToken instead of an internal deadline.
  async fn send_request(&self, method: &str, params: Value) -> Result<Value, String> {
    let id = self.next_id.fetch_add(1, Ordering::SeqCst);
    let (tx, rx) = oneshot::channel();
    self.pending.lock().await.insert(id, tx);

    let req = json!({ "jsonrpc": "2.0", "id": id, "method": method, "params": params });
    let mut line = req.to_string();
    line.push('\n');

    {
      let mut stdin = self.stdin.lock().await;
      let Some(stdin) = stdin.as_mut() else {
        self.pending.lock().await.remove(&id);
        return Err(format!("Server {} is not running", self.name));
      };
      if let Err(e) = stdin.write_all(line.as_bytes()).await {
        self.pending.lock().await.remove(&id);
        return Err(format!("write failed: {e}"));
      }
    }

    rx.await.map_err(|_| "request cancelled".to_string())
  }

  // Port of CallToolAsync. Only called on a Running server (caller checks
  // status first), but re-checks here too since status can flip between
  // the check and the call on a concurrent crash.
  pub async fn call_tool(&self, tool_name: &str, args: Value) -> Result<Value, String> {
    if *self.status.lock().await != ServerStatus::Running {
      return Err(format!("Server {} is not running", self.name));
    }
    self.send_request("tools/call", json!({ "name": tool_name, "arguments": args })).await
  }
}

// spec: add-mcp-http-transport's "HTTP サーバーの疎通ハンドシェイク" /
// "HTTP サーバーへのツール呼び出しと再接続" / "TLS 証明書検証のスキップ設定".
// No child process, no job object, no restart_count - see the module-level
// comment for why. One reqwest::Client per server (not shared) so
// skip_tls_verify only weakens TLS for that one server's connections.
pub struct ManagedMcpHttpServer {
  pub name: String,
  config: McpServerConfig,
  status: Mutex<ServerStatus>,
  tools: Mutex<Vec<Value>>,
  last_error: Mutex<Option<String>>,
  session_id: Mutex<Option<String>>,
  client: reqwest::Client,
  next_id: AtomicU64,
}

impl ManagedMcpHttpServer {
  fn new(config: McpServerConfig) -> Self {
    let client = reqwest::Client::builder()
      .danger_accept_invalid_certs(config.skip_tls_verify)
      .build()
      // A builder failure here (e.g. TLS backend init trouble) shouldn't
      // panic the whole gateway - fall back to a default client, which will
      // then simply fail every request with a normal connection/TLS error
      // that connect() surfaces as this server's last_error like any other
      // unreachable endpoint.
      .unwrap_or_else(|_| reqwest::Client::new());
    ManagedMcpHttpServer {
      name: config.name.clone(),
      config,
      status: Mutex::new(ServerStatus::Stopped),
      tools: Mutex::new(Vec::new()),
      last_error: Mutex::new(None),
      session_id: Mutex::new(None),
      client,
      next_id: AtomicU64::new(1),
    }
  }

  pub async fn status(&self) -> ServerStatus {
    *self.status.lock().await
  }

  pub async fn tool_count(&self) -> usize {
    self.tools.lock().await.len()
  }

  pub async fn last_error(&self) -> Option<String> {
    self.last_error.lock().await.clone()
  }
}

// Extracts the JSON payload from a single-frame `text/event-stream` body
// (some MCP HTTP servers answer even a one-shot POST with SSE framing). Only
// reads the frame already in hand - no live stream subscription, matching
// design.md's Non-Goals.
fn parse_sse_json_frame(text: &str) -> Result<Value, String> {
  let data_lines: Vec<&str> = text.lines().filter_map(|line| line.strip_prefix("data:")).map(|rest| rest.trim_start()).collect();
  if data_lines.is_empty() {
    return Err("no data frame in SSE response".to_string());
  }
  let joined = data_lines.join("\n");
  serde_json::from_str(&joined).map_err(|e| format!("invalid JSON in SSE frame: {e}"))
}

// Port of the stdio side's send_request, but one POST per call instead of a
// write-then-await-on-a-channel over a shared stdin/stdout pipe (Streamable
// HTTP: request/response pairing is just the HTTP request/response itself,
// no id-keyed pending map needed). Captures a returned `Mcp-Session-Id`
// header (case-insensitive per HTTP) and replays it on subsequent calls, per
// spec's "セッションIDの付与" scenario.
async fn send_json_rpc(process: &ManagedMcpHttpServer, method: &str, params: Value) -> Result<Value, String> {
  let Some(url) = process.config.url.as_deref() else {
    return Err(format!("Server {} has no url configured", process.name));
  };
  let id = process.next_id.fetch_add(1, Ordering::SeqCst);
  let body = json!({ "jsonrpc": "2.0", "id": id, "method": method, "params": params });

  let mut req = process.client.post(url).header("Content-Type", "application/json").header("Accept", "application/json, text/event-stream").json(&body);
  if let Some(headers) = &process.config.headers {
    for (k, v) in headers {
      req = req.header(k.as_str(), v.as_str());
    }
  }
  if let Some(sid) = process.session_id.lock().await.clone() {
    req = req.header("Mcp-Session-Id", sid);
  }

  let resp = match tokio::time::timeout(HANDSHAKE_TIMEOUT, req.send()).await {
    Ok(Ok(r)) => r,
    Ok(Err(e)) => return Err(format!("request failed: {e}")),
    Err(_) => return Err(format!("{method} timed out")),
  };

  let status_code = resp.status();
  let content_type = resp.headers().get(reqwest::header::CONTENT_TYPE).and_then(|v| v.to_str().ok()).unwrap_or("").to_string();
  if let Some(sid) = resp.headers().get("mcp-session-id").and_then(|v| v.to_str().ok()) {
    *process.session_id.lock().await = Some(sid.to_string());
  }
  let text = resp.text().await.map_err(|e| format!("read body failed: {e}"))?;
  if !status_code.is_success() {
    return Err(format!("HTTP {status_code}: {text}"));
  }

  let node: Value = if content_type.contains("text/event-stream") { parse_sse_json_frame(&text)? } else { serde_json::from_str(&text).map_err(|e| format!("invalid JSON response: {e}"))? };
  let obj = node.as_object().ok_or_else(|| "invalid JSON-RPC response".to_string())?;
  if let Some(err) = obj.get("error") {
    return Err(err.to_string());
  }
  Ok(obj.get("result").cloned().unwrap_or(Value::Null))
}

// Port of the stdio side's launch_and_handshake, minus the process spawn.
// spec: "HTTP サーバーの疎通ハンドシェイク". Called both eagerly (autoStart,
// from start_auto_start_servers) and lazily (from GatewayManager::call_tool
// when a server isn't currently Running) - see the "再接続" scenarios; there
// is deliberately no supervising task that retries this on its own.
async fn connect_http(process: &ManagedMcpHttpServer) -> Result<(), String> {
  *process.status.lock().await = ServerStatus::Starting;
  *process.last_error.lock().await = None;

  let init_params = json!({
    "protocolVersion": "2024-11-05",
    "clientInfo": { "name": "amm-gateway", "version": "0.1" },
    "capabilities": {},
  });
  if let Err(e) = send_json_rpc(process, "initialize", init_params).await {
    *process.status.lock().await = ServerStatus::Error;
    *process.last_error.lock().await = Some(e.clone());
    return Err(e);
  }

  let tools_result = match send_json_rpc(process, "tools/list", json!({})).await {
    Ok(v) => v,
    Err(e) => {
      *process.status.lock().await = ServerStatus::Error;
      *process.last_error.lock().await = Some(e.clone());
      return Err(e);
    }
  };
  let tools = tools_result.get("tools").and_then(|v| v.as_array()).cloned().unwrap_or_default();
  let tool_count = tools.len();
  *process.tools.lock().await = tools;
  *process.status.lock().await = ServerStatus::Running;
  log::info!("[gateway] {} connected via http ({tool_count} tools)", process.name);
  Ok(())
}

async fn read_loop(process: Arc<ManagedMcpProcess>, stdout: tokio::process::ChildStdout) {
  let mut lines = tokio::io::BufReader::new(stdout).lines();
  loop {
    match lines.next_line().await {
      Ok(Some(line)) => {
        if !line.trim().is_empty() {
          process.handle_incoming_line(&line).await;
        }
      }
      _ => break,
    }
  }
}

// Port of LaunchAndInitAsync: spawns the child, wires stdin/a read-loop
// task, assigns it to a kill-on-close job object, then performs the
// initialize -> tools/list handshake. Returns the live Child (stdout/stdin
// already taken) so the caller's supervisor loop can await its exit.
async fn launch_and_handshake(process: &Arc<ManagedMcpProcess>) -> Result<tokio::process::Child, String> {
  *process.status.lock().await = ServerStatus::Starting;
  *process.last_error.lock().await = None;
  process.close_job().await; // defensive: clear any leftover job from a prior failed attempt

  let mut cmd = tokio::process::Command::new(&process.config.command);
  cmd.args(&process.config.args);
  if let Some(env) = &process.config.env {
    for (k, v) in env {
      cmd.env(k, v);
    }
  }
  cmd.stdin(std::process::Stdio::piped());
  cmd.stdout(std::process::Stdio::piped());
  cmd.stderr(std::process::Stdio::null());
  #[cfg(windows)]
  {
    const CREATE_NO_WINDOW: u32 = 0x0800_0000;
    cmd.creation_flags(CREATE_NO_WINDOW);
  }
  // spec: mcp-gateway process-tree teardown, macOS/Unix delta (design.md
  // D3) - makes the child the leader of a new process group (pgid == its
  // own pid) so assign_kill_on_close_job/close_job_handle below can tear
  // down the whole group with killpg, the Unix analogue of Windows' Job
  // Object kill-on-close. Must happen before spawn (process_group is a
  // pre-exec Command builder option, not something applicable after the
  // fact via Child).
  #[cfg(unix)]
  {
    // tokio::process::Command has its own inherent process_group (no
    // std::os::unix::process::CommandExt import needed, unlike
    // std::process::Command).
    cmd.process_group(0);
  }

  let mut child = cmd.spawn().map_err(|e| format!("Failed to start process: {} ({e})", process.config.command))?;

  let job = assign_kill_on_close_job(&child);
  *process.job_handle.lock().await = job;

  let Some(stdout) = child.stdout.take() else {
    let _ = child.kill().await;
    return Err("no stdout".to_string());
  };
  let Some(stdin) = child.stdin.take() else {
    let _ = child.kill().await;
    return Err("no stdin".to_string());
  };
  *process.stdin.lock().await = Some(stdin);

  // tauri::async_runtime::spawn, not raw tokio::spawn (found via live
  // testing, phase 8.2): launch_and_handshake can run from contexts with no
  // ambient Tokio runtime (e.g. GatewayManager::start_auto_start_servers
  // called from Tauri's synchronous .setup() closure), where tokio::spawn
  // panics with "there is no reactor running". Tauri's own spawn works
  // regardless of the calling context, matching every other background
  // task in this codebase (lib.rs/mcp.rs never use raw tokio::spawn either).
  tauri::async_runtime::spawn(read_loop(process.clone(), stdout));

  let init_params = json!({
    "protocolVersion": "2024-11-05",
    "clientInfo": { "name": "amm-gateway", "version": "0.1" },
    "capabilities": {},
  });
  if let Err(e) = handshake_step(process, "initialize", init_params).await {
    let _ = child.kill().await;
    process.close_job().await;
    return Err(e);
  }

  let tools_result = match handshake_step(process, "tools/list", json!({})).await {
    Ok(v) => v,
    Err(e) => {
      let _ = child.kill().await;
      process.close_job().await;
      return Err(e);
    }
  };
  let tools = tools_result.get("tools").and_then(|v| v.as_array()).cloned().unwrap_or_default();
  let tool_count = tools.len();
  *process.tools.lock().await = tools;
  *process.status.lock().await = ServerStatus::Running;
  log::info!("[gateway] {} started ({tool_count} tools)", process.name);
  Ok(child)
}

async fn handshake_step(process: &Arc<ManagedMcpProcess>, method: &str, params: Value) -> Result<Value, String> {
  match tokio::time::timeout(HANDSHAKE_TIMEOUT, process.send_request(method, params)).await {
    Ok(Ok(v)) if v.get("error").is_some() => Err(v["error"].to_string()),
    Ok(Ok(v)) => Ok(v),
    Ok(Err(e)) => Err(e),
    Err(_) => Err(format!("{method} timed out")),
  }
}

// Port of the StartAsync/OnProcessExited restart cycle, restructured around
// tokio::process::Child::wait() instead of C#'s Process.Exited event - one
// long-running task per configured server for its whole lifetime.
async fn supervise(process: Arc<ManagedMcpProcess>) {
  loop {
    match launch_and_handshake(&process).await {
      Ok(mut child) => {
        let exit = child.wait().await;
        log::info!("[gateway] {} exited: {:?} (restarts={}/{})", process.name, exit, *process.restart_count.lock().await, process.config.max_restarts);
        process.drain_pending().await;
        process.close_job().await;
        *process.stdin.lock().await = None;

        let mut restart_count = process.restart_count.lock().await;
        if *restart_count < process.config.max_restarts {
          *restart_count += 1;
          drop(restart_count);
          tokio::time::sleep(RESTART_DELAY).await;
          continue;
        }
        drop(restart_count);
        *process.status.lock().await = ServerStatus::Error;
        *process.last_error.lock().await = Some("Process exited and max restarts reached".to_string());
        return;
      }
      Err(e) => {
        // Launch/handshake failure (as opposed to a crash-after-running) -
        // matches .NET's LaunchAndInitAsync catch block: sets Error and
        // does NOT enter the restart cycle on its own.
        log::error!("[gateway] {} start failed: {e}", process.name);
        *process.status.lock().await = ServerStatus::Error;
        *process.last_error.lock().await = Some(e);
        return;
      }
    }
  }
}

#[cfg(windows)]
fn assign_kill_on_close_job(child: &tokio::process::Child) -> Option<isize> {
  use windows::Win32::Foundation::{CloseHandle, HANDLE};
  use windows::Win32::System::JobObjects::{
    AssignProcessToJobObject, CreateJobObjectW, JobObjectExtendedLimitInformation, JOBOBJECT_EXTENDED_LIMIT_INFORMATION, JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE,
  };

  let raw_handle = child.raw_handle()?;
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
    let process_handle = HANDLE(raw_handle);
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

// macOS/Unix counterpart (design.md D3): the "handle" stored in
// job_handle is the child's pid, which process_group(0) above made equal
// to its process group id - killpg(pid, ...) therefore signals the whole
// group, not just the direct child.
#[cfg(unix)]
fn assign_kill_on_close_job(child: &tokio::process::Child) -> Option<isize> {
  child.id().map(|id| id as isize)
}

#[cfg(unix)]
fn close_job_handle(handle: isize) {
  let pgid = handle as libc::pid_t;
  unsafe {
    libc::killpg(pgid, libc::SIGTERM);
  }
  // Escalate to SIGKILL after a short grace period for stragglers that
  // ignore SIGTERM. killpg on an already-empty group just fails with
  // ESRCH, harmless to ignore (best-effort cleanup, mirrors the Windows
  // Job Object's own unconditional "kill whatever's left" semantics).
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
fn assign_kill_on_close_job(_child: &tokio::process::Child) -> Option<isize> {
  None
}
#[cfg(not(any(windows, unix)))]
fn close_job_handle(_handle: isize) {}

#[derive(serde::Serialize)]
#[serde(rename_all = "camelCase")]
pub struct GatewayServerInfo {
  pub name: String,
  pub status: ServerStatus,
  pub tool_count: usize,
  pub last_error: Option<String>,
}

// spec: add-mcp-http-transport design.md decision 2 - a small enum instead
// of a trait object (`Arc<dyn ...>`) since there are exactly two transport
// kinds and this avoids pulling in async-trait for a handful of delegating
// methods. GatewayManager's public API (aggregation/forwarding/status
// listing) is unchanged; only this dispatch layer is new.
enum McpServerHandle {
  Stdio(Arc<ManagedMcpProcess>),
  Http(Arc<ManagedMcpHttpServer>),
}

impl McpServerHandle {
  fn name(&self) -> &str {
    match self {
      McpServerHandle::Stdio(p) => &p.name,
      McpServerHandle::Http(p) => &p.name,
    }
  }

  fn auto_start(&self) -> bool {
    match self {
      McpServerHandle::Stdio(p) => p.config.auto_start,
      McpServerHandle::Http(p) => p.config.auto_start,
    }
  }

  async fn status(&self) -> ServerStatus {
    match self {
      McpServerHandle::Stdio(p) => p.status().await,
      McpServerHandle::Http(p) => p.status().await,
    }
  }

  async fn tool_count(&self) -> usize {
    match self {
      McpServerHandle::Stdio(p) => p.tool_count().await,
      McpServerHandle::Http(p) => p.tool_count().await,
    }
  }

  async fn last_error(&self) -> Option<String> {
    match self {
      McpServerHandle::Stdio(p) => p.last_error().await,
      McpServerHandle::Http(p) => p.last_error().await,
    }
  }

  async fn tools_snapshot(&self) -> Vec<Value> {
    match self {
      McpServerHandle::Stdio(p) => p.tools.lock().await.clone(),
      McpServerHandle::Http(p) => p.tools.lock().await.clone(),
    }
  }
}

#[derive(Default)]
pub struct GatewayManager {
  processes: Vec<McpServerHandle>,
}

impl GatewayManager {
  pub fn new(configs: Vec<McpServerConfig>) -> Self {
    GatewayManager {
      processes: configs
        .into_iter()
        .map(|c| match c.transport {
          McpTransportKind::Stdio => McpServerHandle::Stdio(Arc::new(ManagedMcpProcess::new(c))),
          McpTransportKind::Http => McpServerHandle::Http(Arc::new(ManagedMcpHttpServer::new(c))),
        })
        .collect(),
    }
  }

  // spec: "自動起動サーバーの一括起動" - autoStart=true entries only, all
  // launched concurrently (each on its own task, not awaited here - matches
  // Task.WhenAll's "fire and let them run" character for a GUI startup path
  // that must not block on slow/hanging external servers). Stdio spawns a
  // long-lived supervisor task (restart loop); http spawns a single connect
  // attempt (no restart loop - see connect_http's doc comment).
  pub fn start_auto_start_servers(&self) {
    for handle in &self.processes {
      match handle {
        McpServerHandle::Stdio(process) => {
          if process.config.auto_start {
            // tauri::async_runtime::spawn, not raw tokio::spawn - see the
            // comment on the read_loop spawn in launch_and_handshake for why
            // (this is the actual crash site: called from lib.rs::run's
            // synchronous .setup() closure, which has no ambient Tokio
            // runtime; found via live CDP testing, phase 8.2 - amm.exe
            // panicked and exited immediately on every launch once any
            // autoStart=true gateway server was configured).
            tauri::async_runtime::spawn(supervise(process.clone()));
          }
        }
        McpServerHandle::Http(process) => {
          if process.config.auto_start {
            let process = process.clone();
            tauri::async_runtime::spawn(async move {
              if let Err(e) = connect_http(&process).await {
                log::error!("[gateway] {} http connect failed: {e}", process.name);
              }
            });
          }
        }
      }
    }
  }

  // spec: pane-management's new "外部 .amm ファイルの自動起動確認" requirement -
  // lets the caller build a human-readable "what would actually auto-start"
  // list (max 10 shown per spec) without exposing ManagedMcpProcess itself.
  pub fn auto_start_entries(&self) -> Vec<(String, String)> {
    self
      .processes
      .iter()
      .filter(|h| h.auto_start())
      .map(|h| match h {
        McpServerHandle::Stdio(p) => {
          let args = if p.config.args.is_empty() { String::new() } else { format!(" {}", p.config.args.join(" ")) };
          (p.config.name.clone(), format!("{}{}", p.config.command, args))
        }
        McpServerHandle::Http(p) => (p.config.name.clone(), p.config.url.clone().unwrap_or_default()),
      })
      .collect()
  }

  // spec: "ツールの集約と名前空間プレフィックス" - transport-agnostic (matches
  // spec.md's unmodified requirement text).
  pub async fn aggregated_tools(&self) -> Vec<Value> {
    let mut result = Vec::new();
    for handle in &self.processes {
      if handle.status().await != ServerStatus::Running {
        continue;
      }
      let name = handle.name();
      for tool in handle.tools_snapshot().await.iter() {
        let Some(original_name) = tool.get("name").and_then(|v| v.as_str()) else { continue };
        let mut prefixed = json!({
          "name": format!("{}/{}", name, original_name),
          "description": format!("[{}] {}", name, tool.get("description").and_then(|v| v.as_str()).unwrap_or("")),
        });
        if let Some(schema) = tool.get("inputSchema") {
          prefixed["inputSchema"] = schema.clone();
        }
        result.push(prefixed);
      }
    }
    result
  }

  fn find(&self, server_name: &str) -> Option<&McpServerHandle> {
    self.processes.iter().find(|h| h.name().eq_ignore_ascii_case(server_name))
  }

  pub fn is_gateway_tool(&self, prefixed_name: &str) -> bool {
    match prefixed_name.split_once('/') {
      Some((server_name, _)) => self.find(server_name).is_some(),
      None => false,
    }
  }

  // spec: "ゲートウェイツール呼び出しの転送" (stdio path, unchanged) and
  // add-mcp-http-transport's "HTTP サーバーへのツール呼び出しと再接続" (http
  // path, new). Ok(None) mirrors CallToolAsync returning null (server
  // missing/not running, or http reconnect attempt failed) so the caller can
  // surface the exact "-32603 Gateway server for '...' is not running" spec
  // text; Ok(Some(v)) with a top-level "error" key mirrors a remote error
  // response, left for the caller to unwrap same as McpPipeServer.cs does.
  pub async fn call_tool(&self, prefixed_name: &str, args: Value) -> Option<Value> {
    let (server_name, tool_name) = prefixed_name.split_once('/')?;
    let handle = self.find(server_name)?;
    match handle {
      McpServerHandle::Stdio(process) => {
        if process.status().await != ServerStatus::Running {
          return None;
        }
        match process.call_tool(tool_name, args).await {
          Ok(v) => Some(v),
          Err(e) => Some(json!({ "error": { "message": e } })),
        }
      }
      McpServerHandle::Http(process) => {
        if process.status().await != ServerStatus::Running {
          // spec: "停止していたサーバーへの呼び出しで自動復帰" - one lazy
          // reconnect attempt, no background retry loop.
          if connect_http(process).await.is_err() {
            return None;
          }
        }
        match send_json_rpc(process, "tools/call", json!({ "name": tool_name, "arguments": args })).await {
          Ok(v) => Some(v),
          Err(e) => {
            *process.status.lock().await = ServerStatus::Error;
            *process.last_error.lock().await = Some(e.clone());
            Some(json!({ "error": { "message": e } }))
          }
        }
      }
    }
  }

  // spec: mcp-gateway's "管理サーバーのステータス表示" - snapshot the app-boot
  // GatewayManager's live process state for the management dialog.
  pub async fn server_infos(&self) -> Vec<GatewayServerInfo> {
    let mut infos = Vec::new();
    for h in &self.processes {
      infos.push(GatewayServerInfo { name: h.name().to_string(), status: h.status().await, tool_count: h.tool_count().await, last_error: h.last_error().await });
    }
    infos
  }
}

// spec: 外部 MCP サーバー設定の "AMM 共通 (グローバル)" half -
// %LOCALAPPDATA%\amm\mcp-servers.json, { "mcpServers": [...] } shape,
// distinct from profiles.amm's file-local mcpServers field.
fn global_config_path() -> std::path::PathBuf {
  crate::app_data_base_dir().join("amm").join("mcp-servers.json")
}

#[derive(serde::Serialize, serde::Deserialize)]
struct GlobalMcpRoot {
  #[serde(default, rename = "mcpServers")]
  mcp_servers: Vec<McpServerConfig>,
}

pub fn load_global_servers() -> Vec<McpServerConfig> {
  let Ok(text) = std::fs::read_to_string(global_config_path()) else { return Vec::new() };
  match serde_json::from_str::<GlobalMcpRoot>(&text) {
    Ok(root) => root.mcp_servers,
    Err(e) => {
      log::error!("[gateway] global mcp-servers.json parse failed: {e}");
      Vec::new()
    }
  }
}

// spec: mcp-gateway's "管理サーバーのステータス表示"/McpGatewayDialog OK path -
// the global group saves immediately (unlike the file-local group, which
// only updates ProfilesState in memory - matches profiles.amm's own
// explicit-save convention elsewhere). Same atomic temp+rename write as
// profile::save_profiles.
pub fn save_global_servers(servers: &[McpServerConfig]) -> std::io::Result<()> {
  let path = global_config_path();
  if let Some(dir) = path.parent() {
    std::fs::create_dir_all(dir)?;
  }
  let json = serde_json::to_string_pretty(&GlobalMcpRoot { mcp_servers: servers.to_vec() })?;
  let tmp = path.with_extension("json.tmp");
  std::fs::write(&tmp, json)?;
  std::fs::rename(&tmp, &path)
}

// spec: "グローバルとファイル固有の並存" - both groups are combined into one
// GatewayManager, global entries first (matches MdiParentForm.cs's
// `(_mcpServersGlobal ?? []).Concat(_mcpServers ?? [])`).
pub fn build_manager(file_servers: Vec<McpServerConfig>) -> GatewayManager {
  let mut all = load_global_servers();
  all.extend(file_servers);
  GatewayManager::new(all)
}

#[cfg(test)]
mod tests {
  use super::*;

  fn config(name: &str) -> McpServerConfig {
    McpServerConfig {
      name: name.to_string(),
      transport: crate::profile::McpTransportKind::Stdio,
      command: "does-not-exist".to_string(),
      args: vec![],
      env: None,
      auto_start: true,
      max_restarts: 3,
      url: None,
      headers: None,
      skip_tls_verify: false,
    }
  }

  fn http_config(name: &str, url: &str) -> McpServerConfig {
    McpServerConfig {
      name: name.to_string(),
      transport: crate::profile::McpTransportKind::Http,
      command: String::new(),
      args: vec![],
      env: None,
      auto_start: true,
      max_restarts: 3,
      url: Some(url.to_string()),
      headers: None,
      skip_tls_verify: false,
    }
  }

  #[test]
  fn is_gateway_tool_matches_configured_server_name_only() {
    let manager = GatewayManager::new(vec![config("fs"), config("browser")]);
    assert!(manager.is_gateway_tool("fs/read_file"));
    assert!(manager.is_gateway_tool("BROWSER/click")); // case-insensitive, matches .NET's OrdinalIgnoreCase
    assert!(!manager.is_gateway_tool("pane/open")); // built-in tool, no configured server named "pane"
    assert!(!manager.is_gateway_tool("no_slash_at_all"));
  }

  #[tokio::test]
  async fn aggregated_tools_excludes_non_running_servers() {
    // Freshly constructed processes default to Stopped, never having been
    // started - matches the spec's "Running でないサーバーのツールは一覧に
    // 含まれない" without needing to actually spawn a process in a unit test.
    let manager = GatewayManager::new(vec![config("fs")]);
    assert!(manager.aggregated_tools().await.is_empty());
  }

  #[tokio::test]
  async fn call_tool_on_stopped_server_returns_none() {
    let manager = GatewayManager::new(vec![config("fs")]);
    assert_eq!(manager.call_tool("fs/read_file", json!({})).await, None);
  }

  #[tokio::test]
  async fn call_tool_on_unknown_server_returns_none() {
    let manager = GatewayManager::new(vec![config("fs")]);
    assert_eq!(manager.call_tool("unknown/tool", json!({})).await, None);
  }

  #[test]
  fn global_root_parses_mcp_servers_key_with_defaults() {
    let json = r#"{"mcpServers":[{"name":"fs","command":"npx","args":["-y","x"]}]}"#;
    let root: GlobalMcpRoot = serde_json::from_str(json).unwrap();
    assert_eq!(root.mcp_servers.len(), 1);
    assert_eq!(root.mcp_servers[0].name, "fs");
    assert!(root.mcp_servers[0].auto_start); // default true
    assert_eq!(root.mcp_servers[0].max_restarts, 3); // default 3
  }

  #[test]
  fn global_root_missing_key_yields_empty_list() {
    let root: GlobalMcpRoot = serde_json::from_str("{}").unwrap();
    assert!(root.mcp_servers.is_empty());
  }

  #[test]
  fn build_manager_puts_global_entries_before_file_entries() {
    // Can't easily fake %LOCALAPPDATA% in a unit test, but build_manager's
    // ordering contract (global first, then file-local) is what matters and
    // is directly checkable against whatever's actually on this machine.
    let mut expected_prefix = load_global_servers().into_iter().map(|c| c.name).collect::<Vec<_>>();
    expected_prefix.push("file-local".to_string());
    let manager = build_manager(vec![config("file-local")]);
    let names: Vec<String> = manager.processes.iter().map(|h| h.name().to_string()).collect();
    assert_eq!(names, expected_prefix);
  }

  // spec: add-mcp-http-transport's "外部 MCP サーバー設定" - type=http entries
  // are recognized by name the same way stdio ones are.
  #[test]
  fn is_gateway_tool_matches_http_server_name_too() {
    let manager = GatewayManager::new(vec![config("fs"), http_config("obsidian", "https://127.0.0.1:27124/mcp/")]);
    assert!(manager.is_gateway_tool("obsidian/list_notes"));
  }

  // spec: add-mcp-http-transport's "HTTP サーバーへのツール呼び出しと再接続" -
  // a freshly constructed http server is Stopped; the lazy reconnect attempt
  // hits a closed local port and fails fast, so call_tool must return None
  // (same contract as the stdio "not running" case) rather than hang or panic.
  #[tokio::test]
  async fn call_tool_on_unreachable_http_server_returns_none() {
    let manager = GatewayManager::new(vec![http_config("obsidian", "http://127.0.0.1:1/mcp")]);
    assert_eq!(manager.call_tool("obsidian/read_note", json!({})).await, None);
  }
}

// spec: mcp-gateway process-tree teardown, macOS/Unix delta (design.md D3,
// openspec/changes/add-macos-support/). Exercises the real
// assign_kill_on_close_job/close_job_handle pair (not a reimplementation)
// against an actual spawned process tree, to confirm killpg reaches a
// grandchild the direct child spawned - the scenario Windows' Job Object
// handles automatically and process groups must be relied on for here.
#[cfg(all(test, unix))]
mod unix_process_group_tests {
  use super::*;
  use tokio::io::{AsyncBufReadExt, BufReader};

  fn process_alive(pid: libc::pid_t) -> bool {
    // kill(pid, 0) sends no signal, just checks existence/permission -
    // ESRCH means the process is gone, EPERM would mean it exists but we
    // lack permission (won't happen for our own child).
    unsafe { libc::kill(pid, 0) == 0 }
  }

  #[tokio::test]
  async fn killpg_terminates_both_the_direct_child_and_a_grandchild_it_spawned() {
    let mut cmd = tokio::process::Command::new("/bin/sh");
    // Backgrounds a long-lived grandchild (`sleep`), prints its pid, then
    // blocks on it - mirrors a real MCP server that itself spawns a helper
    // process. process_group(0) matches launch_and_handshake's own setup.
    cmd
      .arg("-c")
      .arg("sleep 300 & echo GRANDCHILD:$!; wait")
      .stdout(std::process::Stdio::piped())
      .process_group(0);

    let mut child = cmd.spawn().expect("failed to spawn test shell");
    let child_pid = child.id().expect("spawned child must have a pid") as libc::pid_t;

    let stdout = child.stdout.take().expect("stdout must be piped");
    let mut lines = BufReader::new(stdout).lines();
    let line = lines
      .next_line()
      .await
      .expect("reading the grandchild-pid line must not error")
      .expect("shell must print the GRANDCHILD:<pid> line before this EOFs");
    let grandchild_pid: libc::pid_t = line
      .strip_prefix("GRANDCHILD:")
      .expect("line must have the expected prefix")
      .trim()
      .parse()
      .expect("grandchild pid must parse as an integer");

    assert!(process_alive(child_pid), "direct child must be alive right after spawn");
    assert!(process_alive(grandchild_pid), "grandchild (backgrounded sleep) must be alive right after spawn");

    // assign_kill_on_close_job just captures the pid (== pgid, since
    // process_group(0) made this child its own group leader) - the real
    // function under test.
    let handle = assign_kill_on_close_job(&child).expect("assign_kill_on_close_job must capture a pid on unix");
    close_job_handle(handle);

    // The grandchild isn't our own child (it's the shell's), so
    // kill(pid, 0) - existence check, not reaping - is the only signal
    // available for it, and a clean death here means it was actually
    // reaped by launchd/init after reparenting, not left as a zombie.
    // The *direct* child is a different story: found via a real test
    // failure that kill(pid, 0) still returns success for OUR OWN zombie
    // child on macOS (the pid table entry persists until reaped), even
    // though child.try_wait() already reports it exited via SIGTERM at
    // that point - so the direct child's liveness must be checked via
    // try_wait() (actual reap-or-not), not kill(pid, 0).
    let mut grandchild_gone = false;
    for _ in 0..20 {
      if !process_alive(grandchild_pid) {
        grandchild_gone = true;
        break;
      }
      tokio::time::sleep(std::time::Duration::from_millis(50)).await;
    }
    assert!(grandchild_gone, "grandchild must be terminated by killpg (the whole point of process-group teardown, not just the direct child)");

    let status = tokio::time::timeout(std::time::Duration::from_secs(2), child.wait())
      .await
      .expect("direct child must exit promptly after killpg, not hang")
      .expect("wait() must not itself error");
    assert!(!status.success(), "direct child must have been terminated (by SIGTERM), not exited cleanly on its own");
  }
}
