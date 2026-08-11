import Cocoa
import FlutterMacOS

// 実機検証用: Dockバッジ表示 / 非アクティブ化フローティングパネルの実現性確認 (window management probe)。
// amm本体のトレイアイコン点滅・承認ハブ相当のポップアップがFlutter Desktop(macOS)で作れるかの一次検証。
class MainFlutterWindow: NSWindow {
  private var probePanel: NSPanel?

  override func awakeFromNib() {
    let flutterViewController = FlutterViewController()
    let windowFrame = self.frame
    self.contentViewController = flutterViewController
    self.setFrame(windowFrame, display: true)

    RegisterGeneratedPlugins(registry: flutterViewController)

    let channel = FlutterMethodChannel(
      name: "amm/window_probe",
      binaryMessenger: flutterViewController.engine.binaryMessenger
    )
    channel.setMethodCallHandler { [weak self] call, result in
      switch call.method {
      case "setDockBadge":
        let args = call.arguments as? [String: Any]
        NSApplication.shared.dockTile.badgeLabel = args?["label"] as? String
        result(nil)
      case "toggleAlwaysOnTopPanel":
        self?.toggleProbePanel()
        result(nil)
      default:
        result(FlutterMethodNotImplemented)
      }
    }

    super.awakeFromNib()
  }

  private func toggleProbePanel() {
    if let panel = probePanel {
      panel.close()
      probePanel = nil
      return
    }

    let panel = NSPanel(
      contentRect: NSRect(x: 40, y: 40, width: 260, height: 90),
      styleMask: [.titled, .nonactivatingPanel, .utilityWindow],
      backing: .buffered,
      defer: false
    )
    panel.title = "承認ハブ(probe)"
    panel.level = .floating
    panel.isFloatingPanel = true
    panel.hidesOnDeactivate = false
    panel.becomesKeyOnlyIfNeeded = true

    let label = NSTextField(labelWithString: "非フォーカス奪取・常時最前面\nprobe window")
    label.frame = NSRect(x: 12, y: 12, width: 236, height: 66)
    label.alignment = .center
    label.lineBreakMode = .byWordWrapping
    panel.contentView?.addSubview(label)

    panel.orderFrontRegardless()
    probePanel = panel
  }
}
