// amm MCP / CLI bridge - Rust port of src/apps/Amm.Mcp/Program.cs
// (UDR-amm-20260719T0013-b7e: replaces the .NET Amm.Mcp.exe to eliminate
// the repo's last .NET dependency). Talks to the same Named Pipe wire
// protocol Amm.Tauri's src-tauri/src/mcp.rs implements.
//
// Modes (first arg selects):
//   amm-mcp.exe                       -> stdio MCP bridge (default) or REPL
//   amm-mcp.exe --bridge              -> same, explicit
//   amm-mcp.exe send <nick> [msg...]  -> CLI send. msg omitted -> stdin
//   amm-mcp.exe list                  -> list_participants JSON to stdout
//   amm-mcp.exe notify [...]          -> hook-driven state push (no-op safe)
//   amm-mcp.exe approve [...]         -> hook-driven permission relay
//
// Common options (all modes): --pipe-name <name>, --connect-timeout <ms>
// Exit codes: 0=ok 1=bad args 2=no server 3=io error 4=mcp error
mod notify_mapper;
mod pipe_client;

use serde_json::{json, Value};
use std::io::{IsTerminal, Read, Write};
use tokio::io::{AsyncBufReadExt, BufReader};

const EXIT_OK: i32 = 0;
const EXIT_BAD_ARGS: i32 = 1;
const EXIT_NO_SERVER: i32 = 2;
const EXIT_IO_ERROR: i32 = 3;
const EXIT_MCP_ERROR: i32 = 4;

#[tokio::main]
async fn main() {
  let args: Vec<String> = std::env::args().skip(1).collect();
  let code = run(&args).await;
  std::process::exit(code);
}

async fn run(args: &[String]) -> i32 {
  let (mode, rest) = resolve_mode(args);
  let pipe_name = resolve_pipe_name(rest);
  let connect_timeout_ms = resolve_timeout(rest);

  let result = match mode {
    Mode::Bridge => run_bridge(&pipe_name, connect_timeout_ms).await,
    Mode::Repl => run_repl(&pipe_name, connect_timeout_ms).await,
    Mode::Send => run_send(rest, &pipe_name, connect_timeout_ms).await,
    Mode::List => run_list(&pipe_name, connect_timeout_ms).await,
    Mode::Notify => run_notify(rest, &pipe_name).await,
    Mode::Approve => run_approve(rest, &pipe_name).await,
    Mode::Help => {
      print_usage();
      return EXIT_BAD_ARGS;
    }
  };

  match result {
    Ok(code) => code,
    Err(e) if e.kind() == std::io::ErrorKind::TimedOut => {
      eprintln!("amm-mcp: amm GUI に接続できませんでした (pipe={pipe_name})。GUI を起動してから再試行してください。");
      EXIT_NO_SERVER
    }
    Err(e) => {
      eprintln!("amm-mcp: unexpected error: {e}");
      EXIT_IO_ERROR
    }
  }
}

// ---- bridge mode ----

async fn run_bridge(pipe_name: &str, timeout_ms: u64) -> std::io::Result<i32> {
  let conn = match pipe_client::connect(pipe_name, timeout_ms).await {
    Ok(c) => c,
    Err(e) if e.kind() == std::io::ErrorKind::TimedOut || e.kind() == std::io::ErrorKind::NotFound => {
      eprintln!("amm-mcp: amm GUI に接続できませんでした (pipe={pipe_name})。GUI を起動してから再試行してください。");
      return Ok(EXIT_NO_SERVER);
    }
    Err(e) => return Err(e),
  };
  let (mut pipe_read, mut pipe_write) = conn.into_halves();

  let mut stdin = tokio::io::stdin();
  let mut stdout = tokio::io::stdout();

  // Same race as Program.cs's RunBridge: whichever direction finishes
  // first ends the session, but if stdin closed first we grace-wait up
  // to 250ms for the server's last response to drain through.
  let t1 = tokio::io::copy(&mut stdin, &mut pipe_write);
  let t2 = tokio::io::copy(&mut pipe_read, &mut stdout);
  tokio::pin!(t1);
  tokio::pin!(t2);

  tokio::select! {
    r1 = &mut t1 => {
      let _ = tokio::time::timeout(std::time::Duration::from_millis(250), &mut t2).await;
      if let Err(e) = r1 { eprintln!("amm-mcp: bridge copy ended: {e}"); }
    }
    r2 = &mut t2 => {
      if let Err(e) = r2 { eprintln!("amm-mcp: bridge copy ended: {e}"); }
    }
  }
  Ok(EXIT_OK)
}

