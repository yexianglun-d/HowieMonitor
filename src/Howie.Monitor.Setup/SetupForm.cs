using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Howie.Monitor.Contracts;

namespace Howie.Monitor.Setup;

public sealed class SetupForm : Form
{
    private readonly SetupClient _client;
    private readonly Func<InstallPlan, CancellationToken, Task> _install;
    private readonly Func<bool> _serviceRunning;
    private readonly Func<bool, CancellationToken, Task> _remove;
    private readonly Action<string> _open;
    private readonly string _workRoot;
    private readonly CancellationTokenSource _cancellation = new();
    private readonly Label _title, _description, _status;
    private readonly FlowLayoutPanel _content;
    private readonly Button _next, _back, _rebind;
    private readonly Label _serviceProgress = new(), _registrationProgress = new(), _heartbeatProgress = new(), _dataProgress = new(), _activationProgress = new();
    private readonly TextBox _code = new() { Width = 330, MaxLength = 40, Font = new("Consolas", 18) };
    private readonly CheckBox _confirm = new() { AutoSize = true, Text = "我已阅读隐私说明，确认将这台电脑的监控数据上传至上述服务，并按绑定码指定的方式接入。", ForeColor = Color.FromArgb(230, 232, 228) };
    private readonly CheckBox _allowTerminal = new() { AutoSize = true, Text = "允许私人面板使用当前登录的本账号运行管理员 PowerShell。远程命令可修改文件和系统设置；注销后不可用，不保存 Windows 密码。", ForeColor = Color.FromArgb(230, 232, 228) };
    private readonly CheckBox _removeData = new() { AutoSize = true, Checked = true, Text = "彻底卸载：删除本机配置、令牌、采集缓存和升级文件（无法撤回）", ForeColor = Color.FromArgb(230, 232, 228) };
    private readonly CheckBox _allowUpdates = new() { AutoSize = true, Text = "允许从私人面板确认升级采集端。仅安装本服务发布并校验的安装包；会重启采集服务，失败时尝试恢复原版本。", ForeColor = Color.FromArgb(230, 232, 228) };
    private readonly List<Label> _steps = [];
    private readonly bool _uninstall;
    private InstallPlan? _plan;
    private AgentConfiguration? _existingConfiguration;
    private AgentConfiguration? _rebindConfiguration;
    private LegacyAgent? _legacy;
    private string? _previousComputer;
    private bool _busy, _installed, _needsRebind;
    private CancellationTokenSource? _verificationCancellation;
    private int? _pendingNavigation;
    private DateTimeOffset? _credentialExpiresAt;
    private int _step;
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    internal bool PreviewOnly { get; set; }
    protected override bool ShowWithoutActivation => PreviewOnly;
    private static readonly Color BackgroundColor = Color.FromArgb(16, 19, 19), PanelColor = Color.FromArgb(24, 28, 28), Accent = Color.FromArgb(154, 219, 201);

