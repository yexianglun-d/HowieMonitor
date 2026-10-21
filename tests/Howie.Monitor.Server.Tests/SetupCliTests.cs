using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using Howie.Monitor.Contracts;
using Howie.Monitor.Setup;
using Xunit;

namespace Howie.Monitor.Server.Tests;

public sealed class SetupCliTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task InstallationRequiresConsentAndActivatesOnlyAfterRealReadiness(bool allowTerminal, bool allowUpdates)
    {
        using var fixture = new Fixture();
        Assert.Equal(0, await fixture.Run("ABCD-1234-EF56\n" + (allowTerminal ? "y" : "") + "\n" + (allowUpdates ? "y" : "") + "\ny\n"));
        Assert.Equal(1, fixture.Installs); Assert.Equal(1, fixture.Claims); Assert.Equal(1, fixture.Activations);
        Assert.NotNull(fixture.Plan);
        Assert.Equal(allowTerminal, fixture.Plan.Configuration.Terminal?.Enabled ?? false);
        Assert.Equal(allowUpdates, fixture.Plan.Configuration.Updates?.Enabled ?? false);
        if (allowTerminal) Assert.Equal(Fixture.OwnerSid, fixture.Plan.Configuration.Terminal!.UserSid);
        Assert.Contains("接入验证通过", fixture.Output.ToString());
        Assert.DoesNotContain(Fixture.Token, fixture.Output.ToString());
        Assert.DoesNotContain("ABCD-1234-EF56", fixture.Output.ToString());
        Assert.Empty(Directory.GetFiles(fixture.WorkRoot));
    }

    [Fact]
    public async Task RepairKeepsTheDeviceIdentityAndExistingAccountAuthorization()
    {
        using var fixture = new Fixture { ExistingInstallation = true };
        fixture.Configuration = fixture.Configuration with { Terminal = new(true, Fixture.OwnerSid), Updates = new(true) };
        Assert.Equal(0, await fixture.Run("\n\ny\n"));
        Assert.Equal(0, fixture.Claims); Assert.Equal(0, fixture.Activations);
        Assert.Equal("cli-device", fixture.Plan!.Configuration.DeviceId); Assert.Null(fixture.Plan.RequestId);
        Assert.Equal(Fixture.Token, fixture.Plan.Configuration.Token);
        Assert.Equal(Fixture.OwnerSid, fixture.Plan.Configuration.Terminal!.UserSid);
        Assert.True(fixture.Plan.Configuration.Updates!.Enabled);
        Assert.Contains("修复/升级", fixture.Output.ToString());
    }

    [Fact]
    public async Task DecliningInstallationDoesNotInstallAndCanResumeWithoutAnotherClaim()
    {
        using var fixture = new Fixture();
        Assert.Equal(2, await fixture.Run("ABCD-1234-EF56\nn\nn\nn\n"));
        Assert.Equal(0, fixture.Installs); Assert.Equal(0, fixture.Activations);
        Assert.DoesNotContain("接入验证通过", fixture.Output.ToString());
        Assert.True(File.Exists(Path.Combine(fixture.WorkRoot, "state.json")));
        Assert.Equal(0, await fixture.Run("n\nn\ny\n"));
        Assert.Equal(1, fixture.Claims); Assert.Equal(1, fixture.Installs);
        Assert.Contains("已恢复上次安装请求", fixture.Output.ToString());
    }

    [Fact]
    public async Task LostClaimResponseReusesThePersistedRequestIdentity()
    {
        using var fixture = new Fixture { LoseFirstClaimResponse = true };
        Assert.Equal(1, await fixture.Run("ABCD-1234-EF56\n"));
        Assert.Equal(0, fixture.Installs);
        Assert.Equal(0, await fixture.Run("ABCD-1234-EF56\nn\nn\ny\n"));
        Assert.Equal(2, fixture.Claims);
        Assert.Equal(fixture.Requests[0], fixture.Requests[1]);
        Assert.Equal(1, fixture.Installs);
    }

    [Fact]
    public async Task ResumingAReplacementRetainsThePreviousComputerWarning()
    {
        using var fixture = new Fixture { PreviousComputerName = "previous-computer" };
        Assert.Equal(2, await fixture.Run("ABCD-1234-EF56\nn\nn\nn\n"));
        Assert.Equal("previous-computer", SetupPaths.Read<SetupUiState>(Path.Combine(fixture.WorkRoot, "state.json"))!.PreviousComputer);
        fixture.Output.GetStringBuilder().Clear();
        Assert.Equal(2, await fixture.Run("n\nn\nn\n"));
        Assert.Contains("当前监控电脑：previous-computer", fixture.Output.ToString());
        Assert.Contains("旧凭据届时失效", fixture.Output.ToString());
        Assert.Equal(0, fixture.Installs); Assert.Equal(1, fixture.Claims);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EnvironmentOrPendingUninstallFailureNeverContactsTheServer(bool pendingUninstall)
    {
        using var fixture = new Fixture();
        if (pendingUninstall) fixture.ExistingError = new InvalidOperationException("上一次卸载仍有使用中文件待清理，请关闭向导并重启后重新安装。");
        else fixture.EnvironmentError = new InvalidOperationException("请在管理员 PowerShell 运行。");
        Assert.Equal(1, await fixture.Run(""));
        Assert.Equal(0, fixture.HttpRequests); Assert.Equal(0, fixture.Installs);
        Assert.Contains(pendingUninstall ? "重启" : "管理员", fixture.Output.ToString());
        Assert.Empty(Directory.GetFiles(fixture.WorkRoot));
    }

    [Fact]
    public async Task AnotherComputersCredentialCannotInstallOrActivate()
    {
        using var fixture = new Fixture { StatusComputerName = "different-computer" };
        Assert.Equal(1, await fixture.Run("ABCD-1234-EF56\nn\nn\ny\n"));
        Assert.Equal(0, fixture.Installs); Assert.Equal(0, fixture.Activations);
        Assert.Contains("电脑名称与本机不一致", fixture.Output.ToString());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StoppedServiceOrMissingSamplesCannotReportInstallationSuccess(bool stopped)
    {
        using var fixture = new Fixture { ServiceRunning = !stopped, FreshSamples = false };
        Assert.Equal(1, await fixture.Run("ABCD-1234-EF56\nn\nn\ny\n"));
        Assert.Equal(1, fixture.Installs); Assert.Equal(0, fixture.Activations);
        Assert.DoesNotContain("接入验证通过", fixture.Output.ToString());
        Assert.True(File.Exists(Path.Combine(fixture.WorkRoot, "state.json")));
    }

    [Fact]
    public async Task RevokedCredentialBeforeInstallationDoesNotChangeTheService()
    {
        using var fixture = new Fixture { RevokeBeforeInstall = true };
        Assert.Equal(1, await fixture.Run("ABCD-1234-EF56\nn\nn\ny\n"));
        Assert.Equal(0, fixture.Installs); Assert.Equal(0, fixture.Activations);
        Assert.Contains("凭据已过期或撤销", fixture.Output.ToString());
        Assert.DoesNotContain("点击", fixture.Output.ToString());
    }

    [Fact]
    public async Task EndOfInputDoesNotAcceptAnyConsent()
    {
        using var fixture = new Fixture();
        Assert.Equal(130, await fixture.Run("ABCD-1234-EF56\n"));
        Assert.Equal(0, fixture.Installs); Assert.Equal(0, fixture.Activations);
    }

    [Fact]
    public async Task CancellationCanInterruptAnInputReaderThatIgnoresItsToken()
    {
        using var fixture = new Fixture(); using var reader = new PendingReader(); using var cancellation = new CancellationTokenSource();
        var pending = fixture.Run("", cancellation.Token, reader);
        await reader.Started.Task.WaitAsync(TimeSpan.FromSeconds(5)); cancellation.Cancel();
        try { Assert.Equal(130, await pending.WaitAsync(TimeSpan.FromSeconds(5))); }
        finally { reader.Complete.TrySetResult(null); }
        Assert.Equal(0, fixture.Installs); Assert.Equal(0, fixture.Claims);
    }

    [Theory]
    [InlineData("--help", 0)]
    [InlineData("--cli --help", 0)]
    [InlineData("--cli --unknown", 2)]
    public async Task RealCliEntryPreservesRedirectedStreamsAndExitsWithoutOpeningTheWizard(string arguments, int expectedExit)
    {
        var start = new ProcessStartInfo(Path.Combine(AppContext.BaseDirectory, "HowieMonitorSetup.exe"), arguments)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
        };
        using var process = Process.Start(start)!; process.StandardInput.Close();
        var output = process.StandardOutput.ReadToEndAsync(); var error = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        try { await process.WaitForExitAsync(timeout.Token); }
        finally { if (!process.HasExited) process.Kill(true); }
        Assert.Equal(expectedExit, process.ExitCode);
        Assert.Contains("交互式 CLI 安装", await output + await error);
    }

    private sealed class Fixture : IDisposable
    {
        internal const string OwnerSid = "S-1-5-21-100-200-300-1001", Token = "cli-private-device-token-never-printed";
        internal string WorkRoot { get; } = Path.Combine(Path.GetTempPath(), "howie-cli-test-" + Guid.NewGuid().ToString("N"));
        internal StringWriter Output { get; } = new();
        internal AgentConfiguration Configuration { get; set; } = new(SetupPaths.ServerUrl, "cli-device", Token, Path.Combine(SetupPaths.DataRoot, "queue"), false);
        internal InstallPlan? Plan { get; private set; }
        internal bool ExistingInstallation { get; set; }
        internal bool LoseFirstClaimResponse { get; set; }
        internal bool RevokeBeforeInstall { get; set; }
        internal bool ServiceRunning { get; set; } = true;
        internal bool FreshSamples { get; set; } = true;
        internal string StatusComputerName { get; set; } = Environment.MachineName;
        internal string? PreviousComputerName { get; set; }
        internal Exception? ExistingError { get; set; }
        internal Exception? EnvironmentError { get; set; }
        internal int Claims { get; private set; }
        internal int Activations { get; private set; }
        internal int Installs { get; private set; }
        internal int HttpRequests { get; private set; }
        internal List<string> Requests { get; } = [];
        private DateTimeOffset _now = DateTimeOffset.UtcNow;
        internal Fixture() => Directory.CreateDirectory(WorkRoot);
        internal async Task<int> Run(string input, CancellationToken token = default, TextReader? reader = null)
        {
            using var client = new SetupClient(new HttpClient(new Handler(Send)) { BaseAddress = new(SetupPaths.ServerUrl) });
            using var ownedReader = reader ?? new StringReader(input);
            var cli = new SetupCli(client, ownedReader, Output, WorkRoot, OwnerSid,
                checkEnvironment: () => { if (EnvironmentError is not null) throw EnvironmentError; },
                existing: () => ExistingError is not null ? throw ExistingError : ExistingInstallation ? new(SetupPaths.Product, "0.1.0", "test-agent.exe", OwnerSid, null) : null,
                readConfiguration: () => Configuration,
                install: (plan, _) => { Installs++; Plan = plan; return Task.CompletedTask; },
                serviceRunning: () => ServiceRunning, secureWork: () => { }, utcNow: () => _now,
                delay: _ => { _now = _now.AddMinutes(2); return Task.CompletedTask; });
            return await cli.Run(token);
        }
        private async Task<HttpResponseMessage> Send(HttpRequestMessage request, CancellationToken token)
        {
            HttpRequests++;
            switch (request.RequestUri!.AbsolutePath)
            {
                case "/api/auth/session": return Json(new { authenticated = false });
                case "/api/enrollment/claim":
                    var claim = (await request.Content!.ReadFromJsonAsync<EnrollmentClaim>(ProtocolJson.Options, token))!;
                    Claims++; Requests.Add(claim.RequestId);
                    if (LoseFirstClaimResponse && Claims == 1) throw new HttpRequestException("Simulated lost response");
                    return Json(new EnrollmentCredential(Configuration.DeviceId, Token, _now.AddMinutes(20), PreviousComputerName, false));
                case "/api/ingest/status":
                    if (RevokeBeforeInstall) return new(HttpStatusCode.Unauthorized);
                    return Json(Progress(ExistingInstallation || Activations > 0 ? "active" : Installs > 0 && FreshSamples ? "ready" : "waiting"));
                case "/api/ingest/activate":
                    Assert.True(Installs > 0 && FreshSamples && ServiceRunning); Activations++; return Json(Progress("active"));
                default: throw new InvalidOperationException("Unexpected request");
            }
        }
        private EnrollmentProgress Progress(string state) => new(state, StatusComputerName, Installs > 0 || ExistingInstallation,
            Installs > 0 || ExistingInstallation, FreshSamples ? "fresh" : "waiting", _now, null);
        private static HttpResponseMessage Json<T>(T value) => new(HttpStatusCode.OK) { Content = JsonContent.Create(value, options: ProtocolJson.Options) };
        public void Dispose() { Output.Dispose(); Directory.Delete(WorkRoot, true); }
    }
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => send(request, token);
    }
    private sealed class PendingReader : TextReader
    {
        internal TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource<string?> Complete { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override ValueTask<string?> ReadLineAsync(CancellationToken cancellationToken)
        { Started.TrySetResult(); return new(Complete.Task); }
    }
}
