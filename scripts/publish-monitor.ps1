param([string]$OutputRoot = (Join-Path $PSScriptRoot '..\artifacts'), [switch]$ServerOnly, [switch]$PublicRelease)
$ErrorActionPreference = 'Stop'
$taskRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$taskOutput = [IO.Path]::GetFullPath($OutputRoot)
$taskBuild = Join-Path $taskRoot '.local\deployment-build'
$taskVersion = '0.1.0'
$taskTimestamp = [DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss')

function Write-PackageNotices([string]$ProjectName, [string]$Destination, [string]$Runtime) {
    $taskAssets = Get-Content -LiteralPath (Join-Path $taskBuild ("obj\$ProjectName\project.assets.json")) -Raw | ConvertFrom-Json
    $taskCache = @($taskAssets.packageFolders.PSObject.Properties.Name)[0]
    $taskLicenses = Join-Path $Destination 'licenses'
    [IO.Directory]::CreateDirectory($taskLicenses) | Out-Null
    Copy-Item -LiteralPath (Join-Path $taskRoot 'LICENSE') -Destination (Join-Path $Destination 'LICENSE')
    $taskLines = [Collections.Generic.List[string]]::new()
    $taskLines.Add('# Third-party notices'); $taskLines.Add('')
    $taskLines.Add('Unmodified NuGet packages and .NET 10.0.12 runtime assets. Package metadata and available license texts are included in licenses/. Package repository links provide third-party source code. NuGet metadata may include runtime assets for other platforms; only the selected runtime is distributed.')
    $taskLines.Add(''); $taskLines.Add('| Package | Version | License | Project / source |'); $taskLines.Add('| --- | --- | --- | --- |')
    foreach ($taskLibrary in $taskAssets.libraries.PSObject.Properties) {
        if ($taskLibrary.Value.type -ne 'package') { continue }
        $taskParts = $taskLibrary.Name.Split('/'); $taskId = $taskParts[0]; $taskPackageVersion = $taskParts[1]
        $taskDirectory = Join-Path $taskCache $taskLibrary.Value.path
        $taskNuspec = Get-ChildItem -LiteralPath $taskDirectory -Filter '*.nuspec' -File | Select-Object -First 1
        if (-not $taskNuspec) { throw "缺少包元数据：$taskId" }
        [xml]$taskMetadata = Get-Content -LiteralPath $taskNuspec.FullName -Raw
        $taskLicense = $taskMetadata.package.metadata.license
        $taskLicenseName = if ($taskLicense) { $taskLicense.InnerText } else { [string]$taskMetadata.package.metadata.licenseUrl }
        $taskLines.Add("| $taskId | $taskPackageVersion | $taskLicenseName | $($taskMetadata.package.metadata.projectUrl) |")
        Copy-Item -LiteralPath $taskNuspec.FullName -Destination (Join-Path $taskLicenses ($taskId + '-' + $taskPackageVersion + '.nuspec'))
        if ($taskLicense -and $taskLicense.type -eq 'file') {
            $taskLicenseFile = Join-Path $taskDirectory $taskLicense.InnerText
            if (-not (Test-Path -LiteralPath $taskLicenseFile)) { throw "缺少许可文件：$taskId" }
            Copy-Item -LiteralPath $taskLicenseFile -Destination (Join-Path $taskLicenses ($taskId + '-' + $taskPackageVersion + '-LICENSE.txt'))
        }
        foreach ($taskFile in Get-ChildItem -LiteralPath $taskDirectory -File | Where-Object { $_.Name -match '^(LICENSE|THIRD-PARTY-NOTICES|NOTICE)' }) {
            Copy-Item -LiteralPath $taskFile.FullName -Destination (Join-Path $taskLicenses ($taskId + '-' + $taskPackageVersion + '-' + $taskFile.Name))
        }
    }
    foreach ($taskFramework in @('microsoft.netcore.app', 'microsoft.aspnetcore.app', 'microsoft.windowsdesktop.app')) {
        $taskRuntimeDirectory = Join-Path $taskCache ($taskFramework + '.runtime.' + $Runtime + '\10.0.12')
        if (-not (Test-Path -LiteralPath $taskRuntimeDirectory)) { continue }
        foreach ($taskFile in Get-ChildItem -LiteralPath $taskRuntimeDirectory -File | Where-Object { $_.Name -match '^(LICENSE|THIRD-PARTY-NOTICES)' }) {
            Copy-Item -LiteralPath $taskFile.FullName -Destination (Join-Path $taskLicenses ($taskFramework + '-' + $taskFile.Name))
        }
    }
    if ($ProjectName -eq 'Howie.Monitor.Agent') { Copy-Item -LiteralPath (Join-Path $taskRoot 'licenses\MPL-2.0.txt') -Destination $taskLicenses }
    if ($ProjectName -eq 'Howie.Monitor.Server') {
        $taskWebPackages = @('vue', '@vue/shared', '@vue/reactivity', '@vue/runtime-core', '@vue/runtime-dom')
        $taskLines.Add(''); $taskLines.Add('The bundled browser application includes Vue MIT-licensed runtime packages:')
        foreach ($taskWebPackage in $taskWebPackages) {
            $taskWebDirectory = Join-Path $taskRoot ('src\Howie.Monitor.Web\node_modules\' + $taskWebPackage)
            $taskWebMetadata = Get-Content -LiteralPath (Join-Path $taskWebDirectory 'package.json') -Raw | ConvertFrom-Json
            $taskLines.Add("- $taskWebPackage $($taskWebMetadata.version): $($taskWebMetadata.license), https://github.com/vuejs/core")
            Copy-Item -LiteralPath (Join-Path $taskWebDirectory 'LICENSE') -Destination (Join-Path $taskLicenses ($taskWebPackage.Replace('/', '-').Replace('@', '') + '-LICENSE.txt'))
        }
    }
    [IO.File]::WriteAllLines((Join-Path $Destination 'THIRD-PARTY-NOTICES.md'), $taskLines, [Text.UTF8Encoding]::new($false))
}

Push-Location -LiteralPath $taskRoot
try {
    Push-Location -LiteralPath (Join-Path $taskRoot 'src\Howie.Monitor.Web')
    try {
        $taskRegistry = 'https://registry.npmjs.org'
        $taskProxy = [Net.WebRequest]::GetSystemWebProxy().GetProxy([Uri]$taskRegistry)
        $taskNpmArguments = @('ci', '--registry', $taskRegistry)
        if ($taskProxy -and $taskProxy.IsLoopback -and $taskProxy.AbsoluteUri -ne $taskRegistry) { $taskNpmArguments += @('--https-proxy', $taskProxy.AbsoluteUri) }
        & npm.cmd @taskNpmArguments
        if ($LASTEXITCODE -ne 0) { throw "npm ci 失败：$LASTEXITCODE" }
        & npm.cmd test
        if ($LASTEXITCODE -ne 0) { throw "网页状态测试失败：$LASTEXITCODE" }
        & npm.cmd run build
        if ($LASTEXITCODE -ne 0) { throw "网页构建失败：$LASTEXITCODE" }
    } finally { Pop-Location }
    $taskTargets = if ($ServerOnly) { @(@{ Name = 'Howie.Monitor.Server'; Runtime = 'linux-x64' }) } else { @(@{ Name = 'Howie.Monitor.Agent'; Runtime = 'win-x64' }, @{ Name = 'Howie.Monitor.Server'; Runtime = 'linux-x64' }) }
    foreach ($taskTarget in $taskTargets) {
        $taskPackageName = $taskTarget.Name + '-' + $taskVersion + '-' + $taskTarget.Runtime + '-' + $taskTimestamp
        $taskDirectory = Join-Path $taskOutput $taskPackageName
        $taskExtension = if ($taskTarget.Runtime -eq 'linux-x64') { '.tar.gz' } else { '.zip' }
        $taskArchive = Join-Path $taskOutput ($taskPackageName + $taskExtension)
        if ((Test-Path -LiteralPath $taskDirectory) -or (Test-Path -LiteralPath $taskArchive)) { throw '发布路径已存在，请使用新的输出位置。' }
        $taskProject = Join-Path $taskRoot ("src\$($taskTarget.Name)\$($taskTarget.Name).csproj")
        & dotnet publish $taskProject -c Release -r $taskTarget.Runtime --self-contained true --artifacts-path $taskBuild `
            -p:PublishSingleFile=false -p:PublishTrimmed=false -p:RestoreLockedMode=true -o $taskDirectory
        if ($LASTEXITCODE -ne 0) { throw "发布失败：$LASTEXITCODE" }
        Write-PackageNotices $taskTarget.Name $taskDirectory $taskTarget.Runtime
        if ($PublicRelease) {
            foreach ($taskDocument in @('PRIVACY.md', 'CODE_SIGNING.md', 'BUILDING.md')) {
                Copy-Item -LiteralPath (Join-Path $taskRoot $taskDocument) -Destination $taskDirectory
            }
        } else {
            Copy-Item -LiteralPath (Join-Path $taskRoot 'docs\08-公网部署准备.md') -Destination (Join-Path $taskDirectory 'DEPLOYMENT.md')
            foreach ($taskDocument in @('10-七天历史实现与验收.md', '11-通知实现与验收.md', '09-采集端安装与接入流程.md', '12-安装接入技术设计.md', '14-历史电脑与备份恢复.md')) {
                Copy-Item -LiteralPath (Join-Path $taskRoot ('docs\' + $taskDocument)) -Destination $taskDirectory
            }
        }
        if ($taskTarget.Runtime -eq 'linux-x64') {
            $taskDeployment = Join-Path $taskDirectory 'deploy'
            [IO.Directory]::CreateDirectory($taskDeployment) | Out-Null
            foreach ($taskDeploymentFile in @('nginx.conf.template','howie-monitor.service.template','agent.example.json','renew-monitor-cert.py','backup-monitor.py','backup-monitor.cron.template')) {
                Copy-Item -LiteralPath (Join-Path $taskRoot ('deploy\' + $taskDeploymentFile)) -Destination $taskDeployment
            }
            & tar.exe -czf $taskArchive -C $taskOutput $taskPackageName
            if ($LASTEXITCODE -ne 0) { throw "归档失败：$LASTEXITCODE" }
        } else {
            Copy-Item -LiteralPath (Join-Path $taskRoot 'deploy\agent.example.json') -Destination $taskDirectory
            Compress-Archive -LiteralPath $taskDirectory -DestinationPath $taskArchive
        }
        $taskHash = (Get-FileHash -LiteralPath $taskArchive -Algorithm SHA256).Hash
        [IO.File]::WriteAllText(($taskArchive + '.sha256'), ($taskHash + '  ' + [IO.Path]::GetFileName($taskArchive)), [Text.UTF8Encoding]::new($false))
        if ($taskTarget.Name -eq 'Howie.Monitor.Agent') { $taskAgentArchive = $taskArchive; $taskAgentHash = $taskHash }
        [pscustomobject]@{ Platform = $taskTarget.Runtime; Directory = $taskDirectory; Archive = $taskArchive; Sha256 = $taskHash }
    }
    if ($ServerOnly) { return }
    $taskSetupProject = Join-Path $taskRoot 'src\Howie.Monitor.Setup\Howie.Monitor.Setup.csproj'
    $taskSetupDirectory = Join-Path $taskOutput ('Howie.Monitor.Setup-' + $taskVersion + '-win-x64-' + $taskTimestamp)
    $taskNoticeDirectory = Join-Path $taskBuild ('setup-notices-' + $taskTimestamp)
    $taskNoticeArchive = $taskNoticeDirectory + '.zip'
    if (Test-Path -LiteralPath $taskSetupDirectory) { throw '安装包发布目录已存在。' }
    & dotnet restore $taskSetupProject --artifacts-path $taskBuild -p:RuntimeIdentifier=win-x64 -p:SelfContained=true -p:RestoreLockedMode=true
    if ($LASTEXITCODE -ne 0) { throw "安装向导依赖恢复失败：$LASTEXITCODE" }
    Write-PackageNotices 'Howie.Monitor.Setup' $taskNoticeDirectory 'win-x64'
    Compress-Archive -Path (Join-Path $taskNoticeDirectory '*') -DestinationPath $taskNoticeArchive
    & dotnet publish $taskSetupProject -c Release -r win-x64 --self-contained true --artifacts-path $taskBuild `
        -p:PublishTrimmed=false -p:RestoreLockedMode=true `
        "-p:SetupPayload=$taskAgentArchive" "-p:SetupPayloadHash=$taskAgentHash" "-p:SetupNotices=$taskNoticeArchive" -o $taskSetupDirectory
    if ($LASTEXITCODE -ne 0) { throw "安装向导发布失败：$LASTEXITCODE" }
    $taskSetupExe = Join-Path $taskSetupDirectory 'HowieMonitorSetup.exe'
    $taskCheckStart = [Diagnostics.ProcessStartInfo]::new($taskSetupExe)
    $taskCheckStart.UseShellExecute = $false; $taskCheckStart.CreateNoWindow = $true
    $taskCheckStart.RedirectStandardOutput = $true; $taskCheckStart.RedirectStandardError = $true
    $taskCheckStart.ArgumentList.Add('--check-package')
    $taskCheckProcess = [Diagnostics.Process]::Start($taskCheckStart)
    $taskCheckOutput = $taskCheckProcess.StandardOutput.ReadToEndAsync()
    $taskCheckError = $taskCheckProcess.StandardError.ReadToEndAsync()
    $taskCheckProcess.WaitForExit()
    if ($taskCheckProcess.ExitCode -ne 0) { throw '自带运行时安装包检查失败。' }
    $taskCheckResult = $taskCheckOutput.GetAwaiter().GetResult() | ConvertFrom-Json
    $taskCheckError.GetAwaiter().GetResult() | Out-Null
    $taskCheckProcess.Dispose()
    if ($taskCheckResult.runtime -ne '10.0.12' -or $taskCheckResult.agentSha256 -ne $taskAgentHash) { throw '安装包运行时或采集载荷不一致。' }
    $taskSetupHash = (Get-FileHash -LiteralPath $taskSetupExe -Algorithm SHA256).Hash
    [IO.File]::WriteAllText(($taskSetupExe + '.sha256'), ($taskSetupHash + '  HowieMonitorSetup.exe'), [Text.UTF8Encoding]::new($false))
    [pscustomobject]@{ Platform = 'Windows Setup'; Directory = $taskSetupDirectory; Archive = $taskSetupExe; Sha256 = $taskSetupHash; Verification = $taskCheckResult }
} finally { Pop-Location }
