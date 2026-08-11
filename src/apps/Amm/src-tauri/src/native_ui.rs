// Tray icon, window-flash, and Windows-native window chrome (system menu,
// syscommand hook, drag & drop) - split out of lib.rs (2026-07-26
// architecture-bloat cleanup, review finding: lib.rs was a single
// 1700+ line file with ~60 #[tauri::command]s and no internal module
// boundaries). No behavior change, pure move.
use serde::Serialize;
use std::sync::Mutex;
use tauri::{Emitter, Manager, State};

// spec: tray-icon's "クリック操作によるセッションジャンプ" / "右クリックコンテキスト
// メニュー" / 通知トグル永続化 - found missing entirely in the phase 8.1 parity
// audit (previously only presence/tooltip/balloon-text were implemented).
// Session bookkeeping (label, wait-order) lives in the JS pane model, not
// here, so the frontend pushes the current waiting set via
// update_tray_sessions whenever it changes; this struct is just the
// Rust-side mirror the tray menu/click handlers read from.
#[derive(Default)]
pub(crate) struct TrayState {
  notify_enabled: Mutex<bool>,
  waiting: Mutex<Vec<TraySessionInfo>>,
  last_notified: Mutex<Option<String>>,
}

impl TrayState {
  pub(crate) fn new(notify_enabled: bool) -> Self {
    TrayState { notify_enabled: Mutex::new(notify_enabled), ..Default::default() }
  }
}

#[derive(Clone, serde::Deserialize)]
pub(crate) struct TraySessionInfo {
  #[serde(rename = "paneId")]
  pane_id: String,
  label: String,
}

#[derive(Serialize, serde::Deserialize)]
struct TraySettingsFile {
  #[serde(rename = "notifyEnabled", default = "default_true")]
  notify_enabled: bool,
}
fn default_true() -> bool {
  true
}

// Windows keeps its original exe-adjacent behavior unchanged. macOS/Unix
// use app_data_base_dir() instead (found via add-macos-support real-
// machine verification): exe-adjacent means Contents/MacOS/ inside a
// .app bundle, which is both semantically wrong for per-user settings
// and a location a properly code-signed bundle shouldn't have new files
// written into after signing.
#[cfg(windows)]
fn tray_settings_path() -> std::path::PathBuf {
  let base = std::env::current_exe().ok().and_then(|p| p.parent().map(|p| p.to_path_buf())).unwrap_or_default();
  base.join("tray-settings.json")
}

#[cfg(not(windows))]
fn tray_settings_path() -> std::path::PathBuf {
  crate::app_data_base_dir().join("amm").join("tray-settings.json")
}

pub(crate) fn load_tray_notify_enabled() -> bool {
  let Ok(text) = std::fs::read_to_string(tray_settings_path()) else { return true };
  serde_json::from_str::<TraySettingsFile>(&text).map(|f| f.notify_enabled).unwrap_or(true)
}

fn save_tray_notify_enabled(enabled: bool) {
  if let Ok(json) = serde_json::to_string_pretty(&TraySettingsFile { notify_enabled: enabled }) {
    let _ = std::fs::write(tray_settings_path(), json);
  }
}

// Taskbar-flash for attention state (spec: pane-management "入力待ち状態の可視化"
// scenario "attentionのタスクバー点滅"). Ported as-is from
// reference/poc-tauri-terminal, verified on real Windows hardware there.
#[cfg(windows)]
fn flash_taskbar_icon(hwnd: windows::Win32::Foundation::HWND) {
  use windows::Win32::UI::WindowsAndMessaging::{FlashWindowEx, FLASHWINFO, FLASHW_TIMERNOFG, FLASHW_TRAY};
  let info = FLASHWINFO {
    cbSize: std::mem::size_of::<FLASHWINFO>() as u32,
    hwnd,
    dwFlags: FLASHW_TRAY | FLASHW_TIMERNOFG,
    uCount: 5,
    dwTimeout: 0,
  };
  unsafe {
    let was_flashing = FlashWindowEx(&info);
    log::info!("FlashWindowEx invoked, previous flash state: {:?}", was_flashing);
  }
}

#[tauri::command]
pub(crate) fn flash_window(window: tauri::WebviewWindow) -> Result<(), String> {
  #[cfg(windows)]
  {
    let hwnd = window.hwnd().map_err(|e| e.to_string())?;
    flash_taskbar_icon(hwnd);
  }
  #[cfg(target_os = "macos")]
  {
    crate::native_ui_macos::bounce_dock_icon(&window);
  }
  #[cfg(not(any(windows, target_os = "macos")))]
  {
    let _ = window;
  }
  Ok(())
}

