using System.Text;

namespace Howie.Monitor.Server;

public static class SetupBootstrap
{
    public static string Command(ServerHosting hosting) =>
        "& ([scriptblock]::Create((Invoke-WebRequest -UseBasicParsing '" + Origin(hosting) + "install.ps1' -ErrorAction Stop).Content))";

    private static string Origin(ServerHosting hosting) => (hosting.PublicUrl ?? hosting.ListenUrl).AbsoluteUri;

    public static void Map(WebApplication app, ServerHosting hosting)
    {
        app.MapGet("/install.ps1", (HttpContext context, SetupPackage package) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            var info = package.Info();
            return !info.Available ? Results.NotFound(new { error = "安装包尚未发布，请稍后重试。" })
                : Results.Text(Script(hosting, info), "text/plain", Encoding.UTF8);
        }).RequireRateLimiting("setup-download");
        app.MapGet("/install/{sha256}/HowieMonitorSetup.exe", (string sha256, HttpContext context, SetupPackage package) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            var stream = package.OpenVerified(sha256);
            return stream is null ? Results.NotFound(new { error = "安装包已更新或尚未发布，请重新运行安装命令。" })
                : Results.File(stream, "application/octet-stream", fileDownloadName: "HowieMonitorSetup.exe", enableRangeProcessing: true);
        }).RequireRateLimiting("setup-download");
    }

    public static string Script(ServerHosting hosting, SetupDownload info)
    {
        if (!info.Available || info.Sha256 is not { Length: 64 } hash || !hash.All(Uri.IsHexDigit) || info.Bytes is not > 0)
            throw new InvalidOperationException("安装包信息不完整。");
        return $$"""
            # Howie Monitor: download, verify, then start the interactive installer.
            & {
                $ErrorActionPreference = 'Stop'
                $ProgressPreference = 'SilentlyContinue'
                $global:LASTEXITCODE = 1
                if ($env:OS -ne 'Windows_NT' -or -not [Environment]::Is64BitProcess -or $env:PROCESSOR_ARCHITECTURE -ne 'AMD64') {
                    throw 'Please run this command in 64-bit PowerShell on Windows 11 x64.'
                }
                $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
                try { $administrator = ([Security.Principal.WindowsPrincipal]::new($identity)).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator) }
                finally { $identity.Dispose() }
                if (-not $administrator) { throw 'Open PowerShell as administrator using your own account, then run the command again.' }
                $build = [int](Get-ItemProperty -LiteralPath 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion' -Name CurrentBuildNumber).CurrentBuildNumber
                if ($build -lt 22000) { throw 'Windows 11 or later is required.' }

                # Program Files inherits administrator-only write access; do not execute from a shared temporary directory.
                $work = Join-Path ([Environment]::GetFolderPath('ProgramFiles')) ('HowieMonitorSetup-' + [Guid]::NewGuid().ToString('N'))
                $created = $false
                try {
                    $null = New-Item -ItemType Directory -Path $work -ErrorAction Stop
                    $created = $true
                    $setup = Join-Path $work 'HowieMonitorSetup.exe'
                    Write-Host 'Downloading Howie Monitor...'
                    Invoke-WebRequest -UseBasicParsing -Uri '{{Origin(hosting)}}install/{{hash}}/HowieMonitorSetup.exe' -OutFile $setup -MaximumRedirection 0 -TimeoutSec 600 -ErrorAction Stop
                    if ((Get-Item -LiteralPath $setup).Length -ne {{info.Bytes}}) { throw 'The download is incomplete. Run the install command again.' }
                    if ((Get-FileHash -LiteralPath $setup -Algorithm SHA256).Hash -ne '{{hash}}') { throw 'SHA-256 verification failed. The installer was not started.' }
                    Write-Host 'Download verified. Follow the prompts to enter your pairing code and confirm installation.'
                    $process = Start-Process -FilePath $setup -ArgumentList '--cli' -NoNewWindow -Wait -PassThru -ErrorAction Stop
                    $global:LASTEXITCODE = $process.ExitCode
                    switch ($process.ExitCode) {
                        0 { Write-Host 'Howie Monitor installation and enrollment verified.' }
                        2 { Write-Host 'Installation was not confirmed.' }
                        130 { Write-Host 'Installation canceled.' }
                        default { throw ('Installation or enrollment was not completed (exit code ' + $process.ExitCode + '). Run the command again to retry.') }
                    }
                }
                finally {
                    if ($created) {
                        try { Remove-Item -LiteralPath $work -Recurse -Force -ErrorAction Stop }
                        catch { Write-Warning ('Could not remove the download directory: ' + $work) }
                    }
                }
            }
            """;
    }
}
