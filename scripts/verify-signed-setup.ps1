param(
    [Parameter(Mandatory)][string]$UnsignedSetup,
    [Parameter(Mandatory)][string]$SignedSetup,
    [Parameter(Mandatory)][string]$ReleaseManifest
)
$ErrorActionPreference = 'Stop'
$taskUnsigned = [IO.Path]::GetFullPath($UnsignedSetup)
$taskSigned = [IO.Path]::GetFullPath($SignedSetup)
if ($taskUnsigned -eq $taskSigned) { throw '原始包和签名包必须使用不同路径。' }
foreach ($taskFile in @($taskUnsigned, $taskSigned)) {
    $taskItem = Get-Item -LiteralPath $taskFile
    if ($taskItem.PSIsContainer -or $taskItem.LinkType -or $taskItem.Name -ne 'HowieMonitorSetup.exe') { throw '安装包路径无效。' }
}
$taskSignature = Get-AuthenticodeSignature -LiteralPath $taskSigned
if ($taskSignature.Status -ne 'Valid' -or -not $taskSignature.SignerCertificate -or -not $taskSignature.TimeStamperCertificate) {
    throw '签名或时间戳未通过 Windows 验证，不能发布为已签名版本。'
}
if ($taskSignature.SignerCertificate.Subject -notmatch '(?:^|,\s*)(?:CN|O)=SignPath Foundation(?:,|$)') {
    throw '签名发布者不是预期的 SignPath Foundation。'
}
$taskOriginalVersion = [Diagnostics.FileVersionInfo]::GetVersionInfo($taskUnsigned)
$taskSignedVersion = [Diagnostics.FileVersionInfo]::GetVersionInfo($taskSigned)
$taskRelease = Get-Content -LiteralPath $ReleaseManifest -Raw | ConvertFrom-Json
if ($taskRelease.version -notmatch '^\d+\.\d+\.\d+$' -or $taskRelease.buildId -notmatch '^[a-f0-9]{40}$' `
    -or $taskRelease.sha256 -ne (Get-FileHash -LiteralPath $taskUnsigned -Algorithm SHA256).Hash `
    -or $taskRelease.bytes -ne (Get-Item -LiteralPath $taskUnsigned).Length) { throw '原始发布清单与安装包不一致。' }
foreach ($taskProperty in @('ProductName', 'ProductVersion', 'FileVersion', 'CompanyName')) {
    if ($taskOriginalVersion.$taskProperty -ne $taskSignedVersion.$taskProperty) { throw "签名前后元数据发生变化：$taskProperty" }
}
if ($taskSignedVersion.ProductName -ne 'Howie Monitor' -or $taskSignedVersion.ProductVersion -ne $taskRelease.version -or
    $taskSignedVersion.FileVersion -ne ($taskRelease.version + '.0') -or $taskSignedVersion.CompanyName -ne 'Howie Monitor contributors') {
    throw '安装包产品元数据与签名策略不一致。'
}
function Read-PackageCheck([string]$Path) {
    $taskStart = [Diagnostics.ProcessStartInfo]::new($Path)
    $taskStart.UseShellExecute = $false; $taskStart.CreateNoWindow = $true
    $taskStart.RedirectStandardOutput = $true; $taskStart.RedirectStandardError = $true
    $taskStart.ArgumentList.Add('--check-package')
    $taskProcess = [Diagnostics.Process]::Start($taskStart)
    try {
        $taskOutput = $taskProcess.StandardOutput.ReadToEndAsync()
        $taskError = $taskProcess.StandardError.ReadToEndAsync()
        if (-not $taskProcess.WaitForExit(30000)) { throw '安装包只读检查超时。' }
        if ($taskProcess.ExitCode -ne 0 -or $taskError.GetAwaiter().GetResult()) { throw '安装包只读检查失败。' }
        return $taskOutput.GetAwaiter().GetResult() | ConvertFrom-Json
    } finally { $taskProcess.Dispose() }
}
$taskOriginalCheck = Read-PackageCheck $taskUnsigned
$taskSignedCheck = Read-PackageCheck $taskSigned
foreach ($taskProperty in @('product', 'runtime', 'version', 'buildId', 'updateProtocol', 'agentSha256', 'agentFiles', 'noticeFiles', 'server')) {
    if ($taskOriginalCheck.$taskProperty -ne $taskSignedCheck.$taskProperty) { throw "签名前后安装载荷发生变化：$taskProperty" }
}
$taskHash = (Get-FileHash -LiteralPath $taskSigned -Algorithm SHA256).Hash
if ($taskOriginalCheck.version -ne $taskRelease.version -or $taskOriginalCheck.buildId -ne $taskRelease.buildId `
    -or $taskOriginalCheck.agentSha256 -ne $taskRelease.payloadSha256 -or $taskRelease.protocol -ne 1) { throw '发布清单与内嵌载荷不一致。' }
[IO.File]::WriteAllText(($taskSigned + '.sha256'), ($taskHash + '  HowieMonitorSetup.exe'), [Text.UTF8Encoding]::new($false))
$taskRelease.sha256 = $taskHash; $taskRelease.bytes = (Get-Item -LiteralPath $taskSigned).Length
[IO.File]::WriteAllText(($taskSigned + '.release.json'), ($taskRelease | ConvertTo-Json), [Text.UTF8Encoding]::new($false))
[pscustomobject]@{ Status = 'Valid'; Publisher = $taskSignature.SignerCertificate.Subject; Sha256 = $taskHash; Package = $taskSignedCheck }
