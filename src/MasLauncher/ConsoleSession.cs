using System.Runtime.InteropServices;
using System.Text;

namespace MasLauncher;

/// <summary>
/// Hosts cmd.exe inside a Windows pseudoconsole (ConPTY). This is what makes the batch
/// script interactive inside our own window: the child still sees a real console (so
/// `choice`, `pause` and `timeout` work) while its output arrives through a pipe.
/// Requires Windows 10 1809 or newer.
/// </summary>
internal sealed class ConsoleSession : IDisposable
{
    private const uint ExtendedStartupInfoPresent = 0x00080000;
    private const int ProcThreadAttributePseudoConsole = 0x00020016;
    private const int Infinite = -1;

    private IntPtr _pseudoConsole;
    private IntPtr _inputPipe;      // we write into the pty
    private IntPtr _outputPipe;     // we read the pty's screen stream
    private IntPtr _process;
    private Thread? _reader;
    private Thread? _waiter;
    private volatile bool _disposed;

    /// <summary>Raw pty output. Delivered on a background thread; chunks are UTF-8 decoded.</summary>
    public event Action<string>? Output;

    /// <summary>Raised once the child process has exited.</summary>
    public event Action<int>? Exited;

    public bool IsRunning { get; private set; }

    /// <summary>Details of the last failed interop call, for diagnostics.</summary>
    public string LastError { get; private set; } = "";

    private int _attributeListSize;

    /// <summary>Why the pty output reader stopped, if it did.</summary>
    public string ReadError { get; private set; } = "";

    public string Diagnostics =>
        $"STARTUPINFO={Marshal.SizeOf<StartupInfo>()} STARTUPINFOEX={Marshal.SizeOf<StartupInfoEx>()} attrList={_attributeListSize} reader={(_reader?.IsAlive == true ? "alive" : "stopped")} readError={(ReadError.Length == 0 ? "none" : ReadError)}";

    public bool Start(string commandLine, string workingDirectory, short columns = 118, short rows = 38)
    {
        if (!CreatePipe(out var inputRead, out _inputPipe, IntPtr.Zero, 0))
        {
            LastError = "CreatePipe(in): " + Marshal.GetLastWin32Error();
            return false;
        }

        if (!CreatePipe(out _outputPipe, out var outputWrite, IntPtr.Zero, 0))
        {
            LastError = "CreatePipe(out): " + Marshal.GetLastWin32Error();
            return false;
        }

        var size = new Coord { X = columns, Y = rows };
        // The pty owns these ends now; close them once the child is running, not before.
        var hr = CreatePseudoConsole(size, inputRead, outputWrite, 0, out _pseudoConsole);
        if (hr != 0)
        {
            LastError = $"CreatePseudoConsole: HRESULT 0x{hr:X8}";
            CloseHandle(inputRead);
            CloseHandle(outputWrite);
            return false;
        }

        var attributeList = IntPtr.Zero;
        try
        {
            var listSize = IntPtr.Zero;
            InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref listSize);
            _attributeListSize = (int)listSize;
            attributeList = Marshal.AllocHGlobal(listSize);
            if (!InitializeProcThreadAttributeList(attributeList, 1, 0, ref listSize))
            {
                LastError = "InitializeProcThreadAttributeList: " + Marshal.GetLastWin32Error();
                return false;
            }

            if (!UpdateProcThreadAttribute(attributeList, 0, (IntPtr)ProcThreadAttributePseudoConsole,
                    _pseudoConsole, (IntPtr)IntPtr.Size, IntPtr.Zero, IntPtr.Zero))
            {
                LastError = "UpdateProcThreadAttribute: " + Marshal.GetLastWin32Error();
                return false;
            }

            var startup = new StartupInfoEx();
            startup.StartupInfo.cb = Marshal.SizeOf<StartupInfoEx>();
            // INVALID_HANDLE_VALUE + STARTF_USESTDHANDLES tells conhost to create fresh
            // std handles attached to the pseudoconsole instead of reusing ours.
            startup.StartupInfo.dwFlags = 0x100; // STARTF_USESTDHANDLES
            startup.StartupInfo.hStdInput = new IntPtr(-1);
            startup.StartupInfo.hStdOutput = new IntPtr(-1);
            startup.StartupInfo.hStdError = new IntPtr(-1);
            startup.lpAttributeList = attributeList;

            var command = new StringBuilder(commandLine);
            if (!CreateProcess(null, command, IntPtr.Zero, IntPtr.Zero, false,
                    ExtendedStartupInfoPresent, IntPtr.Zero, workingDirectory, ref startup, out var info))
            {
                LastError = "CreateProcess: " + Marshal.GetLastWin32Error();
                return false;
            }

            CloseHandle(info.hThread);
            _process = info.hProcess;
        }
        finally
        {
            CloseHandle(inputRead);
            CloseHandle(outputWrite);

            if (attributeList != IntPtr.Zero)
            {
                DeleteProcThreadAttributeList(attributeList);
                Marshal.FreeHGlobal(attributeList);
            }
        }

