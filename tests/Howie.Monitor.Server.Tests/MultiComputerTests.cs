using System.Text.Json;
using Howie.Monitor.Contracts;
using Howie.Monitor.Server;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Howie.Monitor.Server.Tests;

public sealed class MultiComputerTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "howie-multi-" + Guid.NewGuid().ToString("N"));
    private readonly IDataProtector _protector = new EphemeralDataProtectionProvider().CreateProtector("multi-test");
    private readonly DeviceCredential _original = new("first", Guid.NewGuid().ToString());
    private readonly DateTimeOffset _now = DateTimeOffset.UtcNow;
    private readonly string _session = Guid.NewGuid().ToString();
    private readonly TelemetryStore _store;
    private string Auth(DeviceCredential credential) => "Bearer " + credential.Token;
    private DeviceRegistration Registration(string id, string? session = null) => new(id, "same-computer-name",
        new("Windows", "11", [], [], [], [], [], [], []), new(false, false, null, false, 2, [], []), "test", "boot", session ?? _session);
    private TelemetrySample Sample(string id, double cpu, long sequence = 0) => new(id, "boot", _session, sequence, _now, 0, 0,
        [MetricReading.Present("cpu.usage", "cpu/total", "CPU", cpu, "%", "explicit-test"),
         MetricReading.Present("memory.usage", "memory", "Memory", 50, "%", "explicit-test")],
        [new(12, _now.AddMinutes(-1), id + ".exe", cpu, 1024, MetricStatus.Available, null)], []);
    public MultiComputerTests()
    {
        _store = new(_directory); _store.InitializeEnrollment(_original, _protector);
        _store.RegisterAuthenticated(Auth(_original), Registration(_original.DeviceId), _now);
        _store.BeatAuthenticated(Auth(_original), new(_original.DeviceId, "boot", _session, 0, 0, 0, null), _now);
        _store.IngestAuthenticated(Auth(_original), new(false, [Sample(_original.DeviceId, 10)]), _now);
    }
    private (DeviceCredential Credential, string Request) Add(bool preserve = false, DeviceCredential? existing = null)
    {
        var code = _store.CreateEnrollmentCode(new(false, null, true), _now).Code!;
        string request = Guid.NewGuid().ToString();
        var claim = _store.ClaimEnrollment(new(code.Code, request, "same-computer-name", preserve ? existing!.DeviceId : null,
            preserve ? existing!.Token : null), _now);
        Assert.Null(claim.Error);
        return (new(claim.Credential!.DeviceId, claim.Credential.Token), request);
    }
    private void Stage(DeviceCredential credential, double value)
    {
        Assert.Null(_store.RegisterAuthenticated(Auth(credential), Registration(credential.DeviceId), _now).Error);
        Assert.Null(_store.BeatAuthenticated(Auth(credential), new(credential.DeviceId, "boot", _session, 0, 0, 0, null), _now).Error);
        Assert.Null(_store.IngestAuthenticated(Auth(credential), new(false, [Sample(credential.DeviceId, value)]), _now).Error);
    }
    private DeviceCredential Activate(double value)
    {
        var (credential, request) = Add(); Stage(credential, value);
        Assert.Null(_store.ActivateEnrollment(Auth(credential), request, _now).Error); return credential;
    }

    [Fact] public void AddingComputersPreservesEveryCredentialAndIsolatesIdenticalSampleIdentity()
    {
        var second = Activate(40); var third = Activate(80);
        foreach (var pair in new[] { (_original, 10d), (second, 40d), (third, 80d) })
        {
            Assert.True(_store.AuthenticateIngest(Auth(pair.Item1), _now));
            var latest = _store.Latest(_now, pair.Item1.DeviceId);
            Assert.Equal(pair.Item1.DeviceId, latest.Sample!.DeviceId); Assert.Equal("online", latest.ConnectionStatus);
            Assert.Equal(pair.Item2, latest.Sample.Metrics[0].Value);
            Assert.Equal(pair.Item2, _store.QueryHistory(pair.Item1.DeviceId, "cpu.usage", "cpu/total", "%", 1, _now).Maximum);
            Assert.Equal(pair.Item1.DeviceId + ".exe", Assert.Single(_store.HistoryProcesses(pair.Item1.DeviceId, new(_session, 0))!.Processes).Name);
            Assert.Equal(pair.Item2, Assert.Single(_store.History("cpu.usage", "cpu/total", _now.AddMinutes(-1), _now, "%", pair.Item1.DeviceId)).Value);
        }
        Assert.Equal(3, _store.SampleCount()); Assert.Equal(3, _store.Computers(_now).Count);
        Assert.Equal(_original.DeviceId, _store.Device()!.DeviceId);
        Assert.Null(_store.Device("unknown")); Assert.Null(_store.Latest(_now, "unknown").Sample);
    }

    [Fact] public void CredentialsAndIndependentSnapshotsSurviveRestartWithoutReimportingLegacyState()
    {
        var second = Activate(40);
        _store.IngestAuthenticated(Auth(_original), new(false, [Sample(_original.DeviceId, 55, 1)]), _now);
        var restarted = new TelemetryStore(_directory); restarted.InitializeEnrollment(_original, _protector);
        Assert.True(restarted.AuthenticateIngest(Auth(second), _now)); Assert.True(restarted.AuthenticateIngest(Auth(_original), _now));
        Assert.Equal(55, restarted.Latest(_now, _original.DeviceId).Sample!.Metrics[0].Value);
        Assert.Equal(40, restarted.Latest(_now, second.DeviceId).Sample!.Metrics[0].Value);
        Assert.Equal(3, restarted.SampleCount());
    }

    [Fact] public void OfflineComputerDoesNotChangeOtherComputersConnectionOrData()
    {
        var second = Activate(40); var later = _now.AddSeconds(40);
        _store.BeatAuthenticated(Auth(second), new(second.DeviceId, "boot", _session, 1, 0, 0, null), later);
        _store.IngestAuthenticated(Auth(second), new(false, [Sample(second.DeviceId, 60, 1) with { SampledAt = later }]), later);
        var computers = _store.Computers(later);
        Assert.Equal("disconnected", computers.Single(value => value.DeviceId == _original.DeviceId).ConnectionStatus);
        Assert.Null(computers.Single(value => value.DeviceId == _original.DeviceId).CpuPercent);
        Assert.Equal("online", computers.Single(value => value.DeviceId == second.DeviceId).ConnectionStatus);
        Assert.Equal(60, computers.Single(value => value.DeviceId == second.DeviceId).CpuPercent);
    }

    [Fact] public void ExplicitLegacyReplacementDoesNotRevokeOtherComputers()
    {
        var second = Activate(40);
        var code = _store.CreateEnrollmentCode(new(true, _original.DeviceId), _now).Code!;
        string request = Guid.NewGuid().ToString();
        var candidate = _store.ClaimEnrollment(new(code.Code, request, "same-computer-name"), _now).Credential!;
        var replacement = new DeviceCredential(candidate.DeviceId, candidate.Token); Stage(replacement, 70);
        Assert.Null(_store.ActivateEnrollment(Auth(replacement), request, _now).Error);
        Assert.False(_store.AuthenticateIngest(Auth(_original), _now)); Assert.True(_store.AuthenticateIngest(Auth(second), _now));
        Assert.Equal(replacement.DeviceId, _store.Device()!.DeviceId); Assert.Equal(2, _store.Computers(_now).Count);
        var restarted = new TelemetryStore(_directory); restarted.InitializeEnrollment(_original, _protector);
        Assert.False(restarted.AuthenticateIngest(Auth(_original), _now)); Assert.True(restarted.AuthenticateIngest(Auth(second), _now));
    }

    [Fact] public void AdoptionRotatesOnlyThatComputersCredentialAndPreservesItsHistory()
    {
        var second = Activate(40); var (adopted, request) = Add(true, second);
        Assert.Equal(second.DeviceId, adopted.DeviceId);
        string newSession = Guid.NewGuid().ToString();
        _store.RegisterAuthenticated(Auth(adopted), Registration(adopted.DeviceId, newSession), _now);
        _store.BeatAuthenticated(Auth(adopted), new(adopted.DeviceId, "boot", newSession, 1, 0, 0, null), _now);
        _store.IngestAuthenticated(Auth(adopted), new(false, [Sample(adopted.DeviceId, 70) with { SessionId = newSession }]), _now);
        Assert.Null(_store.ActivateEnrollment(Auth(adopted), request, _now).Error);
        Assert.True(_store.AuthenticateIngest(Auth(_original), _now)); Assert.False(_store.AuthenticateIngest(Auth(second), _now));
        Assert.Equal(2, _store.Computers(_now).Count);
        Assert.Equal(2, _store.QueryHistory(second.DeviceId, "cpu.usage", "cpu/total", "%", 1, _now).ValidSamples);
    }

    [Fact] public void CredentialsCannotWriteAnotherComputersRegistrationHeartbeatOrSamples()
    {
        var second = Activate(40);
        Assert.NotNull(_store.RegisterAuthenticated(Auth(second), Registration(_original.DeviceId), _now).Error);
        Assert.NotNull(_store.BeatAuthenticated(Auth(second), new(_original.DeviceId, "boot", _session, 0, 0, 0, null), _now).Error);
        Assert.NotNull(_store.IngestAuthenticated(Auth(second), new(false, [Sample(_original.DeviceId, 99)]), _now).Error);
        Assert.Equal(10, _store.Latest(_now, _original.DeviceId).Sample!.Metrics[0].Value);
    }

    [Fact] public async Task ConcurrentUploadsRemainPartitionedWithOverlappingSequences()
    {
        var second = Activate(40);
        await Task.WhenAll(new[] { _original, second }.Select(credential => Task.Run(() =>
        {
            for (int i = 1; i <= 12; i++) Assert.Null(_store.IngestAuthenticated(Auth(credential),
                new(false, [Sample(credential.DeviceId, credential == _original ? 20 : 80, i)]), _now).Error);
        })));
        Assert.Equal(26, _store.SampleCount());
        Assert.Equal(20, _store.Latest(_now, _original.DeviceId).Sample!.Metrics[0].Value);
        Assert.Equal(80, _store.Latest(_now, second.DeviceId).Sample!.Metrics[0].Value);
    }

    [Fact] public void RealLegacyDatabaseMigratesAtomicallyAndOnlyOnce()
    {
        string legacy = Path.Combine(_directory, "legacy"); Directory.CreateDirectory(legacy);
        using (var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.Combine(legacy, "monitor.db") }.ToString()))
        {
            db.Open(); using var command = db.CreateCommand();
            command.CommandText = """
                CREATE TABLE device(id TEXT PRIMARY KEY,registration TEXT NOT NULL);
                CREATE TABLE samples(session TEXT NOT NULL,sequence INTEGER NOT NULL,sampled INTEGER NOT NULL,payload TEXT NOT NULL,PRIMARY KEY(session,sequence));
                CREATE TABLE latest(id INTEGER PRIMARY KEY CHECK(id=1),session TEXT NOT NULL,sequence INTEGER NOT NULL,received INTEGER NOT NULL,payload TEXT NOT NULL);
                CREATE TABLE heartbeat(id INTEGER PRIMARY KEY CHECK(id=1),received INTEGER NOT NULL,payload TEXT NOT NULL);
                INSERT INTO device VALUES('first',$registration);
                INSERT INTO samples VALUES($session,0,$time,$sample);
                INSERT INTO latest VALUES(1,$session,0,$time,$sample);
                INSERT INTO heartbeat VALUES(1,$time,$heartbeat);
                """;
            command.Parameters.AddWithValue("$registration", JsonSerializer.Serialize(Registration("first"), ProtocolJson.Options));
            command.Parameters.AddWithValue("$sample", JsonSerializer.Serialize(Sample("first", 25), ProtocolJson.Options));
            command.Parameters.AddWithValue("$heartbeat", JsonSerializer.Serialize(new Heartbeat("first", "boot", _session, 0, 0, 0, null), ProtocolJson.Options));
            command.Parameters.AddWithValue("$session", _session); command.Parameters.AddWithValue("$time", _now.ToUnixTimeMilliseconds());
            command.ExecuteNonQuery();
        }
        var migrated = new TelemetryStore(legacy); migrated.InitializeEnrollment(_original, _protector);
        Assert.True(migrated.AuthenticateIngest(Auth(_original), _now));
        Assert.Equal(25, migrated.Latest(_now).Sample!.Metrics[0].Value); Assert.Equal("online", migrated.Latest(_now).ConnectionStatus);
        migrated.IngestAuthenticated(Auth(_original), new(false, [Sample("first", 35, 1)]), _now);
        var repeated = new TelemetryStore(legacy); repeated.InitializeEnrollment(_original, _protector);
        Assert.Equal(35, repeated.Latest(_now).Sample!.Metrics[0].Value); Assert.Equal(2, repeated.SampleCount());
        Assert.Equal(2, repeated.QueryHistory("first", "cpu.usage", "cpu/total", "%", 1, _now).ValidSamples);
    }

    public void Dispose() { SqliteConnection.ClearAllPools(); if (Directory.Exists(_directory)) Directory.Delete(_directory, true); }
}
