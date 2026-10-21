using System.Runtime.InteropServices;
using System.Text;

namespace Howie.Monitor.Setup;

internal static class SetupConsole
{
    internal static void Attach()
    {
        int[] streams = [-10, -11, -12];
        var inherited = streams.Select(GetStdHandle).ToArray();
        bool attached = AttachConsole(uint.MaxValue);
        int error = Marshal.GetLastWin32Error();
        if (!attached && error != 5 && !(Valid(inherited[0]) && Valid(inherited[1])))
        {
            if (!AllocConsole()) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            attached = true;
        }
        // GUI 子系统需要接入父控制台；保留调用者提供的管道，支持退出码和输出捕获。
        for (int i = 0; i < streams.Length; i++)
            if (Valid(inherited[i]) && !SetStdHandle(streams[i], inherited[i]))
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        if (attached || error == 5) { SetConsoleCP(65001); SetConsoleOutputCP(65001); }
        var encoding = new UTF8Encoding(false);
        Console.SetIn(new StreamReader(Console.OpenStandardInput(), encoding));
        Console.SetOut(new StreamWriter(Console.OpenStandardOutput(), encoding) { AutoFlush = true });
        Console.SetError(new StreamWriter(Console.OpenStandardError(), encoding) { AutoFlush = true });
    }
    private static bool Valid(IntPtr handle) => handle != IntPtr.Zero && handle != new IntPtr(-1);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool AttachConsole(uint processId);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool AllocConsole();
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr GetStdHandle(int kind);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetStdHandle(int kind, IntPtr handle);
    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetConsoleCP(uint codePage);
    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetConsoleOutputCP(uint codePage);
}
