// Editor integration (spec: editor-integration). Port of .NET's
// MdiParentForm.cs's EditorBridge class: create a per-pane temp file,
// launch a configured external editor on it, watch for saves (poll-based -
// this port has no FileSystemWatcher equivalent and already prefers
// hand-rolled polling over a `notify` crate dependency elsewhere, see
// commands_profile::spawn_profiles_hot_reload), and forward saved content
// to the target pane through the normal send path. Settings (editor mode /
// custom path / post-send action) are application-wide, not per-profile,
// matching the .NET original's own scope.
use serde::{Deserialize, Serialize};
use std::collections::hash_map::DefaultHasher;
use std::collections::HashMap;
use std::hash::{Hash, Hasher};
use std::path::PathBuf;
use std::sync::{Arc, Mutex};
use std::time::SystemTime;
use tauri::{AppHandle, Emitter, Manager, State};
use uuid::Uuid;

use crate::profile;
use crate::pty::PtyState;

// ---- エディタ全体設定 (design.md D2: %LOCALAPPDATA%\amm\editor-settings.json) ----

fn default_editor_mode() -> String {
  "Associated".to_string()
}
fn default_post_send_action() -> String {
  "Focus".to_string()
}

#[derive(Clone, Serialize, Deserialize)]
pub struct EditorSettingsFile {
  #[serde(rename = "editorMode", default = "default_editor_mode")]
  pub editor_mode: String,
  #[serde(rename = "customEditorPath", default)]
  pub custom_editor_path: String,
  #[serde(rename = "postSendAction", default = "default_post_send_action")]
  pub post_send_action: String,
}

impl Default for EditorSettingsFile {
  fn default() -> Self {
    Self {
      editor_mode: default_editor_mode(),
      custom_editor_path: String::new(),
      post_send_action: default_post_send_action(),
    }
  }
}

// %LOCALAPPDATA%\amm\ resolution - same pattern as gateway.rs's
// mcp-servers.json / input_history.rs's history.json / profile.rs's
// trusted-profiles.json (exe-adjacent was considered and rejected, see
// design.md D2: a standard non-admin install under `C:\Program Files\`
// isn't writable there, so exe-adjacent saves fail silently).
fn amm_data_dir() -> PathBuf {
  crate::app_data_base_dir().join("amm")
}

fn editor_settings_path() -> PathBuf {
  amm_data_dir().join("editor-settings.json")
}

pub(crate) fn load_editor_settings() -> EditorSettingsFile {
  let Ok(text) = std::fs::read_to_string(editor_settings_path()) else {
    return EditorSettingsFile::default();
  };
  serde_json::from_str(&text).unwrap_or_default()
}

pub(crate) fn save_editor_settings(settings: &EditorSettingsFile) {
  let dir = amm_data_dir();
  let _ = std::fs::create_dir_all(&dir);
  if let Ok(json) = serde_json::to_string_pretty(settings) {
    let _ = std::fs::write(editor_settings_path(), json);
  }
}

#[tauri::command]
pub(crate) fn get_editor_settings() -> EditorSettingsFile {
  load_editor_settings()
}

#[tauri::command]
pub(crate) fn set_editor_settings(settings: EditorSettingsFile) {
  save_editor_settings(&settings);
}

// spec: editor-integration「エディタ全体設定の永続化」- AMM設定ダイアログの
// カスタムエディタパス欄の「参照...」ボタン。既存のpick_folder/
// pick_working_dir_for_launch(commands_misc.rs)と同じtauri-plugin-dialog
// 呼び出しパターン、対象がフォルダではなく実行ファイルである点のみ異なる。
// macOS: this and every pick_*/ask_drop_action command elsewhere in the
// codebase must be `async fn` - tauri_plugin_dialog's blocking_* helpers
// run_on_main_thread() the panel and block the calling thread on its
// result. A plain (non-async) command runs synchronously on whatever
// thread delivered the IPC message, which on macOS is WKWebView's
// main-thread callback, deadlocking the app. `async fn` makes Tauri
// dispatch it off that thread instead.
#[tauri::command]
pub(crate) async fn pick_editor_exe_path(app: AppHandle) -> Option<String> {
  use tauri_plugin_dialog::DialogExt;
  app
    .dialog()
    .file()
    .add_filter("実行ファイル", &["exe"])
    .add_filter("すべてのファイル", &["*"])
    .blocking_pick_file()
    .and_then(|fp| fp.into_path().ok())
    .map(|p| p.to_string_lossy().to_string())
}

