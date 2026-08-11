// Profile schema (spec: profile-schema). Rust port of SessionProfile's data
// model. Scope for this pass: schema + defaults + the two explicitly
// documented migrations + file resolution order. NOT ported yet (left as
// gaps, see tasks.md 5.1): hot-reload (FileSystemWatcher equivalent),
// hijack-safe PATH resolution for BuildLaunchCommandLine, resume-token
// injection, per-command text formatting (collapseBlankLines/
// commentPrefixes) at the pty_write call site, and the command-template /
// per-window settings dialogs (UI, not data).
use serde::{Deserialize, Serialize};
use serde_json::Value;
use std::path::{Path, PathBuf};

fn default_true() -> bool {
  true
}
fn default_wait_patterns() -> Vec<String> {
  vec!["[>]\\s*$".to_string()]
}
pub fn default_comment_prefixes() -> Vec<String> {
  vec!["'".to_string(), "//".to_string()]
}

// spec: pane-management's titleBarColor - fallback for profiles that
// predate this field (title_bar_color is None) so pre-existing ClaudeCode/
// Codex/CopilotCli commands get colored panes immediately instead of
// staying uncolored until someone opens each one and picks a color by hand
// (user report). Must match app.js's COMMAND_TYPE_PRESETS values - this
// only supplies a *display* fallback and never writes back into the saved
// profile, so the two only need to agree, not share one source of truth.
pub fn default_title_bar_color_for_type(command_type: &str) -> Option<&'static str> {
  match command_type {
    "ClaudeCode" => Some("#5a3a2d"),
    "Codex" => Some("#2d4a3a"),
    "CopilotCli" => Some("#3a2d5a"),
    "AntigravityCli" => Some("#2d3a5a"),
    _ => None,
  }
}

// spec: pane-management's「コマンド追加 - 種類を選択」プリセット(実行ファイル/
// 引数/wait パターン等)を、コマンド管理画面の新規「コマンドタイプ」タブから
// 編集・永続化できるようにする(ユーザー要望、2026-08-03)。永続化先は
// editor-settings.json/mcp-servers.jsonと同じ「どの.ammファイルを開いていても
// 常に同じ内容」なアプリ全体設定ディレクトリ(File>開くで切り替わる
// profiles.ammとは独立、ユーザーとの相談で決定 - profiles.ammを流用すると
// ファイル切り替えのたびに中身が変わってしまい、コマンドタイプがどこに
// 保存されたか分かりにくくなるため)。ファイルが存在しなければプラット
// フォーム別の既定値(旧: dialogs-profile-mcp.jsのCOMMAND_TYPE_PRESETS_
// WINDOWS/_MACOS)を返す。
#[derive(Clone, Debug, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct CommandTypePreset {
  pub key: String,
  pub label: String,
  pub executable: String,
  #[serde(default)]
  pub args: Vec<String>,
  #[serde(default = "default_utf8")]
  pub output_encoding: String,
  #[serde(default)]
  pub auto_chcp: bool,
  #[serde(default)]
  pub wait_patterns: Vec<String>,
  #[serde(default)]
  pub send_line_by_line: bool,
  #[serde(default)]
  pub select_working_dir_on_start: bool,
  #[serde(default)]
  pub prompt_new_name_on_command_add: bool,
  #[serde(default)]
  pub title_bar_color: Option<String>,
}

fn default_utf8() -> String {
  "UTF-8".to_string()
}

fn command_type_presets_file() -> PathBuf {
  crate::app_data_base_dir().join("amm").join("command-type-presets.json")
}

#[cfg(windows)]
pub fn default_command_type_presets() -> Vec<CommandTypePreset> {
  vec![
    CommandTypePreset { key: "Cmd".into(), label: "Cmd".into(), executable: "cmd.exe".into(), args: vec![], output_encoding: "UTF-8".into(), auto_chcp: true, wait_patterns: vec![r"[>]\s*$".into()], send_line_by_line: false, select_working_dir_on_start: false, prompt_new_name_on_command_add: false, title_bar_color: None },
    CommandTypePreset { key: "PowerShell".into(), label: "Powershell".into(), executable: "powershell.exe".into(), args: vec!["-NoLogo".into()], output_encoding: "UTF-8".into(), auto_chcp: true, wait_patterns: vec![r"PS\s+\S+>\s*$".into()], send_line_by_line: false, select_working_dir_on_start: false, prompt_new_name_on_command_add: false, title_bar_color: None },
    CommandTypePreset { key: "ClaudeCode".into(), label: "Claude Code".into(), executable: "claude.exe".into(), args: vec![], output_encoding: "UTF-8".into(), auto_chcp: false, wait_patterns: vec!["^>".into()], send_line_by_line: false, select_working_dir_on_start: true, prompt_new_name_on_command_add: true, title_bar_color: Some("#5a3a2d".into()) },
    CommandTypePreset { key: "Codex".into(), label: "Codex".into(), executable: "cmd.exe".into(), args: vec!["/c".into(), r"%APPDATA%\npm\codex.cmd".into()], output_encoding: "UTF-8".into(), auto_chcp: false, wait_patterns: vec!["^>".into(), "^[❯›]".into()], send_line_by_line: false, select_working_dir_on_start: true, prompt_new_name_on_command_add: true, title_bar_color: Some("#2d4a3a".into()) },
    CommandTypePreset { key: "CopilotCli".into(), label: "COPILOT-CLI".into(), executable: "cmd.exe".into(), args: vec!["/c".into(), r"%APPDATA%\npm\copilot.cmd".into()], output_encoding: "UTF-8".into(), auto_chcp: false, wait_patterns: vec!["^>".into(), "^[❯›]".into()], send_line_by_line: false, select_working_dir_on_start: true, prompt_new_name_on_command_add: true, title_bar_color: Some("#3a2d5a".into()) },
    // agy (Antigravity CLI) is not an npm .cmd shim like Codex/Copilot CLI
    // above - it ships its own native installer (antigravity.google/docs/cli/install)
    // that places agy.exe under %LOCALAPPDATA%\agy\bin and adds that
    // directory to the user's PATH by default (unless --skip-path was
    // used at install time). A bare executable name lets
    // resolve_executable_path's PATHEXT-aware PATH search find it
    // directly, matching the macOS/Unix preset below - no cmd.exe
    // wrapper needed since it's a real .exe, not a batch shim.
    CommandTypePreset { key: "AntigravityCli".into(), label: "Antigravity CLI".into(), executable: "agy.exe".into(), args: vec![], output_encoding: "UTF-8".into(), auto_chcp: false, wait_patterns: vec!["^>".into(), "^[❯›]".into()], send_line_by_line: false, select_working_dir_on_start: true, prompt_new_name_on_command_add: true, title_bar_color: Some("#2d3a5a".into()) },
  ]
}

// spec: macOS(profiles.macos.ammと同じ値)。Linux(未着手プラットフォーム)は
// bareなzshベースの値を暫定共有 - Windows専用のcmd.exeよりはこちらが妥当な
// 暫定値のため。
#[cfg(not(windows))]
pub fn default_command_type_presets() -> Vec<CommandTypePreset> {
  vec![
    CommandTypePreset { key: "Zsh".into(), label: "zsh".into(), executable: "/bin/zsh".into(), args: vec!["-l".into()], output_encoding: "UTF-8".into(), auto_chcp: false, wait_patterns: vec![r"[$#%]\s*$".into()], send_line_by_line: false, select_working_dir_on_start: false, prompt_new_name_on_command_add: false, title_bar_color: None },
    CommandTypePreset { key: "PowerShell".into(), label: "PowerShell (pwsh)".into(), executable: "pwsh".into(), args: vec!["-NoLogo".into()], output_encoding: "UTF-8".into(), auto_chcp: false, wait_patterns: vec![r"PS\s+\S+>\s*$".into()], send_line_by_line: false, select_working_dir_on_start: false, prompt_new_name_on_command_add: false, title_bar_color: None },
    CommandTypePreset { key: "ClaudeCode".into(), label: "Claude Code".into(), executable: "claude".into(), args: vec![], output_encoding: "UTF-8".into(), auto_chcp: false, wait_patterns: vec!["^>".into()], send_line_by_line: false, select_working_dir_on_start: true, prompt_new_name_on_command_add: true, title_bar_color: Some("#5a3a2d".into()) },
    CommandTypePreset { key: "Codex".into(), label: "Codex".into(), executable: "codex".into(), args: vec![], output_encoding: "UTF-8".into(), auto_chcp: false, wait_patterns: vec!["^>".into(), "^[❯›]".into()], send_line_by_line: false, select_working_dir_on_start: true, prompt_new_name_on_command_add: true, title_bar_color: Some("#2d4a3a".into()) },
    CommandTypePreset { key: "CopilotCli".into(), label: "COPILOT-CLI".into(), executable: "copilot".into(), args: vec![], output_encoding: "UTF-8".into(), auto_chcp: false, wait_patterns: vec!["^>".into(), "^[❯›]".into()], send_line_by_line: false, select_working_dir_on_start: true, prompt_new_name_on_command_add: true, title_bar_color: Some("#3a2d5a".into()) },
    CommandTypePreset { key: "AntigravityCli".into(), label: "Antigravity CLI".into(), executable: "agy".into(), args: vec![], output_encoding: "UTF-8".into(), auto_chcp: false, wait_patterns: vec!["^>".into(), "^[❯›]".into()], send_line_by_line: false, select_working_dir_on_start: true, prompt_new_name_on_command_add: true, title_bar_color: Some("#2d3a5a".into()) },
  ]
}

pub(crate) fn load_command_type_presets() -> Vec<CommandTypePreset> {
  let Ok(text) = std::fs::read_to_string(command_type_presets_file()) else {
    return default_command_type_presets();
  };
  serde_json::from_str(&text).unwrap_or_else(|_| default_command_type_presets())
}

pub(crate) fn save_command_type_presets(presets: &[CommandTypePreset]) -> std::io::Result<()> {
  let path = command_type_presets_file();
  if let Some(dir) = path.parent() {
    std::fs::create_dir_all(dir)?;
  }
  let json = serde_json::to_string_pretty(presets).unwrap_or_default();
  std::fs::write(path, json)
}

fn default_auto_send_delay_ms() -> i64 {
  3000
}

// spec: profile-schema "プロファイルファイルスキーマ" requires `theme` to be a
// `{ [key: string]: string }` object (matching .NET's `Dictionary<string,string>`),
// and requires that one profile's type-mismatched `theme` not fail the whole
// file's load. serde's normal behavior on a type mismatch is to bubble the
// error up through the enclosing `Vec<SessionProfile>`, which is exactly the
// "one themed profile poisons the whole file" failure mode the spec forbids -
// so this coerces any non-object shape (including the pre-fix `theme: "some
// string"` shape a stale file might contain) to `None` instead of erroring.
fn deserialize_theme_lenient<'de, D>(
  deserializer: D,
) -> Result<Option<std::collections::BTreeMap<String, String>>, D::Error>
where
  D: serde::Deserializer<'de>,
{
  let v = Value::deserialize(deserializer)?;
  Ok(match v {
    Value::Object(map) => Some(
      map
        .into_iter()
        .filter_map(|(k, val)| match val {
          Value::String(s) => Some((k, s)),
          _ => None,
        })
        .collect(),
    ),
    _ => None,
  })
}

