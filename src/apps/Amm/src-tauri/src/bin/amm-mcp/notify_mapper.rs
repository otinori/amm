// Rust port of src/apps/Amm.Mcp/NotifyPayloadMapper.cs. Normalizes CLI hook
// payloads (Claude Code / Copilot CLI stdin JSON, Codex CLI argv JSON) into
// amm's state vocabulary (idle / attention / busy). None = ignore this event.
use serde_json::Value;

pub fn map_state(payload: Option<&Value>) -> Option<String> {
  let Some(payload) = payload else { return Some("idle".to_string()) };

  let codex_type = payload.get("type").and_then(|v| v.as_str());
  if codex_type.map(|t| t.eq_ignore_ascii_case("agent-turn-complete")).unwrap_or(false) {
    return Some("idle".to_string());
  }

  let event_name = payload.get("hook_event_name").and_then(|v| v.as_str());
  if event_name.map(|e| e.eq_ignore_ascii_case("Stop")).unwrap_or(false) {
    return Some("idle".to_string());
  }

  if event_name.map(|e| e.eq_ignore_ascii_case("Notification")).unwrap_or(false) {
    let nt = payload.get("notification_type").and_then(|v| v.as_str()).map(|s| s.to_lowercase());
    return match nt.as_deref() {
      Some("idle_prompt") | Some("agent_idle") | Some("agent_completed") => Some("idle".to_string()),
      Some("permission_prompt") | Some("elicitation_dialog") => Some("attention".to_string()),
      _ => None,
    };
  }

  if event_name.is_none() && codex_type.is_none() {
    return Some("idle".to_string());
  }
  None
}

#[cfg(test)]
mod tests {
  use super::*;
  use serde_json::json;

  #[test]
  fn no_payload_is_idle() {
    assert_eq!(map_state(None), Some("idle".to_string()));
  }

  #[test]
  fn codex_turn_complete_is_idle() {
    assert_eq!(map_state(Some(&json!({"type": "agent-turn-complete"}))), Some("idle".to_string()));
  }

  #[test]
  fn claude_stop_is_idle() {
    assert_eq!(map_state(Some(&json!({"hook_event_name": "Stop"}))), Some("idle".to_string()));
  }

  #[test]
  fn notification_permission_prompt_is_attention() {
    assert_eq!(
      map_state(Some(&json!({"hook_event_name": "Notification", "notification_type": "permission_prompt"}))),
      Some("attention".to_string())
    );
  }

  #[test]
  fn notification_shell_completed_is_ignored() {
    assert_eq!(map_state(Some(&json!({"hook_event_name": "Notification", "notification_type": "shell_completed"}))), None);
  }

  #[test]
  fn unrecognized_payload_shape_is_idle() {
    assert_eq!(map_state(Some(&json!({"foo": "bar"}))), Some("idle".to_string()));
  }
}
