// GitHelper (spec: git-integration). Process-wrapper core only - scope for
// this pass excludes the confirmation dialog UI (GitCommitDialog) and the
// app-wide multi-repo dedup batch flow; see tasks.md 5.4 for what's covered
// by the simpler per-pane close guard actually wired up.
use std::path::Path;
use std::process::{Command, Stdio};
use std::time::Duration;
#[cfg(windows)]
use std::os::windows::process::CommandExt;

fn run(dir: &Path, args: &[&str], timeout: Duration) -> (i32, String, String) {
  if !dir.is_dir() {
    return (-1, String::new(), String::new());
  }
  let mut cmd = Command::new("git");
  cmd.args(args).current_dir(dir).stdin(Stdio::null()).stdout(Stdio::piped()).stderr(Stdio::piped());
  // Without this, every git-guard check (pane close, app exit) briefly
  // flashes a visible console window on Windows - std::process::Command
  // opens one by default for a console subprocess spawned from a GUI
  // (non-console-subsystem) process. Matches the same flag gateway.rs
  // already sets for its own subprocess spawning.
  #[cfg(windows)]
  {
    const CREATE_NO_WINDOW: u32 = 0x0800_0000;
    cmd.creation_flags(CREATE_NO_WINDOW);
  }
  let mut child = match cmd.spawn() {
    Ok(c) => c,
    Err(_) => return (-1, String::new(), String::new()),
  };

  let start = std::time::Instant::now();
  loop {
    match child.try_wait() {
      Ok(Some(status)) => {
        use std::io::Read;
        let mut stdout = String::new();
        let mut stderr = String::new();
        if let Some(mut s) = child.stdout.take() {
          let _ = s.read_to_string(&mut stdout);
        }
        if let Some(mut s) = child.stderr.take() {
          let _ = s.read_to_string(&mut stderr);
        }
        return (status.code().unwrap_or(-1), stdout, stderr);
      }
      Ok(None) => {
        if start.elapsed() >= timeout {
          let _ = child.kill();
          let _ = child.wait();
          return (-1, String::new(), String::new());
        }
        std::thread::sleep(Duration::from_millis(50));
      }
      Err(_) => return (-1, String::new(), String::new()),
    }
  }
}

pub fn get_repo_root(dir: &Path) -> Option<String> {
  let (code, out, _) = run(dir, &["rev-parse", "--show-toplevel"], Duration::from_secs(3));
  if code != 0 {
    return None;
  }
  let trimmed = out.trim();
  if trimmed.is_empty() {
    return None;
  }
  // git always prints forward slashes (even on Windows). The .NET original
  // normalized this to backslashes for Windows path display; that replace
  // was ported here unconditionally, which found live-testing (add-macos-
  // support) turned the returned root into a bogus backslash-separated
  // string on macOS (e.g. "\private\tmp\...\repo") - a path that doesn't
  // exist, so every downstream `git status --short dir=<that>` silently
  // failed closed (empty output), and runGitGuardForRepo's "no output = no
  // changes" fallback made the close-pane Git guard a silent no-op on
  // every macOS repo. Only normalize on Windows.
  if cfg!(windows) {
    Some(trimmed.replace('/', "\\"))
  } else {
    Some(trimmed.to_string())
  }
}

// spec: git-integration "status 出力の非ASCIIファイル名表示" - without
// core.quotepath=false, git octal-escapes non-ASCII (e.g. Japanese) filenames
// in --short output, which the guard prompt then shows to the user verbatim.
pub fn status_short(dir: &Path) -> String {
  run(dir, &["-c", "core.quotepath=false", "status", "--short"], Duration::from_secs(5)).1
}

pub fn add_all_and_commit(dir: &Path, message: &str) -> (i32, String, String) {
  let (add_code, add_out, add_err) = run(dir, &["add", "-A"], Duration::from_secs(10));
  if add_code != 0 {
    return (add_code, add_out, add_err);
  }
  run(dir, &["commit", "-m", message], Duration::from_secs(10))
}

pub fn push(dir: &Path) -> (i32, String, String) {
  run(dir, &["push"], Duration::from_secs(30))
}

#[cfg(test)]
mod tests {
  use super::*;
  use std::fs;

  // Unique-per-test temp dir, so parallel test threads (and re-runs) don't
  // collide on the same git working tree.
  fn temp_repo_dir(label: &str) -> std::path::PathBuf {
    let dir = std::env::temp_dir().join(format!(
      "amm-git-helper-test-{label}-{}-{}",
      std::process::id(),
      std::time::SystemTime::now().duration_since(std::time::UNIX_EPOCH).unwrap().as_nanos()
    ));
    fs::create_dir_all(&dir).unwrap();
    dir
  }

  // Real `git init` + local (not global) user.name/user.email, so commits
  // work self-contained regardless of the host's global git config.
  fn init_repo(dir: &Path) {
    let status = |args: &[&str]| Command::new("git").args(args).current_dir(dir).status().unwrap();
    assert!(status(&["init", "-q"]).success());
    assert!(status(&["config", "user.email", "git-helper-test@example.com"]).success());
    assert!(status(&["config", "user.name", "git-helper-test"]).success());
  }

  #[test]
  fn get_repo_root_returns_none_for_non_git_directory() {
    let dir = temp_repo_dir("no-repo");
    assert_eq!(get_repo_root(&dir), None);
  }

  #[test]
  fn get_repo_root_returns_none_for_missing_directory() {
    let dir = std::env::temp_dir().join("amm-git-helper-test-does-not-exist-xyz");
    assert_eq!(get_repo_root(&dir), None);
  }

  #[test]
  fn get_repo_root_returns_toplevel_for_git_directory() {
    let dir = temp_repo_dir("toplevel");
    init_repo(&dir);
    let root = get_repo_root(&dir).expect("git init'd dir must resolve a root");
    // git prints a canonicalized (possibly different-case-drive-letter on
    // Windows) absolute path - assert on the leaf dir name rather than a
    // byte-exact match against `dir`.
    assert!(root.ends_with(dir.file_name().unwrap().to_str().unwrap()));
  }

  #[test]
  fn status_short_reports_untracked_file() {
    let dir = temp_repo_dir("status");
    init_repo(&dir);
    fs::write(dir.join("untracked.txt"), "hello").unwrap();
    let out = status_short(&dir);
    assert!(out.contains("untracked.txt"), "expected untracked.txt in status output, got: {out:?}");
  }

  #[test]
  fn add_all_and_commit_succeeds_for_a_new_file() {
    let dir = temp_repo_dir("commit");
    init_repo(&dir);
    fs::write(dir.join("file.txt"), "content").unwrap();
    let (code, _out, err) = add_all_and_commit(&dir, "test commit");
    assert_eq!(code, 0, "commit should succeed, stderr: {err}");
    // status should now be clean (nothing left to commit).
    assert!(status_short(&dir).trim().is_empty());
  }

  #[test]
  fn add_all_and_commit_reports_nonzero_when_nothing_to_commit() {
    let dir = temp_repo_dir("empty-commit");
    init_repo(&dir);
    let (code, _out, _err) = add_all_and_commit(&dir, "empty commit attempt");
    assert_ne!(code, 0, "git commit with nothing staged must fail");
  }
}
