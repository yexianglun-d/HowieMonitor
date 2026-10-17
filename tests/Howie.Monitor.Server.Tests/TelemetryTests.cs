using Howie.Monitor.Server;
using Howie.Monitor.Agent;
using Howie.Monitor.Contracts;
using Xunit;

namespace Howie.Monitor.Server.Tests;

public sealed class TelemetryTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "howie-tests-" + Guid.NewGuid().ToString("N"));
    private readonly string _session = Guid.NewGuid().ToString();
    private readonly DateTimeOffset _now = DateTimeOffset.UtcNow;
    private readonly TelemetryStore _store;
    public TelemetryTests()
    {
        _store = new(_directory);
        _store.Register(new("device", "test", new("Windows", "11", [], [], [], [], [], [], []),
            new(false, false, null, false, 2, [], []), "test", "boot", _session));
    }
    private TelemetrySample Sample(long sequence, DateTimeOffset? time = null) => new("device", "boot", _session, sequence,
        time ?? _now, 1, 1, [MetricReading.Present("cpu.usage", "cpu/total", "CPU", 10, "%", "test")], [], []);

    [Fact] public void DuplicateAndOutOfOrderCannotRegressLatest()
    {
        _store.Ingest(new(false, [Sample(2)]), _now);
        _store.Ingest(new(false, [Sample(2), Sample(1)]), _now);
        Assert.Equal(2, _store.SampleCount()); Assert.Equal(2, _store.Latest(_now).Sample!.Sequence);
    }
    [Fact] public void BackfillAndOldSessionCannotReplaceCurrent()
    {
        _store.Ingest(new(false, [Sample(2)]), _now);
        _store.Ingest(new(true, [Sample(3)]), _now);
        _store.Ingest(new(false, [Sample(50) with { SessionId = Guid.NewGuid().ToString() }]), _now);
        Assert.Equal(2, _store.Latest(_now).Sample!.Sequence); Assert.Equal(3, _store.SampleCount());
    }
    [Fact] public void MissingMetricRemainsGapInHistory()
    {
        var missing = Sample(0) with { Metrics = [MetricReading.Missing("cpu.usage", "cpu/total", "CPU", "%", "test", MetricStatus.NotReady, "baseline")] };
        _store.Ingest(new(false, [missing, Sample(1)]), _now);
        var points = _store.History("cpu.usage", "cpu/total", _now.AddMinutes(-1), _now.AddSeconds(1));
        Assert.Null(points[0].Value); Assert.Equal(10, points[1].Value);
    }
    [Theory] [InlineData(8,"clockError")] [InlineData(-15,"delayed")] [InlineData(-40,"stale")]
    public void FirstObservationWithAbnormalTimeIsVisibleWithTruthfulStatus(int offsetSeconds, string expected)
    {
        _store.Ingest(new(false,[Sample(0,_now.AddSeconds(offsetSeconds))]),_now);
        var latest = _store.Latest(_now);
        Assert.NotNull(latest.Sample); Assert.Equal(expected,latest.DataStatus);
        Assert.Equal(_now.AddSeconds(offsetSeconds),latest.Sample!.SampledAt);
        Assert.Equal(_now.ToUnixTimeMilliseconds(),latest.ReceivedAt!.Value.ToUnixTimeMilliseconds());
    }
    [Fact] public void ClockCorrectionCanMoveTimestampBackWhileSequenceAdvances()
    {
        _store.Ingest(new(false,[Sample(1,_now.AddSeconds(8))]),_now);
        Assert.Equal("clockError",_store.Latest(_now).DataStatus);
        _store.Ingest(new(false,[Sample(2,_now.AddSeconds(-1))]),_now);
        Assert.Equal(2,_store.Latest(_now).Sample!.Sequence);
        Assert.Equal("fresh",_store.Latest(_now).DataStatus);
    }
    [Theory] [InlineData(0,"fresh")] [InlineData(11,"delayed")] [InlineData(31,"stale")] [InlineData(-6,"clockError")]
    public void FreshnessUsesActualSampleTime(int seconds, string expected) => Assert.Equal(expected, TelemetryStore.DataStatus(Sample(1), _session, _now.AddSeconds(seconds)));
    [Fact] public void InvalidMetricsAndForeignDeviceAreRejected()
    {
        Assert.NotNull(TelemetryValidation.Validate(new(false,[Sample(1) with { DeviceId="other" }]), "device", _now));
        Assert.NotNull(TelemetryValidation.Validate(new(false,[Sample(1) with { Metrics = [new("cpu","cpu","CPU",0,"%","test",MetricStatus.Unknown,"missing")] }]), "device", _now));
        Assert.NotNull(TelemetryValidation.Validate(new(false,[Sample(1) with { Metrics = [null!] }]), "device", _now));
    }
    [Fact] public void PersistedQueueOnlyDeletesAcknowledgedSamples()
    {
        var queue = new AgentQueue(Path.Combine(_directory,"agent")); queue.Add(Sample(1)); queue.Add(Sample(2));
        Assert.Equal(2,new AgentQueue(Path.Combine(_directory,"agent")).State().Count);
        queue.Acknowledge(new([new(_session,1)])); Assert.Equal(1,queue.State().Count); Assert.Equal(2,queue.Latest()!.Sequence);
    }
    [Fact] public void ExpiredQueueRecordsLossWithoutLosingCurrent()
    {
        var queue = new AgentQueue(Path.Combine(_directory,"agent")); queue.Add(Sample(1,_now.AddHours(-2))); queue.Add(Sample(2));
        Assert.Equal(1,queue.State().Dropped); Assert.NotNull(queue.State().DroppedFrom); Assert.Equal(2,queue.Latest()!.Sequence);
    }
    [Fact] public void IndependentBrowserSessionsRemainValidAndLogoutOnlyRevokesOne()
    {
        var credentials = new CredentialStore(Path.Combine(_directory,"auth"));
        string first = credentials.Login("A-valid-test-password",true)!;
        string second = credentials.Login("A-valid-test-password",false)!;
        Assert.True(credentials.ValidSession(first)); Assert.True(credentials.ValidSession(second));
        credentials.Logout(first); Assert.False(credentials.ValidSession(first)); Assert.True(credentials.ValidSession(second));
        Assert.True(new CredentialStore(Path.Combine(_directory,"auth")).ValidSession(second));
    }
    [Fact] public void OfflineInitializationPersistsAndCannotResetExistingPassword()
    {
        string path = Path.Combine(_directory,"initialization"); var credentials = new CredentialStore(path);
        Assert.False(credentials.Initialize("short")); Assert.False(credentials.IsSetup);
        Assert.True(credentials.Initialize("A-valid-test-password"));
        Assert.False(credentials.Initialize("Another-valid-password"));
        var restored = new CredentialStore(path); Assert.True(restored.IsSetup);
        Assert.Null(restored.Login("Another-valid-password",false));
        Assert.NotNull(restored.Login("A-valid-test-password",false));
    }
    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        string target = Path.GetFullPath(_directory);
        if (!target.StartsWith(Path.GetFullPath(Path.GetTempPath()),StringComparison.OrdinalIgnoreCase)
            || !Path.GetFileName(target).StartsWith("howie-tests-",StringComparison.Ordinal)) throw new InvalidOperationException("Unsafe cleanup target");
        if (Directory.Exists(target)) Directory.Delete(target,true);
    }
}