#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct AutoSendOnIdleSettings {
  #[serde(default)]
  pub enabled: bool,
  #[serde(default)]
  pub prompt: String,
  #[serde(default = "default_auto_send_delay_ms")]
  pub delay_ms: i64,
}

impl Default for AutoSendOnIdleSettings {
  fn default() -> Self {
    AutoSendOnIdleSettings { enabled: false, prompt: String::new(), delay_ms: default_auto_send_delay_ms() }
  }
}

// spec: profile-schema's "ウィンドウ位置・名前・作業ディレクトリの記憶
// (windowGeometry)" - port of SessionProfile.cs's WindowGeometryEntry.
// index is 1-based, referencing "同 profile の生存 MDI 数 + 1" (resets to 1
// once every pane of that profile is closed, no gap-filling). Faithful
// fixed-field port, not `extra`-flattened like SessionProfile itself: the
// .NET original has no [JsonExtensionData] on this class either, so unknown
// keys are silently dropped there too.
#[derive(Debug, Clone, Serialize, Deserialize, PartialEq, Default)]
#[serde(rename_all = "camelCase")]
pub struct WindowGeometryEntry {
  pub index: u32,
  #[serde(default)]
  pub x: i32,
  #[serde(default)]
  pub y: i32,
  #[serde(default)]
  pub w: i32,
  #[serde(default)]
  pub h: i32,
  #[serde(default)]
  pub name: Option<String>,
  #[serde(default)]
  pub maximized: Option<bool>,
  #[serde(default)]
  pub working_directory: Option<String>,
}

// Resolved effect of a WindowGeometryEntry lookup, ready for the caller
// (mcp.rs::open_pane / a future capture-triggered restore) to apply without
// re-deriving the spec's 3 scenarios itself. `rect: None` covers both "no
// entry at this index" (caller falls back to its own default placement)
// and "entry has w/h=0" (name-only/maximized-only entry - position/size
// untouched, but name/maximized/working_directory still apply
// independently, per the "name-only / maximized エントリ" scenario).
#[derive(Debug, Clone, PartialEq, Default)]
pub struct GeometryApplyPlan {
  pub rect: Option<(i32, i32, i32, i32)>,
  pub name: Option<String>,
  pub maximized: bool,
  pub working_directory: Option<String>,
}

pub fn resolve_geometry_apply_plan(entries: &[WindowGeometryEntry], one_based_index: u32) -> GeometryApplyPlan {
  let Some(entry) = entries.iter().find(|e| e.index == one_based_index) else {
    return GeometryApplyPlan::default();
  };
  GeometryApplyPlan {
    rect: (entry.w > 0 && entry.h > 0).then_some((entry.x, entry.y, entry.w, entry.h)),
    name: entry.name.clone().filter(|n| !n.is_empty()),
    maximized: entry.maximized.unwrap_or(false),
    working_directory: entry.working_directory.clone().filter(|w| !w.trim().is_empty()),
  }
}

// Snapshot of one alive pane's current geometry, as the caller (a future
// "現在の配置を記憶" trigger) would assemble it from the JS pane model +
// Rust-side PtyEntry (name/workingDirectory live in different layers, so
// this is the join point). No native OS window/minimize concept exists for
// these DOM-based panes, so unlike BuildGeometryFromAlive's
// Minimized->RestoreBounds nuance, `maximized` is the only non-Normal state.
#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct AlivePaneGeometry {
  pub x: i32,
  pub y: i32,
  pub w: i32,
  pub h: i32,
  pub maximized: bool,
  pub name: Option<String>,
  pub working_directory: Option<String>,
}

// Port of MdiParentForm.cs's BuildGeometryFromAlive: alive panes (already
// ordered by launch/instance order by the caller) are packed 1..N by
// position in the slice. A maximized pane with no custom name and no saved
// cwd is omitted entirely (index gap = "launch-time maximize fallback"),
// matching the spec exactly.
pub fn build_geometry_from_alive(alive: &[AlivePaneGeometry]) -> Vec<WindowGeometryEntry> {
  let mut entries = Vec::new();
  for (i, c) in alive.iter().enumerate() {
    let name = c.name.clone().filter(|n| !n.is_empty());
    let working_directory = c.working_directory.clone().filter(|w| !w.trim().is_empty());
    if c.maximized && name.is_none() && working_directory.is_none() {
      continue;
    }
    let mut entry = WindowGeometryEntry { index: (i + 1) as u32, name, working_directory, ..Default::default() };
    if c.maximized {
      entry.maximized = Some(true);
    } else {
      (entry.x, entry.y, entry.w, entry.h) = (c.x, c.y, c.w, c.h);
    }
    entries.push(entry);
  }
  entries
}

#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct SessionProfile {
  pub name: String,
  #[serde(default)]
  pub command_type: String,
  pub executable: String,
  #[serde(default)]
  pub args: Vec<String>,
  #[serde(default)]
  pub resume_on_start: bool,
  #[serde(default)]
  pub output_encoding: Option<String>,
  #[serde(default = "default_true")]
  pub auto_chcp: bool,
  #[serde(default = "default_wait_patterns")]
  pub wait_patterns: Vec<String>,
  #[serde(default)]
  pub working_directory: Option<String>,
  #[serde(default = "default_true")]
  pub ctrl_c_copy_on_selection: bool,
  #[serde(default)]
  pub initial_commands: Vec<String>,
  #[serde(default)]
  pub session_log: bool,
  #[serde(default, deserialize_with = "deserialize_theme_lenient")]
  pub theme: Option<std::collections::BTreeMap<String, String>>,
  #[serde(default = "default_true")]
  pub close_on_exit: bool,
  #[serde(default)]
  pub auto_start_count: u32,
  #[serde(default)]
  pub close_prohibited: bool,
  #[serde(default)]
  pub window_geometry: Vec<WindowGeometryEntry>,
  #[serde(default)]
  pub nickname: Option<String>,
  #[serde(default)]
  pub send_line_by_line: bool,
  #[serde(default)]
  pub select_working_dir_on_start: bool,
  #[serde(default)]
  pub prompt_new_name_on_command_add: bool,
  #[serde(default)]
  pub auto_send_on_idle: AutoSendOnIdleSettings,
  #[serde(default)]
  pub font_size: Option<u32>,
  // spec: pane-management - per-command title bar color (CSS color string,
  // e.g. "#3a2d5a"). None = use the default title bar color. Purely a
  // rendering hint, no launch-time behavior.
  #[serde(default)]
  pub title_bar_color: Option<String>,

  // Carries forward unknown/legacy keys so re-serialization doesn't silently
  // drop fields this port doesn't model yet (partial answer to "旧フィールドの
  // 後方互換マイグレーション" - the two *documented* migrations are applied
  // explicitly in `migrate`, everything else just round-trips as-is).
  #[serde(flatten)]
  pub extra: std::collections::BTreeMap<String, Value>,
}

impl SessionProfile {
  fn migrate(mut self) -> Self {
    // promptRenameOnStart -> promptNewNameOnCommandAdd
    if let Some(Value::Bool(v)) = self.extra.remove("promptRenameOnStart") {
      self.prompt_new_name_on_command_add = v;
    }
    // commandType未設定(旧AMMファイル)はnickname/executable/argsから推測補完する
    // (SessionProfile.cs's MigrateLegacyFields/InferCommandType port - found
    // missing in the phase 8.1 parity audit).
    if self.command_type.is_empty() || self.command_type == "Other" {
      if let Some(inferred) = infer_command_type(&self) {
        self.command_type = inferred.to_string();
      }
    }
    self
  }

  // macOS/Linux delta (add-macos-support): "cmd.exe"/chcp are Windows-only
  // concepts (user: "macのコマンドにCmdは無いね") - this bootstrap fallback
  // (the sole profile load_profiles/lib.rs hand back when profiles.amm is
  // missing or fails to parse) was unconditionally named "cmd"/CommandType
  // Cmd/cmd.exe on every platform, which cannot spawn at all off Windows.
  // Falls back to the user's login shell ($SHELL), mirroring pty.rs's
  // spawn_pty_for_pane_with_patterns default_shell fallback. Also fixes an
  // internal inconsistency found in the same review: auto_chcp's own serde
  // default (#[serde(default = "default_true")] above) is true, matching
  // SessionProfile.cs's CreateDefaultCmd, but this constructor was
  // hardcoding false - restored to true for the Windows branch.
  pub fn default_cmd() -> Self {
    #[cfg(windows)]
    let (name, command_type, executable, auto_chcp, wait_patterns) =
      ("CMD".to_string(), "Cmd".to_string(), "cmd.exe".to_string(), true, default_wait_patterns());
    #[cfg(target_os = "macos")]
    let (name, command_type, executable, auto_chcp, wait_patterns) = (
      "zsh".to_string(),
      "Other".to_string(),
      std::env::var("SHELL").unwrap_or_else(|_| "/bin/zsh".to_string()),
      false,
      vec!["[$#%]\\s*$".to_string()],
    );
    #[cfg(all(unix, not(target_os = "macos")))]
    let (name, command_type, executable, auto_chcp, wait_patterns) = (
      "bash".to_string(),
      "Other".to_string(),
      std::env::var("SHELL").unwrap_or_else(|_| "/bin/bash".to_string()),
      false,
      vec!["[$#%]\\s*$".to_string()],
    );

    SessionProfile {
      name,
      command_type,
      executable,
      args: vec![],
      resume_on_start: false,
      output_encoding: None,
      auto_chcp,
      wait_patterns,
      working_directory: None,
      ctrl_c_copy_on_selection: false,
      initial_commands: vec![],
      session_log: false,
      theme: None,
      close_on_exit: false,
      auto_start_count: 0,
      close_prohibited: false,
      window_geometry: vec![],
      nickname: None,
      send_line_by_line: false,
      select_working_dir_on_start: false,
      prompt_new_name_on_command_add: false,
      auto_send_on_idle: AutoSendOnIdleSettings::default(),
      font_size: None,
      title_bar_color: None,
      extra: Default::default(),
    }
  }
}

// nickname最優先、続いて実行ファイル・引数で判定(SessionProfile.cs's
// InferCommandType/ArgsReferToTool を忠実に移植)。
fn infer_command_type(p: &SessionProfile) -> Option<&'static str> {
  if let Some(nick) = p.nickname.as_deref() {
    match nick.trim().to_lowercase().as_str() {
      "claude" => return Some("ClaudeCode"),
      "codex" => return Some("Codex"),
      "copilot" => return Some("CopilotCli"),
      _ => {}
    }
  }
  let exe = p.executable.to_lowercase();
  let args_joined = p.args.join(" ").to_lowercase();
  if exe.contains("claude") {
    return Some("ClaudeCode");
  }
  // トークン単位で照合する(作業ディレクトリ等のパスへの部分一致誤判定を防ぐ、
  // 例: C:\my-codex-notes\)。
  if args_refer_to_tool(&p.args, "codex") {
    return Some("Codex");
  }
  if args_refer_to_tool(&p.args, "copilot") {
    return Some("CopilotCli");
  }
  if exe.contains("powershell") || exe.contains("pwsh") {
    return Some("PowerShell");
  }
  // 純粋なcmd.exeのみCmd。`cmd /c 別スクリプト`はラッパーなのでOther扱い。
  if (exe.ends_with("cmd.exe") || exe == "cmd") && !args_joined.contains("/c") {
    return Some("Cmd");
  }
  None
}