// ---- 一時ファイル名の組み立て ----

// Windows上でファイル名として使えない文字を`_`に置換する (.NET版の
// Path.GetInvalidFileNameChars()相当、ASCII制御文字も含めて手動列挙)。
fn is_invalid_filename_char(c: char) -> bool {
  matches!(c, '<' | '>' | ':' | '"' | '/' | '\\' | '|' | '?' | '*') || (c as u32) < 0x20
}

fn sanitize_filename_component(s: &str) -> String {
  s.chars().map(|c| if is_invalid_filename_char(c) { '_' } else { c }).collect()
}

// spec: editor-integration「エディタ連携の起動」の一時ファイルパス。.NET版は
// `prompt-<profile>[(instance)]-<6桁ID>.md`だったが、このポートのPtyEntryは
// インスタンス番号を保持していない(ペインのラベル/番号付けはフロントエンド側の
// UI状態でバックエンドは関知しない) - profile名+ランダムIDで一意性は十分なため
// インスタンス番号部分は省略する(design.mdに記載の意図的な簡略化)。
fn build_temp_file_name(profile_label: Option<&str>) -> String {
  let label = profile_label.map(sanitize_filename_component).unwrap_or_else(|| "pane".to_string());
  let short_id: String = Uuid::new_v4().simple().to_string().chars().take(6).collect();
  format!("prompt-{label}-{short_id}.md")
}

fn bridge_dir() -> PathBuf {
  amm_data_dir().join("editor")
}

fn write_initial_content(path: &std::path::Path, initial_content: Option<&str>) -> std::io::Result<()> {
  let body = match initial_content {
    Some(text) if !text.is_empty() => text.to_string(),
    _ => "<!-- amm エディタ連携: 保存するたびに対象ペインへ送信されます。\n     このペインを閉じるとこの一時ファイルは自動削除されます。 -->\n".to_string(),
  };
  std::fs::write(path, body)
}

// ---- エディタ起動 (design.md D3のフォールバック含む) ----

// カスタムパスが未設定/不在なら "Associated" へフォールバックすべきかどうかの
// 純粋な判定 (実プロセス起動はテスト不可能なので、この判定だけ分離してテストする)。
fn should_fallback_to_associated(mode: &str, custom_editor_path: &str) -> bool {
  mode == "Custom" && (custom_editor_path.trim().is_empty() || !std::path::Path::new(custom_editor_path.trim()).exists())
}

pub(crate) fn launch_editor(settings: &EditorSettingsFile, file_path: &std::path::Path) {
  let effective_mode = if should_fallback_to_associated(&settings.editor_mode, &settings.custom_editor_path) {
    log::warn!("editor-bridge: custom path not found, falling back to associated app: '{}'", settings.custom_editor_path);
    "Associated"
  } else {
    settings.editor_mode.as_str()
  };
  match effective_mode {
    "Notepad" => {
      let _ = std::process::Command::new("notepad.exe").arg(file_path).spawn();
    }
    "Custom" => {
      let _ = std::process::Command::new(settings.custom_editor_path.trim()).arg(file_path).spawn();
    }
    _ => shell_execute_open(file_path),
  }
}

#[cfg(windows)]
fn shell_execute_open(file_path: &std::path::Path) {
  use std::os::windows::ffi::OsStrExt;
  use windows::core::PCWSTR;
  use windows::Win32::UI::Shell::ShellExecuteW;
  use windows::Win32::UI::WindowsAndMessaging::SW_SHOWNORMAL;

  let wide: Vec<u16> = file_path.as_os_str().encode_wide().chain(std::iter::once(0)).collect();
  unsafe {
    ShellExecuteW(None, PCWSTR::null(), PCWSTR(wide.as_ptr()), PCWSTR::null(), PCWSTR::null(), SW_SHOWNORMAL);
  }
}

// macOS's `open` command is the Cocoa/LaunchServices equivalent of
// ShellExecuteW's "open with associated app" - found while verifying
// editor-integration on macOS (add-macos-support): this was previously a
// true no-op stub (kept only "cfg-symmetric" like gateway.rs's Windows
// Job Object fallback), meaning the *default* editor mode did nothing at
// all on macOS, not just the Windows-only "Notepad" mode.
#[cfg(target_os = "macos")]
fn shell_execute_open(file_path: &std::path::Path) {
  let _ = std::process::Command::new("open").arg(file_path).spawn();
}

