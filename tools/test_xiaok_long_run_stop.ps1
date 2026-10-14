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
$monitorScriptPath = Join-Path $PSScriptRoot 'measure_xiaok_long_run.ps1'
$reportModulePath = Join-Path $PSScriptRoot 'XiaoKLongRunReport.psm1'
Import-Module $reportModulePath -Force
$monitorTokens = $null
$monitorParseErrors = $null
$monitorAst = [System.Management.Automation.Language.Parser]::ParseFile(
    $monitorScriptPath, [ref]$monitorTokens, [ref]$monitorParseErrors)
Assert-Condition ($monitorParseErrors.Count -eq 0) 'The long-run monitor script has PowerShell parse errors.'
$checkpointLoop = $monitorAst.Find({
    param($node)
    if ($node -isnot [System.Management.Automation.Language.WhileStatementAst]) { return $false }
    @($node.FindAll({
        param($child)
        $child -is [System.Management.Automation.Language.CommandAst] -and
            $child.GetCommandName() -eq 'Save-LongRunReportSnapshot'
    }, $true)).Count -gt 0
}, $true)
Assert-Condition ($null -ne $checkpointLoop) 'The long-run monitor must write a report checkpoint from its sampling loop.'
$shutdownCalls = @(
    $monitorAst.FindAll({
        param($node)
        $node -is [System.Management.Automation.Language.InvokeMemberExpressionAst] -and
            $node.Member.Extent.Text -eq 'RequestShutdownForProcess'
    }, $true)
)
Assert-Condition ($shutdownCalls.Count -eq 2) 'Unexpected number of graceful-shutdown call sites in the monitor.'
foreach ($shutdownCall in $shutdownCalls) {
    $ownershipGuarded = $false
    $ancestor = $shutdownCall.Parent
    while ($null -ne $ancestor -and -not $ownershipGuarded) {
        if ($ancestor -is [System.Management.Automation.Language.IfStatementAst]) {
            foreach ($clause in $ancestor.Clauses) {
                if ($clause.Item1.Extent.Text -notmatch '\$hostOwnedByMonitor') { continue }
                $branchNode = $shutdownCall
                while ($null -ne $branchNode.Parent -and $branchNode.Parent -ne $clause.Item2) {
                    $branchNode = $branchNode.Parent
                }
                if ($branchNode.Parent -eq $clause.Item2) {
                    $ownershipGuarded = $true
                    break
                }
            }
        }
        if (-not $ownershipGuarded) { $ancestor = $ancestor.Parent }
    }
    Assert-Condition $ownershipGuarded 'A Host shutdown call is not guarded by monitor ownership.'
}
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

    $atomicTestDirectory = Join-Path $stabilityDirectory "atomic-report-test-$runId"
    [void][IO.Directory]::CreateDirectory($atomicTestDirectory)
    $atomicTestPath = Join-Path $atomicTestDirectory 'report.json'
    try {
        Write-XiaoKLongRunJsonAtomic -Path $atomicTestPath -InputObject ([ordered]@{
            reportState = 'checkpoint'; sampleCount = 3; observationCompleted = $false
        })
        $firstCheckpoint = Get-Content -LiteralPath $atomicTestPath -Raw | ConvertFrom-Json
        Assert-Condition ($firstCheckpoint.reportState -ceq 'checkpoint' -and
            $firstCheckpoint.sampleCount -eq 3 -and -not $firstCheckpoint.observationCompleted) `
            'The atomic writer did not persist a valid incomplete checkpoint.'

        Write-XiaoKLongRunJsonAtomic -Path $atomicTestPath -InputObject ([ordered]@{
            reportState = 'final'; sampleCount = 4; observationCompleted = $true
        })
        $finalReport = Get-Content -LiteralPath $atomicTestPath -Raw | ConvertFrom-Json
        Assert-Condition ($finalReport.reportState -ceq 'final' -and
            $finalReport.sampleCount -eq 4 -and $finalReport.observationCompleted) `
            'The atomic writer did not replace a checkpoint with the final report.'
        Assert-Condition (@(Get-ChildItem -LiteralPath $atomicTestDirectory -File -Force).Count -eq 1) `
            'The atomic writer left a temporary file behind.'
    }
    finally {
        Remove-Item -LiteralPath $atomicTestDirectory -Recurse -Force -ErrorAction SilentlyContinue
    }

    $control = [ordered]@{
        schemaVersion = 1
        runId = $runId
        state = 'running'
        observationMode = 'visible-idle'
        hostOwnedByMonitor = $true
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
        $request.hostProcessId -eq $PID -and $request.observationMode -ceq 'visible-idle' -and
        $request.hostOwnedByMonitor) 'The request marker did not preserve the recorded process identity.'
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

    $attachedRunId = [guid]::NewGuid().ToString('N')
    $attachedEventName = "Local\XiaoKLongRun_$attachedRunId"
    $attachedPath = Join-Path $stabilityDirectory "xiaok-attached-session-$attachedRunId.control.json"
    $attachedRequestPath = Join-Path $stabilityDirectory "xiaok-attached-session-$attachedRunId.stop.json"
    $attachedEventCreated = $false
    $attachedEvent = [Threading.EventWaitHandle]::new(
        $false, [Threading.EventResetMode]::ManualReset, $attachedEventName, [ref]$attachedEventCreated)
    try {
        $unusedHostProcessId = 1000000
        while ($null -ne (Get-Process -Id $unusedHostProcessId -ErrorAction SilentlyContinue)) {
            $unusedHostProcessId++
        }
        $attachedControl = [ordered]@{
            schemaVersion = 1
            runId = $attachedRunId
            state = 'running'
            observationMode = 'attached-session'
            hostOwnedByMonitor = $false
            monitorProcessId = $PID
            monitorStartedUtc = $currentStartedUtc
            hostProcessId = $unusedHostProcessId
            hostStartedUtc = [datetime]::UtcNow.ToString('o')
            stopEventName = $attachedEventName
        }
        [IO.File]::WriteAllText($attachedPath, ($attachedControl | ConvertTo-Json -Compress), [Text.UTF8Encoding]::new($false))
        & $helperPath -ControlFilePath $attachedPath | Out-Null
        Assert-Condition $attachedEvent.WaitOne(0) 'The helper did not stop an attached monitor after its Host exited.'
        $attachedRequest = Get-Content -LiteralPath $attachedRequestPath -Raw | ConvertFrom-Json
        Assert-Condition ($attachedRequest.observationMode -ceq 'attached-session' -and
            -not $attachedRequest.hostOwnedByMonitor) 'The attached stop request lost its no-shutdown ownership state.'
        Assert-Condition ($null -ne (Get-Process -Id $PID -ErrorAction SilentlyContinue)) 'The attached stop helper terminated the monitor process.'
    }
    finally {
        $attachedEvent.Dispose()
        Remove-Item -LiteralPath $attachedPath, $attachedRequestPath -Force -ErrorAction SilentlyContinue
    }

    $ownedMissingRunId = [guid]::NewGuid().ToString('N')
    $ownedMissingEventName = "Local\XiaoKLongRun_$ownedMissingRunId"
    $ownedMissingPath = Join-Path $stabilityDirectory "xiaok-visible-idle-$ownedMissingRunId.control.json"
    $ownedMissingRequestPath = Join-Path $stabilityDirectory "xiaok-visible-idle-$ownedMissingRunId.stop.json"
    $ownedMissingEventCreated = $false
    $ownedMissingEvent = [Threading.EventWaitHandle]::new(
        $false, [Threading.EventResetMode]::ManualReset, $ownedMissingEventName, [ref]$ownedMissingEventCreated)
    try {
        $ownedMissingControl = [ordered]@{
            schemaVersion = 1
            runId = $ownedMissingRunId
            state = 'running'
            observationMode = 'visible-idle'
            hostOwnedByMonitor = $true
            monitorProcessId = $PID
            monitorStartedUtc = $currentStartedUtc
            hostProcessId = $unusedHostProcessId
            hostStartedUtc = [datetime]::UtcNow.ToString('o')
            stopEventName = $ownedMissingEventName
        }
        [IO.File]::WriteAllText($ownedMissingPath, ($ownedMissingControl | ConvertTo-Json -Compress), [Text.UTF8Encoding]::new($false))
        $missingOwnedHostRejected = $false
        try { & $helperPath -ControlFilePath $ownedMissingPath | Out-Null }
        catch { $missingOwnedHostRejected = $true }
        Assert-Condition $missingOwnedHostRejected 'The helper accepted a missing Host owned by the monitor.'
        Assert-Condition (-not $ownedMissingEvent.WaitOne(0)) 'A missing owned Host was signaled.'
        Assert-Condition (-not (Test-Path -LiteralPath $ownedMissingRequestPath)) 'A missing owned Host created a stop marker.'
    }
    finally {
        $ownedMissingEvent.Dispose()
        Remove-Item -LiteralPath $ownedMissingPath, $ownedMissingRequestPath -Force -ErrorAction SilentlyContinue
    }

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
