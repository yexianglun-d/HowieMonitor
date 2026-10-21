using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;
using Howie.Monitor.Contracts;

namespace Howie.Monitor.Setup;

internal sealed record PendingUninstall(string Product, bool RemoveData = false);
internal sealed record UninstallLaunch(string Product, int ParentId, long ParentStartedAt, string Sha256);
internal sealed record UninstallLocations(string ProgramRoot, string DataRoot, string UpdatesRoot, string WorkRoot)
{
    internal string PendingFile => Path.Combine(ProgramRoot, "uninstall-pending.json");
}

internal static class SetupUninstall
{
    internal static string PendingFile => Path.Combine(SetupPaths.ProgramRoot, "uninstall-pending.json");
    private static string StageRoot => Path.Combine(Path.GetDirectoryName(SetupPaths.DataRoot)!, "Uninstall");
    private static UninstallLocations Locations => new(SetupPaths.ProgramRoot, SetupPaths.DataRoot, AgentUpdateFiles.Root, SetupPaths.WorkRoot);

    internal static bool RelaunchIfNeeded(string[] args)
    {
        bool uninstall = args is ["--uninstall"];
        bool pending = File.Exists(PendingFile) && args.Length == 0;
        if (!uninstall && !pending) return false;
        if (!SetupPaths.IsAdministrator)
        {
            var elevated = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = true, Verb = "runas" };
            foreach (string arg in args) elevated.ArgumentList.Add(arg);
            using var child = Process.Start(elevated) ?? throw new InvalidOperationException("未能启动管理员维护操作。");
            return true;
        }
        string self = Path.GetFullPath(Environment.ProcessPath!);
        if (!uninstall || !self.StartsWith(SetupPaths.ProgramRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) return false;
        SetupPaths.VerifyPath(self, SetupPaths.ProgramRoot);
        SetupPaths.PrivateDirectory(Path.GetDirectoryName(SetupPaths.DataRoot)!, SetupPaths.CurrentSid, false);
        SetupPaths.PrivateDirectory(StageRoot, SetupPaths.CurrentSid, false);
        string id = Guid.NewGuid().ToString("N"), stage = Path.Combine(StageRoot, id), executable = Path.Combine(stage, "HowieMonitorSetup.exe");
        SetupPaths.PrivateDirectory(stage, SetupPaths.CurrentSid, false);
        using var parent = Process.GetCurrentProcess();
        File.Copy(self, executable, false);
        SetupPaths.Write(Path.Combine(stage, "launch.json"), new UninstallLaunch(SetupPaths.Product, parent.Id,
            parent.StartTime.ToUniversalTime().Ticks, Hash(executable)));
        try
        {
            var start = new ProcessStartInfo(executable) { UseShellExecute = false, WorkingDirectory = stage };
            start.ArgumentList.Add("--staged-uninstall"); start.ArgumentList.Add(id);
            using var child = Process.Start(start) ?? throw new InvalidOperationException("未能启动独立卸载向导。");
            return true;
        }
        catch { DeleteTree(stage, stage); throw; }
    }

