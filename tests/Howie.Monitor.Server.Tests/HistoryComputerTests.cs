using Howie.Monitor.Contracts;
using Howie.Monitor.Server;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Howie.Monitor.Server.Tests;

public sealed class HistoryComputerTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "howie-computer-tests-" + Guid.NewGuid().ToString("N"));
    private readonly DateTimeOffset _now = DateTimeOffset.FromUnixTimeMilliseconds(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
    private readonly string _session = Guid.NewGuid().ToString();
    private readonly TelemetryStore _store;
    public HistoryComputerTests() { _store = new(_directory); _store.Register(Registration("current", "current-computer")); }
    private DeviceRegistration Registration(string id, string name) => new(id, name, new("Windows", "11", [], [], [], [], [], [], []),
        new(false, false, null, false, 2, [], []), "test", "boot", _session);
    private TelemetrySample Sample(string id, double value, DateTimeOffset? at = null) => new(id, "boot", _session, 0, at ?? _now, 0, 0,
        [MetricReading.Present("cpu.usage", "cpu/total", "CPU", value, "%", "explicit-test")],
        [new(12, _now.AddMinutes(-1), id + ".exe", 20, 1024, MetricStatus.Available, null)], []);
    private void Sql(string text)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.Combine(_directory, "monitor.db") }.ToString());
        connection.Open(); using var command = connection.CreateCommand(); command.CommandText = text; command.ExecuteNonQuery();
    }

    [Fact] public void UnknownOldNameIsNotInventedAndDefaultQueriesStillUseCurrentComputer()
    {
        _store.Ingest(new(true, [Sample("unknown-old", 15), Sample("current", 65)]), _now);
        var computers = _store.HistoryComputers(_now);
        Assert.Equal(2, computers.Count); Assert.True(computers[0].IsCurrent); Assert.Equal("current-computer", computers[0].Name);
        Assert.Null(computers[1].Name); Assert.Equal(_now, computers[1].LastSampleAt);
        Assert.Equal("current", _store.ResolveHistoryComputer(null, _now));
        Assert.Equal("unknown-old", _store.ResolveHistoryComputer("unknown-old", _now));
        Assert.Null(_store.ResolveHistoryComputer("nonexistent", _now));
    }
    [Fact] public void IdenticalMetricAndSampleIdentityCannotMixComputerValuesOrPeakProcesses()
    {
        _store.Register(Registration("old", "same-name")); _store.Ingest(new(true, [Sample("old", 15)]), _now);
        Sql("DELETE FROM device WHERE id='old'"); _store.Register(Registration("current", "same-name"));
        _store.Ingest(new(false, [Sample("current", 80)]), _now);
        Assert.Equal(15, _store.QueryHistory("old", "cpu.usage", "cpu/total", "%", 1, _now).Maximum);
        Assert.Equal(80, _store.QueryHistory("current", "cpu.usage", "cpu/total", "%", 1, _now).Maximum);
        Assert.Equal("old.exe", Assert.Single(_store.HistoryProcesses("old", new(_session, 0))!.Processes).Name);
        Assert.Equal("current.exe", Assert.Single(_store.HistoryProcesses("current", new(_session, 0))!.Processes).Name);
        Assert.Equal("current", _store.Latest(_now).Sample!.DeviceId);
        var restarted = new TelemetryStore(_directory); Assert.Equal(2, restarted.HistoryComputers(_now).Count);
        Assert.All(restarted.HistoryComputers(_now), value => Assert.Equal("same-name", value.Name));
    }
    [Fact] public void ExpiredHistoryDisappearsBeforeBackgroundCleanupAndUnusedNamesArePruned()
    {
        _store.Register(Registration("expired", "expired-computer")); Sql("DELETE FROM device WHERE id='expired'");
        _store.Ingest(new(true, [Sample("expired", 15, _now.AddDays(-8))]), _now);
        Assert.Single(_store.HistoryComputers(_now)); Assert.Null(_store.ResolveHistoryComputer("expired", _now));
        _store.PruneHistory(_now);
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.Combine(_directory, "monitor.db") }.ToString());
        connection.Open(); using var command = connection.CreateCommand(); command.CommandText = "SELECT count(*) FROM history_computers";
        Assert.Equal(1L, command.ExecuteScalar());
    }
    [Fact] public void MigrationRestoresOnlyTheNameAvailableInRealRegistration()
    {
        _store.Ingest(new(true, [Sample("unregistered-old", 15)]), _now); Sql("DROP TABLE history_computers");
        var restarted = new TelemetryStore(_directory); var computers = restarted.HistoryComputers(_now);
        Assert.Equal("current-computer", computers[0].Name); Assert.Null(computers[1].Name);
    }
    [Fact] public void RenamingSameIdentityKeepsOneComputerAndItsHistory()
    {
        _store.Ingest(new(false, [Sample("current", 30)]), _now); _store.Register(Registration("current", "renamed-computer"));
        var computer = Assert.Single(_store.HistoryComputers(_now));
        Assert.Equal("renamed-computer", computer.Name); Assert.Equal(_now, computer.FirstSampleAt);
        Assert.Equal(1, _store.QueryHistory("current", "cpu.usage", "cpu/total", "%", 1, _now).ValidSamples);
    }
    public void Dispose() { SqliteConnection.ClearAllPools(); if (Directory.Exists(_directory)) Directory.Delete(_directory, true); }
}
