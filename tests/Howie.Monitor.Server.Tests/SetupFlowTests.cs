using System.Reflection;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Howie.Monitor.Contracts;
using Howie.Monitor.Setup;
using Xunit;

namespace Howie.Monitor.Server.Tests;

public sealed class SetupFlowTests
{
    static SetupFlowTests()
    {
        Application.SetHighDpiMode(HighDpiMode.SystemAware);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
    }

    [Fact]
    public Task OpeningDashboardAfterCompletionClosesTheWizard() => OnUiThread(async () =>
    {
        string? opened = null;
        using var fixture = new Fixture((_, _) => Task.FromResult(Json(Progress("active"))), open: address => opened = address);
        fixture.StageVerification(); await Next(fixture.Form);
        Assert.Equal(6, Field<int>(fixture.Form, "_step"));
        await Next(fixture.Form);
        Assert.Equal(SetupPaths.ServerUrl, opened);
        Assert.True(fixture.Form.IsDisposed);
    });

    [Fact]
    public Task BrowserLaunchFailureKeepsCompletionPageAvailable() => OnUiThread(async () =>
    {
        int attempts = 0;
        using var fixture = new Fixture((_, _) => Task.FromResult(Json(Progress("active"))), open: _ =>
        {
            if (++attempts == 1) throw new System.ComponentModel.Win32Exception(1155);
        });
        fixture.StageVerification(); await Next(fixture.Form); await Next(fixture.Form);
        Assert.False(fixture.Form.IsDisposed);
        Assert.Equal(6, Field<int>(fixture.Form, "_step"));
        Assert.Contains("浏览器", Field<Label>(fixture.Form, "_status").Text);
        Assert.True(Field<Button>(fixture.Form, "_next").Enabled);
        await Next(fixture.Form);
        Assert.Equal(2, attempts); Assert.True(fixture.Form.IsDisposed);
    });

    [Fact]
    public Task OpeningDashboardWhileBindingKeepsTheWizardOpen() => OnUiThread(() =>
    {
        string? opened = null;
        using var fixture = new Fixture((_, _) => Task.FromResult(Json(Progress("active"))), open: address => opened = address);
        Invoke(fixture.Form, "ShowStep", 2);
        Field<FlowLayoutPanel>(fixture.Form, "_content").Controls.OfType<Button>().Single().PerformClick();
        Assert.Equal(SetupPaths.ServerUrl, opened);
        Assert.False(fixture.Form.IsDisposed);
        Assert.Equal(2, Field<int>(fixture.Form, "_step"));
        return Task.CompletedTask;
    });

    [Theory]
    [InlineData(900, 640)]
    [InlineData(804, 581)]
    [InlineData(1200, 800)]
    public Task EveryStepRemainsReadableWhenWindowResizes(int width, int height) => OnUiThread(() =>
    {
        using var form = ShowForm();
        form.ClientSize = new(width, height);
        SetField(form, "_credentialExpiresAt", DateTimeOffset.UtcNow.AddMinutes(20));
        SetField(form, "_previousComputer", "此前接入的电脑");
        for (int step = 0; step <= 6; step++)
        {
            Invoke(form, "ShowStep", step);
            Field<Label>(form, "_status").Text = "接入凭据已过期或撤销。请点击“重新绑定”，在私人面板为这台电脑生成新绑定码，然后返回向导继续安装。已安装程序和本地数据保留。";
            form.PerformLayout();
            var content = Field<FlowLayoutPanel>(form, "_content");
            Assert.False(content.HorizontalScroll.Visible);
            foreach (Control control in content.Controls)
                Assert.InRange(control.Right, 1, content.ClientSize.Width);
            foreach (string name in new[] { "_title", "_description", "_status" })
            {
                var label = Field<Label>(form, name);
                Assert.True(label.Height >= label.GetPreferredSize(new(label.Width, 0)).Height, name + " clips text");
            }
            var next = Field<Button>(form, "_next");
            Assert.True(next.Parent!.ClientRectangle.Contains(next.Bounds), "Primary action is outside the footer");
            Assert.True(content.Height > next.Height * 2, "Content is squeezed out by surrounding text");
            if (step == 3)
            {
                var confirm = Field<CheckBox>(form, "_confirm");
                if (width <= 900) Assert.True(confirm.Height >= confirm.Font.Height * 2, "Consent text is clipped");
                content.ScrollControlIntoView(confirm);
                var textSize = TextRenderer.MeasureText(confirm.Text, confirm.Font, new(confirm.Width, 0), TextFormatFlags.WordBreak);
                Assert.InRange(confirm.Top + textSize.Height, 1, content.ClientSize.Height);
            }
            Render(form, $"setup-layout-{width}-step-{step + 1}.png");
        }
        return Task.CompletedTask;
    });