// Tray icon (spec: tray-icon). Presence/tooltip/balloon-text, click-to-jump
// (oldest waiting pane), double-click-to-maximize (last notified pane), the
// "入力待ちセッション" jump submenu, and the persisted balloon-notify toggle
// are all covered here - found only partially implemented in the phase 8.1
// parity audit (presence/tooltip/balloon-text existed, the rest didn't).
pub(crate) fn install_tray_icon(app: &tauri::AppHandle) -> tauri::Result<()> {
  use tauri::tray::TrayIconBuilder;

  let tray_state = app.state::<TrayState>();
  let menu = build_tray_menu(app, &tray_state)?;

  let icon = app
    .default_window_icon()
    .cloned()
    .ok_or_else(|| tauri::Error::AssetNotFound("default window icon".into()))?;

  TrayIconBuilder::with_id("main-tray")
    .icon(icon)
    .tooltip("amm — 起動中")
    .menu(&menu)
    .show_menu_on_left_click(false)
    .on_menu_event(|app, event| {
      let id = event.id.as_ref();
      if let Some(pane_id) = id.strip_prefix("tray-jump-") {
        emit_tray_jump(app, pane_id, false);
        show_main_window(app);
        return;
      }
      match id {
        "tray-show" => show_main_window(app),
        "tray-quit" => app.exit(0),
        "tray-notify-toggle" => {
          let state = app.state::<TrayState>();
          let enabled = {
            let mut flag = state.notify_enabled.lock().unwrap_or_else(|e| e.into_inner());
            *flag = !*flag;
            *flag
          };
          save_tray_notify_enabled(enabled);
          if let (Some(tray), Ok(menu)) = (app.tray_by_id("main-tray"), build_tray_menu(app, &state)) {
            let _ = tray.set_menu(Some(menu));
          }
        }
        _ => {}
      }
    })
    .on_tray_icon_event(|tray, event| match event {
      tauri::tray::TrayIconEvent::Click {
        button: tauri::tray::MouseButton::Left,
        button_state: tauri::tray::MouseButtonState::Up,
        ..
      } => {
        let app = tray.app_handle();
        let state = app.state::<TrayState>();
        if let Some(target) = state.waiting.lock().unwrap_or_else(|e| e.into_inner()).first() {
          emit_tray_jump(app, &target.pane_id, false);
        }
        show_main_window(app);
      }
      tauri::tray::TrayIconEvent::DoubleClick {
        button: tauri::tray::MouseButton::Left,
        ..
      } => {
        let app = tray.app_handle();
        let state = app.state::<TrayState>();
        let waiting = state.waiting.lock().unwrap_or_else(|e| e.into_inner());
        let last_notified = state.last_notified.lock().unwrap_or_else(|e| e.into_inner()).clone();
        let target = last_notified
          .filter(|id| waiting.iter().any(|w| &w.pane_id == id))
          .or_else(|| waiting.first().map(|w| w.pane_id.clone()));
        drop(waiting);
        if let Some(pane_id) = target {
          emit_tray_jump(app, &pane_id, true);
        }
        show_main_window(app);
      }
      _ => {}
    })
    .build(app)?;
  Ok(())
}

#[derive(Clone, Serialize)]
struct TrayJumpEvent {
  #[serde(rename = "paneId")]
  pane_id: String,
}

fn emit_tray_jump(app: &tauri::AppHandle, pane_id: &str, maximize: bool) {
  let event_name = if maximize { "tray-jump-pane-maximize" } else { "tray-jump-pane" };
  let _ = app.emit(event_name, TrayJumpEvent { pane_id: pane_id.to_string() });
}

