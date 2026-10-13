$ErrorActionPreference = 'Stop'
$script:assertionCount = 0

function Assert-Condition {
    param([bool] $Condition, [string] $Message)
    $script:assertionCount++
    if (-not $Condition) { throw $Message }
}

$repositoryRoot = Split-Path -Parent $PSScriptRoot
$stabilityDirectory = Join-Path $repositoryRoot '.tools\stability'
New-Item -ItemType Directory -Path $stabilityDirectory -Force | Out-Null
$helperPath = Join-Path $PSScriptRoot 'request_xiaok_long_run_stop.ps1'
$currentProcess = Get-Process -Id $PID
$currentStartedUtc = $currentProcess.StartTime.ToUniversalTime().ToString('o')
$runId = [guid]::NewGuid().ToString('N')
$eventName = "Local\XiaoKLongRun_$runId"
$controlPath = Join-Path $stabilityDirectory "xiaok-visible-idle-$runId.control.json"
$requestPath = Join-Path $stabilityDirectory "xiaok-visible-idle-$runId.stop.json"
$createdEvent = $false
$event = [Threading.EventWaitHandle]::new(
    $false, [Threading.EventResetMode]::ManualReset, $eventName, [ref]$createdEvent)

try {
    Assert-Condition $createdEvent 'Could not create the synthetic named event.'
    $control = [ordered]@{
        schemaVersion = 1
        runId = $runId
        state = 'running'
        monitorProcessId = $PID
        monitorStartedUtc = $currentStartedUtc
        hostProcessId = $PID
        hostStartedUtc = $currentStartedUtc
        stopEventName = $eventName
    }
    [IO.File]::WriteAllText($controlPath, ($control | ConvertTo-Json -Compress), [Text.UTF8Encoding]::new($false))

    & $helperPath -ControlFilePath $controlPath | Out-Null
    Assert-Condition $event.WaitOne(0) 'A valid request did not signal the recorded event.'
    Assert-Condition (Test-Path -LiteralPath $requestPath -PathType Leaf) 'A valid request did not leave its audit marker.'
    $request = Get-Content -LiteralPath $requestPath -Raw | ConvertFrom-Json
    Assert-Condition ($request.runId -ceq $runId -and $request.monitorProcessId -eq $PID -and
        $request.hostProcessId -eq $PID) 'The request marker did not preserve the recorded process identity.'
    Assert-Condition ($null -ne (Get-Process -Id $PID -ErrorAction SilentlyContinue)) 'The helper terminated the calling process.'

    $duplicateRejected = $false
    try { & $helperPath -ControlFilePath $controlPath | Out-Null }
    catch { $duplicateRejected = $true }
    Assert-Condition $duplicateRejected 'A duplicate request should not overwrite an existing request marker.'
    Assert-Condition (Test-Path -LiteralPath $requestPath -PathType Leaf) 'A duplicate request removed the original marker.'

    $outsidePath = Join-Path $repositoryRoot 'tools\not-a-stability-control.json'
    $outsideRejected = $false
    try { & $helperPath -ControlFilePath $outsidePath | Out-Null }
    catch { $outsideRejected = $true }
    Assert-Condition $outsideRejected 'The helper accepted a path outside .tools/stability.'

    $staleRunId = [guid]::NewGuid().ToString('N')
    $staleEventName = "Local\XiaoKLongRun_$staleRunId"
    $stalePath = Join-Path $stabilityDirectory "xiaok-visible-idle-$staleRunId.control.json"
    $staleRequestPath = Join-Path $stabilityDirectory "xiaok-visible-idle-$staleRunId.stop.json"
    $staleEventCreated = $false
    $staleEvent = [Threading.EventWaitHandle]::new(
        $false, [Threading.EventResetMode]::ManualReset, $staleEventName, [ref]$staleEventCreated)
    try {
        $staleControl = [ordered]@{
            schemaVersion = 1
            runId = $staleRunId
            state = 'running'
            monitorProcessId = $PID
            monitorStartedUtc = ([datetime]::UtcNow.AddMinutes(-10)).ToString('o')
            hostProcessId = $PID
            hostStartedUtc = $currentStartedUtc
            stopEventName = $staleEventName
        }
        [IO.File]::WriteAllText($stalePath, ($staleControl | ConvertTo-Json -Compress), [Text.UTF8Encoding]::new($false))
        $staleRejected = $false
        try { & $helperPath -ControlFilePath $stalePath | Out-Null }
        catch { $staleRejected = $true }
        Assert-Condition $staleRejected 'The helper accepted a reused PID with a mismatched start time.'
        Assert-Condition (-not $staleEvent.WaitOne(0)) 'A stale process identity signaled the event.'
        Assert-Condition (-not (Test-Path -LiteralPath $staleRequestPath)) 'A stale process identity created a request marker.'
    }
    finally {
        $staleEvent.Dispose()
        Remove-Item -LiteralPath $stalePath, $staleRequestPath -Force -ErrorAction SilentlyContinue
    }
}
finally {
    $event.Dispose()
    Remove-Item -LiteralPath $controlPath, $requestPath -Force -ErrorAction SilentlyContinue
}

Write-Output "Long-run cooperative-stop checks: $script:assertionCount passed."
