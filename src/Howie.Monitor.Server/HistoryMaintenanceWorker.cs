using Microsoft.Data.Sqlite;

namespace Howie.Monitor.Server;

public sealed class HistoryMaintenanceWorker(TelemetryStore store, ILogger<HistoryMaintenanceWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try { store.PruneHistory(DateTimeOffset.UtcNow); }
            catch (SqliteException exception) { logger.LogError(exception, "历史数据清理失败，将在下一轮重试。"); }
            await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);
        }
    }
}
