<#
.SYNOPSIS
Measures a visible idle run of one explicitly selected installed XiaoK MSIX.
.DESCRIPTION
Refuses to attach to an existing Host, starts the selected package with an isolated diagnostics profile,
checks that launch does not change the foreground window, samples process/system resource counters, and
requests the app's registered graceful shutdown when the observation ends. It never force-kills the Host.
The diagnostics profile uses temporary data/model/voice roots and disables notification monitoring and wake word.
Only aggregate counters and package hashes are written under the repository's ignored .tools/stability folder.
The script publishes an ignored control-file path; request a graceful partial report with
tools/request_xiaok_long_run_stop.ps1 -ControlFilePath <path>. The request never terminates a process.
.EXAMPLE
.\tools\measure_xiaok_long_run.ps1 -DurationMinutes 1440 -ExpectedVersion 0.1.125.0
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidateRange(1, 1440)]
    [int] $DurationMinutes,

    [Parameter(Mandatory = $true)]
    [ValidatePattern('^\d+\.\d+\.\d+\.\d+$')]
    [string] $ExpectedVersion,

    [ValidateRange(15, 600)]
    [int] $SampleIntervalSeconds = 60
)

$ErrorActionPreference = 'Stop'

$nativeSource = @'
using System;
using System.Runtime.InteropServices;

public static class XiaoKLongRunNativeMethods
{
    [DllImport("user32.dll")]
    public static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool IsWindowVisible(IntPtr window);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern uint RegisterWindowMessage(string messageName);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool PostMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate bool EnumWindowsCallback(IntPtr window, IntPtr state);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(EnumWindowsCallback callback, IntPtr state);

    public static int RequestShutdownForProcess(uint expectedProcessId)
    {
        uint message = RegisterWindowMessage("XiaoK.DesktopAssistant.Shutdown.v1");
        if (message == 0) return 0;

        int posted = 0;
        EnumWindowsCallback callback = (window, _) =>
        {
            uint actualProcessId;
            GetWindowThreadProcessId(window, out actualProcessId);
            if (actualProcessId == expectedProcessId
                && PostMessage(window, message, IntPtr.Zero, IntPtr.Zero)) posted++;
            return true;
        };
        if (!EnumWindows(callback, IntPtr.Zero)) return 0;
        return posted;
    }
}
'@
Add-Type -TypeDefinition $nativeSource -Language CSharp

function Get-LongRunStopRequestState {
    param(
        [Parameter(Mandatory = $true)][string] $Path,
        [Parameter(Mandatory = $true)][string] $RunId,
        [Parameter(Mandatory = $true)][int] $MonitorProcessId,
        [Parameter(Mandatory = $true)][int] $HostProcessId
    )

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        return [pscustomobject]@{ Exists = $false; Valid = $false; RequestedUtc = $null }
    }
    try {
        $request = Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json
        $requested = [DateTimeOffset]::MinValue
        $hasTimestamp = if ($request.requestedUtc -is [datetime]) {
            $requested = [DateTimeOffset]([datetime]$request.requestedUtc)
            $true
        }
        else {
            [DateTimeOffset]::TryParse([string]$request.requestedUtc, [ref]$requested)
        }
        $valid = $request.schemaVersion -eq 1 -and $request.runId -ceq $RunId -and
            $request.monitorProcessId -eq $MonitorProcessId -and $request.hostProcessId -eq $HostProcessId -and
            $hasTimestamp
        return [pscustomobject]@{
            Exists = $true
            Valid = [bool]$valid
            RequestedUtc = if ($valid) { $requested.ToUniversalTime().ToString('o') } else { $null }
        }
    }
    catch {
        return [pscustomobject]@{ Exists = $true; Valid = $false; RequestedUtc = $null }
    }
}

$package = @(
    Get-AppxPackage -Name 'MingKaiLin.XiaoK' |
        Where-Object { $_.Status -eq 'Ok' -and $_.Version.ToString() -eq $ExpectedVersion }
)
if ($package.Count -ne 1) {
    throw "Expected one installed XiaoK MSIX version $ExpectedVersion with status Ok; found $($package.Count)."
}

