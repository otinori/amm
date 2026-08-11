// HookCliRegistrar (spec: hook-cli). Rust port of
// src/apps/Amm/Core/Mcp/HookCliRegistrar.cs, read directly to mirror its
// exact file shapes (JSON hook structures, self-guarded command strings,
// the Codex TOML line-editing approach) rather than guess at them.
use regex::Regex;
use serde_json::{json, Value};
use std::path::{Path, PathBuf};
use std::sync::OnceLock;

#[derive(Clone, Copy, PartialEq, Eq, Debug, serde::Serialize, serde::Deserialize)]
#[serde(rename_all = "camelCase")]
pub enum CliKind {
  ClaudeCode,
  Codex,
  CopilotCli,
  Antigravity,
}

pub fn config_path(kind: CliKind, home_dir: &Path) -> PathBuf {
  match kind {
    CliKind::ClaudeCode => home_dir.join(".claude").join("settings.json"),
    CliKind::Codex => home_dir.join(".codex").join("config.toml"),
    CliKind::CopilotCli => home_dir.join(".copilot").join("hooks").join("amm-hooks.json"),
    CliKind::Antigravity => home_dir.join(".antigravity").join("hooks").join("amm-hooks.json"),
  }
}

fn is_amm_notify_command(command: &str) -> bool {
  // Matches both "amm-mcp.exe" (Windows) and bare "amm-mcp" (macOS/Unix,
  // no extension) - found via add-macos-support's real-machine hook-cli
  // verification: with the ".exe"-only check, amm could never recognize
  // its own existing registration on macOS, so re-registration would
  // neither detect nor dedupe an already-registered hook.
  let lower = command.to_lowercase();
  lower.contains("amm-mcp") && (lower.contains("notify") || lower.contains("approve"))
}

// security: code-review 2026-07-26 finding (hook_cli.rs cmd.exe metachar
// injection, same class as H-1/H-2). register_claude/register_copilot_like
// splice `mcp_exe_path` into a `cmd /c if exist "..." "..." ...` command
// string that gets written into the target CLI's own hook config; that
// tool then executes the string via its own shell invocation, which we
// don't control and can't verify the exact quoting/re-parsing behavior of
// (unlike H-1, where we could empirically test our own spawn path). Rather
// than guess at an escaping scheme that might not survive an unknown
// downstream shell layer, reject registration outright if the path
// contains a character that could let it break out of the `"..."`
// wrapping (cmd.exe metacharacters, or a literal quote which - as
// discovered while fixing H-1 - Windows paths can't legitimately contain
// anyway). In normal operation `mcp_exe_path` is the installed amm-mcp.exe
// location, not attacker-controlled input, so this should never actually
// trigger; it's a fail-closed guard in case that assumption is ever wrong
// (e.g. install-path selection UI, unusual environment).
#[cfg(windows)]
fn validate_mcp_exe_path_for_cmd_wrapping(mcp_exe_path: &str) -> Result<(), String> {
  if mcp_exe_path.chars().any(|c| "\"&|<>^%".contains(c)) {
    return Err(format!(
      "amm-mcp.exe path contains a character that cannot be safely embedded in a cmd.exe hook command: {mcp_exe_path:?}"
    ));
  }
  Ok(())
}

// macOS/Unix delta (add-macos-support, found via real-machine hook-cli
// verification): register_claude/register_copilot_like below were
// unconditionally building a `cmd /c if exist "..." "..." <args>` hook
// command - a Windows cmd.exe batch idiom with no macOS equivalent at
// all (`cmd` doesn't exist there), so every registered hook would fail
// outright ("command not found") the instant the target CLI tried to
// invoke it. Fixed with the POSIX shell equivalent
// (`test -x "..." && "..." <args>`, same "only run if the path still
// exists" defense against a stale/moved binary). The character set
// disallowed here differs from the cmd.exe validator above since the
// path sits inside a *double-quoted* POSIX shell string, where `$`,
// backtick, and backslash all remain live (unlike cmd.exe's own
// metacharacter set).
#[cfg(not(windows))]
fn validate_mcp_exe_path_for_cmd_wrapping(mcp_exe_path: &str) -> Result<(), String> {
  if mcp_exe_path.chars().any(|c| "\"$`\\".contains(c)) {
    return Err(format!(
      "amm-mcp path contains a character that cannot be safely embedded in a shell hook command: {mcp_exe_path:?}"
    ));
  }
  Ok(())
}