    [Theory]
    [InlineData(4)]
    [InlineData(5)]
    public Task InstallationAndVerificationOfferBackNavigation(int step) => OnUiThread(() =>
    {
        using var form = ShowForm();
        Invoke(form, "ShowStep", step);
        Assert.True(Field<Button>(form, "_back").Visible);
        return Task.CompletedTask;
    });

    [Fact]
    public Task VerificationExplainsAutomaticCompletionBeforeFirstResponse() => OnUiThread(() =>
    {
        using var form = ShowForm();
        Invoke(form, "ShowStep", 5);
        Assert.Contains("自动", Field<Label>(form, "_description").Text);
        Assert.False(string.IsNullOrWhiteSpace(Field<Label>(form, "_status").Text));
        return Task.CompletedTask;
    });

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    public Task RejectedCredentialCanRebindAndAutomaticallyComplete(HttpStatusCode rejected) => OnUiThread(async () =>
    {
        EnrollmentClaim? claim = null;
        int activations = 0;
        using var fixture = new Fixture(async (request, cancellationToken) =>
        {
            if (request.RequestUri!.AbsolutePath == "/api/enrollment/claim")
            {
                claim = await request.Content!.ReadFromJsonAsync<EnrollmentClaim>(ProtocolJson.Options, cancellationToken);
                return Json(new EnrollmentCredential("new-device", "new-private-test-token", DateTimeOffset.UtcNow.AddMinutes(20), null, false));
            }
            if (request.Headers.Authorization?.Parameter == "old-private-test-token") return new(rejected);
            if (request.RequestUri.AbsolutePath == "/api/ingest/activate")
            {
                activations++;
                var activation = await request.Content!.ReadFromJsonAsync<EnrollmentActivation>(ProtocolJson.Options, cancellationToken);
                Assert.Equal(claim!.RequestId, activation!.RequestId);
                return Json(Progress("active"));
            }
            return Json(Progress("ready"));
        });
        var form = fixture.Form;
        fixture.StageVerification();
        foreach (string name in new[] { "state.json", "plan.json", "claim-request.json" }) File.WriteAllText(Path.Combine(fixture.WorkRoot, name), "temporary test state");
        string keep = Path.Combine(fixture.WorkRoot, "keep.txt"); File.WriteAllText(keep, "unrelated test file");
        await Next(form);
        Assert.Equal(5, Field<int>(form, "_step"));
        Assert.Contains("重新绑定", Field<Label>(form, "_status").Text);
        Assert.False(Field<Button>(form, "_next").Enabled);
        Assert.True(Field<Button>(form, "_rebind").Enabled);
        if (rejected == HttpStatusCode.Unauthorized) Render(form, "setup-credential-recovery.png");
        Field<Button>(form, "_rebind").PerformClick();
        Assert.Equal(2, Field<int>(form, "_step"));
        Assert.Null(Field<InstallPlan?>(form, "_plan"));
        Assert.Equal([keep], Directory.GetFiles(fixture.WorkRoot));
        Field<TextBox>(form, "_code").Text = "ABCD-1234-EF56";
        await Next(form);
        Assert.Equal(3, Field<int>(form, "_step"));
        Assert.NotNull(claim); Assert.Null(claim.ExistingToken); Assert.Null(claim.ExistingDeviceId);
        Assert.Equal(Environment.MachineName, claim.ComputerName);
        Assert.False(Field<CheckBox>(form, "_confirm").Checked);
        Field<CheckBox>(form, "_confirm").Checked = true;
        await Next(form);
        Assert.Equal(6, Field<int>(form, "_step"));
        Assert.Equal(1, fixture.Installs); Assert.Equal(1, activations);
        Assert.Equal("new-private-test-token", fixture.InstalledPlan!.Configuration.Token);
        Assert.True(File.Exists(keep));
        Assert.False(File.Exists(Path.Combine(fixture.WorkRoot, "state.json")));
        if (rejected == HttpStatusCode.Unauthorized) Render(form, "setup-completed.png");
    });

