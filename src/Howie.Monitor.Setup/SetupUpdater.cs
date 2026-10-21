using System.Diagnostics;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Security.Principal;
using Howie.Monitor.Contracts;

namespace Howie.Monitor.Setup;

internal sealed record UpdateSnapshot(InstallMarker Marker, AgentConfiguration Configuration, string SetupSha256);

// 只由已在本机授权的服务启动。独立进程负责停服、安装和回退，不进入 GUI 或交互式 CLI。
internal static class SetupUpdater
{
    internal static async Task<int> Run(string id, bool recover)
    {
        if (!WindowsIdentity.GetCurrent().IsSystem) throw new InvalidOperationException("后台升级只能由采集服务启动。");
        string directory = AgentUpdateFiles.DirectoryFor(id), executable = Path.Combine(directory, "HowieMonitorSetup.exe");
        SetupPaths.VerifyPath(executable, AgentUpdateFiles.Root);
        if (!Path.GetFullPath(Environment.ProcessPath!).Equals(executable, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("升级程序不在受保护的任务目录。");
        using var installLock = InstallEngine.AcquireLock();
        var journal = AgentUpdateFiles.Read<AgentUpdateJournal>(AgentUpdateFiles.Journal) ?? throw new InvalidOperationException("升级任务不存在。");
        if (journal.Ticket.Id != id || AgentUpdatePhases.Finished(journal.Phase)) return 0;
        using var process = Process.GetCurrentProcess();
        journal = journal with { HelperPid = process.Id, HelperStartedAt = process.StartTime.ToUniversalTime().Ticks };
        AgentUpdateFiles.Write(AgentUpdateFiles.Journal, journal);
        string snapshotFile = Path.Combine(directory, "snapshot.json"), setupBackup = Path.Combine(directory, "previous-setup.exe");
        SetupPaths.VerifyPath(SetupPaths.ConfigurationFile, SetupPaths.DataRoot);
        var configuration = SetupPaths.Read<AgentConfiguration>(SetupPaths.ConfigurationFile) ?? throw new InvalidOperationException("本机配置不存在。");
        SetupPaths.ValidateConfiguration(configuration);
        if (configuration.DeviceId != journal.Ticket.DeviceId || configuration.Updates is not { Enabled: true })
            throw new InvalidOperationException("本机未授权此升级任务。");
        using var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false })
        { BaseAddress = new(configuration.ServerUrl), Timeout = TimeSpan.FromSeconds(8) };
        client.DefaultRequestHeaders.Authorization = new("Bearer", configuration.Token);
        bool changed = recover;
        async Task Set(string phase, string? message = null)
        {
            journal = journal with { Phase = phase, Message = message };
            AgentUpdateFiles.Write(AgentUpdateFiles.Journal, journal);
            await Report(client, id, phase, message);
        }
        try
        {
            var release = journal.Ticket.Release;
            if (release is not { Valid: true } || AgentBuild.Current.BuildId != release.BuildId || AgentBuild.Current.Version != release.Version
                || !string.Equals(PackageContents.PayloadHash, release.PayloadSha256, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(Hash(executable), release.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("升级程序与确认的发布版本不一致。");
            if (!recover)
            {
                if (journal.Phase != "staged" || journal.Ticket.ExpiresAt <= DateTimeOffset.UtcNow) throw new InvalidOperationException("升级任务已过期或状态不正确。");
                var marker = InstallEngine.Existing(true) ?? throw new InvalidOperationException("未找到已有安装。");
                if (marker.Build?.BuildId != journal.Ticket.SourceBuildId) throw new InvalidOperationException("本机安装版本已经变化，请重新确认。");
                _ = PackageContents.Check();
                SetupPaths.VerifyPath(SetupPaths.InstalledSetup, SetupPaths.ProgramRoot);
                AgentUpdateFiles.Verify(setupBackup, AgentUpdateFiles.Root);
                File.Copy(SetupPaths.InstalledSetup, setupBackup, true);
                AgentUpdateFiles.Write(snapshotFile, new UpdateSnapshot(marker, configuration, Hash(setupBackup)));
                // 必须先取得服务端安装许可；下载过期、凭据撤销或状态不一致时不停止服务。
                if (!await Report(client, id, "installing", null)) throw new InvalidOperationException("服务端尚未确认安装，原采集端继续运行。");
                journal = journal with { Phase = "installing" }; AgentUpdateFiles.Write(AgentUpdateFiles.Journal, journal);
                changed = true;
                using var installDeadline = new CancellationTokenSource(TimeSpan.FromMinutes(3));
                await InstallEngine.Apply(new(configuration, marker.RequestId, marker.OwnerSid, null), executable, installDeadline.Token, true);
                await Set("verifying");
                if (!await WaitReady(client, id, false)) throw new InvalidOperationException("新版本未在限定时间内恢复心跳和基础指标。");
                await Set("succeeded", "新版本已恢复心跳与 CPU、内存指标。");
                return 0;
            }
            throw new InvalidOperationException("上次升级进程中断，正在恢复原版本。");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or HttpRequestException
            or OperationCanceledException or System.ComponentModel.Win32Exception or System.Text.Json.JsonException or System.ServiceProcess.TimeoutException)
        {
            if (!changed)
            {
                await Set("failed", ex is InvalidOperationException ? ex.Message : "升级准备失败，原采集程序未替换。"); return 1;
            }
            try
            {
                await Set("rollingBack", "新版本安装或上报未确认，正在恢复原版本。");
                var snapshot = AgentUpdateFiles.Read<UpdateSnapshot>(snapshotFile) ?? throw new InvalidOperationException("回退快照不存在。");
                if (snapshot.Marker.Product != SetupPaths.Product || snapshot.Configuration.DeviceId != journal.Ticket.DeviceId
                    || snapshot.Marker.Build?.BuildId != journal.Ticket.SourceBuildId || Hash(setupBackup) != snapshot.SetupSha256)
                    throw new InvalidOperationException("回退快照校验失败。");
                await InstallEngine.RestoreUpdate(snapshot.Marker, snapshot.Configuration, setupBackup);
                if (!await WaitReady(client, id, true)) throw new InvalidOperationException("已切回原程序，但数据恢复尚未确认。");
                await Set("rolledBack", "升级未完成，原版本已恢复心跳与基础指标。"); return 1;
            }
            catch (Exception rollback) when (rollback is IOException or UnauthorizedAccessException or InvalidOperationException or HttpRequestException
                or OperationCanceledException or System.ComponentModel.Win32Exception or System.Text.Json.JsonException or System.ServiceProcess.TimeoutException)
            { await Set("recoveryRequired", "自动回退未确认，请在本机运行安装包修复；本地配置和旧程序已保留。"); return 1; }
        }
    }
    internal static void RecordStartFailure(string id)
    {
        try
        {
            if (!WindowsIdentity.GetCurrent().IsSystem || !Path.GetFullPath(Environment.ProcessPath!).Equals(
                Path.Combine(AgentUpdateFiles.DirectoryFor(id), "HowieMonitorSetup.exe"), StringComparison.OrdinalIgnoreCase)) return;
            var journal = AgentUpdateFiles.Read<AgentUpdateJournal>(AgentUpdateFiles.Journal);
            if (journal?.Ticket.Id != id || AgentUpdatePhases.Finished(journal.Phase)) return;
            if (journal.HelperPid is { } pid && journal.HelperStartedAt is { } started)
            {
                try
                {
                    using var other = Process.GetProcessById(pid);
                    if (other.Id != Environment.ProcessId && !other.HasExited && other.StartTime.ToUniversalTime().Ticks == started) return;
                }
                catch (ArgumentException) { }
            }
            string phase = journal.Phase is "installing" or "verifying" or "rollingBack" or "recoveryRequired" ? "recoveryRequired" : "failed";
            AgentUpdateFiles.Write(AgentUpdateFiles.Journal, journal with { Phase = phase, Message = "后台升级未能继续，请在本机运行安装包检查。" });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or System.ComponentModel.Win32Exception or System.Text.Json.JsonException)
        { Console.Error.WriteLine("升级失败记录未能保存（" + ex.GetType().Name + "），请在本机检查。"); }
    }
    private static string Hash(string path)
    {
        AgentUpdateFiles.Verify(path, AgentUpdateFiles.Root);
        using var stream = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(stream));
    }
    private static async Task<bool> Report(HttpClient client, string id, string phase, string? message)
    {
        try
        {
            using var response = await client.PostAsJsonAsync("api/ingest/updates/report", new AgentUpdateReport(id, phase, message), ProtocolJson.Options);
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException) { return false; }
    }
    private static async Task<bool> WaitReady(HttpClient client, string id, bool rollback)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        while (!deadline.IsCancellationRequested)
        {
            await Report(client, id, rollback ? "rollingBack" : "verifying", null);
            try
            {
                using var response = await client.GetAsync("api/ingest/updates/" + id + "/health?rollback=" + (rollback ? "true" : "false"), deadline.Token);
                if (response.IsSuccessStatusCode && (await response.Content.ReadFromJsonAsync<AgentUpdateHealth>(ProtocolJson.Options, deadline.Token))?.Ready == true) return true;
            }
            catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException) { }
            try { await Task.Delay(TimeSpan.FromSeconds(2), deadline.Token); }
            catch (OperationCanceledException) when (deadline.IsCancellationRequested) { break; }
        }
        return false;
    }
}
