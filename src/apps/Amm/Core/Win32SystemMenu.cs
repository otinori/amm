using System.Runtime.InteropServices;

namespace Amm.Core;

/// <summary>
/// MDI 子ウィンドウのシステムメニュー (タイトルバー左上アイコン / Alt+Space)
/// に独自項目を挿入し、WM_SYSCOMMAND で受信する一連の P/Invoke ラッパー
/// (UDR-amm-20260427T0055-2c1)。
///
/// カスタム ID は SC_AMM_BASE 以降を使う。Windows 既定のシステムコマンド
/// (SC_CLOSE=0xF060 等) の下位 4 ビットは予約されているため、0x10 単位で
/// 切り上げた値を割り当てる。
/// </summary>
internal static class Win32SystemMenu
{
    public const int WM_SYSCOMMAND = 0x0112;

    // Windows 予約: SC_* の下位 4 bit は内部用。0xF000-0xFFF0 帯は使わない。
    // 0x1000-0xEFF0 の 16 単位範囲なら独自 ID として安全に使える。
    public const int SC_AMM_SETTINGS = 0x1010;
    public const int SC_AMM_COPY_EDITOR_PATH = 0x1020; // Phase 4 (UDR-amm-20260427T0238-fb5)
    public const int SC_AMM_RENAME = 0x1030;
    public const int SC_AMM_EDITOR_LINK = 0x1040; // 「エディタ連携」: 連携起動のみ (パスコピーなし)

    // フォントサイズサブメニュー: per-MDI ランタイム上書き (保存しない)
    public const int SC_AMM_FONT_XL = 0x1050; // 極大
    public const int SC_AMM_FONT_L  = 0x1060; // 大
    public const int SC_AMM_FONT_M  = 0x1070; // 中 (既定)
    public const int SC_AMM_FONT_S  = 0x1080; // 小
    public const int SC_AMM_FONT_XS = 0x1090; // 極小

    // Windows 標準コマンド (winuser.h)。並び替えのため一度削除して末尾に再追加する。
    private const int SC_CLOSE = 0xF060;

    public const uint MF_STRING    = 0x00000000;
    public const uint MF_POPUP     = 0x00000010;
    public const uint MF_SEPARATOR = 0x00000800;
    public const uint MF_BYCOMMAND = 0x00000000;

    [DllImport("user32.dll")]
    public static extern IntPtr GetSystemMenu(IntPtr hWnd, [MarshalAs(UnmanagedType.Bool)] bool bRevert);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool AppendMenu(IntPtr hMenu, uint uFlags, IntPtr uIDNewItem, string? lpNewItem);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteMenu(IntPtr hMenu, uint uPosition, uint uFlags);

    [DllImport("user32.dll")]
    private static extern IntPtr CreatePopupMenu();

