using System.IO.Pipes;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;
using Howie.Monitor.Contracts;

namespace Howie.Monitor.Agent;

public sealed class WindowsAdminTerminal : IAsyncDisposable
{
    private readonly NamedPipeClientStream _input;
    private readonly NamedPipeServerStream _output;
    private SafeFileHandle? _job;
    private SafeProcessHandle? _process;
    private Process? _managedProcess;
    private IntPtr _console;
    private int _disposed;
    public AdminTerminalIdentity Identity { get; }

    private WindowsAdminTerminal(AdminTerminalIdentity identity, NamedPipeClientStream input, NamedPipeServerStream output)
    { Identity = identity; _input = input; _output = output; }

    public static async Task<WindowsAdminTerminal> Start(string userSid, int columns, int rows)
    {
        if (!TerminalProtocol.ValidSize(columns, rows)) throw new TerminalAccessException("终端尺寸不正确。");
        var identity = AdminTerminalIdentity.Acquire(userSid);
        NamedPipeClientStream? input = null;
        NamedPipeServerStream? output = null;
        WindowsAdminTerminal? terminal = null;
        try
        {
            // ConPTY 侧使用同步句柄，主机侧使用可取消的异步管道，断线时仍能排空最后一帧。
            string inputName = "howie-terminal-" + Guid.NewGuid().ToString("N"), outputName = "howie-terminal-" + Guid.NewGuid().ToString("N");
            using var inputReader = new NamedPipeServerStream(inputName, PipeDirection.In, 1,
                PipeTransmissionMode.Byte, PipeOptions.CurrentUserOnly, 65536, 65536);
            input = Connect(inputName, inputReader, PipeDirection.Out, PipeOptions.Asynchronous);
            output = Pipe(outputName, PipeDirection.In);
            using var outputClient = Connect(outputName, output, PipeDirection.Out);
            terminal = new(identity, input, output);
            int result = Native.CreatePseudoConsole(new((short)columns, (short)rows), inputReader.SafePipeHandle, outputClient.SafePipeHandle, 0, out terminal._console);
            if (result < 0) Marshal.ThrowExceptionForHR(result);
            terminal.StartProcess();
            return terminal;
        }
        catch
        {
            if (terminal is not null) await terminal.DisposeAsync();
            else { input?.Dispose(); output?.Dispose(); identity.Dispose(); }
            throw;
        }
    }
    private static NamedPipeServerStream Pipe(string name, PipeDirection direction) => new(name, direction, 1,
        PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly, 65536, 65536);
    private static NamedPipeClientStream Connect(string name, NamedPipeServerStream server, PipeDirection direction, PipeOptions options = PipeOptions.None)
    {
        var connection = server.WaitForConnectionAsync();
        var client = new NamedPipeClientStream(".", name, direction, options);
        try { client.Connect(5000); connection.GetAwaiter().GetResult(); return client; }
        catch { client.Dispose(); throw; }
    }

