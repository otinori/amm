// Verifies xterm.dart's terminal-emulation core (ANSI/SGR parsing, buffer
// state, wide-char width) by inspecting buffer cells directly, independent
// of Flutter Web / CanvasKit rendering. This sidesteps the CJK-font-fallback
// rendering hang documented in RESULTS.md and directly answers "does the
// emulator parse and track terminal state correctly," which plain widget
// screenshots can't verify as precisely (screenshots show pixels, not the
// underlying cell attributes).
import 'package:flutter_test/flutter_test.dart';
import 'package:xterm/xterm.dart';

void main() {
  test('ANSI SGR foreground colors (30-37) are distinct and reset works', () {
    final terminal = Terminal(maxLines: 1000);
    terminal.write('\x1b[31mR\x1b[32mG\x1b[34mB\x1b[0mN');
    final line0 = terminal.buffer.lines[0];
    final fgR = line0.getForeground(0);
    final fgG = line0.getForeground(1);
    final fgB = line0.getForeground(2);
    final fgN = line0.getForeground(3);

    expect(fgR, isNot(equals(fgN)), reason: 'red cell should differ from default');
    expect(fgG, isNot(equals(fgR)), reason: 'green should differ from red');
    expect(fgB, isNot(equals(fgG)), reason: 'blue should differ from green');
    expect(fgN, isNot(anyOf(equals(fgR), equals(fgG), equals(fgB))),
        reason: 'reset should return to a color distinct from all three ANSI colors');
  });

  test('bold / italic / underline SGR attribute flags are tracked per cell', () {
    final terminal = Terminal(maxLines: 1000);
    terminal.write('\x1b[1mBOLD\x1b[0m \x1b[3mITALIC\x1b[0m \x1b[4mUNDERLINE\x1b[0m');
    final line0 = terminal.buffer.lines[0];

    expect(line0.getAttributes(0) & CellFlags.bold, isNot(0));
    expect(line0.getAttributes(5) & CellFlags.italic, isNot(0));
    expect(line0.getAttributes(12) & CellFlags.underline, isNot(0));
  });

  test('CJK characters decode to the correct codepoint and report width 2', () {
    final terminal = Terminal(maxLines: 1000);
    terminal.write('日本語A');
    final line0 = terminal.buffer.lines[0];

    expect(line0.getCodePoint(0), 0x65E5); // 日
    expect(line0.getWidth(0), 2, reason: 'kanji should be a wide (2-cell) character');
  });

  test('box-drawing characters round-trip as exact codepoints', () {
    final terminal = Terminal(maxLines: 1000);
    terminal.write('┌─┬─┐');
    final line0 = terminal.buffer.lines[0];
    const expected = ['┌', '─', '┬', '─', '┐'];

    for (var i = 0; i < expected.length; i++) {
      expect(line0.getCodePoint(i), expected[i].runes.first,
          reason: 'cell $i should preserve the exact box-drawing codepoint');
    }
  });

  test('emoji (astral codepoint) decodes without surrogate-pair corruption', () {
    final terminal = Terminal(maxLines: 1000);
    terminal.write('🎉🚀');
    final line0 = terminal.buffer.lines[0];

    expect(line0.getCodePoint(0), 0x1F389); // 🎉
  });

  test('scrollback retains history beyond the visible viewport after resize', () {
    final terminal = Terminal(maxLines: 1000);
    terminal.resize(100, 30);
    for (var i = 1; i <= 40; i++) {
      terminal.write('scrollback line $i\r\n');
    }
    expect(terminal.buffer.lines.length, greaterThan(30));
  });

  test('resize propagates new column count via onResize callback (pty resize path)', () {
    final terminal = Terminal(maxLines: 1000);
    int? seenCols;
    terminal.onResize = (w, h, pw, ph) => seenCols = w;
    terminal.resize(79, 24);
    expect(seenCols, 79);
  });
}
