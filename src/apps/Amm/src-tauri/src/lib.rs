mod ansi;
mod approval;
mod commands_misc;
mod commands_profile;
mod editor_bridge;
mod gateway;
mod git_helper;
mod hook_cli;
mod input_history;
mod mcp;
mod mcp_cli;
mod native_ui;
#[cfg(target_os = "macos")]
mod native_ui_macos;
mod profile;
mod pty;
mod wait_detect;

pub use commands_profile::ProfilesState;
pub(crate) use pty::spawn_pty_for_pane_with_patterns;
pub use pty::PtyState;

use std::sync::Mutex;
use tauri::Manager;

// Per-user app-data base directory, shared by every module that persists
// a `amm/<file>.json` (editor_bridge.rs's editor-settings.json,
// input_history.rs's history.json, profile.rs's trusted-profiles.json,
// gateway.rs's mcp-servers.json). Found via a real macOS install
// (add-macos-support): all four had independently copy-pasted
// `std::env::var("LOCALAPPDATA")` with no #[cfg(windows)] gate and no
// macOS/Unix branch at all - LOCALAPPDATA doesn't exist there, so
// `.unwrap_or_default()` silently produced an *empty* PathBuf on macOS,
// meaning editor settings, command history, the .amm auto-launch trust
// list, and the MCP gateway's server list were all reading/writing to a
// nonsensical CWD-relative location instead of persisting at all. Callers
// still do `.join("amm")` themselves, matching the existing call sites.
pub(crate) fn app_data_base_dir() -> std::path::PathBuf {
  #[cfg(windows)]
  {
    std::env::var("LOCALAPPDATA").map(std::path::PathBuf::from).unwrap_or_default()
  }
  #[cfg(target_os = "macos")]
  {
    // Apple's conventional per-user application-support location.
    std::env::var("HOME")
      .map(|home| std::path::PathBuf::from(home).join("Library").join("Application Support"))
      .unwrap_or_default()
  }
  #[cfg(all(unix, not(target_os = "macos")))]
  {
    // XDG Base Directory spec (freedesktop.org), same convention most
    // Linux desktop apps follow.
    std::env::var("XDG_DATA_HOME").map(std::path::PathBuf::from).unwrap_or_else(|_| {
      std::env::var("HOME").map(|home| std::path::PathBuf::from(home).join(".local").join("share")).unwrap_or_default()
    })
  }
}

// User's Documents folder - the fallback profile::resolve_profiles_path()
// uses when the process's current directory isn't a sane default (see
// is_system_protected_dir below). Env-var based rather than the Win32
// Known Folder API (SHGetKnownFolderPath), matching app_data_base_dir()'s
// existing simple-env-var convention above rather than introducing a
// second resolution mechanism; doesn't account for a OneDrive-redirected
// Documents folder, but %USERPROFILE%\Documents exists as a plain
// filesystem path either way even when redirected (Windows keeps it as a
// junction).
pub(crate) fn documents_dir() -> std::path::PathBuf {
  #[cfg(windows)]
  {
    std::env::var("USERPROFILE").map(|home| std::path::PathBuf::from(home).join("Documents")).unwrap_or_default()
  }
  #[cfg(not(windows))]
  {
    std::env::var("HOME").map(|home| std::path::PathBuf::from(home).join("Documents")).unwrap_or_default()
  }
}

// spec: profile-schema - a user-writable "current directory" is only a
// sane default save/active-file location if it isn't actually one of the
// OS's own protected/admin-owned trees. Found via user report (2026-08-09):
// resolve_profiles_path()'s old exe-adjacent default meant the "unsaved
// changes, save before exit?" prompt suggested writing straight into
// Program Files (or, if a future change ever based the default on the
// process's own cwd unconditionally, the same problem recurs whenever
// amm happens to be launched with Program Files/Windows/ProgramData as
// its working directory - e.g. a shortcut with no explicit "Start in").
// Checked by prefix against the actual env vars for these roots rather
// than hardcoded "C:\..." strings, since the system drive isn't always C:.
pub(crate) fn is_system_protected_dir(dir: &std::path::Path) -> bool {
  #[cfg(windows)]
  {
    for var in ["ProgramFiles", "ProgramFiles(x86)", "ProgramW6432", "ProgramData", "SystemRoot", "windir"] {
      if let Ok(root) = std::env::var(var) {
        if !root.is_empty() && dir.starts_with(&root) {
          return true;
        }
      }
    }
    false
  }
  #[cfg(target_os = "macos")]
  {
    dir.starts_with("/Applications") || dir.starts_with("/System") || dir.starts_with("/Library")
  }
  #[cfg(all(unix, not(target_os = "macos")))]
  {
    dir.starts_with("/usr") || dir.starts_with("/opt") || dir.starts_with("/etc")
  }
}