fn args_refer_to_tool(args: &[String], tool: &str) -> bool {
  for a in args {
    for tok in a.split(['\u{20}', '\t', '\\', '/']).filter(|s| !s.is_empty()) {
      let name = match tok.find('.') {
        Some(dot) if dot > 0 => &tok[..dot],
        _ => tok,
      };
      if name.eq_ignore_ascii_case(tool) {
        return true;
      }
    }
  }
  false
}

// Windows %VAR% expansion (Rust has no direct equivalent of
// Environment.ExpandEnvironmentVariables). Unrecognized/unterminated
// %...% sequences are left untouched, matching the .NET behavior.
pub(crate) fn expand_env_vars(s: &str) -> String {
  let mut result = String::with_capacity(s.len());
  let mut i = 0;
  let bytes = s.as_bytes();
  while i < s.len() {
    if bytes[i] == b'%' {
      if let Some(end) = s[i + 1..].find('%') {
        let var_name = &s[i + 1..i + 1 + end];
        if !var_name.is_empty() {
          if let Ok(val) = std::env::var(var_name) {
            result.push_str(&val);
            i = i + 1 + end + 1;
            continue;
          }
        }
      }
    }
    let ch = s[i..].chars().next().unwrap();
    result.push(ch);
    i += ch.len_utf8();
  }
  result
}

// spec: profile-schema's autoChcp - port of ConPtyWrapper.cs's chcp-wrap
// technique (cmd.exe /d /s /c "chcp 65001 > nul && <original command
// line>") to force a UTF-8 console codepage before the real command runs.
//
// security: profile-authored `executable`/`args` are NOT trusted input in
// general - they can arrive via command-import-export (imported
// .ammprofiles) or a shared/opened profiles.amm, neither of which is
// covered by the .amm untrusted-autostart confirmation gate (that gate only
// covers mcpServers auto-start, not pane launch commands). Two independent
// gaps let a hostile token achieve command injection here (code-review
// 2026-07-26, finding H-1/H-2, mirrored in ConPtyWrapper.cs's legacy .NET
// equivalent):
//   1. A token with no whitespace at all (e.g. `a&calc.exe&b`) skipped
//      quoting entirely (the old check only looked for whitespace) and
//      injected directly.
//   2. A token containing an embedded `"` broke cmd.exe's quote-parity
//      tracking once wrapped in a naive `"token"` (no internal escaping),
//      letting a following `&`/`|` be reinterpreted as a *separate*
//      cmd.exe command.
//
// Gap 1 is fixed below by quoting whenever the token contains any of
// cmd.exe's own metacharacters (`&|<>^%`), not just whitespace.
//
// Gap 2 turned out to NOT be fixable by escaping the embedded quote inside
// this function alone: `spawn_pty_for_pane_with_patterns` (lib.rs) hands
// this string to `portable_pty::CommandBuilder::arg()`, whose Windows
// backend (`cmdbuilder.rs::append_quoted`) applies its own standard
// CRT/ArgvQuote escaping to the *entire* assembled "chcp ... && ..." string
// before cmd.exe ever sees it (verified by reading that crate's source).
// That pass unconditionally inserts a backslash immediately before every
// literal `"` it finds and does not understand caret-escaping, so any
// caret we place next to a quote here to protect it from cmd.exe gets
// physically separated from that quote by an inserted backslash - cmd.exe
// (which reportedly does *not* treat a `"` as escaped just because a
// backslash precedes it, backslash has no meaning to cmd.exe's own
// parser) ends up seeing an unprotected quote regardless of what we do
// here. There is no character sequence that survives append_quoted's
// transformation with a caret landing directly adjacent to the quote it
// needs to protect. Given Windows paths cannot contain `"` at all (NTFS
// disallows it) and it's an unusual character in CLI flag values, the safe
// choice is to refuse to launch rather than attempt an escaping scheme
// that cannot be made correct through this double-quoting pipeline.
fn quote_command_token(token: &str) -> Result<String, String> {
  if token.contains('"') {
    return Err(format!(
      "command/argument contains a \" character, which cannot be safely passed through the auto-chcp launch wrapper: {token:?}"
    ));
  }
  let needs_quotes = token.is_empty() || token.chars().any(|c| c.is_whitespace() || "&|<>^%".contains(c));
  if !needs_quotes {
    return Ok(token.to_string());
  }
  Ok(format!("\"{token}\""))
}

pub fn build_chcp_wrapped_command(shell: &str, args: &[String]) -> Result<(String, Vec<String>), String> {
  let mut inner = quote_command_token(shell)?;
  for a in args {
    inner.push(' ');
    inner.push_str(&quote_command_token(a)?);
  }
  Ok(("cmd.exe".to_string(), vec!["/d".to_string(), "/s".to_string(), "/c".to_string(), format!("chcp 65001 > nul && {inner}")]))
}

// spec: 起動コマンドラインの解決とセキュリティ. Rust port of
// SessionProfile.cs's ResolveExecutablePath/SafeSearchPath: bare names
// (no path separator, not rooted) are resolved via a hijack-safe PATH
// search (System32 first, non-rooted PATH components skipped, PATHEXT
// tried in order) rather than passed to the OS as-is, which would let a
// malicious same-named executable earlier in a hijacked PATH win.
// Explicit paths (containing a separator or already rooted) are only
// env-expanded, never PATH-searched.
pub fn resolve_executable_path(executable: &str) -> String {
  let exe = expand_env_vars(executable);
  if exe.is_empty() {
    return exe;
  }
  if exe.contains('\\') || exe.contains('/') || Path::new(&exe).is_absolute() {
    return exe;
  }
  safe_search_path(&exe).unwrap_or(exe)
}

// spec: profile-schema's working-directory resolution - port of
// SessionProfile.cs's ResolveWorkingDirectory ("未指定 / 空なら現在のカレント
// ディレクトリを返す"). Found via live CDP testing (phase 8.2): every real
// profile in this session's test profiles.amm has workingDirectory=""
// (.NET's plain `string` defaults to "", not null), and the CommandTemplates
// presets ported into this app use "%USERPROFILE%" - neither case was being
// normalized/expanded anywhere in the open_pane/spawn_pty cwd chain, so
// panes launched from profiles.amm either got literally-invalid cwds
// (an "%USERPROFILE%"-named directory) or fell through to an empty PathBuf
// (blank "" short-circuited the None-only current_dir() fallback). Returns
// None for blank/whitespace-only input so the caller's own current_dir()
// fallback applies, matching .NET exactly.
pub fn resolve_working_directory(raw: Option<&str>) -> Option<String> {
  let raw = raw?.trim();
  if raw.is_empty() {
    return None;
  }
  Some(expand_env_vars(raw))
}

// spec: profile-schema's outputEncoding - port of SessionProfile.cs's
// GetEncoding() (same mapping, same UTF-8 fallback for unset/unrecognized
// values). Used to decode the pty's raw output bytes for profiles that set
// a non-UTF-8 console codepage (e.g. legacy Shift-JIS tools).
pub fn resolve_output_encoding(raw: Option<&str>) -> &'static encoding_rs::Encoding {
  match raw.unwrap_or("UTF-8").to_uppercase().as_str() {
    "SHIFT_JIS" | "SHIFT-JIS" => encoding_rs::SHIFT_JIS,
    _ => encoding_rs::UTF_8,
  }
}

// macOS delta (found via a real-machine test run adding macOS support,
// openspec/changes/add-macos-support/): this previously hardcoded `;` as
// the PATH separator and Windows-only PATHEXT/System32 conventions
// unconditionally - not a compile-time gap (no #[cfg] anywhere), but on
// macOS/Linux the `;`-split against a `:`-separated $PATH treated the
// entire PATH as a single non-existent "directory", so this always
// returned None there and resolve_executable_path silently fell back to
// the unresolved bare name for *every* profile, every launch - the
// hijack-safe guarantee this function exists to provide (skip
// non-absolute/relative PATH entries) was quietly not enforced at all on
// non-Windows, even though the pane still happened to launch (the
// downstream spawn call's own OS-level PATH search picks up the slack on
// POSIX, unlike Windows CreateProcess). std::env::split_paths already
// knows the correct separator per-platform, so this now uses it instead
// of manual splitting; the PATHEXT/System32-first/has_ext logic is
// Windows-specific by nature (Unix executables aren't distinguished by
// extension) and moves behind #[cfg(windows)] rather than growing a
// parallel Unix equivalent - the executable bit is unix's own extension.
fn safe_search_path(name: &str) -> Option<String> {
  let path_var = std::env::var_os("PATH").unwrap_or_default();
  let mut dirs: Vec<std::path::PathBuf> = Vec::new();
  #[cfg(windows)]
  if let Ok(system_root) = std::env::var("SystemRoot") {
    dirs.push(std::path::PathBuf::from(format!("{system_root}\\System32")));
  }
  dirs.extend(std::env::split_paths(&path_var));

  #[cfg(windows)]
  let has_ext = Path::new(name).extension().is_some();
  #[cfg(windows)]
  let exts: Vec<String> = std::env::var("PATHEXT")
    .unwrap_or_else(|_| ".EXE;.CMD;.BAT;.COM".to_string())
    .split(';')
    .filter(|s| !s.is_empty())
    .map(|s| s.to_string())
    .collect();

  for dir in dirs {
    if !dir.is_absolute() {
      continue; // ハイジャック防止: 相対/カレント由来は除外
    }
    #[cfg(windows)]
    {
      if has_ext {
        let p = dir.join(name);
        if p.is_file() {
          return Some(p.to_string_lossy().to_string());
        }
      } else {
        for ext in &exts {
          let p = dir.join(format!("{name}{ext}"));
          if p.is_file() {
            return Some(p.to_string_lossy().to_string());
          }
        }
      }
    }
    #[cfg(unix)]
    {
      let p = dir.join(name);
      if is_executable_file(&p) {
        return Some(p.to_string_lossy().to_string());
      }
    }
  }
  None
}

#[cfg(unix)]
fn is_executable_file(p: &Path) -> bool {
  use std::os::unix::fs::PermissionsExt;
  std::fs::metadata(p).map(|m| m.is_file() && m.permissions().mode() & 0o111 != 0).unwrap_or(false)
}

