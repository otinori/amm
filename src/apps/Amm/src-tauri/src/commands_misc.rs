// Approval-hub / input-history / git / hook-cli+mcp-cli-registration /
// launch-profile-pane / untrusted-autostart-confirm commands - split
// out of lib.rs (2026-07-26 architecture-bloat cleanup). No behavior
// change, pure move.
use std::sync::Mutex;
use tauri::{AppHandle, State};
use crate::{approval, commands_profile::ProfilesState, gateway, git_helper, hook_cli, input_history, mcp, mcp_cli, profile};

// spec: profile-schema's コマンド追加ダイアログ - フロントエンド(JS)は
// Rustのような#[cfg(...)]分岐を持たないため、Cmd/PowerShell等Windows専用の
// プリセットをmacOSで出さないようにする判定材料としてOS種別を渡す
// (ユーザー報告: 「コマンド追加にCmdとPowershellがある。Mac用に変更して」)。
#[tauri::command]
pub(crate) fn get_platform() -> &'static str {
  #[cfg(windows)]
  {
    "windows"
  }
  #[cfg(target_os = "macos")]
  {
    "macos"
  }
  #[cfg(all(unix, not(target_os = "macos")))]
  {
    "linux"
  }
}

#[tauri::command]
pub(crate) async fn gateway_server_infos(gateway: State<'_, gateway::GatewayManager>) -> Result<Vec<gateway::GatewayServerInfo>, String> {
  Ok(gateway.server_infos().await)
}

#[tauri::command]
pub(crate) async fn list_approvals(broker: State<'_, approval::ApprovalBroker>) -> Result<Vec<approval::ApprovalEntry>, String> {
  Ok(broker.list().await)
}

#[tauri::command]
pub(crate) async fn resolve_approval(id: String, decision: Option<String>, broker: State<'_, approval::ApprovalBroker>) -> Result<bool, String> {
  Ok(broker.resolve(&id, decision).await)
}

// spec: approval-hub's 4 release triggers include pane activation (found
// missing in the phase 8.1 parity audit - close already released via
// mcp.rs's close_pane, activation never did). Releases with no decision,
// same as close: the hook falls back to its normal in-pane prompt.
#[tauri::command]
pub(crate) async fn release_approval_on_activate(pane_id: String, broker: State<'_, approval::ApprovalBroker>) -> Result<(), String> {
  broker.release_by_token(&pane_id).await;
  Ok(())
}

#[tauri::command]
pub(crate) fn add_to_history(text: String, history: State<input_history::InputHistory>) {
  history.add(&text);
  history.save();
}

#[tauri::command]
pub(crate) fn get_recent_history(n: usize, history: State<input_history::InputHistory>) -> Vec<String> {
  history.recent(n)
}

// spec: pane-management - ペインタイトルバーの「名前変更」がNickName(MCP
// 送信先名)そのものを変更するようになった(ユーザー要望、2026-08-03)。
// ad-hocペイン(nicknameを持たない、command直接指定で起動)にはMCP
// participantエントリ自体が無いためfalseを返す - 呼び出し元(JS)はこの場合
// 従来通りpane.labelのみをローカルで変更する。
#[tauri::command]
pub(crate) async fn rename_pane_nickname(pane_id: String, nickname: String, mcp: State<'_, mcp::McpState>) -> Result<bool, String> {
  Ok(mcp.rename_participant(&pane_id, &nickname).await)
}

// spec: pane-management's new "コマンドメニューによるプロファイルのペイン起動"
// requirement - GUI-callable counterpart to MCP's pane/open with
// profile_name, reusing mcp::open_pane's own logic (mcp.rs::open_pane_for_gui).
#[tauri::command]
pub(crate) async fn launch_profile_pane(
  app: AppHandle,
  profile_name: String,
  working_directory: Option<String>,
  mcp: State<'_, mcp::McpState>,
) -> Result<String, String> {
  mcp::open_pane_for_gui(&app, &mcp, &profile_name, working_directory).await
}

