using Howie.Monitor.Contracts;
using Howie.Monitor.Server;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Howie.Monitor.Server.Tests;

public sealed class EnrollmentTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "howie-enrollment-tests-" + Guid.NewGuid().ToString("N"));
    private readonly IDataProtector _protector = new EphemeralDataProtectionProvider().CreateProtector("test-enrollment");
    private readonly DeviceCredential _old = new("old-device", "old-private-token");
    private readonly DateTimeOffset _now = DateTimeOffset.UtcNow;
    private readonly string _oldSession = Guid.NewGuid().ToString();
    private readonly string _newSession = Guid.NewGuid().ToString();
    private readonly TelemetryStore _store;
    private string OldAuthorization => "Bearer " + _old.Token;
    private DeviceRegistration Registration(string device, string name, string session) => new(device, name,
        new("Windows", "11", [], [], [], [], [], [], []), new(false, false, null, false, 2, [], []), "test", "boot", session);
    private TelemetrySample Sample(string device, string session, long sequence = 0, DateTimeOffset? at = null, bool memory = true) =>
        new(device, "boot", session, sequence, at ?? _now, 0, 0,
            memory ? [MetricReading.Present("cpu.usage", "cpu/total", "CPU", 20, "%", "test"), MetricReading.Present("memory.usage", "memory", "memory", 50, "%", "test")]
                : [MetricReading.Present("cpu.usage", "cpu/total", "CPU", 20, "%", "test")], [], []);
    public EnrollmentTests()
    {
        _store = new(_directory); _store.InitializeEnrollment(_old, _protector);
        Assert.Null(_store.RegisterAuthenticated(OldAuthorization, Registration(_old.DeviceId, "old-computer", _oldSession), _now).Error);
        Assert.NotNull(_store.IngestAuthenticated(OldAuthorization, new(false, [Sample(_old.DeviceId, _oldSession)]), _now).Acknowledgement);
    }
    private EnrollmentCode Code() => Assert.IsType<EnrollmentCode>(_store.CreateEnrollmentCode(new(true, _old.DeviceId), _now).Code);
    private (EnrollmentCredential Credential, string RequestId) Claim(bool preserve = false)
    {
        var code = Code(); string request = Guid.NewGuid().ToString();
        var result = _store.ClaimEnrollment(new(code.Code, request, preserve ? "old-computer" : "new-computer", preserve ? _old.DeviceId : null, preserve ? _old.Token : null), _now);
        Assert.Null(result.Error); return (Assert.IsType<EnrollmentCredential>(result.Credential), request);
    }
    private void Stage(EnrollmentCredential credential, bool memory = true, int offset = 0)
    {
        string authorization = "Bearer " + credential.Token;
        Assert.Null(_store.RegisterAuthenticated(authorization, Registration(credential.DeviceId, credential.PreservesHistory ? "old-computer" : "new-computer", _newSession), _now).Error);
        Assert.Null(_store.BeatAuthenticated(authorization, new(credential.DeviceId, "boot", _newSession, 0, 0, 0, null), _now).Error);
        Assert.Null(_store.IngestAuthenticated(authorization, new(false, [Sample(credential.DeviceId, _newSession, at: _now.AddSeconds(offset), memory: memory)]), _now).Error);
    }

    [Fact] public void LegacyCredentialContinuesUntilActivationAndPendingDoesNotChangeHistory()
    {
        var (credential, _) = Claim(); Stage(credential);
        Assert.Equal(_old.DeviceId, _store.Device()!.DeviceId); Assert.Equal(_oldSession, _store.Latest(_now).Sample!.SessionId);
        Assert.Equal(1, _store.SampleCount()); Assert.True(_store.AuthenticateIngest(OldAuthorization, _now));
        Assert.Equal("ready", _store.EnrollmentStatus("Bearer " + credential.Token, _now)!.State);
        Assert.Equal(0, _store.QueryHistory(credential.DeviceId, "cpu.usage", "cpu/total", "%", 1, _now).ValidSamples);
        var computer = Assert.Single(_store.HistoryComputers(_now));
        Assert.Equal("old-computer", computer.Name); Assert.True(computer.IsCurrent);
    }
    [Fact] public void ActivationChangesAllAuthoritativeStateAndRevokesOldCredential()
    {
        var (credential, request) = Claim(); Stage(credential);
        var result = _store.ActivateEnrollment("Bearer " + credential.Token, request, _now); Assert.Null(result.Error);
        Assert.Equal("active", result.Progress!.State); Assert.Equal(credential.DeviceId, _store.Device()!.DeviceId);
        Assert.Equal(credential.DeviceId, _store.Latest(_now).Sample!.DeviceId); Assert.Equal(_newSession, _store.Latest(_now).Agent!.SessionId);
        Assert.False(_store.AuthenticateIngest(OldAuthorization, _now));
        Assert.False(_store.RegisterAuthenticated(OldAuthorization, Registration(_old.DeviceId, "old-computer", _oldSession), _now).Authorized);
        Assert.False(_store.IngestAuthenticated(OldAuthorization, new(false, [Sample(_old.DeviceId, _oldSession, 100)]), _now).Authorized);
        Assert.Equal(1, _store.QueryHistory(_old.DeviceId, "cpu.usage", "cpu/total", "%", 1, _now).ValidSamples);
        Assert.Equal(1, _store.QueryHistory(credential.DeviceId, "cpu.usage", "cpu/total", "%", 1, _now).ValidSamples);
        Assert.Null(_store.ActivateEnrollment("Bearer " + credential.Token, request, _now).Error);
        Assert.Equal(2, _store.SampleCount());
        var computers = _store.HistoryComputers(_now);
        Assert.Equal("new-computer", computers[0].Name); Assert.True(computers[0].IsCurrent);
        Assert.Equal("old-computer", computers[1].Name); Assert.False(computers[1].IsCurrent);
    }
    [Fact] public void SameRequestRetriesReceiveSameCredentialAndAnotherRequestCannotReuseCode()
    {
        var code = Code(); var claim = new EnrollmentClaim(code.Code, Guid.NewGuid().ToString(), "new-computer");
        var first = _store.ClaimEnrollment(claim, _now); var second = _store.ClaimEnrollment(claim, _now.AddSeconds(5));
        Assert.Equal(first.Credential, second.Credential);
        Assert.Null(_store.ClaimEnrollment(claim with { RequestId = Guid.NewGuid().ToString() }, _now).Credential);
        Assert.Null(_store.ClaimEnrollment(claim with { ComputerName = "another" }, _now).Credential);
    }
    [Fact] public void CodeAndCandidateExpiryNeverDisableCurrentDevice()
    {
        var code = Code(); Assert.Null(_store.ClaimEnrollment(new(code.Code, Guid.NewGuid().ToString(), "new-computer"), _now.AddMinutes(10)).Credential);
        var (credential, request) = Claim(); Stage(credential);
        Assert.False(_store.AuthenticateIngest("Bearer " + credential.Token, _now.AddMinutes(20)));
        Assert.False(_store.ActivateEnrollment("Bearer " + credential.Token, request, _now.AddMinutes(20)).Authorized);
        Assert.True(_store.AuthenticateIngest(OldAuthorization, _now.AddMinutes(20))); Assert.Equal(_old.DeviceId, _store.Device()!.DeviceId);
    }
    [Fact] public void RevocationOrNewCodeCancelsCandidateAndKeepsActiveIdentity()
    {
        var (credential, _) = Claim(); _store.RevokeEnrollment(); Assert.False(_store.AuthenticateIngest("Bearer " + credential.Token, _now));
        (credential, _) = Claim(); Code(); Assert.False(_store.AuthenticateIngest("Bearer " + credential.Token, _now));
        Assert.True(_store.AuthenticateIngest(OldAuthorization, _now));
    }
    [Theory] [InlineData(8, true)] [InlineData(-15, true)] [InlineData(0, false)]
    public void ClockDelayOrMissingBasisCannotActivate(int offset, bool memory)
    {
        var (credential, request) = Claim(); Stage(credential, memory, offset);
        Assert.NotNull(_store.ActivateEnrollment("Bearer " + credential.Token, request, _now).Error);
        Assert.True(_store.AuthenticateIngest(OldAuthorization, _now)); Assert.Equal(_old.DeviceId, _store.Device()!.DeviceId);
    }
    [Fact] public void BackfillWithoutLiveEvidenceCannotActivate()
    {
        var (credential, request) = Claim(); string auth = "Bearer " + credential.Token;
        _store.RegisterAuthenticated(auth, Registration(credential.DeviceId, "new-computer", _newSession), _now);
        _store.BeatAuthenticated(auth, new(credential.DeviceId, "boot", _newSession, 0, 0, 0, null), _now);
        Assert.NotNull(_store.IngestAuthenticated(auth, new(true, [Sample(credential.DeviceId, _newSession)]), _now).Acknowledgement);
        Assert.NotNull(_store.ActivateEnrollment(auth, request, _now).Error);
    }
    [Fact] public void NewSessionClearsEarlierCandidateEvidence()
    {
        var (credential, request) = Claim(); Stage(credential);
        _store.RegisterAuthenticated("Bearer " + credential.Token, Registration(credential.DeviceId, "new-computer", Guid.NewGuid().ToString()), _now);
        Assert.NotNull(_store.ActivateEnrollment("Bearer " + credential.Token, request, _now).Error);
    }
    [Fact] public void RepeatedInitializationCannotRestoreRevokedBootstrapToken()
    {
        var (credential, request) = Claim(); Stage(credential); _store.ActivateEnrollment("Bearer " + credential.Token, request, _now);
        var restarted = new TelemetryStore(_directory); restarted.InitializeEnrollment(_old, _protector);
        Assert.False(restarted.AuthenticateIngest(OldAuthorization, _now)); Assert.True(restarted.AuthenticateIngest("Bearer " + credential.Token, _now));
        Assert.Equal(credential.DeviceId, restarted.Device()!.DeviceId);
        Assert.Null(restarted.ActivateEnrollment("Bearer " + credential.Token, request, _now).Error);
    }
    [Fact] public void PendingCredentialIsEncryptedAndResumesAcrossRestart()
    {
        var code = Code(); var claim = new EnrollmentClaim(code.Code, Guid.NewGuid().ToString(), "new-computer");
        var credential = _store.ClaimEnrollment(claim, _now).Credential!;
        var restarted = new TelemetryStore(_directory); restarted.InitializeEnrollment(_old, _protector);
        Assert.Equal(credential, restarted.ClaimEnrollment(claim, _now).Credential);
        using var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.Combine(_directory, "monitor.db") }.ToString()); db.Open();
        using var command = db.CreateCommand(); command.CommandText = "SELECT payload FROM enrollment_pending";
        string json = (string)command.ExecuteScalar()!; Assert.DoesNotContain(credential.Token, json); Assert.DoesNotContain(code.Code.Replace("-", ""), json);
    }
    [Fact] public void AdoptionPreservesHistoryOnlyWithMatchingExistingProof()
    {
        var (credential, request) = Claim(true); Assert.True(credential.PreservesHistory); Assert.Equal(_old.DeviceId, credential.DeviceId);
        Stage(credential); Assert.Null(_store.ActivateEnrollment("Bearer " + credential.Token, request, _now).Error);
        Assert.Equal(2, _store.QueryHistory(_old.DeviceId, "cpu.usage", "cpu/total", "%", 1, _now).ValidSamples);
        Assert.False(_store.AuthenticateIngest(OldAuthorization, _now));
    }
    [Fact] public void ExplicitReplacementAndValidComputerNameAreRequired()
    {
        Assert.Null(_store.CreateEnrollmentCode(new(false, _old.DeviceId), _now).Code);
        Assert.Null(_store.CreateEnrollmentCode(new(true, "wrong"), _now).Code);
        var code = Code(); var claim = new EnrollmentClaim(code.Code, Guid.NewGuid().ToString(), "other", _old.DeviceId, _old.Token);
        Assert.Null(_store.ClaimEnrollment(claim, _now).Credential);
        Assert.Null(_store.ClaimEnrollment(claim with { ComputerName = "old-computer", ExistingToken = "wrong" }, _now).Credential);
    }
    [Fact] public void WrongDeviceOrInvalidSessionCannotWriteAndUnregisteredNullHeartbeatIsRejected()
    {
        var (credential, _) = Claim(); string auth = "Bearer " + credential.Token;
        Assert.NotNull(_store.RegisterAuthenticated(auth, Registration("wrong", "new-computer", _newSession), _now).Error);
        Assert.NotNull(_store.BeatAuthenticated(auth, new(credential.DeviceId, "boot", null!, 0, 0, 0, null), _now).Error);
        Assert.NotNull(_store.IngestAuthenticated(auth, new(false, [Sample("wrong", _newSession)]), _now).Error);
        Assert.Equal(_old.DeviceId, _store.Device()!.DeviceId);
    }
    public void Dispose() { SqliteConnection.ClearAllPools(); if (Directory.Exists(_directory)) Directory.Delete(_directory, true); }
}
