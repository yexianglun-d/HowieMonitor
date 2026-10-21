using System.Text.Json;

namespace Howie.Monitor.Contracts;

// 路径由本机安装约定生成，协议不能指定执行文件、参数或落盘路径。
public static class AgentUpdateFiles
{
    public static string DataRoot => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "HowieMonitor", "Agent");
    public static string Root => Path.Combine(DataRoot, "updates");
    public static string Journal => Path.Combine(Root, "operation.json");
    public static string DirectoryFor(string id) => Guid.TryParseExact(id, "N", out _) ? Path.Combine(Root, id)
        : throw new InvalidOperationException("升级任务身份无效。");
    public static void Verify(string path, string root)
    {
        string full = Path.GetFullPath(path), boundary = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        if (!full.Equals(boundary, StringComparison.OrdinalIgnoreCase) && !full.StartsWith(boundary + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("升级路径超出本产品目录。");
        for (DirectoryInfo? folder = new(Path.GetDirectoryName(full)!); folder is not null; folder = folder.Parent)
            if (folder.Exists && (folder.Attributes & FileAttributes.ReparsePoint) != 0) throw new InvalidOperationException("升级路径包含目录链接。");
        if (File.Exists(full) && (File.GetAttributes(full) & FileAttributes.ReparsePoint) != 0) throw new InvalidOperationException("升级文件不能为链接。");
    }
    public static T? Read<T>(string path)
    {
        Verify(path, Root);
        if (!File.Exists(path)) return default;
        if (new FileInfo(path).Length > 65536) throw new InvalidOperationException("升级状态文件过大。");
        return JsonSerializer.Deserialize<T>(File.ReadAllText(path), ProtocolJson.Options);
    }
    public static void Write<T>(string path, T value)
    {
        Verify(path, Root);
        string temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        { JsonSerializer.Serialize(stream, value, ProtocolJson.Options); stream.Flush(true); }
        File.Move(temp, path, true);
    }
}
public sealed record AgentUpdateJournal(AgentUpdateTicket Ticket, string Phase, string? Message = null,
    int? HelperPid = null, long? HelperStartedAt = null);
