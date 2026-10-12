[CmdletBinding()]
param(
    [ValidateRange(1, 120)]
    [int]$TimeoutSeconds = 30
)

$ErrorActionPreference = 'Stop'
$packageName = 'MingKaiLin.XiaoK'
$package = Get-AppxPackage -Name $packageName | Select-Object -First 1
if ($null -eq $package) {
    throw '当前账户未安装小K MSIX；未启动程序。'
}

$runningHosts = @(Get-Process -Name 'XiaoK.Host' -ErrorAction SilentlyContinue)
if ($runningHosts.Count -gt 0) {
    throw '小K Host 正在运行；为避免影响现有会话，本次未启动来源诊断。'
}

$executable = Join-Path $package.InstallLocation 'XiaoK.Host.exe'
if (-not (Test-Path -LiteralPath $executable -PathType Leaf)) {
    throw '安装包内未找到小K Host；未启动诊断。'
}

$startInfo = [System.Diagnostics.ProcessStartInfo]::new()
$startInfo.FileName = $executable
$startInfo.Arguments = '--notification-source-diagnostic'
$startInfo.UseShellExecute = $false
$startInfo.RedirectStandardOutput = $true
$startInfo.RedirectStandardError = $true
$startInfo.CreateNoWindow = $true

$process = [System.Diagnostics.Process]::Start($startInfo)
try {
    $stdoutTask = $process.StandardOutput.ReadToEndAsync()
    $stderrTask = $process.StandardError.ReadToEndAsync()
    if (-not $process.WaitForExit($TimeoutSeconds * 1000)) {
        $diagnosticPid = $process.Id
        $process.Kill()
        $process.WaitForExit()
        throw "来源诊断超过 $TimeoutSeconds 秒；已结束本脚本启动的诊断进程 PID $diagnosticPid。"
    }

    $stdout = $stdoutTask.GetAwaiter().GetResult()
    $null = $stderrTask.GetAwaiter().GetResult()
    try {
        $report = $stdout | ConvertFrom-Json -ErrorAction Stop
    }
    catch {
        throw '诊断没有返回有效结果；未保存任何通知内容。'
    }

    $candidates = @($report.Candidates | ForEach-Object {
        $latest = $_.LatestNotificationTimeUtc
        if ($latest -is [System.DateTimeOffset]) {
            $latestUtc = $latest.ToUniversalTime()
        }
        elseif ($latest -is [System.DateTime]) {
            $latestUtc = [System.DateTimeOffset]::new($latest.ToUniversalTime())
        }
        elseif ($null -ne $latest) {
            $latestUtc = [System.DateTimeOffset]::Parse(
                [string]$latest,
                [System.Globalization.CultureInfo]::InvariantCulture,
                [System.Globalization.DateTimeStyles]::AssumeUniversal).ToUniversalTime()
        }
        else {
            $latestUtc = $null
        }

        [pscustomobject]@{
            DisplayName = $_.DisplayName
            AppUserModelId = $_.AppUserModelId
            NotificationCount = $_.NotificationCount
            LatestNotificationTimeUtc = if ($null -eq $latestUtc) { $null } else {
                $latestUtc.ToString('O', [System.Globalization.CultureInfo]::InvariantCulture)
            }
        }
    })

    $startApps = @(Get-StartApps)
    $identityHints = @(
        [pscustomobject]@{ Platform = 'QQ'; DisplayNames = @('QQ'); ProcessNames = @('QQ') }
        [pscustomobject]@{ Platform = 'WeChat'; DisplayNames = @('微信', 'WeChat', 'Weixin'); ProcessNames = @('Weixin') }
    ) | ForEach-Object {
        $platform = $_
        $registered = @($startApps | Where-Object { $_.Name -in $platform.DisplayNames } | ForEach-Object {
            [pscustomobject]@{ DisplayName = $_.Name; StartMenuAppId = $_.AppID }
        })
        $processes = @(Get-Process -Name $platform.ProcessNames -ErrorAction SilentlyContinue)
        [pscustomobject]@{
            Platform = $platform.Platform
            StartMenuRegistrations = $registered
            RunningProcessCount = $processes.Count
            RunningExecutablePaths = @($processes | ForEach-Object {
                try { $_.Path } catch { $null }
            } | Where-Object { -not [string]::IsNullOrWhiteSpace($_) } | Sort-Object -Unique)
        }
    }

    [pscustomobject]@{
        Status = $report.Status
        AccessStatus = $report.AccessStatus
        InspectedNotificationCount = $report.InspectedNotificationCount
        Candidates = $candidates
        ClientIdentityHints = @($identityHints)
        IdentityHintNote = 'StartMenuAppId and process paths are local metadata hints only; they do not prove the Toast AppUserModelId.'
        Note = $report.Note
        ExitCode = $process.ExitCode
    }
}
finally {
    $process.Dispose()
}