// spec: セッション復帰トークンの付加. Rust port of SessionProfile.cs's
// EffectiveArgs/ResumeArgsFor: appends the CLI-appropriate resume token
// to the launch args when resume_on_start is set. codex's "resume" is a
// subcommand but the launch command is `cmd /c codex.cmd ...`, so a
// trailing append still works.
pub fn resume_args_for(command_type: &str) -> Vec<String> {
  match command_type {
    "ClaudeCode" | "CopilotCli" => vec!["--resume".to_string()],
    "Codex" => vec!["resume".to_string()],
    _ => vec![],
  }
}

pub fn effective_args(profile: &SessionProfile) -> Vec<String> {
  if !profile.resume_on_start {
    return profile.args.clone();
  }
  let resume = resume_args_for(&profile.command_type);
  if resume.is_empty() {
    return profile.args.clone();
  }
  let mut args = profile.args.clone();
  args.extend(resume);
  args
}

// spec: 送信前のテキスト整形 (per-command). Rust port of SessionProfile.cs's
// IsCommentLine/FilterLinesForSend (found missing entirely in the phase
// 8.1 parity audit): drops lines starting with any comment_prefixes
// entry (no leading-whitespace trim - indented comments are NOT
// stripped, matching the original), and collapses consecutive blank
// lines to one when collapse_blank_lines is set.
fn is_comment_line(line: &str, comment_prefixes: &[String]) -> bool {
  comment_prefixes.iter().any(|p| !p.is_empty() && line.starts_with(p.as_str()))
}

pub fn filter_lines_for_send(raw_lines: &[String], collapse_blank_lines: bool, comment_prefixes: &[String]) -> Vec<String> {
  let mut result = Vec::with_capacity(raw_lines.len());
  let mut prev_blank = false;
  for line in raw_lines {
    if is_comment_line(line, comment_prefixes) {
      continue;
    }
    let is_blank = line.trim().is_empty();
    if is_blank && collapse_blank_lines && prev_blank {
      continue;
    }
    result.push(line.clone());
    prev_blank = is_blank;
  }
  result
}

// spec: 送信前のテキスト整形 - コマンドごとの設定から、アプリ全体の設定へ
// 変更(ユーザー要望、2026-08-04)。以前はSessionProfile自身のフィールド
// (collapse_blank_lines/comment_prefixes、コマンドごとに編集可能)だった
// が、共通入力欄・エディタ連携以外の送信経路(クイック送信・プロンプト
// 再送信・MCP経由の他CLIからの送信・アイドル時自動送信等)にも意図せず
// 適用されてしまっており分かりにくいとの指摘を受け、(1)適用範囲を共通
// 入力欄とエディタ連携の2経路だけに絞り、(2)設定自体もコマンドごとでは
// なくアプリ全体の単一設定に変更した。永続化はeditor_bridge.rsの
// EditorSettingsFileと同じパターン(%LOCALAPPDATA%\amm\ 配下のJSON、
// どの.ammファイルを開いていても常に同じ内容)。
#[derive(Clone, serde::Serialize, serde::Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct FormatSettingsFile {
  #[serde(default = "default_true")]
  pub collapse_blank_lines: bool,
  #[serde(default = "default_comment_prefixes")]
  pub comment_prefixes: Vec<String>,
}

impl Default for FormatSettingsFile {
  fn default() -> Self {
    Self { collapse_blank_lines: true, comment_prefixes: default_comment_prefixes() }
  }
}

fn format_settings_path() -> PathBuf {
  crate::app_data_base_dir().join("amm").join("format-settings.json")
}

pub fn load_format_settings() -> FormatSettingsFile {
  let Ok(text) = std::fs::read_to_string(format_settings_path()) else {
    return FormatSettingsFile::default();
  };
  serde_json::from_str(&text).unwrap_or_default()
}

pub fn save_format_settings(settings: &FormatSettingsFile) {
  let dir = crate::app_data_base_dir().join("amm");
  let _ = std::fs::create_dir_all(&dir);
  if let Ok(json) = serde_json::to_string_pretty(settings) {
    let _ = std::fs::write(format_settings_path(), json);
  }
}

#[tauri::command]
pub(crate) fn get_format_settings() -> FormatSettingsFile {
  load_format_settings()
}

#[tauri::command]
pub(crate) fn set_format_settings(settings: FormatSettingsFile) {
  save_format_settings(&settings);
}

// spec: quick-command-register - コマンド(プロファイル)ごとの設定から、
// アプリ全体で共有する単一リストへ変更(ユーザー要望、2026-08-04: 「クイック
// 送信は、アプリ共通の設定で良い。ペイン毎の設定は不要とする」)。以前は
// SessionProfile.quick_prompts(プロファイルごと)だったため、ad-hocペイン
// (プロファイル未紐付け)では登録先が無く常に無効化されていた。永続化は
// format-settings.json等と同じパターン。
#[derive(Clone, serde::Serialize, serde::Deserialize)]
pub struct QuickPrompt {
  pub label: String,
  pub prompt: String,
}

fn quick_prompts_path() -> PathBuf {
  crate::app_data_base_dir().join("amm").join("quick-prompts.json")
}

pub fn load_quick_prompts() -> Vec<QuickPrompt> {
  let Ok(text) = std::fs::read_to_string(quick_prompts_path()) else {
    return vec![];
  };
  serde_json::from_str(&text).unwrap_or_default()
}

pub fn save_quick_prompts(prompts: &Vec<QuickPrompt>) {
  let dir = crate::app_data_base_dir().join("amm");
  let _ = std::fs::create_dir_all(&dir);
  if let Ok(json) = serde_json::to_string_pretty(prompts) {
    let _ = std::fs::write(quick_prompts_path(), json);
  }
}

#[tauri::command]
pub(crate) fn get_quick_prompts() -> Vec<QuickPrompt> {
  load_quick_prompts()
}

#[tauri::command]
pub(crate) fn set_quick_prompts(prompts: Vec<QuickPrompt>) {
  save_quick_prompts(&prompts);
}

// spec: mcp-gateway's "外部 MCP サーバー設定" - port of McpServerConfig.cs.
// Lives here (not gateway.rs) since it's part of profiles.amm's own schema,
// same reasoning as WindowGeometryEntry above.
fn default_max_restarts() -> u32 {
  3
}

