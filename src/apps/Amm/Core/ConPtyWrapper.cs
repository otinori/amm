using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Amm.Core;

public sealed class ConPtyWrapper : IDisposable
{
    private IntPtr _hPC;
    private IntPtr _hProcess;
    private IntPtr _hThread;
    private SafeFileHandle? _inputWritePipe;
    private SafeFileHandle? _outputReadPipe;
    private FileStream? _inputStream;
    private Thread? _readThread;
    private Thread? _processWatcherThread;
    private int _processExitedFired;
    private volatile bool _disposed;
    private readonly Encoding _encoding;
    private readonly object _writeLock = new();

    public event Action<string>? OutputReceived;
    public event Action? ProcessExited;

    public ConPtyWrapper(Encoding encoding)
    {
        _encoding = encoding;
    }

    public void Start(string commandLine, short cols, short rows, bool autoChcp, string? workingDirectory = null,
        IReadOnlyDictionary<string, string>? extraEnvironment = null)
    {
        // autoChcp=true の場合は cmd.exe で chcp 65001 を先に実行してから本来の
        // コマンドを起動する。post-launch に stdin へ "chcp 65001\r" を流す方式は
        // claude.exe など stdin を直接読む TUI アプリの入力欄に注入されてしまい
        // 不適切なため (cmd/PS では echo されるだけだが TUI では入力扱いになる)。
        // /d : AutoRun レジストリ無効、/s : 外側引用符をリテラル扱い、
        // /c : コマンド実行後に終了。`> nul` で "Active code page: 65001" 抑止。
        if (autoChcp)
        {
            commandLine = $"cmd.exe /d /s /c \"chcp 65001 > nul && {commandLine}\"";
        }

        // 1. Create pipe pairs
        var sa = new SECURITY_ATTRIBUTES
        {
            nLength = Marshal.SizeOf<SECURITY_ATTRIBUTES>(),
            bInheritHandle = true
        };

        if (!NativeMethods.CreatePipe(out var inputReadPipe, out var inputWritePipe, ref sa, 0))
            throw new InvalidOperationException($"CreatePipe (input) failed: {FormatWin32Error(Marshal.GetLastWin32Error())}");

        if (!NativeMethods.CreatePipe(out var outputReadPipe, out var outputWritePipe, ref sa, 0))
        {
            int err = Marshal.GetLastWin32Error();
            inputReadPipe.Dispose();
            inputWritePipe.Dispose();
            throw new InvalidOperationException($"CreatePipe (output) failed: {FormatWin32Error(err)}");
        }

        _inputWritePipe = inputWritePipe;
        _outputReadPipe = outputReadPipe;

        // 2. Create PseudoConsole
        var size = new COORD { X = cols, Y = rows };
        int hr = NativeMethods.CreatePseudoConsole(size, inputReadPipe, outputWritePipe, 0, out _hPC);
        if (hr != 0)
            throw new InvalidOperationException($"CreatePseudoConsole failed: HRESULT 0x{hr:X8}");

        // 3. Prepare attribute list
        IntPtr attrListSize = IntPtr.Zero;
        NativeMethods.InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref attrListSize);
        var attrList = Marshal.AllocHGlobal(attrListSize);
        if (!NativeMethods.InitializeProcThreadAttributeList(attrList, 1, 0, ref attrListSize))
        {
            int err = Marshal.GetLastWin32Error();
            Marshal.FreeHGlobal(attrList);
            throw new InvalidOperationException($"InitializeProcThreadAttributeList failed: {FormatWin32Error(err)}");
        }

        // 4. Set PSEUDOCONSOLE attribute
        if (!NativeMethods.UpdateProcThreadAttribute(
                attrList, 0,
                (IntPtr)NativeMethods.PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE,
                _hPC,
                (IntPtr)IntPtr.Size,
                IntPtr.Zero, IntPtr.Zero))
        {
            int err = Marshal.GetLastWin32Error();
            NativeMethods.DeleteProcThreadAttributeList(attrList);
            Marshal.FreeHGlobal(attrList);
            throw new InvalidOperationException($"UpdateProcThreadAttribute failed: {FormatWin32Error(err)}");
        }

