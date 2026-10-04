[CmdletBinding()]
param(
    [ValidateRange(1, 30)]
    [int] $SampleIntervalSeconds = 5,
    [ValidateRange(2, 12)]
    [int] $SampleCount = 5
)

$ErrorActionPreference = 'Stop'
$hostProcesses = @(
    Get-Process -Name 'XiaoK.Host' -ErrorAction SilentlyContinue |
        Where-Object { $_.Path -match '\\WindowsApps\\MingKaiLin\.XiaoK_[^\\]+\\XiaoK\.Host\.exe$' }
)
if ($hostProcesses.Count -ne 1) {
    throw ('Expected exactly one installed XiaoK.Host process; found {0}. No samples collected.' -f $hostProcesses.Count)
}

$hostProcess = $hostProcesses[0]
if (-not $hostProcess.Responding) {
    throw 'XiaoK.Host is not responding. No samples collected.'
}

$logicalProcessorCount = [Environment]::ProcessorCount
if ($logicalProcessorCount -le 0) { throw 'Could not read the logical processor count. No samples collected.' }

$before = Get-Process -Id $hostProcess.Id -ErrorAction Stop
$samplingClock = [System.Diagnostics.Stopwatch]::StartNew()
$counterSet = Get-Counter -Counter @(
    '\GPU Process Memory(*)\Dedicated Usage',
    '\GPU Process Memory(*)\Shared Usage',
    '\Memory\Available MBytes'
) -SampleInterval $SampleIntervalSeconds -MaxSamples $SampleCount
$samplingClock.Stop()
$after = Get-Process -Id $hostProcess.Id -ErrorAction Stop
$hostCounterPattern = '^pid_' + [regex]::Escape([string]$hostProcess.Id) + '_'

$hostCounterSamples = @(
    $counterSet.CounterSamples |
        Where-Object { $_.InstanceName -match $hostCounterPattern }
)
if ($hostCounterSamples.Count -eq 0) {
    throw 'Windows returned no GPU process counters for XiaoK.Host. No resource conclusion produced.'
}
$availableMemorySamples = @(
    $counterSet.CounterSamples | Where-Object { $_.Path -match '\\Memory\\Available MBytes$' }
)
if ($availableMemorySamples.Count -eq 0) {
    throw 'Windows returned no available-memory counters. No resource conclusion produced.'
}

$samples = @(
    foreach ($timeGroup in ($hostCounterSamples | Group-Object Timestamp)) {
        $dedicatedBytes = ($timeGroup.Group | Where-Object { $_.Path -match '\\Dedicated Usage$' } |
            Measure-Object -Property CookedValue -Sum).Sum
        $sharedBytes = ($timeGroup.Group | Where-Object { $_.Path -match '\\Shared Usage$' } |
            Measure-Object -Property CookedValue -Sum).Sum
        [pscustomobject]@{
            time = [datetime]$timeGroup.Name
            dedicatedMiB = [math]::Round([double]$dedicatedBytes / 1MB, 2)
            sharedMiB = [math]::Round([double]$sharedBytes / 1MB, 2)
        }
    }
)

$cpuPercentOfMachine = (($after.CPU - $before.CPU) / $samplingClock.Elapsed.TotalSeconds / $logicalProcessorCount) * 100
$result = [ordered]@{
    processId = $hostProcess.Id
    responding = [bool]$after.Responding
    sampleDurationSeconds = [math]::Round($samplingClock.Elapsed.TotalSeconds, 1)
    logicalProcessorCount = $logicalProcessorCount
    averageCpuPercentOfMachine = [math]::Round($cpuPercentOfMachine, 2)
    workingSetMiB = [math]::Round($after.WorkingSet64 / 1MB, 1)
    systemAvailableMemoryMiBMin = [math]::Round(($availableMemorySamples | Measure-Object CookedValue -Minimum).Minimum, 0)
    systemAvailableMemoryMiBMax = [math]::Round(($availableMemorySamples | Measure-Object CookedValue -Maximum).Maximum, 0)
    dedicatedGpuMiBMin = [math]::Round(($samples | Measure-Object dedicatedMiB -Minimum).Minimum, 2)
    dedicatedGpuMiBMax = [math]::Round(($samples | Measure-Object dedicatedMiB -Maximum).Maximum, 2)
    sharedGpuMiBMin = [math]::Round(($samples | Measure-Object sharedMiB -Minimum).Minimum, 2)
    sharedGpuMiBMax = [math]::Round(($samples | Measure-Object sharedMiB -Maximum).Maximum, 2)
    gpuSamples = $samples
}
$result | ConvertTo-Json -Depth 4