// macOS: apps launched via Launch Services (Finder/Dock/`open`, as opposed
// to a terminal) inherit launchd's minimal default PATH
// (`/usr/bin:/bin:/usr/sbin:/sbin`), not the user's shell-configured one -
// so bare executable names installed via Homebrew/nvm/npm-global/etc.
// (`claude`, `codex`, `copilot`, `pwsh`...) fail to resolve even though
// they work fine from a terminal (found via user report: "Unable to spawn
// claude because: No viable candidates found in PATH"). Both
// `profile::safe_search_path` (bare-name resolution) and every pane's
// actual spawned process (`portable_pty::CommandBuilder` inherits the
// current process's env unless overridden) read `PATH` from this process's
// own environment, so fixing it once here at startup - before anything
// else touches PATH-dependent resolution or spawns a pane - fixes both.
//
// `-lc` (login only), NOT `-ilc` (interactive+login): an earlier version of
// this fix used `-ilc`, reasoning that some users only set PATH in
// `.zshrc` (interactive-only). Found via user report ("入力できない" - pane
// input broken app-wide after this fix landed) and reproduced directly:
// `-i` makes macOS's system `/etc/zshrc_Apple_Terminal` integration run its
// Terminal.app session-restore/save machinery on every single invocation
// (~0.3s, and it writes to shared shell history/session state - "Saving
// session... saving history... truncating history files..." on stderr),
// which apparently contends with the zsh instances actually running inside
// panes (every profiles.macos.amm shell profile launches with `-l` only,
// deliberately not `-i`, matching this same reasoning). `-lc` reproduces in
// ~0.01s with no session/history side effects at all. Homebrew's own
// installer instructions add its PATH line to `~/.zprofile` (login-shell
// config) for exactly this GUI-app-PATH-visibility reason, so `-l` alone
// covers the common case; `echo -n "$PATH"` still prints last with no
// trailing newline, so the final line of captured stdout is exactly the
// PATH value even if `-l` startup prints anything ahead of it.
#[cfg(target_os = "macos")]
fn fix_macos_path_env() {
  let shell = std::env::var("SHELL").unwrap_or_else(|_| "/bin/zsh".to_string());
  let Ok(output) = std::process::Command::new(&shell).arg("-lc").arg("echo -n \"$PATH\"").output() else {
    return;
  };
  if !output.status.success() {
    return;
  }
  let Ok(text) = String::from_utf8(output.stdout) else {
    return;
  };
  if let Some(path) = text.lines().last() {
    let path = path.trim();
    if !path.is_empty() {
      std::env::set_var("PATH", path);
    }
  }
}

// spec: pane-management - WKWebView(macOS)/WebView2はTauriのフロントエンド
// 資産を固定URL(`tauri://localhost/xxx.js`)で配信するため、アプリの
// Bundle ID/AppUserModelIdに紐づく永続キャッシュへ保存される。このURLは
// ビルドを跨いでも変わらないので、内容が変わった(=アプリがアップデート
// された)ことをWebView自身が検知する手段が無く、実際に「今日何度も再ビルド
// したのに一つも反映されない」という実害が起きた(ユーザー報告)。アプリの
// バージョンが前回起動時と変わっていたら一度だけ`clear_all_browsing_data`
// で古いキャッシュを破棄する - 毎回クリアはしない(localStorage、ペイン配置
// の記憶等も一緒に消えるため、バージョンが同じ通常の再起動では避けたい)。
fn clear_webview_cache_if_version_changed(window: &tauri::WebviewWindow) {
  let marker_path = app_data_base_dir().join("amm").join("webview-version.txt");
  let current_version = env!("CARGO_PKG_VERSION");
  let previous_version = std::fs::read_to_string(&marker_path).ok();
  if previous_version.as_deref() == Some(current_version) {
    return;
  }
  if let Err(e) = window.clear_all_browsing_data() {
    log::warn!("failed to clear webview cache after version change: {e}");
    return;
  }
  if let Some(dir) = marker_path.parent() {
    let _ = std::fs::create_dir_all(dir);
  }
  let _ = std::fs::write(&marker_path, current_version);
}