fn build_tray_menu(app: &tauri::AppHandle, state: &TrayState) -> tauri::Result<tauri::menu::Menu<tauri::Wry>> {
  use tauri::menu::{CheckMenuItem, Menu, MenuItem, PredefinedMenuItem, Submenu};

  let show_item = MenuItem::with_id(app, "tray-show", "amm を表示", true, None::<&str>)?;

  let waiting = state.waiting.lock().unwrap_or_else(|e| e.into_inner()).clone();
  let sessions_submenu = if waiting.is_empty() {
    Submenu::with_id_and_items(app, "tray-sessions", "入力待ちセッション", false, &[])?
  } else {
    let items = waiting
      .iter()
      .map(|w| MenuItem::with_id(app, format!("tray-jump-{}", w.pane_id), &w.label, true, None::<&str>))
      .collect::<tauri::Result<Vec<_>>>()?;
    let refs: Vec<&dyn tauri::menu::IsMenuItem<tauri::Wry>> = items.iter().map(|i| i as _).collect();
    Submenu::with_id_and_items(app, "tray-sessions", "入力待ちセッション", true, &refs)?
  };

  let notify_checked = *state.notify_enabled.lock().unwrap_or_else(|e| e.into_inner());
  let notify_item = CheckMenuItem::with_id(app, "tray-notify-toggle", "バルーン通知", true, notify_checked, None::<&str>)?;

  let separator = PredefinedMenuItem::separator(app)?;
  let quit_item = MenuItem::with_id(app, "tray-quit", "終了", true, None::<&str>)?;

  Menu::with_items(app, &[&show_item, &sessions_submenu, &notify_item, &separator, &quit_item])
}

fn show_main_window(app: &tauri::AppHandle) {
  if let Some(w) = app.get_webview_window("main") {
    let _ = w.show();
    let _ = w.unminimize();
    let _ = w.set_focus();
  }
  // spec: tray-icon "トレイ操作 / 通知クリックで前面化(macOS)" - show()/
  // unminimize()/set_focus() alone can fail to steal focus from another
  // frontmost app on macOS (see native_ui_macos.rs's doc comment). Always
  // follow up with the AppleScript path as a reliability backstop.
  #[cfg(target_os = "macos")]
  {
    crate::native_ui_macos::activate_via_apple_script();
  }
}

#[tauri::command]
pub(crate) fn set_tray_tooltip(app: tauri::AppHandle, waiting_count: u32) -> Result<(), String> {
  if let Some(tray) = app.tray_by_id("main-tray") {
    let text = if waiting_count == 0 {
      "amm — 起動中".to_string()
    } else {
      format!("amm — 入力待ち {waiting_count} 件")
    };
    tray.set_tooltip(Some(&text)).map_err(|e| e.to_string())?;
  }
  Ok(())
}

// Pushed from app.js whenever the waiting-pane set changes (state
// transition or pane close), ordered oldest-waiting-first. Rebuilds the
// tray's "入力待ちセッション" submenu so it stays in sync (spec: tray-icon
// "右クリックコンテキストメニュー").
#[tauri::command]
pub(crate) fn update_tray_sessions(app: tauri::AppHandle, state: State<TrayState>, waiting: Vec<TraySessionInfo>) -> Result<(), String> {
  *state.waiting.lock().unwrap_or_else(|e| e.into_inner()) = waiting;
  if let (Some(tray), Ok(menu)) = (app.tray_by_id("main-tray"), build_tray_menu(&app, &state)) {
    tray.set_menu(Some(menu)).map_err(|e| e.to_string())?;
  }
  Ok(())
}

#[tauri::command]
pub(crate) fn show_attention_notification(app: tauri::AppHandle, pane_id: String, title: String, body: String) -> Result<(), String> {
  notify_with_activation(app, pane_id, title, body);
  Ok(())
}

