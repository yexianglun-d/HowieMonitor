using System.Diagnostics;
using System.Net.Http.Json;
using Howie.Monitor.Collector;
using Howie.Monitor.Contracts;

namespace Howie.Monitor.Agent;

public sealed class AgentState(AgentConfiguration configuration, DeviceRegistration registration)
{
    public AgentConfiguration Configuration { get; } = configuration;
    public DeviceRegistration Registration { get; } = registration;
    public long LastSequence;
    public volatile string? LastError;
    public volatile bool Registered;
}

public sealed class CollectionWorker(AgentState state, AgentQueue queue, ILogger<CollectionWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try { await Collect(stoppingToken); }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        catch (Exception ex)
        {
            // 非预期采集故障必须以失败退出，Windows 服务恢复才能按已配置策略重新启动。
            logger.LogCritical("采集发生不可恢复异常，退出并等待服务恢复：{Error}", ex.GetType().Name);
            Environment.Exit(1);
        }
    }
    private async Task Collect(CancellationToken stoppingToken)
    {
        using var system = new WindowsSystemCollector(state.Registration.Inventory);
        using var sensors = new SensorCollector(state.Registration.Sensors);
        var processes = new ProcessCollector(); long sequence = 0;
        while (!stoppingToken.IsCancellationRequested)
        {
            var sampledAt = DateTimeOffset.UtcNow; var timer = Stopwatch.StartNew();
            var systemMetrics = system.Collect(); var sensorMetrics = sensors.Collect();
            var processMetrics = processes.Collect(); timer.Stop();
            var sample = new TelemetrySample(state.Registration.DeviceId, state.Registration.BootId,
                state.Registration.SessionId, sequence++, sampledAt, Environment.TickCount64 / 1000d,
                timer.Elapsed.TotalMilliseconds, [.. systemMetrics.Metrics, .. sensorMetrics.Metrics],
                processMetrics, [.. systemMetrics.Issues, .. sensorMetrics.Issues]);
            queue.Add(sample); Interlocked.Exchange(ref state.LastSequence, sample.Sequence);
            if (sample.Sequence % 30 == 0) logger.LogInformation("采样 {Sequence}，进程 {Count}，采集耗时 {Duration:F1} ms", sample.Sequence, processMetrics.Count, timer.Elapsed.TotalMilliseconds);
            await Task.Delay(TimeSpan.FromMilliseconds(Math.Max(1, 2000 - timer.Elapsed.TotalMilliseconds)), stoppingToken);
        }
    }
}

public sealed class UploadWorker(AgentState state, AgentQueue queue, HttpClient client, ILogger<UploadWorker> logger, IHostApplicationLifetime lifetime) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            var live = Live(stoppingToken); var backfill = Backfill(stoppingToken);
            await await Task.WhenAny(live, backfill);
            await Task.WhenAll(live, backfill);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        catch (Exception ex)
        {
            logger.LogCritical("上报发生不可恢复异常，退出并等待服务恢复：{Error}", ex.GetType().Name);
            Environment.Exit(1);
        }
    }

    private async Task Live(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                if (!state.Registered)
                {
                    using var registration = await client.PostAsJsonAsync("/api/ingest/device", state.Registration, ProtocolJson.Options, token);
                    registration.EnsureSuccessStatusCode(); state.Registered = true;
                }
                var queueState = queue.State();
                using var beat = await client.PostAsJsonAsync("/api/ingest/heartbeat", new Heartbeat(state.Registration.DeviceId,
                    state.Registration.BootId, state.Registration.SessionId, Interlocked.Read(ref state.LastSequence),
                    queueState.Count, queueState.Dropped, state.LastError, queueState.DroppedFrom, queueState.DroppedTo), ProtocolJson.Options, token);
                beat.EnsureSuccessStatusCode();
                var sample = queue.Latest();
                if (sample is not null && sample.SessionId == state.Registration.SessionId && sample.SampledAt >= DateTimeOffset.UtcNow.AddSeconds(-10))
                    await Send(new(false, [sample]), token);
                state.LastError = null;
            }
            catch (HttpRequestException ex) when (ex.StatusCode is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden)
            {
                state.LastError = "采集凭据已失效，请通过安装向导重新接入。";
                logger.LogWarning("采集凭据已失效，停止当前实例；本地未确认队列保留。");
                lifetime.StopApplication(); break;
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !token.IsCancellationRequested)
            {
                state.LastError = "实时连接失败：" + ex.GetType().Name;
                logger.LogWarning("实时连接失败，采样继续保存在本地队列：{Error}", ex.GetType().Name);
            }
            await Task.Delay(1000, token);
        }
    }
    private async Task Backfill(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            await Task.Delay(2000, token);
            if (!state.Registered) continue;
            var backlog = queue.Backlog(DateTimeOffset.UtcNow.AddSeconds(-5));
            if (backlog.Count == 0) continue;
            try { await Send(new(true, backlog), token); }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !token.IsCancellationRequested)
            { logger.LogWarning("历史补传失败，保留未确认采样：{Error}", ex.GetType().Name); }
        }
    }
    private async Task Send(IngestRequest request, CancellationToken token)
    {
        using var response = await client.PostAsJsonAsync("/api/ingest/samples", request, ProtocolJson.Options, token);
        response.EnsureSuccessStatusCode();
        var acknowledgement = await response.Content.ReadFromJsonAsync<IngestAcknowledgement>(ProtocolJson.Options, token)
            ?? throw new HttpRequestException("上报缺少接收确认。");
        var requested = request.Samples.Select(sample => new SampleIdentity(sample.SessionId, sample.Sequence)).ToHashSet();
        if (acknowledgement.Accepted.Any(identity => !requested.Contains(identity))) throw new HttpRequestException("服务器返回了非本批次的确认。");
        queue.Acknowledge(acknowledgement);
    }
}
