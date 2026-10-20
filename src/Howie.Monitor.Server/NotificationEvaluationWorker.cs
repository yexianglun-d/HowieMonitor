namespace Howie.Monitor.Server;

public sealed class NotificationEvaluationWorker(NotificationStore notifications, TelemetryStore telemetry,
    IConfiguration configuration, ILogger<NotificationEvaluationWorker> logger) : BackgroundService
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
                foreach (var computer in telemetry.Computers(now))
                {
                    telemetry.WithComputer(computer.DeviceId, device =>
                        notifications.Evaluate(device, telemetry.Latest(now, computer.DeviceId), now, started, publicUrl));
                }
            }
            catch (IOException) { logger.LogWarning("通知规则状态保存失败，下一轮重新检查。"); }
        }
    }
}
