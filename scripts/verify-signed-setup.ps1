param(
    [Parameter(Mandatory)][string]$UnsignedSetup,
    [Parameter(Mandatory)][string]$SignedSetup
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
foreach ($taskProperty in @('ProductName', 'ProductVersion', 'FileVersion', 'CompanyName')) {
    if ($taskOriginalVersion.$taskProperty -ne $taskSignedVersion.$taskProperty) { throw "签名前后元数据发生变化：$taskProperty" }
}
if ($taskSignedVersion.ProductName -ne 'Howie Monitor' -or $taskSignedVersion.ProductVersion -ne '0.1.0' -or
    $taskSignedVersion.FileVersion -ne '0.1.0.0' -or $taskSignedVersion.CompanyName -ne 'Howie Monitor contributors') {
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
foreach ($taskProperty in @('product', 'runtime', 'agentSha256', 'agentFiles', 'noticeFiles', 'server')) {
    if ($taskOriginalCheck.$taskProperty -ne $taskSignedCheck.$taskProperty) { throw "签名前后安装载荷发生变化：$taskProperty" }
}
$taskHash = (Get-FileHash -LiteralPath $taskSigned -Algorithm SHA256).Hash
[IO.File]::WriteAllText(($taskSigned + '.sha256'), ($taskHash + '  HowieMonitorSetup.exe'), [Text.UTF8Encoding]::new($false))
[pscustomobject]@{ Status = 'Valid'; Publisher = $taskSignature.SignerCertificate.Subject; Sha256 = $taskHash; Package = $taskSignedCheck }
