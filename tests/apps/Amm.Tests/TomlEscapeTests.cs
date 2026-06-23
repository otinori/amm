using Amm.Core.Mcp;

namespace Amm.Tests;

public class TomlEscapeTests
{
    [Fact]
    public void Str_PlainWindowsPath_UsesLiteralStringWithoutBackslashEscaping()
    {
        // ' を含まない通常の Windows パスは literal string ('...') で \ をそのまま出す。
        Assert.Equal(@"'C:\Program Files\amm\amm-mcp.exe'",
            TomlEscape.Str(@"C:\Program Files\amm\amm-mcp.exe"));
    }

    [Fact]
    public void Str_PathWithApostrophe_SwitchesToBasicStringAndEscapesBackslashes()
    {
        // ' を含むパスは literal だと途中で閉じて TOML が壊れるため basic string ("...")
        // へ切替え、\ と " をエスケープする。
        Assert.Equal("\"C:\\\\Users\\\\O'Brien\\\\amm-mcp.exe\"",
            TomlEscape.Str(@"C:\Users\O'Brien\amm-mcp.exe"));
    }

    [Fact]
    public void Str_PathWithApostropheAndQuote_EscapesQuote()
    {
        Assert.Equal("\"a'b\\\"c\"", TomlEscape.Str("a'b\"c"));
    }
}
