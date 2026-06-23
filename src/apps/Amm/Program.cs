using Amm.Core;

namespace Amm;

static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        var version = typeof(Program).Assembly.GetName().Version?.ToString() ?? "?";
        AppLogger.Info($"=== amm start (v{version}) ===");

        // 予期しない例外をログへ (ダイアログは標準挙動に任せる)
        Application.ThreadException += (_, e) =>
            AppLogger.Error("Unhandled UI thread exception", e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            AppLogger.Error("Unhandled domain exception", e.ExceptionObject as Exception);

        ApplicationConfiguration.Initialize();
        try
        {
            var options = AppLaunchOptions.Parse(args);
            // 起動時に Shift が押されていれば自動起動を抑止する (.amm を Shift+クリックで
            // 開いたとき等)。OS がプロセスを起こした直後＝クリック直後の修飾キー状態を読む。
            if ((Control.ModifierKeys & Keys.Shift) != 0)
            {
                options.SuppressAutoStart = true;
                AppLogger.Info("auto-start suppressed (Shift held at launch)");
            }
            Application.Run(new Forms.MdiParentForm(options));
        }
        catch (ArgumentException ex)
        {
            AppLogger.Error("Invalid startup arguments", ex);
            MessageBox.Show(
                $"起動引数が不正です。\n\n{ex.Message}\n\n" +
                "使用例:\n" +
                "  amm.exe C:\\path\\to\\profiles.json\n" +
                "  amm.exe C:\\path\\to\\profiles.amm\n" +
                "  amm.exe --start-all\n" +
                "  amm.exe .\\profiles.alt.amm --start-all",
                "起動引数エラー",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
        finally
        {
            AppLogger.Info("=== exit ===");
        }
    }
}