        IsRunning = true;
        _reader = new Thread(ReadLoop) { IsBackground = true, Name = "conpty-reader" };
        _reader.Start();
        _waiter = new Thread(WaitLoop) { IsBackground = true, Name = "conpty-waiter" };
        _waiter.Start();
        return true;
    }

    /// <summary>Feeds text as if it was typed. Use "\r" for Enter.</summary>
    public void Write(string text)
    {
        if (_disposed || _inputPipe == IntPtr.Zero || string.IsNullOrEmpty(text)) return;
        var bytes = Encoding.UTF8.GetBytes(text);
        try { WriteFile(_inputPipe, bytes, (uint)bytes.Length, out _, IntPtr.Zero); }
        catch { /* pipe gone */ }
    }

    public void Resize(short columns, short rows)
    {
        if (_disposed || _pseudoConsole == IntPtr.Zero) return;
        var size = new Coord { X = columns, Y = rows };
        try { ResizePseudoConsole(_pseudoConsole, size); } catch { }
    }

    private void ReadLoop()
    {
        var buffer = new byte[8192];
        var decoder = Encoding.UTF8.GetDecoder();
        var chars = new char[8192];

        while (true)
        {
            int read;
            if (!ReadFile(_outputPipe, buffer, (uint)buffer.Length, out read, IntPtr.Zero))
            {
                ReadError = "ReadFile failed: " + Marshal.GetLastWin32Error();
                break;
            }
            if (read == 0) { ReadError = "ReadFile returned 0 (pty closed)"; break; }

            var count = decoder.GetChars(buffer, 0, read, chars, 0);
            if (count > 0) Output?.Invoke(new string(chars, 0, count));
        }
    }

    private void WaitLoop()
    {
        WaitForSingleObject(_process, Infinite);
        GetExitCodeProcess(_process, out var exitCode);
        IsRunning = false;
        if (!_disposed) Exited?.Invoke(unchecked((int)exitCode));
    }

    public void Kill()
    {
        if (_disposed || _process == IntPtr.Zero) return;
        try { TerminateProcess(_process, 1); } catch { }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        // Stop the child first so its console host stops producing output.
        try { if (_process != IntPtr.Zero && IsRunning) TerminateProcess(_process, 1); } catch { }
        try { if (_process != IntPtr.Zero) WaitForSingleObject(_process, 1000); } catch { }

        try { if (_inputPipe != IntPtr.Zero) CloseHandle(_inputPipe); } catch { }
        _inputPipe = IntPtr.Zero;

        // Closing the pty flushes whatever conhost still has buffered; the reader then
        // sees end-of-file and returns, so no output is lost on shutdown.
        try { if (_pseudoConsole != IntPtr.Zero) ClosePseudoConsole(_pseudoConsole); } catch { }
        _pseudoConsole = IntPtr.Zero;

        try { _reader?.Join(2000); } catch { }

        try { if (_outputPipe != IntPtr.Zero) CloseHandle(_outputPipe); } catch { }
        _outputPipe = IntPtr.Zero;

        try { if (_process != IntPtr.Zero) CloseHandle(_process); } catch { }
        _process = IntPtr.Zero;
        IsRunning = false;
    }

    // ------------------------------------------------------------------ interop

    [StructLayout(LayoutKind.Sequential)]
    private struct Coord
    {
        public short X;
        public short Y;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StartupInfo
    {
        public int cb;
        public string? lpReserved;
        public string? lpDesktop;
        public string? lpTitle;
        public int dwX;
        public int dwY;
        public int dwXSize;
        public int dwYSize;
        public int dwXCountChars;
        public int dwYCountChars;
        public int dwFillAttribute;
        public int dwFlags;
        public short wShowWindow;
        public short cbReserved2;
        public IntPtr lpReserved2;
        public IntPtr hStdInput;
        public IntPtr hStdOutput;
        public IntPtr hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct StartupInfoEx
    {
        public StartupInfo StartupInfo;
        public IntPtr lpAttributeList;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation
    {
        public IntPtr hProcess;
        public IntPtr hThread;
        public int dwProcessId;
        public int dwThreadId;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CreatePipe(out IntPtr readPipe, out IntPtr writePipe, IntPtr attributes, uint size);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern int CreatePseudoConsole(Coord size, IntPtr input, IntPtr output, uint flags, out IntPtr pseudoConsole);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern int ResizePseudoConsole(IntPtr pseudoConsole, Coord size);

    [DllImport("kernel32.dll")]
    private static extern void ClosePseudoConsole(IntPtr pseudoConsole);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool InitializeProcThreadAttributeList(IntPtr list, int count, int flags, ref IntPtr size);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool UpdateProcThreadAttribute(IntPtr list, uint flags, IntPtr attribute, IntPtr value, IntPtr size, IntPtr previous, IntPtr returned);

    [DllImport("kernel32.dll")]
    private static extern void DeleteProcThreadAttributeList(IntPtr list);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CreateProcess(string? application, StringBuilder commandLine, IntPtr processAttributes,
        IntPtr threadAttributes, bool inheritHandles, uint flags, IntPtr environment, string? workingDirectory,
        ref StartupInfoEx startup, out ProcessInformation information);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool ReadFile(IntPtr file, byte[] buffer, uint toRead, out int read, IntPtr overlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool WriteFile(IntPtr file, byte[] buffer, uint toWrite, out int written, IntPtr overlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(IntPtr handle, int milliseconds);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetExitCodeProcess(IntPtr process, out uint exitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool TerminateProcess(IntPtr process, uint exitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);
}
