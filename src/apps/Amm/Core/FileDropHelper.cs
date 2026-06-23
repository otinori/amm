namespace Amm.Core;

/// <summary>
/// ファイル drop 時の種別判定 / 共通成形。MDI 子への drop と入力欄への drop で
/// 共有する。
/// </summary>
internal static class FileDropHelper
{
    // テキスト形式とみなす拡張子。txt / md を起点に、編集用途で想定される
    // 言語ファイル・設定ファイル・軽量マークアップを広めに含める。判定は
    // 拡張子のみで行い、実ファイル内容の sniffing はしない (起動コスト回避)。
    private static readonly HashSet<string> TextExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        // プレーン / ドキュメント
        ".txt", ".md", ".markdown", ".rst", ".adoc", ".asciidoc", ".org", ".log",
        ".readme", ".text", ".wiki",

        // 設定・データ
        ".json", ".jsonc", ".yaml", ".yml", ".toml", ".xml", ".csv", ".tsv",
        ".ini", ".conf", ".config", ".cfg", ".env", ".properties", ".props",

        // Web
        ".html", ".htm", ".xhtml", ".css", ".scss", ".sass", ".less", ".svg",

        // JS / TS 系
        ".js", ".ts", ".jsx", ".tsx", ".mjs", ".cjs", ".vue", ".svelte",

        // 一般プログラミング言語
        ".py", ".pyi", ".rb", ".go", ".rs", ".java", ".kt", ".kts",
        ".swift", ".c", ".cpp", ".cc", ".cxx", ".h", ".hpp", ".hh",
        ".cs", ".vb", ".fs", ".fsx", ".fsi",
        ".php", ".phtml", ".pl", ".pm", ".r", ".m", ".mm",
        ".scala", ".clj", ".cljs", ".cljc", ".elm", ".erl", ".ex", ".exs",
        ".dart", ".lua", ".nim", ".zig", ".odin", ".gleam", ".ml", ".mli",

        // シェル / スクリプト
        ".sh", ".bash", ".zsh", ".fish", ".ksh",
        ".ps1", ".psm1", ".psd1", ".bat", ".cmd",

        // データクエリ
        ".sql", ".graphql", ".gql", ".prql",

        // 組版・論文
        ".tex", ".bib", ".sty", ".cls",

        // 各種 rc / dotfile (拡張子扱い)
        ".gitignore", ".gitattributes", ".editorconfig", ".dockerignore",
        ".npmrc", ".nvmrc", ".prettierrc", ".eslintrc", ".babelrc",

        // ビルド・インフラ
        ".dockerfile", ".make", ".mk", ".cmake", ".gradle", ".sbt", ".bazel",
        ".tf", ".tfvars", ".hcl", ".nix",

        // UDR / 本リポジトリで出てきがちな拡張子
        ".amm",
    };

    public static bool IsTextFile(string path)
    {
        if (string.IsNullOrEmpty(path)) return false;
        var name = Path.GetFileName(path);
        var ext = Path.GetExtension(path);
        if (!string.IsNullOrEmpty(ext) && TextExtensions.Contains(ext)) return true;
        // 拡張子なし (Makefile / Dockerfile / LICENSE / README 等) は大半が
        // テキストなのでテキスト扱い。ドット始まり (.env など) も同様。
        if (string.IsNullOrEmpty(ext)) return true;
        if (name.StartsWith('.') && !name[1..].Contains('.')) return true;
        return false;
    }

    public static bool AllTextFiles(IEnumerable<string> paths)
    {
        var any = false;
        foreach (var p in paths)
        {
            any = true;
            if (!IsTextFile(p)) return false;
        }
        return any;
    }

    /// <summary>
    /// 空白 / タブを含むパスは "..." で囲んで返す (シェルに渡しやすい形)。
    /// </summary>
    public static string QuotePath(string p) =>
        string.IsNullOrEmpty(p) ? p
        : (p.Contains(' ') || p.Contains('\t')) ? $"\"{p}\"" : p;

    public static string JoinPaths(IEnumerable<string> paths) =>
        string.Join(' ', paths.Select(QuotePath));
}