fn build_hook_command(mcp_exe_path: &str, sub_command: &str) -> String {
  #[cfg(windows)]
  {
    format!("cmd /c if exist \"{mcp_exe_path}\" \"{mcp_exe_path}\" {sub_command}")
  }
  #[cfg(not(windows))]
  {
    format!("test -x \"{mcp_exe_path}\" && \"{mcp_exe_path}\" {sub_command}")
  }
}

fn extract_exe_path(command: &str) -> String {
  static QUOTED: OnceLock<Regex> = OnceLock::new();
  let re = QUOTED.get_or_init(|| Regex::new(r#""([^"]*amm-mcp(?:\.exe)?)""#).unwrap());
  if let Some(m) = re.captures(command) {
    return m[1].to_string();
  }
  command.split(' ').next().unwrap_or(command).to_string()
}

fn write_atomic(path: &Path, content: &str) -> std::io::Result<()> {
  if let Some(parent) = path.parent() {
    std::fs::create_dir_all(parent)?;
  }
  let tmp = path.with_extension("amm-tmp");
  std::fs::write(&tmp, content)?;
  std::fs::rename(&tmp, path)
}

// ---- Claude Code (~/.claude/settings.json) ----

const CLAUDE_HOOK_ENTRIES: &[(&str, &str, u32)] =
  &[("Stop", "notify --source claude", 10), ("Notification", "notify --source claude", 10), ("PermissionRequest", "approve", 60)];

pub fn get_claude_command(path: &Path) -> Option<String> {
  let text = std::fs::read_to_string(path).ok()?;
  let root: Value = serde_json::from_str(&text).ok()?;
  let hooks = root.get("hooks")?.as_object()?;
  for (event, _, _) in CLAUDE_HOOK_ENTRIES {
    let Some(groups) = hooks.get(*event).and_then(|v| v.as_array()) else { continue };
    for group in groups {
      if let Some(cmd) = find_amm_command_claude(group) {
        return Some(extract_exe_path(&cmd));
      }
    }
  }
  None
}

fn find_amm_command_claude(group: &Value) -> Option<String> {
  let cmds = group.get("hooks")?.as_array()?;
  for c in cmds {
    if let Some(cmd) = c.get("command").and_then(|v| v.as_str()) {
      if is_amm_notify_command(cmd) {
        return Some(cmd.to_string());
      }
    }
  }
  None
}

pub fn register_claude(path: &Path, mcp_exe_path: &str) -> Result<(), String> {
  validate_mcp_exe_path_for_cmd_wrapping(mcp_exe_path)?;
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
  if !root_obj.get("hooks").map(|v| v.is_object()).unwrap_or(false) {
    root_obj.insert("hooks".to_string(), json!({}));
  }
  let hooks = root_obj.get_mut("hooks").unwrap().as_object_mut().unwrap();

  for (event, sub_command, timeout_sec) in CLAUDE_HOOK_ENTRIES {
    let groups = hooks.entry(event.to_string()).or_insert_with(|| json!([]));
    if !groups.is_array() {
      *groups = json!([]);
    }
    let arr = groups.as_array_mut().unwrap();
    arr.retain(|g| find_amm_command_claude(g).is_none());
    arr.push(json!({
      "hooks": [{
        "type": "command",
        "command": build_hook_command(mcp_exe_path, sub_command),
        "timeout": timeout_sec,
      }]
    }));
  }

  let content = serde_json::to_string_pretty(&root).map_err(|e| e.to_string())? + "\n";
  write_atomic(path, &content).map_err(|e| e.to_string())
}

pub fn unregister_claude(path: &Path) -> Result<(), String> {
  if !path.exists() {
    return Ok(());
  }
  let text = std::fs::read_to_string(path).map_err(|e| e.to_string())?;
  let mut root: Value = serde_json::from_str(&text).map_err(|e| format!("{}: {e}", path.display()))?;
  let Some(root_obj) = root.as_object_mut() else {
    return Err(format!("{} のルートが JSON オブジェクトではありません。", path.display()));
  };
  let Some(hooks) = root_obj.get_mut("hooks").and_then(|v| v.as_object_mut()) else { return Ok(()) };

  let mut changed = false;
  let events: Vec<String> = CLAUDE_HOOK_ENTRIES.iter().map(|(e, _, _)| e.to_string()).collect();
  for event in &events {
    let Some(groups) = hooks.get_mut(event).and_then(|v| v.as_array_mut()) else { continue };
    let before = groups.len();
    groups.retain(|g| find_amm_command_claude(g).is_none());
    if groups.len() != before {
      changed = true;
    }
    if groups.is_empty() {
      hooks.remove(event);
      changed = true;
    }
  }
  if !changed {
    return Ok(());
  }
  if hooks.is_empty() {
    root_obj.remove("hooks");
  }
  let content = serde_json::to_string_pretty(&root).map_err(|e| e.to_string())? + "\n";
  write_atomic(path, &content).map_err(|e| e.to_string())
}

// ---- Codex (~/.codex/config.toml) - line-based, matching the .NET
// implementation's own approach (no full TOML AST needed). ----

fn codex_notify_re() -> &'static Regex {
  static RE: OnceLock<Regex> = OnceLock::new();
  RE.get_or_init(|| Regex::new(r"^\s*notify\s*=").unwrap())
}
fn any_table_header_re() -> &'static Regex {
  static RE: OnceLock<Regex> = OnceLock::new();
  RE.get_or_init(|| Regex::new(r"^\s*\[").unwrap())
}

