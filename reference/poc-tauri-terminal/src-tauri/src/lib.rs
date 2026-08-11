use portable_pty::{native_pty_system, CommandBuilder, MasterPty, PtySize};
use std::io::{Read, Write};
use std::sync::Mutex;
use tauri::{Emitter, Manager, State};

// Real ConPTY integration (via the `portable-pty` crate, no node-pty/Node.js
// involved) as a proxy for how the real Tauri port would talk to a native shell.
// One pty per app instance for this PoC; a real port would key this by pane id.
struct PtyState {
  master: Mutex<Option<Box<dyn MasterPty + Send>>>,
  writer: Mutex<Option<Box<dyn Write + Send>>>,
}

#[tauri::command]
fn pty_spawn(window: tauri::Window, state: State<PtyState>) -> Result<(), String> {
  let pty_system = native_pty_system();
  let pair = pty_system
    .openpty(PtySize {
      rows: 30,
      cols: 100,
      pixel_width: 0,
      pixel_height: 0,
    })
    .map_err(|e| e.to_string())?;

  let shell = std::env::var("COMSPEC").unwrap_or_else(|_| "powershell.exe".into());
  let cmd = CommandBuilder::new(shell);
  pair.slave.spawn_command(cmd).map_err(|e| e.to_string())?;

  let mut reader = pair.master.try_clone_reader().map_err(|e| e.to_string())?;
  let writer = pair.master.take_writer().map_err(|e| e.to_string())?;

  *state.writer.lock().unwrap() = Some(writer);
  *state.master.lock().unwrap() = Some(pair.master);

  std::thread::spawn(move || {
    let mut buf = [0u8; 4096];
    loop {
      match reader.read(&mut buf) {
        Ok(0) => break,
        Ok(n) => {
          let chunk = String::from_utf8_lossy(&buf[..n]).to_string();
          if window.emit("pty-data", chunk).is_err() {
            break;
          }
        }
        Err(_) => break,
      }
    }
  });

  Ok(())
}

#[tauri::command]
fn pty_write(data: String, state: State<PtyState>) -> Result<(), String> {
  let mut guard = state.writer.lock().unwrap();
  if let Some(writer) = guard.as_mut() {
    writer.write_all(data.as_bytes()).map_err(|e| e.to_string())?;
  }
  Ok(())
}

#[tauri::command]
fn pty_resize(cols: u16, rows: u16, state: State<PtyState>) -> Result<(), String> {
  let guard = state.master.lock().unwrap();
  if let Some(master) = guard.as_ref() {
    master
      .resize(PtySize {
        rows,
        cols,
        pixel_width: 0,
        pixel_height: 0,
      })
      .map_err(|e| e.to_string())?;
  }
  Ok(())
}

// System-menu extension (the "AMM ▶" submenu on the window's title-bar icon /
// Alt+Space menu). Verifies that Tauri's raw HWND escape hatch is enough to
// reach Win32 APIs the framework itself doesn't wrap, matching what the
// current WinForms build does via System.Windows.Forms interop.
// Win32 requires system-command IDs to be multiples of 16 (the low 4 bits are
// reserved and get overwritten by the OS to encode how the command was
// invoked), so these must already be 0x10-aligned - not just distinct.
#[cfg(windows)]
const AMM_MENU_ITEM_FLASH: usize = 0x1000;
#[cfg(windows)]
const AMM_MENU_ITEM_TOGGLE_TOP: usize = 0x1010;

