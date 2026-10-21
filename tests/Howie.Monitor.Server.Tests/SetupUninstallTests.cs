using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Howie.Monitor.Setup;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;
using Xunit;

namespace Howie.Monitor.Server.Tests;

public sealed class SetupUninstallTests
{
    [Fact]
    public void LegacyMarkerRecoversOnlyAfterFilesAndOwnedRebootTasksAreCleared()
    {
        using var fixture = new Fixture(); fixture.Pending(false);
        File.WriteAllText(Path.Combine(fixture.Paths.ProgramRoot, "HowieMonitorSetup.exe"), "old binary");
        int cancelled = 0, unregistered = 0;
        SetupUninstall.RecoverPending(fixture.Paths, true, () => false, () =>
        {
            Assert.True(File.Exists(fixture.Paths.PendingFile));
            Assert.False(File.Exists(Path.Combine(fixture.Paths.ProgramRoot, "HowieMonitorSetup.exe")));
            cancelled++;
        }, () => unregistered++);
        Assert.Equal(1, cancelled); Assert.Equal(1, unregistered);
        Assert.False(Directory.Exists(fixture.Paths.ProgramRoot));
        Assert.True(File.Exists(Path.Combine(fixture.Paths.DataRoot, "agent.json")));
        Assert.False(Directory.Exists(fixture.Paths.UpdatesRoot));
        Assert.True(File.Exists(fixture.Unrelated));
        SetupUninstall.RecoverPending(fixture.Paths, true, () => true, () => throw new Exception(), () => throw new Exception());
    }