#[cfg(all(unix, not(target_os = "macos")))]
fn shell_execute_open(_file_path: &std::path::Path) {}

// ---- ブリッジのライフサイクル (design.md D1: ポーリング監視, D4: クリーンアップ) ----

struct BridgeInner {
  file_path: PathBuf,
  last_sent_hash: Option<u64>,
  // false になったらポーリングスレッドは次回ループで自主停止する。
  active: bool,
}

#[derive(Default)]
pub struct EditorBridgeState {
  bridges: Mutex<HashMap<String, Arc<Mutex<BridgeInner>>>>,
}

impl EditorBridgeState {
  fn existing_path(&self, pane_id: &str) -> Option<PathBuf> {
    let bridges = self.bridges.lock().unwrap_or_else(|e| e.into_inner());
    let inner = bridges.get(pane_id)?;
    let inner = inner.lock().unwrap_or_else(|e| e.into_inner());
    inner.active.then(|| inner.file_path.clone())
  }
}

fn raw_content_hash(text: &str) -> u64 {
  let mut hasher = DefaultHasher::new();
  text.hash(&mut hasher);
  hasher.finish()
}

// ペインに紐づくアクティブなブリッジを取得(あれば再利用)、無ければ一時ファイルを
// 新規作成してポーリング監視スレッドを起動する。戻り値は一時ファイルパス。
fn get_or_create_bridge(
  app: &AppHandle,
  pane_id: &str,
  initial_content: Option<&str>,
  editor_state: &EditorBridgeState,
  pty_state: &PtyState,
) -> std::io::Result<PathBuf> {
  if let Some(path) = editor_state.existing_path(pane_id) {
    return Ok(path);
  }

  let dir = bridge_dir();
  std::fs::create_dir_all(&dir)?;
  let profile_label = pty_state.profile_name_for(pane_id);
  let file_name = build_temp_file_name(profile_label.as_deref());
  let file_path = dir.join(file_name);
  write_initial_content(&file_path, initial_content)?;

  let inner = Arc::new(Mutex::new(BridgeInner { file_path: file_path.clone(), last_sent_hash: None, active: true }));
  editor_state.bridges.lock().unwrap_or_else(|e| e.into_inner()).insert(pane_id.to_string(), inner.clone());

  spawn_watch_thread(app.clone(), pane_id.to_string(), inner);
  Ok(file_path)
}

#[derive(Clone, serde::Serialize)]
struct EditorBridgeSendEvent {
  #[serde(rename = "paneId")]
  pane_id: String,
  text: String,
  #[serde(rename = "postSendAction")]
  post_send_action: String,
}

// design.md D1: 250msごとにmtimeをポーリングし、profile::hot_reload_should_apply
// (2回連続で同一mtimeを観測したら安定とみなす)と同じ判定ロジックを再利用する
// ことで.NET版の500msデバウンスタイマーと実質同等の待ち時間を得る。
fn spawn_watch_thread(app: AppHandle, pane_id: String, inner: Arc<Mutex<BridgeInner>>) {
  std::thread::spawn(move || {
    let read_mtime = |p: &std::path::Path| std::fs::metadata(p).and_then(|m| m.modified()).ok();
    let file_path = inner.lock().unwrap_or_else(|e| e.into_inner()).file_path.clone();
    let mut committed: Option<SystemTime> = read_mtime(&file_path);
    let mut candidate = committed;
    loop {
      std::thread::sleep(std::time::Duration::from_millis(250));
      if !inner.lock().unwrap_or_else(|e| e.into_inner()).active {
        break;
      }
      // 対象ペインが(明示的なクリーンアップ呼び出しを経ずに)既に閉じられていた
      // 場合の保険。pty.rsの静穏監視スレッドが自ペイン消滅時に自主終了するのと
      // 同じパターン。
      let pty_state = app.state::<PtyState>();
      if !pty_state.contains(&pane_id) {
        cleanup_bridge_file(&inner);
        break;
      }
      let current = read_mtime(&file_path);
      if profile::hot_reload_should_apply(&committed, &candidate, &current) {
        committed = current;
        process_saved_file(&app, &pane_id, &file_path, &inner);
      }
      candidate = current;
    }
  });
}

