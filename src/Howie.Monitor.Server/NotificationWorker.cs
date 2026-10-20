namespace Howie.Monitor.Server;

public sealed class NotificationWorker(NotificationStore notifications, ServerChanSender sender, TelemetryStore telemetry,
    ILogger<NotificationWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                var now = DateTimeOffset.UtcNow;
                var dispatch = notifications.BeginSend(now);
                if (dispatch is null) continue;
                if (dispatch.Event.ComputerId is { } id && !telemetry.WithComputer(id, _ => { }))
                { notifications.Complete(dispatch.Event.Id, new("cancelled", "电脑已移除，取消发送。"), now); continue; }
                var result = await sender.Send(dispatch, stoppingToken);
                notifications.Complete(dispatch.Event.Id, result, DateTimeOffset.UtcNow);
                logger.LogInformation("通知发送状态：{Status}，事件 {EventId}", result.Status, dispatch.Event.Id);
            }
            catch (IOException) { logger.LogWarning("通知状态保存失败，下一轮重试；已开始的外部请求不会自动重发。"); }
        }
    }
}
