using System.Diagnostics;
using System.Net.Http.Json;
using System.Security.Cryptography;
using Howie.Monitor.Contracts;

namespace Howie.Monitor.Agent;

public sealed class UpdateWorker(AgentState state, HttpClient client, ILogger<UpdateWorker> logger) : BackgroundService
{
    public static bool Eligible(string configurationFile, AgentConfiguration configuration) =>
        configuration.Updates is { Enabled: true } && !configuration.LocalDevelopment && AgentBuild.Current.IsRelease
        && configuration.ServerUrl == "https://monitor.howied.me/"
        && Path.GetFullPath(configurationFile).Equals(Path.Combine(AgentUpdateFiles.DataRoot, "agent.json"), StringComparison.OrdinalIgnoreCase)
        && Path.GetFullPath(configuration.DataDirectory).Equals(Path.Combine(AgentUpdateFiles.DataRoot, "queue"), StringComparison.OrdinalIgnoreCase);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (state.Registration.Updates is null) return;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (state.Registered)
                {
                    if (!await HandleJournal(stoppingToken) && state.Registration.Updates.Enabled)
                    {
                        using var response = await client.PostAsJsonAsync("api/ingest/updates/poll", new AgentUpdatePoll(state.Registration.SessionId), ProtocolJson.Options, stoppingToken);
                        response.EnsureSuccessStatusCode();
                        var poll = await response.Content.ReadFromJsonAsync<AgentUpdatePollResponse>(ProtocolJson.Options, stoppingToken);
                        if (poll?.Ticket is { } ticket) await Download(ticket, stoppingToken);
                    }
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or IOException or InvalidOperationException or UnauthorizedAccessException or System.Text.Json.JsonException or System.ComponentModel.Win32Exception)
            { logger.LogWarning("采集端升级暂未完成（{Type}），将保留状态。", ex.GetType().Name); }
            try { await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
        }
    }
    private async Task<bool> HandleJournal(CancellationToken token)
    {
        var journal = AgentUpdateFiles.Read<AgentUpdateJournal>(AgentUpdateFiles.Journal);
        if (journal is null) return false;
        if (journal.Ticket.DeviceId != state.Configuration.DeviceId) throw new InvalidOperationException("升级记录与本机不一致，需本地修复。");
        if (AgentUpdatePhases.Finished(journal.Phase))
        {
            // 完成回执可以断线重试；由服务端核对新会话和实时指标后再确认结果。
            if (journal.Phase is "succeeded" or "rolledBack") await Report(journal.Ticket.Id,
                journal.Phase == "succeeded" ? "verifying" : "rollingBack", null, token);
            if (await Report(journal.Ticket.Id, journal.Phase, journal.Message, token) && !HelperAlive(journal))
            {
                try { Cleanup(journal.Ticket.Id); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                { logger.LogWarning("已完成的升级临时文件未能清理（{Type}）。", ex.GetType().Name); }
            }
            return false;
        }
        if (HelperAlive(journal)) return true;
        if (!state.Registration.Updates!.Enabled) return true;
        if (journal.Phase == "recoveryRequired") { await Report(journal.Ticket.Id, journal.Phase, journal.Message, token); return true; }
        if (journal.Phase is "installing" or "verifying" or "rollingBack") { StartHelper(journal, true); return true; }
        if (journal.Phase == "staged" && DateTimeOffset.UtcNow < journal.Ticket.ExpiresAt) { StartHelper(journal, false); return true; }
        var failed = journal with { Phase = "failed", Message = "下载中断或任务已过期，尚未替换采集程序。" };
        AgentUpdateFiles.Write(AgentUpdateFiles.Journal, failed);
        await Report(failed.Ticket.Id, failed.Phase, failed.Message, token); return true;
    }
    private static void Cleanup(string id)
    {
        string directory = AgentUpdateFiles.DirectoryFor(id);
        foreach (string name in new[] { "HowieMonitorSetup.exe", "previous-setup.exe", "snapshot.json" })
        {
            string path = Path.Combine(directory, name); AgentUpdateFiles.Verify(path, AgentUpdateFiles.Root);
            if (File.Exists(path)) File.Delete(path);
        }
        if (Directory.Exists(directory) && !Directory.EnumerateFileSystemEntries(directory).Any()) Directory.Delete(directory);
    }
    private static bool HelperAlive(AgentUpdateJournal journal)
    {
        if (journal.HelperPid is not { } pid || journal.HelperStartedAt is not { } started) return false;
        try { using var process = Process.GetProcessById(pid); return !process.HasExited && process.StartTime.ToUniversalTime().Ticks == started; }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException) { return false; }
    }
    private async Task Download(AgentUpdateTicket ticket, CancellationToken token)
    {
        if (ticket.DeviceId != state.Configuration.DeviceId || ticket.SourceSessionId != state.Registration.SessionId
            || ticket.SourceBuildId != AgentBuild.Current.BuildId || ticket.Release is not { Valid: true } || ticket.ExpiresAt <= DateTimeOffset.UtcNow
            || System.Version.Parse(ticket.Release.Version) <= System.Version.Parse(AgentBuild.Current.Version))
            throw new InvalidOperationException("升级任务与本机版本不匹配。");
        string directory = AgentUpdateFiles.DirectoryFor(ticket.Id), executable = Path.Combine(directory, "HowieMonitorSetup.exe");
        AgentUpdateFiles.Verify(executable, AgentUpdateFiles.Root); Directory.CreateDirectory(directory);
        AgentUpdateFiles.Write(AgentUpdateFiles.Journal, new AgentUpdateJournal(ticket, "downloading"));
        try
        {
            var drive = new DriveInfo(Path.GetPathRoot(directory)!);
            if (drive.AvailableFreeSpace < Math.Max(1024L * 1024 * 1024, ticket.Release.Bytes * 4)) throw new InvalidOperationException("可用空间不足 1 GiB。");
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token); timeout.CancelAfter(TimeSpan.FromMinutes(10));
            // 使用独立客户端，采集端的四秒上报超时不能截断安装包下载。禁用跨源重定向。
            using var download = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { BaseAddress = client.BaseAddress, Timeout = Timeout.InfiniteTimeSpan };
            using var response = await download.GetAsync("install/" + ticket.Release.Sha256 + "/HowieMonitorSetup.exe", HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength is { } size && size != ticket.Release.Bytes) throw new InvalidOperationException("安装包大小已变化。");
            using (var input = await response.Content.ReadAsStreamAsync(timeout.Token))
            using (var output = new FileStream(executable, FileMode.Create, FileAccess.Write, FileShare.None))
            using (var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
            {
                byte[] buffer = new byte[65536]; long bytes = 0; int read;
                while ((read = await input.ReadAsync(buffer, timeout.Token)) > 0)
                {
                    bytes += read; if (bytes > ticket.Release.Bytes) throw new InvalidOperationException("安装包超过预期大小。");
                    hash.AppendData(buffer, 0, read); await output.WriteAsync(buffer.AsMemory(0, read), timeout.Token);
                }
                output.Flush(true);
                if (bytes != ticket.Release.Bytes || !string.Equals(Convert.ToHexString(hash.GetHashAndReset()), ticket.Release.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("安装包校验失败，未执行安装。");
            }
            if (DateTimeOffset.UtcNow >= ticket.ExpiresAt) throw new InvalidOperationException("升级任务已过期。");
            var journal = new AgentUpdateJournal(ticket, "staged"); AgentUpdateFiles.Write(AgentUpdateFiles.Journal, journal);
            StartHelper(journal, false);
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or IOException or InvalidOperationException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            // helper 启动前才会走此分支；运行中的安装交由独立进程持久化结果。
            var current = AgentUpdateFiles.Read<AgentUpdateJournal>(AgentUpdateFiles.Journal);
            if (current?.Phase is "installing" or "verifying" or "rollingBack" || current is not null && HelperAlive(current)) throw;
            string message = ex is InvalidOperationException ? ex.Message : "安装包下载或启动失败，原采集程序继续运行。";
            AgentUpdateFiles.Write(AgentUpdateFiles.Journal, new AgentUpdateJournal(ticket, "failed", message));
            await Report(ticket.Id, "failed", message, token); throw;
        }
    }
    private static void StartHelper(AgentUpdateJournal journal, bool recover)
    {
        string executable = Path.Combine(AgentUpdateFiles.DirectoryFor(journal.Ticket.Id), "HowieMonitorSetup.exe");
        AgentUpdateFiles.Verify(executable, AgentUpdateFiles.Root);
        using (var stream = File.OpenRead(executable))
            if (!string.Equals(Convert.ToHexString(SHA256.HashData(stream)), journal.Ticket.Release.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("升级程序校验失败，需本地修复。");
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = Path.GetDirectoryName(executable)! };
        start.ArgumentList.Add(recover ? "--recover-update" : "--apply-update"); start.ArgumentList.Add(journal.Ticket.Id);
        // 不等待子进程：升级程序会停止此服务，等待会造成停服死锁。
        using var process = Process.Start(start) ?? throw new InvalidOperationException("未能启动独立升级程序。");
    }
    private async Task<bool> Report(string id, string phase, string? message, CancellationToken token)
    {
        using var response = await client.PostAsJsonAsync("api/ingest/updates/report", new AgentUpdateReport(id, phase, message), ProtocolJson.Options, token);
        return response.IsSuccessStatusCode;
    }
}