    internal static string EnterStage(string id)
    {
        if (!SetupPaths.IsAdministrator || !Guid.TryParseExact(id, "N", out _)) throw new InvalidOperationException("卸载维护请求无效。");
        string stage = Path.Combine(StageRoot, id), executable = Path.Combine(stage, "HowieMonitorSetup.exe");
        SetupPaths.VerifyPath(executable, StageRoot);
        if (!Path.GetFullPath(Environment.ProcessPath!).Equals(executable, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("卸载向导不在本次维护目录。");
        var launch = SetupPaths.Read<UninstallLaunch>(Path.Combine(stage, "launch.json")) ?? throw new InvalidOperationException("卸载维护记录不存在。");
        if (launch.Product != SetupPaths.Product || launch.Sha256 != Hash(executable)) throw new InvalidOperationException("卸载维护记录不一致。");
        try
        {
            using var parent = Process.GetProcessById(launch.ParentId);
            if (parent.StartTime.ToUniversalTime().Ticks == launch.ParentStartedAt && !parent.WaitForExit(30000))
                throw new InvalidOperationException("请先关闭原安装或卸载窗口，再重新卸载。");
        }
        catch (ArgumentException) { }
        return stage;
    }

    internal static void RecoverPending()
    {
        RecoverPending(Locations, SetupPaths.IsAdministrator, () =>
        {
            using var service = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\" + SetupPaths.ServiceName);
            return service is not null;
        }, () => PendingFileDeletes.RemoveOwnedDeletes(SetupPaths.ProgramRoot, SetupPaths.DataRoot), () => Registry.LocalMachine.DeleteSubKeyTree(InstallEngine.UninstallKey, false));
    }

    internal static void RecoverPending(UninstallLocations paths, bool administrator, Func<bool> serviceExists, Action removePendingDeletes, Action unregister)
    {
        SetupPaths.VerifyPath(paths.PendingFile, paths.ProgramRoot);
        if (!File.Exists(paths.PendingFile)) return;
        if (!administrator) throw new InvalidOperationException("检测到上次卸载未完成，请以管理员身份运行新版安装包，自动清理后重试。");
        var pending = ReadPending(paths);
        if (serviceExists()) throw new InvalidOperationException("旧采集服务仍存在或等待删除，请关闭服务管理窗口后重试；仍无法释放时重启 Windows。");
        Finish(paths, pending.RemoveData, removePendingDeletes, unregister);
    }

    internal static PendingUninstall ReadPending() => ReadPending(Locations);
    private static PendingUninstall ReadPending(UninstallLocations paths)
    {
        SetupPaths.VerifyPath(paths.PendingFile, paths.ProgramRoot);
        if (new FileInfo(paths.PendingFile).Length > 4096) throw new InvalidOperationException("待清理标记无效，未删除任何文件。");
        var pending = SetupPaths.Read<PendingUninstall>(paths.PendingFile);
        return pending?.Product == SetupPaths.Product ? pending : throw new InvalidOperationException("待清理标记不属于本产品，未删除任何文件。");
    }

    private static void Finish(UninstallLocations paths, bool removeData, Action removePendingDeletes, Action unregister)
    {
        if (removeData && Directory.Exists(paths.DataRoot) && Directory.EnumerateFileSystemEntries(paths.DataRoot).Any())
        {
            string marker = Path.Combine(paths.DataRoot, "product.json"); SetupPaths.VerifyPath(marker, paths.DataRoot);
            if (SetupPaths.Read<Dictionary<string, string>>(marker)?.GetValueOrDefault("product") != SetupPaths.Product)
                throw new InvalidOperationException("本地数据目录归属未确认，未执行彻底清理。");
        }
        // 先删除实际文件；仍有旧安装器持有句柄时保留标记和系统任务，绝不放行新装。
        try
        {
            DeleteTree(paths.ProgramRoot, paths.ProgramRoot, paths.PendingFile);
            if (removeData)
            {
                DeleteTree(paths.UpdatesRoot, paths.UpdatesRoot);
                string productMarker = Path.Combine(paths.DataRoot, "product.json");
                DeleteTree(paths.DataRoot, paths.DataRoot, productMarker);
                File.Delete(productMarker);
                if (Directory.Exists(paths.DataRoot)) Directory.Delete(paths.DataRoot);
                DeleteTree(paths.WorkRoot, paths.WorkRoot);
            }
        }
        catch (IOException ex) { throw new InvalidOperationException("卸载文件仍被占用，请关闭旧安装/卸载窗口后，用新版安装包重试清理。", ex); }
        removePendingDeletes();
        unregister();
        File.Delete(paths.PendingFile);
        if (Directory.Exists(paths.ProgramRoot)) Directory.Delete(paths.ProgramRoot);
    }

    internal static void DeleteTree(string directory, string root, string? keep = null)
    {
        SetupPaths.VerifyPath(Path.Combine(directory, ".check"), root);
        if (!Directory.Exists(directory)) return;
        foreach (string file in Directory.GetFiles(directory))
        {
            SetupPaths.VerifyPath(file, root);
            if (!string.Equals(file, keep, StringComparison.OrdinalIgnoreCase)) File.Delete(file);
        }
        foreach (string child in Directory.GetDirectories(directory)) DeleteTree(child, root, keep);
        if (!Directory.EnumerateFileSystemEntries(directory).Any()) Directory.Delete(directory);
    }

    internal static void RemoveStageAfterExit(string stage)
    {
        using var process = Process.GetCurrentProcess();
        var request = Convert.ToBase64String(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
        { product = SetupPaths.Product, stage, pid = process.Id, started = process.StartTime.ToUniversalTime().Ticks.ToString(CultureInfo.InvariantCulture) })));
        string shell = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe");
        var start = new ProcessStartInfo(shell) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = Path.GetDirectoryName(shell)! };
        start.Environment["PSModulePath"] = Path.Combine(Path.GetDirectoryName(shell)!, "Modules");
        foreach (string arg in new[] { "-NoLogo", "-NoProfile", "-NonInteractive", "-EncodedCommand", Convert.ToBase64String(Encoding.Unicode.GetBytes(CleanupScript(request))) }) start.ArgumentList.Add(arg);
        using var child = Process.Start(start) ?? throw new InvalidOperationException("临时卸载向导清理未启动。");
    }

    internal static string CleanupScript(string request, string? stageRoot = null) => """
        $ErrorActionPreference = 'Stop'
        $request = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('__REQUEST__')) | ConvertFrom-Json
        $root = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('__ROOT__'))
        $stage = [IO.Path]::GetFullPath($request.stage)
        if ($request.product -ne 'HowieMonitor-93746211-9989-4cef-9d88-07870b3f35dd' -or
            [IO.Path]::GetDirectoryName($stage) -ne $root -or [IO.Path]::GetFileName($stage) -notmatch '^[a-f0-9]{32}$') { exit 2 }
        try { $parent = [Diagnostics.Process]::GetProcessById($request.pid) } catch [ArgumentException] { $parent = $null }
        if ($null -ne $parent) {
            try { if ($parent.StartTime.ToUniversalTime().Ticks.ToString() -eq $request.started) { $parent.WaitForExit() } }
            finally { $parent.Dispose() }
        }
        function Assert-PlainPath([string]$path) {
            $check = $path
            while ($check) {
                if (([IO.File]::Exists($check) -or [IO.Directory]::Exists($check)) -and
                    (([IO.File]::GetAttributes($check) -band [IO.FileAttributes]::ReparsePoint) -ne 0)) { throw 'Uninstall cleanup path is a link.' }
                $check = [IO.Path]::GetDirectoryName($check)
            }
        }
        function Remove-StageTree([string]$directory) {
            Assert-PlainPath $directory
            foreach ($file in [IO.Directory]::GetFiles($directory)) { Assert-PlainPath $file; [IO.File]::Delete($file) }
            foreach ($child in [IO.Directory]::GetDirectories($directory)) { Remove-StageTree $child }
            [IO.Directory]::Delete($directory)
        }
        if ([IO.Directory]::Exists($stage)) { Remove-StageTree $stage }
        if ([IO.Directory]::Exists($root) -and [IO.Directory]::GetFileSystemEntries($root).Length -eq 0) { [IO.Directory]::Delete($root) }
        $shared = [IO.Path]::GetDirectoryName($root)
        $expected = [IO.Path]::Combine([Environment]::GetFolderPath('CommonApplicationData'), 'HowieMonitor')
        if ($shared -eq $expected -and [IO.Directory]::Exists($shared)) {
            $lockFile = [IO.Path]::Combine($shared, 'install.lock')
            $entries = [IO.Directory]::GetFileSystemEntries($shared)
            if ($entries.Length -eq 1 -and $entries[0] -eq $lockFile) {
                Assert-PlainPath $lockFile
                try {
                    $guard = [IO.FileStream]::new($lockFile, [IO.FileMode]::Open, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None, 1, [IO.FileOptions]::DeleteOnClose)
                    $guard.Dispose()
                } catch [IO.IOException] { }
            }
            if ([IO.Directory]::GetFileSystemEntries($shared).Length -eq 0) { [IO.Directory]::Delete($shared) }
        }
        """.Replace("__REQUEST__", request, StringComparison.Ordinal).Replace("__ROOT__", Convert.ToBase64String(Encoding.UTF8.GetBytes(stageRoot ?? StageRoot)), StringComparison.Ordinal);

    private static string Hash(string path)
    { using var file = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(file)); }
}
