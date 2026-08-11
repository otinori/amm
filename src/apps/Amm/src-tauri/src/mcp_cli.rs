// McpCliRegistrar (spec: mcp-server, "CLI設定ファイルへのMCPサーバ登録").
// Rust port of src/apps/Amm/Core/Mcp/McpCliRegistrar.cs, read directly to
// mirror its exact file shapes. Distinct from hook_cli.rs's HookCliRegistrar
// (notify/approve hooks) - this registers amm-mcp.exe itself as an MCP
// server in each CLI's config, so external tools can call amm's tools
// (send_message etc.) through the standard MCP protocol. Found missing
// entirely in the phase 8.1 parity audit.
use regex::Regex;
use serde_json::{json, Value};
use std::path::{Path, PathBuf};
use std::sync::OnceLock;

pub const SERVER_NAME: &str = "amm";

#[derive(Clone, Copy, PartialEq, Eq, Debug, serde::Serialize, serde::Deserialize)]
#[serde(rename_all = "camelCase")]
pub enum McpCliKind {
  ClaudeCode,
  Codex,
  CopilotCli,
  Antigravity,
}

pub fn config_path(kind: McpCliKind, home_dir: &Path) -> PathBuf {
  match kind {
    McpCliKind::ClaudeCode => home_dir.join(".claude.json"),
    McpCliKind::Codex => home_dir.join(".codex").join("config.toml"),
    McpCliKind::CopilotCli => home_dir.join(".copilot").join("mcp-config.json"),
    McpCliKind::Antigravity => home_dir.join(".antigravity").join("mcp-config.json"),
  }
}

fn write_atomic(path: &Path, content: &str) -> std::io::Result<()> {
  if let Some(parent) = path.parent() {
    std::fs::create_dir_all(parent)?;
  }
  let tmp = path.with_extension("amm-tmp");
  std::fs::write(&tmp, content)?;
  std::fs::rename(&tmp, path)
}

fn toml_escape(s: &str) -> String {
  if !s.contains('\'') {
    return format!("'{s}'");
  }
  let mut out = String::with_capacity(s.len() + 8);
  out.push('"');
  for c in s.chars() {
    match c {
      '\\' => out.push_str("\\\\"),
      '"' => out.push_str("\\\""),
      '\n' => out.push_str("\\n"),
      '\r' => out.push_str("\\r"),
      '\t' => out.push_str("\\t"),
      _ => out.push(c),
    }
  }
  out.push('"');
  out
}

// ---- Claude Code (~/.claude.json) / Copilot CLI / Antigravity (dedicated
// mcp-config.json) - all plain {"mcpServers": {"amm": {...}}} JSON, only
// the per-entry shape differs (stdio vs local+tools). ----

fn get_json_command(path: &Path) -> Option<String> {
  let text = std::fs::read_to_string(path).ok()?;
  let root: Value = serde_json::from_str(&text).ok()?;
  let entry = root.get("mcpServers")?.get(SERVER_NAME)?;
  Some(entry.get("command").and_then(|v| v.as_str()).unwrap_or("").to_string())
}

fn register_json(kind: McpCliKind, path: &Path, mcp_exe_path: &str) -> Result<(), String> {
  let mut root: Value = if path.exists() {
    let text = std::fs::read_to_string(path).map_err(|e| e.to_string())?;
    serde_json::from_str(&text).map_err(|e| format!("{}: {e}", path.display()))?
  } else {
    json!({})
  };
  if !root.is_object() {
    return Err(format!("{} のルートが JSON オブジェクトではありません。", path.display()));
  }
  let root_obj = root.as_object_mut().unwrap();
  if !root_obj.get("mcpServers").map(|v| v.is_object()).unwrap_or(false) {
    root_obj.insert("mcpServers".to_string(), json!({}));
  }
  let servers = root_obj.get_mut("mcpServers").unwrap().as_object_mut().unwrap();

  let entry = if kind == McpCliKind::ClaudeCode {
    json!({ "type": "stdio", "command": mcp_exe_path, "args": [], "env": {} })
  } else {
    json!({ "type": "local", "command": mcp_exe_path, "args": [], "tools": ["*"] })
  };
  servers.insert(SERVER_NAME.to_string(), entry);

  let content = serde_json::to_string_pretty(&root).map_err(|e| e.to_string())? + "\n";
  write_atomic(path, &content).map_err(|e| e.to_string())
}