// ---- REPL mode ----

async fn run_repl(pipe_name: &str, timeout_ms: u64) -> std::io::Result<i32> {
  println!("amm-mcp interactive (type 'help' or '?' for commands, 'quit' to exit)");
  println!("  pipe: {pipe_name}");
  println!();

  let mut stdin = BufReader::new(tokio::io::stdin());
  loop {
    print!("> ");
    std::io::stdout().flush().ok();
    let mut line = String::new();
    let n = stdin.read_line(&mut line).await?;
    if n == 0 {
      println!();
      return Ok(EXIT_OK);
    }
    let line = line.trim();
    if line.is_empty() {
      continue;
    }
    match line {
      "quit" | "exit" | "q" => return Ok(EXIT_OK),
      "help" | "?" | "h" => {
        print_repl_help();
        continue;
      }
      _ => {}
    }
    if let Err(e) = dispatch_repl_command(line, pipe_name, timeout_ms).await {
      eprintln!("error: {e}");
    }
  }
}

async fn dispatch_repl_command(line: &str, pipe_name: &str, timeout_ms: u64) -> std::io::Result<()> {
  let tokens = split_repl_line(line);
  if tokens.is_empty() {
    return Ok(());
  }
  match tokens[0].to_lowercase().as_str() {
    "list" => {
      run_list(pipe_name, timeout_ms).await?;
    }
    "send" => {
      let mut fake_args = vec!["send".to_string()];
      fake_args.extend(tokens[1..].iter().cloned());
      run_send(&fake_args, pipe_name, timeout_ms).await?;
    }
    "peek" => {
      let mut conn = match pipe_client::connect(pipe_name, timeout_ms).await {
        Ok(c) => c,
        Err(_) => return Ok(()),
      };
      let mut args = json!({});
      if tokens.len() >= 2 {
        args["recipient"] = json!(tokens[1]);
      }
      let Some(resp) = conn.call_tool("peek_queue", args).await? else {
        eprintln!("error: no response");
        return Ok(());
      };
      if let Some(err) = resp.get("error") {
        eprintln!("server error: {}", err.get("message").and_then(|v| v.as_str()).unwrap_or(""));
        return Ok(());
      }
      let queues = resp.pointer("/result/structuredContent/queues").cloned().unwrap_or(json!([]));
      println!("{}", serde_json::to_string_pretty(&queues).unwrap_or_default());
    }
    "open" => {
      let mut conn = match pipe_client::connect(pipe_name, timeout_ms).await {
        Ok(c) => c,
        Err(_) => return Ok(()),
      };
      let mut args = json!({});
      if tokens.len() >= 2 {
        args["profile_name"] = json!(tokens[1..].join(" "));
      }
      let Some(resp) = conn.call_tool("pane/open", args).await? else {
        eprintln!("error: no response");
        return Ok(());
      };
      if let Some(err) = resp.get("error") {
        eprintln!("server error: {}", err.get("message").and_then(|v| v.as_str()).unwrap_or(""));
        return Ok(());
      }
      let session_id = resp.pointer("/result/structuredContent/session_id").and_then(|v| v.as_str()).unwrap_or("(null)");
      println!("session_id: {session_id}");
    }
    "close" => {
      if tokens.len() < 2 {
        eprintln!("usage: close <session-id> [--force]");
        return Ok(());
      }
      let mut conn = match pipe_client::connect(pipe_name, timeout_ms).await {
        Ok(c) => c,
        Err(_) => return Ok(()),
      };
      let mut args = json!({ "session_id": tokens[1] });
      if tokens.iter().any(|t| t == "--force") {
        args["force"] = json!(true);
      }
      let Some(resp) = conn.call_tool("pane/close", args).await? else {
        eprintln!("error: no response");
        return Ok(());
      };
      if let Some(err) = resp.get("error") {
        eprintln!("server error: {}", err.get("message").and_then(|v| v.as_str()).unwrap_or(""));
        return Ok(());
      }
      // NOTE: the .NET Program.cs original reads `/result/success` here,
      // which is a pre-existing bug (MakeToolResult always nests fields
      // under structuredContent, so that path was never populated and
      // always printed "success: false" regardless of outcome). Fixed
      // here since it doesn't affect any hook-cli-critical path, only
      // this rarely-used REPL command's own status text.
      let success = resp.pointer("/result/structuredContent/success").and_then(|v| v.as_bool()).unwrap_or(false);
      println!("success: {success}");
    }
    "wait" => {
      if tokens.len() < 2 {
        eprintln!("usage: wait <session-id|nickname> [idle|attention] [--timeout-ms N]");
        return Ok(());
      }
      let mut target_state = "idle".to_string();
      let mut wait_timeout_ms: u64 = 300_000;
      let mut i = 2;
      while i < tokens.len() {
        if tokens[i] == "--timeout-ms" && i + 1 < tokens.len() {
          if let Ok(ms) = tokens[i + 1].parse::<u64>() {
            wait_timeout_ms = ms;
          }
          i += 1;
        } else if tokens[i] == "idle" || tokens[i] == "attention" {
          target_state = tokens[i].clone();
        }
        i += 1;
      }

      let resolved_session_id = if uuid_like(&tokens[1]) {
        tokens[1].clone()
      } else {
        let mut list_conn = match pipe_client::connect(pipe_name, timeout_ms).await {
          Ok(c) => c,
          Err(_) => return Ok(()),
        };
        let Some(list_resp) = list_conn.call_tool("list_participants", json!({})).await? else {
          eprintln!("error: no response");
          return Ok(());
        };
        let participants = list_resp.pointer("/result/structuredContent/participants").and_then(|v| v.as_array()).cloned().unwrap_or_default();
        let Some(m) = participants.iter().find(|p| {
          p.get("nickname").and_then(|v| v.as_str()).map(|n| n.eq_ignore_ascii_case(&tokens[1])).unwrap_or(false)
        }) else {
          eprintln!("error: nickname '{}' が見つかりません", tokens[1]);
          return Ok(());
        };
        let Some(sid) = m.get("session_id").and_then(|v| v.as_str()) else {
          eprintln!("error: '{}' に session_id がありません", tokens[1]);
          return Ok(());
        };
        eprintln!("  nickname '{}' → session_id: {}", tokens[1], sid);
        sid.to_string()
      };

      let mut conn = match pipe_client::connect(pipe_name, timeout_ms).await {
        Ok(c) => c,
        Err(_) => return Ok(()),
      };
      let args = json!({ "session_id": resolved_session_id, "target_state": target_state, "timeout_ms": wait_timeout_ms });
      let Some(resp) = conn.call_tool("pane/wait_state", args).await? else {
        eprintln!("error: no response");
        return Ok(());
      };
      if let Some(err) = resp.get("error") {
        eprintln!("server error: {}", err.get("message").and_then(|v| v.as_str()).unwrap_or(""));
        return Ok(());
      }
      let state = resp.pointer("/result/structuredContent/state").and_then(|v| v.as_str()).unwrap_or("?");
      let elapsed = resp.pointer("/result/structuredContent/elapsed_ms").and_then(|v| v.as_i64());
      println!("state: {state}, elapsed_ms: {}", elapsed.map(|e| e.to_string()).unwrap_or_else(|| "?".to_string()));
    }
    other => eprintln!("unknown command: {other} (type 'help')"),
  }
  Ok(())
}