        // 5. Create process
        var siEx = new STARTUPINFOEX();
        siEx.StartupInfo.cb = Marshal.SizeOf<STARTUPINFOEX>();
        siEx.lpAttributeList = attrList;

        // extraEnvironment: lpEnvironment = IntPtr.Zero (親 env 継承) を活かし、
        // CreateProcess の直前に自プロセスの環境変数へ一時セット → 直後に復元する。
        // 子プロセスは CreateProcess 時点の env ブロックをコピーするので、復元後も
        // 子には残る。Start は UI thread からのみ呼ばれる前提 (TryStartConPty /
        // autoStartCount ループとも UI thread) のため並行 Start での混入はない。
        var savedEnv = new Dictionary<string, string?>();
        if (extraEnvironment != null)
        {
            foreach (var (key, value) in extraEnvironment)
            {
                savedEnv[key] = Environment.GetEnvironmentVariable(key);
                Environment.SetEnvironmentVariable(key, value);
            }
        }

        bool created;
        PROCESS_INFORMATION pi;
        int createErr = 0;
        try
        {
            created = NativeMethods.CreateProcess(
                null, commandLine,
                IntPtr.Zero, IntPtr.Zero, false,
                NativeMethods.EXTENDED_STARTUPINFO_PRESENT,
                IntPtr.Zero, workingDirectory,
                ref siEx, out pi);
            // 重要: GetLastWin32Error() は CreateProcess 直後に読む。
            // 後続の SetEnvironmentVariable / DeleteProcThreadAttributeList が
            // ERROR_SUCCESS (0) で上書きしてしまい、エラーコードが消える。
            if (!created) createErr = Marshal.GetLastWin32Error();
        }
        finally
        {
            foreach (var (key, original) in savedEnv)
                Environment.SetEnvironmentVariable(key, original);
        }

        if (!created)
        {
            NativeMethods.DeleteProcThreadAttributeList(attrList);
            Marshal.FreeHGlobal(attrList);
            throw new InvalidOperationException(
                $"CreateProcess failed: {FormatWin32Error(createErr)} (commandLine: \"{commandLine}\")");
        }

        _hProcess = pi.hProcess;
        _hThread = pi.hThread;

        NativeMethods.DeleteProcThreadAttributeList(attrList);
        Marshal.FreeHGlobal(attrList);

        // 6. Close ConPTY-side pipe ends (no longer needed by this process)
        inputReadPipe.Dispose();
        outputWritePipe.Dispose();

        // 6.5 Create persistent input stream
        _inputStream = new FileStream(_inputWritePipe, FileAccess.Write, bufferSize: 256, isAsync: false);

        // 7. Start read thread
        _readThread = new Thread(ReadLoop) { IsBackground = true, Name = "ConPTY-Reader" };
        _readThread.Start();

        // 7.5 Process watcher: ConPTY の出力パイプは CreatePseudoConsole が立てた
        // conhost 側が握ったままなので、cmd.exe 等の子プロセスが exit しても
        // ReadLoop は EOF を受け取らない。子プロセスの HANDLE を
        // WaitForSingleObject で見張る専用スレッドを用意する。
        _processWatcherThread = new Thread(ProcessWatcher)
        {
            IsBackground = true,
            Name = "ConPTY-ProcessWatcher",
        };
        _processWatcherThread.Start();