fn process_saved_file(app: &AppHandle, pane_id: &str, file_path: &std::path::Path, inner: &Arc<Mutex<BridgeInner>>) {
  let Ok(text) = std::fs::read_to_string(file_path) else { return };
  if text.is_empty() {
    return;
  }
  let hash = raw_content_hash(&text);
  {
    let mut guard = inner.lock().unwrap_or_else(|e| e.into_inner());
    if guard.last_sent_hash == Some(hash) {
      return;
    }
    guard.last_sent_hash = Some(hash);
  }

  // フィルタ適用後が空文字列なら送信しない(spec: 保存監視による自動送信)。
  // 実際の送信・フィルタ適用自体はフロントエンドのsendToPane()に委ねる
  // (filter_lines_for_sendは冪等なので、ここでの事前チェックと二重適用しても
  // 結果は変わらない)。フィルタ設定はコマンドごとではなくアプリ全体設定
  // (profile::FormatSettingsFile、ユーザー要望2026-08-04)。
  let settings = profile::load_format_settings();
  let raw_lines: Vec<String> = text.split('\n').map(String::from).collect();
  let filtered = profile::filter_lines_for_send(&raw_lines, settings.collapse_blank_lines, &settings.comment_prefixes).join("\n");
  if filtered.trim().is_empty() {
    return;
  }

  let post_send_action = load_editor_settings().post_send_action;
  let _ = app.emit(
    "amm-editor-bridge-send",
    EditorBridgeSendEvent { pane_id: pane_id.to_string(), text, post_send_action },
  );
}

fn cleanup_bridge_file(inner: &Arc<Mutex<BridgeInner>>) {
  let mut guard = inner.lock().unwrap_or_else(|e| e.into_inner());
  guard.active = false;
  let _ = std::fs::remove_file(&guard.file_path);
}

// spec: editor-integration「ブリッジのクリーンアップ」- 対象ペインが閉じられた
// ときにフロントエンドから呼ばれる(pane-lifecycle.jsのclosePane())。
#[tauri::command]
pub(crate) fn editor_bridge_cleanup(pane_id: String, editor_state: State<EditorBridgeState>) {
  let inner = editor_state.bridges.lock().unwrap_or_else(|e| e.into_inner()).remove(&pane_id);
  if let Some(inner) = inner {
    cleanup_bridge_file(&inner);
  }
}

// ---- Tauriコマンド ----

// spec: editor-integration「エディタ連携の起動」。initial_contentは呼び出し側
// (フロントエンド)の共有入力欄の現在値。
#[tauri::command]
pub(crate) fn editor_link_open(
  pane_id: String,
  initial_content: Option<String>,
  app: AppHandle,
  editor_state: State<EditorBridgeState>,
  pty_state: State<PtyState>,
) -> Result<(), String> {
  let path = get_or_create_bridge(&app, &pane_id, initial_content.as_deref(), &editor_state, &pty_state).map_err(|e| e.to_string())?;
  launch_editor(&load_editor_settings(), &path);
  Ok(())
}

// spec: editor-integration「エディタ連携ファイルパスのコピー」。エディタは
// 起動せず、既存または新規作成したブリッジの一時ファイルパスを返す
// (クリップボードへの実書き込みはフロントエンド側の既存navigator.clipboard
// 慣習に委ねる)。
#[tauri::command]
pub(crate) fn editor_link_copy_path(
  pane_id: String,
  app: AppHandle,
  editor_state: State<EditorBridgeState>,
  pty_state: State<PtyState>,
) -> Result<String, String> {
  let path = get_or_create_bridge(&app, &pane_id, None, &editor_state, &pty_state).map_err(|e| e.to_string())?;
  Ok(path.display().to_string())
}

#[cfg(test)]
mod tests {
  use super::*;

  #[test]
  fn sanitize_filename_component_replaces_invalid_chars() {
    assert_eq!(sanitize_filename_component("claude:code/test"), "claude_code_test");
    assert_eq!(sanitize_filename_component("normal-name_1"), "normal-name_1");
  }

  #[test]
  fn build_temp_file_name_uses_pane_fallback_when_no_profile() {
    let name = build_temp_file_name(None);
    assert!(name.starts_with("prompt-pane-"));
    assert!(name.ends_with(".md"));
  }

