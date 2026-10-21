using System.Diagnostics;
using System.Security.AccessControl;
using System.Security.Principal;

namespace Howie.Monitor.Setup;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        string? uninstallStage = null;
        try
        {
            if (args is ["--staged-uninstall", var id]) { uninstallStage = SetupUninstall.EnterStage(id); args = ["--uninstall"]; }
            if (!SetupUninstall.RelaunchIfNeeded(args)) Run(args);
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223) { Environment.ExitCode = 130; }
        catch (Exception ex)
        { MessageBox.Show(ex is InvalidOperationException ? ex.Message : "安装维护未能启动，请检查文件权限后重试。", "Howie Monitor", MessageBoxButtons.OK, MessageBoxIcon.Error); Environment.ExitCode = 1; }
        finally
        {
            if (uninstallStage is not null)
                try { SetupUninstall.RemoveStageAfterExit(uninstallStage); }
                catch (Exception ex) when (ex is IOException or System.ComponentModel.Win32Exception or InvalidOperationException)
                { MessageBox.Show("临时卸载向导未能自动清理，可关闭窗口后重新运行新版安装包检查。", "Howie Monitor", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
        }
    }
    private static void Run(string[] args)
    {
        if (args is [var updateMode, var updateId] && updateMode is "--apply-update" or "--recover-update")
        {
            try { Environment.ExitCode = SetupUpdater.Run(updateId, args[0] == "--recover-update").GetAwaiter().GetResult(); }
            catch (InvalidOperationException ex) when (ex.InnerException is IOException) { Environment.ExitCode = 1; }
            catch (Exception ex) { SetupUpdater.RecordStartFailure(updateId); Console.Error.WriteLine("后台升级未完成（" + ex.GetType().Name + "），请检查本机维护状态。"); Environment.ExitCode = 1; }
            return;
        }
        if (args.Contains("--cli", StringComparer.Ordinal) || args is ["--help"])
        {
            RunCli(args); return;
        }
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
    private static void RunCli(string[] args)
    {
        try
        {
            SetupConsole.Attach();
            if (args is ["--help"] or ["--cli", "--help"]) { SetupCli.Help(Console.Out); return; }
            if (args is not ["--cli"]) { SetupCli.Help(Console.Error); Environment.ExitCode = 2; return; }
            using var mutex = new Mutex(true, "Local\\HowieMonitorSetup", out bool unique);
            if (!unique) { Console.Error.WriteLine("安装或卸载向导已在运行，请先关闭已有窗口。"); Environment.ExitCode = 1; return; }
            using var cancellation = new CancellationTokenSource();
            ConsoleCancelEventHandler cancel = (_, e) => { e.Cancel = true; cancellation.Cancel(); };
            Console.CancelKeyPress += cancel;
            try
            {
                using var client = new SetupClient();
                var cli = new SetupCli(client, Console.In, Console.Out, SetupPaths.WorkRoot, SetupPaths.CurrentSid);
                Environment.ExitCode = cli.Run(cancellation.Token).GetAwaiter().GetResult();
            }
            finally { Console.CancelKeyPress -= cancel; }
        }
        catch (Exception ex)
        { Console.Error.WriteLine("CLI 未能启动（" + ex.GetType().Name + "）。请从管理员 PowerShell 运行。"); Environment.ExitCode = 1; }
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