fn find_codex_notify_line(lines: &[String]) -> Option<usize> {
  for (i, line) in lines.iter().enumerate() {
    if any_table_header_re().is_match(line) {
      break;
    }
    if codex_notify_re().is_match(line) {
      return Some(i);
    }
  }
  None
}

pub fn get_codex_command(path: &Path) -> Option<String> {
  let text = std::fs::read_to_string(path).ok()?;
  let lines: Vec<String> = text.lines().map(String::from).collect();
  let idx = find_codex_notify_line(&lines)?;
  let line = &lines[idx];
  if !line.to_lowercase().contains("amm-mcp") {
    return None;
  }
  static EXE_RE: OnceLock<Regex> = OnceLock::new();
  let re = EXE_RE.get_or_init(|| Regex::new(r"(?i)'([^']*amm-mcp(?:\.exe)?)'").unwrap());
  re.captures(line).map(|m| m[1].to_string()).or(Some(String::new()))
}

fn toml_escape(s: &str) -> String {
  if s.contains('\'') {
    format!("\"{}\"", s.replace('\\', "\\\\").replace('"', "\\\""))
  } else {
    format!("'{s}'")
  }
}

pub fn register_codex(path: &Path, mcp_exe_path: &str) -> Result<(), String> {
  let mut lines: Vec<String> = if path.exists() {
    std::fs::read_to_string(path).map_err(|e| e.to_string())?.lines().map(String::from).collect()
  } else {
    Vec::new()
  };

  let existing = find_codex_notify_line(&lines);
  if let Some(idx) = existing {
    if !lines[idx].to_lowercase().contains("amm-mcp") {
      return Err(
        "~/.codex/config.toml に既存の notify 設定があるため登録できません。手動で notify を amm-mcp に変更するか、既存設定を退避してください。"
          .to_string(),
      );
    }
  }

  let new_notify = format!("notify = [{}, 'notify', '--source', 'codex']", toml_escape(mcp_exe_path));
  match existing {
    Some(idx) => lines[idx] = new_notify,
    None => {
      let insert_at = lines.iter().position(|l| any_table_header_re().is_match(l));
      match insert_at {
        Some(at) => {
          lines.insert(at, String::new());
          lines.insert(at, new_notify);
        }
        None => {
          while lines.last().map(|l| l.trim().is_empty()).unwrap_or(false) {
            lines.pop();
          }
          if !lines.is_empty() {
            lines.push(String::new());
          }
          lines.push(new_notify);
        }
      }
    }
  }

  ensure_codex_tui_notifications(&mut lines);
  write_atomic(path, &(lines.join("\n") + "\n")).map_err(|e| e.to_string())
}

