// Profile (profiles.amm) CRUD/import-export and MCP-server-config
// CRUD/import-export commands - split out of lib.rs (2026-07-26
// architecture-bloat cleanup). No behavior change, pure move.
use std::sync::Mutex;
use tauri::{Manager, State};
use crate::{gateway, profile};

pub struct ProfilesState {
  pub file: Mutex<profile::ProfilesFile>,
  // Mutex, not a plain PathBuf: "開く"/"名前を付けて保存" (open_profiles_file/
  // save_profiles_as) can retarget which .amm file is active mid-session -
  // see spawn_profiles_hot_reload's path-swap detection for the other half
  // of this.
  pub path: Mutex<std::path::PathBuf>,
}

impl ProfilesState {
  pub(crate) fn find_by_name(&self, name: &str) -> Option<profile::SessionProfile> {
    self.file.lock().unwrap_or_else(|e| e.into_inner()).profiles.iter().find(|p| p.name.eq_ignore_ascii_case(name)).cloned()
  }
}

// spec: profile-schema's "プロファイルファイルのホットリロード" - found missing
// entirely in the phase 8.1 parity audit (previously undocumented in any
// phase). Polls mtime instead of a FileSystemWatcher equivalent (no `notify`
// crate dependency, matching this port's preference for hand-rolled logic
// over new crates elsewhere - see profile::expand_env_vars /
// resolve_executable_path). A 300ms poll interval that only reloads once
// the mtime has been stable across two consecutive polls gives the same
// practical debounce the spec asks for (save-temp-rename editors fire
// several fs events within a single interval).
pub(crate) fn spawn_profiles_hot_reload(app: tauri::AppHandle, path: std::path::PathBuf) {
  std::thread::spawn(move || {
    let read_mtime = |p: &std::path::Path| std::fs::metadata(p).and_then(|m| m.modified()).ok();
    let mut watched = path;
    let mut committed = read_mtime(&watched);
    let mut candidate = committed;
    loop {
      std::thread::sleep(std::time::Duration::from_millis(300));
      // "開く"/"名前を付けて保存" may have retargeted the active file since the
      // last poll - reset the mtime baseline instead of comparing against a
      // now-unrelated file's history (which could otherwise misfire a reload
      // from the *old* path's stale state right after a switch).
      let current_path = app.state::<ProfilesState>().path.lock().unwrap_or_else(|e| e.into_inner()).clone();
      if current_path != watched {
        watched = current_path;
        committed = read_mtime(&watched);
        candidate = committed;
        continue;
      }
      let current = read_mtime(&watched);
      if profile::hot_reload_should_apply(&committed, &candidate, &current) {
        committed = current;
        match profile::load_profiles(&watched) {
          Ok(file) => {
            let state = app.state::<ProfilesState>();
            *state.file.lock().unwrap_or_else(|e| e.into_inner()) = file;
            log::info!("[profile] hot-reloaded {}", watched.display());
          }
          // spec: "再読込中にJSONが壊れていた場合は例外を握りつぶし現在の設定を維持する"
          Err(profile::LoadError::InvalidJson(msg)) => {
            log::warn!("[profile] hot-reload skipped, invalid JSON: {msg}");
          }
        }
      }
      candidate = current;
    }
  });
}

#[tauri::command]
pub(crate) fn list_profiles(state: State<ProfilesState>) -> Vec<profile::SessionProfile> {
  state.file.lock().unwrap_or_else(|e| e.into_inner()).profiles.clone()
}

// spec: pane-management's「コマンドタイプ」タブ - profiles.amm(File>開くで
// 切り替わる「コマンド」)とは独立したアプリ全体設定として、コマンド追加時の
// 型プリセットを取得・保存する(ユーザーとの相談で決定した保存先、
// profile.rs::load_command_type_presets/save_command_type_presets参照)。
#[tauri::command]
pub(crate) fn get_command_type_presets() -> Vec<profile::CommandTypePreset> {
  profile::load_command_type_presets()
}

