using System.Diagnostics;
using System.IO.Compression;
using System.ServiceProcess;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using Howie.Monitor.Contracts;

namespace Howie.Monitor.Setup;

public static class InstallEngine
{
    private const string UninstallKey = "SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Uninstall\\HowieMonitor";
    private static string ServiceCommand(string agent) => $"\"{agent}\" --config \"{SetupPaths.ConfigurationFile}\"";
    public static InstallMarker? Existing()
    {
        SetupPaths.VerifyPath(SetupPaths.MarkerFile, SetupPaths.ProgramRoot);
        if (File.Exists(Path.Combine(SetupPaths.ProgramRoot, "uninstall-pending.json")))
            throw new InvalidOperationException("上一次卸载仍有使用中文件待清理，请关闭向导并重启后重新安装。");
        var marker = SetupPaths.Read<InstallMarker>(SetupPaths.MarkerFile);
        if (marker is not null && marker.Product != SetupPaths.Product) throw new InvalidOperationException("安装目录中的标记不属于本产品。");
        if (marker is not null) SetupPaths.VerifyPath(marker.AgentPath, SetupPaths.ProgramRoot);
        using var service = Registry.LocalMachine.OpenSubKey("SYSTEM\\CurrentControlSet\\Services\\" + SetupPaths.ServiceName);
        if (service is not null && (marker is null || !string.Equals(service.GetValue("ImagePath") as string, ServiceCommand(marker.AgentPath), StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("同名服务与本产品安装路径不一致，未修改该服务。");
        return marker;
    }
    public static async Task Apply(InstallPlan plan, string setupExecutable, CancellationToken cancellationToken)
    {
        if (!SetupPaths.IsAdministrator) throw new InvalidOperationException("安装需要管理员授权。");
        SetupPaths.ValidateConfiguration(plan.Configuration);
        if (plan.Configuration.Terminal is { Enabled: true } access && access.UserSid != plan.OwnerSid)
            throw new InvalidOperationException("管理员终端只能授权本次安装的账号。");
        var previous = Existing();
        string dataMarker = Path.Combine(SetupPaths.DataRoot, "product.json");
        SetupPaths.VerifyPath(dataMarker, SetupPaths.DataRoot);
        if (previous is null && Directory.Exists(SetupPaths.DataRoot) && Directory.EnumerateFileSystemEntries(SetupPaths.DataRoot).Any()
            && SetupPaths.Read<Dictionary<string, string>>(dataMarker)?.GetValueOrDefault("product") != SetupPaths.Product)
            throw new InvalidOperationException("数据目录已有未确认归属的内容，未覆盖或修改权限。");
        using var serviceKey = Registry.LocalMachine.OpenSubKey("SYSTEM\\CurrentControlSet\\Services\\" + SetupPaths.ServiceName);
        bool serviceExisted = serviceKey is not null;
        if (previous is null && Directory.Exists(SetupPaths.ProgramRoot) && Directory.EnumerateFileSystemEntries(SetupPaths.ProgramRoot).Any())
            throw new InvalidOperationException("安装目录已有其他内容，未覆盖。");
        string hash = PackageContents.PayloadHash;
        using var payload = PackageContents.OpenPayload();
        string versionRoot = Path.Combine(SetupPaths.ProgramRoot, "versions", "0.1.0-" + hash[..12]);
        SetupPaths.VerifyPath(Path.Combine(versionRoot, "Howie.Monitor.Agent.exe"), SetupPaths.ProgramRoot);
        SetupPaths.PrivateDirectory(Path.GetDirectoryName(SetupPaths.DataRoot)!, plan.OwnerSid, false);
        SetupPaths.PrivateDirectory(SetupPaths.DataRoot, plan.OwnerSid, false);
        SetupPaths.Write(dataMarker, new Dictionary<string, string> { ["product"] = SetupPaths.Product });
        Directory.CreateDirectory(SetupPaths.ProgramRoot);
        SetupPaths.VerifyPath(SetupPaths.ConfigurationFile, SetupPaths.DataRoot);
        byte[]? oldConfiguration = File.Exists(SetupPaths.ConfigurationFile) ? File.ReadAllBytes(SetupPaths.ConfigurationFile) : null;
        string? savedQueue = null, oldQueue = null;
        if (serviceExisted) await Stop(cancellationToken);
        try
        {
            Directory.CreateDirectory(versionRoot);
            using var archive = new ZipArchive(payload, ZipArchiveMode.Read);
            string? prefix = null;
            foreach (var entry in archive.Entries)
            {
                if (((entry.ExternalAttributes >> 16) & 0xF000) == 0xA000) throw new InvalidOperationException("安装载荷包含文件链接。");
                var parts = entry.FullName.Replace('\\', '/').Split('/');
                if (parts.Any(part => part == "..") || parts[0].Length == 0 || parts.Any(part => part.Contains(':'))) throw new InvalidOperationException("安装载荷路径无效。");
                prefix ??= parts[0]; if (parts[0] != prefix) throw new InvalidOperationException("安装载荷目录不一致。");
                string relative = string.Join(Path.DirectorySeparatorChar, parts.Skip(1));
                if (relative.Length == 0) continue;
                string target = Path.Combine(versionRoot, relative); SetupPaths.VerifyPath(target, versionRoot);
                if (entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\')) Directory.CreateDirectory(target);
                else { Directory.CreateDirectory(Path.GetDirectoryName(target)!); entry.ExtractToFile(target, true); }
            }
            string agent = Directory.GetFiles(versionRoot, "Howie.Monitor.Agent.exe", SearchOption.TopDirectoryOnly).Single();
            PackageContents.InstallNotices();
            var configuration = plan.Configuration;
            var old = oldConfiguration is null ? null : System.Text.Json.JsonSerializer.Deserialize<AgentConfiguration>(oldConfiguration, ProtocolJson.Options);
            if (old is not null && old.DeviceId != configuration.DeviceId && Directory.Exists(old.DataDirectory))
            {
                SetupPaths.VerifyPath(old.DataDirectory, SetupPaths.DataRoot);
                string backup = Path.Combine(SetupPaths.DataRoot, "queue-before-" + Guid.NewGuid().ToString("N")); SetupPaths.VerifyPath(backup, SetupPaths.DataRoot);
                Directory.Move(old.DataDirectory, backup);
                savedQueue = backup; oldQueue = old.DataDirectory;
            }
            SetupPaths.Write(SetupPaths.ConfigurationFile, configuration);
            var marker = new InstallMarker(SetupPaths.Product, "0.1.0", agent, plan.OwnerSid, plan.RequestId);
            SetupPaths.Write(SetupPaths.MarkerFile, marker);
            var args = new[] { serviceExisted ? "config" : "create", SetupPaths.ServiceName, "binPath=", ServiceCommand(agent), "start=", "auto", "obj=", "LocalSystem", "DisplayName=", "Howie Monitor Agent" };
            await Sc(args, cancellationToken);
            await Sc(["description", SetupPaths.ServiceName, "Howie Monitor 性能采集与经本机授权的远程终端，通过 HTTPS 连接私人面板。"], cancellationToken);
            await Sc(["failure", SetupPaths.ServiceName, "reset=", "86400", "actions=", "restart/5000/restart/15000/restart/60000"], cancellationToken);
            await Sc(["start", SetupPaths.ServiceName], cancellationToken);
            using (var started = new ServiceController(SetupPaths.ServiceName))
                await Task.Run(() => started.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(30)), cancellationToken);
            if (!Path.GetFullPath(setupExecutable).Equals(SetupPaths.InstalledSetup, StringComparison.OrdinalIgnoreCase)) File.Copy(setupExecutable, SetupPaths.InstalledSetup, true);
            using var key = Registry.LocalMachine.CreateSubKey(UninstallKey);
            key.SetValue("DisplayName", "Howie Monitor 采集端"); key.SetValue("DisplayVersion", "0.1.0"); key.SetValue("Publisher", "Howie Monitor");
            key.SetValue("InstallLocation", SetupPaths.ProgramRoot); key.SetValue("UninstallString", $"\"{SetupPaths.InstalledSetup}\" --uninstall");
            key.SetValue("NoModify", 1); key.SetValue("NoRepair", 0);
        }
        catch
        {
            if (previous is not null)
            {
                await Stop(CancellationToken.None);
                if (savedQueue is not null && oldQueue is not null)
                {
                    SetupPaths.VerifyPath(oldQueue, SetupPaths.DataRoot); SetupPaths.VerifyPath(savedQueue, SetupPaths.DataRoot);
                    if (Directory.Exists(oldQueue))
                    {
                        string failed = Path.Combine(SetupPaths.DataRoot, "queue-failed-" + Guid.NewGuid().ToString("N")); SetupPaths.VerifyPath(failed, SetupPaths.DataRoot);
                        Directory.Move(oldQueue, failed);
                    }
                    Directory.Move(savedQueue, oldQueue);
                }
                if (oldConfiguration is not null) File.WriteAllBytes(SetupPaths.ConfigurationFile, oldConfiguration);
                SetupPaths.Write(SetupPaths.MarkerFile, previous);
                if (serviceExisted)
                {
                    await Sc(["config", SetupPaths.ServiceName, "binPath=", ServiceCommand(previous.AgentPath)], CancellationToken.None);
                    await Sc(["start", SetupPaths.ServiceName], CancellationToken.None);
                }
            }
            throw;
        }
    }
    public static async Task Stop(CancellationToken cancellationToken)
    {
        using var service = new ServiceController(SetupPaths.ServiceName);
        try
        {
            if (service.Status == ServiceControllerStatus.Stopped) return;
            service.Stop(); await Task.Run(() => service.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(30)), cancellationToken);
        }
        catch (InvalidOperationException ex) when (ex.InnerException is System.ComponentModel.Win32Exception { NativeErrorCode: 1060 }) { }
    }
    public static async Task Uninstall(bool removeData, CancellationToken cancellationToken)
    {
        if (!SetupPaths.IsAdministrator) throw new InvalidOperationException("卸载需要管理员授权。");
        var marker = Existing() ?? throw new InvalidOperationException("未找到本产品安装，未删除任何文件。");
        await Stop(cancellationToken);
        using (var serviceKey = Registry.LocalMachine.OpenSubKey("SYSTEM\\CurrentControlSet\\Services\\" + SetupPaths.ServiceName))
            if (serviceKey is not null) await Sc(["delete", SetupPaths.ServiceName], cancellationToken);
        string self = Path.GetFullPath(Environment.ProcessPath!);
        DeleteOwnedTree(SetupPaths.ProgramRoot, SetupPaths.ProgramRoot, self);
        if (Directory.Exists(SetupPaths.ProgramRoot))
        {
            string pending = Path.Combine(SetupPaths.ProgramRoot, "uninstall-pending.json");
            SetupPaths.Write(pending, new { product = SetupPaths.Product });
            if (!MoveFileEx(pending, null, 4)) throw new InvalidOperationException("卸载清理登记失败，请重启后检查安装目录。");
        }
        Registry.LocalMachine.DeleteSubKeyTree(UninstallKey, false);
        if (removeData) DeleteOwnedTree(SetupPaths.DataRoot, SetupPaths.DataRoot, self);
    }
    private static void DeleteOwnedTree(string directory, string root, string runningExecutable)
    {
        SetupPaths.VerifyPath(Path.Combine(directory, ".check"), root);
        foreach (string file in Directory.GetFiles(directory))
        {
            SetupPaths.VerifyPath(file, root);
            if (file.Equals(runningExecutable, StringComparison.OrdinalIgnoreCase))
            {
                if (!MoveFileEx(file, null, 4)) throw new InvalidOperationException("卸载后清理登记失败，请重启后通过向导重试。");
            }
            else File.Delete(file);
        }
        foreach (string child in Directory.GetDirectories(directory)) DeleteOwnedTree(child, root, runningExecutable);
        if (!Directory.EnumerateFileSystemEntries(directory).Any()) Directory.Delete(directory);
        else if (!MoveFileEx(directory, null, 4)) throw new InvalidOperationException("目录清理登记失败，请重启后重试。");
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool MoveFileEx(string existing, string? target, int flags);
    private static async Task Sc(string[] arguments, CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "sc.exe"))
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync(cancellationToken); var error = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken); await Task.WhenAll(output, error);
        if (process.ExitCode != 0) throw new InvalidOperationException($"Windows 服务操作失败（{process.ExitCode}）。请检查权限或通过向导重试。");
    }
}
