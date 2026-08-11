import 'dart:convert';

import 'package:flutter/material.dart';
import 'package:flutter/services.dart';
import 'package:web_socket_channel/web_socket_channel.dart';
import 'package:xterm/xterm.dart';

void main() {
  runApp(const PocApp());
}

class PocApp extends StatelessWidget {
  const PocApp({super.key});

  @override
  Widget build(BuildContext context) {
    return MaterialApp(
      title: 'poc-flutter-terminal',
      debugShowCheckedModeBanner: false,
      theme: ThemeData.dark(),
      home: const TerminalPocPage(),
    );
  }
}

class TerminalPocPage extends StatefulWidget {
  const TerminalPocPage({super.key});

  @override
  State<TerminalPocPage> createState() => _TerminalPocPageState();
}

class _TerminalPocPageState extends State<TerminalPocPage> {
  late final Terminal terminal;
  late final TerminalController controller;
  WebSocketChannel? channel;

  @override
  void initState() {
    super.initState();
    terminal = Terminal(maxLines: 10000);
    controller = TerminalController();

    terminal.onOutput = (data) {
      channel?.sink.add(jsonEncode({'type': 'input', 'data': data}));
    };
    terminal.onResize = (w, h, pw, ph) {
      channel?.sink.add(jsonEncode({'type': 'resize', 'cols': w, 'rows': h}));
    };

    _connect();
  }

  void _connect() {
    final uri = Uri.parse('ws://localhost:5174');
    channel = WebSocketChannel.connect(uri);
    channel!.stream.listen((event) {
      final msg = jsonDecode(event as String) as Map<String, dynamic>;
      if (msg['type'] == 'data') {
        terminal.write(msg['data'] as String);
      }
    });
  }

  @override
  void dispose() {
    channel?.sink.close();
    super.dispose();
  }

  @override
  Widget build(BuildContext context) {
    return Scaffold(
      backgroundColor: const Color(0xFF1e1e1e),
      body: SafeArea(
        child: Column(
          children: [
            Expanded(
              child: Padding(
                padding: const EdgeInsets.all(8.0),
                child: TerminalView(
                  terminal,
                  controller: controller,
                  autofocus: true,
                  backgroundOpacity: 1,
                  // 'Segoe UI Emoji' added here on the theory that xterm's default
                  // fallback list (Noto Color Emoji, Linux/Android-only) simply
                  // lacks a Windows emoji font. Verified on real Windows this does
                  // NOT fix emoji rendering (see RESULTS.md) - xterm's custom cell
                  // painter doesn't do the same font-fallback text layout a plain
                  // Text widget does, so listing the right font name here doesn't
                  // help. Left in since it's still a more correct fallback list for
                  // Windows/CJK than the upstream default, even though it doesn't
                  // fix the emoji gap.
                  textStyle: const TerminalStyle(
                    fontFamilyFallback: [
                      'Cascadia Code',
                      'Consolas',
                      'Segoe UI Emoji',
                      'Noto Sans Mono CJK JP',
                      'monospace',
                    ],
                  ),
                  theme: const TerminalTheme(
                    cursor: Color(0xFFd4d4d4),
                    selection: Color(0x554d4d4d),
                    foreground: Color(0xFFd4d4d4),
                    background: Color(0xFF1e1e1e),
                    black: Color(0xFF000000),
                    white: Color(0xFFd4d4d4),
                    red: Color(0xFFcd3131),
                    green: Color(0xFF0dbc79),
                    yellow: Color(0xFFe5e510),
                    blue: Color(0xFF2472c8),
                    magenta: Color(0xFFbc3fbc),
                    cyan: Color(0xFF11a8cd),
                    brightBlack: Color(0xFF666666),
                    brightRed: Color(0xFFf14c4c),
                    brightGreen: Color(0xFF23d18b),
                    brightYellow: Color(0xFFf5f543),
                    brightBlue: Color(0xFF3b8eea),
                    brightMagenta: Color(0xFFd670d6),
                    brightCyan: Color(0xFF29b8db),
                    brightWhite: Color(0xFFe5e5e5),
                    searchHitBackground: Color(0xFFffff00),
                    searchHitBackgroundCurrent: Color(0xFFff8000),
                    searchHitForeground: Color(0xFF000000),
                  ),
                ),
              ),
            ),
            const _WindowProbeBar(),
            _InputBar(
              onSubmit: (text) {
                channel?.sink.add(jsonEncode({'type': 'input', 'data': '$text\r'}));
              },
            ),
            const Padding(
              padding: EdgeInsets.symmetric(horizontal: 8.0, vertical: 2.0),
              child: Align(
                alignment: Alignment.centerLeft,
                child: Text(
                  '送信先: claude (アクティブ) — poc-flutter-terminal (xterm.dart + node-pty over ws)',
                  style: TextStyle(color: Colors.grey, fontSize: 11),
                ),
              ),
            ),
            const _ImeLogView(),
          ],
        ),
      ),
    );
  }
}

// 実機検証用: IME合成イベントログ表示 (ImeLog参照)。
class _ImeLogView extends StatefulWidget {
  const _ImeLogView();

  @override
  State<_ImeLogView> createState() => _ImeLogViewState();
}

class _ImeLogViewState extends State<_ImeLogView> {
  @override
  void initState() {
    super.initState();
    ImeLog.instance.addListener(_onChanged);
  }

  @override
  void dispose() {
    ImeLog.instance.removeListener(_onChanged);
    super.dispose();
  }

  void _onChanged() => setState(() {});

