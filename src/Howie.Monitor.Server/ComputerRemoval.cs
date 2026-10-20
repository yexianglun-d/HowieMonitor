using Microsoft.Data.Sqlite;

namespace Howie.Monitor.Server;

public sealed record ComputerRemovalRequest(string Password);
public sealed class ComputerRemoval(TelemetryStore telemetry, NotificationStore notifications, TerminalBroker terminals)
{
    private readonly object _gate = new();
    public void Remove(string id)
    {
        lock (_gate)
        {
            telemetry.BeginComputerRemoval(id);
            terminals.RemoveComputer(id);
            notifications.RemoveComputer(id);
            telemetry.CompleteComputerRemoval(id);
        }
    }
    public void Resume()
    {
        foreach (string id in telemetry.PendingComputerRemovals()) Remove(id);
    }
}
public sealed class ComputerRemovalWorker(ComputerRemoval removal, ILogger<ComputerRemovalWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
        do
        {
            try { removal.Resume(); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or SqliteException)
            { logger.LogWarning("电脑移除尚未完成，将继续清理：{Error}", error.GetType().Name); }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
