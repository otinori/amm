// macOS-specific native UI: focus activation and Dock attention bounce.
// Counterpart to native_ui.rs's Windows-only flash_taskbar_icon/system-menu
// code. Split into its own file rather than interleaving #[cfg(target_os =
// "macos")] blocks into native_ui.rs, per design.md D1 (mirrors the existing
// native_ui.rs-was-split-from-lib.rs precedent).
//
// Background (spec: pane-management/approval-hub/tray-icon macOS deltas,
// openspec/changes/add-macos-support/): a discarded Avalonia-based Mac PoC
// (reference/mac-avalonia-poc-lessons/README.md) found on real macOS
// hardware that plain window-activation APIs (Window.Activate(), WindowState
// toggling - the Avalonia equivalents of Tauri's show()/unminimize()/
// set_focus()) fail to steal focus from another frontmost app (confirmed
// asymmetric: worked when Finder was frontmost, failed when Claude Desktop
// was). Modern macOS increasingly restricts NSApplication self-activation
// regardless of how it's triggered. `osascript -e 'tell application id "..."
// to activate'` goes through Apple Events/Launch Services instead, a
// distinctly more privileged activation path.

/// Bundle identifier declared in tauri.conf.json's `identifier` field -
/// osascript's `tell application id "<id>"` needs this to resolve amm via
/// Launch Services rather than NSApplication self-activation.
const BUNDLE_ID: &str = "com.otinori.amm";

/// Fire-and-forget: brings amm to the foreground via AppleScript/Apple
/// Events, bypassing macOS's focus-stealing prevention for NSApplication
/// self-activation. Callers should still call show()/unminimize()/
/// set_focus() first (cheap, and correct on platforms/situations where
/// macOS's stricter path isn't needed) - this is the reliability backstop.
pub(crate) fn activate_via_apple_script() {
  let script = format!("tell application id \"{BUNDLE_ID}\" to activate");
  match std::process::Command::new("osascript").arg("-e").arg(&script).spawn() {
    Ok(_) => {}
    Err(e) => log::warn!("osascript activate failed to spawn: {e}"),
  }
}

/// Dock icon bounce for attention states (spec: pane-management "attention
/// (許可・確認待ち)のDockバウンス(macOS)", approval-hub Level 1 macOS delta).
/// Windows' equivalent is native_ui.rs's flash_taskbar_icon (FlashWindowEx);
/// this is Tauri's own cross-platform API for the same concept, no Cocoa
/// API needed directly (cross-platform-feasibility.md flagged this as an
/// open question - resolved: Tauri exposes it).
pub(crate) fn bounce_dock_icon(window: &tauri::WebviewWindow) {
  if let Err(e) = window.request_user_attention(Some(tauri::UserAttentionType::Informational)) {
    log::warn!("request_user_attention failed: {e}");
  }
}