$existingHosts = @(Get-Process -Name 'XiaoK.Host' -ErrorAction SilentlyContinue)
if ($existingHosts.Count -ne 0) {
    throw "A XiaoK.Host process is already running; refusing to attach or activate it."
}

$hostExecutable = Join-Path $package[0].InstallLocation 'XiaoK.Host.exe'
$hostLibrary = Join-Path $package[0].InstallLocation 'XiaoK.Host.dll'
if (-not (Test-Path -LiteralPath $hostExecutable -PathType Leaf) -or
    -not (Test-Path -LiteralPath $hostLibrary -PathType Leaf)) {
    throw 'The installed Host executable or library is missing.'
}

$executableHash = (Get-FileHash -LiteralPath $hostExecutable -Algorithm SHA256).Hash
$libraryHash = (Get-FileHash -LiteralPath $hostLibrary -Algorithm SHA256).Hash
$reportDirectory = Join-Path (Split-Path -Parent $PSScriptRoot) '.tools\stability'
New-Item -ItemType Directory -Path $reportDirectory -Force | Out-Null
$runId = [guid]::NewGuid().ToString('N')
$reportPath = Join-Path $reportDirectory ('xiaok-visible-idle-{0}-{1}.json' -f $ExpectedVersion, $runId)
$controlFilePath = Join-Path $reportDirectory "xiaok-visible-idle-$runId.control.json"
$stopRequestPath = Join-Path $reportDirectory "xiaok-visible-idle-$runId.stop.json"
$stopEventName = "Local\XiaoKLongRun_$runId"
$createdStopEvent = $false
$stopEvent = [Threading.EventWaitHandle]::new(
    $false, [Threading.EventResetMode]::ManualReset, $stopEventName, [ref]$createdStopEvent)
if (-not $createdStopEvent) {
    $stopEvent.Dispose()
    throw 'Could not create a unique long-run stop event.'
}
$monitorProcess = Get-Process -Id $PID
$foregroundBeforeLaunch = [XiaoKLongRunNativeMethods]::GetForegroundWindow()
$startedUtc = [datetime]::UtcNow
$launched = $null
$hostProcessId = 0
try {
    $launched = Start-Process -FilePath $hostExecutable -ArgumentList '--diagnostics-profile' -PassThru
    $hostProcessId = $launched.Id
    $hostStartedUtc = $launched.StartTime.ToUniversalTime().ToString('o')
    $monitorStartedUtc = $monitorProcess.StartTime.ToUniversalTime().ToString('o')
    $control = [ordered]@{
        schemaVersion = 1
        runId = $runId
        state = 'running'
        monitorProcessId = $PID
        monitorStartedUtc = $monitorStartedUtc
        hostProcessId = $hostProcessId
        hostStartedUtc = $hostStartedUtc
        stopEventName = $stopEventName
    }
    $control | ConvertTo-Json -Compress | Set-Content -LiteralPath $controlFilePath -Encoding UTF8
}
catch {
    if ($null -ne $launched) {
        [void][XiaoKLongRunNativeMethods]::RequestShutdownForProcess([uint32]$hostProcessId)
        $cleanupClock = [System.Diagnostics.Stopwatch]::StartNew()
        while ($cleanupClock.Elapsed.TotalSeconds -lt 15 -and
            (Get-Process -Id $hostProcessId -ErrorAction SilentlyContinue)) {
            Start-Sleep -Milliseconds 500
        }
    }
    $stopEvent.Dispose()
    Remove-Item -LiteralPath $controlFilePath, $stopRequestPath -Force -ErrorAction SilentlyContinue
    throw
}
Write-Output "controlFile=$controlFilePath"
$process = $null
$mainWindow = [IntPtr]::Zero
$launchForegroundUnchanged = $false
$outcome = 'startup_timeout'
$errorKind = $null
$stopRequestObserved = $false
$stopRequestedUtc = $null
$clock = [System.Diagnostics.Stopwatch]::StartNew()
$startupClock = [System.Diagnostics.Stopwatch]::StartNew()

