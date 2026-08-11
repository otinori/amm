// InputHistory (spec: input-history). Full-duplicate-removal add, capped
// entry count, persisted across restarts. The cursor-based NavigateUp/Down
// API from the .NET version is intentionally not ported: the spec itself
// documents that the current UI never calls it (superseded by the Ctrl+H
// dropdown), so porting it would be dead code.
use serde::{Deserialize, Serialize};
use std::collections::VecDeque;
use std::path::PathBuf;
use std::sync::Mutex;

const DEFAULT_MAX_ENTRIES: usize = 500;

#[derive(Default)]
pub struct InputHistory {
  entries: Mutex<VecDeque<String>>,
  max_entries: Mutex<usize>,
}

// `entries` accepts the legacy .NET WinForms alias `Entries`: the old build
// wrote to this exact same `%LOCALAPPDATA%\amm\history.json` path (see
// `MdiParentForm.cs`'s `HistoryFile.Entries`, PascalCase, no `max_entries`
// key) so a user migrating from WinForms to Tauri has a pre-existing file
// at this path in the old schema. Without the alias, serde's default
// case-sensitive match on the mismatched key silently discards it as an
// empty list (no parse error - `#[serde(default)]` masks it) and the next
// save() call permanently overwrites it with the new-schema file.
#[derive(Serialize, Deserialize, Default)]
struct HistoryFile {
  #[serde(default = "default_max")]
  max_entries: usize,
  #[serde(default, alias = "Entries")]
  entries: Vec<String>,
}
fn default_max() -> usize {
  DEFAULT_MAX_ENTRIES
}

impl InputHistory {
  pub fn new() -> Self {
    InputHistory { entries: Mutex::new(VecDeque::new()), max_entries: Mutex::new(DEFAULT_MAX_ENTRIES) }
  }

  pub fn add(&self, text: &str) {
    if text.trim().is_empty() {
      return;
    }
    let mut entries = self.entries.lock().unwrap_or_else(|e| e.into_inner());
    entries.retain(|e| e != text);
    entries.push_back(text.to_string());
    let max = *self.max_entries.lock().unwrap_or_else(|e| e.into_inner());
    while entries.len() > max {
      entries.pop_front();
    }
  }

  pub fn recent(&self, n: usize) -> Vec<String> {
    let entries = self.entries.lock().unwrap_or_else(|e| e.into_inner());
    entries.iter().rev().take(n).cloned().collect()
  }

  // spec: input-history "一括ロード／スナップショットによる永続化" requires a
  // per-user-writable location independent of the exe's own install
  // directory permissions - a standard non-admin MSI/NSIS install lands under
  // `C:\Program Files\`, where a plain non-admin user can't write next to the
  // exe, so `add`-triggered `save()` calls would fail silently and history
  // would never actually persist. Mirrors gateway.rs's own
  // `%LOCALAPPDATA%\amm\` resolution for the same class of per-user file.
  fn history_path() -> PathBuf {
    crate::app_data_base_dir().join("amm").join("history.json")
  }

  pub fn load(&self) {
    let Ok(text) = std::fs::read_to_string(Self::history_path()) else { return };
    let Ok(file) = serde_json::from_str::<HistoryFile>(&text) else { return };
    *self.max_entries.lock().unwrap_or_else(|e| e.into_inner()) = file.max_entries.max(1);
    let mut entries = self.entries.lock().unwrap_or_else(|e| e.into_inner());
    entries.clear();
    for e in file.entries {
      if !e.trim().is_empty() {
        entries.push_back(e);
      }
    }
    let max = *self.max_entries.lock().unwrap_or_else(|e| e.into_inner());
    while entries.len() > max {
      entries.pop_front();
    }
  }

  pub fn save(&self) {
    let file = HistoryFile {
      max_entries: *self.max_entries.lock().unwrap_or_else(|e| e.into_inner()),
      entries: self.entries.lock().unwrap_or_else(|e| e.into_inner()).iter().cloned().collect(),
    };
    let path = Self::history_path();
    if let Some(dir) = path.parent() {
      let _ = std::fs::create_dir_all(dir);
    }
    if let Ok(json) = serde_json::to_string_pretty(&file) {
      let _ = std::fs::write(path, json);
    }
  }
}

#[cfg(test)]
mod tests {
  use super::HistoryFile;

  #[test]
  fn history_file_reads_legacy_dotnet_pascal_case_entries() {
    let legacy = r#"{"Entries":["こんばんわ","こんにちわ"]}"#;
    let file: HistoryFile = serde_json::from_str(legacy).unwrap();
    assert_eq!(file.entries, vec!["こんばんわ", "こんにちわ"]);
  }

  #[test]
  fn history_file_reads_current_lower_case_schema() {
    let current = r#"{"max_entries":500,"entries":["a","b"]}"#;
    let file: HistoryFile = serde_json::from_str(current).unwrap();
    assert_eq!(file.max_entries, 500);
    assert_eq!(file.entries, vec!["a", "b"]);
  }
}