// Bypasses tauri-plugin-notification's fire-and-forget desktop backend (it
// drops the returned NotificationHandle immediately after .show(), never
// wiring up a click callback) so that clicking a toast can bring amm to
// the foreground and jump to the pane it concerns - notify-rust (already a
// transitive dependency via tauri-plugin-notification, pinned to the same
// resolved version) already supports this via on_activated, the plugin
// just never exposes it. Also called directly from mcp.rs's "amm/approval"
// handler, so Level 2 approval requests get the same OS-level toast
// treatment as plain "waiting for input" transitions - previously an
// approval request only emitted the in-window amm-approval-requested DOM
// event, invisible whenever amm isn't the foreground window.
pub(crate) fn notify_with_activation(app: tauri::AppHandle, pane_id: String, title: String, body: String) {
  let state = app.state::<TrayState>();
  if !*state.notify_enabled.lock().unwrap_or_else(|e| e.into_inner()) {
    return;
  }

  let mut notification = notify_rust::Notification::new();
  notification.summary(&title).body(&body);
  #[cfg(windows)]
  {
    // Mirrors tauri-plugin-notification desktop.rs's own guard: only set a
    // real app_id (registered on the NSIS installer's Start-Menu shortcut,
    // spec: tray-icon) when running the installed app - an unregistered
    // app_id on a dev build can make toast creation fail outright.
    // notify-rust falls back to Toast::POWERSHELL_APP_ID automatically
    // when app_id is left unset.
    use std::path::MAIN_SEPARATOR as SEP;
    if let Ok(exe) = tauri::utils::platform::current_exe() {
      if let Some(exe_dir) = exe.parent() {
        let curr_dir = exe_dir.display().to_string();
        if !(curr_dir.ends_with(format!("{SEP}target{SEP}debug").as_str())
          || curr_dir.ends_with(format!("{SEP}target{SEP}release").as_str()))
        {
          notification.app_id("com.otinori.amm");
        }
      }
    }
  }

  let handle = match notification.show() {
    Ok(handle) => handle,
    Err(e) => {
      log::warn!("failed to show toast notification: {e}");
      return;
    }
  };
  *state.last_notified.lock().unwrap_or_else(|e| e.into_inner()) = Some(pane_id.clone());
  drop(state);

  // wait_for_response blocks on a channel recv until the toast is clicked,
  // dismissed, or times out, so it must run off the calling thread (the
  // Tauri command thread or, when called from mcp.rs, the async MCP
  // handler's task) rather than block it.
  std::thread::spawn(move || {
    let _ = handle.wait_for_response(move |response: &notify_rust::NotificationResponse| {
      if response.is_default_action() {
        show_main_window(&app);
        emit_tray_jump(&app, &pane_id, false);
      }
    });
  });
}

// File drag & drop (spec: pane-management "ファイルドラッグ&ドロップ"). Uses
// Tauri's built-in DragDrop window event rather than a raw Win32 IDropTarget
// COM interception - the WinForms->Tauri migration notes flagged the old
// NativeDropTarget approach as needing individual re-verification, and this
// higher-level API is WRY's own cross-platform answer to the same problem.
const TEXT_FILE_EXTENSIONS: &[&str] = &[
  "txt", "md", "rs", "js", "ts", "jsx", "tsx", "json", "toml", "yaml", "yml", "py", "java", "c", "cpp", "h", "hpp",
  "cs", "go", "rb", "html", "htm", "css", "xml", "sh", "ps1", "bat", "cmd", "log", "csv", "ini", "cfg", "conf",
];

fn is_text_file(path: &std::path::Path) -> bool {
  path
    .extension()
    .and_then(|e| e.to_str())
    .map(|e| TEXT_FILE_EXTENSIONS.contains(&e.to_lowercase().as_str()))
    .unwrap_or(false)
}

#[tauri::command]
pub(crate) fn read_text_file(path: String) -> Result<String, String> {
  std::fs::read_to_string(&path).map_err(|e| e.to_string())
}

pub(crate) fn install_drag_drop_handler(window: &tauri::WebviewWindow, app_handle: tauri::AppHandle) {
  let scale_factor = window.scale_factor().unwrap_or(1.0);
  window.on_window_event(move |event| {
    match event {
      tauri::WindowEvent::DragDrop(tauri::DragDropEvent::Drop { paths, position }) => {
        let all_text = !paths.is_empty() && paths.iter().all(|p| is_text_file(p));
        let payload = serde_json::json!({
          "paths": paths.iter().map(|p| p.display().to_string()).collect::<Vec<_>>(),
          "allText": all_text,
          // spec: ペイン領域へのドロップはそのペインのシェル入力バッファへ (found via
          // a real-machine check, phase 8.2: this port always routed to the
          // shared input bar, unlike TerminalChildForm.cs's per-pane SendText
          // and this port's own migrate-to-tauri spec, "入力欄およびペイン領域への
          // ファイルドロップ"). Physical position converted to CSS/logical
          // pixels here so the frontend can hit-test document.elementFromPoint
          // directly against its own DOM layout.
          "cssX": position.x / scale_factor,
          "cssY": position.y / scale_factor,
        });
        let _ = app_handle.emit("amm-files-dropped", payload);
      }
      _ => {}
    }
  });
}

