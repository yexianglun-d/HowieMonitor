param([ValidatePattern('^[a-z0-9-]+$')][string]$Instance = 'local')
$ErrorActionPreference = 'Stop'
$taskRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$taskFile = Join-Path $taskRoot ('.local\instances\'+$Instance+'\processes.json')
if (-not(Test-Path -LiteralPath $taskFile)) { Write-Output '实例没有运行记录。'; return }
$taskState = Get-Content -LiteralPath $taskFile -Raw | ConvertFrom-Json
foreach($taskKind in @('agent','server')) {
    $taskProcessId = $taskState.($taskKind+'Pid')
    $taskStartValue = $taskState.($taskKind+'Started')
    $taskExpectedStart = if ($taskStartValue -is [DateTime]) { $taskStartValue.ToUniversalTime() } else {
        [DateTime]::Parse($taskStartValue, [Globalization.CultureInfo]::InvariantCulture, [Globalization.DateTimeStyles]::RoundtripKind).ToUniversalTime()
    }
    $taskProcess = Get-Process -Id $taskProcessId -ErrorAction SilentlyContinue
    if ($taskProcess) {
        if ([Math]::Abs(($taskProcess.StartTime.ToUniversalTime()-$taskExpectedStart).TotalSeconds) -gt 1) { throw '进程号已被复用，拒绝停止其他进程。' }
        Stop-Process -Id $taskProcessId
    }
}
Write-Output '本地实例已停止，数据库和未确认队列已保留。'