fn unregister_json(path: &Path) -> Result<(), String> {
  if !path.exists() {
    return Ok(());
  }
  let text = std::fs::read_to_string(path).map_err(|e| e.to_string())?;
  let mut root: Value = serde_json::from_str(&text).map_err(|e| format!("{}: {e}", path.display()))?;
  let Some(root_obj) = root.as_object_mut() else {
    return Err(format!("{} のルートが JSON オブジェクトではありません。", path.display()));
  };
  let Some(servers) = root_obj.get_mut("mcpServers").and_then(|v| v.as_object_mut()) else { return Ok(()) };
  if servers.remove(SERVER_NAME).is_none() {
    return Ok(());
  }
  let content = serde_json::to_string_pretty(&root).map_err(|e| e.to_string())? + "\n";
  write_atomic(path, &content).map_err(|e| e.to_string())
}

// ---- Codex (~/.codex/config.toml) - line-based, no TOML AST needed,
// matching the .NET original's own approach exactly. ----

fn section_header_re() -> &'static Regex {
  static RE: OnceLock<Regex> = OnceLock::new();
  RE.get_or_init(|| Regex::new(&format!(r#"^\s*\[mcp_servers\.("{SERVER_NAME}"|{SERVER_NAME})\]\s*(#.*)?$"#)).unwrap())
}
fn any_table_header_re() -> &'static Regex {
  static RE: OnceLock<Regex> = OnceLock::new();
  RE.get_or_init(|| Regex::new(r"^\s*\[").unwrap())
}
fn command_line_re() -> &'static Regex {
  static RE: OnceLock<Regex> = OnceLock::new();
  RE.get_or_init(|| Regex::new(r#"^\s*command\s*=\s*(?:'(?P<lit>[^']*)'|"(?P<basic>[^"]*)")\s*(#.*)?$"#).unwrap())
}

// Returns (start, end-exclusive, command) for the [mcp_servers.amm]
// section, or start=None if not found.
fn find_toml_section(lines: &[String]) -> (Option<usize>, usize, Option<String>) {
  for (i, line) in lines.iter().enumerate() {
    if !section_header_re().is_match(line) {
      continue;
    }
    let mut end = i + 1;
    let mut command = None;
    while end < lines.len() && !any_table_header_re().is_match(&lines[end]) {
      if let Some(caps) = command_line_re().captures(&lines[end]) {
        command = Some(if let Some(lit) = caps.name("lit") {
          lit.as_str().to_string()
        } else {
          caps.name("basic").map(|m| m.as_str().replace("\\\\", "\\")).unwrap_or_default()
        });
      }
      end += 1;
    }
    return (Some(i), end, command);
  }
  (None, 0, None)
}

fn get_codex_command(path: &Path) -> Option<String> {
  let text = std::fs::read_to_string(path).ok()?;
  let lines: Vec<String> = text.lines().map(String::from).collect();
  let (start, _, command) = find_toml_section(&lines);
  start.map(|_| command.unwrap_or_default())
}

fn register_codex(path: &Path, mcp_exe_path: &str) -> Result<(), String> {
  let mut lines: Vec<String> = if path.exists() {
    std::fs::read_to_string(path).map_err(|e| e.to_string())?.lines().map(String::from).collect()
  } else {
    Vec::new()
  };

  let (start, end, _) = find_toml_section(&lines);
  if let Some(start) = start {
    lines.drain(start..end);
  }

  while lines.last().map(|l| l.trim().is_empty()).unwrap_or(false) {
    lines.pop();
  }
  if !lines.is_empty() {
    lines.push(String::new());
  }
  lines.push(format!("[mcp_servers.{SERVER_NAME}]"));
  lines.push(format!("command = {}", toml_escape(mcp_exe_path)));
  lines.push("args = []".to_string());

  write_atomic(path, &(lines.join("\n") + "\n")).map_err(|e| e.to_string())
}

fn unregister_codex(path: &Path) -> Result<(), String> {
  if !path.exists() {
    return Ok(());
  }
  let mut lines: Vec<String> = std::fs::read_to_string(path).map_err(|e| e.to_string())?.lines().map(String::from).collect();
  let (start, end, _) = find_toml_section(&lines);
  let Some(mut start) = start else { return Ok(()) };
  lines.drain(start..end);
  while start > 0 && start < lines.len() && lines[start - 1].trim().is_empty() && lines[start].trim().is_empty() {
    lines.remove(start);
    start -= 1;
  }
  while lines.last().map(|l| l.trim().is_empty()).unwrap_or(false) {
    lines.pop();
  }
  let content = if lines.is_empty() { String::new() } else { lines.join("\n") + "\n" };
  write_atomic(path, &content).map_err(|e| e.to_string())
}

// ---- unified entry points ----

pub fn get_registered_command(kind: McpCliKind, home_dir: &Path) -> Option<String> {
  let path = config_path(kind, home_dir);
  match kind {
    McpCliKind::Codex => get_codex_command(&path),
    _ => get_json_command(&path),
  }
}

pub fn is_registered(kind: McpCliKind, home_dir: &Path) -> bool {
  get_registered_command(kind, home_dir).is_some()
}

pub fn register(kind: McpCliKind, home_dir: &Path, mcp_exe_path: &str) -> Result<(), String> {
  let path = config_path(kind, home_dir);
  match kind {
    McpCliKind::Codex => register_codex(&path, mcp_exe_path),
    _ => register_json(kind, &path, mcp_exe_path),
  }
}

pub fn unregister(kind: McpCliKind, home_dir: &Path) -> Result<(), String> {
  let path = config_path(kind, home_dir);
  match kind {
    McpCliKind::Codex => unregister_codex(&path),
    _ => unregister_json(&path),
  }
}

#[cfg(test)]
mod tests {
  use super::*;

  fn scratch_home(tag: &str) -> PathBuf {
    let dir = std::env::temp_dir().join(format!("amm-mcpcli-test-{tag}-{}", std::process::id()));
    let _ = std::fs::remove_dir_all(&dir);
    std::fs::create_dir_all(&dir).unwrap();
    dir
  }

  #[test]
  fn not_registered_when_file_missing() {
    let home = scratch_home("missing");
    assert!(!is_registered(McpCliKind::ClaudeCode, &home));
    assert!(!is_registered(McpCliKind::Codex, &home));
    assert!(!is_registered(McpCliKind::CopilotCli, &home));
  }

  #[test]
  fn claude_register_creates_file_and_coexists_with_other_servers() {
    let home = scratch_home("claude");
    let path = config_path(McpCliKind::ClaudeCode, &home);
    std::fs::create_dir_all(path.parent().unwrap()).unwrap();
    std::fs::write(&path, serde_json::to_string_pretty(&json!({ "mcpServers": { "other": { "command": "other.exe" } } })).unwrap()).unwrap();

    register(McpCliKind::ClaudeCode, &home, "C:\\amm\\amm-mcp.exe").unwrap();
    let root: Value = serde_json::from_str(&std::fs::read_to_string(&path).unwrap()).unwrap();
    assert_eq!(root["mcpServers"]["amm"]["command"], "C:\\amm\\amm-mcp.exe");
    assert_eq!(root["mcpServers"]["amm"]["type"], "stdio");
    assert_eq!(root["mcpServers"]["other"]["command"], "other.exe", "other server must be preserved");
  }

  #[test]
  fn claude_register_on_missing_file_creates_new() {
    let home = scratch_home("claude-new");
    register(McpCliKind::ClaudeCode, &home, "C:\\amm\\amm-mcp.exe").unwrap();
    assert_eq!(get_registered_command(McpCliKind::ClaudeCode, &home).unwrap(), "C:\\amm\\amm-mcp.exe");
  }

  #[test]
  fn copilot_like_uses_local_type_with_tools() {
    let home = scratch_home("copilot");
    register(McpCliKind::CopilotCli, &home, "C:\\amm\\amm-mcp.exe").unwrap();
    let path = config_path(McpCliKind::CopilotCli, &home);
    let root: Value = serde_json::from_str(&std::fs::read_to_string(&path).unwrap()).unwrap();
    assert_eq!(root["mcpServers"]["amm"]["type"], "local");
    assert_eq!(root["mcpServers"]["amm"]["tools"][0], "*");
  }

  #[test]
  fn unregister_removes_only_amm_entry() {
    let home = scratch_home("claude-unreg");
    let path = config_path(McpCliKind::ClaudeCode, &home);
    std::fs::create_dir_all(path.parent().unwrap()).unwrap();
    std::fs::write(&path, serde_json::to_string_pretty(&json!({ "mcpServers": { "other": { "command": "other.exe" } } })).unwrap()).unwrap();
    register(McpCliKind::ClaudeCode, &home, "C:\\amm\\amm-mcp.exe").unwrap();

    unregister(McpCliKind::ClaudeCode, &home).unwrap();
    let root: Value = serde_json::from_str(&std::fs::read_to_string(&path).unwrap()).unwrap();
    assert!(root["mcpServers"].get("amm").is_none());
    assert_eq!(root["mcpServers"]["other"]["command"], "other.exe");
  }

  #[test]
  fn codex_register_and_unregister_roundtrip() {
    let home = scratch_home("codex");
    let path = config_path(McpCliKind::Codex, &home);
    std::fs::create_dir_all(path.parent().unwrap()).unwrap();
    std::fs::write(&path, "model = 'gpt'\n\n[other_table]\nkey = 1\n").unwrap();

    register(McpCliKind::Codex, &home, "C:\\amm\\amm-mcp.exe").unwrap();
    let text = std::fs::read_to_string(&path).unwrap();
    assert!(text.contains("[mcp_servers.amm]"));
    assert!(text.contains("command = 'C:\\amm\\amm-mcp.exe'"));
    assert!(text.contains("[other_table]"), "unrelated table must be preserved");
    assert_eq!(get_codex_command(&path).unwrap(), "C:\\amm\\amm-mcp.exe");

    // re-register with a new path replaces the single entry, no dup.
    register(McpCliKind::Codex, &home, "D:\\moved\\amm-mcp.exe").unwrap();
    let text = std::fs::read_to_string(&path).unwrap();
    assert_eq!(text.matches("[mcp_servers.amm]").count(), 1);
    assert_eq!(get_codex_command(&path).unwrap(), "D:\\moved\\amm-mcp.exe");

    unregister(McpCliKind::Codex, &home).unwrap();
    let text = std::fs::read_to_string(&path).unwrap();
    assert!(!text.contains("mcp_servers.amm"));
    assert!(text.contains("[other_table]"));
  }

  #[test]
  fn codex_toml_escape_uses_double_quote_when_path_has_apostrophe() {
    let home = scratch_home("codex-quote");
    let path = config_path(McpCliKind::Codex, &home);
    register(McpCliKind::Codex, &home, "C:\\it's\\amm-mcp.exe").unwrap();
    let text = std::fs::read_to_string(&path).unwrap();
    assert!(text.contains(r#"command = "C:\\it's\\amm-mcp.exe""#));
  }
}
