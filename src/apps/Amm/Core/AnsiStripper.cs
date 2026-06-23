using System.Text.RegularExpressions;

namespace Amm.Core;

/// <summary>
/// ANSI エスケープシーケンス (CSI / OSC など) を取り除くユーティリティ。
/// WaitPatternDetector でのパターンマッチ前処理と、セッションログ保存時の
/// プレーンテキスト化で共有する。
/// </summary>
public static class AnsiStripper
{
    // CSI (ESC [ ... letter) と OSC (ESC ] ... BEL) を除去。最低限だが
    // プロンプト検出・ログ閲覧用途では十分。
    private static readonly Regex AnsiRegex = new(
        @"\x1B\[[0-9;?]*[a-zA-Z]|\x1B\][^\x07]*\x07",
        RegexOptions.Compiled);

    public static string Strip(string text) => AnsiRegex.Replace(text, string.Empty);
}
