using System.Text.Json;
using Howie.Monitor.Contracts;
using Howie.Monitor.Server;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Howie.Monitor.Server.Tests;

public sealed class HistoryTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "howie-tests-" + Guid.NewGuid().ToString("N"));
    private readonly string _session = Guid.NewGuid().ToString();
    private readonly DateTimeOffset _now = DateTimeOffset.UtcNow;
    private readonly TelemetryStore _store;
    public HistoryTests()
    {
        _store = new(_directory);
        _store.Register(new("device", "test", new("Windows", "11", [], [], [], [], [], [], []),
            new(false, false, null, false, 2, [], []), "test", "boot", _session));
    }
    private TelemetrySample Sample(long sequence, DateTimeOffset time, double? value, string unit = "%") =>
        new("device", "boot", _session, sequence, time, 1, 1,
            [value.HasValue ? MetricReading.Present("cpu.usage", "cpu/total", "CPU", value.Value, unit, "test")
                : MetricReading.Missing("cpu.usage", "cpu/total", "CPU", unit, "test", MetricStatus.NotReady, "missing")], [], []);
    private HistorySeries Query(int hours = 24, DateTimeOffset? now = null, string unit = "%") =>
        _store.QueryHistory("device", "cpu.usage", "cpu/total", unit, hours, now ?? _now);
    private static DateTimeOffset Align(DateTimeOffset time, int seconds) =>
        DateTimeOffset.FromUnixTimeMilliseconds(time.ToUnixTimeMilliseconds() / (seconds * 1000L) * (seconds * 1000L));

    [Fact] public void MeansIgnoreMissingAndPeakKeepsExactProcessIdentity()
    {
        var start = Align(_now.AddMinutes(-5), 60);
        var peak = Sample(2, start.AddSeconds(4), 90) with { Processes = [new(123, start, "peak.exe", 60, 4096, MetricStatus.Available, null)] };
        _store.Ingest(new(true, [Sample(0, start, 10), Sample(1, start.AddSeconds(2), null), peak]), _now);
        var series = Query(); var bucket = Assert.Single(series.Points, point => point.TotalSamples > 0);
        Assert.Equal(50, series.Average); Assert.Equal(10, series.Minimum); Assert.Equal(90, series.Maximum);
        Assert.Equal(2, bucket.ValidSamples); Assert.Equal(3, bucket.TotalSamples); Assert.Equal(4, bucket.CoverageSeconds);
        Assert.Equal(peak.SampledAt.ToUnixTimeMilliseconds(), series.PeakAt!.Value.ToUnixTimeMilliseconds());
        var processes = _store.HistoryProcesses("device", series.Peak!);
        Assert.Equal("peak.exe", Assert.Single(processes!.Processes).Name);
        Assert.Null(_store.HistoryProcesses("other", series.Peak!));
    }

    [Fact] public void DuplicateReplayAfterRawCleanupCannotIncreaseAggregateCounts()
    {
        var old = Sample(0, _now.AddHours(-2), 10);
        _store.Ingest(new(false, [old]), old.SampledAt);
        _store.Ingest(new(false, [Sample(1, _now, 30)]), _now);
        Assert.Equal(1, _store.SampleCount());
        _store.Ingest(new(true, [old, old]), _now);
        Assert.Equal(2, Query().ValidSamples); Assert.Equal(20, Query().Average);
        Assert.Equal(1, _store.SampleCount());
    }

    [Fact] public void LateBackfillUpdatesItsBucketWithoutRegressingLive()
    {
        _store.Ingest(new(false, [Sample(100, _now, 20)]), _now);
        _store.Ingest(new(true, [Sample(1, _now.AddHours(-3), 80)]), _now);
        Assert.Equal(100, _store.Latest(_now).Sample!.Sequence);
        Assert.Equal(80, Query().Maximum); Assert.Equal(2, Query().ValidSamples);
    }

    [Fact] public void FiveMinuteMeansWeightSamplesRatherThanMinuteMeans()
    {
        var start = Align(_now.AddMinutes(-10), 300);
        _store.Ingest(new(true, [Sample(1, start, 10), Sample(2, start.AddMinutes(1), 90),
            Sample(3, start.AddMinutes(1).AddSeconds(2), 90), Sample(4, start.AddMinutes(1).AddSeconds(4), 90)]), _now);
        var series = Query(168);
        Assert.Equal(300, series.IntervalSeconds); Assert.Equal(70, series.Average); Assert.Equal(4, series.ValidSamples);
        Assert.InRange(series.Points.Count, 2016, 2017);
    }

    [Fact] public void CoverageUsesUniqueTwoSecondSlotsIncludingHighBitSlots()
    {
        var start = Align(_now.AddMinutes(-10), 300);
        _store.Ingest(new(true, [Sample(0, start, 10), Sample(1, start.AddSeconds(1), 30),
            Sample(2, start.AddSeconds(126), 20), Sample(3, start.AddSeconds(128), 20), Sample(4, start.AddSeconds(298), 20)]), _now);
        var series = Query(168);
        Assert.Equal(5, series.ValidSamples); Assert.Equal(8, series.CoverageSeconds);
        Assert.Equal(20, series.Average);
    }

    [Fact] public void EmptyBucketsAndAbsentMetricRemainGaps()
    {
        var start = Align(_now.AddMinutes(-3), 60);
        _store.Ingest(new(true, [Sample(0, start, 10), Sample(1, start.AddSeconds(2), 20) with { Metrics = [] },
            Sample(2, start.AddMinutes(2), 30)]), _now);
        Assert.Null(Query().Points.Single(point => point.SampledAt == start.AddMinutes(1)).Value);
        Assert.Null(_store.History("cpu.usage", "cpu/total", start, _now)[1].Value);
        Assert.Equal(2, Query().ValidSamples); Assert.Equal(20, Query().Average);
    }

    [Fact] public void DifferentDevicesAndUnitsAreNeverMerged()
    {
        _store.Ingest(new(true, [Sample(1, _now.AddSeconds(-4), 20), Sample(2, _now.AddSeconds(-2), 100, "B"),
            Sample(3, _now, 90) with { DeviceId = "other" }]), _now);
        Assert.Equal(20, Query().Average); Assert.Equal(100, Query(unit: "B").Average);
        Assert.Equal(90, _store.QueryHistory("other", "cpu.usage", "cpu/total", "%", 24, _now).Average);
    }

    [Fact] public void ClockErrorNeverContributesValidHistoricalValues()
    {
        var sample = Sample(1, _now.AddSeconds(8), 90);
        _store.Ingest(new(false, [sample]), _now);
        var series = Query(now: _now.AddSeconds(10));
        Assert.Null(series.Average); Assert.Equal(0, series.ValidSamples); Assert.Equal(1, series.ClockErrorSamples);
        Assert.Equal("clockError", _store.Latest(_now).DataStatus);
    }

    [Fact] public void RestartPreservesAggregatesAndSevenDayRetention()
    {
        var sixDaysAgo = _now.AddDays(-6);
        _store.Ingest(new(false, [Sample(1, sixDaysAgo, 70)]), sixDaysAgo);
        var restored = new TelemetryStore(_directory);
        restored.PruneHistory(_now);
        Assert.Equal(0, restored.SampleCount());
        Assert.Equal(70, restored.QueryHistory("device", "cpu.usage", "cpu/total", "%", 168, _now).Average);
        Assert.NotNull(restored.HistoryProcesses("device", new(_session, 1)));
        restored.PruneHistory(_now.AddDays(2));
        Assert.Null(restored.QueryHistory("device", "cpu.usage", "cpu/total", "%", 168, _now.AddDays(2)).Average);
        Assert.Null(restored.HistoryProcesses("device", new(_session, 1)));
    }

    [Fact] public void LegacyDatabaseIsMigratedOnceWithItsActualValues()
    {
        string directory = Path.Combine(_directory, "legacy"); Directory.CreateDirectory(directory);
        using (var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.Combine(directory, "monitor.db") }.ToString()))
        {
            db.Open(); using var command = db.CreateCommand();
            command.CommandText = "CREATE TABLE samples(session TEXT NOT NULL,sequence INTEGER NOT NULL,sampled INTEGER NOT NULL,payload TEXT NOT NULL,PRIMARY KEY(session,sequence)); INSERT INTO samples VALUES($session,1,$time,$json)";
            command.Parameters.AddWithValue("$session", _session); command.Parameters.AddWithValue("$time", _now.AddMinutes(-5).ToUnixTimeMilliseconds());
            command.Parameters.AddWithValue("$json", JsonSerializer.Serialize(Sample(1, _now.AddMinutes(-5), 42), ProtocolJson.Options));
            command.ExecuteNonQuery();
        }
        var first = new TelemetryStore(directory); var second = new TelemetryStore(directory);
        Assert.Equal(42, first.QueryHistory("device", "cpu.usage", "cpu/total", "%", 24, _now).Average);
        Assert.Equal(1, second.QueryHistory("device", "cpu.usage", "cpu/total", "%", 24, _now).ValidSamples);
    }

    [Fact] public void InvalidHistoryRangesAreRejected() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => Query(1000));

    public void Dispose()
    {
        SqliteConnection.ClearAllPools(); string target = Path.GetFullPath(_directory);
        if (!target.StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase)
            || !Path.GetFileName(target).StartsWith("howie-tests-", StringComparison.Ordinal)) throw new InvalidOperationException("Unsafe cleanup target");
        if (Directory.Exists(target)) Directory.Delete(target, true);
    }
}