// Approval-hub Level 2's "非モーダル集約ポップアップ UI" (spec: approval-hub)
// used to be a second tauri::WebviewWindow built on demand by a
// show_approval_popup command, ported as-is from reference/poc-tauri-
// terminal. That design was found (real-machine check, phase 8.2) to
// permanently deadlock the entire app the moment it was invoked - every
// subsequent command, even unrelated ones, stopped responding forever.
//
// ROOT CAUSE ISOLATED 2026-07-21 via cdb.exe attached to a live hung
// process (`~*kb` on all threads - see tasks/pending-real-machine-
// verification.md item 3.4 for the full stack trace and reproduction
// steps). Same-thread reentrancy deadlock, not a WRY/WebView2 bug in the
// abstract: Tauri's IPC delivery on Windows/WebView2 arrives through the
// main webview's own CDP network interception (WebResourceRequested ->
// Fetch.requestPaused), so a command handler always runs on the main UI
// thread nested inside that webview's own request-handling callback.
// Building a *second* WebviewWindow from there requires a synchronous
// nested wait (webview2_com::wait_with_pump, a nested GetMessageA loop)
// for a second CoreWebView2Controller whose async creation needs a
// round-trip through the WebView2 browser process - which can't respond
// until the main thread returns from the callback it's already stuck
// inside. Circular wait, no timeout, permanent hang. This is why every
// mitigation tried before root-causing it (removing decorations, a
// post-build size nudge, wrapping the call in app.run_on_main_thread)
// made no difference: none of them avoid creating a second WebviewWindow
// from inside the first one's own IPC callback, which is the actual
// deadlock condition - not any particular window option.
//
// Fixed by dropping the second WebviewWindow/show_approval_popup command
// entirely: the approval panel is now rendered as an in-main-window DOM
// overlay (app.js's approvalOverlay / .approval-overlay in style.css),
// driven by the same list_approvals/resolve_approval/
// release_approval_on_activate commands and the same amm-approval-
// requested event this always used - only the "how is it displayed" part
// changed. This can never hit the deadlock above since it never creates
// a second WebView2 controller. approval-popup.html (the old window's
// content) was removed as dead code.

// System-menu extension (spec: pane-management "ペインのシステムメニュー拡張").
// IDs must be multiples of 16 - Win32 reserves the low 4 bits of wParam in
// WM_SYSCOMMAND to encode how the command was invoked (mouse vs. keyboard),
// so any ID that isn't already 16-aligned gets silently corrupted.
#[cfg(windows)]
const ID_RENAME: usize = 0x1000;
#[cfg(windows)]
const ID_EDITOR_INTEGRATION: usize = 0x1010;
#[cfg(windows)]
const ID_COPY_EDITOR_PATH: usize = 0x1020;
#[cfg(windows)]
const ID_FONT_HUGE: usize = 0x1030;
#[cfg(windows)]
const ID_FONT_LARGE: usize = 0x1040;
#[cfg(windows)]
const ID_FONT_MEDIUM: usize = 0x1050;
#[cfg(windows)]
const ID_FONT_SMALL: usize = 0x1060;
#[cfg(windows)]
const ID_FONT_TINY: usize = 0x1070;
#[cfg(windows)]
const ID_SETTINGS: usize = 0x1080;
// Standard Win32 SC_CLOSE (0xF060), hardcoded rather than imported since its
// exact export path varies across `windows` crate versions.
#[cfg(windows)]
const SC_CLOSE: u32 = 0xF060;