fn uuid_like(s: &str) -> bool {
  uuid::Uuid::parse_str(s).is_ok()
}

fn split_repl_line(line: &str) -> Vec<String> {
  let mut result = Vec::new();
  let mut cur = String::new();
  let mut in_quotes = false;
  for ch in line.chars() {
    if ch == '"' {
      in_quotes = !in_quotes;
      continue;
    }
    if ch.is_whitespace() && !in_quotes {
      if !cur.is_empty() {
        result.push(std::mem::take(&mut cur));
      }
      continue;
    }
    cur.push(ch);
  }
  if !cur.is_empty() {
    result.push(cur);
  }
  result
}

fn print_repl_help() {
  println!(
    r#"  list                              - participants 一覧 (JSON)
  send <nick> <message...>          - 指定 nickname に送信 (入力待ち優先)
  send <nick> --all <message...>    - 同 nickname の全インスタンスに送信
  send --broadcast <message...>     - 登録済み全 nickname に送信
  peek [<nick>]                     - 配信待ちキューを覗き見 (nickname 指定可)
  open [<profile-name>]             - ペインを新規起動 → session_id を返す
  close <session-id> [--force]      - ペインを閉じる
  wait <session-id|nickname> [idle|attention] [--timeout-ms N]
                                    - 指定セッションが状態になるまで待機
                                      nickname 指定時は list で session_id を自動解決
  help / ?                          - このヘルプ
  quit / exit / Ctrl+Z+Enter        - 終了

  メッセージ / プロファイル名にスペースを含めたいときは "..." で囲む
  例: send claude "hello world from REPL"
       open "Claude Code"
       wait d4f7a2b1-... idle --timeout-ms 60000
       wait claude idle
"#
  );
}

