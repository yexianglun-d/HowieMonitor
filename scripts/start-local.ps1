param([int]$Port = 5080, [ValidatePattern('^[a-z0-9-]+$')][string]$Instance = 'local', [switch]$SkipBuild)
$ErrorActionPreference = 'Stop'
$taskRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$taskStateDirectory = Join-Path $taskRoot ('.local\instances\' + $Instance)
$taskServerData = Join-Path $taskStateDirectory 'server'
$taskAgentData = Join-Path $taskStateDirectory 'agent'
$taskProcessFile = Join-Path $taskStateDirectory 'processes.json'
if (Test-Path -LiteralPath $taskProcessFile) {
    $taskPrevious = Get-Content -LiteralPath $taskProcessFile -Raw | ConvertFrom-Json
    foreach ($taskProcessId in @($taskPrevious.serverPid, $taskPrevious.agentPid)) {
        if (Get-Process -Id $taskProcessId -ErrorAction SilentlyContinue) { throw '该实例仍在运行，请先运行 stop-local.ps1。' }
    }
}
if (Get-NetTCPConnection -State Listen -LocalPort $Port -ErrorAction SilentlyContinue) { throw "端口 $Port 已被使用。" }
[IO.Directory]::CreateDirectory($taskStateDirectory) | Out-Null
$taskDirectoryInfo = [IO.DirectoryInfo]::new($taskStateDirectory)
$taskLegacyAcl = $taskDirectoryInfo.PSObject.Methods.Name -contains 'GetAccessControl'
$taskAcl = if ($taskLegacyAcl) { $taskDirectoryInfo.GetAccessControl([Security.AccessControl.AccessControlSections]::Access) } else {
    [IO.FileSystemAclExtensions]::GetAccessControl($taskDirectoryInfo, [Security.AccessControl.AccessControlSections]::Access)
}
$taskAcl.SetAccessRuleProtection($true, $false)
$taskCurrentUser = [Security.Principal.WindowsIdentity]::GetCurrent().User
foreach ($taskPrincipal in @($taskCurrentUser, [Security.Principal.SecurityIdentifier]::new('S-1-5-18'))) {
    $taskAcl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new($taskPrincipal, 'FullControl', 'ContainerInherit,ObjectInherit', 'None', 'Allow'))
}
if ($taskLegacyAcl) { $taskDirectoryInfo.SetAccessControl($taskAcl) } else {
    [IO.FileSystemAclExtensions]::SetAccessControl($taskDirectoryInfo, $taskAcl)
}
Push-Location -LiteralPath $taskRoot
try {
    if (-not $SkipBuild) {
        & dotnet restore Howie.Monitor.slnx --locked-mode
        if ($LASTEXITCODE -ne 0) { throw '依赖还原失败。' }
        & dotnet build Howie.Monitor.slnx -c Release --no-restore
        if ($LASTEXITCODE -ne 0) { throw '后端构建失败。' }
        Push-Location -LiteralPath 'src\Howie.Monitor.Web'
        try {
            $taskNpmArguments = @('ci', '--registry=https://registry.npmjs.org')
            $taskRegistryUri = [Uri]'https://registry.npmjs.org'
            $taskProxy = [Net.WebRequest]::GetSystemWebProxy()
            $taskProxyUri = $taskProxy.GetProxy($taskRegistryUri)
            if (-not $taskProxy.IsBypassed($taskRegistryUri) -and $taskProxyUri.IsLoopback) {
                $taskNpmArguments += '--https-proxy=' + $taskProxyUri.GetLeftPart([UriPartial]::Authority)
            }
            & npm @taskNpmArguments
            if ($LASTEXITCODE -ne 0) { throw '前端依赖还原失败。' }
            & npm run build
            if ($LASTEXITCODE -ne 0) { throw '前端构建失败。' }
        } finally { Pop-Location }
    }
    $taskDotnet = (Get-Command dotnet).Source
    $taskUrl = "http://localhost:$Port"
    $taskServerDll = Join-Path $taskRoot 'src\Howie.Monitor.Server\bin\Release\net10.0\Howie.Monitor.Server.dll'
    $taskAgentDll = Join-Path $taskRoot 'src\Howie.Monitor.Agent\bin\Release\net10.0-windows\Howie.Monitor.Agent.dll'
    $taskServerProcess = Start-Process -FilePath $taskDotnet -WindowStyle Hidden -PassThru `
        -WorkingDirectory (Join-Path $taskRoot 'src\Howie.Monitor.Server') `
        -ArgumentList @(('"'+$taskServerDll+'"'), '--local-development', 'true', '--server-url', $taskUrl, '--data-dir', ('"'+$taskServerData+'"')) `
        -RedirectStandardOutput (Join-Path $taskStateDirectory 'server.log') -RedirectStandardError (Join-Path $taskStateDirectory 'server-error.log')
    $taskDeadline = [DateTime]::UtcNow.AddSeconds(30)
    $taskHealthy = $false
    while ([DateTime]::UtcNow -lt $taskDeadline) {
        if ($taskServerProcess.HasExited) { throw '后端启动失败，请查看实例目录中的 server-error.log。' }
        try { $taskHealth = Invoke-RestMethod -Uri ($taskUrl+'/health/live') -TimeoutSec 2; if ($taskHealth.status -eq 'ok') { $taskHealthy=$true; break } } catch { Start-Sleep -Milliseconds 300 }
    }
    if (-not $taskHealthy) { throw '后端启动超时。' }
    $taskCredential = Get-Content -LiteralPath (Join-Path $taskServerData 'device-credential.json') -Raw | ConvertFrom-Json
    $taskConfigFile = Join-Path $taskStateDirectory 'agent.json'
    $taskAgentConfiguration = @{serverUrl=$taskUrl;deviceId=$taskCredential.deviceId;token=$taskCredential.token;dataDirectory=$taskAgentData;localDevelopment=$true} | ConvertTo-Json
    [IO.File]::WriteAllText($taskConfigFile,$taskAgentConfiguration,[Text.UTF8Encoding]::new($false))
    $taskAgentProcess = Start-Process -FilePath $taskDotnet -WindowStyle Hidden -PassThru -WorkingDirectory $taskRoot `
        -ArgumentList @(('"'+$taskAgentDll+'"'), '--config', ('"'+$taskConfigFile+'"')) `
        -RedirectStandardOutput (Join-Path $taskStateDirectory 'agent.log') -RedirectStandardError (Join-Path $taskStateDirectory 'agent-error.log')
    $taskProcessState = @{serverPid=$taskServerProcess.Id;serverStarted=$taskServerProcess.StartTime.ToUniversalTime().ToString('O');agentPid=$taskAgentProcess.Id;agentStarted=$taskAgentProcess.StartTime.ToUniversalTime().ToString('O');url=$taskUrl;instance=$Instance}
    [IO.File]::WriteAllText($taskProcessFile,($taskProcessState|ConvertTo-Json),[Text.UTF8Encoding]::new($false))
    [pscustomobject]@{Url=$taskUrl;Instance=$Instance;ServerPid=$taskServerProcess.Id;AgentPid=$taskAgentProcess.Id}
} catch {
    if ($taskAgentProcess -and -not $taskAgentProcess.HasExited) { Stop-Process -Id $taskAgentProcess.Id }
    if ($taskServerProcess -and -not $taskServerProcess.HasExited) { Stop-Process -Id $taskServerProcess.Id }
    throw
} finally { Pop-Location }
