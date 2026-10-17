using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Howie.Monitor.Contracts;

namespace Howie.Monitor.Setup;

public sealed class SetupForm : Form
{
    private readonly SetupClient _client = new();
    private readonly CancellationTokenSource _cancellation = new();
    private readonly Label _title, _description, _status;
    private readonly FlowLayoutPanel _content;
    private readonly Button _next, _back;
    private readonly TextBox _code = new() { Width = 330, MaxLength = 40, Font = new("Consolas", 18) };
    private readonly CheckBox _confirm = new() { AutoSize = true, MaximumSize = new(620, 0), Text = "我已阅读隐私说明，确认将这台电脑的监控数据上传至上述服务，并接入或更换当前监控电脑。", ForeColor = Color.FromArgb(230, 232, 228) };
    private readonly CheckBox _removeData = new() { AutoSize = true, Text = "同时删除本地配置、令牌和缓存（无法撤回）", ForeColor = Color.FromArgb(230, 232, 228) };
    private readonly List<Label> _steps = [];
    private readonly bool _uninstall;
    private InstallPlan? _plan;
    private AgentConfiguration? _existingConfiguration;
    private LegacyAgent? _legacy;
    private string? _previousComputer;
    private bool _busy, _installed;
    private int _step;
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    internal bool PreviewOnly { get; set; }
    protected override bool ShowWithoutActivation => PreviewOnly;
    private static readonly Color BackgroundColor = Color.FromArgb(16, 19, 19), PanelColor = Color.FromArgb(24, 28, 28), Accent = Color.FromArgb(154, 219, 201);

