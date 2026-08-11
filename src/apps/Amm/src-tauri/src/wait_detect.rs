// WaitPatternDetector (spec: wait-detection). Regex-based Running/
// WaitingForInput state detection fed by the pty reader thread, replacing
// the phase-2 idle-timeout placeholder (which lived in app.js and only
// approximated "no keystrokes for N ms", not "output looks like a prompt").
//
// Simplification vs. the .NET original (documented, not silently dropped):
// the .NET version re-arms a 500ms no-output timer on every non-matching
// Feed and only promotes to WaitingForInput after a *separate* 4000ms
// silence threshold if nothing ever matched. This port collapses that to
// two end states checked by a single silence-watcher thread: immediate
// pattern match -> WaitingForInput now; otherwise Running until 4000ms of
// silence -> WaitingForInput (self-recovery). The "flicker-free 500ms
// settle window" nuance is dropped.
use regex::Regex;
use std::collections::VecDeque;
use std::sync::OnceLock;
use std::time::{Duration, Instant};

const SILENCE_THRESHOLD_MS: u64 = 4000;
const MAX_RECENT_LINES: usize = 50;

fn default_patterns() -> &'static Vec<Regex> {
  static PATTERNS: OnceLock<Vec<Regex>> = OnceLock::new();
  PATTERNS.get_or_init(|| {
    [
      r"[\$#>]\s*$",
      r"(?i)PS\s+\S+>\s*$",
      r"(?i)\(y/n\)\s*$",
      r"(?i)password[:\s]*$",
      r":\s*$",
      r"\?\s*$",
      r"^>(?:\s|$)",
      r"^[❯›](?:\s|$)",
      r"続行するには何かキーを押してください",
    ]
    .iter()
    .map(|p| Regex::new(p).expect("built-in wait pattern must compile"))
    .collect()
  })
}

fn strip_ansi(s: &str) -> String {
  crate::ansi::strip_ansi(s)
}

fn is_decorative(line: &str) -> bool {
  let t = line.trim();
  !t.is_empty() && t.chars().all(|c| matches!(c, '\u{2500}'..='\u{257F}' | '|' | ' '))
}

// spec: wait-detection "装飾のみの行を除外し直近50行を保持" requires stripping
// whitespace and frame chars *alternately* until neither changes (.NET's
// TrimSurroundingFrame), not a single non-alternating pass - a lone-pass trim
// leaves a leading space around box-prompt lines like "│ > ... │" and breaks
// the immediate-match path for the Claude Code/Codex box prompt pattern.
fn trim_surrounding_frame(s: &str) -> &str {
  let mut cur = s;
  loop {
    let next = cur.trim().trim_matches(|c: char| matches!(c, '\u{2500}'..='\u{257F}' | '|'));
    if next.len() == cur.len() {
      return next;
    }
    cur = next;
  }
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum WaitState {
  Unknown,
  Running,
  WaitingForInput,
  Stopped,
}

impl WaitState {
  pub fn as_str(&self) -> &'static str {
    match self {
      WaitState::Unknown => "Unknown",
      WaitState::Running => "Running",
      WaitState::WaitingForInput => "WaitingForInput",
      WaitState::Stopped => "Stopped",
    }
  }
}

pub struct WaitPatternDetector {
  user_patterns: Vec<Regex>,
  recent_lines: VecDeque<String>,
  pub state: WaitState,
  pub has_attention: bool,
  last_output_at: Instant,
  line_carry: String,
}

impl WaitPatternDetector {
  pub fn new(user_wait_patterns: &[String]) -> Self {
    // Regex crate has no built-in per-pattern match-timeout the way .NET's
    // Regex(matchTimeout:) does; the ReDoS guard from the spec is deferred
    // (documented gap - see tasks.md 5.6).
    let user_patterns = user_wait_patterns.iter().filter_map(|p| Regex::new(p).ok()).collect();
    WaitPatternDetector {
      user_patterns,
      recent_lines: VecDeque::with_capacity(MAX_RECENT_LINES),
      state: WaitState::Unknown,
      has_attention: false,
      last_output_at: Instant::now(),
      line_carry: String::new(),
    }
  }

  fn matches_any(&self, line: &str) -> bool {
    let frame_trimmed = trim_surrounding_frame(line);
    default_patterns().iter().any(|re| re.is_match(frame_trimmed))
      || self.user_patterns.iter().any(|re| re.is_match(frame_trimmed))
  }

  // Returns true if the state changed (caller should emit an event).
  pub fn feed(&mut self, chunk: &str) -> bool {
    self.last_output_at = Instant::now();
    let plain = strip_ansi(chunk);
    self.line_carry.push_str(&plain);

    while let Some(idx) = self.line_carry.find('\n') {
      let line: String = self.line_carry.drain(..=idx).collect();
      let line = line.trim_end_matches(['\r', '\n']).to_string();
      if !is_decorative(&line) {
        if self.recent_lines.len() >= MAX_RECENT_LINES {
          self.recent_lines.pop_front();
        }
        self.recent_lines.push_back(line);
      }
    }

    let matched = self
      .recent_lines
      .iter()
      .rev()
      .chain(std::iter::once(&self.line_carry))
      .any(|l| self.matches_any(l));

    let new_state = if matched { WaitState::WaitingForInput } else { WaitState::Running };
    self.transition(new_state)
  }

