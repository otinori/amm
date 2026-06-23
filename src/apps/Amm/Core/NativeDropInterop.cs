using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;
using IDataObject = System.Runtime.InteropServices.ComTypes.IDataObject;

namespace Amm.Core;

/// <summary>
/// Win32 IDropTarget 経由でネイティブ OLE ドロップを処理するためのインフラ。
/// WebView2 は Chromium セキュリティで web コンテンツに file:// 絶対パスを渡さない
/// ため、MDI (=WebView2) への drop でパス送信機能を実現するには HWND レベルで
/// OS からの drop を横取りするしかない。
/// </summary>
internal static class NativeDropInterop
{
    [DllImport("ole32.dll")]
    public static extern int RegisterDragDrop(IntPtr hwnd,
        [MarshalAs(UnmanagedType.Interface)] IDropTarget pDropTarget);

    [DllImport("ole32.dll")]
    public static extern int RevokeDragDrop(IntPtr hwnd);

    [DllImport("ole32.dll")]
    public static extern void ReleaseStgMedium(ref STGMEDIUM pmedium);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    public static extern uint DragQueryFile(IntPtr hDrop, uint iFile,
        [Out] StringBuilder? lpszFile, uint cch);

    public delegate bool EnumChildProc(IntPtr hwnd, IntPtr lParam);
    [DllImport("user32.dll")]
    public static extern bool EnumChildWindows(IntPtr parent, EnumChildProc proc, IntPtr param);

    public const short CF_HDROP = 15;
    public const int DROPEFFECT_NONE = 0;
    public const int DROPEFFECT_COPY = 1;

    // DRAGDROP_E_ALREADYREGISTERED: 既に別の drop target が登録されているときの戻り値
    public const int DRAGDROP_E_ALREADYREGISTERED = unchecked((int)0x80040101);
}

[ComImport]
[Guid("00000122-0000-0000-C000-000000000046")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IDropTarget
{
    [PreserveSig]
    int DragEnter([In, MarshalAs(UnmanagedType.Interface)] IDataObject pDataObj,
        uint grfKeyState, POINTL pt, ref uint pdwEffect);

    [PreserveSig]
    int DragOver(uint grfKeyState, POINTL pt, ref uint pdwEffect);

    [PreserveSig]
    int DragLeave();

    [PreserveSig]
    int Drop([In, MarshalAs(UnmanagedType.Interface)] IDataObject pDataObj,
        uint grfKeyState, POINTL pt, ref uint pdwEffect);
}

[StructLayout(LayoutKind.Sequential)]
public struct POINTL { public int X; public int Y; }

/// <summary>
/// 指定 HWND に登録できる IDropTarget 実装。drop 成立時に CF_HDROP から絶対パス
/// 配列を取り出し、コールバックへ渡す。
/// </summary>
internal sealed class NativeDropTarget : IDropTarget
{
    private readonly Action<string[]> _onDrop;
    private bool _hasFiles;

    public NativeDropTarget(Action<string[]> onDrop) { _onDrop = onDrop; }

    public int DragEnter(IDataObject pDataObj, uint grfKeyState, POINTL pt, ref uint pdwEffect)
    {
        _hasFiles = HasFileDrop(pDataObj);
        pdwEffect = _hasFiles ? (uint)NativeDropInterop.DROPEFFECT_COPY
                              : (uint)NativeDropInterop.DROPEFFECT_NONE;
        return 0; // S_OK
    }

    public int DragOver(uint grfKeyState, POINTL pt, ref uint pdwEffect)
    {
        pdwEffect = _hasFiles ? (uint)NativeDropInterop.DROPEFFECT_COPY
                              : (uint)NativeDropInterop.DROPEFFECT_NONE;
        return 0;
    }

    public int DragLeave()
    {
        _hasFiles = false;
        return 0;
    }

    public int Drop(IDataObject pDataObj, uint grfKeyState, POINTL pt, ref uint pdwEffect)
    {
        var paths = ExtractPaths(pDataObj);
        pdwEffect = (uint)NativeDropInterop.DROPEFFECT_COPY;
        if (paths.Length > 0)
        {
            try { _onDrop(paths); }
            catch (Exception ex) { AppLogger.Error("native drop callback failed", ex); }
        }
        return 0;
    }

    private static bool HasFileDrop(IDataObject pDataObj)
    {
        var fmt = new FORMATETC
        {
            cfFormat = NativeDropInterop.CF_HDROP,
            dwAspect = DVASPECT.DVASPECT_CONTENT,
            lindex = -1,
            tymed = TYMED.TYMED_HGLOBAL,
            ptd = IntPtr.Zero,
        };
        try { return pDataObj.QueryGetData(ref fmt) == 0; }
        catch { return false; }
    }

    private static string[] ExtractPaths(IDataObject pDataObj)
    {
        var fmt = new FORMATETC
        {
            cfFormat = NativeDropInterop.CF_HDROP,
            dwAspect = DVASPECT.DVASPECT_CONTENT,
            lindex = -1,
            tymed = TYMED.TYMED_HGLOBAL,
            ptd = IntPtr.Zero,
        };
        STGMEDIUM medium = default;
        try
        {
            pDataObj.GetData(ref fmt, out medium);
            var hDrop = medium.unionmember;
            if (hDrop == IntPtr.Zero) return Array.Empty<string>();

            var count = NativeDropInterop.DragQueryFile(hDrop, 0xFFFFFFFF, null, 0);
            var result = new string[count];
            for (uint i = 0; i < count; i++)
            {
                var len = NativeDropInterop.DragQueryFile(hDrop, i, null, 0);
                var buf = new StringBuilder((int)len + 1);
                NativeDropInterop.DragQueryFile(hDrop, i, buf, (uint)buf.Capacity);
                result[i] = buf.ToString();
            }
            return result;
        }
        catch (Exception ex)
        {
            AppLogger.Error("ExtractPaths failed", ex);
            return Array.Empty<string>();
        }
        finally
        {
            try { NativeDropInterop.ReleaseStgMedium(ref medium); } catch { }
        }
    }
}