// spec: mcp-gateway's add-mcp-http-transport change - transport discriminator.
// Missing/omitted `type` (every pre-existing mcp-servers.json/profiles.amm
// entry) deserializes to Stdio via #[default], so old files keep working
// unchanged.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize, Default)]
#[serde(rename_all = "lowercase")]
pub enum McpTransportKind {
  #[default]
  Stdio,
  Http,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct McpServerConfig {
  pub name: String,
  // spec: add-mcp-http-transport - kept flat (not split into enum variants
  // per transport) so the many existing call sites that read `.command`/
  // `.name` directly (gateway.rs, mcp_server_key, the JS edit dialog) don't
  // need a match arm added everywhere; see design.md decision 1.
  #[serde(default, rename = "type")]
  pub transport: McpTransportKind,
  // Required for stdio, ignored for http (defaults to "" so http-only JSON
  // like the Obsidian example doesn't need a dummy command field).
  #[serde(default)]
  pub command: String,
  #[serde(default)]
  pub args: Vec<String>,
  #[serde(default)]
  pub env: Option<std::collections::HashMap<String, String>>,
  #[serde(default = "default_true")]
  pub auto_start: bool,
  // stdio-only (crash-restart loop); ignored for http, see design.md decision 3.
  #[serde(default = "default_max_restarts")]
  pub max_restarts: u32,
  // Required for http, ignored for stdio.
  #[serde(default)]
  pub url: Option<String>,
  #[serde(default)]
  pub headers: Option<std::collections::HashMap<String, String>>,
  #[serde(default)]
  pub skip_tls_verify: bool,
}

#[derive(Debug, Clone, Serialize, Deserialize, Default)]
pub struct ProfilesFile {
  #[serde(default)]
  pub profiles: Vec<SessionProfile>,
  #[serde(default, rename = "mcpServers")]
  pub mcp_servers: Vec<McpServerConfig>,
}

// spec: プロファイルファイルの解決順序 - argument validation half. Rust port
// of AppLaunchOptions.Parse's argv loop: rejects unrecognized `--` flags
// and a 2nd positional path argument, matching the .NET original's
// ArgumentException behavior exactly (found missing in the phase 8.1
// parity audit - the Rust version previously silently took only argv[1]
// and never validated malformed invocations at all).
pub fn parse_profiles_path_arg(args: &[String]) -> Result<Option<String>, String> {
  let mut explicit: Option<String> = None;
  for arg in args {
    if arg.starts_with("--") {
      return Err(format!("不明な引数です: {arg}"));
    }
    if explicit.is_some() {
      return Err("profiles 設定ファイルのパスは 1 つだけ指定できます。".to_string());
    }
    explicit = Some(arg.clone());
  }
  Ok(explicit)
}

// spec: プロファイルファイルの解決順序. explicit_arg is a CLI positional
// argument if one was given at launch (std::env::args, resolved by the
// caller). Falls back to an exe-adjacent profiles.amm if one already
// exists there (dev-mode/portable installs), otherwise to the process's
// current directory - or Documents, if that's a system/admin-owned tree -
// since this is also what the exit-time "save unsaved changes?" prompt
// shows/writes to when the user never did File>Open/Save As (see
// is_system_protected_dir's doc comment in lib.rs, user report 2026-08-09:
// a fresh installer-based install had no exe-adjacent profiles.amm to
// inherit, so this used to fall through to the exe's own directory -
// Program Files on Windows - which isn't a sane place to default a save
// target to, quite apart from usually not being user-writable at all).
pub fn resolve_profiles_path(explicit_arg: Option<&str>) -> PathBuf {
  if let Some(arg) = explicit_arg {
    let p = PathBuf::from(arg);
    if p.is_relative() {
      return std::env::current_dir().unwrap_or_default().join(p);
    }
    return p;
  }
  let exe_dir = std::env::current_exe()
    .ok()
    .and_then(|p| p.parent().map(|p| p.to_path_buf()))
    .unwrap_or_default();
  // In a macOS .app bundle, the executable lives at Contents/MacOS/<name>
  // while bundled resources (profiles.amm) land at Contents/Resources/ - a
  // sibling directory, not alongside the exe the way Windows' flat
  // install layout has it. Found via a real-machine check (add-macos-
  // support): this function's "next to the exe" assumption meant the
  // bundled default profiles.amm was never found at all on macOS, so a
  // fresh install silently started with zero profiles. Only redirects
  // when the bundle-relative file actually exists, so a dev-mode raw
  // binary with a manually-placed profiles.amm next to it still works.
  #[cfg(target_os = "macos")]
  {
    if let Some(bundled) = macos_bundle_profiles_path(&exe_dir) {
      if bundled.is_file() {
        return bundled;
      }
    }
  }
  let exe_adjacent = exe_dir.join("profiles.amm");
  if exe_adjacent.is_file() {
    return exe_adjacent;
  }
  let default_dir = match std::env::current_dir() {
    Ok(dir) if !crate::is_system_protected_dir(&dir) => dir,
    _ => crate::documents_dir(),
  };
  default_dir.join("profiles.amm")
}

#[cfg(target_os = "macos")]
fn macos_bundle_profiles_path(exe_dir: &std::path::Path) -> Option<PathBuf> {
  exe_dir.parent().map(|contents| contents.join("Resources").join("profiles.amm"))
}

// spec: pane-management's new "外部 .amm ファイルの自動起動確認" requirement -
// tracks which explicitly-opened profiles.amm paths the user has already
// approved auto-starting mcpServers for, so re-opening the same trusted file
// doesn't re-prompt every time (mirrors the .NET original's persisted-trust
// intent). The default exe-adjacent profiles.amm never goes through this
// check at all (caller only consults it for an explicit CLI-arg/file-
// association path).
//
// security: code-review 2026-07-26 finding (TOFU bypass). Trust was
// previously keyed on the path string alone, so once a path was approved,
// *any future content* at that same path was auto-started without
// re-prompting - a file swap after the initial approval (a synced folder,
// a compromised download mirror re-using the same filename, etc.) silently
// bypassed the confirmation gate this mechanism exists for. Trust is now
// keyed on (path, content hash) together: a fresh content_hash is recorded
// on approval and re-checked on every lookup, so any change to the file
// forces re-confirmation. FNV-1a (self-contained, no new dependency) is
// used rather than a cryptographic hash - this only needs to detect
// "did the file change since I trusted it", not resist a deliberate hash
// collision attack, and pulling in a crypto crate for that would be
// overkill. Old trusted-profiles.json files (bare path-string array, no
// hash) fail to deserialize into the new schema and are treated as empty
// (load_trusted_entries' `.unwrap_or_default()`) - a safe-by-default
// one-time re-prompt on upgrade, not a silent trust downgrade.
fn trusted_paths_file() -> PathBuf {
  crate::app_data_base_dir().join("amm").join("trusted-profiles.json")
}

#[derive(serde::Serialize, serde::Deserialize, Clone)]
struct TrustedProfileEntry {
  path: String,
  content_hash: String,
}

fn fnv1a_hash(data: &[u8]) -> String {
  const FNV_OFFSET: u64 = 0xcbf29ce484222325;
  const FNV_PRIME: u64 = 0x0000_0100_0000_01b3;
  let mut hash = FNV_OFFSET;
  for &byte in data {
    hash ^= byte as u64;
    hash = hash.wrapping_mul(FNV_PRIME);
  }
  format!("{hash:016x}")
}

fn load_trusted_entries_from(list_file: &Path) -> Vec<TrustedProfileEntry> {
  std::fs::read_to_string(list_file).ok().and_then(|t| serde_json::from_str(&t).ok()).unwrap_or_default()
}

// Testable cores (take the trusted-list file location as a parameter so
// unit tests can point at a temp file instead of the real
// %LOCALAPPDATA%\amm\trusted-profiles.json - std::env::set_var isn't
// process-isolated across Rust's parallel test threads, so that path can't
// safely be overridden per-test).
fn is_path_trusted_in(list_file: &Path, path: &Path) -> bool {
  let Ok(content) = std::fs::read(path) else {
    return false;
  };
  let canon = path.to_string_lossy().to_lowercase();
  let hash = fnv1a_hash(&content);
  load_trusted_entries_from(list_file).iter().any(|e| e.path.to_lowercase() == canon && e.content_hash == hash)
}

fn mark_path_trusted_in(list_file: &Path, path: &Path) {
  let Ok(content) = std::fs::read(path) else {
    return;
  };
  let hash = fnv1a_hash(&content);
  let canon = path.to_string_lossy().to_string();
  let mut list = load_trusted_entries_from(list_file);
  // Replace any stale entry for the same path (re-approving updates the
  // trusted hash to the current content rather than leaving the old one).
  list.retain(|e| !e.path.eq_ignore_ascii_case(&canon));
  list.push(TrustedProfileEntry { path: canon, content_hash: hash });
  if let Some(dir) = list_file.parent() {
    let _ = std::fs::create_dir_all(dir);
  }
  if let Ok(json) = serde_json::to_string_pretty(&list) {
    let _ = std::fs::write(list_file, json);
  }
}

pub fn is_path_trusted(path: &Path) -> bool {
  is_path_trusted_in(&trusted_paths_file(), path)
}

pub fn mark_path_trusted(path: &Path) {
  mark_path_trusted_in(&trusted_paths_file(), path)
}

#[derive(Debug)]
pub enum LoadError {
  InvalidJson(String),
}

// spec scenarios: missing file -> single default CMD profile (no error);
// malformed JSON -> error (caller falls back to default CMD + warns).
pub fn load_profiles(path: &PathBuf) -> Result<ProfilesFile, LoadError> {
  let text = match std::fs::read_to_string(path) {
    Ok(t) => t,
    Err(_) => return Ok(ProfilesFile { profiles: vec![SessionProfile::default_cmd()], mcp_servers: vec![] }),
  };
  let mut file: ProfilesFile =
    serde_json::from_str(&text).map_err(|e| LoadError::InvalidJson(format!("{}: {e}", path.display())))?;
  file.profiles = file.profiles.into_iter().map(|p| p.migrate()).collect();
  Ok(file)
}

// spec: profile-schema's "プロファイルファイルのホットリロード" - pure decision
// function for lib.rs's poll-based watcher (300ms interval), extracted so
// the debounce logic (only reload once the mtime has been stable across
// two consecutive polls) can be unit tested without spawning a thread.
pub(crate) fn hot_reload_should_apply<T: PartialEq>(committed: &Option<T>, candidate: &Option<T>, current: &Option<T>) -> bool {
  current != committed && current == candidate
}

pub fn save_profiles(path: &PathBuf, file: &ProfilesFile) -> std::io::Result<()> {
  let json = serde_json::to_string_pretty(file)?;
  // Same-directory temp + rename, matching AtomicFileWriter.cs's approach
  // (spec elsewhere requires atomic writes for profiles.amm). The temp
  // filename is randomized (not fixed) so concurrent writers targeting the
  // same profiles path (e.g. two amm instances, or a retry racing a prior
  // write) don't collide on the same tmp file, and the write is fsync'd
  // before the rename so the rename can't observe a partially-flushed file.
  let tmp = path.with_extension(format!("amm.{}.tmp", uuid::Uuid::new_v4()));
  {
    use std::io::Write;
    let mut f = std::fs::File::create(&tmp)?;
    f.write_all(json.as_bytes())?;
    f.sync_all()?;
  }
  std::fs::rename(&tmp, path)
}

// spec: quick-command-register's "テキスト欄の初期値" rule ("ANSI除去後の直前
//送信テキスト全文").
pub fn strip_ansi(text: &str) -> String {
  crate::ansi::strip_ansi(text)
}

// spec: quick-command-register's label-generation rule ("ANSI除去後の直前送信
// テキストの先頭行を最大30文字に切り詰め").
pub fn quick_prompt_label_suggestion(text: &str) -> String {
  let stripped = strip_ansi(text);
  let first_line = stripped.lines().next().unwrap_or("");
  first_line.chars().take(30).collect()
}

// ---- command-import-export (spec: command-import-export). Data-layer only
// - the ExportProfilesDialog/ImportProfilesDialog checklist UIs and the
// save/open file pickers are deferred; these are the pure/IO primitives a
// future UI would call. ----

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct ProfilesExportFile {
  pub version: u32,
  pub profiles: Vec<SessionProfile>,
}

// Accepts both the `.ammprofiles` shape ({"version":1,"profiles":[...]}) and
// a bare `SessionProfile[]` root for backward compat, per spec. Applies the
// same migrate() as load_profiles to each entry.
pub fn parse_import_profiles(text: &str) -> Result<Vec<SessionProfile>, String> {
  let root: Value = serde_json::from_str(text).map_err(|e| e.to_string())?;
  let profiles: Vec<SessionProfile> = if root.is_array() {
    serde_json::from_value(root).map_err(|e| e.to_string())?
  } else if let Some(arr) = root.get("profiles") {
    serde_json::from_value(arr.clone()).map_err(|e| e.to_string())?
  } else {
    return Err("ファイルの読み込みに失敗しました。".to_string());
  };
  Ok(profiles.into_iter().map(|p| p.migrate()).collect())
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum ConflictPolicy {
  Skip,
  Rename,
  Overwrite,
}

#[derive(Debug, Clone, Copy, Default, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct ImportSummary {
  pub added: usize,
  pub overwritten: usize,
  pub skipped: usize,
}

fn nickname_key(p: &SessionProfile) -> Option<String> {
  p.nickname.as_ref().map(|n| n.to_lowercase())
}

// spec: dedup key is Nickname (case-insensitive); profiles without a
// nickname never collide and are always added. Entries added earlier in
// the same batch participate in later collision/rename checks (existing
// grows in place).
pub fn merge_imported_profiles(existing: &mut Vec<SessionProfile>, imported: Vec<SessionProfile>, policy: ConflictPolicy) -> ImportSummary {
  let mut summary = ImportSummary::default();
  for mut prof in imported {
    let key = nickname_key(&prof);
    let existing_idx = key.as_ref().and_then(|k| existing.iter().position(|e| nickname_key(e).as_deref() == Some(k.as_str())));
    match existing_idx {
      None => {
        existing.push(prof);
        summary.added += 1;
      }
      Some(idx) => match policy {
        ConflictPolicy::Skip => summary.skipped += 1,
        ConflictPolicy::Overwrite => {
          existing[idx] = prof;
          summary.overwritten += 1;
        }
        ConflictPolicy::Rename => {
          let base = prof.nickname.clone().unwrap_or_default();
          let mut n = 2;
          loop {
            let candidate = format!("{base}_{n}");
            let candidate_key = candidate.to_lowercase();
            if !existing.iter().any(|e| nickname_key(e).as_deref() == Some(candidate_key.as_str())) {
              prof.nickname = Some(candidate);
              break;
            }
            n += 1;
          }
          existing.push(prof);
          summary.added += 1;
        }
      },
    }
  }
  summary
}

// spec: mcp-gateway's「AMM 共通」グループのインポート/エクスポート -
// command-import-export と同じ形(version付きエクスポートファイル、bare
// array/wrapped rootの両対応パース、Skip/Rename/Overwriteのマージ)をMCP
// サーバー設定にも適用する。ConflictPolicy/ImportSummaryは汎用なのでprofiles
// 側の型をそのまま再利用する。
#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct McpServersExportFile {
  pub version: u32,
  pub servers: Vec<McpServerConfig>,
}

// Accepts this export format's own {"version":1,"servers":[...]} shape, the
// live global config file's {"mcpServers":[...]} shape (so a copy of
// mcp-servers.json can be imported directly), and a bare McpServerConfig[]
// root.
pub fn parse_import_mcp_servers(text: &str) -> Result<Vec<McpServerConfig>, String> {
  let root: Value = serde_json::from_str(text).map_err(|e| e.to_string())?;
  let servers: Vec<McpServerConfig> = if root.is_array() {
    serde_json::from_value(root).map_err(|e| e.to_string())?
  } else if let Some(arr) = root.get("servers") {
    serde_json::from_value(arr.clone()).map_err(|e| e.to_string())?
  } else if let Some(arr) = root.get("mcpServers") {
    serde_json::from_value(arr.clone()).map_err(|e| e.to_string())?
  } else {
    return Err("ファイルの読み込みに失敗しました。".to_string());
  };
  Ok(servers)
}