    public SetupForm(bool uninstall)
    {
        _uninstall = uninstall;
        Text = "Howie Monitor · 采集端安装向导"; ClientSize = new(900, 640); MinimumSize = new(820, 620);
        AutoScaleMode = AutoScaleMode.Dpi; BackColor = BackgroundColor; ForeColor = Color.FromArgb(230, 232, 228);
        Font = new("Microsoft YaHei UI", 10); StartPosition = FormStartPosition.CenterScreen;
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1 };
        layout.ColumnStyles.Add(new(SizeType.Absolute, 205)); layout.ColumnStyles.Add(new(SizeType.Percent, 100)); Controls.Add(layout);
        var sidebar = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, BackColor = PanelColor, Padding = new(22, 30, 15, 20) };
        sidebar.Controls.Add(new Label { AutoSize = true, Text = "H  Howie Monitor", Font = new("Bahnschrift", 15, FontStyle.Bold), ForeColor = Accent, Margin = new(0, 0, 0, 36) });
        foreach (string text in new[] { "01  欢迎使用", "02  环境检查", "03  绑定电脑", "04  确认安装", "05  安装服务", "06  接入验证", "07  接入完成" })
        {
            var label = new Label { AutoSize = true, Text = text, Padding = new(0, 10, 0, 10), Margin = new(0, 4, 0, 4), ForeColor = Color.FromArgb(135, 146, 143) };
            sidebar.Controls.Add(label); _steps.Add(label);
        }
        layout.Controls.Add(sidebar);
        var main = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 5, ColumnCount = 1, Padding = new(30) };
        main.RowStyles.Add(new(SizeType.Absolute, 45)); main.RowStyles.Add(new(SizeType.Absolute, 72)); main.RowStyles.Add(new(SizeType.Percent, 100)); main.RowStyles.Add(new(SizeType.Absolute, 65)); main.RowStyles.Add(new(SizeType.Absolute, 50));
        _title = new() { Dock = DockStyle.Fill, Font = new("Microsoft YaHei UI", 21, FontStyle.Bold), AutoEllipsis = true };
        _description = new() { Dock = DockStyle.Fill, ForeColor = Color.FromArgb(160, 174, 168) };
        _content = new() { Dock = DockStyle.Fill, AutoScroll = true, FlowDirection = FlowDirection.TopDown, WrapContents = false };
        _status = new() { Dock = DockStyle.Fill, ForeColor = Accent, AutoEllipsis = true };
        var actions = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft, WrapContents = false };
        _next = Button("开始检查", async (_, _) => await Next()); _next.BackColor = Accent; _next.ForeColor = BackgroundColor;
        _back = Button("上一步", (_, _) => { if (!_busy && _step > 0 && _step < 4) ShowStep(_step - 1); });
        actions.Controls.Add(_next); actions.Controls.Add(_back); main.Controls.Add(_title); main.Controls.Add(_description); main.Controls.Add(_content); main.Controls.Add(_status); main.Controls.Add(actions); layout.Controls.Add(main, 1, 0);
        FormClosing += (_, e) => { if (_busy) { e.Cancel = true; MessageBox.Show("当前步骤正在处理，请完成或取消管理员授权后再关闭。", "Howie Monitor"); } };
        FormClosed += (_, _) => { _cancellation.Cancel(); _client.Dispose(); _cancellation.Dispose(); };
        ShowStep(0);
    }
    private Button Button(string text, EventHandler action)
    {
        var button = new Button { Text = text, AutoSize = true, MinimumSize = new(120, 40), FlatStyle = FlatStyle.Flat, Margin = new(8, 0, 0, 0), ForeColor = Accent, BackColor = PanelColor };
        button.FlatAppearance.BorderColor = Color.FromArgb(53, 64, 60); button.Click += action; return button;
    }
    private void TextLine(string text, bool heading = false)
    {
        _content.Controls.Add(new Label { Text = text, AutoSize = true, MaximumSize = new(590, 0), Margin = new(0, 0, 0, 18), Font = new("Microsoft YaHei UI", heading ? 13 : 10, heading ? FontStyle.Bold : FontStyle.Regular) });
    }
    private void ShowStep(int step)
    {
        _step = step; _content.Controls.Clear(); _status.Text = ""; _back.Visible = step is > 0 and < 4;
        for (int i = 0; i < _steps.Count; i++) _steps[i].ForeColor = i == step ? Accent : Color.FromArgb(135, 146, 143);
        switch (step)
        {
            case 0:
                _title.Text = _uninstall ? "卸载采集端" : "接入你的电脑";
                _description.Text = "从安装到真实上报，一步一步完成。";
                TextLine("当前电脑：" + Environment.MachineName, true);
                TextLine("采集 CPU、GPU、内存、磁盘、网络、可读取传感器与进程占用；通过 HTTPS 上传到你的私人面板。");
                TextLine("服务入口：" + SetupPaths.ServerUrl);
                TextLine("后台服务会在开机后运行，不需要保持窗口打开。温度、功耗等不支持的指标保留缺失原因。");
                var information = new FlowLayoutPanel { AutoSize = true, WrapContents = false };
                information.Controls.Add(Button("隐私说明", (_, _) => PackageContents.ShowPrivacy(this)));
                information.Controls.Add(Button("第三方许可", (_, _) => PackageContents.ShowNotices(this)));
                _content.Controls.Add(information);
                _next.Text = _uninstall ? "查看卸载选项" : "开始检查"; break;
            case 1:
                _title.Text = "环境检查"; _description.Text = "检查系统、安装位置与安全连接。";
                TextLine("Windows 11 x64 与磁盘空间", true); TextLine("服务入口使用系统证书验证，证书或网络失败可重试。");
                _next.Text = "重新检查"; break;
            case 2:
                _title.Text = "绑定这台电脑"; _description.Text = "在已登录面板的“电脑接入”生成绑定码，有效期十分钟。";
                TextLine("短期绑定码", true); _content.Controls.Add(_code); TextLine("例如 ABCD-1234-EF56；无需填写网页登录密码。长期凭据由向导安全保存。 ");
                if (_existingConfiguration is not null) TextLine("检测到本机现有采集配置，通过凭据核对后会保留设备身份与历史。");
                _content.Controls.Add(Button("打开私人面板", (_, _) => Open(SetupPaths.ServerUrl)));
                _next.Text = "验证绑定码"; break;
            case 3:
                _title.Text = "确认安装"; _description.Text = "确认目标电脑和后台运行方式，然后授权安装。";
                TextLine("目标电脑：" + Environment.MachineName, true);
                TextLine("上传服务：" + SetupPaths.ServerUrl);
                TextLine("安装位置：" + SetupPaths.ProgramRoot + "\n私有配置：" + SetupPaths.DataRoot);
                TextLine("管理员权限用于安装 Windows 后台服务。服务以 LocalSystem 读取全机指标，开启自动运行和故障恢复。");
                if (_previousComputer is not null) TextLine("当前监控电脑：" + _previousComputer + "。新服务通过真实采样验证并激活后才切换，旧凭据届时失效。");
                _content.Controls.Add(Button("隐私说明", (_, _) => PackageContents.ShowPrivacy(this)));
                _content.Controls.Add(_confirm); _next.Text = "安装并启动"; break;
            case 4:
                _title.Text = "安装后台服务"; _description.Text = "校验载荷、保存私有配置并启动采集。";
                TextLine("请在 Windows 管理员授权中确认本产品安装。拒绝授权时可以返回重试。"); _next.Text = "重试安装"; break;
            case 5:
                _title.Text = "确认真实数据"; _description.Text = "安装完成，接入仍待云端验证。";
                TextLine("逐项确认设备注册、连接心跳、真实 CPU/内存和数据时间。缺少基础数据时不会显示成功。");
                _content.Controls.Add(Button("打开日期与时间", (_, _) => Open("ms-settings:dateandtime")));
                _next.Text = "重新检测"; break;
            case 6:
                _title.Text = "这台电脑已接入"; _description.Text = "云端已确认当前电脑的新鲜数据。";
                TextLine(Environment.MachineName, true); TextLine("后台采集已运行，重启后自动启动。请用手机移动流量打开私人面板，确认电脑名称与数值持续更新。");
                TextLine("温度或功耗等传感器可能不可读，请在硬件信息中查看实际状态。安装向导可以关闭。");
                _next.Text = "打开监控面板"; _content.Controls.Add(Button("关闭向导", (_, _) => Close())); break;
        }
    }
    private async Task Next()
    {
        if (_busy) return;
        if (_uninstall && _step == 6) { Close(); return; }
        _busy = true; _next.Enabled = false; _back.Enabled = false;
        try
        {
            if (_uninstall) { await Uninstall(); return; }
            switch (_step)
            {
                case 0: case 1: await CheckEnvironment(); break;
                case 2: await Claim(); break;
                case 3:
                    if (!_confirm.Checked) throw new InvalidOperationException("请先确认安装与接入操作。");
                    ShowStep(4); await Install(); break;
                case 4: await Install(); break;
                case 5: await Verify(); break;
                case 6: Open(SetupPaths.ServerUrl); break;
            }
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223) { _status.Text = "管理员授权已取消，安装尚未完成，可重试。"; }
        catch (HttpRequestException) { _status.Text = "HTTPS 网络或证书验证失败，请检查网络与电脑时间后重试。"; }
        catch (TaskCanceledException) { _status.Text = "连接超时，保留当前步骤，可重试。"; }
        catch (Exception ex) { _status.Text = ex is InvalidOperationException ? ex.Message : $"操作未完成（{ex.GetType().Name}），请重试或查看运行状态。"; }
        finally { _busy = false; _next.Enabled = true; _back.Enabled = true; }
    }
    private async Task CheckEnvironment()
    {
        _existingConfiguration = null; _legacy = null;
        ShowStep(1);
        if (RuntimeInformation.OSArchitecture != Architecture.X64 || Environment.OSVersion.Version.Build < 22000)
            throw new InvalidOperationException("首版安装程序支持 Windows 11 x64。当前系统不满足要求。");
        var drive = new DriveInfo(Path.GetPathRoot(SetupPaths.ProgramRoot)!);
        if (drive.AvailableFreeSpace < 1024L * 1024 * 1024) throw new InvalidOperationException("安装盘至少需要 1 GiB 可用空间。");
        await _client.Check(_cancellation.Token);
        var marker = InstallEngine.Existing();
        if (marker is not null)
        {
            var configuration = SetupPaths.Read<AgentConfiguration>(SetupPaths.ConfigurationFile);
            if (configuration is not null)
            {
                try
                {
                    var status = await _client.Status(configuration.Token, _cancellation.Token);
                    if (status.ComputerName == Environment.MachineName)
                    {
                        _plan = new(configuration, status.State == "active" ? null : marker.RequestId, SetupPaths.CurrentSid, null);
                        ShowStep(3); _status.Text = "检测到本产品安装，将修复/升级并保留配置和设备身份。"; return;
                    }
                }
                catch (InvalidOperationException) { }
            }
        }
        var legacy = await Task.Run(LegacyDetection.Find, _cancellation.Token);
        if (legacy is not null)
        {
            var status = await _client.Status(legacy.Value.Configuration.Token, _cancellation.Token);
            if (status.State == "active" && status.ComputerName == Environment.MachineName)
            { _existingConfiguration = legacy.Value.Configuration; _legacy = legacy.Value.Process; }
        }
        var saved = SetupPaths.Read<SetupUiState>(Path.Combine(SetupPaths.WorkRoot, "state.json"));
        if (saved is not null && saved.ComputerName == Environment.MachineName)
        {
            try
            {
                var status = await _client.Status(saved.Plan.Configuration.Token, _cancellation.Token);
                if (status.ComputerName == Environment.MachineName && status.State is "ready" or "waiting" or "active")
                { _plan = saved.Plan; _previousComputer = saved.PreviousComputer; ShowStep(3); _status.Text = "已恢复上次安装请求，可以继续。"; return; }
            }
            catch (InvalidOperationException) { }
        }
        ShowStep(2); _status.Text = "环境与 HTTPS 连接检查通过。";
    }
    private async Task Claim()
    {
        SetupPaths.PrivateDirectory(SetupPaths.WorkRoot, SetupPaths.CurrentSid);
        string requestPath = Path.Combine(SetupPaths.WorkRoot, "claim-request.json");
        string codeHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(_code.Text.Replace("-", "", StringComparison.Ordinal).Trim().ToUpperInvariant())));
        var attempt = SetupPaths.Read<ClaimAttempt>(requestPath);
        string requestId = attempt is not null && attempt.ComputerName == Environment.MachineName && attempt.CodeHash == codeHash
            ? attempt.RequestId : Guid.NewGuid().ToString();
        // 请求身份先保存，网络响应丢失时仍兑换同一份凭据，不能生成另一个请求重用已消费短码。
        SetupPaths.Write(requestPath, new ClaimAttempt(Environment.MachineName, codeHash, requestId));
        var credential = await _client.Claim(new(_code.Text.Trim(), requestId, Environment.MachineName, _existingConfiguration?.DeviceId, _existingConfiguration?.Token), _cancellation.Token);
        _plan = new(new(SetupPaths.ServerUrl, credential.DeviceId, credential.Token, Path.Combine(SetupPaths.DataRoot, "queue"), false), requestId, SetupPaths.CurrentSid, _legacy);
        _previousComputer = credential.PreviousComputerName;
        SetupPaths.Write(Path.Combine(SetupPaths.WorkRoot, "state.json"), new SetupUiState(Environment.MachineName, _plan, _previousComputer));
        _code.Text = ""; ShowStep(3); _status.Text = credential.PreservesHistory ? "原设备身份已核对，将保留历史。" : "短期绑定授权已核对，当前监控尚未切换。";
    }
    private async Task Install()
    {
        if (_plan is null) throw new InvalidOperationException("请先验证绑定码。");
        _status.Text = "正在安装…";
        await Program.RunElevated(_plan, _cancellation.Token); _installed = true; ShowStep(5); await Verify();
    }
    private async Task Verify()
    {
        if (_plan is null || !_installed) throw new InvalidOperationException("请先完成安装步骤。");
        var deadline = DateTimeOffset.UtcNow.AddMinutes(1);
        while (DateTimeOffset.UtcNow < deadline)
        {
            var progress = await _client.Status(_plan.Configuration.Token, _cancellation.Token);
            string statusText = progress.DataStatus switch { "fresh" => "最新", "delayed" => "延迟", "stale" => "过期", "clockError" => "时钟异常", _ => "等待采样" };
            _status.Text = $"注册：{(progress.Registered ? "已确认" : "等待")} · 心跳：{(progress.HeartbeatReceived ? "已确认" : "等待")} · 数据：{statusText}";
            if (progress.Detail is not null) _status.Text += "\n" + progress.Detail;
            if (progress.State == "ready" && _plan.RequestId is not null)
            {
                using var service = new System.ServiceProcess.ServiceController(SetupPaths.ServiceName);
                if (service.Status != System.ServiceProcess.ServiceControllerStatus.Running)
                    throw new InvalidOperationException("后台服务未运行，请返回安装步骤修复后重新检测。");
                progress = await _client.Activate(_plan, _cancellation.Token);
            }
            if (progress.State == "active" && progress.Registered && progress.HeartbeatReceived && progress.DataStatus == "fresh" && progress.ComputerName == Environment.MachineName)
            {
                bool stopped = _plan.Legacy is null || LegacyDetection.StopVerified(_plan.Legacy);
                foreach (string name in new[] { "state.json", "plan.json", "claim-request.json" })
                {
                    string temporary = Path.Combine(SetupPaths.WorkRoot, name); SetupPaths.VerifyPath(temporary, SetupPaths.WorkRoot);
                    if (File.Exists(temporary)) File.Delete(temporary);
                }
                ShowStep(6); if (!stopped) _status.Text = "已接入；旧手动实例未能安全核对关闭，请退出原采集程序。"; return;
            }
            if (progress.DataStatus == "clockError") throw new InvalidOperationException("安装完成，接入待验证：电脑时间异常，请同步系统时间后重新检测。");
            await Task.Delay(2000, _cancellation.Token);
        }
        throw new InvalidOperationException("安装完成，接入待验证。尚无符合条件的实时数据，请稍后重新检测；当前旧电脑没有提前停用。");
    }
    private async Task Uninstall()
    {
        if (_step == 0)
        {
            InstallEngine.Existing(); _step = 1; _content.Controls.Clear(); _title.Text = "确认卸载";
            _description.Text = "停止并移除本产品后台服务，云端历史保留。";
            TextLine("默认保留本地配置与缓存，便于重新安装。若选择删除，将在停止服务后删除本产品私有数据。其他软件与服务不受影响。");
            _content.Controls.Add(_removeData); _next.Text = "确认卸载"; return;
        }
        var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = true, Verb = "runas", Arguments = "--remove-installed " + (_removeData.Checked ? "remove-data" : "keep-data"), WindowStyle = ProcessWindowStyle.Hidden };
        using var process = Process.Start(start) ?? throw new InvalidOperationException("卸载未启动。");
        await process.WaitForExitAsync(_cancellation.Token);
        if (process.ExitCode != 0) throw new InvalidOperationException("卸载未完成，请查看提示后重试。");
        _step = 6; _content.Controls.Clear(); _title.Text = "卸载完成"; _description.Text = "采集已停止，Windows 服务已移除。";
        TextLine("云端历史没有删除。正在使用的向导程序及其目录将在下次 Windows 启动时清理，无需现在重启。");
        _next.Text = "关闭向导";
    }
    private static void Open(string address) => Process.Start(new ProcessStartInfo(address) { UseShellExecute = true });
    private sealed record SetupUiState(string ComputerName, InstallPlan Plan, string? PreviousComputer);
    private sealed record ClaimAttempt(string ComputerName, string CodeHash, string RequestId);
}
