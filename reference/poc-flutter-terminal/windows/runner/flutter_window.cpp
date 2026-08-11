#include "flutter_window.h"

#include <optional>

#include "flutter/generated_plugin_registrant.h"

namespace {
// Win32 reserves the low 4 bits of a WM_SYSCOMMAND wParam to encode how the
// command was invoked, so custom IDs must be 0x10-aligned (see the Tauri
// PoC's RESULTS.md for the same lesson learned the hard way there).
constexpr UINT_PTR kAmmMenuItemToggleTop = 0x1000;
}  // namespace

FlutterWindow::FlutterWindow(const flutter::DartProject& project)
    : project_(project) {}

FlutterWindow::~FlutterWindow() {}

bool FlutterWindow::OnCreate() {
  if (!Win32Window::OnCreate()) {
    return false;
  }

  RECT frame = GetClientArea();

  // The size here must match the window dimensions to avoid unnecessary surface
  // creation / destruction in the startup path.
  flutter_controller_ = std::make_unique<flutter::FlutterViewController>(
      frame.right - frame.left, frame.bottom - frame.top, project_);
  // Ensure that basic setup of the controller was successful.
  if (!flutter_controller_->engine() || !flutter_controller_->view()) {
    return false;
  }
  RegisterPlugins(flutter_controller_->engine());
  SetChildContent(flutter_controller_->view()->GetNativeWindow());

  window_probe_channel_ =
      std::make_unique<flutter::MethodChannel<flutter::EncodableValue>>(
          flutter_controller_->engine()->messenger(), "amm/window_probe",
          &flutter::StandardMethodCodec::GetInstance());
  window_probe_channel_->SetMethodCallHandler(
      [this](const flutter::MethodCall<flutter::EncodableValue>& call,
             std::unique_ptr<flutter::MethodResult<flutter::EncodableValue>>
                 result) {
        if (call.method_name() == "setDockBadge") {
          // No taskbar/dock-badge equivalent wired up on Windows in this
          // PoC (see reference/poc-tauri-terminal's FlashWindowEx instead);
          // acknowledge so the shared Dart UI doesn't error.
          result->Success();
        } else if (call.method_name() == "toggleAlwaysOnTopPanel") {
          ToggleProbePanel();
          result->Success();
        } else {
          result->NotImplemented();
        }
      });

  InstallAmmSystemMenu();

  flutter_controller_->engine()->SetNextFrameCallback([&]() {
    this->Show();
  });

  // Flutter can complete the first frame before the "show window" callback is
  // registered. The following call ensures a frame is pending to ensure the
  // window is shown. It is a no-op if the first frame hasn't completed yet.
  flutter_controller_->ForceRedraw();

  return true;
}

void FlutterWindow::OnDestroy() {
  if (flutter_controller_) {
    flutter_controller_ = nullptr;
  }

  Win32Window::OnDestroy();
}

// 実機検証用: 「AMM ▶」サブメニューをシステムメニューへ挿入する
// (reference/poc-tauri-terminal の GetSystemMenu+AppendMenuW と同じ手法をC++側で再現)。
void FlutterWindow::InstallAmmSystemMenu() {
  HMENU hmenu = GetSystemMenu(GetHandle(), FALSE);
  if (!hmenu) return;
  HMENU submenu = CreatePopupMenu();
  AppendMenuW(submenu, MF_STRING, kAmmMenuItemToggleTop,
              L"AMM: 常時最前面パネル切替(テスト)");
  AppendMenuW(hmenu, MF_SEPARATOR, 0, nullptr);
  AppendMenuW(hmenu, MF_POPUP, reinterpret_cast<UINT_PTR>(submenu),
              L"AMM ▶");
}

// 実機検証用: 非フォーカス奪取の常時最前面ポップアップ
// (macOS版のNSPanel(.nonactivatingPanel + .floating)に相当する
// WS_EX_NOACTIVATE + WS_EX_TOPMOSTの素のWin32ポップアップウィンドウ)。
void FlutterWindow::ToggleProbePanel() {
  if (probe_panel_) {
    DestroyWindow(probe_panel_);
    probe_panel_ = nullptr;
    return;
  }

  static const wchar_t kClassName[] = L"AmmProbePanel";
  static bool class_registered = false;
  if (!class_registered) {
    WNDCLASSW wc = {};
    wc.lpfnWndProc = FlutterWindow::ProbePanelWndProc;
    wc.hInstance = GetModuleHandle(nullptr);
    wc.lpszClassName = kClassName;
    wc.hbrBackground = CreateSolidBrush(RGB(0xa3, 0x5a, 0x1a));
    RegisterClassW(&wc);
    class_registered = true;
  }

  // WS_EX_NOACTIVATE: does not steal focus when shown.
  // WS_EX_TOPMOST: always-on-top.
  probe_panel_ = CreateWindowExW(
      WS_EX_TOPMOST | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW,
      kClassName, L"承認ハブ (probe)", WS_POPUP | WS_BORDER,
      100, 100, 260, 90, GetHandle(), nullptr, GetModuleHandle(nullptr),
      nullptr);
  if (probe_panel_) {
    ShowWindow(probe_panel_, SW_SHOWNOACTIVATE);
  }
}

LRESULT CALLBACK FlutterWindow::ProbePanelWndProc(HWND hwnd, UINT message,
                                                  WPARAM wparam,
                                                  LPARAM lparam) {
  if (message == WM_PAINT) {
    PAINTSTRUCT ps;
    HDC hdc = BeginPaint(hwnd, &ps);
    SetBkMode(hdc, TRANSPARENT);
    SetTextColor(hdc, RGB(0xff, 0xff, 0xff));
    RECT rect;
    GetClientRect(hwnd, &rect);
    const wchar_t* text = L"非フォーカス奪取・常時最前面\nprobe window";
    DrawTextW(hdc, text, -1, &rect, DT_CENTER | DT_VCENTER | DT_WORDBREAK);
    EndPaint(hwnd, &ps);
    return 0;
  }
  return DefWindowProc(hwnd, message, wparam, lparam);
}

LRESULT
FlutterWindow::MessageHandler(HWND hwnd, UINT const message,
                              WPARAM const wparam,
                              LPARAM const lparam) noexcept {
  if (message == WM_SYSCOMMAND) {
    UINT_PTR cmd = wparam & 0xFFF0;
    if (cmd == kAmmMenuItemToggleTop) {
      ToggleProbePanel();
      return 0;
    }
  }

  // Give Flutter, including plugins, an opportunity to handle window messages.
  if (flutter_controller_) {
    std::optional<LRESULT> result =
        flutter_controller_->HandleTopLevelWindowProc(hwnd, message, wparam,
                                                      lparam);
    if (result) {
      return *result;
    }
  }

  switch (message) {
    case WM_FONTCHANGE:
      flutter_controller_->engine()->ReloadSystemFonts();
      break;
  }

  return Win32Window::MessageHandler(hwnd, message, wparam, lparam);
}