#[cfg(windows)]
fn install_amm_system_menu(window: &tauri::WebviewWindow) -> windows::core::Result<()> {
  use windows::Win32::UI::WindowsAndMessaging::{AppendMenuW, CreatePopupMenu, GetSystemMenu, MF_POPUP, MF_SEPARATOR, MF_STRING};
  use windows::core::PCWSTR;

  let hwnd = window.hwnd().map_err(|_| windows::core::Error::from(windows::Win32::Foundation::E_FAIL))?;
  unsafe {
    let hmenu = GetSystemMenu(hwnd, false);
    let submenu = CreatePopupMenu()?;
    let item1: Vec<u16> = "AMM: タスクバー点滅(テスト)\0".encode_utf16().collect();
    let item2: Vec<u16> = "AMM: 常時最前面切替(テスト)\0".encode_utf16().collect();
    AppendMenuW(submenu, MF_STRING, AMM_MENU_ITEM_FLASH, PCWSTR(item1.as_ptr()))?;
    AppendMenuW(submenu, MF_STRING, AMM_MENU_ITEM_TOGGLE_TOP, PCWSTR(item2.as_ptr()))?;
    AppendMenuW(hmenu, MF_SEPARATOR, 0, PCWSTR::null())?;
    let label: Vec<u16> = "AMM ▶\0".encode_utf16().collect();
    AppendMenuW(hmenu, MF_POPUP, submenu.0 as usize, PCWSTR(label.as_ptr()))?;
  }
  Ok(())
}

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

// Approval-hub-style popup: always-on-top, no taskbar entry, and does not
// steal focus from whatever the user was doing when it appears (matching the
// current WinForms approval hub's non-activating floating behavior).
#[tauri::command]
fn show_approval_popup(app: tauri::AppHandle) -> Result<(), String> {
  if let Some(existing) = app.get_webview_window("approval-popup") {
    existing.show().map_err(|e| e.to_string())?;
    return Ok(());
  }
  tauri::WebviewWindowBuilder::new(&app, "approval-popup", tauri::WebviewUrl::App("approval-popup.html".into()))
    .title("承認ハブ (PoC)")
    .inner_size(280.0, 100.0)
    .always_on_top(true)
    .skip_taskbar(true)
    .decorations(false)
    .focused(false)
    .resizable(false)
    .build()
    .map_err(|e| e.to_string())?;
  Ok(())
}

#[tauri::command]
fn flash_window(window: tauri::WebviewWindow) -> Result<(), String> {
  #[cfg(windows)]
  {
    let hwnd = window.hwnd().map_err(|e| e.to_string())?;
    flash_taskbar_icon(hwnd);
  }
  #[cfg(not(windows))]
  {
    let _ = window;
  }
  Ok(())
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
    if cmd == AMM_MENU_ITEM_FLASH || cmd == AMM_MENU_ITEM_TOGGLE_TOP {
      let app_handle = &*(dwrefdata as *const tauri::AppHandle);
      let label = if cmd == AMM_MENU_ITEM_FLASH { "flash" } else { "toggle-top" };
      if cmd == AMM_MENU_ITEM_FLASH {
        flash_taskbar_icon(hwnd);
      }
      let _ = app_handle.emit("amm-system-menu-clicked", label);
      return windows::Win32::Foundation::LRESULT(0);
    }
  }
  DefSubclassProc(hwnd, msg, wparam, lparam)
}

#[cfg(windows)]
fn install_amm_syscommand_hook(window: &tauri::WebviewWindow, app_handle: tauri::AppHandle) -> windows::core::Result<()> {
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

#[cfg_attr(mobile, tauri::mobile_entry_point)]
pub fn run() {
  tauri::Builder::default()
    .manage(PtyState {
      master: Mutex::new(None),
      writer: Mutex::new(None),
    })
    .invoke_handler(tauri::generate_handler![pty_spawn, pty_write, pty_resize, flash_window, show_approval_popup])
    .setup(|app| {
      if cfg!(debug_assertions) {
        app.handle().plugin(
          tauri_plugin_log::Builder::default()
            .level(log::LevelFilter::Info)
            .build(),
        )?;
      }
      #[cfg(windows)]
      {
        if let Some(window) = app.get_webview_window("conpty-native") {
          if let Err(e) = install_amm_system_menu(&window) {
            log::error!("failed to install AMM system menu: {e:?}");
          }
          if let Err(e) = install_amm_syscommand_hook(&window, app.handle().clone()) {
            log::error!("failed to install AMM syscommand hook: {e:?}");
          }
        }
      }
      Ok(())
    })
    .run(tauri::generate_context!())
    .expect("error while running tauri application");
}
