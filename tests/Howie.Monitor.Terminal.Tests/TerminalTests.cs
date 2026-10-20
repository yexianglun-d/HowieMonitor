using Howie.Monitor.Contracts;
using Howie.Monitor.Server;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Howie.Monitor.Terminal.Tests;

public sealed class TerminalTests
{
    private readonly DateTimeOffset _now = DateTimeOffset.UtcNow;
    private readonly TerminalBroker _broker = new(NullLogger<TerminalBroker>.Instance);
    private readonly TerminalCapability _capability = new(Guid.NewGuid().ToString(), true, true, "PC\\admin", "S-1-5-21-1-2-3-1001", null, "1:1234");
    private TerminalSession Create(string computer = "first", string owner = "browser-owner")
    {
        _broker.Poll(computer, _capability, _now);
        var result = _broker.Create(computer, owner, 80, 24, _now);
        Assert.Null(result.Error); return Assert.IsType<TerminalSession>(result.Session);
    }
    [Fact] public void SessionsAreIsolatedByComputerAndBrowserLoginAndCannotBeReattached()
    {
        var first = Create(); var second = Create("second");
        Assert.Null(_broker.ClaimBrowser(first.Id, "second", "browser-owner", _now));
        Assert.Null(_broker.ClaimBrowser(first.Id, "first", "other-login", _now));
        Assert.False(_broker.Close(first.Id, "second", "browser-owner"));
        Assert.False(_broker.Close(first.Id, "first", "other-login"));
        Assert.Same(first, _broker.ClaimBrowser(first.Id, "first", "browser-owner", _now));
        Assert.Null(_broker.ClaimBrowser(first.Id, "first", "browser-owner", _now));
        Assert.True(_broker.Close(first.Id, "first", "browser-owner"));
        Assert.True(first.Closed); Assert.False(second.Closed);
    }
    [Fact] public void OffersRequireAnAttachedBrowserAndAreNeverReplayed()
    {
        var session = Create();
        Assert.Null(_broker.Poll("first", _capability, _now));
        Assert.Null(_broker.ClaimAgent(session.Id, "first", _capability.AgentSessionId, _now));
        _broker.ClaimBrowser(session.Id, "first", "browser-owner", _now);
        Assert.Equal(session.Id, _broker.Poll("first", _capability, _now)!.SessionId);
        Assert.Null(_broker.Poll("first", _capability, _now));
        Assert.Null(_broker.ClaimAgent(session.Id, "second", _capability.AgentSessionId, _now));
        Assert.Null(_broker.ClaimAgent(session.Id, "first", Guid.NewGuid().ToString(), _now));
        Assert.Same(session, _broker.ClaimAgent(session.Id, "first", _capability.AgentSessionId, _now));
        Assert.Null(_broker.ClaimAgent(session.Id, "first", _capability.AgentSessionId, _now));
    }
    [Fact] public void LocalLogoutAndLogonReplacementImmediatelyEndTheOldSession()
    {
        var session = Create();
        _broker.Poll("first", _capability with { LogonId = "2:5678" }, _now);
        Assert.True(session.Closed);
        var replacement = Create();
        _broker.Poll("first", _capability with { Available = false }, _now);
        Assert.True(replacement.Closed); Assert.False(_broker.Status("first", _now).Available);
    }
    [Fact] public void OldOrUnavailableCapabilitiesCannotCreateSessionsAndParallelSessionsAreRejected()
    {
        Assert.Null(_broker.Create("first", "owner", 80, 24, _now).Session);
        _broker.Poll("first", _capability, _now);
        Assert.Null(_broker.Create("first", "owner", 80, 24, _now.AddSeconds(16)).Session);
        var session = Create();
        Assert.Null(_broker.Create("first", "owner", 80, 24, _now).Session);
        Assert.Null(_broker.Create("second", "owner", 1, 1, _now).Session);
        Assert.False(session.Closed);
    }
    [Theory]
    [InlineData("login")]
    [InlineData("agent")]
    [InlineData("offline")]
    [InlineData("handshake")]
    [InlineData("idle")]
    [InlineData("hour")]
    public void SweepTerminatesExpiredAuthorityAndStalledConnections(string cause)
    {
        var session = Create();
        var later = cause switch { "offline" => _now.AddSeconds(16), "handshake" => _now.AddSeconds(21), "idle" => _now.AddMinutes(6), "hour" => _now.AddMinutes(61), _ => _now };
        if (cause != "offline") _broker.Poll("first", _capability, later);
        if (cause is "idle" or "hour") session.Ready = true;
        if (cause == "hour") session.Touch(later);
        _broker.Sweep(later, _ => cause != "login", _ => cause == "agent" ? "new-agent" : _capability.AgentSessionId);
        Assert.True(session.Closed); Assert.True(session.Lifetime.IsCancellationRequested);
        Assert.False(session.Agent.Writer.TryWrite(new("input", "must-not-execute")));
    }
    [Fact] public async Task OutputStopsAtTheWindowUntilTheRendererAcknowledgesAndCancellationReleasesTheWait()
    {
        var session = Create();
        Assert.False(session.AcknowledgeOutput());
        for (int i = 0; i < 32; i++) await session.ReserveOutput(session.Lifetime.Token);
        var pending = session.ReserveOutput(session.Lifetime.Token);
        Assert.False(pending.IsCompleted);
        Assert.True(session.AcknowledgeOutput()); await pending.WaitAsync(TimeSpan.FromSeconds(2));
        var canceled = session.ReserveOutput(session.Lifetime.Token);
        session.End("closed");
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => canceled);
    }
    [Theory]
    [InlineData(null)] [InlineData("S-1-5-18")] [InlineData("S-1-5-19")] [InlineData("S-1-5-80-123")]
    public void SystemAndServiceIdentitiesCannotBecomeInteractiveAdministrators(string? sid)
    {
        Assert.False(TerminalProtocol.ValidUser(sid));
        Assert.False(TerminalEndpoints.ValidCapability(_capability with { UserSid = sid }));
    }
    [Fact] public void MissingLogonAndFalseElevationAreNotValidCapabilities()
    {
        Assert.False(TerminalEndpoints.ValidCapability(_capability with { LogonId = null }));
        Assert.False(TerminalEndpoints.ValidCapability(_capability with { Enabled = false }));
        Assert.False(TerminalProtocol.ValidBrowserFrame(new("ready", Administrator: true)));
        Assert.False(TerminalProtocol.ValidBrowserFrame(new("input", new string('x', 16385))));
        Assert.False(TerminalProtocol.ValidBrowserFrame(new("resize", Columns: 500, Rows: 24)));
        Assert.True(TerminalProtocol.ValidBrowserFrame(new("input", "\u0003")));
        Assert.Null(new AgentConfiguration("https://example.test", "token", "computer", "data", false).Terminal);
    }
    [Fact] public void LegacyConfigurationDefaultsToNoTerminalAndPasswordRecheckDoesNotCreateLoginAuthority()
    {
        var configuration = System.Text.Json.JsonSerializer.Deserialize<AgentConfiguration>(
            """{"serverUrl":"https://example.test","deviceId":"computer","token":"example-only","dataDirectory":"queue","localDevelopment":false}""", ProtocolJson.Options)!;
        Assert.Null(configuration.Terminal); Assert.Equal("computer", configuration.DeviceId);
        string path = Path.Combine(Path.GetTempPath(), "howie-password-recheck-" + Guid.NewGuid().ToString("N"));
        try
        {
            var credentials = new CredentialStore(path); Assert.True(credentials.Initialize("isolated-test-password"));
            string stamp = credentials.Login("isolated-test-password", false)!;
            string before = File.ReadAllText(Path.Combine(path, "user.json"));
            Assert.True(credentials.VerifyPassword("isolated-test-password")); Assert.False(credentials.VerifyPassword("wrong"));
            Assert.True(credentials.ValidSession(stamp)); Assert.Equal(before, File.ReadAllText(Path.Combine(path, "user.json")));
        }
        finally { Directory.Delete(path, true); }
    }
    [Theory]
    [InlineData("https://monitor.example", true)]
    [InlineData("https://monitor.example:444", false)]
    [InlineData("http://monitor.example", false)]
    [InlineData("https://evil.example", false)]
    [InlineData("https://monitor.example/path", false)]
    [InlineData("null", false)]
    [InlineData("", false)]
    public void BrowserSocketRequiresTheExactPublicOrigin(string origin, bool expected)
    {
        var context = new DefaultHttpContext(); context.Request.Headers.Origin = origin;
        Assert.Equal(expected, TerminalEndpoints.SameOrigin(context, ServerHosting.Create(false, "http://127.0.0.1:5080", "https://monitor.example")));
    }
    [Fact] public void PairingCandidatesCannotPollForCommandsBeforeActivation()
    {
        string path = Path.Combine(Path.GetTempPath(), "howie-terminal-auth-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new TelemetryStore(path); var credentials = new CredentialStore(path);
            store.InitializeEnrollment(credentials.Device, new EphemeralDataProtectionProvider().CreateProtector("terminal-test"));
            var code = store.CreateEnrollmentCode(new(false, null, true), _now).Code!;
            var candidate = store.ClaimEnrollment(new(code.Code, Guid.NewGuid().ToString(), "PC"), _now).Credential!;
            Assert.Null(store.AuthorizeTerminal("Bearer " + candidate.Token, _now));
            Assert.Null(store.AuthorizeTerminal("Bearer wrong", _now));
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(path, true); }
    }
}