const AMM_MARKER: &str = "# added by amm";

fn ensure_codex_tui_notifications(lines: &mut Vec<String>) {
  static TUI_HEADER: OnceLock<Regex> = OnceLock::new();
  static NOTIF_LINE: OnceLock<Regex> = OnceLock::new();
  let tui_header = TUI_HEADER.get_or_init(|| Regex::new(r"^\s*\[tui\]\s*(#.*)?$").unwrap());
  let notif_line = NOTIF_LINE.get_or_init(|| Regex::new(r"^\s*notifications\s*=").unwrap());

  let tui_start = lines.iter().position(|l| tui_header.is_match(l));
  match tui_start {
    Some(start) => {
      let mut end = start + 1;
      let mut has_notifications = false;
      while end < lines.len() && !any_table_header_re().is_match(&lines[end]) {
        if notif_line.is_match(&lines[end]) {
          has_notifications = true;
        }
        end += 1;
      }
      if has_notifications {
        return;
      }
      lines.insert(end, format!("notification_method = \"osc9\" {AMM_MARKER}"));
      lines.insert(end, format!("notifications = [\"agent-turn-complete\", \"approval-requested\"] {AMM_MARKER}"));
    }
    None => {
      while lines.last().map(|l| l.trim().is_empty()).unwrap_or(false) {
        lines.pop();
      }
      if !lines.is_empty() {
        lines.push(String::new());
      }
      lines.push("[tui]".to_string());
      // spec: hook-cli's MODIFIED "Codex への notify / TUI 通知登録" -
      // these specific values (matching the .NET original's
      // EnsureCodexTuiNotifications exactly) enable wait-detection's OSC 9
      // attention-detection channel, Codex's only approval-hub signaling
      // path since it has no blocking hook the way Claude Code does (found
      // written as `notifications = true` / `notification_method = "tui"` -
      // values the .NET original never wrote - in the source-diff parity audit).
      lines.push(format!("notifications = [\"agent-turn-complete\", \"approval-requested\"] {AMM_MARKER}"));
      lines.push(format!("notification_method = \"osc9\" {AMM_MARKER}"));
    }
  }
}

pub fn unregister_codex(path: &Path) -> Result<(), String> {
  if !path.exists() {
    return Ok(());
  }
  let mut lines: Vec<String> = std::fs::read_to_string(path).map_err(|e| e.to_string())?.lines().map(String::from).collect();
  let before = lines.len();
  lines.retain(|l| {
    let is_amm_notify = codex_notify_re().is_match(l) && l.to_lowercase().contains("amm-mcp");
    let is_amm_marker = l.contains(AMM_MARKER);
    !(is_amm_notify || is_amm_marker)
  });
  if lines.len() == before {
    return Ok(());
  }
  while lines.last().map(|l| l.trim().is_empty()).unwrap_or(false) {
    lines.pop();
  }
  let content = if lines.is_empty() { String::new() } else { lines.join("\n") + "\n" };
  write_atomic(path, &content).map_err(|e| e.to_string())
}

// ---- Copilot CLI / Antigravity (dedicated amm-owned JSON file) ----