    [Theory]
    [InlineData("_back", 3)]
    [InlineData("_rebind", 2)]
    public Task NavigationCancelsVerificationAndIgnoresLateSuccess(string button, int destination) => OnUiThread(async () =>
    {
        var response = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var fixture = new Fixture((_, _) => response.Task);
        fixture.StageVerification();
        Task verification = Next(fixture.Form);
        Assert.True(Field<bool>(fixture.Form, "_busy"));
        Assert.True(Field<Button>(fixture.Form, button).Enabled);
        Assert.Contains("正在", Field<Label>(fixture.Form, "_status").Text);
        if (button == "_back") Render(fixture.Form, "setup-verification.png");
        Field<Button>(fixture.Form, button).PerformClick();
        response.SetResult(Json(Progress("active")));
        await verification;
        Assert.Equal(destination, Field<int>(fixture.Form, "_step"));
        Assert.False(Field<bool>(fixture.Form, "_busy"));
        Assert.True(Field<Button>(fixture.Form, "_next").Enabled);
        Assert.Equal(destination == 2, Field<InstallPlan?>(fixture.Form, "_plan") is null);
    });

    [Fact]
    public Task StoppedServiceOffersRepairWithoutWaitingForCredentialExpiry() => OnUiThread(async () =>
    {
        int requests = 0;
        using var fixture = new Fixture((_, _) => { requests++; return Task.FromResult(Json(Progress("ready"))); }, serviceRunning: false);
        fixture.StageVerification(); await Next(fixture.Form);
        Assert.Equal(1, requests);
        Assert.Equal(5, Field<int>(fixture.Form, "_step"));
        Assert.Contains("后台服务未运行", Field<Label>(fixture.Form, "_status").Text);
        Field<Button>(fixture.Form, "_back").PerformClick();
        Assert.Equal(3, Field<int>(fixture.Form, "_step"));
        Assert.Equal(0, fixture.Installs);
    });

    [Theory]
    [InlineData("waiting", "clockError", "电脑时间异常")]
    [InlineData("expired", "waiting", "重新绑定")]
    public Task InvalidEvidenceCannotComplete(string state, string dataStatus, string explanation) => OnUiThread(async () =>
    {
        using var fixture = new Fixture((_, _) => Task.FromResult(Json(Progress(state) with { DataStatus = dataStatus })));
        fixture.StageVerification(); await Next(fixture.Form);
        Assert.Equal(5, Field<int>(fixture.Form, "_step"));
        Assert.Contains(explanation, Field<Label>(fixture.Form, "_status").Text);
        Assert.True(Field<Button>(fixture.Form, "_rebind").Enabled);
    });

    [Fact]
    public Task MissingBasicMetricsRemainsInVerificationAndCanReturn() => OnUiThread(async () =>
    {
        using var fixture = new Fixture((_, _) => Task.FromResult(Json(Progress("waiting") with { Detail = "等待可用的 CPU 与内存基础指标。" })));
        fixture.StageVerification(); Task verification = Next(fixture.Form);
        Assert.Equal(5, Field<int>(fixture.Form, "_step"));
        Assert.Contains("基础指标", Field<Label>(fixture.Form, "_dataProgress").Text);
        Field<Button>(fixture.Form, "_back").PerformClick(); await verification;
        Assert.Equal(3, Field<int>(fixture.Form, "_step"));
    });

    [Fact]
    public Task ActivationFailureCanRetryAndFinish() => OnUiThread(async () =>
    {
        int activations = 0;
        using var fixture = new Fixture((request, _) => Task.FromResult(request.Method == HttpMethod.Post && activations++ == 0
            ? Json(new { error = "云端暂时未完成激活，请重新检测。" }, HttpStatusCode.Conflict)
            : Json(Progress(request.Method == HttpMethod.Post ? "active" : "ready"))));
        fixture.StageVerification(); await Next(fixture.Form);
        Assert.Equal(5, Field<int>(fixture.Form, "_step"));
        Assert.True(Field<Button>(fixture.Form, "_next").Enabled);
        Assert.Contains("重新检测", Field<Label>(fixture.Form, "_status").Text);
        await Next(fixture.Form);
        Assert.Equal(6, Field<int>(fixture.Form, "_step"));
        Assert.Equal(2, activations);
    });