#[cfg(windows)]
pub(crate) fn install_amm_system_menu(window: &tauri::WebviewWindow) -> windows::core::Result<()> {
  use windows::core::PCWSTR;
  use windows::Win32::UI::WindowsAndMessaging::{
    AppendMenuW, CreatePopupMenu, DeleteMenu, GetSystemMenu, MF_BYCOMMAND, MF_POPUP, MF_SEPARATOR, MF_STRING,
  };

  let hwnd = window.hwnd().map_err(|_| windows::core::Error::from(windows::Win32::Foundation::E_FAIL))?;
  unsafe {
    let hmenu = GetSystemMenu(hwnd, false);

    let font_menu = CreatePopupMenu()?;
    for (id, label) in [
      (ID_FONT_HUGE, "極大\0"),
      (ID_FONT_LARGE, "大\0"),
      (ID_FONT_MEDIUM, "中\0"),
      (ID_FONT_SMALL, "小\0"),
      (ID_FONT_TINY, "極小\0"),
    ] {
      let w: Vec<u16> = label.encode_utf16().collect();
      AppendMenuW(font_menu, MF_STRING, id, PCWSTR(w.as_ptr()))?;
    }

    let amm_menu = CreatePopupMenu()?;
    let rename: Vec<u16> = "名前変更…\0".encode_utf16().collect();
    AppendMenuW(amm_menu, MF_STRING, ID_RENAME, PCWSTR(rename.as_ptr()))?;
    let editor: Vec<u16> = "エディタ連携\0".encode_utf16().collect();
    AppendMenuW(amm_menu, MF_STRING, ID_EDITOR_INTEGRATION, PCWSTR(editor.as_ptr()))?;
    let copy_path: Vec<u16> = "エディタ連携ファイルパスをコピー\0".encode_utf16().collect();
    AppendMenuW(amm_menu, MF_STRING, ID_COPY_EDITOR_PATH, PCWSTR(copy_path.as_ptr()))?;
    let font_label: Vec<u16> = "フォントサイズ\0".encode_utf16().collect();
    AppendMenuW(amm_menu, MF_POPUP, font_menu.0 as usize, PCWSTR(font_label.as_ptr()))?;
    let settings: Vec<u16> = "AMM 設定…\0".encode_utf16().collect();
    AppendMenuW(amm_menu, MF_STRING, ID_SETTINGS, PCWSTR(settings.as_ptr()))?;

    // spec要件: 「閉じる」を一度削除してから最下段に付け直す
    DeleteMenu(hmenu, SC_CLOSE, MF_BYCOMMAND)?;

    AppendMenuW(hmenu, MF_SEPARATOR, 0, PCWSTR::null())?;
    let amm_label: Vec<u16> = "AMM ▶\0".encode_utf16().collect();
    AppendMenuW(hmenu, MF_POPUP, amm_menu.0 as usize, PCWSTR(amm_label.as_ptr()))?;

    let close_label: Vec<u16> = "閉じる\0".encode_utf16().collect();
    AppendMenuW(hmenu, MF_STRING, SC_CLOSE as usize, PCWSTR(close_label.as_ptr()))?;
  }
  Ok(())
}

#[cfg(windows)]
fn amm_system_menu_action_label(cmd: usize) -> Option<&'static str> {
  match cmd {
    ID_RENAME => Some("rename"),
    ID_EDITOR_INTEGRATION => Some("editor-integration"),
    ID_COPY_EDITOR_PATH => Some("copy-editor-path"),
    ID_FONT_HUGE => Some("font-huge"),
    ID_FONT_LARGE => Some("font-large"),
    ID_FONT_MEDIUM => Some("font-medium"),
    ID_FONT_SMALL => Some("font-small"),
    ID_FONT_TINY => Some("font-tiny"),
    ID_SETTINGS => Some("settings"),
    _ => None,
  }
}

#[cfg(windows)]
unsafe extern "system" fn amm_subclass_proc(
  hwnd: windows::Win32::Foundation::HWND,
  msg: u32,
  wparam: windows::Win32::Foundation::WPARAM,
  lparam: windows::Win32::Foundation::LPARAM,
  _uidsubclass: usize,
  dwrefdata: usize,
) -> windows::Win32::Foundation::LRESULT {
  use windows::Win32::UI::Shell::DefSubclassProc;
  const WM_SYSCOMMAND: u32 = 0x0112;

  if msg == WM_SYSCOMMAND {
    let cmd = wparam.0 & 0xFFF0;
    if let Some(label) = amm_system_menu_action_label(cmd) {
      let app_handle = &*(dwrefdata as *const tauri::AppHandle);
      let _ = app_handle.emit("amm-system-menu-clicked", label);
      return windows::Win32::Foundation::LRESULT(0);
    }
  }
  DefSubclassProc(hwnd, msg, wparam, lparam)
}

#[cfg(windows)]
pub(crate) fn install_amm_syscommand_hook(window: &tauri::WebviewWindow, app_handle: tauri::AppHandle) -> windows::core::Result<()> {
  use windows::Win32::UI::Shell::SetWindowSubclass;
  let hwnd = window.hwnd().map_err(|_| windows::core::Error::from(windows::Win32::Foundation::E_FAIL))?;
  // Leaked intentionally: lives for the app's lifetime, same as the window itself.
  let boxed = Box::new(app_handle);
  let refdata = Box::into_raw(boxed) as usize;
  unsafe {
    SetWindowSubclass(hwnd, Some(amm_subclass_proc), 1, refdata).ok()?;
  }
  Ok(())
}