#[cfg_attr(mobile, tauri::mobile_entry_point)]
pub fn run() {
  #[cfg(target_os = "macos")]
  fix_macos_path_env();
  let argv: Vec<String> = std::env::args().skip(1).collect();
  let explicit_arg = match profile::parse_profiles_path_arg(&argv) {
    Ok(arg) => arg,
    Err(msg) => {
      eprintln!("amm: {msg}");
      std::process::exit(1);
    }
  };
  let profiles_path = profile::resolve_profiles_path(explicit_arg.as_deref());
  let profiles_file = match profile::load_profiles(&profiles_path) {
    Ok(f) => f,
    Err(profile::LoadError::InvalidJson(msg)) => {
      log::error!("[profile] failed to parse {}: falling back to default cmd profile", msg);
      profile::ProfilesFile { profiles: vec![profile::SessionProfile::default_cmd()], mcp_servers: vec![] }
    }
  };
  // spec: mcp-gateway's "グローバルとファイル固有の並存" - combined before the
  // profiles_file itself is moved into ProfilesState below.
  let gateway_manager = gateway::build_manager(profiles_file.mcp_servers.clone());

  tauri::Builder::default()
    .plugin(tauri_plugin_single_instance::init(|app, _argv, _cwd| {
      // spec: 二重起動防止(tray-icon等の一意性はプロセスが単一である前提).
      // 既存インスタンスへフォーカスを移すだけで、新規インスタンスはここで終了する。
      if let Some(window) = app.get_webview_window("main") {
        let _ = window.unminimize();
        let _ = window.show();
        let _ = window.set_focus();
      }
    }))
    .manage(PtyState::default())
    .manage(mcp::McpState::default())
    .manage(gateway_manager)
    .manage(ProfilesState { file: Mutex::new(profiles_file), path: Mutex::new(profiles_path.clone()) })
    .manage(approval::ApprovalBroker::default())
    .manage(commands_misc::PendingUntrustedAutostart::default())
    .manage({
      let h = input_history::InputHistory::new();
      h.load();
      h
    })
    .manage(native_ui::TrayState::new(native_ui::load_tray_notify_enabled()))
    .manage(editor_bridge::EditorBridgeState::default())
    .plugin(tauri_plugin_notification::init())
    .plugin(tauri_plugin_dialog::init())
    .invoke_handler(tauri::generate_handler![
      pty::pty_spawn,
      pty::pty_write,
      pty::pty_resize,
      pty::pty_close,
      native_ui::flash_window,
      native_ui::set_tray_tooltip,
      native_ui::update_tray_sessions,
      native_ui::show_attention_notification,
      native_ui::read_text_file,
      commands_profile::list_profiles,
      commands_profile::get_command_type_presets,
      commands_profile::set_command_type_presets,
      commands_profile::reset_command_type_presets,
      profile::get_format_settings,
      profile::set_format_settings,
      profile::get_quick_prompts,
      profile::set_quick_prompts,
      commands_misc::get_platform,
      commands_misc::list_approvals,
      commands_misc::resolve_approval,
      commands_misc::release_approval_on_activate,
      commands_misc::add_to_history,
      commands_misc::get_recent_history,
      pty::get_pane_working_dir,
      pty::filter_text_for_send,
      commands_misc::git_repo_root,
      commands_misc::git_status_short,
      commands_misc::git_commit,
      commands_misc::git_push,
      commands_misc::get_mcp_exe_path,
      commands_misc::hook_registered_command,
      commands_misc::hook_register,
      commands_misc::hook_unregister,
      commands_misc::mcp_registered_command,
      commands_misc::mcp_register,
      commands_misc::mcp_unregister,
      commands_profile::quick_prompt_label_suggestion,
      commands_profile::strip_ansi_text,
      commands_profile::register_quick_prompt,
      commands_profile::capture_window_geometry,
      commands_profile::export_profiles_list_to_path,
      commands_profile::preview_import_profiles,
      commands_profile::merge_profiles_into_list,
      commands_profile::pick_export_save_path,
      commands_profile::pick_import_open_path,
      commands_profile::pick_folder,
      commands_profile::ask_drop_action,
      commands_profile::commit_profiles,
      commands_profile::add_profile,
      commands_profile::update_profile_settings,
      commands_profile::save_profiles_now,
      commands_profile::open_profiles_file,
      commands_profile::save_profiles_as,
      commands_profile::pick_open_profiles_path,
      commands_profile::pick_save_as_profiles_path,
      commands_misc::pick_working_dir_for_launch,
      commands_profile::list_global_mcp_servers,
      commands_profile::list_file_mcp_servers,
      commands_profile::save_global_mcp_servers,
      commands_profile::save_file_mcp_servers,
      commands_profile::export_mcp_servers_to_path,
      commands_profile::preview_import_mcp_servers,
      commands_profile::merge_mcp_servers_list,
      commands_profile::pick_export_mcp_servers_path,
      commands_profile::pick_import_mcp_servers_path,
      commands_misc::gateway_server_infos,
      commands_misc::rename_pane_nickname,
      commands_misc::launch_profile_pane,
      commands_misc::confirm_untrusted_autostart,
      commands_misc::check_pending_untrusted_autostart,
      commands_misc::has_unsaved_profile_changes,
      commands_misc::get_active_profiles_path,
      pty::notify_pane_state,
      editor_bridge::get_editor_settings,
      editor_bridge::set_editor_settings,
      editor_bridge::editor_link_open,
      editor_bridge::editor_link_copy_path,
      editor_bridge::editor_bridge_cleanup,
      editor_bridge::pick_editor_exe_path
    ])
    .setup(move |app| {
      // spec: pane-management - .setup()はwebviewがindex.htmlへナビゲートする
      // 前に実行される(上記のPendingUntrustedAutostartのコメント参照)ため、
      // ここでバージョン変更時のキャッシュ破棄を行えば、古いキャッシュされた
      // JS/HTMLが一度でも読み込まれる前に間に合う。
      if let Some(window) = app.get_webview_window("main") {
        clear_webview_cache_if_version_changed(&window);
      }
      if cfg!(debug_assertions) {
        app.handle().plugin(
          tauri_plugin_log::Builder::default()
            .level(log::LevelFilter::Info)
            .build(),
        )?;
      }
      mcp::spawn_server(app.handle().clone());
      commands_profile::spawn_profiles_hot_reload(app.handle().clone(), profiles_path.clone());
      // spec: pane-management's new "外部 .amm ファイルの自動起動確認"
      // requirement - an explicitly-opened (CLI arg / file-association
      // double-click) profiles.amm that hasn't been approved before must not
      // silently auto-start its mcpServers (the 🔴 High "confirmed no
      // arbitrary-code-execution-on-open" security-review finding). The
      // default exe-adjacent profiles.amm (explicit_arg is None) is always
      // trusted, matching today's behavior exactly.
      let auto_start_entries = app.state::<gateway::GatewayManager>().auto_start_entries();
      let needs_confirm = explicit_arg.is_some() && !profile::is_path_trusted(&profiles_path) && !auto_start_entries.is_empty();
      if needs_confirm {
        // Stored for the frontend to pull via check_pending_untrusted_autostart
        // once it has actually loaded and registered its own logic - a plain
        // app.emit() here was found to fire far too early (.setup() runs
        // before the webview has even navigated to index.html) and silently
        // dropped this event every single time (see PendingUntrustedAutostart's
        // doc comment for the full real-machine finding). Pull, not push, so
        // there's no listener-registration race to lose.
        let payload = serde_json::json!({
          "path": profiles_path.display().to_string(),
          "commands": auto_start_entries.iter().take(10).map(|(n, c)| format!("{n}: {c}")).collect::<Vec<_>>(),
        });
        *app.state::<commands_misc::PendingUntrustedAutostart>().0.lock().unwrap_or_else(|e| e.into_inner()) = Some(payload);
      } else {
        // spec: mcp-gateway's "自動起動サーバーの一括起動"
        app.state::<gateway::GatewayManager>().start_auto_start_servers();
      }
      if let Err(e) = native_ui::install_tray_icon(app.handle()) {
        log::error!("failed to install tray icon: {e:?}");
      }
      #[cfg(windows)]
      {
        if let Some(window) = app.get_webview_window("main") {
          if let Err(e) = native_ui::install_amm_system_menu(&window) {
            log::error!("failed to install AMM system menu: {e:?}");
          }
          if let Err(e) = native_ui::install_amm_syscommand_hook(&window, app.handle().clone()) {
            log::error!("failed to install AMM syscommand hook: {e:?}");
          }
        }
      }
      // install_drag_drop_handler uses Tauri's own cross-platform DragDrop
      // window event (not a Win32 API) - previously nested inside the
      // #[cfg(windows)] block above alongside the genuinely Windows-only
      // system-menu calls, so it silently never ran on non-Windows
      // platforms (found while adding macOS support). Moved out so drag &
      // drop (spec: pane-management) works on every platform.
      if let Some(window) = app.get_webview_window("main") {
        native_ui::install_drag_drop_handler(&window, app.handle().clone());
      }
      Ok(())
    })
    .run(tauri::generate_context!())
    .expect("error while running tauri application");
}