fn mcp_server_key(s: &McpServerConfig) -> String {
  s.name.to_lowercase()
}

// spec: dedup key is name (case-insensitive) - the same field the gateway
// itself already keys statuses by (see openMcpGatewayDialog's cfg.name
// matching against gateway_server_infos).
pub fn merge_imported_mcp_servers(existing: &mut Vec<McpServerConfig>, imported: Vec<McpServerConfig>, policy: ConflictPolicy) -> ImportSummary {
  let mut summary = ImportSummary::default();
  for mut server in imported {
    let key = mcp_server_key(&server);
    let existing_idx = existing.iter().position(|e| mcp_server_key(e) == key);
    match existing_idx {
      None => {
        existing.push(server);
        summary.added += 1;
      }
      Some(idx) => match policy {
        ConflictPolicy::Skip => summary.skipped += 1,
        ConflictPolicy::Overwrite => {
          existing[idx] = server;
          summary.overwritten += 1;
        }
        ConflictPolicy::Rename => {
          let base = server.name.clone();
          let mut n = 2;
          loop {
            let candidate = format!("{base}_{n}");
            let candidate_key = candidate.to_lowercase();
            if !existing.iter().any(|e| mcp_server_key(e) == candidate_key) {
              server.name = candidate;
              break;
            }
            n += 1;
          }
          existing.push(server);
          summary.added += 1;
        }
      },
    }
  }
  summary
}

#[cfg(test)]
mod tests {
  use super::*;

  fn profile_with_nickname(name: &str, nickname: &str) -> SessionProfile {
    let mut p = SessionProfile::default_cmd();
    p.name = name.to_string();
    p.nickname = Some(nickname.to_string());
    p
  }

  // Unique-per-test temp file helper for the trust tests below, so parallel
  // test threads don't collide on the same path.
  fn temp_file(label: &str) -> PathBuf {
    std::env::temp_dir().join(format!(
      "amm-trust-test-{label}-{}-{}",
      std::process::id(),
      std::time::SystemTime::now().duration_since(std::time::UNIX_EPOCH).unwrap().as_nanos()
    ))
  }