    [Fact]
    public void CompleteRemovalDeletesLocalIdentityQueueUpdatesAndInstallerCache()
    {
        using var fixture = new Fixture(); fixture.Pending(true);
        SetupUninstall.RecoverPending(fixture.Paths, true, () => false, () => { }, () => { });
        foreach (string root in new[] { fixture.Paths.ProgramRoot, fixture.Paths.DataRoot, fixture.Paths.UpdatesRoot, fixture.Paths.WorkRoot })
            Assert.False(Directory.Exists(root));
        Assert.True(File.Exists(fixture.Unrelated));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public void MissingElevationOrExistingServiceDoesNotDeleteAnything(bool admin, bool service)
    {
        using var fixture = new Fixture(); fixture.Pending(true);
        Assert.Throws<InvalidOperationException>(() => SetupUninstall.RecoverPending(fixture.Paths, admin, () => service, () => throw new Exception(), () => throw new Exception()));
        Assert.True(File.Exists(fixture.Paths.PendingFile));
        Assert.True(File.Exists(Path.Combine(fixture.Paths.DataRoot, "agent.json")));
    }

    [Fact]
    public void WrongProductOrUnownedDataIsNeverRemoved()
    {
        using var fixture = new Fixture();
        SetupPaths.Write(fixture.Paths.PendingFile, new PendingUninstall("other-product", true));
        Assert.Throws<InvalidOperationException>(() => SetupUninstall.RecoverPending(fixture.Paths, true, () => false, () => { }, () => { }));
        fixture.Pending(true); File.WriteAllText(Path.Combine(fixture.Paths.DataRoot, "product.json"), "{}");
        Assert.Throws<InvalidOperationException>(() => SetupUninstall.RecoverPending(fixture.Paths, true, () => false, () => { }, () => { }));
        Assert.True(File.Exists(Path.Combine(fixture.Paths.DataRoot, "agent.json")));
    }

    [Fact]
    public void LockedFileRetainsMarkerAndRebootTasksUntilNextAttempt()
    {
        using var fixture = new Fixture(); fixture.Pending(true);
        string locked = Path.Combine(fixture.Paths.ProgramRoot, "HowieMonitorSetup.exe"); File.WriteAllText(locked, "locked binary");
        using (var handle = new FileStream(locked, FileMode.Open, FileAccess.Read, FileShare.Read))
            Assert.Throws<InvalidOperationException>(() => SetupUninstall.RecoverPending(fixture.Paths, true, () => false, () => throw new Exception("must not cancel"), () => throw new Exception("must not unregister")));
        Assert.True(File.Exists(fixture.Paths.PendingFile));
        SetupUninstall.RecoverPending(fixture.Paths, true, () => false, () => { }, () => { });
        Assert.False(Directory.Exists(fixture.Paths.ProgramRoot));
    }

    [Fact]
    public void FailedRegistryRecoveryKeepsMarkerAndCanResume()
    {
        using var fixture = new Fixture(); fixture.Pending(false);
        Assert.Throws<IOException>(() => SetupUninstall.RecoverPending(fixture.Paths, true, () => false, () => throw new IOException("transaction conflict"), () => { }));
        Assert.True(File.Exists(fixture.Paths.PendingFile));
        SetupUninstall.RecoverPending(fixture.Paths, true, () => false, () => { }, () => { });
        Assert.False(Directory.Exists(fixture.Paths.ProgramRoot));
    }

    [Fact]
    public void LockedQueuePreservesDataOwnershipForACompleteRemovalRetry()
    {
        using var fixture = new Fixture(); fixture.Pending(true);
        string queue = Path.Combine(fixture.Paths.DataRoot, "queue"); Directory.CreateDirectory(queue);
        string sample = Path.Combine(queue, "pending.json"); File.WriteAllText(sample, "queued sample");
        using (var handle = new FileStream(sample, FileMode.Open, FileAccess.Read, FileShare.Read))
            Assert.Throws<InvalidOperationException>(() => SetupUninstall.RecoverPending(fixture.Paths, true, () => false, () => throw new Exception(), () => { }));
        Assert.True(File.Exists(Path.Combine(fixture.Paths.DataRoot, "product.json")));
        SetupUninstall.RecoverPending(fixture.Paths, true, () => false, () => { }, () => { });
        Assert.False(Directory.Exists(fixture.Paths.DataRoot));
    }

    [Fact]
    public void DirectoryLinksCannotExpandRemovalToAnotherDirectory()
    {
        using var fixture = new Fixture(); fixture.Pending(true);
        Directory.CreateSymbolicLink(Path.Combine(fixture.Paths.ProgramRoot, "linked"), fixture.Paths.DataRoot);
        Assert.Throws<InvalidOperationException>(() => SetupUninstall.RecoverPending(fixture.Paths, true, () => false, () => { }, () => { }));
        Assert.True(File.Exists(Path.Combine(fixture.Paths.DataRoot, "agent.json")));
        Directory.Delete(Path.Combine(fixture.Paths.ProgramRoot, "linked"));
    }

    [Fact]
    public void RebootPairsPreserveOtherSoftwareEmptyDestinationsAndOrder()
    {
        string root = @"C:\Program Files\HowieMonitor";
        byte[] other = Multi(@"\??\C:\Other\delete.exe", "", @"\??\C:\Other\before.dll", @"\??\C:\Other\after.dll");
        byte[] input = Multi(@"\??\C:\Other\delete.exe", "", @"\??\C:\Program Files\HowieMonitor\HowieMonitorSetup.exe", "", @"\??\C:\Other\before.dll", @"\??\C:\Other\after.dll", @"\??\C:\Program Files\HowieMonitor", "");
        Assert.Equal(other, PendingFileDeletes.WithoutOwnedDeletes(input, root));
        Assert.Equal(other, PendingFileDeletes.WithoutOwnedDeletes(other, root));
        Assert.Equal(Multi(@"\??\C:\Program Files\HowieMonitor-other\keep.exe", ""), PendingFileDeletes.WithoutOwnedDeletes(Multi(@"\??\C:\Program Files\HowieMonitor-other\keep.exe", ""), root));
    }

    [Fact]
    public void RebootMovesAndMalformedPairsAreNotSilentlyDropped()
    {
        const string root = @"C:\Program Files\HowieMonitor";
        Assert.Throws<InvalidOperationException>(() => PendingFileDeletes.WithoutOwnedDeletes(Multi(root + @"\a", root + @"\b"), root));
        Assert.Throws<InvalidDataException>(() => PendingFileDeletes.WithoutOwnedDeletes([1], root));
        Assert.Throws<InvalidDataException>(() => PendingFileDeletes.WithoutOwnedDeletes(Encoding.Unicode.GetBytes("unterminated"), root));
    }

    [Fact]
    public void RegistryTransactionOnlyChangesTheDisposableTestValue()
    {
        string name = @"Software\HowieMonitor.Tests\" + Guid.NewGuid().ToString("N");
        const string root = @"C:\Program Files\HowieMonitor";
        using var key = Registry.CurrentUser.CreateSubKey(name);
        try
        {
            byte[] original = Multi(root + @"\HowieMonitorSetup.exe", "", @"C:\Other\keep.exe", "");
            Assert.Equal(0, RegSetValueEx(key.Handle, "pending", 0, 7, original, (uint)original.Length));
            key.SetValue("unrelated", "retained");
            PendingFileDeletes.RemoveOwnedDeletes(Registry.CurrentUser.Handle, name, "pending", root);
            uint size = 1024; var actual = new byte[size];
            Assert.Equal(0, RegQueryValueEx(key.Handle, "pending", IntPtr.Zero, out uint type, actual, ref size));
            Assert.Equal(7u, type); Assert.Equal(Multi(@"C:\Other\keep.exe", ""), actual[..(int)size]);
            Assert.Equal("retained", key.GetValue("unrelated"));
        }
        finally { Registry.CurrentUser.DeleteSubKeyTree(name, false); }
    }

    [Fact]
    public void SystemQueueSupportsReadOnlyInspectionForAnUnqueuedTemporaryPath()
    {
        using var fixture = new Fixture();
        // 本例不登记任何开机任务。唯一临时路径不可能匹配系统已有项，生产方法不会写入或提交。
        PendingFileDeletes.RemoveOwnedDeletes(fixture.Paths.ProgramRoot);
        Assert.True(File.Exists(fixture.Unrelated));
    }

    [Fact]
    public async Task StageCleanupWaitsForItsParentAndOnlyRemovesTheUniqueStage()
    {
        using var fixture = new Fixture();
        string stages = Path.Combine(fixture.Root, "stages & ' quotes"), stage = Path.Combine(stages, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(stage); File.WriteAllText(Path.Combine(stage, "HowieMonitorSetup.exe"), "temporary helper");
        string sibling = Path.Combine(stages, "keep.txt"); File.WriteAllText(sibling, "not this stage");
        using var parent = PowerShell("[Console]::ReadLine() | Out-Null", input: true);
        string request = Request(stage, parent.Id, parent.StartTime.ToUniversalTime().Ticks);
        using var cleanup = PowerShell(SetupUninstall.CleanupScript(request, stages));
        Assert.True(Directory.Exists(stage));
        parent.StandardInput.WriteLine("exit"); parent.StandardInput.Close();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        try { await parent.WaitForExitAsync(deadline.Token); await cleanup.WaitForExitAsync(deadline.Token); }
        finally { Stop(parent); Stop(cleanup); }
        string error = await cleanup.StandardError.ReadToEndAsync(deadline.Token);
        Assert.True(cleanup.ExitCode == 0, error); Assert.False(Directory.Exists(stage)); Assert.True(File.Exists(sibling));
    }

    [Fact]
    public async Task StageCleanupRejectsSiblingPrefixesAndDoesNotWaitForReusedPid()
    {
        using var fixture = new Fixture();
        string stages = Path.Combine(fixture.Root, "stages"), wrong = Path.Combine(fixture.Root, "stages-other", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(wrong);
        using var current = Process.GetCurrentProcess();
        using var rejected = PowerShell(SetupUninstall.CleanupScript(Request(wrong, current.Id, current.StartTime.ToUniversalTime().Ticks), stages));
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        try { await rejected.WaitForExitAsync(deadline.Token); } finally { Stop(rejected); }
        Assert.Equal(2, rejected.ExitCode); Assert.True(Directory.Exists(wrong));
        string stage = Path.Combine(stages, Guid.NewGuid().ToString("N")); Directory.CreateDirectory(stage);
        using var cleanup = PowerShell(SetupUninstall.CleanupScript(Request(stage, current.Id, 0), stages));
        try { await cleanup.WaitForExitAsync(deadline.Token); } finally { Stop(cleanup); }
        Assert.Equal(0, cleanup.ExitCode); Assert.False(Directory.Exists(stage));
    }

    private static byte[] Multi(params string[] fields) => Encoding.Unicode.GetBytes(string.Join('\0', fields) + "\0\0");
    private static string Request(string stage, int pid, long started) => Convert.ToBase64String(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
    { product = SetupPaths.Product, stage, pid, started = started.ToString(System.Globalization.CultureInfo.InvariantCulture) })));
    private static Process PowerShell(string script, bool input = false)
    {
        var start = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), @"WindowsPowerShell\v1.0\powershell.exe"))
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = input, RedirectStandardError = true, RedirectStandardOutput = true };
        start.ArgumentList.Add("-NoLogo"); start.ArgumentList.Add("-NoProfile"); start.ArgumentList.Add("-NonInteractive");
        start.ArgumentList.Add("-EncodedCommand"); start.ArgumentList.Add(Convert.ToBase64String(Encoding.Unicode.GetBytes(script)));
        start.Environment["PSModulePath"] = Path.Combine(Path.GetDirectoryName(start.FileName)!, "Modules");
        return Process.Start(start)!;
    }
    private static void Stop(Process process) { if (!process.HasExited) { process.Kill(true); process.WaitForExit(); } }

    private sealed class Fixture : IDisposable
    {
        internal string Root { get; } = Path.Combine(Path.GetTempPath(), "HowieMonitor-uninstall-tests-" + Guid.NewGuid().ToString("N"));
        internal UninstallLocations Paths { get; }
        internal string Unrelated => Path.Combine(Root, "unrelated.txt");
        internal Fixture()
        {
            Paths = new(Path.Combine(Root, "program"), Path.Combine(Root, "data"), Path.Combine(Root, "updates"), Path.Combine(Root, "cache"));
            foreach (string path in new[] { Paths.ProgramRoot, Paths.DataRoot, Paths.UpdatesRoot, Paths.WorkRoot }) Directory.CreateDirectory(path);
            File.WriteAllText(Unrelated, "not the product");
            File.WriteAllText(Path.Combine(Paths.DataRoot, "agent.json"), "private identity");
            File.WriteAllText(Path.Combine(Paths.UpdatesRoot, "snapshot.json"), "update snapshot");
            File.WriteAllText(Path.Combine(Paths.WorkRoot, "state.json"), "pending installer state");
            SetupPaths.Write(Path.Combine(Paths.DataRoot, "product.json"), new Dictionary<string, string> { ["product"] = SetupPaths.Product });
        }
        internal void Pending(bool removeData) => SetupPaths.Write(Paths.PendingFile, new PendingUninstall(SetupPaths.Product, removeData));
        public void Dispose() { if (Directory.Exists(Root)) Directory.Delete(Root, true); }
    }

    [DllImport("advapi32.dll", EntryPoint = "RegSetValueExW", CharSet = CharSet.Unicode)]
    private static extern int RegSetValueEx(SafeRegistryHandle key, string value, uint reserved, uint type, byte[] data, uint size);
    [DllImport("advapi32.dll", EntryPoint = "RegQueryValueExW", CharSet = CharSet.Unicode)]
    private static extern int RegQueryValueEx(SafeRegistryHandle key, string value, IntPtr reserved, out uint type, [Out] byte[] data, ref uint size);
}
