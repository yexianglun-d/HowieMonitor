using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Howie.Monitor.Contracts;
using Microsoft.Win32.SafeHandles;

namespace Howie.Monitor.Agent;

public sealed class TerminalAccessException(string message) : Exception(message);

public sealed class AdminTerminalIdentity : IDisposable
{
    public SafeAccessTokenHandle Token { get; }
    public string Account { get; }
    public string UserSid { get; }
    public string LogonId { get; }
    public uint SessionId { get; }
    private AdminTerminalIdentity(SafeAccessTokenHandle token, uint sessionId)
    {
        Token = token; SessionId = sessionId;
        using var identity = new WindowsIdentity(token.DangerousGetHandle());
        if (identity.IsSystem || !TerminalProtocol.ValidUser(identity.User?.Value) || !Elevated(token)
            || !new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))
            throw new TerminalAccessException("当前账号没有可用的管理员令牌。");
        UserSid = identity.User!.Value; Account = identity.Name;
        var statistics = Information<TokenStatistics>(token, 10);
        LogonId = $"{sessionId}:{statistics.AuthenticationId.High:x8}{statistics.AuthenticationId.Low:x8}";
    }

    public static AdminTerminalIdentity Acquire(string userSid)
    {
        if (!TerminalProtocol.ValidUser(userSid)) throw new TerminalAccessException("尚未在本机授权管理员终端。");
        using var current = WindowsIdentity.GetCurrent();
        if (!current.IsSystem)
        {
            if (current.User?.Value != userSid || Process.GetCurrentProcess().SessionId == 0)
                throw new TerminalAccessException("终端服务需要访问已登录的授权账号。");
            return FromToken(current.AccessToken, (uint)Process.GetCurrentProcess().SessionId);
        }
        if (!Native.WTSEnumerateSessions(IntPtr.Zero, 0, 1, out var buffer, out int count)) throw Error("无法读取登录会话");
        try
        {
            int size = Marshal.SizeOf<WtsSession>();
            for (int i = 0; i < count; i++)
            {
                var session = Marshal.PtrToStructure<WtsSession>(buffer + i * size);
                if (session.State != 0 || session.Id == 0 || !Native.WTSQueryUserToken(session.Id, out var token)) continue;
                using (token)
                {
                    using var identity = new WindowsIdentity(token.DangerousGetHandle());
                    if (identity.User?.Value == userSid) return FromToken(token, session.Id);
                }
            }
        }
        finally { Native.WTSFreeMemory(buffer); }
        throw new TerminalAccessException("授权的管理员账号尚未登录或会话已断开。");
    }

    private static AdminTerminalIdentity FromToken(SafeAccessTokenHandle token, uint sessionId)
    {
        SafeAccessTokenHandle? linked = null;
        try
        {
            if (!Elevated(token))
            {
                IntPtr handle = Information<IntPtr>(token, 19);
                linked = new SafeAccessTokenHandle(handle);
                token = linked;
            }
            if (!Native.DuplicateTokenEx(token, 0x02000000, IntPtr.Zero, 2, 1, out var duplicate)) throw Error("无法创建管理员令牌");
            try { return new(duplicate, sessionId); }
            catch { duplicate.Dispose(); throw; }
        }
        catch (Win32Exception) { throw new TerminalAccessException("当前账号没有可用的提升权限令牌。"); }
        finally { linked?.Dispose(); }
    }
    public static bool Elevated(SafeAccessTokenHandle token) => Information<int>(token, 20) != 0;
    public static void VerifyProcess(IntPtr process, AdminTerminalIdentity expected)
    {
        if (!Native.OpenProcessToken(process, 8, out var token)) throw Error("无法验证终端执行身份");
        using (token)
        using (var actual = new WindowsIdentity(token.DangerousGetHandle()))
        {
            var statistics = Information<TokenStatistics>(token, 10);
            string logon = $"{Information<uint>(token, 12)}:{statistics.AuthenticationId.High:x8}{statistics.AuthenticationId.Low:x8}";
            if (actual.IsSystem || actual.User?.Value != expected.UserSid || !Elevated(token)
                || !new WindowsPrincipal(actual).IsInRole(WindowsBuiltInRole.Administrator) || logon != expected.LogonId)
                throw new TerminalAccessException("终端进程的管理员身份校验失败。");
        }
    }
    private static T Information<T>(SafeAccessTokenHandle token, int kind) where T : struct
    {
        int size = Marshal.SizeOf<T>(); IntPtr buffer = Marshal.AllocHGlobal(size);
        try
        {
            if (!Native.GetTokenInformation(token, kind, buffer, size, out _)) throw new Win32Exception(Marshal.GetLastWin32Error());
            return Marshal.PtrToStructure<T>(buffer);
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }
    private static TerminalAccessException Error(string operation) => new(operation + $"（错误码 {Marshal.GetLastWin32Error()}）。");
    public void Dispose() => Token.Dispose();

    [StructLayout(LayoutKind.Sequential)] private struct WtsSession { public uint Id; public IntPtr Name; public int State; }
    [StructLayout(LayoutKind.Sequential)] private struct Luid { public uint Low; public int High; }
    [StructLayout(LayoutKind.Sequential)] private struct TokenStatistics
    {
        public Luid TokenId, AuthenticationId;
        public long ExpirationTime;
        public int TokenType, ImpersonationLevel;
        public uint DynamicCharged, DynamicAvailable, GroupCount, PrivilegeCount;
        public Luid ModifiedId;
    }
    private static class Native
    {
        [DllImport("wtsapi32.dll", EntryPoint = "WTSEnumerateSessionsW", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool WTSEnumerateSessions(IntPtr server, int reserved, int version, out IntPtr sessions, out int count);
        [DllImport("wtsapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool WTSQueryUserToken(uint sessionId, out SafeAccessTokenHandle token);
        [DllImport("wtsapi32.dll")] internal static extern void WTSFreeMemory(IntPtr memory);
        [DllImport("advapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool DuplicateTokenEx(SafeAccessTokenHandle token, uint access, IntPtr attributes, int impersonation, int type, out SafeAccessTokenHandle duplicate);
        [DllImport("advapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool GetTokenInformation(SafeAccessTokenHandle token, int kind, IntPtr information, int length, out int required);
        [DllImport("advapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool OpenProcessToken(IntPtr process, uint access, out SafeAccessTokenHandle token);
    }
}