pub fn get_copilot_like_command(path: &Path) -> Option<String> {
  let text = std::fs::read_to_string(path).ok()?;
  let root: Value = serde_json::from_str(&text).ok()?;
  let hooks = root.get("hooks")?.as_object()?;
  for (_, defs) in hooks {
    let Some(defs) = defs.as_array() else { continue };
    for def in defs {
      if let Some(cmd) = def.get("command").and_then(|v| v.as_str()) {
        if is_amm_notify_command(cmd) {
          return Some(extract_exe_path(cmd));
        }
      }
    }
  }
  None
}

pub fn register_copilot_like(path: &Path, mcp_exe_path: &str, source: &str) -> Result<(), String> {
  validate_mcp_exe_path_for_cmd_wrapping(mcp_exe_path)?;
  let root = json!({
    "version": 1,
    "hooks": {
      "agentStop": [{
        "type": "command",
        "command": build_hook_command(mcp_exe_path, &format!("notify --state idle --source {source}")),
        "timeoutSec": 10,
      }],
      "permissionRequest": [{
        "type": "command",
        "command": build_hook_command(mcp_exe_path, &format!("approve --source {source}")),
        "timeoutSec": 60,
      }],
    },
  });
  let content = serde_json::to_string_pretty(&root).map_err(|e| e.to_string())? + "\n";
  write_atomic(path, &content).map_err(|e| e.to_string())
}

pub fn unregister_copilot_like(path: &Path) -> Result<(), String> {
  if path.exists() {
    std::fs::remove_file(path).map_err(|e| e.to_string())?;
  }
  Ok(())
}

// ---- unified entry points ----

pub fn get_registered_command(kind: CliKind, home_dir: &Path) -> Option<String> {
  let path = config_path(kind, home_dir);
  match kind {
    CliKind::ClaudeCode => get_claude_command(&path),
    CliKind::Codex => get_codex_command(&path),
    CliKind::CopilotCli | CliKind::Antigravity => get_copilot_like_command(&path),
  }
}

pub fn is_registered(kind: CliKind, home_dir: &Path) -> bool {
  get_registered_command(kind, home_dir).is_some()
}

pub fn register(kind: CliKind, home_dir: &Path, mcp_exe_path: &str) -> Result<(), String> {
  let path = config_path(kind, home_dir);
  match kind {
    CliKind::ClaudeCode => register_claude(&path, mcp_exe_path),
    CliKind::Codex => register_codex(&path, mcp_exe_path),
    CliKind::CopilotCli => register_copilot_like(&path, mcp_exe_path, "copilot"),
    CliKind::Antigravity => register_copilot_like(&path, mcp_exe_path, "antigravity"),
  }
}

pub fn unregister(kind: CliKind, home_dir: &Path) -> Result<(), String> {
  let path = config_path(kind, home_dir);
  match kind {
    CliKind::ClaudeCode => unregister_claude(&path),
    CliKind::Codex => unregister_codex(&path),
    CliKind::CopilotCli | CliKind::Antigravity => unregister_copilot_like(&path),
  }
}

#[cfg(test)]
mod tests {
  use super::*;

  fn scratch_home(tag: &str) -> PathBuf {
    let dir = std::env::temp_dir().join(format!("amm-hookcli-test-{tag}-{}", std::process::id()));
    let _ = std::fs::remove_dir_all(&dir);
    std::fs::create_dir_all(&dir).unwrap();
    dir
  }

  #[cfg(not(windows))]
  #[test]
  fn build_hook_command_uses_posix_test_dash_x_not_cmd_exe() {
    // spec: hook-cli macOS/Unix delta - the previous unconditional
    // "cmd /c if exist ..." wrapping had no macOS equivalent (`cmd`
    // doesn't exist there), so every registered hook failed outright.
    let cmd = build_hook_command("/Applications/amm.app/Contents/Resources/amm-mcp", "notify --source claude");
    assert_eq!(cmd, "test -x \"/Applications/amm.app/Contents/Resources/amm-mcp\" && \"/Applications/amm.app/Contents/Resources/amm-mcp\" notify --source claude");
    assert!(!cmd.contains("cmd /c"), "must not fall back to the Windows cmd.exe idiom on this platform");
  }