// spec: 旧.NET版のSelectWorkingDirOnStart用FolderBrowserDialog相当
// ("「{profile.Name}」の作業ディレクトリを選択"というタイトル、profileの
// 現在の作業ディレクトリを初期表示、存在しないパスはOS既定にフォールバック)。
// macOS: tauri-plugin-dialog's blocking_* helpers run_on_main_thread() the
// actual panel and block the calling thread on a channel recv() for the
// result. A plain (non-async) #[tauri::command] runs synchronously on
// whatever thread delivered the IPC message - on macOS that's WKWebView's
// main-thread script-message callback - so calling blocking_pick_folder()
// there deadlocks the whole app (main thread blocked waiting for a task it
// itself needs to pump). Marking the command async makes Tauri dispatch it
// onto the async runtime instead, off the main thread, matching the
// plugin's own documented usage (found via user report: folder/file picker
// froze the app on macOS).
#[tauri::command]
pub(crate) async fn pick_working_dir_for_launch(app: tauri::AppHandle, profile_name: String, initial_dir: Option<String>) -> Option<String> {
  use tauri_plugin_dialog::DialogExt;
  let mut builder = app.dialog().file().set_title(format!("「{profile_name}」の作業ディレクトリを選択"));
  if let Some(dir) = initial_dir.filter(|d| !d.is_empty() && std::path::Path::new(d).is_dir()) {
    builder = builder.set_directory(dir);
  }
  builder.blocking_pick_folder().and_then(|fp| fp.into_path().ok()).map(|p| p.to_string_lossy().to_string())
}

// spec: pane-management's new "外部 .amm ファイルの自動起動確認" requirement -
// the frontend calls this once the user has answered the
// amm-untrusted-autostart-confirm prompt this session's .setup() may have
// emitted instead of auto-starting mcpServers immediately.
#[tauri::command]
pub(crate) fn confirm_untrusted_autostart(approve: bool, path: String, gateway: State<'_, gateway::GatewayManager>) {
  if approve {
    gateway.start_auto_start_servers();
    profile::mark_path_trusted(std::path::Path::new(&path));
  }
}

// Holds the untrusted-autostart-confirm payload (if any) computed once
// during .setup(), so the frontend can *pull* it once the page has
// actually loaded instead of relying solely on the amm-untrusted-
// autostart-confirm event .setup() also emits. Found via a real-machine
// check (2026-07-21, tasks.md 8.6.1 verification): .setup() runs and
// calls app.emit() well before the webview has loaded index.html and run
// app.js's listen() call, so the event was silently dropped every time -
// the confirmation dialog never appeared, the untrusted profile's
// mcpServers just stayed stopped forever with no explanation and no way
// to approve them (the security property "don't auto-run" held, but the
// UX to explicitly opt in was completely unreachable). check_pending_
// untrusted_autostart is the fix: called once by the frontend on load,
// after its own listen() calls are already registered, so it can never
// lose the race the way an emit() fired from .setup() can.
#[derive(Default)]
pub(crate) struct PendingUntrustedAutostart(pub(crate) Mutex<Option<serde_json::Value>>);

#[tauri::command]
pub(crate) fn check_pending_untrusted_autostart(state: State<'_, PendingUntrustedAutostart>) -> Option<serde_json::Value> {
  state.0.lock().unwrap_or_else(|e| e.into_inner()).take()
}

// spec: pane-management's "終了時の確認ガード" - "未保存プロファイル変更の確認"
// (found entirely unimplemented in the source-diff parity audit - the exit
// handler only ever guarded running sessions/git). Compares the live
// in-memory profiles against what's actually on disk right now, rather than
// tracking a separate load-time snapshot, so it stays correct across
// hot-reloads too.
#[tauri::command]
pub(crate) fn has_unsaved_profile_changes(state: State<ProfilesState>) -> bool {
  let current = state.file.lock().unwrap_or_else(|e| e.into_inner()).clone();
  let on_disk = profile::load_profiles(&state.path.lock().unwrap_or_else(|e| e.into_inner()))
    .unwrap_or_else(|_| profile::ProfilesFile { profiles: vec![profile::SessionProfile::default_cmd()], mcp_servers: vec![] });
  serde_json::to_string(&current).ok() != serde_json::to_string(&on_disk).ok()
}

// spec: pane-management's "終了時の確認ガード" - the unsaved-profile-changes
// confirmation needs to tell the user *which* file "上書き保存" would target
// (found confusing via real-machine manual testing 2026-07-27: the confirm
// dialog gave no indication of a save destination at all).
#[tauri::command]
pub(crate) fn get_active_profiles_path(state: State<ProfilesState>) -> String {
  state.path.lock().unwrap_or_else(|e| e.into_inner()).display().to_string()
}

#[tauri::command]
pub(crate) fn git_repo_root(dir: String) -> Option<String> {
  git_helper::get_repo_root(std::path::Path::new(&dir))
}

#[tauri::command]
pub(crate) fn git_status_short(dir: String) -> String {
  git_helper::status_short(std::path::Path::new(&dir))
}

#[tauri::command]
pub(crate) fn git_commit(dir: String, message: String) -> (i32, String, String) {
  git_helper::add_all_and_commit(std::path::Path::new(&dir), &message)
}

