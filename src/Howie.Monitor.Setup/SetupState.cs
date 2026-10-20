using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using Howie.Monitor.Contracts;

namespace Howie.Monitor.Setup;

public sealed record LegacyAgent(int ProcessId, DateTimeOffset StartedAt, string Executable, string ConfigurationFile);
public sealed record InstallPlan(AgentConfiguration Configuration, string? RequestId, string OwnerSid, LegacyAgent? Legacy);
public sealed record InstallMarker(string Product, string Version, string AgentPath, string OwnerSid, string? RequestId);

public static class SetupPaths
{
    public const string Product = "HowieMonitor-93746211-9989-4cef-9d88-07870b3f35dd";
    public const string ServiceName = "HowieMonitorAgent";
    public const string ServerUrl = "https://monitor.howied.me/";
    public static string ProgramRoot => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "HowieMonitor");
    public static string DataRoot => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "HowieMonitor", "Agent");
    public static string ConfigurationFile => Path.Combine(DataRoot, "agent.json");
    public static string MarkerFile => Path.Combine(ProgramRoot, "install.json");
    public static string InstalledSetup => Path.Combine(ProgramRoot, "HowieMonitorSetup.exe");
    public static string WorkRoot => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HowieMonitor", "Setup");
    public static string PlanFile => Path.Combine(WorkRoot, "plan.json");

    public static void VerifyPath(string path, string root)
    {
        string full = Path.GetFullPath(path), boundary = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        if (!full.Equals(boundary, StringComparison.OrdinalIgnoreCase) && !full.StartsWith(boundary + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("操作路径超出本产品目录。");
        for (DirectoryInfo? folder = new(Path.GetDirectoryName(full)!); folder is not null; folder = folder.Parent)
            if (folder.Exists && (folder.Attributes & FileAttributes.ReparsePoint) != 0) throw new InvalidOperationException("安装路径包含目录链接，无法安全操作。");
        if (File.Exists(full) && (File.GetAttributes(full) & FileAttributes.ReparsePoint) != 0) throw new InvalidOperationException("目标文件为链接，无法安全操作。");
    }

    public static void PrivateDirectory(string directory, string ownerSid, bool writableByOwner = true)
    {
        var owner = new SecurityIdentifier(ownerSid);
        if (!owner.IsAccountSid()) throw new InvalidOperationException("安装管理者身份无效。");
        VerifyPath(Path.Combine(directory, ".check"), directory);
        Directory.CreateDirectory(directory);
        var info = new DirectoryInfo(directory);
        var acl = info.GetAccessControl(AccessControlSections.Access | AccessControlSections.Owner);
        acl.SetAccessRuleProtection(true, false);
        foreach (FileSystemAccessRule rule in acl.GetAccessRules(true, false, typeof(SecurityIdentifier))) acl.RemoveAccessRuleSpecific(rule);
        var administrators = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
        if (!writableByOwner) acl.SetOwner(administrators);
        acl.AddAccessRule(new(owner, writableByOwner ? FileSystemRights.FullControl : FileSystemRights.ReadAndExecute,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        foreach (var principal in new[] { new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), administrators })
            acl.AddAccessRule(new(principal, FileSystemRights.FullControl, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        info.SetAccessControl(acl);
    }
    public static T? Read<T>(string path) => File.Exists(path) ? JsonSerializer.Deserialize<T>(File.ReadAllText(path), ProtocolJson.Options) : default;
    public static void Write<T>(string path, T value)
    {
        string temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None)) JsonSerializer.Serialize(stream, value, ProtocolJson.Options);
        File.Move(temp, path, true);
    }
    public static string CurrentSid => WindowsIdentity.GetCurrent().User!.Value;
    public static bool IsAdministrator => new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);
    public static void ValidateConfiguration(AgentConfiguration configuration)
    {
        if (configuration.ServerUrl != ServerUrl || configuration.LocalDevelopment || string.IsNullOrWhiteSpace(configuration.DeviceId)
            || configuration.DeviceId.Length > 200 || configuration.Token is null || configuration.Token.Length is < 16 or > 512 || configuration.DataDirectory != Path.Combine(DataRoot, "queue"))
            throw new InvalidOperationException("安装配置与正式服务入口或本产品目录不一致。");
        if (configuration.Terminal is { Enabled: true } access && !TerminalProtocol.ValidUser(access.UserSid))
            throw new InvalidOperationException("管理员终端授权账号无效。");
    }
}
