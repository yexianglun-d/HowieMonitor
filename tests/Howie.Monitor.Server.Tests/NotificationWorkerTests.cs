using System.Net;
using Howie.Monitor.Contracts;
using Howie.Monitor.Server;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Howie.Monitor.Server.Tests;

public sealed class NotificationWorkerTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "howie-worker-tests-" + Guid.NewGuid().ToString("N"));

    [Fact] public async Task SlowSendDoesNotInterruptContinuousRulesForOtherComputers()
    {
        var notifications = new NotificationStore(_directory, new EphemeralDataProtectionProvider());
        var rule = new AlertRule(Guid.NewGuid().ToString(), "CPU", true, "metric", "cpu.usage", "cpu/total", "%", "above", 90, 4, 70, 4, true);
        Assert.Null(notifications.Update(new(true, 5, "SCT" + "0123456789abcdefghijklmnopqrstuvwxyz", false, [])));
        Assert.Null(notifications.QueueTest(DateTimeOffset.UtcNow, "https://monitor.example"));
        foreach (string id in new[] { "first", "second" }) Assert.Null(notifications.UpdateComputer(id, new([rule])));
        var telemetry = new TelemetryStore(_directory);
        string session = Guid.NewGuid().ToString();
        foreach (string id in new[] { "first", "second" })
            telemetry.Register(new(id, id, new("Windows", "11", [], [], [], [], [], [], []),
                new(false, false, null, false, 2, [], []), "test", "boot", session));
        var handler = new SlowHandler(); using var sender = new ServerChanSender(handler);
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            { ["public-url"] = "https://monitor.example" }).Build();
        using var worker = new NotificationWorker(notifications, sender, telemetry, NullLogger<NotificationWorker>.Instance);
        using var evaluation = new NotificationEvaluationWorker(notifications, telemetry, configuration, NullLogger<NotificationEvaluationWorker>.Instance);
        using var publishing = new CancellationTokenSource();
        var publisher = Task.Run(async () =>
        {
            long sequence = 0;
            while (!publishing.IsCancellationRequested)
            {
                var now = DateTimeOffset.UtcNow;
                foreach (string id in new[] { "first", "second" })
                    telemetry.Ingest(new(false, [new(id, "boot", session, sequence, now, 0, 0,
                        [MetricReading.Present("cpu.usage", "cpu/total", "CPU", 95, "%", "test")], [], [])]), now);
                sequence++; await Task.Delay(500, publishing.Token);
            }
        });
        try
        {
            await evaluation.StartAsync(CancellationToken.None);
            await worker.StartAsync(CancellationToken.None);
            await handler.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await Until(() => new[] { "first", "second" }.All(id => notifications.ViewComputer(id).Events.Any(item => item.Kind == "trigger")));
            var view = notifications.View(DateTimeOffset.UtcNow);
            Assert.All(new[] { "first", "second" }, id => Assert.Equal("queued", Assert.Single(notifications.ViewComputer(id).Events).Status));
            Assert.Equal("sending", Assert.Single(view.Events, item => item.Kind == "test").Status);
            Assert.Equal(1, handler.Calls); Assert.Equal(1, view.RequestsToday);
            handler.Release.TrySetResult();
            await Until(() => notifications.View(DateTimeOffset.UtcNow).Events.Any(item => item.Kind == "test" && item.Status == "accepted"));
            Assert.Equal(1, handler.Calls);
        }
        finally
        {
            handler.Release.TrySetResult(); publishing.Cancel();
            await worker.StopAsync(CancellationToken.None);
            await evaluation.StopAsync(CancellationToken.None);
            try { await publisher; } catch (OperationCanceledException) when (publishing.IsCancellationRequested) { }
        }
    }

    private static async Task Until(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        while (!condition()) await Task.Delay(50, timeout.Token);
    }

    private sealed class SlowHandler : HttpMessageHandler
    {
        public readonly TaskCompletionSource Started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Calls;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Calls); Started.TrySetResult();
            await Release.Task.WaitAsync(cancellationToken);
            return new(HttpStatusCode.OK) { Content = new StringContent("{\"code\":0}") };
        }
    }

    public void Dispose() { SqliteConnection.ClearAllPools(); if (Directory.Exists(_directory)) Directory.Delete(_directory, true); }
}
