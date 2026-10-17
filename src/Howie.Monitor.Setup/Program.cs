using System.Diagnostics;
using System.Security.AccessControl;
using System.Security.Principal;

namespace Howie.Monitor.Setup;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        if (args is ["--check-package"])
        {
            try { Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(PackageContents.Check())); }
            catch (Exception ex) { Console.WriteLine(ex.GetType().Name + ": 安装包检查失败。"); Environment.ExitCode = 1; }
            return;
        }
        if (args is ["--remove-installed", var dataChoice])
        {
            try { InstallEngine.Uninstall(dataChoice == "remove-data", CancellationToken.None).GetAwaiter().GetResult(); Environment.ExitCode = 0; }
            catch (Exception ex) { MessageBox.Show(ex is InvalidOperationException ? ex.Message : "卸载未完成，请检查服务状态后重试。", "Howie Monitor", MessageBoxButtons.OK, MessageBoxIcon.Error); Environment.ExitCode = 1; }
            return;
        }
        if (args is ["--apply-install", var planPath])
        {
            try
            {
                if (!SetupPaths.IsAdministrator) throw new InvalidOperationException("安装尚未获得管理员授权。");
                SetupPaths.VerifyPath(planPath, Path.GetDirectoryName(planPath)!);
                if (new FileInfo(planPath).Length > 65536 || Path.GetFileName(planPath) != "plan.json") throw new InvalidOperationException("安装请求文件无效。");
                var plan = SetupPaths.Read<InstallPlan>(planPath) ?? throw new InvalidOperationException("安装请求不存在。");
                var owner = new FileInfo(planPath).GetAccessControl(AccessControlSections.Owner).GetOwner(typeof(SecurityIdentifier))?.Value;
                if (owner != plan.OwnerSid && owner != new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null).Value)
                    throw new InvalidOperationException("安装请求管理者身份不一致。");
                InstallEngine.Apply(plan, Environment.ProcessPath!, CancellationToken.None).GetAwaiter().GetResult(); Environment.ExitCode = 0;
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex is InvalidOperationException ? ex.Message : $"安装未完成（{ex.GetType().Name}），请返回向导重试。", "Howie Monitor", MessageBoxButtons.OK, MessageBoxIcon.Error);
                Environment.ExitCode = 1;
            }
            return;
        }
        using var mutex = new Mutex(true, "Local\\HowieMonitorSetup", out bool unique);
        if (!unique) { MessageBox.Show("安装向导已在运行，请切回已有窗口。", "Howie Monitor"); return; }
        using var form = new SetupForm(args.Contains("--uninstall", StringComparer.Ordinal));
#if DEBUG
        if (args.Contains("--preview", StringComparer.Ordinal))
        {
            Directory.CreateDirectory("reports"); form.PreviewOnly = true; form.ShowInTaskbar = false; form.Opacity = 0; form.Show(); Application.DoEvents(); form.PerformLayout();
            using var bitmap = new Bitmap(form.Width, form.Height); form.DrawToBitmap(bitmap, new(0, 0, form.Width, form.Height)); bitmap.Save("reports/setup-welcome.png"); return;
        }
#endif
        Application.Run(form);
    }
    public static async Task RunElevated(InstallPlan plan, CancellationToken cancellationToken)
    {
        if (SetupPaths.IsAdministrator) { await InstallEngine.Apply(plan, Environment.ProcessPath!, cancellationToken); return; }
        SetupPaths.PrivateDirectory(SetupPaths.WorkRoot, plan.OwnerSid);
        SetupPaths.Write(SetupPaths.PlanFile, plan);
        var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = true, Verb = "runas", Arguments = "--apply-install \"" + SetupPaths.PlanFile + "\"", WindowStyle = ProcessWindowStyle.Hidden };
        using var process = Process.Start(start) ?? throw new InvalidOperationException("未能启动安装操作。");
        await process.WaitForExitAsync(cancellationToken);
        if (process.ExitCode != 0) throw new InvalidOperationException("安装操作未完成，已有监控设备保持原状，请检查提示后重试。");
    }
}