  @override
  Widget build(BuildContext context) {
    return Container(
      height: 90,
      width: double.infinity,
      padding: const EdgeInsets.symmetric(horizontal: 8, vertical: 4),
      color: const Color(0xFF252526),
      child: SingleChildScrollView(
        child: Text(
          ImeLog.instance.lines.join('\n'),
          style: const TextStyle(color: Color(0xFFce9178), fontSize: 11, fontFamily: 'monospace'),
        ),
      ),
    );
  }
}

// 実機検証用: Dockバッジ / 非フォーカス奪取フローティングパネルのトリガーUI (window_probe channel)。
class _WindowProbeBar extends StatefulWidget {
  const _WindowProbeBar();

  @override
  State<_WindowProbeBar> createState() => _WindowProbeBarState();
}

class _WindowProbeBarState extends State<_WindowProbeBar> {
  static const _channel = MethodChannel('amm/window_probe');
  int _badgeCount = 0;

  Future<void> _bumpBadge() async {
    _badgeCount += 1;
    await _channel.invokeMethod('setDockBadge', {'label': '$_badgeCount'});
  }

  Future<void> _clearBadge() async {
    _badgeCount = 0;
    await _channel.invokeMethod('setDockBadge', {'label': null});
  }

  Future<void> _togglePanel() async {
    await _channel.invokeMethod('toggleAlwaysOnTopPanel');
  }

  @override
  Widget build(BuildContext context) {
    return Container(
      padding: const EdgeInsets.symmetric(horizontal: 8, vertical: 4),
      color: const Color(0xFF2d2d2d),
      child: Row(
        children: [
          const Text('window probe:', style: TextStyle(color: Colors.grey, fontSize: 11)),
          const SizedBox(width: 8),
          TextButton(onPressed: _bumpBadge, child: const Text('Dockバッジ+1')),
          TextButton(onPressed: _clearBadge, child: const Text('バッジ解除')),
          TextButton(onPressed: _togglePanel, child: const Text('常時最前面パネル切替')),
        ],
      ),
    );
  }
}

// IME合成状態のログを画面表示するための簡易グローバルバス
// (実機検証用: 共通入力欄とターミナル、双方のIME compositing状態を1箇所で観測する)。
class ImeLog extends ChangeNotifier {
  static final ImeLog instance = ImeLog();
  final List<String> lines = [];

  void add(String line) {
    lines.add('[${lines.length + 1}] $line');
    if (lines.length > 20) lines.removeAt(0);
    notifyListeners();
  }
}

class _InputBar extends StatefulWidget {
  const _InputBar({required this.onSubmit});
  final void Function(String text) onSubmit;

  @override
  State<_InputBar> createState() => _InputBarState();
}

class _InputBarState extends State<_InputBar> {
  final _controller = TextEditingController();
  bool _wasComposing = false;

  @override
  void initState() {
    super.initState();
    _controller.addListener(_onControllerChanged);
  }

  void _onControllerChanged() {
    final composing = _controller.value.composing.isValid;
    if (composing && !_wasComposing) {
      ImeLog.instance.add('compositionstart on #prompt-input');
    } else if (!composing && _wasComposing) {
      ImeLog.instance.add("compositionend on #prompt-input: '${_controller.text}'");
    }
    _wasComposing = composing;
  }

  @override
  void dispose() {
    _controller.dispose();
    super.dispose();
  }

  Widget _quickSwitchButton(String label, Color bg) {
    return Container(
      margin: const EdgeInsets.only(right: 4),
      padding: const EdgeInsets.symmetric(horizontal: 8, vertical: 2),
      decoration: BoxDecoration(
        color: bg,
        borderRadius: BorderRadius.circular(4),
        border: Border.all(color: const Color(0xFF555555)),
      ),
      child: Text(label, style: const TextStyle(color: Color(0xFFdddddd), fontSize: 12)),
    );
  }

  @override
  Widget build(BuildContext context) {
    return Container(
      padding: const EdgeInsets.symmetric(horizontal: 8, vertical: 6),
      color: const Color(0xFF252526),
      child: Row(
        children: [
          // Miscellaneous-Symbols glyphs (gear/warning/bullet) hit a separate
          // CanvasKit fallback-font category (Noto Sans Symbols/Symbols2) on
          // top of the CJK ones already needed for the Japanese text below.
          // Isolating that combination reproducibly hangs first paint in
          // this sandbox (see RESULTS.md) -- plain-text status markers here
          // while keeping the Japanese sentences elsewhere, to test that
          // isolation without giving up on real CJK rendering.
          _quickSwitchButton('[Ctrl+1] [idle] claude', const Color(0xFF6b6320)),
          _quickSwitchButton('[Ctrl+2] [run] codex', const Color(0xFF3a3a3a)),
          _quickSwitchButton('[Ctrl+3] [!] copilot', const Color(0xFFa35a1a)),
          const SizedBox(width: 8),
          Expanded(
            child: TextField(
              controller: _controller,
              style: const TextStyle(color: Color(0xFFdddddd), fontSize: 13),
              decoration: InputDecoration(
                isDense: true,
                filled: true,
                fillColor: const Color(0xFF1e1e1e),
                hintText: '共通入力欄(IME確定後Enterでpty送信)',
                hintStyle: const TextStyle(color: Colors.grey, fontSize: 12),
                contentPadding: const EdgeInsets.symmetric(horizontal: 8, vertical: 8),
                border: OutlineInputBorder(
                  borderRadius: BorderRadius.circular(4),
                  borderSide: const BorderSide(color: Color(0xFF555555)),
                ),
              ),
              onSubmitted: (text) {
                widget.onSubmit(text);
                _controller.clear();
              },
            ),
          ),
        ],
      ),
    );
  }
}
