using System.Runtime.InteropServices;
using System.Text;

namespace Quotra.WinUI.Interop;

/// <summary>A short-lived ConPTY process used only for the read-only /usage screen.</summary>
internal sealed class ClaudePseudoConsole : IDisposable
{
    private const int MaximumOutputBytes = 128 * 1024;
    private const short TerminalWidth = 120;
    private const short TerminalHeight = 40;
    private const uint CreateSuspended = 0x00000004;
    private const uint CreateUnicodeEnvironment = 0x00000400;
    private const uint ExtendedStartupInfoPresent = 0x00080000;
    private const nuint ProcThreadAttributePseudoConsole = 0x00020016;
    private const uint ErrorBrokenPipe = 109;
    private const uint StillActive = 259;
    private const uint WaitObject0 = 0x00000000;

    private IntPtr _pseudoConsole;
    private IntPtr _inputWrite;
    private IntPtr _outputRead;
    private IntPtr _process;
    private IntPtr _thread;
    private ClaudeProcessJob? _job;
    private readonly Decoder _decoder = Encoding.UTF8.GetDecoder();
    private readonly TerminalBuffer _terminal = new(TerminalWidth, TerminalHeight);
    private readonly byte[] _byteBuffer = new byte[4096];
    private readonly char[] _charBuffer = new char[4096];
    private int _totalOutputBytes;
    private bool _disposed;

    private ClaudePseudoConsole() { }

    internal static ClaudePseudoConsole Start(
        string executablePath,
        IReadOnlyList<string> arguments,
        string workingDirectory,
        IReadOnlyDictionary<string, string> environment)
    {
        if (!OperatingSystem.IsWindows()) throw new InvalidOperationException("ConPTY is available only on Windows.");

        var session = new ClaudePseudoConsole();
        try
        {
            session.StartCore(executablePath, arguments, workingDirectory, environment);
            return session;
        }
        catch
        {
            session.Dispose();
            throw;
        }
    }

    internal string ReadAvailable()
    {
        ThrowIfDisposed();
        while (PeekNamedPipe(_outputRead, IntPtr.Zero, 0, IntPtr.Zero, out uint available, IntPtr.Zero))
        {
            if (available == 0) break;
            uint count = Math.Min(available, (uint)_byteBuffer.Length);
            if (_totalOutputBytes + count > MaximumOutputBytes)
                throw new InvalidOperationException("Claude CLI output exceeded the capture limit.");
            if (!ReadFile(_outputRead, _byteBuffer, count, out uint read, IntPtr.Zero))
            {
                uint error = unchecked((uint)Marshal.GetLastWin32Error());
                if (error == ErrorBrokenPipe) break;
                throw new InvalidOperationException("Claude CLI output could not be read.");
            }
            if (read == 0) break;
            _totalOutputBytes += checked((int)read);
            int chars = _decoder.GetChars(_byteBuffer, 0, checked((int)read), _charBuffer, 0, flush: false);
            _terminal.Write(_charBuffer.AsSpan(0, chars));
        }

        return _terminal.ToString();
    }

    internal void WriteInput(string text)
    {
        ThrowIfDisposed();
        byte[] bytes = Encoding.UTF8.GetBytes(text);
        int offset = 0;
        while (offset < bytes.Length)
        {
            int requested = bytes.Length - offset;
            byte[] remaining = offset == 0 ? bytes : bytes.AsSpan(offset).ToArray();
            if (!WriteFile(_inputWrite, remaining, checked((uint)requested), out uint written, IntPtr.Zero) || written == 0)
                throw new InvalidOperationException("Claude CLI input could not be sent.");
            offset += checked((int)written);
        }
    }

    internal bool HasExited
    {
        get
        {
            ThrowIfDisposed();
            if (!GetExitCodeProcess(_process, out uint exitCode))
                throw new InvalidOperationException("Claude CLI process state could not be read.");
            return exitCode != StillActive;
        }
    }