  #[test]
  fn build_temp_file_name_embeds_sanitized_profile_label() {
    let name = build_temp_file_name(Some("Claude:Code"));
    assert!(name.starts_with("prompt-Claude_Code-"), "got: {name}");
  }

  #[test]
  fn write_initial_content_uses_provided_text_when_present() {
    let dir = std::env::temp_dir().join(format!("amm-editor-bridge-test-{}", Uuid::new_v4()));
    std::fs::create_dir_all(&dir).unwrap();
    let path = dir.join("t.md");
    write_initial_content(&path, Some("hello")).unwrap();
    assert_eq!(std::fs::read_to_string(&path).unwrap(), "hello");
    std::fs::remove_dir_all(&dir).unwrap();
  }

  #[test]
  fn write_initial_content_uses_placeholder_comment_when_absent() {
    let dir = std::env::temp_dir().join(format!("amm-editor-bridge-test-{}", Uuid::new_v4()));
    std::fs::create_dir_all(&dir).unwrap();
    let path = dir.join("t.md");
    write_initial_content(&path, None).unwrap();
    let text = std::fs::read_to_string(&path).unwrap();
    assert!(text.contains("amm エディタ連携"));
    std::fs::remove_dir_all(&dir).unwrap();
  }

  #[test]
  fn write_initial_content_uses_placeholder_when_empty_string() {
    let dir = std::env::temp_dir().join(format!("amm-editor-bridge-test-{}", Uuid::new_v4()));
    std::fs::create_dir_all(&dir).unwrap();
    let path = dir.join("t.md");
    write_initial_content(&path, Some("")).unwrap();
    let text = std::fs::read_to_string(&path).unwrap();
    assert!(text.contains("amm エディタ連携"));
    std::fs::remove_dir_all(&dir).unwrap();
  }

  #[test]
  fn should_fallback_to_associated_when_custom_path_missing() {
    assert!(should_fallback_to_associated("Custom", ""));
    assert!(should_fallback_to_associated("Custom", "C:\\does\\not\\exist.exe"));
  }

  #[test]
  fn should_not_fallback_for_non_custom_modes() {
    assert!(!should_fallback_to_associated("Associated", ""));
    assert!(!should_fallback_to_associated("Notepad", ""));
  }

  #[test]
  fn should_not_fallback_when_custom_path_exists() {
    // std::env::current_exe() は常に存在するパスなので fallback判定のexists()
    // チェックの「存在する」分岐を確実に踏める。
    let exe = std::env::current_exe().unwrap().display().to_string();
    assert!(!should_fallback_to_associated("Custom", &exe));
  }

  #[test]
  fn raw_content_hash_is_deterministic_and_content_sensitive() {
    assert_eq!(raw_content_hash("abc"), raw_content_hash("abc"));
    assert_ne!(raw_content_hash("abc"), raw_content_hash("abd"));
  }

  #[test]
  fn editor_settings_file_defaults() {
    let s = EditorSettingsFile::default();
    assert_eq!(s.editor_mode, "Associated");
    assert_eq!(s.custom_editor_path, "");
    assert_eq!(s.post_send_action, "Focus");
  }

  #[test]
  fn editor_settings_roundtrip_via_json() {
    let s = EditorSettingsFile { editor_mode: "Custom".to_string(), custom_editor_path: "C:\\x.exe".to_string(), post_send_action: "Maximize".to_string() };
    let json = serde_json::to_string(&s).unwrap();
    let back: EditorSettingsFile = serde_json::from_str(&json).unwrap();
    assert_eq!(back.editor_mode, "Custom");
    assert_eq!(back.custom_editor_path, "C:\\x.exe");
    assert_eq!(back.post_send_action, "Maximize");
  }

  #[test]
  fn editor_settings_deserialize_defaults_missing_fields() {
    let back: EditorSettingsFile = serde_json::from_str("{}").unwrap();
    assert_eq!(back.editor_mode, "Associated");
    assert_eq!(back.post_send_action, "Focus");
  }

  #[test]
  fn editor_settings_falls_back_on_invalid_json() {
    // load_editor_settingsの不正JSON時フォールバック相当を、ファイルI/Oを
    // 介さず直接検証する(load_editor_settings自体は%LOCALAPPDATA%依存のため
    // 単体テストでは経路のみ確認)。
    let result: Result<EditorSettingsFile, _> = serde_json::from_str("not json");
    assert!(result.is_err());
  }
}