// ---- send subcommand ----

async fn run_send(rest: &[String], pipe_name: &str, timeout_ms: u64) -> std::io::Result<i32> {
  let send_args: Vec<&String> = rest.iter().enumerate().filter(|(i, _)| !is_common_option(rest, *i)).map(|(_, a)| a).skip(1).collect();

  let mut broadcast = false;
  let mut mode = "first".to_string();
  let mut positional: Vec<String> = Vec::new();
  let mut i = 0;
  while i < send_args.len() {
    let a = send_args[i].as_str();
    if a == "--broadcast" {
      broadcast = true;
      i += 1;
      continue;
    }
    if a == "--all" {
      mode = "all".to_string();
      i += 1;
      continue;
    }
    if a == "--mode" && i + 1 < send_args.len() {
      mode = send_args[i + 1].clone();
      i += 2;
      continue;
    }
    positional.push(send_args[i].clone());
    i += 1;
  }

  let nickname: Option<String>;
  let message: String;
  if broadcast {
    nickname = None;
    message = if !positional.is_empty() { positional.join(" ") } else { read_all_stdin() };
  } else {
    if positional.is_empty() {
      eprintln!("amm-mcp send: <nickname> が必要です (--broadcast を指定する場合は除く)");
      return Ok(EXIT_BAD_ARGS);
    }
    nickname = Some(positional[0].clone());
    message = if positional.len() > 1 { positional[1..].join(" ") } else { read_all_stdin() };
  }

  if message.is_empty() {
    eprintln!("amm-mcp send: メッセージが空です (引数または stdin から指定してください)");
    return Ok(EXIT_BAD_ARGS);
  }

  let mut conn = match pipe_client::connect(pipe_name, timeout_ms).await {
    Ok(c) => c,
    Err(e) if e.kind() == std::io::ErrorKind::TimedOut || e.kind() == std::io::ErrorKind::NotFound => {
      eprintln!("amm-mcp: amm GUI に接続できませんでした (pipe={pipe_name})。GUI を起動してから再試行してください。");
      return Ok(EXIT_NO_SERVER);
    }
    Err(e) => return Err(e),
  };

  let mut args = json!({ "message": message, "mode": mode });
  if !broadcast {
    args["recipient"] = json!(nickname);
  }

  let Some(resp) = conn.call_tool("send_message", args).await? else { return Ok(EXIT_MCP_ERROR) };
  if let Some(err) = resp.get("error") {
    eprintln!("amm-mcp: server error: {}", err.get("message").and_then(|v| v.as_str()).unwrap_or(""));
    return Ok(EXIT_MCP_ERROR);
  }

  let result = resp.pointer("/result/structuredContent");
  let delivered = result.and_then(|r| r.get("delivered_count")).and_then(|v| v.as_i64()).unwrap_or(0);
  let queued = result.and_then(|r| r.get("queued_count")).and_then(|v| v.as_i64()).unwrap_or(0);
  let recipients: Vec<String> =
    result.and_then(|r| r.get("recipients")).and_then(|v| v.as_array()).map(|a| a.iter().filter_map(|v| v.as_str().map(String::from)).collect()).unwrap_or_default();
  eprintln!("delivered={delivered} queued={queued} recipients=[{}]", recipients.join(","));
  Ok(EXIT_OK)
}