#[tauri::command]
pub(crate) fn git_push(dir: String) -> (i32, String, String) {
  git_helper::push(std::path::Path::new(&dir))
}

// macOS/Unix delta (add-macos-support, found via real-machine hook-cli
// verification): USERPROFILE doesn't exist there, so this silently
// resolved to an empty PathBuf - hook_cli/mcp_cli registration would then
// look for ~/.claude/etc. config files at a bogus root-relative path.
fn resolve_home_dir() -> std::path::PathBuf {
  #[cfg(windows)]
  {
    std::env::var("USERPROFILE").map(std::path::PathBuf::from).unwrap_or_default()
  }
  #[cfg(not(windows))]
  {
    std::env::var("HOME").map(std::path::PathBuf::from).unwrap_or_default()
  }
}

// The path resolved here is written *literally* into the target CLI's
// config file (hook_cli::register/mcp_cli::register both take this as the
// command string) - so it must be the actual, executable path on disk,
// not just a display string. On macOS the bundled binary lives at
// Contents/Resources/amm-mcp (no .exe suffix), a sibling of
// Contents/MacOS/ - the same split resolve_profiles_path's macOS branch
// already handles for profiles.amm - not exe-adjacent the way Windows'
// flat install layout has it. Without this, hook/MCP registration on
// macOS would write a nonexistent `.../Contents/MacOS/amm-mcp.exe` (wrong
// directory *and* wrong filename) into e.g. ~/.claude/settings.json.
fn resolve_mcp_exe_path() -> std::path::PathBuf {
  let exe_dir = std::env::current_exe().ok().and_then(|p| p.parent().map(|p| p.to_path_buf())).unwrap_or_default();
  #[cfg(windows)]
  {
    exe_dir.join("amm-mcp.exe")
  }
  #[cfg(target_os = "macos")]
  {
    if let Some(bundled) = exe_dir.parent().map(|contents| contents.join("Resources").join("amm-mcp")) {
      if bundled.is_file() {
        return bundled;
      }
    }
    exe_dir.join("amm-mcp")
  }
  #[cfg(all(unix, not(target_os = "macos")))]
  {
    exe_dir.join("amm-mcp")
  }
}

// spec: mcp-server's "CLI設定ファイルへのMCPサーバ登録" UI - the .NET
// original's McpRegistrationDialog shows the resolved amm-mcp.exe path (so
// the user can tell whether an existing registration is stale, e.g. after
// moving from a dev build to an installed location) and whether it exists
// on disk. Found missing an equivalent GUI entirely in the source-diff
// parity audit (tasks.md 8.6.17) - hook_register/mcp_register were only
// ever reachable via a direct invoke() call, never from a button.
#[tauri::command]
pub(crate) fn get_mcp_exe_path() -> serde_json::Value {
  let path = resolve_mcp_exe_path();
  serde_json::json!({ "path": path.display().to_string(), "exists": path.exists() })
}

#[tauri::command]
pub(crate) fn hook_registered_command(kind: hook_cli::CliKind) -> Option<String> {
  hook_cli::get_registered_command(kind, &resolve_home_dir())
}

#[tauri::command]
pub(crate) fn hook_register(kind: hook_cli::CliKind) -> Result<(), String> {
  let exe = resolve_mcp_exe_path();
  hook_cli::register(kind, &resolve_home_dir(), &exe.to_string_lossy())
}

#[tauri::command]
pub(crate) fn hook_unregister(kind: hook_cli::CliKind) -> Result<(), String> {
  hook_cli::unregister(kind, &resolve_home_dir())
}

// spec: mcp-server's "CLI設定ファイルへのMCPサーバ登録" (McpCliRegistrar,
// distinct from hook_cli's HookCliRegistrar) - registers amm-mcp.exe
// itself as an MCP server in each AI CLI's config, found missing
// entirely in the phase 8.1 parity audit.
#[tauri::command]
pub(crate) fn mcp_registered_command(kind: mcp_cli::McpCliKind) -> Option<String> {
  mcp_cli::get_registered_command(kind, &resolve_home_dir())
}

#[tauri::command]
pub(crate) fn mcp_register(kind: mcp_cli::McpCliKind) -> Result<(), String> {
  let exe = resolve_mcp_exe_path();
  mcp_cli::register(kind, &resolve_home_dir(), &exe.to_string_lossy())
}

#[tauri::command]
pub(crate) fn mcp_unregister(kind: mcp_cli::McpCliKind) -> Result<(), String> {
  mcp_cli::unregister(kind, &resolve_home_dir())
}
