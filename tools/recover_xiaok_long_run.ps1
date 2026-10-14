<#
.SYNOPSIS
Marks a stale XiaoK long-run checkpoint as interrupted without contacting the Host.
.DESCRIPTION
This tool only updates a matching control file and its checkpoint report inside .tools/stability.
It refuses to act while the recorded monitor process identity is still alive, and never starts,
stops, signals, or attaches to a XiaoK Host.
.EXAMPLE
.\tools\recover_xiaok_long_run.ps1 -ControlFilePath ".tools\stability\xiaok-attached-session-<run-id>.control.json"
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string] $ControlFilePath
)

$ErrorActionPreference = 'Stop'

$repositoryRoot = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
$toolsDirectory = [IO.Path]::GetFullPath((Join-Path $repositoryRoot '.tools'))
$stabilityDirectory = [IO.Path]::GetFullPath((Join-Path $toolsDirectory 'stability'))

foreach ($directoryPath in @($toolsDirectory, $stabilityDirectory)) {
    $directory = Get-Item -LiteralPath $directoryPath -ErrorAction Stop
    if (-not $directory.PSIsContainer -or
        ($directory.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw 'The .tools/stability directory chain must contain ordinary directories.'
    }
}

if ([IO.Path]::IsPathRooted($ControlFilePath)) {
    $fullControlPath = [IO.Path]::GetFullPath($ControlFilePath)
}
else {
    $fullControlPath = [IO.Path]::GetFullPath((Join-Path $repositoryRoot $ControlFilePath))
}

$controlFile = Get-Item -LiteralPath $fullControlPath -ErrorAction Stop
if (($controlFile.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0 -or
    -not [string]::Equals($controlFile.DirectoryName, $stabilityDirectory, [StringComparison]::OrdinalIgnoreCase) -or
    $controlFile.Name -notmatch '^xiaok-(visible-idle|attached-session)-([0-9a-f]{32})\.control\.json$') {
    throw 'Control file path is not a valid direct child of .tools/stability.'
}

$fileObservationMode = $Matches[1]
$fileRunId = $Matches[2]
$controlText = Get-Content -LiteralPath $fullControlPath -Raw -ErrorAction Stop
$control = $controlText | ConvertFrom-Json -ErrorAction Stop
$monitorProcessId = 0
$hostProcessId = 0
if (-not [int]::TryParse([string]$control.monitorProcessId, [ref]$monitorProcessId) -or
    -not [int]::TryParse([string]$control.hostProcessId, [ref]$hostProcessId)) {
    throw 'Control file contents failed the process identity checks.'
}

$hostOwnedByMonitor = $null
if ($control.hostOwnedByMonitor -isnot [bool]) {
    throw 'Control file ownership state is invalid.'
}
$hostOwnedByMonitor = $control.hostOwnedByMonitor

if ($control.schemaVersion -ne 1 -or $control.runId -cne $fileRunId -or
    $control.state -cnotin @('running', 'finishing', 'interrupted') -or
    $control.stopEventName -cne "Local\XiaoKLongRun_$fileRunId" -or
    $control.observationMode -cne $fileObservationMode -or
    $hostOwnedByMonitor -ne ($fileObservationMode -eq 'visible-idle') -or
    $monitorProcessId -le 0 -or $hostProcessId -le 0) {
    throw 'Control file contents failed the run identity checks.'
}

function ConvertTo-ValidatedUtcTicks {
    param(
        [Parameter(Mandatory = $true)]
        [object] $Value,
        [Parameter(Mandatory = $true)]
        [string] $Name
    )

    $parsed = [DateTimeOffset]::MinValue
    if ($Value -is [datetime]) {
        $parsed = [DateTimeOffset]([datetime]$Value)
    }
    elseif (-not [DateTimeOffset]::TryParse([string]$Value, [ref]$parsed)) {
        throw "The recorded $Name timestamp is invalid."
    }
    return $parsed.ToUniversalTime().UtcTicks
}

$monitorStartedTicks = ConvertTo-ValidatedUtcTicks -Value $control.monitorStartedUtc -Name 'monitor start'
$hostStartedTicks = ConvertTo-ValidatedUtcTicks -Value $control.hostStartedUtc -Name 'Host start'

function Assert-RecordedMonitorExited {
    param(
        [Parameter(Mandatory = $true)]
        [int] $ProcessId,
        [Parameter(Mandatory = $true)]
        [long] $StartedUtcTicks
    )

    $process = $null
    try {
        $process = Get-Process -Id $ProcessId -ErrorAction Stop
    }
    catch {
        if ($_.CategoryInfo.Category -eq [System.Management.Automation.ErrorCategory]::ObjectNotFound -or
            $_.FullyQualifiedErrorId -like 'NoProcessFoundForGivenId,*') {
            return
        }
        throw "Cannot establish whether recorded monitor PID $ProcessId is still alive; recovery refused."
    }

    try {
        $actualStartedTicks = ([DateTimeOffset]$process.StartTime.ToUniversalTime()).UtcTicks
    }
    catch {
        throw "Cannot verify the start time of monitor PID $ProcessId; recovery refused."
    }

    if ($actualStartedTicks -eq $StartedUtcTicks) {
        throw "The recorded monitor PID $ProcessId is still alive; recovery refused."
    }
}

# Prove the monitor is stale before requiring report metadata. This also protects the active
# pre-checkpoint observation, whose control file intentionally has no reportPath.
Assert-RecordedMonitorExited -ProcessId $monitorProcessId -StartedUtcTicks $monitorStartedTicks

if ($control.PSObject.Properties.Name -notcontains 'reportPath' -or
    [string]::IsNullOrWhiteSpace([string]$control.reportPath)) {
    throw 'This run has no recorded checkpoint path; partial samples cannot be reconstructed.'
}

$reportPathText = [string]$control.reportPath
if ([IO.Path]::IsPathRooted($reportPathText)) {
    $fullReportPath = [IO.Path]::GetFullPath($reportPathText)
}
else {
    $fullReportPath = [IO.Path]::GetFullPath((Join-Path $repositoryRoot $reportPathText))
}

$reportFile = Get-Item -LiteralPath $fullReportPath -ErrorAction Stop
if (($reportFile.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0 -or
    -not [string]::Equals($reportFile.DirectoryName, $stabilityDirectory, [StringComparison]::OrdinalIgnoreCase) -or
    $reportFile.Name -notmatch '^xiaok-(visible-idle|attached-session)-([0-9]+\.[0-9]+\.[0-9]+\.[0-9]+)-([0-9a-f]{32})\.json$') {
    throw 'Report path is not a valid direct child of .tools/stability.'
}

$reportFileMode = $Matches[1]
$reportVersion = $Matches[2]
$reportRunId = $Matches[3]
$expectedReportName = "xiaok-$fileObservationMode-$reportVersion-$fileRunId.json"
if ($reportFileMode -cne $fileObservationMode -or $reportRunId -cne $fileRunId -or
    $reportFile.Name -cne $expectedReportName -or
    -not [string]::Equals($fullReportPath, [IO.Path]::GetFullPath((Join-Path $stabilityDirectory $expectedReportName)), [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Report path does not match the control file run identity.'
}

$reportText = Get-Content -LiteralPath $fullReportPath -Raw -ErrorAction Stop
$report = $reportText | ConvertFrom-Json -ErrorAction Stop
$reportRunId = [string]$report.runId
$reportMonitorStartedTicks = ConvertTo-ValidatedUtcTicks -Value $report.monitorStartedUtc -Name 'report monitor start'
$reportHostStartedTicks = ConvertTo-ValidatedUtcTicks -Value $report.hostStartedUtc -Name 'report Host start'

if ($reportRunId -cne $fileRunId -or $report.packageVersion -cne $reportVersion -or
    $report.observationMode -cne $fileObservationMode -or
    $report.hostLaunchedByMonitor -isnot [bool] -or
    $report.hostLaunchedByMonitor -ne $hostOwnedByMonitor -or
    $report.monitorProcessId -ne $monitorProcessId -or
    $reportMonitorStartedTicks -ne $monitorStartedTicks -or
    $report.processId -ne $hostProcessId -or $reportHostStartedTicks -ne $hostStartedTicks) {
    throw 'Checkpoint report identity does not match the control file.'
}

$alreadyRecovered = $report.reportState -ceq 'interrupted' -and
    $report.outcome -ceq 'monitor_interrupted' -and
    $report.observationCompleted -is [bool] -and -not $report.observationCompleted
if (-not $alreadyRecovered -and
    ($report.reportState -cne 'checkpoint' -or $report.outcome -cne 'monitoring' -or
        $report.observationCompleted -isnot [bool] -or $report.observationCompleted -or
        $null -ne $report.endedUtc)) {
    throw 'Report is not an incomplete checkpoint; recovery refused.'
}
if ($control.state -ceq 'interrupted' -and -not $alreadyRecovered) {
    throw 'Control file is interrupted but its report is not; manual inspection is required.'
}

# Recheck identity and bytes immediately before the first write so a monitor that finished
# between the initial read and validation cannot have its final report overwritten.
Assert-RecordedMonitorExited -ProcessId $monitorProcessId -StartedUtcTicks $monitorStartedTicks
$controlTextBeforeWrite = Get-Content -LiteralPath $fullControlPath -Raw -ErrorAction Stop
$reportTextBeforeWrite = Get-Content -LiteralPath $fullReportPath -Raw -ErrorAction Stop
if ($controlTextBeforeWrite -cne $controlText -or $reportTextBeforeWrite -cne $reportText) {
    throw 'Control or report changed during recovery validation; no file was modified.'
}

Import-Module (Join-Path $PSScriptRoot 'XiaoKLongRunReport.psm1') -Force
$recoveredUtc = [datetime]::UtcNow.ToString('o')
if (-not $alreadyRecovered) {
    $report.reportState = 'interrupted'
    $report.outcome = 'monitor_interrupted'
    $report.observationCompleted = $false
    $report.endedUtc = $null
    $report | Add-Member -NotePropertyName monitorInterrupted -NotePropertyValue $true -Force
    $report | Add-Member -NotePropertyName recoveredUtc -NotePropertyValue $recoveredUtc -Force
    Write-XiaoKLongRunJsonAtomic -Path $fullReportPath -InputObject $report
}

if ($control.state -cne 'interrupted') {
    $control.state = 'interrupted'
    $control | Add-Member -NotePropertyName terminalOutcome -NotePropertyValue 'monitor_interrupted' -Force
    $control | Add-Member -NotePropertyName recoveredUtc -NotePropertyValue $recoveredUtc -Force
    Write-XiaoKLongRunJsonAtomic -Path $fullControlPath -InputObject $control
}

Write-Output "Recovered stale long-run checkpoint as interrupted; runId=$fileRunId; samples=$($report.sampleCount); observationCompleted=false. Host was not contacted."