  // Called by the silence-watcher companion thread.
  pub fn check_silence(&mut self) -> bool {
    if self.state == WaitState::Running && self.last_output_at.elapsed() >= Duration::from_millis(SILENCE_THRESHOLD_MS) {
      return self.transition(WaitState::WaitingForInput);
    }
    false
  }

  // hook-driven forced transition (spec: amm/notify -> ForceState). Stopped
  // is terminal - no ForceState call can revive a pane after process exit.
  //
  // spec: wait-detection "強制状態通知は自動検知の巻き戻しから保護される" -
  // reset the silence clock here so the companion silence-watcher thread
  // (which polls check_silence() unconditionally every 200ms for the pane's
  // whole lifetime) doesn't see a stale last_output_at and flip a just-forced
  // Running state back to WaitingForInput within one poll tick.
  pub fn force_state(&mut self, target: &str) -> bool {
    if self.state == WaitState::Stopped {
      return false;
    }
    self.last_output_at = Instant::now();
    match target {
      "idle" => {
        let changed = self.transition(WaitState::WaitingForInput);
        changed
      }
      "attention" => {
        let state_changed = self.transition(WaitState::WaitingForInput);
        let attention_changed = !self.has_attention;
        self.has_attention = true;
        state_changed || attention_changed
      }
      "busy" => {
        let state_changed = self.transition(WaitState::Running);
        let attention_changed = self.has_attention;
        self.has_attention = false;
        state_changed || attention_changed
      }
      _ => false,
    }
  }

  pub fn notify_exit(&mut self) {
    self.state = WaitState::Stopped;
  }

  fn transition(&mut self, new_state: WaitState) -> bool {
    if self.state == WaitState::Stopped {
      return false;
    }
    if new_state == WaitState::Running {
      self.has_attention = false;
    }
    if self.state == new_state {
      return false;
    }
    self.state = new_state;
    true
  }
}

#[cfg(test)]
mod tests {
  use super::*;

  // spec: wait-detection "装飾のみの行を除外し直近50行を保持" (tasks.md 8.6.4).
  // Real-machine verification (2026-07-21) confirmed this via a Rust unit
  // test rather than a live box-prompt CLI, since trim_surrounding_frame is
  // a pure function - a PTY-based GUI test would exercise the exact same
  // code path with far more setup for no extra confidence.
  #[test]
  fn trim_surrounding_frame_strips_box_char_then_whitespace_alternately() {
    // A single non-alternating pass (trim whitespace once, then frame chars
    // once) leaves " > " - the leading space then fails `^>(?:\s|$)`. Only
    // repeating both trims until neither changes anything yields the bare
    // ">" the box-prompt pattern needs to match immediately.
    assert_eq!(trim_surrounding_frame("\u{2502} > \u{2502}"), ">");
  }

  #[test]
  fn trim_surrounding_frame_handles_ascii_pipe_too() {
    assert_eq!(trim_surrounding_frame("| > |"), ">");
  }

  #[test]
  fn trim_surrounding_frame_leaves_plain_text_untouched() {
    assert_eq!(trim_surrounding_frame("hello"), "hello");
  }

  #[test]
  fn box_prompt_line_matches_default_pattern_after_frame_trim() {
    let detector = WaitPatternDetector::new(&[]);
    assert!(detector.matches_any("\u{2502} > \u{2502}"));
  }

  // spec: wait-detection "強制状態通知は自動検知の巻き戻しから保護される"
  // (tasks.md 8.6.13). Before the fix, the always-running 200ms silence-
  // watcher poll could rewind a just-forced Running state back to
  // WaitingForInput within one tick if last_output_at was already stale
  // when force_state was called - force_state now resets it unconditionally
  // as its first action, regardless of which branch (idle/attention/busy)
  // follows.
  #[test]
  fn force_state_resets_silence_clock_to_protect_against_rewind() {
    let mut d = WaitPatternDetector::new(&[]);
    d.feed("some output with no wait pattern\n");
    assert_eq!(d.state, WaitState::Running);
    // Backdate the clock to simulate the detector having already been
    // silent long enough that, without the reset in force_state, the very
    // next check_silence() poll would immediately flip back to
    // WaitingForInput.
    d.last_output_at = Instant::now() - Duration::from_millis(SILENCE_THRESHOLD_MS + 500);
    d.force_state("busy");
    assert!(
      !d.check_silence(),
      "check_silence() rewound the state immediately after force_state() - the silence clock was not reset"
    );
    assert_eq!(d.state, WaitState::Running);
  }
}