    [Fact]
    public Task RevokedCredentialIsRejectedBeforeInstallation() => OnUiThread(async () =>
    {
        using var fixture = new Fixture((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized)));
        fixture.StageVerification(); Invoke(fixture.Form, "ShowStep", 3); Field<CheckBox>(fixture.Form, "_confirm").Checked = true;
        await Next(fixture.Form);
        Assert.Equal(0, fixture.Installs);
        Assert.True(Field<Button>(fixture.Form, "_rebind").Enabled);
        Assert.Contains("重新绑定", Field<Label>(fixture.Form, "_status").Text);
    });

    [Fact]
    public Task AuthorizationCancellationCanReturnToConfirmation() => OnUiThread(async () =>
    {
        using var fixture = new Fixture((_, _) => Task.FromResult(Json(Progress("ready"))), installError: new System.ComponentModel.Win32Exception(1223));
        fixture.StageVerification(); Invoke(fixture.Form, "ShowStep", 3); Field<CheckBox>(fixture.Form, "_confirm").Checked = true;
        await Next(fixture.Form);
        Assert.Equal(4, Field<int>(fixture.Form, "_step"));
        Assert.Contains("管理员授权已取消", Field<Label>(fixture.Form, "_status").Text);
        Field<Button>(fixture.Form, "_back").PerformClick();
        Assert.Equal(3, Field<int>(fixture.Form, "_step"));
    });

    [Fact]
    public Task TimeoutExplainsRecoveryWithoutClaimingSuccess() => OnUiThread(async () =>
    {
        using var fixture = new Fixture((_, _) => Task.FromResult(Json(Progress("waiting") with { Registered = false, HeartbeatReceived = false, DataStatus = "waiting" })));
        fixture.StageVerification(); await Next(fixture.Form);
        Assert.Equal(5, Field<int>(fixture.Form, "_step"));
        Assert.Contains("本次检测已结束", Field<Label>(fixture.Form, "_status").Text);
        Assert.True(Field<Button>(fixture.Form, "_next").Enabled);
        Assert.True(Field<Button>(fixture.Form, "_back").Enabled);
        Assert.True(Field<Button>(fixture.Form, "_rebind").Enabled);
        Assert.Equal("重新检测", Field<Button>(fixture.Form, "_next").Text);
    });

    [Fact]
    public Task CleanupFailureDoesNotHideConfirmedActivation() => OnUiThread(async () =>
    {
        using var fixture = new Fixture((_, _) => Task.FromResult(Json(Progress("active"))));
        fixture.StageVerification();
        string state = Path.Combine(fixture.WorkRoot, "state.json"); File.WriteAllText(state, "temporary test state"); File.SetAttributes(state, FileAttributes.ReadOnly);
        try
        {
            await Next(fixture.Form);
            Assert.Equal(6, Field<int>(fixture.Form, "_step"));
            Assert.Contains("临时安装记录未能清理", Field<Label>(fixture.Form, "_status").Text);
        }
        finally { File.SetAttributes(state, FileAttributes.Normal); }
    });

    [Fact]
    public Task RebindAfterLostActivationResponseRetainsTheActivatedComputer() => OnUiThread(async () =>
    {
        var activationResponse = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var activationStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        bool activated = false;
        EnrollmentClaim? claim = null;
        using var fixture = new Fixture(async (request, cancellationToken) =>
        {
            if (request.RequestUri!.AbsolutePath == "/api/ingest/activate")
            {
                activated = true; activationStarted.SetResult(); return await activationResponse.Task;
            }
            if (request.RequestUri.AbsolutePath == "/api/enrollment/claim")
            {
                claim = await request.Content!.ReadFromJsonAsync<EnrollmentClaim>(ProtocolJson.Options, cancellationToken);
                return Json(new EnrollmentCredential("old-device", "rotated-private-test-token", DateTimeOffset.UtcNow.AddMinutes(20), Environment.MachineName, true));
            }
            return Json(Progress(activated ? "active" : "ready"));
        });
        fixture.StageVerification();
        SetField(fixture.Form, "_existingConfiguration", new AgentConfiguration(SetupPaths.ServerUrl, "previous-device", "previous-private-test-token", Path.Combine(SetupPaths.DataRoot, "queue"), false));
        Task verification = Next(fixture.Form); await activationStarted.Task;
        Field<Button>(fixture.Form, "_rebind").PerformClick();
        activationResponse.SetResult(Json(Progress("active"))); await verification;
        Assert.Equal(2, Field<int>(fixture.Form, "_step"));
        Field<TextBox>(fixture.Form, "_code").Text = "ABCD-1234-EF56"; await Next(fixture.Form);
        Assert.NotNull(claim);
        Assert.Equal("old-device", claim.ExistingDeviceId);
        Assert.Equal("old-private-test-token", claim.ExistingToken);
        Assert.Equal(3, Field<int>(fixture.Form, "_step"));
    });

    private static EnrollmentProgress Progress(string state) => new(state, Environment.MachineName, true, true, "fresh", DateTimeOffset.UtcNow, null);
    private static HttpResponseMessage Json<T>(T value, HttpStatusCode status = HttpStatusCode.OK) => new(status) { Content = JsonContent.Create(value, options: ProtocolJson.Options) };
    private static Task Next(SetupForm form) => (Task)Invoke(form, "Next")!;
    private static void SetField(SetupForm form, string name, object value) => typeof(SetupForm).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(form, value);
    private static void Render(SetupForm form, string name)
    {
        form.PerformLayout();
        string directory = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../reports")); Directory.CreateDirectory(directory);
        using var bitmap = new Bitmap(form.Width, form.Height);
        form.DrawToBitmap(bitmap, new(0, 0, form.Width, form.Height)); bitmap.Save(Path.Combine(directory, name));
    }
    private sealed class Fixture : IDisposable
    {
        public string WorkRoot { get; } = Path.Combine(Path.GetTempPath(), "howie-setup-flow-" + Guid.NewGuid().ToString("N"));
        public SetupForm Form { get; }
        public int Installs { get; private set; }
        public InstallPlan? InstalledPlan { get; private set; }
        public Fixture(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send, bool serviceRunning = true, Exception? installError = null, Action<string>? open = null)
        {
            Directory.CreateDirectory(WorkRoot);
            var client = new SetupClient(new HttpClient(new Handler(send)) { BaseAddress = new(SetupPaths.ServerUrl) });
            Form = new(false, client, (plan, _) =>
            {
                Installs++; InstalledPlan = plan;
                return installError is null ? Task.CompletedTask : Task.FromException(installError);
            }, () => serviceRunning, WorkRoot, open) { PreviewOnly = true, ShowInTaskbar = false, Opacity = 0 };
            Form.Show();
        }
        public void StageVerification()
        {
            SetField(Form, "_plan", new InstallPlan(new(SetupPaths.ServerUrl, "old-device", "old-private-test-token", Path.Combine(SetupPaths.DataRoot, "queue"), false), Guid.NewGuid().ToString(), SetupPaths.CurrentSid, null));
            SetField(Form, "_installed", true); Invoke(Form, "ShowStep", 5);
        }
        public void Dispose() { Form.Dispose(); Directory.Delete(WorkRoot, true); }
    }
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request, cancellationToken);
    }

    private static SetupForm ShowForm()
    {
        var form = new SetupForm(false) { ShowInTaskbar = false, Opacity = 0 };
        typeof(SetupForm).GetProperty("PreviewOnly", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(form, true);
        form.Show();
        return form;
    }
    private static T Field<T>(SetupForm form, string name) => (T)typeof(SetupForm).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(form)!;
    private static object? Invoke(SetupForm form, string name, params object[] args) => typeof(SetupForm).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(form, args);
    private static Task OnUiThread(Func<Task> test)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            EventHandler? run = null;
            run = async (_, _) =>
            {
                Application.Idle -= run;
                try { await test(); completion.SetResult(); }
                catch (Exception ex) { completion.SetException(ex); }
                finally { Application.ExitThread(); }
            };
            Application.Idle += run;
            Application.Run();
        });
        thread.SetApartmentState(ApartmentState.STA); thread.IsBackground = true; thread.Start();
        return completion.Task.WaitAsync(TimeSpan.FromSeconds(90));
    }
}