  #[cfg(not(windows))]
  #[test]
  fn is_amm_notify_command_recognizes_extension_less_macos_path() {
    // Without this, amm could never recognize its own existing
    // registration on macOS (no ".exe" suffix), breaking idempotent
    // re-registration/dedup.
    assert!(is_amm_notify_command(
      "test -x \"/Applications/amm.app/Contents/Resources/amm-mcp\" && \"/Applications/amm.app/Contents/Resources/amm-mcp\" notify --source claude"
    ));
  }

  #[test]
  fn not_registered_when_file_missing() {
    let home = scratch_home("missing");
    assert!(!is_registered(CliKind::ClaudeCode, &home));
    assert!(!is_registered(CliKind::Codex, &home));
    assert!(!is_registered(CliKind::CopilotCli, &home));
  }

  #[test]
  fn register_rejects_mcp_exe_path_with_cmd_metacharacters() {
    // security: regression test for code-review 2026-07-26 finding
    // (hook_cli.rs cmd.exe metachar injection).
    let home = scratch_home("evil-path");
    let evil_path = "C:\\amm & calc.exe & \"amm-mcp.exe";
    assert!(register(CliKind::ClaudeCode, &home, evil_path).is_err());
    assert!(register(CliKind::CopilotCli, &home, evil_path).is_err());
    assert!(!is_registered(CliKind::ClaudeCode, &home), "a rejected registration must not write a partial/unsafe config");
    assert!(!is_registered(CliKind::CopilotCli, &home));
  }

  #[test]
  fn claude_coexists_with_user_hook_and_unregister_keeps_it() {
    let home = scratch_home("claude");
    let path = config_path(CliKind::ClaudeCode, &home);
    std::fs::create_dir_all(path.parent().unwrap()).unwrap();
    std::fs::write(
      &path,
      serde_json::to_string_pretty(&json!({
        "hooks": { "Stop": [{"hooks": [{"type": "command", "command": "my-own-tool.exe"}]}] }
      }))
      .unwrap(),
    )
    .unwrap();

    register(CliKind::ClaudeCode, &home, "C:/amm/amm-mcp.exe").unwrap();
    let root: Value = serde_json::from_str(&std::fs::read_to_string(&path).unwrap()).unwrap();
    let stop = root["hooks"]["Stop"].as_array().unwrap();
    assert_eq!(stop.len(), 2, "user's own entry must survive alongside amm's");
    assert!(root["hooks"]["Notification"].is_array());
    assert!(root["hooks"]["PermissionRequest"].is_array());

    // idempotent re-register with the same path must not duplicate.
    register(CliKind::ClaudeCode, &home, "C:/amm/amm-mcp.exe").unwrap();
    let root: Value = serde_json::from_str(&std::fs::read_to_string(&path).unwrap()).unwrap();
    assert_eq!(root["hooks"]["Stop"].as_array().unwrap().len(), 2);

    assert_eq!(get_claude_command(&path).unwrap(), "C:/amm/amm-mcp.exe");

    // re-register after an exe move replaces the single amm entry, no dup.
    register(CliKind::ClaudeCode, &home, "D:/moved/amm-mcp.exe").unwrap();
    let root: Value = serde_json::from_str(&std::fs::read_to_string(&path).unwrap()).unwrap();
    assert_eq!(root["hooks"]["Stop"].as_array().unwrap().len(), 2);
    assert_eq!(get_claude_command(&path).unwrap(), "D:/moved/amm-mcp.exe");

    unregister(CliKind::ClaudeCode, &home).unwrap();
    let root: Value = serde_json::from_str(&std::fs::read_to_string(&path).unwrap()).unwrap();
    let stop = root["hooks"]["Stop"].as_array().unwrap();
    assert_eq!(stop.len(), 1, "only amm's entry should be removed");
    assert!(root["hooks"].get("Notification").is_none(), "amm-only event key must be dropped");
    assert!(root["hooks"].get("PermissionRequest").is_none());
  }

