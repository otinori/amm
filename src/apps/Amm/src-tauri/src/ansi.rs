// Shared ANSI escape-sequence stripping. Previously the same regex was
// defined independently in profile.rs, wait_detect.rs and the since-removed
// chat_recording.rs (found duplicated 3x in the 2026-07-26 full-repo
// review) - consolidated here as the single source of truth.
use regex::Regex;
use std::sync::OnceLock;

fn ansi_regex() -> &'static Regex {
  static RE: OnceLock<Regex> = OnceLock::new();
  RE.get_or_init(|| Regex::new(r"\x1b\[[0-9;]*[a-zA-Z]|\x1b\][^\x07]*\x07").unwrap())
}

pub fn strip_ansi(s: &str) -> String {
  ansi_regex().replace_all(s, "").into_owned()
}

#[cfg(test)]
mod tests {
  use super::*;

  #[test]
  fn strip_ansi_removes_csi_and_osc_sequences() {
    assert_eq!(strip_ansi("\x1b[31mred\x1b[0m"), "red");
    assert_eq!(strip_ansi("\x1b]0;title\x07plain"), "plain");
  }

  #[test]
  fn strip_ansi_leaves_plain_text_untouched() {
    assert_eq!(strip_ansi("hello world"), "hello world");
  }
}
