using System.Text.Json;
using Howie.Monitor.Contracts;
using Howie.Monitor.Server;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Howie.Monitor.Server.Tests;

public sealed class ComputerRemovalTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "howie-removal-" + Guid.NewGuid().ToString("N"));
    private readonly IDataProtectionProvider _protection = new EphemeralDataProtectionProvider();
    private readonly DateTimeOffset _now = DateTimeOffset.UtcNow;
    private readonly string _session = Guid.NewGuid().ToString();
    private readonly DeviceCredential _first = new("first", Guid.NewGuid().ToString());
    private readonly TelemetryStore _telemetry;
    private readonly NotificationStore _notifications;
    private readonly TerminalBroker _terminals = new(NullLogger<TerminalBroker>.Instance);
    private readonly ComputerRemoval _removal;
    private DeviceRegistration Registration(string id) => new(id, "same-name", new("Windows", "11", [], [], [], [], [], [], []),
        new(false, false, null, false, 2, [], []), "test", "boot", _session);
    private TelemetrySample Sample(string id, long sequence = 0) => new(id, "boot", _session, sequence, _now, 0, 0,
        [MetricReading.Present("cpu.usage", "cpu/total", "CPU", 95, "%", "test"),
         MetricReading.Present("memory.usage", "memory", "Memory", 50, "%", "test")], [], []);
    private Heartbeat Beat(string id) => new(id, "boot", _session, 0, 0, 0, null);
    private static string Auth(DeviceCredential credential) => "Bearer " + credential.Token;
    public ComputerRemovalTests()
    {
        _telemetry = new(_directory); _telemetry.InitializeEnrollment(_first, _protection.CreateProtector("removal")); Stage(_first);
        _notifications = new(_directory, _protection);
        _removal = new(_telemetry, _notifications, _terminals);
    }
    private void Stage(DeviceCredential credential)
    {
        Assert.True(_telemetry.RegisterAuthenticated(Auth(credential), Registration(credential.DeviceId), _now).Authorized);
        Assert.Null(_telemetry.BeatAuthenticated(Auth(credential), Beat(credential.DeviceId), _now).Error);
        Assert.Null(_telemetry.IngestAuthenticated(Auth(credential), new(false, [Sample(credential.DeviceId)]), _now).Error);
    }
    private DeviceCredential Add(bool replacement = false)
    {
        var code = _telemetry.CreateEnrollmentCode(new(replacement, replacement ? _first.DeviceId : null, !replacement), _now).Code!;
        string request = Guid.NewGuid().ToString();
        var candidate = _telemetry.ClaimEnrollment(new(code.Code, request, "same-name"), _now).Credential!;
        var credential = new DeviceCredential(candidate.DeviceId, candidate.Token); Stage(credential);
        Assert.Null(_telemetry.ActivateEnrollment(Auth(credential), request, _now).Error); return credential;
    }
    private void Configure(params string[] ids)
    {
        var rule = new AlertRule(Guid.NewGuid().ToString(), "CPU", true, "metric", "cpu.usage", "cpu/total", "%", "above", 90, 2, 70, 2, true);
        Assert.Null(_notifications.Update(new(true, 5, "SCT" + "0123456789abcdefghijklmnopqrstuvwxyz", false, [])));
        foreach (string id in ids)
        {
            Assert.Null(_notifications.UpdateComputer(id, new([rule])));
            foreach (int seconds in new[] { 0, 2 })
            {
                var at = _now.AddSeconds(seconds);
                _notifications.Evaluate(Registration(id), new(Sample(id) with { SampledAt = at, Sequence = seconds }, at, at, at, "fresh", "online", null),
                    at, _now, "https://example.test");
            }
        }
    }
    private long Count(string table, string? id = null, string column = "device")
    {
        using var db = new SqliteConnection($"Data Source={Path.Combine(_directory, "monitor.db")}"); db.Open();
        using var command = db.CreateCommand(); command.CommandText = $"SELECT COUNT(*) FROM {table}" + (id is null ? "" : $" WHERE {column}=$id");
        if (id is not null) command.Parameters.AddWithValue("$id", id); return (long)command.ExecuteScalar()!;
    }
    private NotificationData NotificationData() => JsonSerializer.Deserialize<NotificationData>(
        File.ReadAllText(Path.Combine(_directory, "notifications.json")), ProtocolJson.Options)!;

    [Fact] public void RemovalPurgesOnlyTheExplicitComputerIncludingSameNameRulesAndHistory()
    {
        var second = Add(); Configure(_first.DeviceId, second.DeviceId);
        var dispatch = Assert.IsType<NotificationDispatch>(_notifications.BeginSend(_now.AddSeconds(2)));
        Assert.Equal(_first.DeviceId, dispatch.Event.ComputerId);
        var before = NotificationData(); Assert.Equal(2, before.States.Count);
        _removal.Remove(_first.DeviceId);
        foreach (string table in new[] { "samples", "computer_latest", "computer_heartbeat", "history_samples", "history_values", "history_metric_buckets", "history_computers", "enrollment_devices" })
        { Assert.Equal(0, Count(table, _first.DeviceId)); Assert.True(Count(table, second.DeviceId) > 0, table); }
        Assert.Null(_telemetry.Device(_first.DeviceId)); Assert.Equal(second.DeviceId, Assert.Single(_telemetry.Computers(_now)).DeviceId);
        Assert.Empty(_telemetry.HistoryMetrics(_first.DeviceId)); Assert.NotEmpty(_telemetry.HistoryMetrics(second.DeviceId));
        Assert.Empty(_notifications.ViewComputer(_first.DeviceId).Rules); Assert.Empty(_notifications.ViewComputer(_first.DeviceId).Events);
        Assert.Single(_notifications.ViewComputer(second.DeviceId).Rules); Assert.Single(_notifications.ViewComputer(second.DeviceId).Events);
        _notifications.Complete(dispatch.Event.Id, new("accepted", "late result"), _now.AddSeconds(4));
        var after = NotificationData(); Assert.Single(after.States); Assert.Equal(before.ProtectedKey, after.ProtectedKey);
        Assert.Equal(before.RequestsToday, after.RequestsToday); Assert.Equal(before.DailyLimit, after.DailyLimit); Assert.True(after.Enabled);
        Assert.Empty(_telemetry.PendingComputerRemovals());
    }

    [Fact] public void RemovingTheLastComputerNeverReimportsItsBootstrapCredentialAndNewPairingStillWorks()
    {
        _removal.Remove(_first.DeviceId); _removal.Remove(_first.DeviceId);
        var restarted = new TelemetryStore(_directory); restarted.InitializeEnrollment(_first, _protection.CreateProtector("removal"));
        Assert.False(restarted.AuthenticateIngest(Auth(_first), _now));
        Assert.False(restarted.RegisterAuthenticated(Auth(_first), Registration(_first.DeviceId), _now).Authorized);
        Assert.False(restarted.BeatAuthenticated(Auth(_first), Beat(_first.DeviceId), _now).Authorized);
        Assert.False(restarted.IngestAuthenticated(Auth(_first), new(false, [Sample(_first.DeviceId)]), _now).Authorized);
        Assert.Null(restarted.AuthorizeTerminal(Auth(_first), _now)); Assert.Empty(restarted.Computers(_now));
        Assert.Equal(1, Count("enrollment_active")); Assert.Equal(0, Count("enrollment_devices"));
        Assert.NotNull(restarted.CreateEnrollmentCode(new(false, null, true), _now).Code);
        var added = Add(); Assert.True(_telemetry.AuthenticateIngest(Auth(added), _now));
    }

    [Fact] public void HistoricalOnlyComputersAreRemovableWithoutDeletingTheirReplacement()
    {
        var replacement = Add(true); Assert.Null(_telemetry.Device(_first.DeviceId)); Assert.NotEmpty(_telemetry.HistoryMetrics(_first.DeviceId));
        _removal.Remove(_first.DeviceId);
        Assert.Equal(0, Count("history_computers", _first.DeviceId)); Assert.Empty(_telemetry.HistoryMetrics(_first.DeviceId));
        Assert.Equal(replacement.DeviceId, Assert.Single(_telemetry.Computers(_now)).DeviceId);
        Assert.True(_telemetry.AuthenticateIngest(Auth(replacement), _now));
    }

    [Theory] [InlineData(true)] [InlineData(false)]
    public void RemovingAComputerCancelsItsPendingAdoptionButPreservesAnUnrelatedPairing(bool related)
    {
        var code = _telemetry.CreateEnrollmentCode(new(false, null, true), _now).Code!;
        string request = Guid.NewGuid().ToString();
        var candidate = _telemetry.ClaimEnrollment(new(code.Code, request, "same-name", related ? _first.DeviceId : null, related ? _first.Token : null), _now).Credential!;
        _removal.Remove(_first.DeviceId);
        Assert.Equal(!related, _telemetry.AuthenticateIngest("Bearer " + candidate.Token, _now));
        Assert.Equal(related ? 0 : 1, Count("enrollment_pending"));
    }

    [Fact] public void FailedNotificationWriteRevokesAccessFirstAndResumesAfterRestart()
    {
        Configure(_first.DeviceId);
        using (var blocked = new FileStream(Path.Combine(_directory, "notifications.json.tmp"), FileMode.Create, FileAccess.ReadWrite, FileShare.None))
            Assert.Throws<IOException>(() => _removal.Remove(_first.DeviceId));
        Assert.False(_telemetry.AuthenticateIngest(Auth(_first), _now)); Assert.Single(_telemetry.PendingComputerRemovals());
        Assert.Single(_notifications.ViewComputer(_first.DeviceId).Rules); Assert.Equal(1, Count("samples", _first.DeviceId));
        bool ran = false; Assert.False(_telemetry.WithComputer(_first.DeviceId, _ => ran = true)); Assert.False(ran);
        var restarted = new TelemetryStore(_directory); restarted.InitializeEnrollment(_first, _protection.CreateProtector("removal"));
        var notifications = new NotificationStore(_directory, _protection);
        new ComputerRemoval(restarted, notifications, _terminals).Resume();
        Assert.Empty(restarted.PendingComputerRemovals()); Assert.Null(restarted.Device(_first.DeviceId));
        Assert.Empty(notifications.ViewComputer(_first.DeviceId).Rules); Assert.Equal(0, Count("samples"));
    }

    [Fact] public void LegacyCopiesAndTerminalPresenceAreRemovedOnlyForTheTargetComputer()
    {
        var second = Add();
        using (var db = new SqliteConnection($"Data Source={Path.Combine(_directory, "monitor.db")}"))
        {
            db.Open(); using var command = db.CreateCommand();
            command.CommandText = "INSERT INTO latest VALUES(1,$session,0,0,$sample); INSERT INTO heartbeat VALUES(1,0,$beat)";
            command.Parameters.AddWithValue("$session", _session); command.Parameters.AddWithValue("$sample", JsonSerializer.Serialize(Sample(_first.DeviceId), ProtocolJson.Options));
            command.Parameters.AddWithValue("$beat", JsonSerializer.Serialize(Beat(second.DeviceId), ProtocolJson.Options)); command.ExecuteNonQuery();
        }
        var capability = new TerminalCapability(_session, true, true, "PC\\admin", "S-1-5-21-1-2-3-1001", null, "1:test");
        _terminals.Poll(_first.DeviceId, capability, _now); _terminals.Poll(second.DeviceId, capability, _now);
        var firstSession = _terminals.Create(_first.DeviceId, "owner", 80, 24, _now).Session!;
        var secondSession = _terminals.Create(second.DeviceId, "owner", 80, 24, _now).Session!;
        _removal.Remove(_first.DeviceId);
        Assert.Equal(0, Count("latest")); Assert.Equal(1, Count("heartbeat"));
        Assert.True(firstSession.Closed); Assert.True(firstSession.Lifetime.IsCancellationRequested); Assert.False(secondSession.Closed);
        Assert.False(_terminals.Status(_first.DeviceId, _now).Available); Assert.True(_terminals.Status(second.DeviceId, _now).Available);
        _removal.Remove(second.DeviceId); Assert.Equal(0, Count("heartbeat"));
    }

    [Fact] public async Task ConcurrentLateUploadsCannotRestoreRemovedData()
    {
        await Task.WhenAll(Task.Run(() =>
        {
            for (int sequence = 1; sequence <= 20; sequence++)
                _telemetry.IngestAuthenticated(Auth(_first), new(false, [Sample(_first.DeviceId, sequence)]), _now);
        }), Task.Run(() => _removal.Remove(_first.DeviceId)));
        Assert.False(_telemetry.RegisterAuthenticated(Auth(_first), Registration(_first.DeviceId), _now).Authorized);
        Assert.Equal(0, Count("samples")); Assert.Equal(0, Count("history_values")); Assert.Empty(_telemetry.Computers(_now));
    }

    public void Dispose() { SqliteConnection.ClearAllPools(); if (Directory.Exists(_directory)) Directory.Delete(_directory, true); }
}