// ---- notify subcommand ----

async fn run_notify(rest: &[String], pipe_name: &str) -> std::io::Result<i32> {
  let token = std::env::var("AMM_NOTIFY_ID").ok();
  let Some(token) = token.filter(|t| !t.is_empty()) else { return Ok(EXIT_OK) };

  let args: Vec<&String> = rest.iter().enumerate().filter(|(i, _)| !is_common_option(rest, *i)).map(|(_, a)| a).skip(1).collect();
  let mut state_arg: Option<String> = None;
  let mut source: Option<String> = None;
  let mut positional: Vec<String> = Vec::new();
  let mut i = 0;
  while i < args.len() {
    if args[i] == "--state" && i + 1 < args.len() {
      state_arg = Some(args[i + 1].clone());
      i += 2;
      continue;
    }
    if args[i] == "--source" && i + 1 < args.len() {
      source = Some(args[i + 1].clone());
      i += 2;
      continue;
    }
    positional.push(args[i].clone());
    i += 1;
  }

  let state = match state_arg {
    Some(s) => Some(s),
    None => {
      let payload = try_parse_notify_payload(&positional);
      notify_mapper::map_state(payload.as_ref())
    }
  };
  let Some(state) = state else { return Ok(EXIT_OK) };

  let has_explicit_timeout = rest.iter().any(|a| a.eq_ignore_ascii_case("--connect-timeout"));
  let timeout_ms = if has_explicit_timeout { resolve_timeout(rest) } else { 2000 };

  // Best-effort: any failure here is silent (hook must never fail the CLI).
  let outcome: std::io::Result<()> = async {
    let mut conn = pipe_client::connect(pipe_name, timeout_ms).await?;
    let req = json!({
      "jsonrpc": "2.0", "id": 1, "method": "amm/notify",
      "params": { "token": token, "state": state, "source": source },
    });
    conn.write_line(&req).await?;
    let _ = conn.read_line().await;
    Ok(())
  }
  .await;
  let _ = outcome;
  Ok(EXIT_OK)
}

fn try_parse_notify_payload(positional: &[String]) -> Option<Value> {
  if !std::io::stdin().is_terminal() {
    let mut text = String::new();
    if std::io::stdin().read_to_string(&mut text).is_ok() && !text.trim().is_empty() {
      if let Ok(v) = serde_json::from_str::<Value>(&text) {
        if v.is_object() {
          return Some(v);
        }
      }
    }
  }
  for p in positional.iter().rev() {
    if let Ok(v) = serde_json::from_str::<Value>(p) {
      if v.is_object() {
        return Some(v);
      }
    }
  }
  None
}

// ---- approve subcommand ----