    private void StartProcess()
    {
        _job = Native.CreateJobObject(IntPtr.Zero, null);
        if (_job.IsInvalid) throw Error("无法创建终端进程组");
        var limits = new JobLimits { Basic = new() { Flags = 0x2000 } };
        if (!Native.SetInformationJobObject(_job, 9, ref limits, (uint)Marshal.SizeOf<JobLimits>())) throw Error("无法设置终端进程组");
        nuint bytes = 0;
        Native.InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref bytes);
        if (bytes == 0) throw Error("无法初始化终端启动参数");
        IntPtr attributes = Marshal.AllocHGlobal((nint)bytes), environment = IntPtr.Zero, desktop = Marshal.StringToHGlobalUni("winsta0\\default");
        bool initialized = false;
        try
        {
            if (!Native.InitializeProcThreadAttributeList(attributes, 1, 0, ref bytes)) throw Error("无法初始化终端启动参数");
            initialized = true;
            if (!Native.UpdateProcThreadAttribute(attributes, 0, (nuint)0x00020016, _console, (nuint)IntPtr.Size, IntPtr.Zero, IntPtr.Zero)) throw Error("无法绑定终端会话");
            if (!Native.CreateEnvironmentBlock(out environment, Identity.Token, false)) throw Error("无法读取管理员用户环境");
            uint length = 0; Native.GetUserProfileDirectory(Identity.Token, null, ref length);
            if (length == 0) throw Error("无法读取管理员用户目录");
            var profile = new StringBuilder((int)length);
            if (!Native.GetUserProfileDirectory(Identity.Token, profile, ref length)) throw Error("无法读取管理员用户目录");
            string shell = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe");
            if (!File.Exists(shell)) throw new TerminalAccessException("系统 PowerShell 不可用。");
            var startup = new StartupInfoEx { Info = new() { Size = (uint)Marshal.SizeOf<StartupInfoEx>(), Desktop = desktop }, Attributes = attributes };
            // 先以挂起状态创建，验证真实身份并纳入进程组后才允许运行任何命令。
            if (!Native.CreateProcessAsUser(Identity.Token, shell, new StringBuilder("\"" + shell + "\" -NoLogo -NoProfile"),
                IntPtr.Zero, IntPtr.Zero, false, 0x00080000 | 0x00000400 | 0x00000004, environment, profile.ToString(), ref startup, out var process))
                throw Error("无法启动管理员 PowerShell");
            _process = new(process.Process, true);
            using var thread = new SafeFileHandle(process.Thread, true);
            try
            {
                if (!Native.AssignProcessToJobObject(_job, _process)) throw Error("无法绑定终端进程组");
                AdminTerminalIdentity.VerifyProcess(_process.DangerousGetHandle(), Identity);
                using (var current = AdminTerminalIdentity.Acquire(Identity.UserSid))
                    if (current.LogonId != Identity.LogonId) throw new TerminalAccessException("管理员登录会话已变更。");
                _managedProcess = Process.GetProcessById((int)process.ProcessId);
                _ = _managedProcess.Handle;
                if (Native.ResumeThread(thread) == uint.MaxValue) throw Error("无法启动终端线程");
            }
            catch { Native.TerminateProcess(_process, 1); throw; }
        }
        finally
        {
            if (environment != IntPtr.Zero) Native.DestroyEnvironmentBlock(environment);
            if (initialized) Native.DeleteProcThreadAttributeList(attributes);
            Marshal.FreeHGlobal(attributes); Marshal.FreeHGlobal(desktop);
        }
    }
    public ValueTask<int> Read(Memory<byte> buffer, CancellationToken token) => _output.ReadAsync(buffer, token);
    public ValueTask Write(string value, CancellationToken token) => _input.WriteAsync(Encoding.UTF8.GetBytes(value), token);
    public Task WaitForExit(CancellationToken token) => _managedProcess!.WaitForExitAsync(token);
    public int ExitCode => _managedProcess!.ExitCode;
    public void Resize(int columns, int rows)
    {
        int result = Native.ResizePseudoConsole(_console, new((short)columns, (short)rows));
        if (result < 0) Marshal.ThrowExceptionForHR(result);
    }
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _job?.Dispose();
        try
        {
            if (_console != IntPtr.Zero)
            {
                async Task Drain()
                {
                    var buffer = new byte[8192];
                    try { while (await _output.ReadAsync(buffer) > 0) { } }
                    catch (IOException) { }
                }
                var drain = Drain();
                await Task.Run(() => Native.ClosePseudoConsole(_console));
                _console = IntPtr.Zero;
                await drain;
            }
        }
        finally { _input.Dispose(); _output.Dispose(); _managedProcess?.Dispose(); _process?.Dispose(); Identity.Dispose(); }
    }
    private static TerminalAccessException Error(string operation) => new(operation + $"（错误码 {Marshal.GetLastWin32Error()}）。");

    [StructLayout(LayoutKind.Sequential)] private readonly struct Coord(short x, short y) { public readonly short X = x, Y = y; }
    [StructLayout(LayoutKind.Sequential)] private struct StartupInfo
    {
        public uint Size; public IntPtr Reserved, Desktop, Title;
        public uint X, Y, Width, Height, Columns, Rows, Fill, Flags;
        public ushort Show, ReservedLength;
        public IntPtr ReservedBytes, Input, Output, Error;
    }
    [StructLayout(LayoutKind.Sequential)] private struct StartupInfoEx { public StartupInfo Info; public IntPtr Attributes; }
    [StructLayout(LayoutKind.Sequential)] private struct ProcessInfo { public IntPtr Process, Thread; public uint ProcessId, ThreadId; }
    [StructLayout(LayoutKind.Sequential)] private struct BasicLimits
    {
        public long ProcessTime, JobTime; public uint Flags;
        public nuint MinimumWorkingSet, MaximumWorkingSet; public uint ActiveProcesses;
        public nuint Affinity; public uint Priority, Scheduling;
    }
    [StructLayout(LayoutKind.Sequential)] private struct IoCounters { public ulong Reads, Writes, Other, ReadBytes, WriteBytes, OtherBytes; }
    [StructLayout(LayoutKind.Sequential)] private struct JobLimits { public BasicLimits Basic; public IoCounters Io; public nuint ProcessMemory, JobMemory, PeakProcessMemory, PeakJobMemory; }
    private static class Native
    {
        [DllImport("kernel32.dll")] internal static extern int CreatePseudoConsole(Coord size, SafePipeHandle input, SafePipeHandle output, uint flags, out IntPtr console);
        [DllImport("kernel32.dll")] internal static extern int ResizePseudoConsole(IntPtr console, Coord size);
        [DllImport("kernel32.dll")] internal static extern void ClosePseudoConsole(IntPtr console);
        [DllImport("kernel32.dll", EntryPoint = "CreateJobObjectW", CharSet = CharSet.Unicode, SetLastError = true)] internal static extern SafeFileHandle CreateJobObject(IntPtr attributes, string? name);
        [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool SetInformationJobObject(SafeFileHandle job, int kind, ref JobLimits information, uint size);
        [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool AssignProcessToJobObject(SafeFileHandle job, SafeProcessHandle process);
        [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool InitializeProcThreadAttributeList(IntPtr attributes, int count, uint flags, ref nuint size);
        [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool UpdateProcThreadAttribute(IntPtr attributes, uint flags, nuint attribute, IntPtr value, nuint size, IntPtr previous, IntPtr required);
        [DllImport("kernel32.dll")] internal static extern void DeleteProcThreadAttributeList(IntPtr attributes);
        [DllImport("userenv.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool CreateEnvironmentBlock(out IntPtr environment, SafeAccessTokenHandle token, [MarshalAs(UnmanagedType.Bool)] bool inherit);
        [DllImport("userenv.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool DestroyEnvironmentBlock(IntPtr environment);
        [DllImport("userenv.dll", EntryPoint = "GetUserProfileDirectoryW", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool GetUserProfileDirectory(SafeAccessTokenHandle token, StringBuilder? directory, ref uint size);
        [DllImport("advapi32.dll", EntryPoint = "CreateProcessAsUserW", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool CreateProcessAsUser(SafeAccessTokenHandle token, string application, StringBuilder command, IntPtr processAttributes, IntPtr threadAttributes, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint flags, IntPtr environment, string directory, ref StartupInfoEx startup, out ProcessInfo process);
        [DllImport("kernel32.dll", SetLastError = true)] internal static extern uint ResumeThread(SafeFileHandle thread);
        [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool TerminateProcess(SafeProcessHandle process, uint exitCode);
    }
}
