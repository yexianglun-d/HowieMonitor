namespace Howie.Monitor.Server;

public sealed class NotificationWorker(NotificationStore notifications, TelemetryStore telemetry,
    ServerChanSender sender, IConfiguration configuration, ILogger<NotificationWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var started = DateTimeOffset.UtcNow;
        string publicUrl = configuration["public-url"] ?? configuration["server-url"] ?? "http://localhost:5080";
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                var now = DateTimeOffset.UtcNow;
                var device = telemetry.Device();
                var view = telemetry.Latest(now);
                // 设备切换后的旧快照不能参与新设备规则。
                if (view.Sample?.DeviceId != device?.DeviceId && view.Sample is not null) view = view with { DataStatus = "stale" };
                notifications.Evaluate(device, view, now, started, publicUrl);
                var dispatch = notifications.BeginSend(now);
                if (dispatch is null) continue;
                var result = await sender.Send(dispatch, stoppingToken);
                notifications.Complete(dispatch.Event.Id, result, DateTimeOffset.UtcNow);
                logger.LogInformation("通知发送状态：{Status}，事件 {EventId}", result.Status, dispatch.Event.Id);
            }
            catch (IOException) { logger.LogWarning("通知状态保存失败，下一轮重试；已开始的外部请求不会自动重发。"); }
        }
    }
}