async fn run_approve(rest: &[String], pipe_name: &str) -> std::io::Result<i32> {
  let token = std::env::var("AMM_NOTIFY_ID").ok();
  let Some(token) = token.filter(|t| !t.is_empty()) else { return Ok(EXIT_OK) };

  let args: Vec<&String> = rest.iter().enumerate().filter(|(i, _)| !is_common_option(rest, *i)).map(|(_, a)| a).skip(1).collect();
  let mut source: Option<String> = None;
  let mut i = 0;
  while i < args.len() {
    if args[i] == "--source" && i + 1 < args.len() {
      source = Some(args[i + 1].clone());
    }
    i += 1;
  }

  let mut payload: Option<Value> = None;
  if !std::io::stdin().is_terminal() {
    let mut text = String::new();
    if std::io::stdin().read_to_string(&mut text).is_ok() {
      payload = serde_json::from_str(&text).ok();
    }
  }
  let tool_name = payload
    .as_ref()
    .and_then(|p| p.get("tool_name").or_else(|| p.get("toolName")))
    .and_then(|v| v.as_str())
    .unwrap_or("(unknown)")
    .to_string();
  let tool_input = payload.as_ref().and_then(|p| p.get("tool_input").or_else(|| p.get("toolArgs"))).cloned();

  // Any failure (GUI absent / disconnect / parse) -> silent "no decision".
  let result: std::io::Result<Option<String>> = async {
    let mut conn = pipe_client::connect(pipe_name, 2000).await?;
    let req = json!({
      "jsonrpc": "2.0", "id": 1, "method": "amm/approval",
      "params": { "token": token, "toolName": tool_name, "toolInput": tool_input },
    });
    conn.write_line(&req).await?;
    let Some(line) = conn.read_line_timeout(55_000).await? else { return Ok(None) };
    let decision = serde_json::from_str::<Value>(&line).ok().and_then(|v| v.pointer("/result/decision").and_then(|d| d.as_str()).map(String::from));
    Ok(decision)
  }
  .await;

  if let Ok(Some(decision)) = result {
    if decision == "allow" || decision == "deny" {
      println!("{}", build_approve_output(source.as_deref(), &decision));
    }
  }
  Ok(EXIT_OK)
}

fn build_approve_output(source: Option<&str>, decision: &str) -> String {
  const DENY_MESSAGE: &str = "Denied by the user via amm Approval Hub.";
  if source.map(|s| s.eq_ignore_ascii_case("copilot")).unwrap_or(false) {
    let v = if decision == "allow" { json!({ "behavior": "allow" }) } else { json!({ "behavior": "deny", "message": DENY_MESSAGE }) };
    return v.to_string();
  }
  let decision_json =
    if decision == "allow" { json!({ "behavior": "allow" }) } else { json!({ "behavior": "deny", "message": DENY_MESSAGE }) };
  json!({ "hookSpecificOutput": { "hookEventName": "PermissionRequest", "decision": decision_json } }).to_string()
}

// ---- list subcommand ----

async fn run_list(pipe_name: &str, timeout_ms: u64) -> std::io::Result<i32> {
  let mut conn = match pipe_client::connect(pipe_name, timeout_ms).await {
    Ok(c) => c,
    Err(e) if e.kind() == std::io::ErrorKind::TimedOut || e.kind() == std::io::ErrorKind::NotFound => {
      eprintln!("amm-mcp: amm GUI に接続できませんでした (pipe={pipe_name})。GUI を起動してから再試行してください。");
      return Ok(EXIT_NO_SERVER);
    }
    Err(e) => return Err(e),
  };
  let Some(resp) = conn.call_tool("list_participants", json!({})).await? else { return Ok(EXIT_MCP_ERROR) };
  if let Some(err) = resp.get("error") {
    eprintln!("amm-mcp: server error: {}", err.get("message").and_then(|v| v.as_str()).unwrap_or(""));
    return Ok(EXIT_MCP_ERROR);
  }
  let participants = resp.pointer("/result/structuredContent/participants").cloned().unwrap_or(json!([]));
  println!("{}", serde_json::to_string_pretty(&participants).unwrap_or_default());
  Ok(EXIT_OK)
}

// ---- helpers ----

fn read_all_stdin() -> String {
  if std::io::stdin().is_terminal() {
    return String::new();
  }
  let mut text = String::new();
  if std::io::stdin().read_to_string(&mut text).is_err() {
    return String::new();
  }
  text.trim_end_matches(['\r', '\n']).to_string()
}

enum Mode {
  Bridge,
  Repl,
  Send,
  List,
  Notify,
  Approve,
  Help,
}

fn resolve_mode(args: &[String]) -> (Mode, &[String]) {
  if args.is_empty() {
    return if std::io::stdin().is_terminal() { (Mode::Repl, args) } else { (Mode::Bridge, args) };
  }
  match args[0].to_lowercase().as_str() {
    "send" => (Mode::Send, args),
    "list" => (Mode::List, args),
    "notify" => (Mode::Notify, args),
    "approve" => (Mode::Approve, args),
    "repl" => (Mode::Repl, &args[1..]),
    "--bridge" => (Mode::Bridge, &args[1..]),
    "--help" | "-h" | "/?" => (Mode::Help, args),
    _ if args[0].starts_with("--") => (Mode::Bridge, args),
    _ => (Mode::Help, args),
  }
}