#[tauri::command]
pub(crate) fn set_command_type_presets(presets: Vec<profile::CommandTypePreset>) -> Result<(), String> {
  profile::save_command_type_presets(&presets).map_err(|e| e.to_string())
}

#[tauri::command]
pub(crate) fn reset_command_type_presets() -> Vec<profile::CommandTypePreset> {
  profile::default_command_type_presets()
}

#[tauri::command]
pub(crate) fn quick_prompt_label_suggestion(text: String) -> String {
  profile::quick_prompt_label_suggestion(&text)
}

// spec: quick-command-register's "テキスト欄の初期値" (full ANSI-stripped text,
// as opposed to the label's truncated-first-line variant above).
#[tauri::command]
pub(crate) fn strip_ansi_text(text: String) -> String {
  profile::strip_ansi(&text)
}

// spec: quick-command-register - OK with non-empty prompt appends
// {label, prompt} to the app-wide quick-prompts list (profile::QuickPrompt,
// persisted to quick-prompts.json - changed from per-profile to app-wide,
// ユーザー要望 2026-08-04: 「クイック送信は、アプリ共通の設定で良い。ペイン
// 毎の設定は不要とする」。以前はプロファイル未紐付けのad-hocペインだと
// 登録先が無く常に無効化されていたが、この変更で解消される)。Empty prompt
// is a no-op (dialog-level validation, enforced here too since the UI
// dialog itself is deferred).
#[tauri::command]
pub(crate) fn register_quick_prompt(label: String, prompt: String) -> Result<(), String> {
  if prompt.is_empty() {
    return Ok(());
  }
  let mut prompts = profile::load_quick_prompts();
  prompts.push(profile::QuickPrompt { label, prompt });
  profile::save_quick_prompts(&prompts);
  Ok(())
}

// spec: profile-schema's windowGeometry "現在の配置を記憶" (OnCaptureCurrentLayout
// in MdiParentForm.cs) - overwrites the profile's window_geometry with a
// fresh snapshot built from its currently-alive panes. Memory-only, same
// unification as register_quick_prompt above - previously saved .amm
// immediately with no confirmation at all, now deferred to 上書き保存 like
// every other profile mutation.
#[tauri::command]
pub(crate) fn capture_window_geometry(profile_name: String, panes: Vec<profile::AlivePaneGeometry>, state: State<ProfilesState>) -> Result<(), String> {
  let mut file = state.file.lock().unwrap_or_else(|e| e.into_inner());
  let Some(p) = file.profiles.iter_mut().find(|p| p.name.eq_ignore_ascii_case(&profile_name)) else {
    return Err(format!("profile not found: {profile_name}"));
  };
  p.window_geometry = profile::build_geometry_from_alive(&panes);
  p.auto_start_count = panes.len() as u32;
  Ok(())
}

// spec: command-import-export - export writes an .ammprofiles file
// immediately (unlike quickPrompts/profiles.amm, this is a distinct
// user-chosen export path, not the app's own profiles store). Takes the
// already-selected profile objects directly rather than reading
// ProfilesState by name, since this is called from 「コマンドを管理...」's own
// not-yet-committed working copy - reading live state here would silently
// export stale (pre-edit) data whenever the dialog has unsaved edits.
#[tauri::command]
pub(crate) fn export_profiles_list_to_path(path: String, profiles: Vec<profile::SessionProfile>) -> Result<usize, String> {
  if profiles.is_empty() {
    return Err("コマンドが選択されていません。".to_string());
  }
  let count = profiles.len();
  let export = profile::ProfilesExportFile { version: 1, profiles };
  let json = serde_json::to_string_pretty(&export).map_err(|e| e.to_string())?;
  let target = std::path::Path::new(&path);
  let tmp = target.with_extension("ammprofiles.tmp");
  std::fs::write(&tmp, json).map_err(|e| e.to_string())?;
  std::fs::rename(&tmp, target).map_err(|e| e.to_string())?;
  Ok(count)
}