    internal int CursorRow
    {
        get
        {
            ThrowIfDisposed();
            return _terminal.CursorRow;
        }
    }

    private void StartCore(string executablePath, IReadOnlyList<string> arguments, string workingDirectory,
        IReadOnlyDictionary<string, string> environment)
    {
        if (!CreatePipe(out IntPtr inputRead, out _inputWrite, IntPtr.Zero, 0))
            throw new InvalidOperationException("Claude CLI input channel could not be created.");
        if (!CreatePipe(out _outputRead, out IntPtr outputWrite, IntPtr.Zero, 0))
        {
            CloseHandle(inputRead);
            throw new InvalidOperationException("Claude CLI output channel could not be created.");
        }

        try
        {
            int pseudoConsoleResult = CreatePseudoConsole(new Coord(TerminalWidth, TerminalHeight), inputRead, outputWrite, 0, out _pseudoConsole);
            if (pseudoConsoleResult < 0) throw new InvalidOperationException("Claude CLI terminal could not be created.");
        }
        finally
        {
            CloseHandle(inputRead);
            CloseHandle(outputWrite);
        }

        IntPtr attributeList = IntPtr.Zero;
        IntPtr environmentBlock = IntPtr.Zero;
        try
        {
            IntPtr attributeBytes = IntPtr.Zero;
            _ = InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref attributeBytes);
            if (attributeBytes == IntPtr.Zero) throw new InvalidOperationException("Claude CLI process attributes could not be initialized.");
            attributeList = Marshal.AllocHGlobal(attributeBytes);
            if (!InitializeProcThreadAttributeList(attributeList, 1, 0, ref attributeBytes))
                throw new InvalidOperationException("Claude CLI process attributes could not be initialized.");

            if (!UpdateProcThreadAttribute(attributeList, 0, (IntPtr)ProcThreadAttributePseudoConsole,
                    _pseudoConsole, (IntPtr)IntPtr.Size, IntPtr.Zero, IntPtr.Zero))
                throw new InvalidOperationException("Claude CLI terminal could not be attached.");

            environmentBlock = CreateEnvironmentBlock(environment);
            var startup = new StartupInfoEx
            {
                // Explicit null std handles prevent a redirected parent log from being inherited.
                // https://github.com/microsoft/terminal/discussions/15814
                StartupInfo = new StartupInfo { Cb = checked((uint)Marshal.SizeOf<StartupInfoEx>()), Flags = 0x00000100 },
                AttributeList = attributeList
            };
            string commandLine = BuildCommandLine(executablePath, arguments);
            var mutableCommandLine = new StringBuilder(commandLine);
            // ConPTY owns the invisible console. CREATE_NO_WINDOW would detach console I/O.
            uint flags = CreateSuspended | CreateUnicodeEnvironment | ExtendedStartupInfoPresent;
            if (!CreateProcessW(executablePath, mutableCommandLine, IntPtr.Zero, IntPtr.Zero, false, flags,
                    environmentBlock, workingDirectory, ref startup, out ProcessInformation info))
                throw new InvalidOperationException("Claude CLI process could not be started.");

            _process = info.Process;
            _thread = info.Thread;
            try
            {
                _job = ClaudeProcessJob.CreateAndAssign(_process);
                if (ResumeThread(_thread) == uint.MaxValue)
                    throw new InvalidOperationException("Claude CLI process could not be resumed.");
            }
            catch
            {
                _ = TerminateProcess(_process, 1);
                throw;
            }
        }
        finally
        {
            if (environmentBlock != IntPtr.Zero) Marshal.FreeHGlobal(environmentBlock);
            if (attributeList != IntPtr.Zero)
            {
                DeleteProcThreadAttributeList(attributeList);
                Marshal.FreeHGlobal(attributeList);
            }
        }
    }

    private static IntPtr CreateEnvironmentBlock(IReadOnlyDictionary<string, string> environment)
    {
        string block = string.Join('\0', environment.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
            .Select(pair => pair.Key + "=" + pair.Value)) + "\0\0";
        return Marshal.StringToHGlobalUni(block);
    }

    internal static string BuildCommandLine(string executablePath, IEnumerable<string> arguments) =>
        string.Join(" ", new[] { executablePath }.Concat(arguments).Select(QuoteArgument));

    private static string QuoteArgument(string value)
    {
        var result = new StringBuilder(value.Length + 2).Append('"');
        int slashes = 0;
        foreach (char character in value)
        {
            if (character == '\\') { slashes++; continue; }
            if (character == '"')
            {
                result.Append('\\', slashes * 2 + 1).Append('"');
                slashes = 0;
                continue;
            }
            result.Append('\\', slashes).Append(character);
            slashes = 0;
        }
        result.Append('\\', slashes * 2).Append('"');
        return result.ToString();
    }

    private void ThrowIfDisposed()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(ClaudePseudoConsole));
    }

    internal uint ProcessId
    {
        get
        {
            ThrowIfDisposed();
            uint id = GetProcessId(_process);
            if (id == 0) throw new InvalidOperationException("Claude CLI process state could not be read.");
            return id;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _job?.Terminate();
        if (_process != IntPtr.Zero && WaitForSingleObject(_process, 1000) != WaitObject0)
        {
            _ = TerminateProcess(_process, 1);
            if (WaitForSingleObject(_process, 1000) != WaitObject0 && _job is not null)
            {
                _job.Dispose();
                _job = null;
                _ = WaitForSingleObject(_process, 1000);
            }
        }
        // Close the endpoints after the client stops so a full output pipe cannot stall shutdown.
        CloseIfValid(ref _inputWrite);
        CloseIfValid(ref _outputRead);
        if (_pseudoConsole != IntPtr.Zero)
        {
            ClosePseudoConsole(_pseudoConsole);
            _pseudoConsole = IntPtr.Zero;
        }
        CloseIfValid(ref _thread);
        CloseIfValid(ref _process);
        _job?.Dispose();
        _job = null;
    }

    private static void CloseIfValid(ref IntPtr handle)
    {
        if (handle != IntPtr.Zero && handle != new IntPtr(-1)) CloseHandle(handle);
        handle = IntPtr.Zero;
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct Coord(short x, short y)
    {
        public readonly short X = x;
        public readonly short Y = y;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StartupInfo
    {
        public uint Cb;
        public IntPtr Reserved;
        public IntPtr Desktop;
        public IntPtr Title;
        public uint X;
        public uint Y;
        public uint XSize;
        public uint YSize;
        public uint XCountChars;
        public uint YCountChars;
        public uint FillAttribute;
        public uint Flags;
        public ushort ShowWindow;
        public ushort Reserved2Size;
        public IntPtr Reserved2;
        public IntPtr StdInput;
        public IntPtr StdOutput;
        public IntPtr StdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct StartupInfoEx
    {
        public StartupInfo StartupInfo;
        public IntPtr AttributeList;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation
    {
        public IntPtr Process;
        public IntPtr Thread;
        public uint ProcessId;
        public uint ThreadId;
    }

    private sealed class TerminalBuffer(int width, int height)
    {
        private readonly char[,] _cells = new char[height, width];
        private int _row;
        private int _column;
        private bool _escape;
        private int _escapeMode;
        private readonly StringBuilder _sequence = new();

        internal int CursorRow => _row;

        internal void Write(ReadOnlySpan<char> text)
        {
            foreach (char ch in text)
            {
                if (_escape)
                {
                    ConsumeEscape(ch);
                    continue;
                }
                if (ch == '\x1b') { _escape = true; _escapeMode = 0; _sequence.Clear(); continue; }
                if (ch == '\r') { _column = 0; continue; }
                if (ch == '\n') { NewLine(); continue; }
                if (ch == '\b') { _column = Math.Max(0, _column - 1); continue; }
                if (ch == '\t') { _column = Math.Min(width - 1, (_column + 8) & ~7); continue; }
                if (ch < ' ' || ch == '\x7f') continue;
                Put(ch);
            }
        }

        private void ConsumeEscape(char ch)
        {
            if (_escapeMode == 0)
            {
                if (ch == '[') { _escapeMode = 1; return; }
                if (ch == ']') { _escapeMode = 2; return; }
                _escape = false;
                if (ch == 'c') Clear();
                return;
            }
            if (_escapeMode == 2)
            {
                if (ch == '\a') _escape = false;
                else if (ch == '\x1b') _escapeMode = 3;
                return;
            }
            if (_escapeMode == 3)
            {
                _escape = false;
                _escapeMode = 0;
                return;
            }
            if ((ch >= '@' && ch <= '~'))
            {
                ApplyCsi(ch);
                _escape = false;
                _escapeMode = 0;
                _sequence.Clear();
            }
            else if (_sequence.Length < 64) _sequence.Append(ch);
        }

        private void ApplyCsi(char final)
        {
            string raw = _sequence.ToString();
            bool privateMode = raw.StartsWith("?", StringComparison.Ordinal);
            raw = raw.TrimStart('?');
            int[] args = raw.Split(';', StringSplitOptions.None)
                .Select(value => int.TryParse(value, out int number) ? number : 0).ToArray();
            int first = args.Length > 0 && args[0] != 0 ? args[0] : 1;
            switch (final)
            {
                case 'A': _row = Math.Max(0, _row - first); break;
                case 'B': _row = Math.Min(height - 1, _row + first); break;
                case 'C': _column = Math.Min(width - 1, _column + first); break;
                case 'D': _column = Math.Max(0, _column - first); break;
                case 'H':
                case 'f':
                    _row = Math.Clamp((args.ElementAtOrDefault(0) == 0 ? 1 : args[0]) - 1, 0, height - 1);
                    _column = Math.Clamp((args.ElementAtOrDefault(1) == 0 ? 1 : args[1]) - 1, 0, width - 1);
                    break;
                case 'J':
                    if (args.ElementAtOrDefault(0) is 2 or 3) Clear();
                    else if (args.ElementAtOrDefault(0) == 0) ClearBelow();
                    break;
                case 'K':
                    int mode = args.ElementAtOrDefault(0);
                    int start = mode is 1 or 2 ? 0 : _column;
                    int end = mode == 0 ? width : mode == 1 ? _column + 1 : width;
                    for (int col = start; col < end && col < width; col++) _cells[_row, col] = '\0';
                    break;
                case 'h' when privateMode:
                    if (raw.Contains("1049", StringComparison.Ordinal)) Clear();
                    break;
            }
        }

        private void Put(char ch)
        {
            if (_column >= width) NewLine();
            _cells[_row, _column++] = ch;
        }

        private void NewLine()
        {
            _column = 0;
            if (_row < height - 1) { _row++; return; }
            for (int row = 1; row < height; row++)
                for (int col = 0; col < width; col++) _cells[row - 1, col] = _cells[row, col];
            for (int col = 0; col < width; col++) _cells[height - 1, col] = '\0';
        }

        private void Clear()
        {
            Array.Clear(_cells);
            _row = 0;
            _column = 0;
        }

        private void ClearBelow()
        {
            for (int row = _row; row < height; row++)
                for (int col = row == _row ? _column : 0; col < width; col++) _cells[row, col] = '\0';
        }

        public override string ToString()
        {
            var result = new StringBuilder(height * (width + 1));
            for (int row = 0; row < height; row++)
            {
                int last = width - 1;
                while (last >= 0 && _cells[row, last] == '\0') last--;
                for (int col = 0; col <= last; col++) result.Append(_cells[row, col] == '\0' ? ' ' : _cells[row, col]);
                if (row != height - 1) result.Append('\n');
            }
            return result.ToString();
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CreatePipe(out IntPtr readPipe, out IntPtr writePipe, IntPtr securityAttributes, uint size);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern int CreatePseudoConsole(Coord size, IntPtr input, IntPtr output, uint flags, out IntPtr pseudoConsole);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern void ClosePseudoConsole(IntPtr pseudoConsole);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool InitializeProcThreadAttributeList(IntPtr attributeList, int count, int flags, ref IntPtr size);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool UpdateProcThreadAttribute(IntPtr attributeList, uint flags, IntPtr attribute, IntPtr value,
        IntPtr size, IntPtr previousValue, IntPtr returnSize);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern void DeleteProcThreadAttributeList(IntPtr attributeList);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CreateProcessW")]
    private static extern bool CreateProcessW(string applicationName, StringBuilder commandLine, IntPtr processAttributes,
        IntPtr threadAttributes, bool inheritHandles, uint creationFlags, IntPtr environment, string currentDirectory,
        ref StartupInfoEx startupInfo, out ProcessInformation processInformation);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint ResumeThread(IntPtr thread);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetExitCodeProcess(IntPtr process, out uint exitCode);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint GetProcessId(IntPtr process);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool PeekNamedPipe(IntPtr pipe, IntPtr buffer, uint bufferSize, IntPtr bytesRead, out uint bytesAvailable, IntPtr bytesLeftThisMessage);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool ReadFile(IntPtr file, byte[] buffer, uint bytesToRead, out uint bytesRead, IntPtr overlapped);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool WriteFile(IntPtr file, byte[] buffer, uint bytesToWrite, out uint bytesWritten, IntPtr overlapped);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool TerminateProcess(IntPtr process, uint exitCode);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);
}

/// <summary>Job object with kill-on-close so timeout/cancellation cannot leave CLI descendants behind.</summary>
internal sealed class ClaudeProcessJob : IDisposable
{
    private const uint KillOnJobClose = 0x00002000;
    private const int JobObjectExtendedLimitInformation = 9;
    private IntPtr _handle;

    private ClaudeProcessJob(IntPtr handle) => _handle = handle;

    internal static ClaudeProcessJob CreateAndAssign(IntPtr process)
    {
        IntPtr handle = CreateJobObjectW(IntPtr.Zero, null);
        if (handle == IntPtr.Zero) throw new InvalidOperationException("Claude CLI cleanup could not be prepared.");
        var job = new ClaudeProcessJob(handle);
        var limits = new ExtendedLimitInformation
        {
            BasicLimitInformation = new BasicLimitInformation { LimitFlags = KillOnJobClose }
        };
        if (!SetInformationJobObject(handle, JobObjectExtendedLimitInformation, ref limits,
                checked((uint)Marshal.SizeOf<ExtendedLimitInformation>())) || !AssignProcessToJobObject(handle, process))
        {
            job.Dispose();
            throw new InvalidOperationException("Claude CLI cleanup could not be attached.");
        }
        return job;
    }

    internal void Terminate()
    {
        if (_handle != IntPtr.Zero) _ = TerminateJobObject(_handle, 0xC000013A);
    }

    public void Dispose()
    {
        if (_handle == IntPtr.Zero) return;
        CloseHandle(_handle);
        _handle = IntPtr.Zero;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BasicLimitInformation
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize;
        public UIntPtr MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ExtendedLimitInformation
    {
        public BasicLimitInformation BasicLimitInformation;
        public IoCounters IoInfo;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateJobObjectW(IntPtr jobAttributes, string? name);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetInformationJobObject(IntPtr job, int informationClass, ref ExtendedLimitInformation information, uint informationLength);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool TerminateJobObject(IntPtr job, uint exitCode);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);
}