while ($startupClock.Elapsed.TotalSeconds -lt 45) {
    if ($stopEvent.WaitOne(0)) {
        $stopState = Get-LongRunStopRequestState -Path $stopRequestPath -RunId $runId `
            -MonitorProcessId $PID -HostProcessId $hostProcessId
        $stopRequestObserved = $true
        $stopRequestedUtc = $stopState.RequestedUtc
        $outcome = if ($stopState.Valid) { 'interrupted_during_startup' } else { 'invalid_stop_request' }
        break
    }
    try {
        $process = Get-Process -Id $hostProcessId -ErrorAction SilentlyContinue
        if ($null -eq $process) {
            $outcome = 'process_exited_during_startup'
            break
        }

        $process.Refresh()
        if ($process.Path -and [string]::Equals(
                [IO.Path]::GetFullPath($process.Path),
                [IO.Path]::GetFullPath($hostExecutable),
                [StringComparison]::OrdinalIgnoreCase) -and
            $process.MainWindowHandle -ne [IntPtr]::Zero -and
            [XiaoKLongRunNativeMethods]::IsWindowVisible($process.MainWindowHandle)) {
            $mainWindow = $process.MainWindowHandle
            $launchForegroundUnchanged = ([XiaoKLongRunNativeMethods]::GetForegroundWindow() -eq $foregroundBeforeLaunch)
            if ($launchForegroundUnchanged -and $process.Responding) {
                $outcome = 'completed_duration'
            }
            else {
                $outcome = 'foreground_changed_at_launch'
            }
            break
        }
    }
    catch {
        $outcome = 'startup_error'
        $errorKind = $_.Exception.GetType().Name
        break
    }
    Start-Sleep -Seconds 1
}

if ($outcome -ne 'completed_duration') {
    $clock.Stop()
    $clock.Reset()
}

$sampleCount = 0
$expectedSampleCount = [int][Math]::Ceiling(($DurationMinutes * 60) / $SampleIntervalSeconds)
$visibleSampleCount = 0
$responsiveSampleCount = 0
$unresponsiveStreak = 0
$maxUnresponsiveStreak = 0
$counterFailureCount = 0
$availableMemoryMiBMin = $null
$availableMemoryMiBMax = $null
$privateMemoryMiBMax = 0.0
$workingSetMiBMax = 0.0
$dedicatedGpuMiBMax = $null
$sharedGpuMiBMax = $null
$threadCountMin = $null
$threadCountMax = 0
$cpuAtStartSeconds = $null
$cpuAtEndSeconds = $null
$foregroundWindowWasHost = $false
$sampleGapCount = 0
$maximumSampleGapSeconds = 0.0
$previousSampleUtc = $null
$shutdownPosted = $false
$shutdownExited = $false
$exitCode = $null

try {
    if ($outcome -eq 'completed_duration' -and $stopEvent.WaitOne(0)) {
        $stopState = Get-LongRunStopRequestState -Path $stopRequestPath -RunId $runId `
            -MonitorProcessId $PID -HostProcessId $hostProcessId
        $stopRequestObserved = $true
        $stopRequestedUtc = $stopState.RequestedUtc
        $outcome = if ($stopState.Valid) { 'interrupted_during_startup' } else { 'invalid_stop_request' }
    }
    if ($outcome -eq 'completed_duration') {
        $clock.Restart()
        $process.Refresh()
        $cpuAtStartSeconds = [double]$process.CPU
        $previousSampleUtc = [datetime]::UtcNow
        while ($clock.Elapsed.TotalMinutes -lt $DurationMinutes) {
            if ($stopEvent.WaitOne(0)) {
                $stopState = Get-LongRunStopRequestState -Path $stopRequestPath -RunId $runId `
                    -MonitorProcessId $PID -HostProcessId $hostProcessId
                $stopRequestObserved = $true
                $stopRequestedUtc = $stopState.RequestedUtc
                $outcome = if ($stopState.Valid) { 'interrupted' } else { 'invalid_stop_request' }
                break
            }
            $process = Get-Process -Id $hostProcessId -ErrorAction SilentlyContinue
            if ($null -eq $process) {
                $outcome = 'process_exited_early'
                break
            }

            $process.Refresh()
            if (-not $process.Path -or -not [string]::Equals(
                    [IO.Path]::GetFullPath($process.Path),
                    [IO.Path]::GetFullPath($hostExecutable),
                    [StringComparison]::OrdinalIgnoreCase)) {
                $outcome = 'process_identity_changed'
                break
            }

            $sampleUtc = [datetime]::UtcNow
            $gapSeconds = ($sampleUtc - $previousSampleUtc).TotalSeconds
            if ($gapSeconds -gt ($SampleIntervalSeconds * 1.75)) { $sampleGapCount++ }
            $maximumSampleGapSeconds = [Math]::Max($maximumSampleGapSeconds, $gapSeconds)
            $previousSampleUtc = $sampleUtc
            $sampleCount++

            if ([XiaoKLongRunNativeMethods]::IsWindowVisible($mainWindow)) { $visibleSampleCount++ }
            if ([XiaoKLongRunNativeMethods]::GetForegroundWindow() -eq $mainWindow) {
                $foregroundWindowWasHost = $true
            }

            if ($process.Responding) {
                $responsiveSampleCount++
                $unresponsiveStreak = 0
            }
            else {
                $unresponsiveStreak++
                $maxUnresponsiveStreak = [Math]::Max($maxUnresponsiveStreak, $unresponsiveStreak)
                if ($unresponsiveStreak -ge 3) {
                    $outcome = 'unresponsive'
                    break
                }
            }

            $privateMemoryMiBMax = [Math]::Max($privateMemoryMiBMax, [double]$process.PrivateMemorySize64 / 1MB)
            $workingSetMiBMax = [Math]::Max($workingSetMiBMax, [double]$process.WorkingSet64 / 1MB)
            $threadCountMin = if ($null -eq $threadCountMin) { $process.Threads.Count } else {
                [Math]::Min([int]$threadCountMin, $process.Threads.Count)
            }
            $threadCountMax = [Math]::Max($threadCountMax, $process.Threads.Count)

            try {
                $counterSet = Get-Counter -Counter @(
                    '\GPU Process Memory(*)\Dedicated Usage',
                    '\GPU Process Memory(*)\Shared Usage',
                    '\Memory\Available MBytes'
                ) -MaxSamples 1
                $pidPattern = '^pid_' + [regex]::Escape([string]$hostProcessId) + '_'
                $availableSamples = @(
                    $counterSet.CounterSamples | Where-Object { $_.Path -match '\\Memory\\Available MBytes$' }
                )
                if ($availableSamples.Count -gt 0) {
                    $availableMiB = [double]$availableSamples[0].CookedValue
                    $availableMemoryMiBMin = if ($null -eq $availableMemoryMiBMin) { $availableMiB } else {
                        [Math]::Min([double]$availableMemoryMiBMin, $availableMiB)
                    }
                    $availableMemoryMiBMax = if ($null -eq $availableMemoryMiBMax) { $availableMiB } else {
                        [Math]::Max([double]$availableMemoryMiBMax, $availableMiB)
                    }
                }
                $hostGpuSamples = @(
                    $counterSet.CounterSamples | Where-Object { $_.InstanceName -match $pidPattern }
                )
                $dedicated = @($hostGpuSamples | Where-Object { $_.Path -match '\\Dedicated Usage$' })
                $shared = @($hostGpuSamples | Where-Object { $_.Path -match '\\Shared Usage$' })
                if ($availableSamples.Count -eq 0 -or $dedicated.Count -eq 0 -or $shared.Count -eq 0) {
                    $counterFailureCount++
                }
                if ($dedicated.Count -gt 0) {
                    $dedicatedBytes = ($dedicated | Measure-Object -Property CookedValue -Sum).Sum
                    $dedicatedMiB = [double]$dedicatedBytes / 1MB
                    $dedicatedGpuMiBMax = if ($null -eq $dedicatedGpuMiBMax) { $dedicatedMiB } else {
                        [Math]::Max([double]$dedicatedGpuMiBMax, $dedicatedMiB)
                    }
                }
                if ($shared.Count -gt 0) {
                    $sharedBytes = ($shared | Measure-Object -Property CookedValue -Sum).Sum
                    $sharedMiB = [double]$sharedBytes / 1MB
                    $sharedGpuMiBMax = if ($null -eq $sharedGpuMiBMax) { $sharedMiB } else {
                        [Math]::Max([double]$sharedGpuMiBMax, $sharedMiB)
                    }
                }
            }
            catch {
                $counterFailureCount++
            }

            $remainingSleepMilliseconds = [int][Math]::Ceiling(
                ($SampleIntervalSeconds - ([datetime]::UtcNow - $sampleUtc).TotalSeconds) * 1000)
            $remainingObservationMilliseconds = [int][Math]::Ceiling(
                ($DurationMinutes * 60 - $clock.Elapsed.TotalSeconds) * 1000)
            $sleepMilliseconds = [Math]::Min($remainingSleepMilliseconds, $remainingObservationMilliseconds)
            if ($sleepMilliseconds -gt 0 -and $stopEvent.WaitOne($sleepMilliseconds)) {
                $stopState = Get-LongRunStopRequestState -Path $stopRequestPath -RunId $runId `
                    -MonitorProcessId $PID -HostProcessId $hostProcessId
                $stopRequestObserved = $true
                $stopRequestedUtc = $stopState.RequestedUtc
                $outcome = if ($stopState.Valid) { 'interrupted' } else { 'invalid_stop_request' }
                break
            }
        }

        if ($outcome -eq 'completed_duration' -and $clock.Elapsed.TotalMinutes -ge $DurationMinutes) {
            $process = Get-Process -Id $hostProcessId -ErrorAction SilentlyContinue
            if ($null -ne $process) {
                $process.Refresh()
                $cpuAtEndSeconds = [double]$process.CPU
            }
            else {
                $outcome = 'process_exited_at_end'
            }
        }
        elseif ($null -eq $cpuAtEndSeconds -and $null -ne $cpuAtStartSeconds) {
            $process = Get-Process -Id $hostProcessId -ErrorAction SilentlyContinue
            if ($null -ne $process) {
                $process.Refresh()
                $cpuAtEndSeconds = [double]$process.CPU
            }
        }
        $clock.Stop()
    }
}
catch {
    $clock.Stop()
    $outcome = 'monitor_error'
    $errorKind = $_.Exception.GetType().Name
}
finally {
    try {
        $control.state = 'finishing'
        $control | ConvertTo-Json -Compress | Set-Content -LiteralPath $controlFilePath -Encoding UTF8
    }
    catch { }

    $process = Get-Process -Id $hostProcessId -ErrorAction SilentlyContinue
    if ($null -ne $process) {
        $shutdownPosted = ([XiaoKLongRunNativeMethods]::RequestShutdownForProcess([uint32]$hostProcessId) -gt 0)
        $shutdownClock = [System.Diagnostics.Stopwatch]::StartNew()
        while ($shutdownClock.Elapsed.TotalSeconds -lt 15 -and
            (Get-Process -Id $hostProcessId -ErrorAction SilentlyContinue)) {
            Start-Sleep -Milliseconds 500
        }
        $shutdownExited = ($null -eq (Get-Process -Id $hostProcessId -ErrorAction SilentlyContinue))
        if (-not $shutdownExited -and $outcome -eq 'completed_duration') { $outcome = 'shutdown_timeout' }
    }
    elseif ($null -eq $process) {
        $shutdownExited = $true
    }

    $process = Get-Process -Id $hostProcessId -ErrorAction SilentlyContinue
    if ($null -eq $process) {
        try {
            $launched.Refresh()
            if ($launched.HasExited) { $exitCode = $launched.ExitCode }
        }
        catch { $exitCode = $null }
    }
    $stopEvent.Dispose()
}