#[cfg(test)]
mod tests {
  use super::*;

  #[cfg(windows)]
  #[test]
  fn is_system_protected_dir_matches_program_files_and_windows_dir() {
    // These env vars are always set by Windows itself, not test-mutated -
    // safe under parallel test execution (unlike tests that would need to
    // set/unset a shared env var across threads).
    let program_files = std::env::var("ProgramFiles").expect("ProgramFiles must be set on Windows");
    assert!(is_system_protected_dir(std::path::Path::new(&program_files)));
    assert!(is_system_protected_dir(&std::path::PathBuf::from(&program_files).join("amm")));
    let windir = std::env::var("SystemRoot").expect("SystemRoot must be set on Windows");
    assert!(is_system_protected_dir(std::path::Path::new(&windir)));
  }

  #[cfg(windows)]
  #[test]
  fn is_system_protected_dir_allows_ordinary_user_directories() {
    assert!(!is_system_protected_dir(std::path::Path::new(r"C:\Users\someone\Documents")));
    assert!(!is_system_protected_dir(std::path::Path::new(r"D:\projects\amm")));
  }

  #[cfg(target_os = "macos")]
  #[test]
  fn is_system_protected_dir_matches_applications() {
    assert!(is_system_protected_dir(std::path::Path::new("/Applications/amm.app")));
    assert!(is_system_protected_dir(std::path::Path::new("/System/Library")));
  }

  #[cfg(target_os = "macos")]
  #[test]
  fn is_system_protected_dir_allows_ordinary_user_directories() {
    assert!(!is_system_protected_dir(std::path::Path::new("/Users/someone/Documents")));
    assert!(!is_system_protected_dir(std::path::Path::new("/Users/someone/projects/amm")));
  }

  #[test]
  fn documents_dir_is_non_empty_and_absolute() {
    // Doesn't assert an exact path (varies by CI/dev-machine username), just
    // that resolution actually produced *something* usable rather than the
    // empty-PathBuf fallback (which would silently resolve to a bogus
    // cwd-relative "Documents/profiles.amm" instead of a real user folder).
    let dir = documents_dir();
    assert!(!dir.as_os_str().is_empty(), "documents_dir() must not be empty when HOME/USERPROFILE is set");
    assert!(dir.is_absolute(), "documents_dir() must be an absolute path, got {dir:?}");
  }
}
