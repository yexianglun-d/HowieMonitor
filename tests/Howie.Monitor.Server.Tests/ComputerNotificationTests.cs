using System.Text.Json;
using Howie.Monitor.Contracts;
using Howie.Monitor.Server;
using Microsoft.AspNetCore.DataProtection;
using Xunit;

namespace Howie.Monitor.Server.Tests;

public sealed class ComputerNotificationTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "howie-scoped-rules-" + Guid.NewGuid().ToString("N"));
    private readonly IDataProtectionProvider _protection = new EphemeralDataProtectionProvider();
    private readonly DateTimeOffset _now = DateTimeOffset.UtcNow;
    private readonly string _session = Guid.NewGuid().ToString();
    private AlertRule Rule => new(Guid.NewGuid().ToString(), "CPU", true, "metric", "cpu.usage", "cpu/total", "%", "above", 90, 4, 70, 4, true);
    private NotificationStore Create() => new(_directory, _protection, ["first", "second"]);
    private void Configure(NotificationStore store, AlertRule rule)
    {
        Assert.Null(store.Update(new(true, 5, "SCT" + "0123456789abcdefghijklmnopqrstuvwxyz", false, [])));
        foreach (string id in new[] { "first", "second" }) Assert.Null(store.UpdateComputer(id, new([rule])));
    }
    private void Observe(NotificationStore store, string id, int seconds, double cpu = 95)
    {
        var at = _now.AddSeconds(seconds);
        var device = new DeviceRegistration(id, "same-name", new("Windows", "11", [], [], [], [], [], [], []), new(false, false, null, false, 2, [], []), "test", "boot", _session);
        var sample = new TelemetrySample(id, "boot", _session, seconds, at, 0, 0,
            [MetricReading.Present("cpu.usage", "cpu/total", "CPU", cpu, "%", "test")], [], []);
        store.Evaluate(device, new(sample, at, at, at, "fresh", "online", null), at, _now, "https://monitor.example");
    }

    [Fact] public void EditingOneComputersRulesPreservesOtherComputersEvidenceAndQueuedTasks()
    {
        var store = Create(); var rule = Rule; Configure(store, rule);
        foreach (int seconds in new[] { 0, 2 }) { Observe(store, "first", seconds); Observe(store, "second", seconds); }
        Assert.Null(store.UpdateComputer("first", new([rule with { Threshold = 99 }])));
        Observe(store, "first", 4); Observe(store, "second", 4);
        Assert.Empty(store.ViewComputer("first").Events);
        var second = Assert.Single(store.ViewComputer("second").Events);
        Assert.Equal("second", second.ComputerId); Assert.Contains("computer=second", second.Description);
        Assert.Null(store.UpdateComputer("first", new([])));
        Assert.Equal("queued", Assert.Single(store.ViewComputer("second").Events).Status);
        Assert.Equal("second", Assert.IsType<NotificationDispatch>(store.BeginSend(_now.AddSeconds(5))).Event.ComputerId);
        Assert.Empty(store.View(_now).Events);
    }

    [Fact] public void SameRuleIdOnDifferentComputersDoesNotShareCancellationOrThresholds()
    {
        var store = Create(); var rule = Rule; Configure(store, rule);
        foreach (int seconds in new[] { 0, 2, 4 }) { Observe(store, "first", seconds); Observe(store, "second", seconds); }
        Assert.Null(store.UpdateComputer("first", new([])));
        Assert.Equal("cancelled", Assert.Single(store.ViewComputer("first").Events).Status);
        Assert.Equal("queued", Assert.Single(store.ViewComputer("second").Events).Status);
        Assert.Single(Create().ViewComputer("second").Rules);
        Assert.Empty(Create().ViewComputer("first").Rules);
    }

    [Fact] public void SharedLegacyRulesMigrateOnceAndNewComputersStartEmpty()
    {
        Directory.CreateDirectory(_directory); var rule = Rule;
        var legacy = new NotificationData { Rules = [rule], Events = [new("legacy", rule.Id, "trigger", "old", "unknown owner", _now, "queued")] };
        File.WriteAllText(Path.Combine(_directory, "notifications.json"), JsonSerializer.Serialize(legacy, ProtocolJson.Options));
        var store = Create();
        Assert.Equal(rule, Assert.Single(store.ViewComputer("first").Rules));
        Assert.Equal(rule, Assert.Single(store.ViewComputer("second").Rules));
        Assert.Equal("cancelled", Assert.Single(store.View(_now).Events).Status);
        Assert.Empty(store.ViewComputer("first").Events);
        Assert.Null(store.UpdateComputer("first", new([])));
        store = new(_directory, _protection, ["first", "second", "new"]);
        Assert.Empty(store.ViewComputer("first").Rules); Assert.Empty(store.ViewComputer("new").Rules);
        Assert.Single(store.ViewComputer("second").Rules); Assert.Empty(store.View(_now).Settings.Rules);
    }

    [Fact] public void InvalidAndFailedSavesPreserveBothComputersRulesAndEvents()
    {
        var store = Create(); var rule = Rule; Configure(store, rule);
        Assert.NotNull(store.UpdateComputer("first", new([rule, rule])));
        Assert.NotNull(store.UpdateComputer("first", new([rule with { RecoveryThreshold = 100 }])));
        foreach (int seconds in new[] { 0, 2, 4 }) { Observe(store, "first", seconds); Observe(store, "second", seconds); }
        using (var blocked = new FileStream(Path.Combine(_directory, "notifications.json.tmp"), FileMode.Create, FileAccess.ReadWrite, FileShare.None))
            Assert.Throws<IOException>(() => store.UpdateComputer("first", new([])));
        Assert.Single(store.ViewComputer("first").Rules); Assert.Single(store.ViewComputer("second").Rules);
        Assert.Equal("queued", Assert.Single(store.ViewComputer("first").Events).Status);
        Assert.Equal("queued", Assert.Single(store.ViewComputer("second").Events).Status);
        Assert.Single(Create().ViewComputer("first").Rules);
    }

    [Fact] public void NewComputerCannotInheritAnExistingComputersRules()
    {
        var store = Create(); Configure(store, Rule);
        foreach (int seconds in new[] { 0, 2, 4 }) Observe(store, "new", seconds);
        Assert.Empty(store.ViewComputer("new").Rules); Assert.Empty(store.ViewComputer("new").Events);
        Assert.NotNull(store.Update(new(true, 5, null, false, [Rule])));
    }

    [Fact] public void OneComputersEventLimitCannotEvictAnotherComputersHistory()
    {
        var store = Create(); Configure(store, Rule with { DurationSeconds = 2, RecoverySeconds = 2 });
        Observe(store, "second", 0); Observe(store, "second", 2);
        for (int index = 0; index < 212; index++) Observe(store, "first", index * 2, index % 4 < 2 ? 95 : 50);
        Assert.Equal(100, store.ViewComputer("first").Events.Count);
        Assert.Single(store.ViewComputer("second").Events);
    }

    [Fact] public void EventRetentionKeepsInFlightDeliveryUntilItsResultIsRecorded()
    {
        var store = Create(); Configure(store, Rule with { DurationSeconds = 2, RecoverySeconds = 2 });
        Observe(store, "first", 0); Observe(store, "first", 2);
        var dispatch = Assert.IsType<NotificationDispatch>(store.BeginSend(_now.AddSeconds(2)));
        for (int index = 2; index < 212; index++) Observe(store, "first", index * 2, index % 4 < 2 ? 95 : 50);
        Assert.Equal(100, store.ViewComputer("first").Events.Count);
        Assert.Contains(store.ViewComputer("first").Events, value => value.Id == dispatch.Event.Id && value.Status == "sending");
        store.Complete(dispatch.Event.Id, new("accepted", "test"), _now.AddMinutes(8));
        Assert.Contains(store.ViewComputer("first").Events, value => value.Id == dispatch.Event.Id && value.Status == "accepted");
    }

    public void Dispose() { if (Directory.Exists(_directory)) Directory.Delete(_directory, true); }
}