  #[test]
  fn is_path_trusted_true_after_marking_same_content() {
    let list_file = temp_file("list-a");
    let profile_path = temp_file("profile-a.amm");
    std::fs::write(&profile_path, r#"{"mcpServers":[]}"#).unwrap();

    assert!(!is_path_trusted_in(&list_file, &profile_path), "must not be trusted before marking");
    mark_path_trusted_in(&list_file, &profile_path);
    assert!(is_path_trusted_in(&list_file, &profile_path), "must be trusted after marking, same content");

    let _ = std::fs::remove_file(&list_file);
    let _ = std::fs::remove_file(&profile_path);
  }

  #[test]
  fn is_path_trusted_false_after_content_changes_at_same_path() {
    // security: regression test for code-review 2026-07-26 finding (TOFU
    // bypass) - trust must not survive a content swap at a previously
    // trusted path.
    let list_file = temp_file("list-b");
    let profile_path = temp_file("profile-b.amm");
    std::fs::write(&profile_path, r#"{"mcpServers":[]}"#).unwrap();
    mark_path_trusted_in(&list_file, &profile_path);
    assert!(is_path_trusted_in(&list_file, &profile_path));

    // Same path, attacker-swapped content.
    std::fs::write(&profile_path, r#"{"mcpServers":[{"name":"evil","command":"calc.exe","autoStart":true}]}"#).unwrap();
    assert!(
      !is_path_trusted_in(&list_file, &profile_path),
      "a content change at a previously-trusted path must force re-confirmation"
    );

    let _ = std::fs::remove_file(&list_file);
    let _ = std::fs::remove_file(&profile_path);
  }

  #[test]
  fn is_path_trusted_false_for_legacy_path_only_trust_file() {
    // Old schema (bare array of path strings, no content hash) must not
    // silently grandfather trust in - safe-by-default one-time re-prompt.
    let list_file = temp_file("list-c");
    let profile_path = temp_file("profile-c.amm");
    std::fs::write(&profile_path, r#"{"mcpServers":[]}"#).unwrap();
    std::fs::write(&list_file, format!("[{:?}]", profile_path.to_string_lossy())).unwrap();

    assert!(!is_path_trusted_in(&list_file, &profile_path));

    let _ = std::fs::remove_file(&list_file);
    let _ = std::fs::remove_file(&profile_path);
  }

  #[test]
  fn mark_path_trusted_replaces_stale_entry_for_same_path() {
    let list_file = temp_file("list-d");
    let profile_path = temp_file("profile-d.amm");
    std::fs::write(&profile_path, "v1").unwrap();
    mark_path_trusted_in(&list_file, &profile_path);

    std::fs::write(&profile_path, "v2").unwrap();
    mark_path_trusted_in(&list_file, &profile_path);

    let entries = load_trusted_entries_from(&list_file);
    let matches: Vec<_> = entries.iter().filter(|e| e.path.eq_ignore_ascii_case(&profile_path.to_string_lossy())).collect();
    assert_eq!(matches.len(), 1, "re-approving must replace, not duplicate, the entry for the same path");
    assert!(is_path_trusted_in(&list_file, &profile_path), "the updated (v2) content must be trusted");

    let _ = std::fs::remove_file(&list_file);
    let _ = std::fs::remove_file(&profile_path);
  }

  #[test]
  fn resolve_working_directory_none_for_missing_or_blank() {
    // spec: ResolveWorkingDirectory "未指定/空なら現在のカレントディレクトリを
    // 返す" - None here means "caller should fall back to current_dir()"
    // (found via live CDP testing, phase 8.2, that "" was short-circuiting
    // that fallback and producing an unusable empty PathBuf instead).
    assert_eq!(resolve_working_directory(None), None);
    assert_eq!(resolve_working_directory(Some("")), None);
    assert_eq!(resolve_working_directory(Some("   ")), None);
  }

  #[test]
  fn resolve_working_directory_expands_env_vars() {
    std::env::set_var("AMM_TEST_WD_VAR", "C:\\amm-test-target");
    assert_eq!(resolve_working_directory(Some("%AMM_TEST_WD_VAR%\\sub")), Some("C:\\amm-test-target\\sub".to_string()));
    std::env::remove_var("AMM_TEST_WD_VAR");
  }

  #[test]
  fn resolve_working_directory_passes_through_plain_paths() {
    assert_eq!(resolve_working_directory(Some("D:\\work")), Some("D:\\work".to_string()));
  }

  #[test]
  fn resolve_executable_path_leaves_explicit_paths_alone() {
    assert_eq!(resolve_executable_path("C:\\Windows\\System32\\cmd.exe"), "C:\\Windows\\System32\\cmd.exe");
    assert_eq!(resolve_executable_path(".\\relative\\tool.exe"), ".\\relative\\tool.exe");
  }

  #[cfg(windows)]
  #[test]
  fn resolve_executable_path_resolves_bare_name_via_system32() {
    // cmd.exe always exists in System32 on the CI/dev Windows machine.
    let resolved = resolve_executable_path("cmd.exe");
    assert!(resolved.to_lowercase().ends_with("system32\\cmd.exe"), "got: {resolved}");
  }

  // macOS/Linux counterpart (found running the suite on real macOS
  // hardware while adding macOS support) - /bin/sh always exists and is
  // executable on any Unix system, the closest equivalent to cmd.exe's
  // "always in System32" guarantee used above.
  #[cfg(unix)]
  #[test]
  fn resolve_executable_path_resolves_bare_name_via_path() {
    let resolved = resolve_executable_path("sh");
    assert!(resolved.ends_with("/sh"), "got: {resolved}");
    assert!(Path::new(&resolved).is_absolute(), "got: {resolved}");
  }

  #[test]
  fn resolve_executable_path_falls_back_to_original_when_not_found() {
    assert_eq!(resolve_executable_path("definitely-not-a-real-tool-xyz"), "definitely-not-a-real-tool-xyz");
  }

  #[test]
  fn resolve_output_encoding_maps_shift_jis_case_insensitively() {
    assert_eq!(resolve_output_encoding(Some("Shift_JIS")), encoding_rs::SHIFT_JIS);
    assert_eq!(resolve_output_encoding(Some("SHIFT-JIS")), encoding_rs::SHIFT_JIS);
  }

  #[test]
  fn resolve_output_encoding_defaults_to_utf8() {
    assert_eq!(resolve_output_encoding(Some("UTF-8")), encoding_rs::UTF_8);
    assert_eq!(resolve_output_encoding(Some("UTF8")), encoding_rs::UTF_8);
    assert_eq!(resolve_output_encoding(None), encoding_rs::UTF_8);
    assert_eq!(resolve_output_encoding(Some("bogus")), encoding_rs::UTF_8);
  }

  #[test]
  fn build_chcp_wrapped_command_quotes_tokens_with_spaces() {
    let (exe, args) = build_chcp_wrapped_command(
      "C:\\Program Files\\tool.exe",
      &["--flag".to_string(), "value with space".to_string()],
    )
    .unwrap();
    assert_eq!(exe, "cmd.exe");
    assert_eq!(
      args,
      vec![
        "/d".to_string(),
        "/s".to_string(),
        "/c".to_string(),
        "chcp 65001 > nul && \"C:\\Program Files\\tool.exe\" --flag \"value with space\"".to_string(),
      ]
    );
  }

  #[test]
  fn build_chcp_wrapped_command_leaves_simple_tokens_unquoted() {
    let (exe, args) = build_chcp_wrapped_command("cmd.exe", &["/c".to_string(), "dir".to_string()]).unwrap();
    assert_eq!(exe, "cmd.exe");
    assert_eq!(
      args,
      vec!["/d".to_string(), "/s".to_string(), "/c".to_string(), "chcp 65001 > nul && cmd.exe /c dir".to_string()]
    );
  }

  #[test]
  fn quote_command_token_rejects_embedded_quote() {
    // security: regression test for code-review 2026-07-26 finding H-1.
    // Embedded `"` cannot be safely escaped through the
    // CommandBuilder::append_quoted + cmd.exe double-quoting pipeline (see
    // the doc comment on quote_command_token), so it must be rejected
    // rather than silently mis-escaped.
    let evil = "foo\" & echo INJECTED & \"bar";
    assert!(quote_command_token(evil).is_err());
    assert!(build_chcp_wrapped_command("shell.exe", &[evil.to_string()]).is_err());
  }

  #[test]
  fn quote_command_token_quotes_tokens_with_no_whitespace_but_metachars() {
    // security: a token with cmd.exe metacharacters and NO whitespace used
    // to sail through completely unquoted (needs_quotes only checked
    // whitespace), injecting directly.
    let evil = "a&calc.exe&b";
    assert_eq!(quote_command_token(evil).unwrap(), "\"a&calc.exe&b\"");
  }

  // Windows-only: actually spawns a real cmd.exe (see doc comment below),
  // and chcp/cmd.exe wrapping (auto_chcp) is itself a Windows-only console
  // codepage concern with no macOS/Linux equivalent (found running the
  // suite on real macOS hardware while adding macOS support).
  #[cfg(windows)]
  #[test]
  fn chcp_wrapped_command_blocks_metachar_injection() {
    // security: empirical end-to-end regression test for H-1. Actually
    // spawns the wrapped command line through real cmd.exe (std::process::Command
    // performs the same Win32 CRT-style argument quoting portable_pty's
    // CommandBuilder does for the production spawn path, so this is a
    // faithful proxy without ConPTY's async-lifecycle complications - a
    // PTY-based version of this test was tried and hung waiting on the
    // child, unrelated to the fix itself). Payload has no embedded quote
    // (that class is covered by the rejection test above) but does have
    // unquoted-looking metacharacters that historically escaped quoting
    // when a token had no whitespace. Detects injection via a side-effect
    // marker file.
    let marker = std::env::temp_dir().join(format!(
      "amm-quote-injection-test-{}-{}.marker",
      std::process::id(),
      std::time::SystemTime::now().duration_since(std::time::UNIX_EPOCH).unwrap().as_nanos()
    ));
    let _ = std::fs::remove_file(&marker);
    let marker_display = marker.display().to_string();
    let evil = format!("safe&echo^INJECTED>{marker_display}&rem");
    let (exe, args) = build_chcp_wrapped_command("cmd.exe", &["/c".to_string(), "echo".to_string(), evil]).unwrap();
    let status = std::process::Command::new(exe)
      .args(args)
      .status()
      .expect("failed to spawn cmd.exe for injection regression test");
    assert!(status.success(), "wrapped command should still run the intended (benign) command successfully");
    assert!(
      !marker.exists(),
      "metacharacter injection succeeded: marker file was created by a smuggled command"
    );
    let _ = std::fs::remove_file(&marker);
  }

  #[test]
  fn resume_args_for_matches_per_command_type() {
    assert_eq!(resume_args_for("ClaudeCode"), vec!["--resume".to_string()]);
    assert_eq!(resume_args_for("CopilotCli"), vec!["--resume".to_string()]);
    assert_eq!(resume_args_for("Codex"), vec!["resume".to_string()]);
    assert!(resume_args_for("Cmd").is_empty());
    assert!(resume_args_for("Other").is_empty());
  }

  #[test]
  fn effective_args_appends_resume_token_only_when_enabled() {
    let mut p = SessionProfile::default_cmd();
    p.command_type = "ClaudeCode".to_string();
    p.args = vec!["--foo".to_string()];
    p.resume_on_start = false;
    assert_eq!(effective_args(&p), vec!["--foo".to_string()]);

    p.resume_on_start = true;
    assert_eq!(effective_args(&p), vec!["--foo".to_string(), "--resume".to_string()]);
  }

  fn lines(s: &[&str]) -> Vec<String> {
    s.iter().map(|s| s.to_string()).collect()
  }

  #[test]
  fn filter_lines_drops_comment_lines() {
    let prefixes = default_comment_prefixes();
    let raw = lines(&["'comment", "// also comment", "real line", "  // indented not stripped"]);
    let out = filter_lines_for_send(&raw, true, &prefixes);
    assert_eq!(out, lines(&["real line", "  // indented not stripped"]));
  }

  #[test]
  fn filter_lines_collapses_consecutive_blank_lines_when_enabled() {
    let raw = lines(&["a", "", "", "", "b"]);
    let out = filter_lines_for_send(&raw, true, &[]);
    assert_eq!(out, lines(&["a", "", "b"]));
  }

  #[test]
  fn filter_lines_keeps_all_blank_lines_when_disabled() {
    let raw = lines(&["a", "", "", "b"]);
    let out = filter_lines_for_send(&raw, false, &[]);
    assert_eq!(out, lines(&["a", "", "", "b"]));
  }

  #[test]
  fn filter_lines_empty_comment_prefixes_never_matches() {
    let raw = lines(&["'not a comment without prefixes configured"]);
    let out = filter_lines_for_send(&raw, true, &[]);
    assert_eq!(out, raw);
  }

  #[test]
  fn parse_profiles_path_arg_accepts_single_positional() {
    let args = vec!["C:\\foo\\profiles.amm".to_string()];
    assert_eq!(parse_profiles_path_arg(&args).unwrap(), Some("C:\\foo\\profiles.amm".to_string()));
    assert_eq!(parse_profiles_path_arg(&[]).unwrap(), None);
  }

  #[cfg(target_os = "macos")]
  #[test]
  fn macos_bundle_profiles_path_points_at_resources_sibling_of_macos_dir() {
    // A real macOS .app's exe lives at Contents/MacOS/<name>; profiles.amm
    // bundled via tauri.conf.json's bundle.resources lands at
    // Contents/Resources/profiles.amm - a sibling of MacOS/, not inside it.
    let exe_dir = Path::new("/Applications/amm.app/Contents/MacOS");
    assert_eq!(
      macos_bundle_profiles_path(exe_dir),
      Some(PathBuf::from("/Applications/amm.app/Contents/Resources/profiles.amm"))
    );
  }

  #[test]
  fn parse_profiles_path_arg_rejects_unknown_flag() {
    let args = vec!["--bogus".to_string()];
    assert!(parse_profiles_path_arg(&args).is_err());
  }

  #[test]
  fn parse_profiles_path_arg_rejects_second_positional() {
    let args = vec!["a.amm".to_string(), "b.amm".to_string()];
    assert!(parse_profiles_path_arg(&args).is_err());
  }

  #[test]
  fn infer_command_type_prefers_nickname() {
    let mut p = SessionProfile::default_cmd();
    p.command_type = String::new();
    p.nickname = Some("Claude".to_string());
    p.executable = "cmd.exe".to_string();
    assert_eq!(infer_command_type(&p), Some("ClaudeCode"));
  }

  #[test]
  fn infer_command_type_from_executable() {
    let mut p = SessionProfile::default_cmd();
    p.command_type = String::new();
    p.nickname = None;
    p.executable = "C:\\tools\\claude.exe".to_string();
    assert_eq!(infer_command_type(&p), Some("ClaudeCode"));

    p.executable = "powershell.exe".to_string();
    assert_eq!(infer_command_type(&p), Some("PowerShell"));
  }

  #[test]
  fn infer_command_type_args_token_match_not_substring() {
    let mut p = SessionProfile::default_cmd();
    p.command_type = String::new();
    p.nickname = None;
    p.executable = "cmd.exe".to_string();
    // Path substring containing "codex" must NOT match (token-based only).
    p.args = vec!["/c".to_string(), "cd".to_string(), "C:\\my-codex-notes\\".to_string()];
    assert_eq!(infer_command_type(&p), None);

    // A genuine "codex" token (e.g. `cmd /c codex.cmd`) must match.
    p.args = vec!["/c".to_string(), "codex.cmd".to_string()];
    assert_eq!(infer_command_type(&p), Some("Codex"));
  }

  #[test]
  fn infer_command_type_cmd_wrapper_is_other() {
    let mut p = SessionProfile::default_cmd();
    p.command_type = String::new();
    p.nickname = None;
    p.executable = "cmd.exe".to_string();
    p.args = vec!["/c".to_string(), "gemini.cmd".to_string()];
    assert_eq!(infer_command_type(&p), None);
  }

  #[test]
  fn quick_prompt_label_strips_ansi_and_truncates_first_line() {
    let text = "\x1b[31mhello world\x1b[0m\nsecond line here that is long";
    assert_eq!(quick_prompt_label_suggestion(text), "hello world");

    let long_single_line = "a".repeat(45);
    assert_eq!(quick_prompt_label_suggestion(&long_single_line), "a".repeat(30));
  }

  #[test]
  fn strip_ansi_keeps_full_multiline_text_untruncated() {
    let text = "\x1b[31mhello world\x1b[0m\nsecond line here that is long";
    assert_eq!(strip_ansi(text), "hello world\nsecond line here that is long");
  }

  #[test]
  fn strip_ansi_arrow_key_escape_sequences_become_empty() {
    // spec: quick-command-register's disablement scenario ("矢印キー等のみ").
    assert_eq!(strip_ansi("\x1b[A\x1b[B\x1b[C\x1b[D"), "");
  }

  #[test]
  fn parse_import_accepts_wrapped_and_bare_array_roots() {
    let wrapped = r#"{"version":1,"profiles":[{"name":"a","executable":"cmd.exe"}]}"#;
    assert_eq!(parse_import_profiles(wrapped).unwrap().len(), 1);

    let bare = r#"[{"name":"a","executable":"cmd.exe"},{"name":"b","executable":"pwsh.exe"}]"#;
    assert_eq!(parse_import_profiles(bare).unwrap().len(), 2);

    let invalid = r#"{"notProfiles": []}"#;
    assert!(parse_import_profiles(invalid).is_err());
  }

  #[test]
  fn merge_skip_mode_leaves_existing_untouched() {
    let mut existing = vec![profile_with_nickname("orig", "Foo")];
    let imported = vec![profile_with_nickname("incoming", "foo")];
    let summary = merge_imported_profiles(&mut existing, imported, ConflictPolicy::Skip);
    assert_eq!((summary.added, summary.overwritten, summary.skipped), (0, 0, 1));
    assert_eq!(existing.len(), 1);
    assert_eq!(existing[0].name, "orig");
  }

  #[test]
  fn merge_overwrite_mode_replaces_existing() {
    let mut existing = vec![profile_with_nickname("orig", "Foo")];
    let imported = vec![profile_with_nickname("incoming", "foo")];
    let summary = merge_imported_profiles(&mut existing, imported, ConflictPolicy::Overwrite);
    assert_eq!((summary.added, summary.overwritten, summary.skipped), (0, 1, 0));
    assert_eq!(existing.len(), 1);
    assert_eq!(existing[0].name, "incoming");
  }

  #[test]
  fn merge_rename_mode_appends_numeric_suffix_avoiding_collisions() {
    let mut existing = vec![profile_with_nickname("orig", "Foo"), profile_with_nickname("orig2", "Foo_2")];
    let imported = vec![profile_with_nickname("incoming", "foo")];
    let summary = merge_imported_profiles(&mut existing, imported, ConflictPolicy::Rename);
    assert_eq!((summary.added, summary.overwritten, summary.skipped), (1, 0, 0));
    assert_eq!(existing.len(), 3);
    assert_eq!(existing[2].nickname.as_deref(), Some("foo_3"));
  }

  #[test]
  fn merge_non_colliding_profiles_are_always_added() {
    let mut existing = vec![profile_with_nickname("orig", "Foo")];
    let imported = vec![profile_with_nickname("incoming", "Bar")];
    let summary = merge_imported_profiles(&mut existing, imported, ConflictPolicy::Skip);
    assert_eq!((summary.added, summary.overwritten, summary.skipped), (1, 0, 0));
    assert_eq!(existing.len(), 2);
  }

  fn mcp_server(name: &str) -> McpServerConfig {
    McpServerConfig {
      name: name.to_string(),
      transport: McpTransportKind::Stdio,
      command: "cmd".to_string(),
      args: vec![],
      env: None,
      auto_start: true,
      max_restarts: 3,
      url: None,
      headers: None,
      skip_tls_verify: false,
    }
  }

  fn mcp_http_server(name: &str, url: &str) -> McpServerConfig {
    McpServerConfig {
      name: name.to_string(),
      transport: McpTransportKind::Http,
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
  fn merge_mcp_servers_skip_mode_leaves_existing_untouched() {
    let mut existing = vec![mcp_server("Foo")];
    let imported = vec![mcp_server("foo")];
    let summary = merge_imported_mcp_servers(&mut existing, imported, ConflictPolicy::Skip);
    assert_eq!((summary.added, summary.overwritten, summary.skipped), (0, 0, 1));
    assert_eq!(existing.len(), 1);
    assert_eq!(existing[0].name, "Foo");
  }

  #[test]
  fn merge_mcp_servers_overwrite_mode_replaces_existing() {
    let mut existing = vec![mcp_server("Foo")];
    let mut incoming = mcp_server("foo");
    incoming.command = "npx".to_string();
    let summary = merge_imported_mcp_servers(&mut existing, vec![incoming], ConflictPolicy::Overwrite);
    assert_eq!((summary.added, summary.overwritten, summary.skipped), (0, 1, 0));
    assert_eq!(existing[0].command, "npx");
  }

  #[test]
  fn merge_mcp_servers_rename_mode_appends_numeric_suffix_avoiding_collisions() {
    let mut existing = vec![mcp_server("Foo"), mcp_server("Foo_2")];
    let imported = vec![mcp_server("foo")];
    let summary = merge_imported_mcp_servers(&mut existing, imported, ConflictPolicy::Rename);
    assert_eq!((summary.added, summary.overwritten, summary.skipped), (1, 0, 0));
    assert_eq!(existing.len(), 3);
    assert_eq!(existing[2].name, "foo_3");
  }

  #[test]
  fn parse_import_mcp_servers_accepts_wrapped_bare_and_live_config_roots() {
    assert_eq!(parse_import_mcp_servers(r#"{"version":1,"servers":[{"name":"a","command":"cmd"}]}"#).unwrap().len(), 1);
    assert_eq!(parse_import_mcp_servers(r#"[{"name":"a","command":"cmd"}]"#).unwrap().len(), 1);
    assert_eq!(parse_import_mcp_servers(r#"{"mcpServers":[{"name":"a","command":"cmd"}]}"#).unwrap().len(), 1);
    assert!(parse_import_mcp_servers(r#"{"nope":true}"#).is_err());
  }

  // spec: add-mcp-http-transport - "type" omitted on every pre-existing
  // mcp-servers.json/profiles.amm entry must keep deserializing as stdio.
  #[test]
  fn mcp_server_config_without_type_field_defaults_to_stdio() {
    let servers = parse_import_mcp_servers(r#"[{"name":"fs","command":"npx","args":["-y","x"]}]"#).unwrap();
    assert_eq!(servers[0].transport, McpTransportKind::Stdio);
    assert_eq!(servers[0].command, "npx");
    assert!(servers[0].url.is_none());
  }

  // spec: add-mcp-http-transport - the Obsidian example shape
  // ({"type":"http","url":"...","headers":{"Authorization":"Bearer ..."}}) is
  // the motivating case this whole change exists for; must round-trip.
  #[test]
  fn mcp_server_config_parses_http_type_with_url_and_headers() {
    let json = r#"[{"name":"obsidian","type":"http","url":"https://127.0.0.1:27124/mcp/","headers":{"Authorization":"Bearer tok"}}]"#;
    let servers = parse_import_mcp_servers(json).unwrap();
    assert_eq!(servers[0].transport, McpTransportKind::Http);
    assert_eq!(servers[0].url.as_deref(), Some("https://127.0.0.1:27124/mcp/"));
    assert_eq!(servers[0].headers.as_ref().unwrap().get("Authorization").unwrap(), "Bearer tok");
    assert!(!servers[0].skip_tls_verify); // default false even though url is https+self-signed-looking
    assert_eq!(servers[0].command, ""); // ignored for http, defaults to empty
  }

  #[test]
  fn mcp_http_server_helper_round_trips_through_export_import() {
    let mut existing: Vec<McpServerConfig> = vec![];
    let summary = merge_imported_mcp_servers(&mut existing, vec![mcp_http_server("obsidian", "https://127.0.0.1:27124/mcp/")], ConflictPolicy::Skip);
    assert_eq!(summary.added, 1);
    assert_eq!(existing[0].transport, McpTransportKind::Http);
    assert_eq!(existing[0].url.as_deref(), Some("https://127.0.0.1:27124/mcp/"));
  }

  #[test]
  fn hot_reload_no_change_never_applies() {
    let m = Some(1u64);
    assert!(!hot_reload_should_apply(&m, &m, &m));
  }

  #[test]
  fn hot_reload_change_not_yet_stable_does_not_apply() {
    // mtime just moved from 1 -> 2 this poll; candidate is still the old
    // value from the *previous* poll, so it hasn't been observed twice yet.
    let committed = Some(1u64);
    let candidate = Some(1u64);
    let current = Some(2u64);
    assert!(!hot_reload_should_apply(&committed, &candidate, &current));
  }

  #[test]
  fn hot_reload_stable_change_applies() {
    // mtime was 2 on the previous poll (candidate) and is still 2 now
    // (current), and differs from what's committed -> debounced, reload.
    let committed = Some(1u64);
    let candidate = Some(2u64);
    let current = Some(2u64);
    assert!(hot_reload_should_apply(&committed, &candidate, &current));
  }

  #[test]
  fn hot_reload_still_changing_keeps_waiting() {
    // mtime moved again before stabilizing (2 -> 3): candidate != current,
    // so still not applied even though both differ from committed.
    let committed = Some(1u64);
    let candidate = Some(2u64);
    let current = Some(3u64);
    assert!(!hot_reload_should_apply(&committed, &candidate, &current));
  }

  #[test]
  fn hot_reload_file_deleted_then_recreated_applies() {
    let committed = Some(1u64);
    let candidate = None;
    let current = None;
    assert!(hot_reload_should_apply(&committed, &candidate, &current));
  }

  #[test]
  fn geometry_missing_index_yields_empty_plan() {
    let entries = vec![WindowGeometryEntry { index: 1, x: 10, y: 20, w: 300, h: 200, ..Default::default() }];
    assert_eq!(resolve_geometry_apply_plan(&entries, 2), GeometryApplyPlan::default());
  }

  #[test]
  fn geometry_zero_size_entry_yields_no_rect() {
    let entries = vec![WindowGeometryEntry { index: 1, w: 0, h: 0, ..Default::default() }];
    assert_eq!(resolve_geometry_apply_plan(&entries, 1).rect, None);
  }

  #[test]
  fn geometry_full_entry_resolves_rect() {
    let entries = vec![WindowGeometryEntry { index: 1, x: 10, y: 20, w: 300, h: 200, ..Default::default() }];
    let plan = resolve_geometry_apply_plan(&entries, 1);
    assert_eq!(plan.rect, Some((10, 20, 300, 200)));
    assert_eq!(plan.maximized, false);
  }

  #[test]
  fn geometry_name_only_entry_applies_name_without_rect() {
    let entries =
      vec![WindowGeometryEntry { index: 1, name: Some("お気に入り".to_string()), ..Default::default() }];
    let plan = resolve_geometry_apply_plan(&entries, 1);
    assert_eq!(plan.rect, None);
    assert_eq!(plan.name.as_deref(), Some("お気に入り"));
    assert_eq!(plan.maximized, false);
  }

  #[test]
  fn geometry_maximized_only_entry_applies_maximized_without_rect() {
    let entries = vec![WindowGeometryEntry { index: 1, maximized: Some(true), ..Default::default() }];
    let plan = resolve_geometry_apply_plan(&entries, 1);
    assert_eq!(plan.rect, None);
    assert!(plan.maximized);
  }

  #[test]
  fn geometry_working_directory_applies_independently() {
    let entries =
      vec![WindowGeometryEntry { index: 1, working_directory: Some("D:\\work".to_string()), ..Default::default() }];
    assert_eq!(resolve_geometry_apply_plan(&entries, 1).working_directory.as_deref(), Some("D:\\work"));
  }

  fn alive(x: i32, y: i32, w: i32, h: i32, maximized: bool, name: Option<&str>, cwd: Option<&str>) -> AlivePaneGeometry {
    AlivePaneGeometry { x, y, w, h, maximized, name: name.map(String::from), working_directory: cwd.map(String::from) }
  }

  #[test]
  fn build_geometry_indexes_by_launch_order() {
    let alive_panes = vec![
      alive(0, 0, 400, 300, false, None, None),
      alive(50, 50, 500, 400, false, None, None),
    ];
    let entries = build_geometry_from_alive(&alive_panes);
    assert_eq!(entries.len(), 2);
    assert_eq!(entries[0].index, 1);
    assert_eq!(entries[1].index, 2);
    assert_eq!(entries[1].x, 50);
  }

  #[test]
  fn build_geometry_omits_plain_maximized_pane() {
    // Maximized + no custom name + no saved cwd -> gap (launch-time-maximize
    // fallback is enough, no entry needed).
    let alive_panes = vec![alive(0, 0, 0, 0, true, None, None)];
    assert!(build_geometry_from_alive(&alive_panes).is_empty());
  }

  #[test]
  fn build_geometry_keeps_maximized_pane_with_name() {
    let alive_panes = vec![alive(0, 0, 0, 0, true, Some("main"), None)];
    let entries = build_geometry_from_alive(&alive_panes);
    assert_eq!(entries.len(), 1);
    assert_eq!(entries[0].maximized, Some(true));
    assert_eq!(entries[0].name.as_deref(), Some("main"));
    assert_eq!((entries[0].x, entries[0].y, entries[0].w, entries[0].h), (0, 0, 0, 0));
  }

  #[test]
  fn build_geometry_keeps_maximized_pane_with_cwd() {
    let alive_panes = vec![alive(0, 0, 0, 0, true, None, Some("D:\\work"))];
    let entries = build_geometry_from_alive(&alive_panes);
    assert_eq!(entries.len(), 1);
    assert_eq!(entries[0].working_directory.as_deref(), Some("D:\\work"));
  }

  #[test]
  fn build_geometry_round_trips_through_apply_plan() {
    let alive_panes = vec![alive(10, 20, 300, 200, false, Some("foo"), Some("D:\\work"))];
    let entries = build_geometry_from_alive(&alive_panes);
    let plan = resolve_geometry_apply_plan(&entries, 1);
    assert_eq!(plan.rect, Some((10, 20, 300, 200)));
    assert_eq!(plan.name.as_deref(), Some("foo"));
    assert_eq!(plan.working_directory.as_deref(), Some("D:\\work"));
    assert!(!plan.maximized);
  }
}
