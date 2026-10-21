using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Howie.Monitor.Server;
using Xunit;

namespace Howie.Monitor.Server.Tests;

// Windows PowerShell 5.1 解析完整脚本并执行原文中的下载块；跳过系统预检，使用临时目录和进程/网络替身。
public sealed class SetupBootstrapScriptTests
{
    [Theory]
    [InlineData("success", 0)]
    [InlineData("declined", 2)]
    [InlineData("canceled", 130)]
    [InlineData("installer-error", 1)]
    [InlineData("wrong-hash", 1)]
    [InlineData("truncated", 1)]
    [InlineData("download-error", 1)]
    public async Task NativePowerShellChecksTheDownloadAndPreservesInstallerResults(string scenario, int expectedCode)
    {
        string payload = "harmless bootstrap test package", hash = Convert.ToHexString(SHA256.HashData(Encoding.ASCII.GetBytes(payload)));
        string bootstrap = SetupBootstrap.Script(ServerHosting.Create(false, "http://127.0.0.1:5080", "https://monitor.example/"), new(true, "0.1.0", "HowieMonitorSetup.exe", payload.Length, hash));
        string prefix = $$"""
            $scenario = '{{scenario}}'
            $script:launched = $false
            $script:download = $null
            $global:LASTEXITCODE = 1
            $work = Join-Path ([IO.Path]::GetTempPath()) ('howie-bootstrap-test-' + [Guid]::NewGuid().ToString('N'))
            $created = $false
            $ErrorActionPreference = 'Stop'
            $ProgressPreference = 'SilentlyContinue'
            function Write-Host { param($Object) [Console]::WriteLine([string]$Object) }
            function Invoke-WebRequest {
                param($Uri, $OutFile, [switch]$UseBasicParsing, $MaximumRedirection, $TimeoutSec, $ErrorAction)
                if ($Uri -ne 'https://monitor.example/install/{{hash}}/HowieMonitorSetup.exe' -or $MaximumRedirection -ne 0) { throw 'unexpected download source' }
                $script:download = $OutFile
                if ($scenario -eq 'download-error') { throw 'simulated unavailable download' }
                $content = '{{payload}}'
                if ($scenario -eq 'wrong-hash') { $content = 'X' * $content.Length }
                if ($scenario -eq 'truncated') { $content = 'short' }
                [IO.File]::WriteAllText($OutFile, $content, [Text.Encoding]::ASCII)
            }
            function Start-Process {
                param($FilePath, $ArgumentList, [switch]$NoNewWindow, [switch]$Wait, [switch]$PassThru, $ErrorAction)
                if (-not $NoNewWindow -or -not $Wait -or -not $PassThru -or $ArgumentList -ne '--cli' -or $FilePath -ne $script:download) { throw 'invalid CLI launch' }
                $script:launched = $true
                $code = switch ($scenario) { 'declined' { 2 } 'canceled' { 130 } 'installer-error' { 1 } default { 0 } }
                [pscustomobject]@{ ExitCode = $code }
            }
            $messages = @()
            try {
            """;
        string suffix = $$"""
            } catch { $messages += $_.Exception.Message }
            $expectedLaunch = $scenario -in @('success', 'declined', 'canceled', 'installer-error')
            if ($script:launched -ne $expectedLaunch) { throw ('unexpected launch status: ' + ($messages -join ';')) }
            if ($global:LASTEXITCODE -ne {{expectedCode}}) { throw 'installer exit code was not preserved' }
            if (-not $script:download -or (Test-Path -LiteralPath (Split-Path -Parent $script:download))) { throw 'download directory was not cleaned' }
            if (($scenario -in @('success', 'declined', 'canceled')) -and $messages.Count) { throw ($messages -join ';') }
            if (($scenario -notin @('success', 'declined', 'canceled')) -and -not $messages.Count) { throw 'failure was not reported' }
            Write-Output 'BOOTSTRAP_TEST_PASSED'
            exit 0
            """;
        string command = prefix + "\n" + $$"""
            $source = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('{{Convert.ToBase64String(Encoding.UTF8.GetBytes(bootstrap))}}'))
            $parsed = [scriptblock]::Create($source)
            $flow = @($parsed.Ast.FindAll({ param($node) $node -is [Management.Automation.Language.TryStatementAst] -and $node.Body.Extent.Text.Contains('Invoke-WebRequest') }, $true))
            if ($flow.Count -ne 1) { throw 'expected one download block' }
            & ([scriptblock]::Create($flow[0].Extent.Text))
            """ + "\n" + suffix;
        var start = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe"))
        { UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        // CI 从 PowerShell 7 启动测试，5.1 子进程必须使用自己的系统模块，不能继承另一版本的模块路径。
        start.Environment["PSModulePath"] = Path.Combine(Path.GetDirectoryName(start.FileName)!, "Modules");
        foreach (string argument in new[] { "-NoProfile", "-NonInteractive", "-OutputFormat", "Text", "-EncodedCommand", Convert.ToBase64String(Encoding.Unicode.GetBytes(command)) }) start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!; process.StandardInput.Close();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var output = process.StandardOutput.ReadToEndAsync(deadline.Token); var error = process.StandardError.ReadToEndAsync(deadline.Token);
        try { await process.WaitForExitAsync(deadline.Token); }
        finally { if (!process.HasExited) { process.Kill(true); await process.WaitForExitAsync(); } }
        string stdout = await output, stderr = await error;
        Assert.True(process.ExitCode == 0, $"PowerShell exited {process.ExitCode}: {stdout}\n{stderr}");
        Assert.Contains("BOOTSTRAP_TEST_PASSED", stdout); Assert.Equal("", stderr);
    }
}
