<#
.SYNOPSIS
Requests a cooperative stop for one XiaoK long-run observation.
.DESCRIPTION
Signals only the named event recorded by an active observation's control file. It never terminates a process
or stops an attached XiaoK Host. Control and request files are restricted to the repository's ignored .tools/stability directory.
.EXAMPLE
.\tools\request_xiaok_long_run_stop.ps1 -ControlFilePath .tools\stability\xiaok-visible-idle-<run-id>.control.json

.EXAMPLE
.\tools\request_xiaok_long_run_stop.ps1 -ControlFilePath .tools\stability\xiaok-attached-session-<run-id>.control.json
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string] $ControlFilePath
)

$ErrorActionPreference = 'Stop'

$repositoryRoot = Split-Path -Parent $PSScriptRoot
$stabilityDirectory = [IO.Path]::GetFullPath((Join-Path $repositoryRoot '.tools\stability'))
$expectedParent = $stabilityDirectory.TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
if ([IO.Path]::IsPathRooted($ControlFilePath)) {
    $fullControlPath = [IO.Path]::GetFullPath($ControlFilePath)
}
else {
    $fullControlPath = [IO.Path]::GetFullPath((Join-Path $repositoryRoot $ControlFilePath))
}
if (-not $fullControlPath.StartsWith($expectedParent, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Control file must be inside this repository .tools/stability directory.'
}

$controlFile = Get-Item -LiteralPath $fullControlPath -ErrorAction Stop
if (($controlFile.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0 -or
    -not [string]::Equals($controlFile.DirectoryName, $stabilityDirectory, [StringComparison]::OrdinalIgnoreCase) -or
    $controlFile.Name -notmatch '^xiaok-(visible-idle|attached-session)-([0-9a-f]{32})\.control\.json$') {
    throw 'Control file path is not a valid long-run control file.'
}
$fileObservationMode = $Matches[1]
$fileRunId = $Matches[2]

$control = Get-Content -LiteralPath $fullControlPath -Raw | ConvertFrom-Json
$monitorProcessId = 0
$hostProcessId = 0
if (-not [int]::TryParse([string]$control.monitorProcessId, [ref]$monitorProcessId) -or
    -not [int]::TryParse([string]$control.hostProcessId, [ref]$hostProcessId)) {
    throw 'Control file contents failed the process identity checks.'
}
if ($control.schemaVersion -ne 1 -or $control.runId -cne $fileRunId -or
    $control.state -cne 'running' -or $control.stopEventName -cne "Local\XiaoKLongRun_$fileRunId" -or
    $monitorProcessId -le 0 -or $hostProcessId -le 0) {
    throw 'Control file contents failed the run identity checks.'
}

$hostOwnedByMonitor = $true
if ($control.PSObject.Properties.Name -contains 'hostOwnedByMonitor') {
    if ($control.hostOwnedByMonitor -isnot [bool]) {
        throw 'Control file ownership state is invalid.'
    }
    $hostOwnedByMonitor = $control.hostOwnedByMonitor
}
$controlObservationMode = 'visible-idle'
if ($control.PSObject.Properties.Name -contains 'observationMode') {
    if ($control.observationMode -isnot [string]) {
        throw 'Control file observation mode is invalid.'
    }
    $controlObservationMode = $control.observationMode
}
if ($fileObservationMode -ne $controlObservationMode -or
    $hostOwnedByMonitor -ne ($fileObservationMode -eq 'visible-idle')) {
    throw 'Control file observation mode is invalid.'
}

foreach ($identity in @(
    @{ ProcessId = $monitorProcessId; StartedUtc = $control.monitorStartedUtc; Name = 'monitor' },
    @{ ProcessId = $hostProcessId; StartedUtc = $control.hostStartedUtc; Name = 'Host' }
)) {
    $process = Get-Process -Id $identity.ProcessId -ErrorAction SilentlyContinue
    if ($null -eq $process) {
        if ($identity.Name -eq 'Host' -and -not $hostOwnedByMonitor) { continue }
        throw "The recorded $($identity.Name) process is no longer running."
    }
    $expectedStartedUtc = [DateTimeOffset]::MinValue
    $hasExpectedStart = if ($identity.StartedUtc -is [datetime]) {
        $expectedStartedUtc = [DateTimeOffset]([datetime]$identity.StartedUtc)
        $true
    }
    else {
        [DateTimeOffset]::TryParse([string]$identity.StartedUtc, [ref]$expectedStartedUtc)
    }
    $actualStartedUtc = [DateTimeOffset]$process.StartTime.ToUniversalTime()
    if (-not $hasExpectedStart -or $actualStartedUtc.UtcTicks -ne $expectedStartedUtc.ToUniversalTime().UtcTicks) {
        throw "The recorded $($identity.Name) process identity no longer matches."
    }
}

$requestPath = [regex]::Replace($fullControlPath, '\.control\.json$', '.stop.json', [Text.RegularExpressions.RegexOptions]::IgnoreCase)
$event = [Threading.EventWaitHandle]::OpenExisting([string]$control.stopEventName)
$request = [ordered]@{
    schemaVersion = 1
    runId = $fileRunId
    observationMode = $fileObservationMode
    hostOwnedByMonitor = $hostOwnedByMonitor
    monitorProcessId = $monitorProcessId
    hostProcessId = $hostProcessId
    requestedUtc = [datetime]::UtcNow.ToString('o')
}
$requestJson = $request | ConvertTo-Json -Compress
$stream = $null
$createdRequest = $false
try {
    $stream = [IO.File]::Open($requestPath, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
    $createdRequest = $true
    $writer = [IO.StreamWriter]::new($stream, [Text.UTF8Encoding]::new($false))
    $writer.WriteLine($requestJson)
    $writer.Flush()
    $stream.Flush($true)
    $writer.Dispose()
    $stream = $null
    [void]$event.Set()
}
catch {
    if ($null -ne $stream) { $stream.Dispose() }
    if ($createdRequest -and (Test-Path -LiteralPath $requestPath)) {
        Remove-Item -LiteralPath $requestPath -ErrorAction SilentlyContinue
    }
    throw
}
finally {
    $event.Dispose()
}

Write-Output "Cooperative stop requested for XiaoK long-run monitor PID $($control.monitorProcessId); no process was terminated."
