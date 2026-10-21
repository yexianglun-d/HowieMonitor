using Howie.Monitor.Contracts;
using Howie.Monitor.Server;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Howie.Monitor.Server.Tests;

public sealed class AgentUpdateTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "howie-update-tests-" + Guid.NewGuid().ToString("N"));
    private readonly DateTimeOffset _now = DateTimeOffset.UtcNow;
    private readonly string _session = Guid.NewGuid().ToString();
    private readonly AgentBuild _current = new("0.2.0", new('a', 40), 1);
    private readonly AgentRelease _release = new("0.2.1", new('b', 40), 1, new('d', 64), new('c', 64), 256);
    private readonly TelemetryStore _store;
    private const string Device = "update-device", Authorization = "Bearer update-device-private-token";
    public AgentUpdateTests()
    {
        _store = new(_directory);
        _store.InitializeEnrollment(new(Device, "update-device-private-token"), new EphemeralDataProtectionProvider().CreateProtector("updates"));
        Register(_current, _session, _now); Fresh(_session, _now);
    }
    private void Register(AgentBuild? build, string session, DateTimeOffset at, bool enabled = true)
    {
        var device = new DeviceRegistration(Device, "my-computer", new("Windows", "11", [], [], [], [], [], [], []),
            new(false, false, null, false, 2, [], []), build?.Version ?? "0.1.0", "boot", session, build, build is null ? null : new(1, enabled));
        Assert.Null(_store.RegisterAuthenticated(Authorization, device, at).Error);
    }
    private void Fresh(string session, DateTimeOffset at, bool memory = true)
    {
        Assert.Null(_store.BeatAuthenticated(Authorization, new(Device, "boot", session, 1, 0, 0, null), at).Error);
        MetricReading[] metrics = memory
            ? [MetricReading.Present("cpu.usage", "cpu/total", "CPU", 20, "%", "test"), MetricReading.Present("memory.usage", "memory", "内存", 40, "%", "test")]
            : [MetricReading.Present("cpu.usage", "cpu/total", "CPU", 20, "%", "test")];
        Assert.Null(_store.IngestAuthenticated(Authorization, new(false, [new(Device, "boot", session, at.ToUnixTimeMilliseconds(), at, 0, 0, metrics, [], [])]), at).Error);
    }
    private AgentUpdateOperation Create() => Assert.IsType<AgentUpdateOperation>(_store.CreateUpdate(new(Device, "unused-in-store", _session, _release.Sha256), _release, _now).Operation);
    private void Start(AgentUpdateOperation operation)
    {
        Assert.NotNull(_store.PollUpdate(Authorization, _session, _now).Ticket);
        Assert.True(_store.ReportUpdate(Authorization, new(operation.Ticket.Id, "installing"), _now).Accepted);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LegacyOrUnapprovedAgentsCannotReceiveUpdates(bool legacy)
    {
        Register(legacy ? null : _current, _session, _now, false);
        Assert.False(_store.UpdateStatus(Device, _release, _now)!.CanUpdate);
        Assert.Null(_store.CreateUpdate(new(Device, "unused", _session, _release.Sha256), _release, _now).Operation);
    }
    [Fact] public void ReleaseAndSessionArePinnedAndOnlyOneTaskPersists()
    {
        Assert.Null(_store.CreateUpdate(new(Device, "unused", Guid.NewGuid().ToString(), _release.Sha256), _release, _now).Operation);
        Assert.Null(_store.CreateUpdate(new(Device, "unused", _session, new('f', 64)), _release, _now).Operation);
        var operation = Create();
        Assert.Null(_store.CreateUpdate(new(Device, "unused", _session, _release.Sha256), _release, _now).Operation);
        var reopened = new TelemetryStore(_directory);
        Assert.Equal(operation.Ticket.Id, reopened.UpdateStatus(Device, _release, _now)!.Operation!.Ticket.Id);
        Assert.Null(_store.PollUpdate(Authorization, Guid.NewGuid().ToString(), _now).Ticket);
        Assert.False(_store.PollUpdate("Bearer someone-elses-token", _session, _now).Authorized);
    }
    [Fact] public void StaleDataAndSameVersionBuildReplacementAreBlocked()
    {
        Assert.False(_store.UpdateStatus(Device, _release, _now.AddMinutes(1))!.CanUpdate);
        Assert.False(_store.UpdateStatus(Device, _release with { Version = _current.Version }, _now)!.CanUpdate);
        Assert.False(_store.UpdateStatus(Device, null, _now)!.CanUpdate);
    }
    [Fact] public void SuccessRequiresNewBuildSessionHeartbeatAndBothBaseMetrics()
    {
        var operation = Create(); Start(operation);
        Assert.True(_store.ReportUpdate(Authorization, new(operation.Ticket.Id, "verifying"), _now).Accepted);
        Assert.False(_store.ReportUpdate(Authorization, new(operation.Ticket.Id, "succeeded"), _now).Accepted);
        string next = Guid.NewGuid().ToString(); DateTimeOffset at = _now.AddSeconds(1);
        Register(new(_release.Version, _release.BuildId, 1), next, at); Fresh(next, at, false);
        Assert.False(_store.ReportUpdate(Authorization, new(operation.Ticket.Id, "succeeded"), at).Accepted);
        Fresh(next, at.AddSeconds(1));
        Assert.True(_store.ReportUpdate(Authorization, new(operation.Ticket.Id, "succeeded"), at.AddSeconds(1)).Accepted);
        Assert.False(_store.HasActiveUpdate(Device));
    }
    [Fact] public void RollbackNeedsTheOriginalBuildRunningInANewSession()
    {
        var operation = Create(); Start(operation);
        Assert.True(_store.ReportUpdate(Authorization, new(operation.Ticket.Id, "rollingBack"), _now).Accepted);
        Assert.False(_store.ReportUpdate(Authorization, new(operation.Ticket.Id, "rolledBack"), _now).Accepted);
        string next = Guid.NewGuid().ToString(); DateTimeOffset at = _now.AddSeconds(2);
        Register(_current, next, at); Fresh(next, at);
        Assert.True(_store.ReportUpdate(Authorization, new(operation.Ticket.Id, "rolledBack"), at).Accepted);
    }
    [Fact] public void ExpiredUnclaimedTasksAreReleased()
    {
        Create(); Assert.False(_store.HasActiveUpdate("other-device"));
        Assert.Equal("failed", _store.UpdateStatus(Device, _release, _now.AddMinutes(21))!.Operation!.Phase);
    }
    [Fact] public void InProgressTasksRemainLockedWhenTheirResultIsUnknown()
    {
        var operation = Create(); Start(operation);
        Assert.Equal("installing", _store.UpdateStatus(Device, _release, _now.AddMinutes(21))!.Operation!.Phase);
    }
    [Fact] public void RemovalRevokesTheUpdateChannelAndDoesNotRecreateTasks()
    {
        var operation = Create(); _store.BeginComputerRemoval(Device); _store.CompleteComputerRemoval(Device);
        Assert.False(_store.PollUpdate(Authorization, _session, _now).Authorized);
        Assert.False(_store.ReportUpdate(Authorization, new(operation.Ticket.Id, "installing"), _now).Authorized);
        Assert.Null(_store.UpdateStatus(Device, _release, _now));
    }
    [Fact] public void RestartBeforeInstallationRequiresNewConsent()
    {
        var operation = Create(); Assert.NotNull(_store.PollUpdate(Authorization, _session, _now).Ticket);
        string next = Guid.NewGuid().ToString(); Register(_current, next, _now); Fresh(next, _now);
        Assert.False(_store.ReportUpdate(Authorization, new(operation.Ticket.Id, "installing"), _now).Accepted);
        Assert.Equal("failed", _store.UpdateStatus(Device, _release, _now)!.Operation!.Phase);
    }
    [Fact] public void PriorInstallPermissionCannotBeReusedAfterTheSourceSessionChanges()
    {
        var operation = Create(); Start(operation);
        string next = Guid.NewGuid().ToString(); Register(_current, next, _now); Fresh(next, _now);
        Assert.False(_store.ReportUpdate(Authorization, new(operation.Ticket.Id, "installing"), _now).Accepted);
    }
    public void Dispose() { SqliteConnection.ClearAllPools(); Directory.Delete(_directory, true); }
}
