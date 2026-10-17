using System.Net;
using Microsoft.AspNetCore.DataProtection;
using Howie.Monitor.Contracts;
using Howie.Monitor.Server;
using Xunit;

namespace Howie.Monitor.Server.Tests;

public sealed class NotificationTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "howie-notification-tests-" + Guid.NewGuid().ToString("N"));
    private readonly IDataProtectionProvider _protection = new EphemeralDataProtectionProvider();
    private readonly DateTimeOffset _now = new(2026, 10, 12, 4, 0, 0, TimeSpan.Zero);
    // 分段构造固定的虚构测试键，避免公开源码检查将其误认为真实凭据。
    private const string Key = "SCT" + "0123456789abcdefghijklmnopqrstuvwxyz";
    private const string PublicUrl = "https://monitor.example";
    private readonly string _session = Guid.NewGuid().ToString();
    private AlertRule Rule => new(Guid.NewGuid().ToString(), "CPU", true, "metric", "cpu.usage", "cpu/total", "%", "above", 90, 4, 70, 4, true);
    private DeviceRegistration Device => new("device", "computer", new("Windows", "11", [], [], [], [], [], [], []), new(false, false, null, false, 2, [], []), "test", "boot", _session);
    private LatestView View(long sequence, int seconds, double? value = 95, string status = "fresh", string? session = null)
    {
        var at = _now.AddSeconds(seconds);
        MetricReading metric = value.HasValue ? MetricReading.Present("cpu.usage", "cpu/total", "CPU", value.Value, "%", "test")
            : MetricReading.Missing("cpu.usage", "cpu/total", "CPU", "%", "test", MetricStatus.Unsupported, "missing");
        var sample = new TelemetrySample("device", "boot", session ?? _session, sequence, at, 0, 0, [metric], [], []);
        return new(sample, at, at, at, status, "online", null);
    }
    private NotificationStore Store() => new(_directory, _protection);
    private void Configure(NotificationStore store, AlertRule rule, bool enabled = true, int limit = 5)
        => Assert.Null(store.Update(new(enabled, limit, Key, false, [rule])));
    private void Evaluate(NotificationStore store, int seconds, long sequence, double? value = 95, string status = "fresh")
        => store.Evaluate(Device, View(sequence, seconds, value, status), _now.AddSeconds(seconds), _now, PublicUrl);

    [Fact] public void ContinuousTriggerRecoveryAndRepeatedSamplesDoNotDuplicate()
    {
        var rule = Rule; var state = new AlertRuleState();
        Assert.Null(AlertRules.Evaluate(rule, state, View(0, 0), _now, _now));
        Assert.Null(AlertRules.Evaluate(rule, state, View(0, 0), _now.AddSeconds(20), _now));
        Assert.Null(AlertRules.Evaluate(rule, state, View(1, 2), _now.AddSeconds(2), _now));
        Assert.Equal("trigger", AlertRules.Evaluate(rule, state, View(2, 4), _now.AddSeconds(4), _now));
        Assert.Null(AlertRules.Evaluate(rule, state, View(3, 6), _now.AddSeconds(6), _now));
        Assert.Null(AlertRules.Evaluate(rule, state, View(4, 8, 60), _now.AddSeconds(8), _now));
        Assert.Null(AlertRules.Evaluate(rule, state, View(5, 10, 60), _now.AddSeconds(10), _now));
        Assert.Equal("recovery", AlertRules.Evaluate(rule, state, View(6, 12, 60), _now.AddSeconds(12), _now));
    }

    [Theory] [InlineData("fresh", null)] [InlineData("stale", 95d)] [InlineData("delayed", 95d)] [InlineData("clockError", 95d)]
    public void MissingOrNonRealtimeBreaksEvidence(string status, double? value)
    {
        var store = Store(); Configure(store, Rule);
        Evaluate(store, 0, 0); Evaluate(store, 2, 1, value, status); Evaluate(store, 4, 2); Evaluate(store, 6, 3);
        Assert.Empty(store.View(_now).Events);
        Evaluate(store, 8, 4); Assert.Single(store.View(_now).Events);
    }

    [Fact] public void MissingDoesNotRecoverAndGapOrNewSessionRestartsObservation()
    {
        var rule = Rule; var state = new AlertRuleState { Triggered = true };
        Assert.Null(AlertRules.Evaluate(rule, state, View(0, 0, null), _now, _now)); Assert.True(state.Triggered);
        Assert.Null(AlertRules.Evaluate(rule, state, View(1, 2, 60), _now.AddSeconds(2), _now));
        Assert.Null(AlertRules.Evaluate(rule, state, View(2, 10, 60), _now.AddSeconds(10), _now));
        Assert.Null(AlertRules.Evaluate(rule, state, View(0, 12, 60, session: Guid.NewGuid().ToString()), _now.AddSeconds(12), _now));
        Assert.True(state.Triggered);
    }

    [Fact] public void LowValueConditionAndHysteresisAreApplied()
    {
        var rule = Rule with { Comparison = "below", Threshold = 10, RecoveryThreshold = 20, DurationSeconds = 2, RecoverySeconds = 2 };
        var state = new AlertRuleState();
        Assert.Null(AlertRules.Evaluate(rule, state, View(0, 0, 5), _now, _now));
        Assert.Equal("trigger", AlertRules.Evaluate(rule, state, View(1, 2, 5), _now.AddSeconds(2), _now));
        Assert.Null(AlertRules.Evaluate(rule, state, View(2, 4, 15), _now.AddSeconds(4), _now)); Assert.True(state.Triggered);
        Assert.Null(AlertRules.Evaluate(rule, state, View(3, 6, 25), _now.AddSeconds(6), _now));
        Assert.Equal("recovery", AlertRules.Evaluate(rule, state, View(4, 8, 25), _now.AddSeconds(8), _now));
    }

    [Fact] public void OfflineRestartGraceDoesNotCauseFalseRecovery()
    {
        var rule = Rule with { Kind = "offline", DurationSeconds = 4, RecoverySeconds = 4 };
        var state = new AlertRuleState { Triggered = true };
        var view = new LatestView(null, _now, null, null, "waiting", "disconnected", null);
        Assert.Null(AlertRules.Evaluate(rule, state, view, _now.AddSeconds(10), _now)); Assert.True(state.Triggered);
        Assert.Null(AlertRules.Evaluate(rule, state, view, _now.AddSeconds(14), _now)); Assert.True(state.Triggered);
        var connected = view with { LastHeartbeat = _now.AddSeconds(16) };
        Assert.Null(AlertRules.Evaluate(rule, state, connected, _now.AddSeconds(16), _now));
        Assert.Equal("recovery", AlertRules.Evaluate(rule, state, connected, _now.AddSeconds(20), _now));
        Assert.Null(AlertRules.Evaluate(rule, state, view, _now.AddSeconds(30), _now));
        Assert.Equal("trigger", AlertRules.Evaluate(rule, state, view, _now.AddSeconds(34), _now));
    }

    [Fact] public void RestartKeepsTriggeredStateAndDoesNotResendAcceptedEvent()
    {
        var store = Store(); Configure(store, Rule); Evaluate(store, 0, 0); Evaluate(store, 2, 1); Evaluate(store, 4, 2);
        var dispatch = Assert.IsType<NotificationDispatch>(store.BeginSend(_now.AddSeconds(4)));
        store.Complete(dispatch.Event.Id, new("accepted", "accepted"), _now.AddSeconds(5));
        store = Store(); Evaluate(store, 6, 3); Evaluate(store, 8, 4); Evaluate(store, 10, 5);
        Assert.Single(store.View(_now).Events); Assert.Null(store.BeginSend(_now.AddMinutes(2)));
    }

    [Fact] public void RestartDoesNotReusePendingDuration()
    {
        var store = Store(); Configure(store, Rule); Evaluate(store, 0, 0); Evaluate(store, 2, 1); store = Store();
        Evaluate(store, 4, 2); Evaluate(store, 6, 3); Assert.Empty(store.View(_now).Events);
        Evaluate(store, 8, 4); Assert.Single(store.View(_now).Events);
    }

    [Fact] public void TurningOffCancelsQueuedTasksButExplicitTestIsAllowed()
    {
        var rule = Rule; var store = Store(); Configure(store, rule); Evaluate(store, 0, 0); Evaluate(store, 2, 1); Evaluate(store, 4, 2);
        Assert.Null(store.Update(new(false, 5, null, false, [rule])));
        Assert.Equal("cancelled", Assert.Single(store.View(_now).Events).Status); Assert.Null(store.BeginSend(_now));
        Assert.Null(store.QueueTest(_now, PublicUrl)); Assert.NotNull(store.BeginSend(_now));
    }

    [Fact] public void CrashDuringSendIsUnknownAndCountsBudgetWithoutBlindRetry()
    {
        var store = Store(); Configure(store, Rule, false, 1); Assert.Null(store.QueueTest(_now, PublicUrl)); Assert.NotNull(store.BeginSend(_now));
        store = Store(); var view = store.View(_now);
        Assert.Equal("unknown", Assert.Single(view.Events).Status); Assert.Equal(1, view.RequestsToday);
        Assert.Null(store.BeginSend(_now.AddMinutes(2))); Assert.NotNull(store.QueueTest(_now.AddMinutes(2), PublicUrl));
        Assert.Null(store.QueueTest(_now.AddDays(1), PublicUrl)); Assert.NotNull(store.BeginSend(_now.AddDays(1)));
    }

    [Fact] public void CredentialIsEncryptedAndWrongKeysFailWithoutLeaking()
    {
        var store = Store(); Configure(store, Rule, false); Assert.DoesNotContain(Key, File.ReadAllText(Path.Combine(_directory, "notifications.json")));
        Assert.True(store.View(_now).Settings.HasCredential);
        Assert.Null(store.QueueTest(_now, PublicUrl));
        store = new(_directory, new EphemeralDataProtectionProvider()); Assert.Null(store.BeginSend(_now));
        Assert.Equal("failed", Assert.Single(store.View(_now).Events).Status);
        Assert.DoesNotContain(Key, store.View(_now).Events[0].Detail!);
    }

    [Fact] public void RateLimitExpiryAndRecoveryToggleAreHonored()
    {
        var store = Store(); Configure(store, Rule with { NotifyRecovery = false }); Evaluate(store, 0, 0); Evaluate(store, 2, 1); Evaluate(store, 4, 2);
        var dispatch = Assert.IsType<NotificationDispatch>(store.BeginSend(_now.AddSeconds(4))); store.Complete(dispatch.Event.Id, new("accepted", "ok"), _now.AddSeconds(4));
        Evaluate(store, 6, 3, 50); Evaluate(store, 8, 4, 50); Evaluate(store, 10, 5, 50); Assert.Single(store.View(_now).Events);
        Assert.Null(store.QueueTest(_now.AddSeconds(11), PublicUrl)); Assert.Null(store.BeginSend(_now.AddSeconds(12)));
        Assert.Null(store.BeginSend(_now.AddMinutes(11))); Assert.Contains(store.View(_now).Events, item => item.Status == "expired");
    }

    [Fact] public void DeviceChangeAndInvalidRulesCannotReuseEvidence()
    {
        var store = Store(); var rule = Rule; Configure(store, rule); Evaluate(store, 0, 0); Evaluate(store, 2, 1);
        store.Evaluate(Device with { DeviceId = "other" }, View(2, 4) with { DataStatus = "stale" }, _now.AddSeconds(4), _now, PublicUrl);
        Assert.Empty(store.View(_now).Events);
        Assert.NotNull(store.Update(new(true, 5, "sctp-invalid", false, [rule])));
        Assert.NotNull(store.Update(new(true, 5, null, false, [rule, rule])));
        Assert.NotNull(store.Update(new(true, 5, null, false, [rule with { RecoveryThreshold = 95 }])));
    }

    [Fact] public void InterleavedComputersKeepIndependentContinuityAndQueuedEvents()
    {
        var store = Store(); Configure(store, Rule);
        for (int sequence = 0; sequence <= 2; sequence++)
        {
            int seconds = sequence * 2;
            Evaluate(store, seconds, sequence);
            var other = View(sequence, seconds);
            store.Evaluate(Device with { DeviceId = "other", Name = "second-computer" },
                other with { Sample = other.Sample! with { DeviceId = "other" } }, _now.AddSeconds(seconds), _now, PublicUrl);
        }
        var events = store.View(_now).Events;
        Assert.Equal(2, events.Count); Assert.All(events, item => Assert.Equal("queued", item.Status));
        Assert.Contains(events, item => item.Description.Contains("second-computer", StringComparison.Ordinal));
        Assert.Contains(events, item => item.Description.Contains("电脑：computer\n", StringComparison.Ordinal));
        store = Store(); Assert.Equal(2, store.View(_now).Events.Count);
        Assert.NotNull(store.BeginSend(_now.AddSeconds(5))); Assert.Null(store.BeginSend(_now.AddSeconds(6)));
    }

    [Fact] public void AnotherComputersSamplesCannotCompleteOrRecoverThisComputersRule()
    {
        var store = Store(); Configure(store, Rule); Evaluate(store, 0, 0); Evaluate(store, 2, 1);
        var other = View(2, 4);
        store.Evaluate(Device with { DeviceId = "other" }, other with { Sample = other.Sample! with { DeviceId = "other" } }, _now.AddSeconds(4), _now, PublicUrl);
        Assert.Empty(store.View(_now).Events);
        Evaluate(store, 4, 2); Assert.Single(store.View(_now).Events);
        for (int sequence = 3; sequence <= 5; sequence++)
        {
            var low = View(sequence, sequence * 2, 50);
            store.Evaluate(Device with { DeviceId = "other" }, low with { Sample = low.Sample! with { DeviceId = "other" } }, _now.AddSeconds(sequence * 2), _now, PublicUrl);
        }
        Assert.Single(store.View(_now).Events);
    }

    [Theory] [InlineData("{\"code\":0}", "accepted")] [InlineData("{\"code\":40001}", "failed")]
    [InlineData("invalid", "unknown")] [InlineData("{}", "unknown")] [InlineData("[]", "unknown")] [InlineData("{\"code\":\"0\"}", "unknown")]
    public async Task OfficialProtocolAcceptsOnlyCodeZeroAndNeverEchoesProviderText(string response, string status)
    {
        var item = new AlertEvent("event", null, "test", "test title", "description", _now, "sending");
        var handler = new Handler(async request =>
        {
            Assert.Equal("https://sctapi.ftqq.com/" + Key + ".send", request.RequestUri!.AbsoluteUri);
            Assert.Equal(HttpMethod.Post, request.Method); Assert.Equal("application/x-www-form-urlencoded", request.Content!.Headers.ContentType!.MediaType);
            string form = await request.Content.ReadAsStringAsync(); Assert.Contains("title=test+title", form); Assert.Contains("desp=description", form);
            return new(HttpStatusCode.OK) { Content = new StringContent(response) };
        });
        using var sender = new ServerChanSender(handler);
        var result = await sender.Send(new(item, Key), CancellationToken.None); Assert.Equal(status, result.Status); Assert.DoesNotContain(Key, result.Detail);
    }

    [Fact] public async Task TimeoutDoesNotClaimFailureOrRetryAndErrorResponseIsRedacted()
    {
        var dispatch = new NotificationDispatch(new("event", null, "test", "test", "description", _now, "sending"), Key);
        using var timeout = new ServerChanSender(new Handler(_ => throw new TaskCanceledException(Key)));
        Assert.Equal("unknown", (await timeout.Send(dispatch, CancellationToken.None)).Status);
        using var rejection = new ServerChanSender(new Handler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.TooManyRequests) { Content = new StringContent(Key) })));
        var result = await rejection.Send(dispatch, CancellationToken.None); Assert.Equal("failed", result.Status); Assert.DoesNotContain(Key, result.Detail);
    }
    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request);
    }
    [Fact] public void RealKeyFilesRemainDecryptableAcrossProviderRestart()
    {
        var keys = new DirectoryInfo(Path.Combine(_directory, "keys"));
        var store = new NotificationStore(_directory, DataProtectionProvider.Create(keys)); Configure(store, Rule, false);
        store = new(_directory, DataProtectionProvider.Create(keys)); Assert.Null(store.QueueTest(_now, PublicUrl));
        Assert.Equal(Key, Assert.IsType<NotificationDispatch>(store.BeginSend(_now)).SendKey);
    }
    [Fact] public void FailedReservationSaveCannotStartSendOrConsumeBudget()
    {
        var store = Store(); Configure(store, Rule, false); Assert.Null(store.QueueTest(_now, PublicUrl));
        string temp = Path.Combine(_directory, "notifications.json.tmp");
        using (var blocked = new FileStream(temp, FileMode.Create, FileAccess.ReadWrite, FileShare.None))
            Assert.Throws<IOException>(() => store.BeginSend(_now));
        Assert.Equal(0, store.View(_now).RequestsToday); Assert.Equal("queued", Assert.Single(store.View(_now).Events).Status);
        Assert.NotNull(store.BeginSend(_now));
    }
    [Fact] public void FailedSettingsSaveDoesNotEnableNotificationsInMemory()
    {
        var store = Store(); Configure(store, Rule, false);
        string temp = Path.Combine(_directory, "notifications.json.tmp");
        using (var blocked = new FileStream(temp, FileMode.Create, FileAccess.ReadWrite, FileShare.None))
            Assert.Throws<IOException>(() => store.Update(new(true, 5, null, false, [Rule])));
        Assert.False(store.View(_now).Settings.Enabled); Assert.False(Store().View(_now).Settings.Enabled);
    }
    public void Dispose() { if (Directory.Exists(_directory)) Directory.Delete(_directory, true); }
}
