#ifndef RUNNER_FLUTTER_WINDOW_H_
#define RUNNER_FLUTTER_WINDOW_H_

#include <flutter/dart_project.h>
#include <flutter/flutter_view_controller.h>
#include <flutter/method_channel.h>
#include <flutter/standard_method_codec.h>

#include <memory>

#include "win32_window.h"

// A window that does nothing but host a Flutter view.
class FlutterWindow : public Win32Window {
 public:
  // Creates a new FlutterWindow hosting a Flutter view running |project|.
  explicit FlutterWindow(const flutter::DartProject& project);
  virtual ~FlutterWindow();

 protected:
  // Win32Window:
  bool OnCreate() override;
  void OnDestroy() override;
  LRESULT MessageHandler(HWND window, UINT const message, WPARAM const wparam,
                         LPARAM const lparam) noexcept override;

 private:
  // The project to run.
  flutter::DartProject project_;

  // The Flutter instance hosted by this window.
  std::unique_ptr<flutter::FlutterViewController> flutter_controller_;

  // 実機検証用 (amm/window_probe channel): システムメニュー拡張・
  // 非フォーカス奪取の常時最前面ポップアップの実現性確認。
  std::unique_ptr<flutter::MethodChannel<flutter::EncodableValue>>
      window_probe_channel_;
  HWND probe_panel_ = nullptr;

  void InstallAmmSystemMenu();
  void ToggleProbePanel();
  static LRESULT CALLBACK ProbePanelWndProc(HWND hwnd, UINT message,
                                            WPARAM wparam, LPARAM lparam);
};

#endif  // RUNNER_FLUTTER_WINDOW_H_