// spec: command-import-export's "重複検出と選択・競合解決ダイアログ" - the
// ImportProfilesDialog checklist needs the full parsed list *before*
// merging (to show duplicate marks and let the user check/uncheck rows),
// so parsing and merging are two separate commands rather than one
// do-everything call. Neither writes profiles.amm - only save_profiles_now
// (the "上書き保存" menu action) does, matching "反映はメモリ上のみ".
#[tauri::command]
pub(crate) fn preview_import_profiles(path: String) -> Result<Vec<profile::SessionProfile>, String> {
  let text = std::fs::read_to_string(&path).map_err(|e| e.to_string())?;
  let imported = profile::parse_import_profiles(&text)?;
  if imported.is_empty() {
    return Err("インポートするコマンドが見つかりませんでした。".to_string());
  }
  Ok(imported)
}

// Same "operate on the caller's own list, not live ProfilesState" reasoning
// as export_profiles_list_to_path above - merges into and returns the
// working copy 「コマンドを管理...」passed in, rather than mutating global
// state, so Cancel on that dialog correctly discards the import too.
#[tauri::command]
pub(crate) fn merge_profiles_into_list(
  mut existing: Vec<profile::SessionProfile>,
  imported: Vec<profile::SessionProfile>,
  policy: String,
) -> (Vec<profile::SessionProfile>, profile::ImportSummary) {
  let policy = match policy.as_str() {
    "rename" => profile::ConflictPolicy::Rename,
    "overwrite" => profile::ConflictPolicy::Overwrite,
    _ => profile::ConflictPolicy::Skip,
  };
  let summary = profile::merge_imported_profiles(&mut existing, imported, policy);
  (existing, summary)
}

// spec: command-import-export's SaveFileDialog/OpenFileDialog (filter
// *.ammprofiles/*.amm/*.*). Wrapped in custom commands (rather than calling
// the dialog plugin directly from JS) to keep this port's established
// pattern of everything going through invoke()'d app commands.
//
// All pick_*/ask_drop_action commands in this file are `async fn`: their
// tauri_plugin_dialog blocking_* calls run_on_main_thread() the panel and
// block the calling thread waiting for it. A plain (non-async) command runs
// synchronously on the thread that delivered the IPC message, which on
// macOS is WKWebView's main-thread callback - deadlocking the app (main
// thread blocked on a task only the main thread can run). `async fn` makes
// Tauri dispatch the command off that thread instead (found via user
// report: every native file/folder dialog froze the app on macOS).
#[tauri::command]
pub(crate) async fn pick_export_save_path(app: tauri::AppHandle, default_file_name: String) -> Option<String> {
  use tauri_plugin_dialog::DialogExt;
  app
    .dialog()
    .file()
    .add_filter("AMM Profiles", &["ammprofiles", "amm"])
    .add_filter("All Files", &["*"])
    .set_file_name(&default_file_name)
    .blocking_save_file()
    .and_then(|fp| fp.into_path().ok())
    .map(|p| p.to_string_lossy().to_string())
}

#[tauri::command]
pub(crate) async fn pick_import_open_path(app: tauri::AppHandle) -> Option<String> {
  use tauri_plugin_dialog::DialogExt;
  app
    .dialog()
    .file()
    .add_filter("AMM Profiles", &["ammprofiles", "amm"])
    .add_filter("All Files", &["*"])
    .blocking_pick_file()
    .and_then(|fp| fp.into_path().ok())
    .map(|p| p.to_string_lossy().to_string())
}

// spec: profile-schema's CommandTemplateDialog working-directory browse
// button (FolderBrowserDialog equivalent).
#[tauri::command]
pub(crate) async fn pick_folder(app: tauri::AppHandle) -> Option<String> {
  use tauri_plugin_dialog::DialogExt;
  app.dialog().file().blocking_pick_folder().and_then(|fp| fp.into_path().ok()).map(|p| p.to_string_lossy().to_string())
}

