using System.Text;

namespace Amm.Core.Mcp;

/// <summary>
/// TOML 文字列リテラルを安全に生成する。Windows パス (バックスラッシュを含む) を
/// config.toml に書くため、通常は literal string ('...') を使い `\` をそのまま書く。
/// ただし値に `'` (アポストロフィ) が含まれると literal string は途中で閉じてしまい、
/// 後続が任意 TOML として解釈される (例: C:\Users\O'Brien\... で設定破壊)。その場合は
/// basic string ("...") へ切替え、`\` と `"` をエスケープする。
/// </summary>
internal static class TomlEscape
{
    public static string Str(string value)
    {
        if (!value.Contains('\''))
            return "'" + value + "'";

        var sb = new StringBuilder(value.Length + 8);
        sb.Append('"');
        foreach (var c in value)
        {
            switch (c)
            {
                case '\\': sb.Append("\\\\"); break;
                case '"': sb.Append("\\\""); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                default: sb.Append(c); break;
            }
        }
        sb.Append('"');
        return sb.ToString();
    }
}