    public SetupForm(bool uninstall) : this(uninstall, new SetupClient(), Program.RunElevated, InstallEngine.IsServiceRunning, SetupPaths.WorkRoot) { }
    internal SetupForm(bool uninstall, SetupClient client, Func<InstallPlan, CancellationToken, Task> install, Func<bool> serviceRunning, string workRoot, Action<string>? open = null, Func<bool, CancellationToken, Task>? remove = null)
    {
        _uninstall = uninstall; _client = client; _install = install; _serviceRunning = serviceRunning; _workRoot = workRoot; _open = open ?? Open; _remove = remove ?? RemoveInstalled;
        SuspendLayout();
        Text = uninstall ? "Howie Monitor · 采集端卸载向导" : "Howie Monitor · 采集端安装向导"; ClientSize = new(900, 640); MinimumSize = new(820, 620);
        AutoScaleDimensions = new(96, 96); AutoScaleMode = AutoScaleMode.Dpi; BackColor = BackgroundColor; ForeColor = Color.FromArgb(230, 232, 228);
        Font = new("Microsoft YaHei UI", 10); StartPosition = FormStartPosition.CenterScreen;
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Margin = Padding.Empty };
        layout.ColumnStyles.Add(new(SizeType.Absolute, 220)); layout.ColumnStyles.Add(new(SizeType.Percent, 100)); Controls.Add(layout);
        var sidebar = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, BackColor = PanelColor, Padding = new(24, 32, 20, 24), Margin = Padding.Empty };
        sidebar.Controls.Add(new Label { AutoSize = true, Text = "Howie Monitor", Font = new(Font.FontFamily, 14, FontStyle.Bold), ForeColor = Accent, Margin = new(0, 0, 0, 32) });
        string[] steps = uninstall
            ? ["01  卸载说明", "02  确认卸载", "03  正在卸载", "04  卸载完成"]
            : ["01  欢迎使用", "02  环境检查", "03  绑定电脑", "04  确认安装", "05  安装服务", "06  接入验证", "07  接入完成"];
        foreach (string text in steps)
        {
            var label = new Label { AutoSize = true, Text = text, Padding = new(0, 12, 0, 12), Margin = new(0, 0, 0, 4), ForeColor = Color.FromArgb(135, 146, 143) };
            sidebar.Controls.Add(label); _steps.Add(label);
        }
        layout.Controls.Add(sidebar);
        var main = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 5, ColumnCount = 1, Padding = new(32, 32, 32, 24), Margin = Padding.Empty };
        main.ColumnStyles.Add(new(SizeType.Percent, 100));
        main.RowStyles.Add(new(SizeType.AutoSize)); main.RowStyles.Add(new(SizeType.AutoSize)); main.RowStyles.Add(new(SizeType.Percent, 100)); main.RowStyles.Add(new(SizeType.AutoSize)); main.RowStyles.Add(new(SizeType.AutoSize));
        _title = new() { AutoSize = true, Dock = DockStyle.Fill, Font = new(Font.FontFamily, 21, FontStyle.Bold), Margin = new(0, 0, 0, 10) };
        _description = new() { AutoSize = true, Dock = DockStyle.Fill, ForeColor = Color.FromArgb(160, 174, 168), Margin = new(0, 0, 0, 24) };
        _content = new() { Dock = DockStyle.Fill, AutoScroll = true, FlowDirection = FlowDirection.TopDown, WrapContents = false, Margin = Padding.Empty };
        _content.SizeChanged += (_, _) => FitContentWidth();
        _content.ControlAdded += (_, _) => FitContentWidth();
        _status = new() { AutoSize = true, Dock = DockStyle.Fill, ForeColor = Accent, Margin = new(0, 16, 0, 0) };
        var actions = new FlowLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft, WrapContents = false, Margin = new(0, 24, 0, 0) };
        main.Controls.Add(_title, 0, 0); main.Controls.Add(_description, 0, 1); main.Controls.Add(_content, 0, 2); main.Controls.Add(_status, 0, 3); main.Controls.Add(actions, 0, 4); layout.Controls.Add(main, 1, 0);
        ResumeLayout(true);
        // 页面内容在切换时才创建，按当前 DPI 换算间距，避免与窗体的初始缩放重复。
        _code.Margin = Spacing(0, 0, 0, 16); _code.Width = LogicalToDeviceUnits(330);
        _allowUpdates.Margin = _allowTerminal.Margin = _confirm.Margin = _removeData.Margin = Spacing(0, 0, 0, 16);
        _allowUpdates.CheckAlign = _allowTerminal.CheckAlign = _confirm.CheckAlign = _removeData.CheckAlign = ContentAlignment.TopLeft;
        _allowUpdates.TextAlign = _allowTerminal.TextAlign = _confirm.TextAlign = _removeData.TextAlign = ContentAlignment.TopLeft;
        _next = Button("开始检查", async (_, _) => await Next()); _next.BackColor = Accent; _next.ForeColor = BackgroundColor;
        _back = Button("上一步", (_, _) => RequestNavigation(_step == 5 ? 3 : _step - 1));
        _rebind = Button("重新绑定", (_, _) => RequestNavigation(2));
        _next.Margin = Padding.Empty; _back.Margin = _rebind.Margin = Spacing(0, 0, 12, 0);
        actions.Controls.Add(_next); actions.Controls.Add(_back); actions.Controls.Add(_rebind);
        FormClosing += (_, e) => { if (_busy) { e.Cancel = true; MessageBox.Show(_step == 5 ? "正在检测接入状态，可点击“上一步”或“重新绑定”停止检测后再关闭。" : "当前步骤正在处理，请完成或取消管理员授权后再关闭。", "Howie Monitor"); } };
        FormClosed += (_, _) => { _cancellation.Cancel(); _client.Dispose(); _cancellation.Dispose(); };
        ShowStep(0);
    }
    private Button Button(string text, EventHandler action)
    {
        var button = new Button { Text = text, AutoSize = true, MinimumSize = LogicalToDeviceUnits(new Size(120, 42)), FlatStyle = FlatStyle.Flat, Margin = Spacing(0, 0, 0, 16), Padding = Spacing(16, 0, 16, 0), ForeColor = Accent, BackColor = PanelColor, TextAlign = ContentAlignment.MiddleCenter };
        button.FlatAppearance.BorderColor = Color.FromArgb(53, 64, 60); button.Click += action; return button;
    }
    private Padding Spacing(int left, int top, int right, int bottom) => new(LogicalToDeviceUnits(left), LogicalToDeviceUnits(top), LogicalToDeviceUnits(right), LogicalToDeviceUnits(bottom));
    private void FitContentWidth()
    {
        // 预留滚动条宽度，长段落只纵向滚动，避免滚动条出现后反复挤压与横向偏移。
        int width = Math.Max(1, _content.ClientSize.Width - LogicalToDeviceUnits(SystemInformation.VerticalScrollBarWidth) - _content.Padding.Horizontal);
        _content.SuspendLayout();
        try
        {
            foreach (Control control in _content.Controls)
            {
                control.MaximumSize = new(Math.Max(1, width - control.Margin.Horizontal), 0);
                if (control is TextBox) control.Width = Math.Min(LogicalToDeviceUnits(330), width);
                if (control is CheckBox checkBox)
                {
                    // CheckBox 的自动尺寸可能仍按单行计算，须为完整的换行授权文案保留高度。
                    using var graphics = checkBox.CreateGraphics();
                    var glyph = CheckBoxRenderer.GetGlyphSize(graphics, System.Windows.Forms.VisualStyles.CheckBoxState.UncheckedNormal);
                    int textWidth = Math.Max(1, control.MaximumSize.Width - glyph.Width - LogicalToDeviceUnits(6));
                    var textSize = TextRenderer.MeasureText(graphics, checkBox.Text, checkBox.Font, new(textWidth, 0), TextFormatFlags.WordBreak);
                    checkBox.MinimumSize = new(control.MaximumSize.Width, Math.Max(glyph.Height, textSize.Height) + LogicalToDeviceUnits(6));
                }
            }
        }
        finally { _content.ResumeLayout(true); }
    }
    private void TextLine(string text, bool heading = false)
    {
        var label = new Label { Text = text, AutoSize = true, Margin = Spacing(0, 0, 0, 16) };
        if (heading) label.Font = new(Font.FontFamily, 13, FontStyle.Bold);
        _content.Controls.Add(label);
    }
    private void ShowStep(int step)
    {
        _step = step; _content.Controls.Clear(); _status.Text = _needsRebind ? new SetupCredentialException().Message : "";
        _back.Visible = !_uninstall && step is > 0 and < 6; _rebind.Visible = !_uninstall && step is >= 3 and <= 5;
        for (int i = 0; i < _steps.Count; i++) _steps[i].ForeColor = i == step ? Accent : Color.FromArgb(135, 146, 143);
        if (_uninstall) { ShowUninstallStep(step); UpdateActions(); return; }
        switch (step)
        {
            case 0:
                _title.Text = "接入你的电脑";
                _description.Text = "从安装到真实上报，一步一步完成。";
                TextLine("当前电脑：" + Environment.MachineName, true);
                TextLine("采集 CPU、GPU、内存、磁盘、网络、可读取传感器与进程占用；通过 HTTPS 上传到你的私人面板。");
                TextLine("服务入口：" + SetupPaths.ServerUrl);
                TextLine("后台服务会在开机后运行，不需要保持窗口打开。温度、功耗等不支持的指标保留缺失原因。");
                var information = new FlowLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Margin = Spacing(0, 0, 0, 16) };
                information.Controls.Add(Button("隐私说明", (_, _) => PackageContents.ShowPrivacy(this)));
                information.Controls.Add(Button("第三方许可", (_, _) => PackageContents.ShowNotices(this)));
                foreach (Control button in information.Controls) button.Margin = Spacing(0, 0, 12, 0);
                _content.Controls.Add(information);
                _next.Text = "开始检查"; break;
            case 1:
                _title.Text = "环境检查"; _description.Text = "检查系统、安装位置与安全连接。";
                TextLine("Windows 11 x64 与磁盘空间", true); TextLine("服务入口使用系统证书验证，证书或网络失败可重试。");
                _next.Text = "重新检查"; break;
            case 2:
                _title.Text = "绑定这台电脑"; _description.Text = "打开私人面板 →“电脑接入”→ 为这台电脑生成新绑定码，再回到这里输入。绑定码有效期十分钟。";
                TextLine("短期绑定码", true); _content.Controls.Add(_code); TextLine("例如 ABCD-1234-EF56；无需填写网页登录密码。长期凭据由向导安全保存。 ");
                if (_existingConfiguration is not null) TextLine("检测到本机现有采集配置，通过凭据核对后会保留设备身份与历史。");
                _content.Controls.Add(Button("打开私人面板", (_, _) => _open(SetupPaths.ServerUrl)));
                _next.Text = "验证绑定码"; break;
            case 3:
                _title.Text = "确认安装"; _description.Text = "确认目标电脑和后台运行方式，然后授权安装。";
                TextLine("目标电脑：" + Environment.MachineName, true);
                TextLine("上传服务：" + SetupPaths.ServerUrl);
                TextLine("安装位置：" + SetupPaths.ProgramRoot + "\n私有配置：" + SetupPaths.DataRoot);
                TextLine("管理员权限用于安装 Windows 后台服务。服务以 LocalSystem 读取全机指标，开启自动运行和故障恢复。");
                if (_previousComputer is not null) TextLine("当前监控电脑：" + _previousComputer + "。新服务通过真实采样验证并激活后才切换，旧凭据届时失效。");
                else TextLine("新增接入不会停止其他电脑的采集。");
                if (_credentialExpiresAt.HasValue) TextLine($"本次接入授权有效至 {_credentialExpiresAt.Value.ToLocalTime():HH:mm}。若已过期或在网页撤销，请点击“重新绑定”。");
                _content.Controls.Add(Button("隐私说明", (_, _) => PackageContents.ShowPrivacy(this)));
                _content.Controls.Add(_allowTerminal);
                _content.Controls.Add(_allowUpdates);
                _content.Controls.Add(_confirm); _next.Text = "安装并启动"; break;
            case 4:
                _title.Text = "安装后台服务"; _description.Text = "校验载荷、保存私有配置并启动采集。";
                TextLine("请在 Windows 管理员授权中确认本产品安装。拒绝授权后可重试或点击“上一步”返回确认；凭据失效可重新绑定。"); _next.Text = "重试安装"; break;
            case 5:
                _title.Text = "确认真实数据"; _description.Text = "正在自动验证，通过后自动进入第七步“接入完成”，无需点击下一步。";
                foreach (var label in new[] { _serviceProgress, _registrationProgress, _heartbeatProgress, _dataProgress, _activationProgress })
                {
                    label.AutoSize = true; label.Margin = Spacing(0, 0, 0, 12); _content.Controls.Add(label);
                }
                _serviceProgress.Text = "后台服务：等待检查"; _registrationProgress.Text = "电脑注册：等待云端确认";
                _heartbeatProgress.Text = "连接心跳：等待云端确认"; _dataProgress.Text = "CPU / 内存与采样时间：等待云端确认"; _activationProgress.Text = "设备激活：等待真实数据符合条件";
                TextLine("检测最多约一分钟。可点“上一步”返回安装修复，或点“重新绑定”更换失效凭据；程序和本地数据会保留。");
                _content.Controls.Add(Button("打开日期与时间", (_, _) => _open("ms-settings:dateandtime")));
                _status.Text = "正在连接云端并检查后台服务，请稍候…";
                _next.Text = "重新检测"; break;
            case 6:
                _title.Text = "这台电脑已接入"; _description.Text = "云端已确认当前电脑的新鲜数据。";
                TextLine(Environment.MachineName, true); TextLine("后台采集已运行，重启后自动启动。请用手机移动流量打开私人面板，确认电脑名称与数值持续更新。");
                TextLine("温度或功耗等传感器可能不可读，请在硬件信息中查看实际状态。点击“打开监控面板”后，安装向导将自动退出。");
                _next.Text = "打开监控面板"; _content.Controls.Add(Button("关闭向导", (_, _) => Close())); break;
        }
        UpdateActions();
    }
    private void UpdateActions()
    {
        bool canNavigate = !_busy || (_step == 5 && !_pendingNavigation.HasValue);
        _back.Enabled = canNavigate; _rebind.Enabled = canNavigate;
        _next.Enabled = !_busy && !(_needsRebind && _step is >= 3 and <= 5);
        if (_step == 5) _next.Text = _busy ? "正在检测…" : _needsRebind ? "需要重新绑定" : "重新检测";
    }
    private void RequestNavigation(int step)
    {
        if (step < 0 || step > 3 || (_busy && _step != 5)) return;
        if (_busy)
        {
            // 先结束本轮请求，再切换页面，防止迟到的验证响应跳回完成页。
            _pendingNavigation = step; _verificationCancellation?.Cancel(); _status.Text = "正在停止检测，请稍候…"; UpdateActions(); return;
        }
        Navigate(step);
    }
    private void Navigate(int step)
    {
        if (step == 2)
        {
            try { ClearPendingFiles(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            { _status.Text = "未能重置临时安装请求，请关闭向导后重新运行再绑定；已安装程序和本地数据保留。"; return; }
            _rebindConfiguration = _plan?.Configuration;
            _plan = null; _previousComputer = null; _credentialExpiresAt = null; _installed = false; _needsRebind = false; _confirm.Checked = false; _code.Clear();
        }
        ShowStep(step);
        if (step == 2) { _status.Text = "请在私人面板生成新绑定码并输入。已安装的程序和本地数据保留，重新确认后更新服务凭据。"; _code.Focus(); }
    }
    private void ClearPendingFiles()
    {
        foreach (string name in new[] { "state.json", "plan.json", "claim-request.json" })
        {
            string temporary = Path.Combine(_workRoot, name); SetupPaths.VerifyPath(temporary, _workRoot);
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }
    private async Task Next()
    {
        if (_busy) return;
        if (_step == (_uninstall ? 3 : 6))
        {
            try { if (!_uninstall) _open(SetupPaths.ServerUrl); }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
            { _status.Text = "未能打开默认浏览器。请重试，或手动访问 " + SetupPaths.ServerUrl + " 后关闭向导。"; return; }
            // 完成页不进入安装忙碌状态，否则 FormClosing 会阻止正常退出。
            Close(); return;
        }
        _busy = true; UpdateActions();
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
            }
        }
        catch (OperationCanceledException) when (_pendingNavigation.HasValue) { }
        catch (SetupCredentialException ex) { _needsRebind = true; _status.Text = ex.Message; _activationProgress.Text = "设备激活：凭据失效，请重新绑定"; }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223) { _status.Text = _uninstall ? "管理员授权已取消，尚未卸载。可重新确认卸载。" : "管理员授权已取消，安装尚未完成。可重试安装，或点击“上一步”返回确认。"; }
        catch (HttpRequestException) { _status.Text = "HTTPS 网络或证书验证失败，请检查网络与电脑时间后重试。"; }
        catch (TaskCanceledException) { _status.Text = "连接超时，保留当前步骤，可重试。"; }
        catch (Exception ex) { _status.Text = ex is InvalidOperationException ? ex.Message : $"操作未完成（{ex.GetType().Name}），请重试或查看运行状态。"; }
        finally
        {
            _busy = false;
            if (_pendingNavigation is int target) { _pendingNavigation = null; Navigate(target); }
            if (_step == 5) _description.Text = _needsRebind ? "安装文件已保留。请点击“重新绑定”获取新授权，再继续接入。" : "本次检测已结束。处理下方提示后点击“重新检测”，通过后自动进入第七步。";
            UpdateActions();
        }
    }
    private async Task CheckEnvironment()
    {
        _existingConfiguration = null; _rebindConfiguration = null; _legacy = null; _plan = null; _previousComputer = null; _installed = false; _needsRebind = false; _credentialExpiresAt = null;
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
                        _allowTerminal.Checked = configuration.Terminal is { Enabled: true } access && access.UserSid == SetupPaths.CurrentSid;
                        _allowUpdates.Checked = configuration.Updates is { Enabled: true };
                        if (status.State == "active") _existingConfiguration = configuration;
                        ShowStep(3); _status.Text = "检测到本产品安装，将修复/升级并保留配置和设备身份。"; return;
                    }
                }
                catch (SetupCredentialException) { }
            }
        }
        var legacy = await Task.Run(LegacyDetection.Find, _cancellation.Token);
        if (legacy is not null)
        {
            try
            {
                var status = await _client.Status(legacy.Value.Configuration.Token, _cancellation.Token);
                if (status.State == "active" && status.ComputerName == Environment.MachineName)
                { _existingConfiguration = legacy.Value.Configuration; _legacy = legacy.Value.Process; }
            }
            catch (SetupCredentialException) { }
        }
        var saved = SetupPaths.Read<SetupUiState>(Path.Combine(_workRoot, "state.json"));
        if (saved is not null && saved.ComputerName == Environment.MachineName)
        {
            try
            {
                var status = await _client.Status(saved.Plan.Configuration.Token, _cancellation.Token);
                if (status.ComputerName == Environment.MachineName && status.State is "ready" or "waiting" or "active")
                { _plan = saved.Plan; _allowTerminal.Checked = saved.Plan.Configuration.Terminal is { Enabled: true } access && access.UserSid == SetupPaths.CurrentSid; _allowUpdates.Checked = saved.Plan.Configuration.Updates is { Enabled: true }; _previousComputer = saved.PreviousComputer; _credentialExpiresAt = saved.ExpiresAt; ShowStep(3); _status.Text = "已恢复上次安装请求，可以继续。"; return; }
            }
            catch (SetupCredentialException) { ClearPendingFiles(); }
        }
        ShowStep(2); _status.Text = "环境与 HTTPS 连接检查通过。";
    }
    private async Task Claim()
    {
        if (_rebindConfiguration is not null || _existingConfiguration is not null)
        {
            AgentConfiguration? verified = null;
            // 激活响应丢失时，刚安装的凭据可能已生效；优先核对它，再核对原配置，避免重复创建本机身份。
            foreach (var configuration in new[] { _rebindConfiguration, _existingConfiguration }.OfType<AgentConfiguration>().Distinct())
            {
                try
                {
                    var status = await _client.Status(configuration.Token, _cancellation.Token);
                    if (status.State == "active" && status.ComputerName == Environment.MachineName) { verified = configuration; break; }
                }
                catch (SetupCredentialException) { }
            }
            _existingConfiguration = verified; _rebindConfiguration = null;
            if (verified is null) _legacy = null;
        }
        SetupPaths.PrivateDirectory(_workRoot, SetupPaths.CurrentSid);
        string requestPath = Path.Combine(_workRoot, "claim-request.json");
        string codeHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(_code.Text.Replace("-", "", StringComparison.Ordinal).Trim().ToUpperInvariant())));
        var attempt = SetupPaths.Read<ClaimAttempt>(requestPath);
        string requestId = attempt is not null && attempt.ComputerName == Environment.MachineName && attempt.CodeHash == codeHash
            ? attempt.RequestId : Guid.NewGuid().ToString();
        // 请求身份先保存，网络响应丢失时仍兑换同一份凭据，不能生成另一个请求重用已消费短码。
        SetupPaths.Write(requestPath, new ClaimAttempt(Environment.MachineName, codeHash, requestId));
        var credential = await _client.Claim(new(_code.Text.Trim(), requestId, Environment.MachineName, _existingConfiguration?.DeviceId, _existingConfiguration?.Token), _cancellation.Token);
        _plan = new(new(SetupPaths.ServerUrl, credential.DeviceId, credential.Token, Path.Combine(SetupPaths.DataRoot, "queue"), false), requestId, SetupPaths.CurrentSid, _legacy);
        _previousComputer = credential.PreviousComputerName;
        _credentialExpiresAt = credential.ExpiresAt; _installed = false; _needsRebind = false; _confirm.Checked = false;
        SetupPaths.Write(Path.Combine(_workRoot, "state.json"), new SetupUiState(Environment.MachineName, _plan, _previousComputer, credential.ExpiresAt));
        _code.Text = ""; ShowStep(3); _status.Text = credential.PreservesHistory ? "原设备身份已核对，将保留历史。" : "短期绑定授权已核对，请确认安装；完成前不会改变已有电脑的采集。";
    }
    private async Task Install()
    {
        if (_plan is null) throw new InvalidOperationException("请先验证绑定码。");
        _status.Text = "正在核对接入授权，随后请求管理员权限…";
        await _client.Status(_plan.Configuration.Token, _cancellation.Token);
        _status.Text = "正在安装…请完成 Windows 管理员授权。";
        _plan = _plan with { Configuration = _plan.Configuration with { Terminal = _allowTerminal.Checked ? new(true, _plan.OwnerSid) : null, Updates = new(_allowUpdates.Checked) } };
        await _install(_plan, _cancellation.Token); _installed = true; ShowStep(5); await Verify();
    }
    private async Task Verify()
    {
        if (_plan is null || !_installed) throw new InvalidOperationException("请先完成安装步骤。");
        ShowStep(5);
        using var verification = CancellationTokenSource.CreateLinkedTokenSource(_cancellation.Token);
        _verificationCancellation = verification;
        try { await VerifyUntilComplete(verification.Token); }
        finally { _verificationCancellation = null; }
    }
    private async Task VerifyUntilComplete(CancellationToken cancellationToken)
    {
        var plan = _plan!;
        var deadline = DateTimeOffset.UtcNow.AddMinutes(1);
        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            bool running = _serviceRunning();
            _serviceProgress.Text = running ? "后台服务：正在运行" : "后台服务：未运行";
            _status.Text = $"正在自动检测，剩余约 {Math.Max(1, (int)(deadline - DateTimeOffset.UtcNow).TotalSeconds)} 秒。可随时点击“上一步”或“重新绑定”停止检测。";
            var progress = await _client.Status(plan.Configuration.Token, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (!running) throw new InvalidOperationException("后台服务未运行。请点击“上一步”返回确认安装，再点击“安装并启动”修复并启动服务。");
            string statusText = progress.DataStatus switch { "fresh" => "最新", "delayed" => "延迟", "stale" => "过期", "clockError" => "时钟异常", _ => "等待采样" };
            _registrationProgress.Text = "电脑注册：" + (progress.Registered ? "已确认" : "等待采集端注册");
            _heartbeatProgress.Text = "连接心跳：" + (progress.HeartbeatReceived ? "已确认" : "等待采集端上报");
            _dataProgress.Text = "CPU / 内存与采样时间：" + statusText + (progress.Detail is null ? "" : "；" + progress.Detail);
            _activationProgress.Text = progress.State == "active" ? "设备激活：已确认" : "设备激活：等待真实数据符合条件";
            if (progress.State == "ready" && plan.RequestId is not null)
            {
                _activationProgress.Text = "设备激活：正在请求云端确认…";
                progress = await _client.Activate(plan, cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
            }
            if (progress.State == "active" && progress.Registered && progress.HeartbeatReceived && progress.DataStatus == "fresh" && progress.ComputerName == Environment.MachineName)
            {
                bool stopped = plan.Legacy is null || LegacyDetection.StopVerified(plan.Legacy);
                string? cleanupWarning = null;
                try { ClearPendingFiles(); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { cleanupWarning = "已接入，临时安装记录未能清理；可关闭向导后重新运行检查。"; }
                ShowStep(6); _status.Text = cleanupWarning ?? (stopped ? "云端验证通过，接入已完成。可打开监控面板或关闭向导。" : "已接入；旧手动实例未能安全核对关闭，请退出原采集程序。"); return;
            }
            if (progress.State == "expired") throw new SetupCredentialException();
            if (progress.ComputerName is not null && progress.ComputerName != Environment.MachineName) throw new InvalidOperationException("云端返回的电脑名称与本机不一致，请点击“重新绑定”为这台电脑生成新绑定码。");
            if (progress.DataStatus == "clockError") throw new InvalidOperationException("安装完成，接入待验证：电脑时间异常，请同步系统时间后重新检测。");
            await Task.Delay(2000, cancellationToken);
        }
        throw new InvalidOperationException("本次检测已结束，尚未收到完整的实时数据。请按上方未通过项检查网络和系统时间，再点“重新检测”；服务异常可点“上一步”修复，授权失效可点“重新绑定”。");
    }
    private void ShowUninstallStep(int step)
    {
        switch (step)
        {
            case 0:
                _title.Text = "卸载采集端"; _description.Text = "停止这台电脑的采集并移除后台服务。";
                TextLine("当前电脑：" + Environment.MachineName, true);
                TextLine("向导会移除 Howie Monitor 服务和安装文件。云端电脑与历史记录保留。");
                TextLine("下一步可选择保留或删除本地配置与缓存。");
                _next.Text = "查看卸载选项"; break;
            case 1:
                _title.Text = "确认卸载"; _description.Text = "停止并移除本产品后台服务，云端历史保留。";
                TextLine("默认彻底清理本机配置、令牌、采集缓存和升级文件。需要保留配置以便重装时，可取消下方选项。云端记录保留。");
                _content.Controls.Add(_removeData); _next.Text = "确认卸载"; break;
            case 2:
                _title.Text = "正在卸载"; _description.Text = "正在请求管理员授权并处理卸载。";
                TextLine("授权后将停止采集服务、移除服务和安装文件，请等待卸载结果。");
                _next.Text = "正在卸载…"; break;
            case 3:
                _title.Text = "卸载完成"; _description.Text = "采集已停止，Windows 服务已移除。";
                TextLine("安装目录与 Windows 服务已移除。关闭窗口后自动清理本次临时卸载向导，正常情况下无需重启。");
                TextLine(_removeData.Checked ? "本机配置、令牌、采集缓存和升级文件已删除；云端历史保留。" : "已按选择保留本机配置与采集缓存；升级文件已清理，云端历史保留。");
                _next.Text = "关闭向导"; break;
        }
    }
    private async Task Uninstall()
    {
        if (_step == 0)
        {
            if (File.Exists(SetupUninstall.PendingFile)) _ = SetupUninstall.ReadPending();
            else InstallEngine.Existing();
            ShowStep(1); return;
        }
        ShowStep(2);
        try { await _remove(_removeData.Checked, _cancellation.Token); }
        catch { ShowStep(1); throw; }
        ShowStep(3);
    }
    private static async Task RemoveInstalled(bool removeData, CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = true, Verb = "runas", Arguments = "--remove-installed " + (removeData ? "remove-data" : "keep-data"), WindowStyle = ProcessWindowStyle.Hidden };
        using var process = Process.Start(start) ?? throw new InvalidOperationException("卸载未启动。");
        await process.WaitForExitAsync(cancellationToken);
        if (process.ExitCode != 0) throw new InvalidOperationException("卸载未完成，请查看提示后重试。");
    }
    private static void Open(string address) => Process.Start(new ProcessStartInfo(address) { UseShellExecute = true });
}