// spec: pane-management's ファイルドラッグ&ドロップ - port of
// TerminalChildForm.cs's AskDropAction (Win32 TaskDialog, 3 custom
// buttons: content/path/cancel). Native MessageDialogButtons::
// YesNoCancelCustom instead of two chained confirm()s, both matching the
// .NET original's UX (found via user feedback, phase 8.2: a plain OK/
// Cancel confirm() can't express "don't paste anything at all") and
// sidestepping the same "confirm() returns a Promise instead of blocking"
// issue this whole dialog had to move off of in the first place.
#[tauri::command]
pub(crate) async fn ask_drop_action(app: tauri::AppHandle, file_count: usize) -> String {
  use tauri_plugin_dialog::{DialogExt, MessageDialogButtons, MessageDialogResult};
  const CONTENT: &str = "ファイル内容を送信";
  const PATH: &str = "絶対パスを送信";
  let message = if file_count == 1 {
    "ドロップされたファイルをどう送信しますか?".to_string()
  } else {
    format!("ドロップされた {file_count} 件のファイルをどう送信しますか?\n・内容: 全ファイルを連結してそのまま送信\n・パス: 絶対パスをスペース区切りで送信")
  };
  let result = app
    .dialog()
    .message(message)
    .title("ファイルのドロップ")
    .buttons(MessageDialogButtons::YesNoCancelCustom(CONTENT.to_string(), PATH.to_string(), "キャンセル".to_string()))
    .blocking_show_with_result();
  match result {
    MessageDialogResult::Custom(s) if s == CONTENT => "content".to_string(),
    MessageDialogResult::Custom(s) if s == PATH => "path".to_string(),
    _ => "cancel".to_string(),
  }
}

// spec: profile-schema's CommandManagerDialog - "OK で親側の_profilesへコミット"
// (found missing entirely: no UI existed to add/edit/remove/reorder
// profiles at all). Whole-list replace rather than the .NET original's
// per-entry reference-preservation (Original != null -> flow fields into
// the existing object so live MDI children keep their profile reference) -
// this port's panes capture profile-derived values at open-time rather
// than holding a live reference, so there's nothing to preserve identity
// for. Memory-only, matching "反映はメモリ上のみ" (save_profiles_now
// persists).
#[tauri::command]
pub(crate) fn commit_profiles(profiles: Vec<profile::SessionProfile>, state: State<ProfilesState>) {
  state.file.lock().unwrap_or_else(|e| e.into_inner()).profiles = profiles;
}

// spec: profile-schema's promptNewNameOnCommandAdd - port of OpenTerminal's
// "clone the launching profile under a new name, add it, launch from the
// clone" flow. Memory-only, matching commit_profiles/register_quick_prompt's
// own "反映はメモリ上のみ" convention - persisted only once the user runs
// "上書き保存".
#[tauri::command]
pub(crate) fn add_profile(profile: profile::SessionProfile, state: State<ProfilesState>) {
  state.file.lock().unwrap_or_else(|e| e.into_inner()).profiles.push(profile);
}

// spec: profile-schema's AmmSettingsDialog - "確定後は AMM ファイルへ書き戻す
// かどうかを確認する" (persist is the frontend's confirm() answer).
#[tauri::command]
pub(crate) fn update_profile_settings(
  profile_name: String,
  close_prohibited: bool,
  persist: bool,
  state: State<ProfilesState>,
) -> Result<(), String> {
  let mut file = state.file.lock().unwrap_or_else(|e| e.into_inner());
  let Some(p) = file.profiles.iter_mut().find(|p| p.name.eq_ignore_ascii_case(&profile_name)) else {
    return Err(format!("profile not found: {profile_name}"));
  };
  p.close_prohibited = close_prohibited;
  if persist {
    profile::save_profiles(&state.path.lock().unwrap_or_else(|e| e.into_inner()), &file).map_err(|e| e.to_string())?;
  }
  Ok(())
}

#[tauri::command]
pub(crate) fn save_profiles_now(state: State<ProfilesState>) -> Result<(), String> {
  let file = state.file.lock().unwrap_or_else(|e| e.into_inner());
  profile::save_profiles(&state.path.lock().unwrap_or_else(|e| e.into_inner()), &file).map_err(|e| e.to_string())
}

