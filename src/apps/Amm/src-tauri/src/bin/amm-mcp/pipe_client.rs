// Named Pipe JSON-RPC client helpers. Rust port of the connection/RPC
// helpers in src/apps/Amm.Mcp/Program.cs (ConnectAsync/CallToolAsync) and
// src/modules/Amm.PowerShell/Pipe/AmmPipeClient.cs, talking to the same
// wire protocol Amm.Tauri's src-tauri/src/mcp.rs implements.
use serde_json::{json, Value};
use std::io;
use std::time::{Duration, Instant};
use tokio::io::{AsyncBufReadExt, AsyncWriteExt, BufReader};
#[cfg(windows)]
use tokio::net::windows::named_pipe::{ClientOptions, NamedPipeClient};
#[cfg(unix)]
use tokio::net::UnixStream;

// windows::Win32::Foundation::ERROR_PIPE_BUSY, hardcoded to avoid pulling
// the `windows` crate into this binary just for one constant.
#[cfg(windows)]
const ERROR_PIPE_BUSY: i32 = 231;

#[cfg(windows)]
type Stream = NamedPipeClient;
#[cfg(unix)]
type Stream = UnixStream;

// spec: ps-module's macOS delta - AMM_MCP_SOCKET_PATH mirrors
// AMM_MCP_PIPE_NAME's override role, but holds a full path (not a bare
// name) since Unix domain sockets are addressed by filesystem path.
#[cfg(windows)]
pub fn default_pipe_name() -> String {
  std::env::var("AMM_MCP_PIPE_NAME").unwrap_or_else(|_| format!("amm-mcp-{}", whoami_user()))
}

#[cfg(unix)]
pub fn default_pipe_name() -> String {
  if let Ok(path) = std::env::var("AMM_MCP_SOCKET_PATH") {
    return path;
  }
  let uid = unsafe { libc::getuid() };
  std::env::temp_dir().join(format!("amm-mcp-{uid}")).join("mcp.sock").to_string_lossy().into_owned()
}

#[cfg(windows)]
fn whoami_user() -> String {
  std::env::var("USERNAME").unwrap_or_default()
}

pub struct PipeConn {
  reader: BufReader<tokio::io::ReadHalf<Stream>>,
  writer: tokio::io::WriteHalf<Stream>,
}

#[cfg(windows)]
async fn open_stream(pipe_name: &str) -> io::Result<Stream> {
  let addr = format!(r"\\.\pipe\{pipe_name}");
  ClientOptions::new().open(&addr)
}

// pipe_name here is already a full socket path (see default_pipe_name/
// AMM_MCP_SOCKET_PATH above), not a bare name needing a prefix.
#[cfg(unix)]
async fn open_stream(pipe_name: &str) -> io::Result<Stream> {
  UnixStream::connect(pipe_name).await
}

// Retry loop bounded by timeout_ms overall - mirrors .NET
// NamedPipeClientStream.ConnectAsync(timeoutMs)'s external contract. The
// ERROR_PIPE_BUSY retry is Windows Named Pipe-specific (no equivalent
// "busy" state for Unix domain sockets, whose listen() backlog already
// queues concurrent connections); the NotFound retry (waiting for the
// server to start listening/create the socket file) applies to both.
pub async fn connect(pipe_name: &str, timeout_ms: u64) -> io::Result<PipeConn> {
  let deadline = Instant::now() + Duration::from_millis(timeout_ms.max(1));
  loop {
    match open_stream(pipe_name).await {
      Ok(stream) => {
        let (read_half, write_half) = tokio::io::split(stream);
        return Ok(PipeConn { reader: BufReader::new(read_half), writer: write_half });
      }
      #[cfg(windows)]
      Err(e) if e.raw_os_error() == Some(ERROR_PIPE_BUSY) => {
        if Instant::now() >= deadline {
          return Err(io::Error::new(io::ErrorKind::TimedOut, "amm-mcp: timed out waiting for a free pipe instance"));
        }
        tokio::time::sleep(Duration::from_millis(50)).await;
      }
      Err(e) if Instant::now() >= deadline => return Err(e),
      Err(e) if e.kind() == io::ErrorKind::NotFound => {
        if Instant::now() >= deadline {
          return Err(e);
        }
        tokio::time::sleep(Duration::from_millis(50)).await;
      }
      Err(e) => return Err(e),
    }
  }
}

impl PipeConn {
  pub async fn write_line(&mut self, value: &Value) -> io::Result<()> {
    let mut line = value.to_string();
    line.push('\n');
    self.writer.write_all(line.as_bytes()).await?;
    self.writer.flush().await
  }

  pub async fn read_line(&mut self) -> io::Result<Option<String>> {
    let mut line = String::new();
    let n = self.reader.read_line(&mut line).await?;
    if n == 0 {
      return Ok(None);
    }
    while line.ends_with('\n') || line.ends_with('\r') {
      line.pop();
    }
    Ok(Some(line))
  }

  pub async fn read_line_timeout(&mut self, timeout_ms: u64) -> io::Result<Option<String>> {
    match tokio::time::timeout(Duration::from_millis(timeout_ms), self.read_line()).await {
      Ok(res) => res,
      Err(_) => Err(io::Error::new(io::ErrorKind::TimedOut, "amm-mcp: timed out waiting for response")),
    }
  }

  // MCP-standard initialize -> tools/call round trip, matching
  // Program.cs's CallToolAsync exactly (initialize response is read and
  // discarded, same as the .NET client).
  pub async fn call_tool(&mut self, tool_name: &str, arguments: Value) -> io::Result<Option<Value>> {
    let init_req = json!({
      "jsonrpc": "2.0",
      "id": 0,
      "method": "initialize",
      "params": {
        "protocolVersion": "2024-11-05",
        "capabilities": {},
        "clientInfo": { "name": "amm-mcp-cli", "version": "0.3.0" },
      },
    });
    self.write_line(&init_req).await?;
    self.read_line().await?; // discard initialize response

    let req = json!({
      "jsonrpc": "2.0",
      "id": 1,
      "method": "tools/call",
      "params": { "name": tool_name, "arguments": arguments },
    });
    self.write_line(&req).await?;
    let Some(line) = self.read_line().await? else { return Ok(None) };
    Ok(serde_json::from_str(&line).ok())
  }

  pub fn into_halves(self) -> (BufReader<tokio::io::ReadHalf<Stream>>, tokio::io::WriteHalf<Stream>) {
    (self.reader, self.writer)
  }
}