        // chcp は Start 冒頭で commandLine ラップ済み (post-launch stdin 注入は
        // TUI アプリの入力欄に文字が打ち込まれてしまうため廃止)。
    }

    public void Write(string text)
    {
        if (_disposed || _inputStream == null) return;

        lock (_writeLock)
        {
            try
            {
                var bytes = _encoding.GetBytes(text);
                _inputStream.Write(bytes, 0, bytes.Length);
                _inputStream.Flush();
            }
            catch (IOException) { }
            catch (ObjectDisposedException) { }
        }
    }

    private static string FormatWin32Error(int err) => err switch
    {
        0 => "0 (エラーコード取得失敗)",
        2 => "2 (ERROR_FILE_NOT_FOUND — 実行ファイルが見つかりません。PATH を確認するか executable にフルパスを指定してください)",
        3 => "3 (ERROR_PATH_NOT_FOUND — 指定されたパスが見つかりません。workingDirectory を確認してください)",
        5 => "5 (ERROR_ACCESS_DENIED)",
        87 => "87 (ERROR_INVALID_PARAMETER)",
        193 => "193 (ERROR_BAD_EXE_FORMAT — 無効な実行ファイル)",
        267 => "267 (ERROR_DIRECTORY — workingDirectory が無効)",
        _ => $"{err} ({new System.ComponentModel.Win32Exception(err).Message})"
    };

    public void Resize(short cols, short rows)
    {
        if (_disposed || _hPC == IntPtr.Zero) return;
        NativeMethods.ResizePseudoConsole(_hPC, new COORD { X = cols, Y = rows });
    }

    private void ReadLoop()
    {
        var buf = new byte[4096];
        // フィールドをローカルへスナップショット。Start 直後に別スレッドが Dispose を
        // 呼ぶと _outputReadPipe が null 化され得るため、null 逆参照 (読みスレッドの
        // 未処理 NRE = プロセスクラッシュ) を避ける。Dispose によるハンドル破棄 (使用中)
        // は下の IOException/ObjectDisposedException catch で吸収される。
        var pipe = _outputReadPipe;
        if (pipe == null) return;
        try
        {
            using var stream = new FileStream(pipe, FileAccess.Read, bufferSize: 4096, isAsync: false);
            while (!_disposed)
            {
                int n = stream.Read(buf, 0, buf.Length);
                if (n <= 0) break;
                var text = _encoding.GetString(buf, 0, n);
                OutputReceived?.Invoke(text);
            }
        }
        catch (IOException) { /* pipe closed */ }
        catch (ObjectDisposedException) { /* pipe disposed */ }

        // パイプ EOF 経路でも ProcessExited を発火 (conhost 側を ClosePseudoConsole
        // した場合など)。二重発火は FireProcessExitedOnce で防ぐ。
        if (!_disposed) FireProcessExitedOnce();
    }

    private void ProcessWatcher()
    {
        if (_hProcess == IntPtr.Zero) return;
        try
        {
            // INFINITE (0xFFFFFFFF) で子プロセスの終了を待つ
            NativeMethods.WaitForSingleObject(_hProcess, 0xFFFFFFFF);
        }
        catch { }
        if (!_disposed) FireProcessExitedOnce();
    }

    private void FireProcessExitedOnce()
    {
        if (System.Threading.Interlocked.CompareExchange(ref _processExitedFired, 1, 0) == 0)
            ProcessExited?.Invoke();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        // 1. Close input stream and pipe (signals EOF to child)
        _inputStream?.Dispose();
        _inputStream = null;
        _inputWritePipe?.Dispose();
        _inputWritePipe = null;

        // 2. Wait for process (3 second timeout)
        if (_hProcess != IntPtr.Zero)
        {
            if (NativeMethods.WaitForSingleObject(_hProcess, 3000) != NativeMethods.WAIT_OBJECT_0)
                NativeMethods.TerminateProcess(_hProcess, 1);
        }

        // 3. Close PseudoConsole
        if (_hPC != IntPtr.Zero)
        {
            NativeMethods.ClosePseudoConsole(_hPC);
            _hPC = IntPtr.Zero;
        }

        // 4. Close output pipe (causes read thread to exit)
        _outputReadPipe?.Dispose();
        _outputReadPipe = null;

        // 5. Wait for read thread
        _readThread?.Join(2000);

        // 5.5 Process watcher も合流。_hProcess に対する WaitForSingleObject は
        // 子プロセスが既に死んでいる場合すぐ返るので通常即合流する。
        _processWatcherThread?.Join(2000);

        // 6. Close process/thread handles
        if (_hProcess != IntPtr.Zero)
        {
            NativeMethods.CloseHandle(_hProcess);
            _hProcess = IntPtr.Zero;
        }
        if (_hThread != IntPtr.Zero)
        {
            NativeMethods.CloseHandle(_hThread);
            _hThread = IntPtr.Zero;
        }
    }
}