// spec: 旧.NET版のファイルメニュー「開く」相当 - アクティブな.amm自体を切り替え、
// 以後の上書き保存・ホットリロード監視も新パスへ向く(spawn_profiles_hot_reload
// 側のpath-swap検知と対で機能する)。既存の生存ペインには一切触れない(旧版の
// 「既存 MDI 子は影響させない」と同じ - 自動起動するかはJS側がShiftキーの状態
// を見て判断し、するならtopUpAutoStartPanesを別途呼ぶ)。
#[tauri::command]
pub(crate) fn open_profiles_file(path: String, state: State<ProfilesState>) -> Result<Vec<profile::SessionProfile>, String> {
  let path_buf = std::path::PathBuf::from(&path);
  let loaded = match profile::load_profiles(&path_buf) {
    Ok(f) => f,
    Err(profile::LoadError::InvalidJson(msg)) => return Err(format!("JSON の解析に失敗しました: {msg}")),
  };
  let profiles = loaded.profiles.clone();
  *state.file.lock().unwrap_or_else(|e| e.into_inner()) = loaded;
  *state.path.lock().unwrap_or_else(|e| e.into_inner()) = path_buf;
  Ok(profiles)
}

// spec: 旧.NET版のファイルメニュー「名前を付けて保存」相当 - 現在のメモリ上の
// プロファイル一覧を新しいパスへ書き込み、以後の上書き保存もそちらへ向くように
// アクティブパスを切り替える。
#[tauri::command]
pub(crate) fn save_profiles_as(path: String, state: State<ProfilesState>) -> Result<(), String> {
  let path_buf = std::path::PathBuf::from(&path);
  let file = state.file.lock().unwrap_or_else(|e| e.into_inner());
  profile::save_profiles(&path_buf, &file).map_err(|e| e.to_string())?;
  drop(file);
  *state.path.lock().unwrap_or_else(|e| e.into_inner()) = path_buf;
  Ok(())
}

// spec: command-import-exportのSaveFileDialog/OpenFileDialogと同じラップ方針
// (pick_export_save_path/pick_import_open_pathを参照)、フィルタのみ.amm用。
#[tauri::command]
pub(crate) async fn pick_open_profiles_path(app: tauri::AppHandle) -> Option<String> {
  use tauri_plugin_dialog::DialogExt;
  app
    .dialog()
    .file()
    .add_filter("AMM files", &["amm", "json"])
    .add_filter("All Files", &["*"])
    .blocking_pick_file()
    .and_then(|fp| fp.into_path().ok())
    .map(|p| p.to_string_lossy().to_string())
}

#[tauri::command]
pub(crate) async fn pick_save_as_profiles_path(app: tauri::AppHandle, default_file_name: String) -> Option<String> {
  use tauri_plugin_dialog::DialogExt;
  app
    .dialog()
    .file()
    .add_filter("AMM files", &["amm"])
    .add_filter("All Files", &["*"])
    .set_file_name(&default_file_name)
    .blocking_save_file()
    .and_then(|fp| fp.into_path().ok())
    .map(|p| p.to_string_lossy().to_string())
}

// spec: mcp-gateway's McpGatewayDialog - グローバル/ファイル固有 の2グループを
// それぞれ独立に読み書きする。ステータス表示は app-boot 時点の
// GatewayManager のライブスナップショット(server_infos)を参照するのみで、
// 保存操作自体はまだ実行中の GatewayManager を再起動しない(次回起動時に
// 反映される) - McpGatewayDialog OK 押下時の "旧GatewayManagerを停止し、
// 新しい結合設定で再起動" は本パスでは未実装、意図的な簡略化として記録
// (PARITY-AUDIT.md参照)。
#[tauri::command]
pub(crate) fn list_global_mcp_servers() -> Vec<profile::McpServerConfig> {
  gateway::load_global_servers()
}

