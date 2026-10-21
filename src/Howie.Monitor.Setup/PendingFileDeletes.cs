using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

namespace Howie.Monitor.Setup;

internal static class PendingFileDeletes
{
    private const string SessionManager = @"SYSTEM\CurrentControlSet\Control\Session Manager";
    private const string ValueName = "PendingFileRenameOperations";

    // 删除项的目标名为空，不能用会丢失空字符串的 REG_MULTI_SZ 高层读取方式。
    internal static byte[] WithoutOwnedDeletes(byte[] raw, string root)
    {
        if (raw.Length % 2 != 0 || raw.Length > 1024 * 1024) throw new InvalidDataException("系统延迟清理记录格式无效。");
        var text = new UnicodeEncoding(false, false, true).GetString(raw);
        using var kept = new MemoryStream();
        int position = 0;
        while (position < text.Length && text[position] != '\0')
        {
            int start = position, sourceEnd = text.IndexOf('\0', position);
            if (sourceEnd < 0) throw new InvalidDataException("系统延迟清理记录不完整。");
            string source = text[position..sourceEnd]; position = sourceEnd + 1;
            int targetEnd = text.IndexOf('\0', position);
            if (targetEnd < 0) throw new InvalidDataException("系统延迟清理记录不完整。");
            string target = text[position..targetEnd]; position = targetEnd + 1;
            bool owned = Within(source, root);
            if (target.Length != 0 && (owned || Within(target.TrimStart('!'), root)))
                throw new InvalidOperationException("系统仍有涉及安装目录的移动任务，请重启 Windows 后重试。");
            if (!owned || target.Length != 0) kept.Write(raw, start * 2, (position - start) * 2);
        }
        if (text[position..].Any(value => value != '\0')) throw new InvalidDataException("系统延迟清理记录含无效尾部。");
        kept.Write(raw, position * 2, raw.Length - position * 2);
        if (kept.Length > 0 && (kept.Length < 4 || kept.GetBuffer()[kept.Length - 4] != 0 || kept.GetBuffer()[kept.Length - 3] != 0))
            kept.Write([0, 0]);
        return kept.ToArray();
    }

    private static bool Within(string value, string root)
    {
        if (value.StartsWith(@"\??\", StringComparison.Ordinal) || value.StartsWith(@"\\?\", StringComparison.Ordinal)) value = value[4..];
        if (!Path.IsPathFullyQualified(value)) return false;
        string boundary = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)), full = Path.GetFullPath(value);
        return full.Equals(boundary, StringComparison.OrdinalIgnoreCase) || full.StartsWith(boundary + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    internal static void RemoveOwnedDeletes(params string[] roots) => RemoveOwnedDeletes(Registry.LocalMachine.Handle, SessionManager, ValueName, roots);

    internal static void RemoveOwnedDeletes(SafeRegistryHandle hive, string keyName, string valueName, params string[] roots)
    {
        using var transaction = Native.CreateTransaction(IntPtr.Zero, IntPtr.Zero, 0, 0, 0, 10000, "Howie Monitor uninstall recovery");
        if (transaction.IsInvalid) throw Failure();
        Check(Native.RegOpenKeyTransacted(hive, keyName, 0, 0x0003, out var key, transaction, IntPtr.Zero));
        using (key)
        {
            uint size = 0;
            int result = Native.RegQueryValueEx(key, valueName, IntPtr.Zero, out uint kind, null, ref size);
            if (result == 2) return;
            Check(result);
            if (kind != 7 || size > 1024 * 1024) throw new InvalidDataException("系统延迟清理记录类型无效。");
            var raw = new byte[size]; Check(Native.RegQueryValueEx(key, valueName, IntPtr.Zero, out kind, raw, ref size));
            if (size != raw.Length || kind != 7) throw new InvalidDataException("系统延迟清理记录已变化，请重试。");
            byte[] next = raw;
            foreach (string root in roots) next = WithoutOwnedDeletes(next, root);
            if (raw.AsSpan().SequenceEqual(next)) return;
            if (next.All(value => value == 0)) Check(Native.RegDeleteValue(key, valueName));
            else Check(Native.RegSetValueEx(key, valueName, 0, 7, next, (uint)next.Length));
            // 并发的非事务写会使本事务失败，不覆盖 Windows 或其他安装程序的新任务。
            if (!Native.CommitTransaction(transaction)) throw Failure();
        }
    }

    private static void Check(int result)
    {
        if (result != 0) throw new InvalidOperationException("无法安全更新本产品的延迟清理记录，请关闭其他安装程序后重试；仍失败时重启 Windows。", new Win32Exception(result));
    }
    private static InvalidOperationException Failure() => new("卸载恢复事务未完成，请关闭其他安装程序后重试；仍失败时重启 Windows。", new Win32Exception(Marshal.GetLastWin32Error()));

    private static class Native
    {
        [DllImport("ktmw32.dll", EntryPoint = "CreateTransaction", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
        internal static extern SafeFileHandle CreateTransaction(IntPtr attributes, IntPtr unitOfWork, uint options, uint isolation, uint flags, uint timeout, string description);
        [DllImport("ktmw32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool CommitTransaction(SafeFileHandle transaction);
        [DllImport("advapi32.dll", EntryPoint = "RegOpenKeyTransactedW", CharSet = CharSet.Unicode)]
        internal static extern int RegOpenKeyTransacted(SafeRegistryHandle hive, string subKey, uint options, uint access, out SafeRegistryHandle key, SafeFileHandle transaction, IntPtr extended);
        [DllImport("advapi32.dll", EntryPoint = "RegQueryValueExW", CharSet = CharSet.Unicode)]
        internal static extern int RegQueryValueEx(SafeRegistryHandle key, string value, IntPtr reserved, out uint type, [Out] byte[]? data, ref uint size);
        [DllImport("advapi32.dll", EntryPoint = "RegSetValueExW", CharSet = CharSet.Unicode)]
        internal static extern int RegSetValueEx(SafeRegistryHandle key, string value, uint reserved, uint type, byte[] data, uint size);
        [DllImport("advapi32.dll", EntryPoint = "RegDeleteValueW", CharSet = CharSet.Unicode)]
        internal static extern int RegDeleteValue(SafeRegistryHandle key, string value);
    }
}
