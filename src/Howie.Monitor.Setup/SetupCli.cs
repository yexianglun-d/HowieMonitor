using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Howie.Monitor.Contracts;

namespace Howie.Monitor.Setup;

internal sealed class SetupCli
{
    private readonly SetupClient _client;
    private readonly TextReader _input;
    private readonly TextWriter _output;
    private readonly string _workRoot, _ownerSid;
    private readonly Action _checkEnvironment, _secureWork;
    private readonly Func<InstallMarker?> _existing;
    private readonly Func<AgentConfiguration?> _readConfiguration;
    private readonly Func<InstallPlan, CancellationToken, Task> _install;
    private readonly Func<bool> _serviceRunning;
    private readonly Func<DateTimeOffset> _utcNow;
    private readonly Func<CancellationToken, Task> _delay;
    private string? _previousComputer;

    internal SetupCli(SetupClient client, TextReader input, TextWriter output, string workRoot, string ownerSid,
        Action? checkEnvironment = null, Func<InstallMarker?>? existing = null,
        Func<AgentConfiguration?>? readConfiguration = null, Func<InstallPlan, CancellationToken, Task>? install = null,
        Func<bool>? serviceRunning = null, Action? secureWork = null,
        Func<DateTimeOffset>? utcNow = null, Func<CancellationToken, Task>? delay = null)
    {
        _client = client; _input = input; _output = output; _workRoot = workRoot; _ownerSid = ownerSid;
        _checkEnvironment = checkEnvironment ?? CheckEnvironment; _existing = existing ?? InstallEngine.Existing;
        _readConfiguration = readConfiguration ?? (() =>
        {
            SetupPaths.VerifyPath(SetupPaths.ConfigurationFile, SetupPaths.DataRoot);
            return SetupPaths.Read<AgentConfiguration>(SetupPaths.ConfigurationFile);
        });
        _install = install ?? ((plan, token) => InstallEngine.Apply(plan, Environment.ProcessPath!, token));
        _serviceRunning = serviceRunning ?? InstallEngine.IsServiceRunning;
        _secureWork = secureWork ?? (() => SetupPaths.PrivateDirectory(workRoot, ownerSid));
        _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow); _delay = delay ?? (token => Task.Delay(2000, token));
    }
    internal static void Help(TextWriter output) => output.WriteLine("""
        Howie Monitor · 交互式 CLI 安装

        在需要安装的 Windows 11 x64 电脑，以当前管理员账号打开 PowerShell：
          Start-Process -FilePath .\HowieMonitorSetup.exe -ArgumentList '--cli' -NoNewWindow -Wait

        --cli         按提示绑定电脑、确认安装或修复、选择终端和面板升级授权
        --help        显示帮助（也支持 --cli --help）
        无参数        打开原图形安装向导

        安装为开机自启的 Windows 服务，首次安装的管理员终端和面板升级默认关闭。
        无需另装 .NET、Node.js 或 Python。绑定码在提示后输入，不放进命令参数。
        卸载待重启时先重启 Windows，CLI 不会删除标记或绕过清理检查。
        退出码：0 接入验证通过；1 安装或验证失败；2 未确认/参数错误；130 中止。
        """);

    internal async Task<int> Run(CancellationToken token)
    {
        bool installationStarted = false;
        try
        {
            _output.WriteLine("Howie Monitor · 交互式 CLI 安装");
            _checkEnvironment();
            var marker = _existing();
            _output.WriteLine("电脑：" + Environment.MachineName + "\n上传服务：" + SetupPaths.ServerUrl);
            _output.WriteLine("当前安装账号：" + Environment.UserDomainName + "\\" + Environment.UserName);
            await _client.Check(token);
            var plan = await Restore(marker, token);
            if (plan is null) plan = await Claim(token);
            SetupPaths.ValidateConfiguration(plan.Configuration);
            bool previousAccess = plan.Configuration.Terminal is { Enabled: true } access && access.UserSid == _ownerSid;
            _output.WriteLine("管理员终端可修改这台电脑的文件和系统设置。只使用当前安装账号；注销后不可用，不保存 Windows 密码。");
            bool terminal = await Confirm("允许私人面板使用当前账号运行管理员 PowerShell", previousAccess, token);
            _output.WriteLine("面板升级仅接受本服务发布、经校验的采集端安装包；安装会重启采集服务，不开放任意命令执行。");
            bool updates = await Confirm("允许从私人面板确认升级采集端", plan.Configuration.Updates is { Enabled: true }, token);
            plan = plan with { OwnerSid = _ownerSid, Configuration = plan.Configuration with { Terminal = terminal ? new(true, _ownerSid) : null, Updates = new(updates) } };
            _output.WriteLine("安装位置：" + SetupPaths.ProgramRoot + "\n本地数据：" + SetupPaths.DataRoot);
            _output.WriteLine("服务开机自启，以 LocalSystem 采集硬件和进程指标，通过 HTTPS 上传私人面板。管理员终端：" + (terminal ? "允许当前账号" : "关闭"));
            _output.WriteLine("允许面板升级：" + (updates ? "开启" : "关闭") + "。采集端版本：" + AgentBuild.Current.Version + " / " + AgentBuild.Current.BuildId);
            if (_previousComputer is not null)
                _output.WriteLine("当前监控电脑：" + _previousComputer + "。本机通过真实采样验证并激活后才切换，旧凭据届时失效。");
            else _output.WriteLine("本次操作不会停止其他电脑的采集。");
            _output.WriteLine("隐私说明：https://github.com/yexianglun-d/HowieMonitor/blob/main/PRIVACY.md");
            if (!await Confirm(marker is null ? "确认上述数据上传和安装操作" : "确认上述数据上传和修复/升级操作", false, token))
            { _output.WriteLine("未确认安装，未执行服务安装操作。"); return 2; }
            _secureWork(); Save(new(Environment.MachineName, plan, _previousComputer));
            var authorization = await _client.Status(plan.Configuration.Token, token);
            if (authorization.State == "expired") throw new SetupCredentialException();
            if (authorization.State is not ("waiting" or "ready" or "active"))
                throw new InvalidOperationException("接入授权状态不允许安装，请在面板重新生成绑定码。");
            if (authorization.ComputerName is not null && authorization.ComputerName != Environment.MachineName)
                throw new InvalidOperationException("云端电脑名称与本机不一致，请重新核对绑定码。");
            _output.WriteLine("正在安装并启动 Windows 服务…");
            installationStarted = true; await _install(plan, token);
            _output.WriteLine("服务安装操作已返回，正在验证注册、心跳、真实采样和云端激活…");
            await Verify(plan, token);
            try { ClearPending(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            { _output.WriteLine("接入已确认，但临时安装记录未能清理；下次运行可重新核对。"); }
            _output.WriteLine("接入验证通过。面板：" + SetupPaths.ServerUrl);
            return 0;
        }
        catch (SetupCredentialException)
        { _output.WriteLine("接入凭据已过期或撤销。请在面板为这台电脑生成新绑定码，再重新运行 --cli。"); }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        { _output.WriteLine(installationStarted ? "操作已中止；已写入的安装文件和服务保留，重新运行 --cli 检查状态。" : "操作已中止。"); return 130; }
        catch (TaskCanceledException) { _output.WriteLine("HTTPS 请求超时。安装请求已保留，可重新运行 --cli 重试。"); }
        catch (OperationCanceledException) { _output.WriteLine("输入已结束，操作中止。"); return 130; }
        catch (HttpRequestException) { _output.WriteLine("HTTPS 网络或证书验证失败，请检查网络和电脑时间后重试。"); }
        catch (InvalidOperationException ex) { _output.WriteLine(ex.Message); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or System.Text.Json.JsonException)
        { _output.WriteLine("安装未完成（" + ex.GetType().Name + "）。请检查文件访问权限和服务状态，再重新运行 --cli。"); }
        return 1;
    }
    private static void CheckEnvironment()
    {
        if (!SetupPaths.IsAdministrator) throw new InvalidOperationException("请以当前管理员账号打开管理员 PowerShell，再运行 --cli。");
        if (RuntimeInformation.OSArchitecture != Architecture.X64 || Environment.OSVersion.Version.Build < 22000)
            throw new InvalidOperationException("安装程序支持 Windows 11 x64。");
        if (new DriveInfo(Path.GetPathRoot(SetupPaths.ProgramRoot)!).AvailableFreeSpace < 1024L * 1024 * 1024)
            throw new InvalidOperationException("安装盘至少需要 1 GiB 可用空间。");
        PackageContents.Check();
    }
    private async Task<InstallPlan?> Restore(InstallMarker? marker, CancellationToken token)
    {
        if (marker is not null && _readConfiguration() is { } configuration)
        {
            SetupPaths.ValidateConfiguration(configuration);
            try
            {
                var status = await _client.Status(configuration.Token, token);
                if (status.ComputerName == Environment.MachineName && status.State is "active" or "ready" or "waiting")
                { _output.WriteLine("检测到已有安装，将修复/升级并保留电脑身份。"); return new(configuration, status.State == "active" ? null : marker.RequestId, _ownerSid, null); }
            }
            catch (SetupCredentialException) { _output.WriteLine("已有接入凭据失效，需要重新绑定。"); }
        }
        var saved = Read<SetupUiState>("state.json");
        if (saved is not null && saved.ComputerName == Environment.MachineName && saved.Plan.OwnerSid == _ownerSid)
        {
            SetupPaths.ValidateConfiguration(saved.Plan.Configuration);
            try
            {
                var status = await _client.Status(saved.Plan.Configuration.Token, token);
                if (status.ComputerName == Environment.MachineName && status.State is "ready" or "waiting" or "active")
                { _previousComputer = saved.PreviousComputer; _output.WriteLine("已恢复上次安装请求。"); return saved.Plan; }
            }
            catch (SetupCredentialException) { ClearPending(); }
        }
        return null;
    }
    private async Task<InstallPlan> Claim(CancellationToken token)
    {
        _output.WriteLine("请在私人面板的“电脑接入”中生成绑定码。");
        string code;
        do
        {
            code = (await ReadLine("绑定码：", token)).Trim();
            if (code.Length > 40) _output.WriteLine("绑定码过长，请核对后重新输入。");
        } while (code.Length is 0 or > 40);
        _secureWork();
        string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(code.Replace("-", "", StringComparison.Ordinal).ToUpperInvariant())));
        var attempt = Read<ClaimAttempt>("claim-request.json");
        string request = attempt is not null && attempt.ComputerName == Environment.MachineName && attempt.CodeHash == hash ? attempt.RequestId : Guid.NewGuid().ToString();
        // 先持久化请求身份，兑换响应丢失时仍能用同一绑定码恢复同一请求。
        Write("claim-request.json", new ClaimAttempt(Environment.MachineName, hash, request));
        var credential = await _client.Claim(new(code, request, Environment.MachineName), token);
        _previousComputer = credential.PreviousComputerName;
        var plan = new InstallPlan(new(SetupPaths.ServerUrl, credential.DeviceId, credential.Token, Path.Combine(SetupPaths.DataRoot, "queue"), false), request, _ownerSid, null);
        Save(new(Environment.MachineName, plan, credential.PreviousComputerName, credential.ExpiresAt));
        _output.WriteLine(credential.PreservesHistory ? "原电脑身份已核对，将保留历史。" : "绑定码已核对。");
        return plan;
    }
    private async Task Verify(InstallPlan plan, CancellationToken token)
    {
        var deadline = _utcNow().AddMinutes(1);
        while (_utcNow() < deadline)
        {
            token.ThrowIfCancellationRequested();
            if (!_serviceRunning()) throw new InvalidOperationException("后台服务未运行。请重新运行 --cli 修复并启动服务。");
            var progress = await _client.Status(plan.Configuration.Token, token);
            if (progress.ComputerName is not null && progress.ComputerName != Environment.MachineName)
                throw new InvalidOperationException("云端电脑名称与本机不一致，请重新核对绑定码。");
            if (progress.State == "expired") throw new SetupCredentialException();
            if (progress.DataStatus == "clockError") throw new InvalidOperationException("服务已安装，但电脑时间异常。请同步系统时间后重新运行 --cli 验证。");
            if (progress.State == "ready" && plan.RequestId is not null) progress = await _client.Activate(plan, token);
            if (progress.State == "active" && progress.Registered && progress.HeartbeatReceived && progress.DataStatus == "fresh" && progress.ComputerName == Environment.MachineName)
            {
                if (plan.Legacy is not null && !LegacyDetection.StopVerified(plan.Legacy))
                    _output.WriteLine("旧手动实例未能安全核对关闭，请在本机退出原采集程序。");
                return;
            }
            await _delay(token);
        }
        throw new InvalidOperationException("服务安装操作已完成，但一分钟内未收到完整实时数据，接入尚未确认。请检查网络和电脑时间后重新运行 --cli。");
    }
    private async Task<bool> Confirm(string prompt, bool defaultYes, CancellationToken token)
    {
        while (true)
        {
            string answer = (await ReadLine(prompt + (defaultYes ? " [Y/n]：" : " [y/N]："), token)).Trim().ToLowerInvariant();
            if (answer.Length == 0) return defaultYes;
            if (answer is "y" or "yes" or "是") return true;
            if (answer is "n" or "no" or "否") return false;
            _output.WriteLine("请输入 y 或 n。");
        }
    }
    private async Task<string> ReadLine(string prompt, CancellationToken token)
    {
        _output.Write(prompt); await _output.FlushAsync(token);
        return await _input.ReadLineAsync(token).AsTask().WaitAsync(token) ?? throw new OperationCanceledException("输入已结束。");
    }
    private T? Read<T>(string name)
    {
        string path = Path.Combine(_workRoot, name); SetupPaths.VerifyPath(path, _workRoot);
        if (File.Exists(path) && new FileInfo(path).Length > 65536) throw new InvalidOperationException("临时安装记录无效，请先核对安装缓存。");
        return SetupPaths.Read<T>(path);
    }
    private void Write<T>(string name, T value)
    { string path = Path.Combine(_workRoot, name); SetupPaths.VerifyPath(path, _workRoot); SetupPaths.Write(path, value); }
    private void Save(SetupUiState state) => Write("state.json", state);
    private void ClearPending()
    {
        foreach (string name in new[] { "state.json", "plan.json", "claim-request.json" })
        { string path = Path.Combine(_workRoot, name); SetupPaths.VerifyPath(path, _workRoot); if (File.Exists(path)) File.Delete(path); }
    }
}