fn resolve_pipe_name(args: &[String]) -> String {
  for i in 0..args.len().saturating_sub(1) {
    if args[i].eq_ignore_ascii_case("--pipe-name") {
      return args[i + 1].clone();
    }
  }
  pipe_client::default_pipe_name()
}

fn resolve_timeout(args: &[String]) -> u64 {
  for i in 0..args.len().saturating_sub(1) {
    if args[i].eq_ignore_ascii_case("--connect-timeout") {
      if let Ok(ms) = args[i + 1].parse::<u64>() {
        return ms;
      }
    }
  }
  5000
}

fn is_common_option(args: &[String], i: usize) -> bool {
  if args[i].eq_ignore_ascii_case("--pipe-name") || args[i].eq_ignore_ascii_case("--connect-timeout") {
    return true;
  }
  if i > 0 && (args[i - 1].eq_ignore_ascii_case("--pipe-name") || args[i - 1].eq_ignore_ascii_case("--connect-timeout")) {
    return true;
  }
  false
}

fn print_usage() {
  eprintln!(
    r#"amm-mcp.exe — amm MCP server / CLI

使い方:
  amm-mcp.exe                          引数なし: 端末からなら REPL、stdin が
                                       redirect されているなら MCP stdio bridge
  amm-mcp.exe repl                     REPL を明示起動 (list/send/peek/help/quit)
  amm-mcp.exe --bridge                 MCP stdio bridge を明示起動
  amm-mcp.exe send <nickname> [msg]            指定 nickname へ送信 (mode=first、入力待ち優先)
  amm-mcp.exe send <nickname> --all [msg]      同 nickname を持つ全ペインに送信
  amm-mcp.exe send --broadcast [msg]           nickname 登録済みの全ペインに送信
  amm-mcp.exe list                             参加者一覧を JSON で stdout に出力
  amm-mcp.exe notify [--state <s>] [--source <l>]  CLI hook 用: 状態を amm GUI へ通知
                                               (環境変数 AMM_NOTIFY_ID 必須。無ければ no-op。
                                                payload は stdin / argv 末尾の JSON を自動判別)
  amm-mcp.exe approve [--source <l>]           CLI hook (Claude: PermissionRequest /
                                               Copilot: permissionRequest) 用: 許可要求を
                                               amm のポップアップへ転送し回答を待つ
                                               (AMM_NOTIFY_ID 必須。無回答は出力なし exit 0。
                                                --source copilot は {{"behavior": ...}} 形式で応答)
  msg を省略すると stdin から読み込む

オプション (全モード):
  --pipe-name <name>       既定 amm-mcp-{{ユーザ名}}
  --connect-timeout <ms>   既定 5000、0 で無制限

終了コード: 0=成功 / 1=引数不正 / 2=GUI 未起動 / 3=IO / 4=MCP エラー
"#
  );
}

#[cfg(test)]
mod tests {
  use super::*;

  #[test]
  fn split_repl_line_handles_quotes() {
    let tokens = split_repl_line(r#"send claude "hello world""#);
    assert_eq!(tokens, vec!["send", "claude", "hello world"]);
  }

  #[test]
  fn build_approve_output_copilot_allow() {
    assert_eq!(build_approve_output(Some("copilot"), "allow"), r#"{"behavior":"allow"}"#);
  }

  #[test]
  fn build_approve_output_claude_deny() {
    let out = build_approve_output(None, "deny");
    assert!(out.contains("hookSpecificOutput"));
    assert!(out.contains("PermissionRequest"));
    assert!(out.contains("\"deny\""));
  }

  #[test]
  fn is_common_option_matches_flag_and_value() {
    let args = vec!["send".to_string(), "--pipe-name".to_string(), "foo".to_string(), "claude".to_string()];
    assert!(is_common_option(&args, 1));
    assert!(is_common_option(&args, 2));
    assert!(!is_common_option(&args, 0));
    assert!(!is_common_option(&args, 3));
  }
}