    /// <summary>
    /// 指定ハンドルのシステムメニューに独自項目を挿入し、最後に「閉じる」(SC_CLOSE)
    /// を最下段へ移し直す。並びは:
    ///   (Restore/Move/Size/Min/Max + 既定 [SEP])
    ///   AMM ▶
    ///     名前変更…
    ///     [SEP]
    ///     エディタ連携
    ///     エディタ連携ファイルパスをコピー
    ///     [SEP]
    ///     フォントサイズ ▶ (極大/大/中/小/極小)
    ///     AMM 設定…
    ///   [SEP]
    ///   閉じる
    /// 同じ ID で重複登録しないよう、呼び出し側でガードすること。
    /// </summary>
    public static void RegisterAmmSettings(
        IntPtr hWnd,
        string settingsText,
        string copyPathText,
        string renameText,
        string editorLinkText)
    {
        var hMenu = GetSystemMenu(hWnd, false);
        if (hMenu == IntPtr.Zero) return;

        // 「閉じる」を一旦削除して、独自項目の後ろに付け直す。SC_CLOSE は
        // ウィンドウクラスの所定コマンドなので、AppendMenu で再登録すれば
        // クリックで通常通り WM_SYSCOMMAND/SC_CLOSE が飛ぶ。Alt+F4 などの
        // ハードコードされた close 経路はメニュー有無に依存しない。
        DeleteMenu(hMenu, SC_CLOSE, MF_BYCOMMAND);

        // フォントサイズサブメニュー (保存なし; 子の WM_SYSCOMMAND で即時反映)
        var hFontMenu = CreatePopupMenu();
        if (hFontMenu != IntPtr.Zero)
        {
            AppendMenu(hFontMenu, MF_STRING, (IntPtr)SC_AMM_FONT_XL, "極大");
            AppendMenu(hFontMenu, MF_STRING, (IntPtr)SC_AMM_FONT_L,  "大");
            AppendMenu(hFontMenu, MF_STRING, (IntPtr)SC_AMM_FONT_M,  "中");
            AppendMenu(hFontMenu, MF_STRING, (IntPtr)SC_AMM_FONT_S,  "小");
            AppendMenu(hFontMenu, MF_STRING, (IntPtr)SC_AMM_FONT_XS, "極小");
        }

        // 独自項目をひとつの「AMM ▶」サブメニューに束ねる。
        // CreatePopupMenu の hMenu はそのまま AppendMenu(MF_POPUP, ...) に
        // 渡せばシステムメニューが破棄される時に一緒に解放される。
        var hAmmMenu = CreatePopupMenu();
        if (hAmmMenu != IntPtr.Zero)
        {
            AppendMenu(hAmmMenu, MF_STRING, (IntPtr)SC_AMM_RENAME, renameText);
            AppendMenu(hAmmMenu, MF_SEPARATOR, IntPtr.Zero, null);
            AppendMenu(hAmmMenu, MF_STRING, (IntPtr)SC_AMM_EDITOR_LINK, editorLinkText);
            AppendMenu(hAmmMenu, MF_STRING, (IntPtr)SC_AMM_COPY_EDITOR_PATH, copyPathText);
            AppendMenu(hAmmMenu, MF_SEPARATOR, IntPtr.Zero, null);
            if (hFontMenu != IntPtr.Zero)
            {
                AppendMenu(hAmmMenu, MF_POPUP, hFontMenu, "フォントサイズ");
            }
            AppendMenu(hAmmMenu, MF_STRING, (IntPtr)SC_AMM_SETTINGS, settingsText);
            AppendMenu(hMenu, MF_POPUP, hAmmMenu, "AMM");
        }

        AppendMenu(hMenu, MF_SEPARATOR, IntPtr.Zero, null);
        AppendMenu(hMenu, MF_STRING, (IntPtr)SC_CLOSE, "閉じる");
    }

    // ---- DWM caption color (Windows 11 22000+) ----
    // 子ウィンドウ (MDI 子も含む) の caption 背景色を変更する。古い OS では
    // E_INVALIDARG 等のエラーで返るが、エラーは握って no-op にする。caption が
    // 表示される非最大化時のみ視覚効果がある。

    private const int DWMWA_CAPTION_COLOR = 35;
    private const int DWMWA_TEXT_COLOR = 36;
    private const uint DWMWA_COLOR_DEFAULT = 0xFFFFFFFF;

    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref uint attrValue, int attrSize);

    /// <summary>
    /// COLORREF (0x00BBGGRR) 形式に変換した値で caption 背景色を設定する。
    /// color が null のときは OS 既定に戻す。
    /// </summary>
    public static void SetCaptionColor(IntPtr hWnd, System.Drawing.Color? color)
    {
        if (hWnd == IntPtr.Zero) return;
        try
        {
            uint value = color.HasValue
                ? (uint)(color.Value.R | (color.Value.G << 8) | (color.Value.B << 16))
                : DWMWA_COLOR_DEFAULT;
            DwmSetWindowAttribute(hWnd, DWMWA_CAPTION_COLOR, ref value, sizeof(uint));
        }
        catch { /* dwmapi 未対応 OS では握り潰し */ }
    }

    /// <summary>
    /// caption 上のテキスト色を設定する。背景を黄色に変えた時の可読性確保用。
    /// </summary>
    public static void SetCaptionTextColor(IntPtr hWnd, System.Drawing.Color? color)
    {
        if (hWnd == IntPtr.Zero) return;
        try
        {
            uint value = color.HasValue
                ? (uint)(color.Value.R | (color.Value.G << 8) | (color.Value.B << 16))
                : DWMWA_COLOR_DEFAULT;
            DwmSetWindowAttribute(hWnd, DWMWA_TEXT_COLOR, ref value, sizeof(uint));
        }
        catch { }
    }
}
