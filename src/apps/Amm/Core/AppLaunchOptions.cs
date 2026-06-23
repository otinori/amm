namespace Amm.Core;

public sealed class AppLaunchOptions
{
    // Phase 2 (UDR-amm-20260427T0159-d4e): 「ファイル → 名前を付けて保存」で
    // 起動後にパスを切り替えられるよう init → set に緩和。AutoStartAll は
    // 起動時のみ参照されるので init のまま。
    public string ProfilesPath { get; set; } = AppPaths.ProfilesPath;
    public bool AutoStartAll { get; init; }

    // 「amm ファイルを明示的に開いた」(コマンドライン引数 / ファイル → 開く /
    // 名前を付けて保存) かを示すフラグ。false の場合は ProfilesPath は
    // BaseDirectory/profiles.amm の自動検出パス。保存ダイアログの初期フォルダ
    // と「上書き保存」の挙動分岐に使う。
    public bool HasExplicitFile { get; set; }

    // 起動時に Shift が押されていれば true。.amm を Shift+クリックで開いたとき等に
    // 自動起動 (autoStartCount / --start-all) を抑止するためのフラグ。キーボード状態に
    // 依存するため Parse(args) ではなく起動直後の Program.Main で設定する。
    public bool SuppressAutoStart { get; set; }

    // 起動時のカレントフォルダ。コモンダイアログ (開く / 名前を付けて保存) の初期
    // フォルダ判定 (DialogPaths) に使う。実行中にダイアログが CWD を変えても (OFD の
    // RestoreDirectory 既定 false) 判定がぶれないよう、起動時点の値を固定保持する。
    public string StartupCurrentDirectory { get; init; } = Environment.CurrentDirectory;

    public static AppLaunchOptions Parse(string[] args)
    {
        var startupCwd = Environment.CurrentDirectory;
        var profilesPath = AppPaths.ProfilesPath;
        var autoStartAll = false;
        var hasExplicitFile = false;

        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            switch (arg)
            {
                case "--start-all":
                    autoStartAll = true;
                    break;

                default:
                    if (arg.StartsWith("--", StringComparison.Ordinal))
                        throw new ArgumentException($"不明な引数です: {arg}");

                    if (hasExplicitFile)
                        throw new ArgumentException("profiles 設定ファイルのパスは 1 つだけ指定できます。");

                    profilesPath = arg;
                    hasExplicitFile = true;
                    break;
            }
        }

        if (!Path.IsPathRooted(profilesPath))
            profilesPath = Path.GetFullPath(Path.Combine(Environment.CurrentDirectory, profilesPath));

        return new AppLaunchOptions
        {
            ProfilesPath = profilesPath,
            AutoStartAll = autoStartAll,
            HasExplicitFile = hasExplicitFile,
            StartupCurrentDirectory = startupCwd,
        };
    }
}