if ($outcome -eq 'completed_duration') {
    if ($sampleGapCount -gt 0 -or $sampleCount -lt $expectedSampleCount -or
        $visibleSampleCount -lt $sampleCount) {
        $outcome = 'incomplete_observation'
    }
    elseif ($responsiveSampleCount -lt $sampleCount) {
        $outcome = 'unresponsive_samples'
    }
    elseif ($counterFailureCount -gt 0) {
        $outcome = 'incomplete_counters'
    }
}

$logicalProcessorCount = [Environment]::ProcessorCount
$cpuAveragePercentOfMachine = $null
if ($null -ne $cpuAtStartSeconds -and $null -ne $cpuAtEndSeconds -and $clock.Elapsed.TotalSeconds -gt 0) {
    $cpuAveragePercentOfMachine = [Math]::Round(
        (($cpuAtEndSeconds - $cpuAtStartSeconds) / $clock.Elapsed.TotalSeconds / $logicalProcessorCount) * 100, 4)
}

$report = [ordered]@{
    packageVersion = $ExpectedVersion
    hostExecutableSha256 = $executableHash
    hostDllSha256 = $libraryHash
    durationRequestedMinutes = $DurationMinutes
    durationObservedSeconds = [Math]::Round($clock.Elapsed.TotalSeconds, 1)
    sampleIntervalSeconds = $SampleIntervalSeconds
    expectedSampleCount = $expectedSampleCount
    sampleCount = $sampleCount
    outcome = $outcome
    observationCompleted = ($outcome -eq 'completed_duration')
    stopRequestObserved = $stopRequestObserved
    stopRequestedUtc = $stopRequestedUtc
    monitorErrorType = $errorKind
    processId = $hostProcessId
    processExitedNormallyAfterShutdownRequest = $shutdownExited
    shutdownMessagePosted = $shutdownPosted
    exitCode = $exitCode
    launchForegroundUnchanged = $launchForegroundUnchanged
    foregroundWindowWasHostDuringSample = $foregroundWindowWasHost
    visibleWindowSamples = $visibleSampleCount
    responsiveSamples = $responsiveSampleCount
    maximumUnresponsiveStreak = $maxUnresponsiveStreak
    sampleGapCount = $sampleGapCount
    maximumSampleGapSeconds = [Math]::Round($maximumSampleGapSeconds, 1)
    averageCpuPercentOfMachine = $cpuAveragePercentOfMachine
    privateMemoryMiBPeak = [Math]::Round($privateMemoryMiBMax, 1)
    workingSetMiBPeak = [Math]::Round($workingSetMiBMax, 1)
    threadCountMinimum = $threadCountMin
    threadCountMaximum = $threadCountMax
    availableMemoryMiBMinimum = if ($null -eq $availableMemoryMiBMin) { $null } else { [Math]::Round($availableMemoryMiBMin, 0) }
    availableMemoryMiBMaximum = if ($null -eq $availableMemoryMiBMax) { $null } else { [Math]::Round($availableMemoryMiBMax, 0) }
    dedicatedGpuMiBPeak = if ($null -eq $dedicatedGpuMiBMax) { $null } else { [Math]::Round($dedicatedGpuMiBMax, 2) }
    sharedGpuMiBPeak = if ($null -eq $sharedGpuMiBMax) { $null } else { [Math]::Round($sharedGpuMiBMax, 2) }
    counterFailureCount = $counterFailureCount
    startedUtc = $startedUtc.ToString('o')
    endedUtc = [datetime]::UtcNow.ToString('o')
}

$json = $report | ConvertTo-Json -Depth 4
Set-Content -LiteralPath $reportPath -Value $json -Encoding UTF8
Remove-Item -LiteralPath $controlFilePath, $stopRequestPath -Force -ErrorAction SilentlyContinue
Write-Output $json
Write-Output "reportPath=$reportPath"

if ($outcome -ne 'completed_duration' -or -not $shutdownExited) { exit 1 }