#[tauri::command]
pub(crate) fn list_file_mcp_servers(state: State<ProfilesState>) -> Vec<profile::McpServerConfig> {
  state.file.lock().unwrap_or_else(|e| e.into_inner()).mcp_servers.clone()
}

#[tauri::command]
pub(crate) fn save_global_mcp_servers(servers: Vec<profile::McpServerConfig>) -> Result<(), String> {
  gateway::save_global_servers(&servers).map_err(|e| e.to_string())
}

// spec: "反映はメモリ上のみ" (command-import-export と同じ規約をファイル固有
// グループにも適用) - profiles.amm への書き込みは save_profiles_now のみ。
#[tauri::command]
pub(crate) fn save_file_mcp_servers(servers: Vec<profile::McpServerConfig>, state: State<ProfilesState>) {
  state.file.lock().unwrap_or_else(|e| e.into_inner()).mcp_servers = servers;
}

// spec: mcp-gatewayの「AMM共通」グループのインポート/エクスポート -
// command-import-exportの同名コマンド群(export_profiles_list_to_path等)と
// 同じ設計: 呼び出し元(openMcpGatewayDialogの作業コピー)が選択済みの一覧を
// 直接渡す/受け取るだけで、ライブのグローバル設定には一切触れない(OK押下
// までは"AMM共通"グループも他のグループと同じくメモリ上のみ)。
#[tauri::command]
pub(crate) fn export_mcp_servers_to_path(path: String, servers: Vec<profile::McpServerConfig>) -> Result<usize, String> {
  if servers.is_empty() {
    return Err("エクスポートするサーバーがありません。".to_string());
  }
  let count = servers.len();
  let export = profile::McpServersExportFile { version: 1, servers };
  let json = serde_json::to_string_pretty(&export).map_err(|e| e.to_string())?;
  let target = std::path::Path::new(&path);
  let tmp = target.with_extension("json.tmp");
  std::fs::write(&tmp, json).map_err(|e| e.to_string())?;
  std::fs::rename(&tmp, target).map_err(|e| e.to_string())?;
  Ok(count)
}

#[tauri::command]
pub(crate) fn preview_import_mcp_servers(path: String) -> Result<Vec<profile::McpServerConfig>, String> {
  let text = std::fs::read_to_string(&path).map_err(|e| e.to_string())?;
  let imported = profile::parse_import_mcp_servers(&text)?;
  if imported.is_empty() {
    return Err("インポートするサーバーが見つかりませんでした。".to_string());
  }
  Ok(imported)
}

#[tauri::command]
pub(crate) fn merge_mcp_servers_list(
  mut existing: Vec<profile::McpServerConfig>,
  imported: Vec<profile::McpServerConfig>,
  policy: String,
) -> (Vec<profile::McpServerConfig>, profile::ImportSummary) {
  let policy = match policy.as_str() {
    "rename" => profile::ConflictPolicy::Rename,
    "overwrite" => profile::ConflictPolicy::Overwrite,
    _ => profile::ConflictPolicy::Skip,
  };
  let summary = profile::merge_imported_mcp_servers(&mut existing, imported, policy);
  (existing, summary)
}

#[tauri::command]
pub(crate) async fn pick_export_mcp_servers_path(app: tauri::AppHandle, default_file_name: String) -> Option<String> {
  use tauri_plugin_dialog::DialogExt;
  app
    .dialog()
    .file()
    .add_filter("JSON", &["json"])
    .add_filter("All Files", &["*"])
    .set_file_name(&default_file_name)
    .blocking_save_file()
    .and_then(|fp| fp.into_path().ok())
    .map(|p| p.to_string_lossy().to_string())
}

#[tauri::command]
pub(crate) async fn pick_import_mcp_servers_path(app: tauri::AppHandle) -> Option<String> {
  use tauri_plugin_dialog::DialogExt;
  app
    .dialog()
    .file()
    .add_filter("JSON", &["json"])
    .add_filter("All Files", &["*"])
    .blocking_pick_file()
    .and_then(|fp| fp.into_path().ok())
    .map(|p| p.to_string_lossy().to_string())
}