  #[test]
  fn claude_replaces_unguarded_legacy_entry() {
    let home = scratch_home("claude-legacy");
    let path = config_path(CliKind::ClaudeCode, &home);
    std::fs::create_dir_all(path.parent().unwrap()).unwrap();
    std::fs::write(
      &path,
      serde_json::to_string_pretty(&json!({
        "hooks": { "Stop": [{"hooks": [{"type": "command", "command": "\"C:/old/amm-mcp.exe\" notify --source claude"}]}] }
      }))
      .unwrap(),
    )
    .unwrap();

    register(CliKind::ClaudeCode, &home, "C:/new/amm-mcp.exe").unwrap();
    let root: Value = serde_json::from_str(&std::fs::read_to_string(&path).unwrap()).unwrap();
    assert_eq!(root["hooks"]["Stop"].as_array().unwrap().len(), 1);
    assert_eq!(get_claude_command(&path).unwrap(), "C:/new/amm-mcp.exe");
  }

  #[test]
  fn codex_register_and_marker_only_unregister() {
    let home = scratch_home("codex");
    let path = config_path(CliKind::Codex, &home);
    std::fs::create_dir_all(path.parent().unwrap()).unwrap();
    std::fs::write(&path, "model = 'gpt'\n\n[tui]\ntheme = 'dark'\n").unwrap();

    register(CliKind::Codex, &home, "C:/amm/amm-mcp.exe").unwrap();
    let text = std::fs::read_to_string(&path).unwrap();
    assert!(text.contains("notify = ["));
    assert!(text.contains("notifications = [\"agent-turn-complete\", \"approval-requested\"] # added by amm"));
    assert!(text.contains("notification_method = \"osc9\" # added by amm"));
    assert!(text.contains("theme = 'dark'"), "user's [tui] key must survive");
    assert_eq!(get_codex_command(&path).unwrap(), "C:/amm/amm-mcp.exe");

    unregister(CliKind::Codex, &home).unwrap();
    let text = std::fs::read_to_string(&path).unwrap();
    assert!(!text.contains("notify = ["));
    assert!(!text.contains("added by amm"));
    assert!(text.contains("[tui]"));
    assert!(text.contains("theme = 'dark'"), "unregister must not touch user's own tui key");
  }

  #[test]
  fn codex_respects_user_notifications_setting() {
    let home = scratch_home("codex-notif");
    let path = config_path(CliKind::Codex, &home);
    std::fs::create_dir_all(path.parent().unwrap()).unwrap();
    std::fs::write(&path, "[tui]\nnotifications = false\n").unwrap();

    register(CliKind::Codex, &home, "C:/amm/amm-mcp.exe").unwrap();
    let text = std::fs::read_to_string(&path).unwrap();
    assert!(text.contains("notify = ["));
    assert!(text.contains("notifications = false"));
    assert!(!text.contains("added by amm"), "must not touch section when user already set notifications");
  }

  #[test]
  fn codex_rejects_foreign_notify() {
    let home = scratch_home("codex-collision");
    let path = config_path(CliKind::Codex, &home);
    std::fs::create_dir_all(path.parent().unwrap()).unwrap();
    let original = "notify = ['my-notifier.exe']\n";
    std::fs::write(&path, original).unwrap();

    let err = register(CliKind::Codex, &home, "C:/amm/amm-mcp.exe");
    assert!(err.is_err());
    assert_eq!(std::fs::read_to_string(&path).unwrap(), original, "file must be untouched on collision");
  }

  #[test]
  fn copilot_full_rewrite_and_delete_on_unregister() {
    let home = scratch_home("copilot");
    let path = config_path(CliKind::CopilotCli, &home);

    register(CliKind::CopilotCli, &home, "C:/amm/amm-mcp.exe").unwrap();
    assert!(path.exists());
    assert_eq!(get_copilot_like_command(&path).unwrap(), "C:/amm/amm-mcp.exe");

    unregister(CliKind::CopilotCli, &home).unwrap();
    assert!(!path.exists());
    assert!(!is_registered(CliKind::CopilotCli, &home));
  }
}
