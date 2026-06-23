namespace Amm.Core;

/// <summary>
/// コモンダイアログ (開く / 名前を付けて保存) の初期フォルダを優先順位に従って解決する。
///
/// 優先順位:
///   1. amm ファイルを明示指定しているとき (クリック起動 / コマンドライン引数 /
///      「開く」「名前を付けて保存」で確定済み) → その amm ファイルのあるフォルダ。
///      ただしそのフォルダ自体がシステム系フォルダ (Program Files 等) の場合は
///      スキップして 2 へ進む。
///   2. 起動時カレントフォルダがシステム系の環境変数で定義されたフォルダ
///      (%TEMP% %TMP% %SystemRoot% %ProgramData% %ProgramFiles% %ProgramFiles(x86)%
///       %ProgramW6432% %LOCALAPPDATA%) と一致 / その配下のとき → マイドキュメント。
///      未定義の環境変数は無視する。インストール後に Program Files / System32 等から
///      起動されたケースで、ユーザの想定しないシステムフォルダを提示しないための分岐。
///   3. それ以外 → 起動時カレントフォルダ。
///
/// 環境変数 / マイドキュメント / CWD を引数で受け取り副作用を持たないため単体テスト可能。
/// </summary>
public static class DialogPaths
{
    // 「ここがカレントなら作業フォルダではなくマイドキュメントを既定にすべき」
    // システム系フォルダを定義する環境変数。順序は判定結果に影響しない。
    private static readonly string[] SystemFolderEnvVars =
    [
        "TEMP", "TMP", "SystemRoot", "ProgramData",
        "ProgramFiles", "ProgramFiles(x86)", "ProgramW6432", "LOCALAPPDATA",
    ];

    /// <summary>
    /// コモンダイアログの初期フォルダを解決する。
    /// </summary>
    /// <param name="explicitFilePath">
    /// 明示指定中の amm ファイルのフルパス。未指定 (既定 profiles.amm のまま) なら null。
    /// </param>
    /// <param name="startupCurrentDirectory">起動時のカレントフォルダ。</param>
    /// <param name="myDocuments">ユーザのマイドキュメントフォルダ。</param>
    /// <param name="getEnvironmentVariable">環境変数取得 (未定義は null/空)。</param>
    public static string ResolveInitialDirectory(
        string? explicitFilePath,
        string startupCurrentDirectory,
        string myDocuments,
        Func<string, string?> getEnvironmentVariable)
    {
        // 1. 明示 amm ファイルのフォルダ (システム系フォルダでなければ使う)
        if (!string.IsNullOrWhiteSpace(explicitFilePath))
        {
            var dir = SafeGetDirectoryName(explicitFilePath);
            if (!string.IsNullOrWhiteSpace(dir) && !IsSystemFolder(dir, getEnvironmentVariable))
                return dir;
        }

        // 2. 起動時 CWD がシステム系フォルダ → マイドキュメント
        if (IsSystemFolder(startupCurrentDirectory, getEnvironmentVariable))
            return myDocuments;

        // 3. それ以外は起動時 CWD
        if (!string.IsNullOrWhiteSpace(startupCurrentDirectory))
            return startupCurrentDirectory;

        return myDocuments;
    }

    /// <summary>
    /// <paramref name="directory"/> がシステム系環境変数フォルダと一致、または
    /// その配下かを判定する。
    /// </summary>
    public static bool IsSystemFolder(string? directory, Func<string, string?> getEnvironmentVariable)
    {
        var target = NormalizePath(directory);
        if (target is null) return false;

        foreach (var name in SystemFolderEnvVars)
        {
            var envDir = NormalizePath(getEnvironmentVariable(name));
            if (envDir is null) continue; // 未定義の環境変数は無視

            if (string.Equals(target, envDir, StringComparison.OrdinalIgnoreCase))
                return true;

            // 配下判定: envDir + セパレータ が target の接頭辞か
            // (System32 は %SystemRoot% 配下、インストール先は %ProgramFiles% 配下 等)。
            var prefix = envDir + Path.DirectorySeparatorChar;
            if (target.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    private static string? NormalizePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        try
        {
            return Path.GetFullPath(path)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        catch
        {
            return null;
        }
    }

    private static string? SafeGetDirectoryName(string path)
    {
        try { return Path.GetDirectoryName(path); }
        catch { return null; }
    }
}
