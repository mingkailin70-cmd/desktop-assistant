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
$recoveryScriptPath = Join-Path $PSScriptRoot 'recover_xiaok_long_run.ps1'
$reportModulePath = Join-Path $PSScriptRoot 'XiaoKLongRunReport.psm1'
Import-Module $reportModulePath -Force
$monitorTokens = $null
$monitorParseErrors = $null
$monitorAst = [System.Management.Automation.Language.Parser]::ParseFile(
    $monitorScriptPath, [ref]$monitorTokens, [ref]$monitorParseErrors)
Assert-Condition ($monitorParseErrors.Count -eq 0) 'The long-run monitor script has PowerShell parse errors.'
$atomicControlWrites = @(
    $monitorAst.FindAll({
        param($node)
        $node -is [System.Management.Automation.Language.CommandAst] -and
            $node.GetCommandName() -eq 'Write-XiaoKLongRunJsonAtomic' -and
            $node.Extent.Text -match '\$controlFilePath'
    }, $true)
)
$nonAtomicControlWrites = @(
    $monitorAst.FindAll({
        param($node)
        $node -is [System.Management.Automation.Language.CommandAst] -and
            $node.GetCommandName() -in @('Set-Content', 'Out-File') -and
            $node.Extent.Text -match '\$controlFilePath'
    }, $true)
)
Assert-Condition ($atomicControlWrites.Count -eq 2 -and $nonAtomicControlWrites.Count -eq 0) `
    'The long-run control file must use atomic writes both when published and when marked finishing.'
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
$recoveryTokens = $null
$recoveryParseErrors = $null
$recoveryAst = [System.Management.Automation.Language.Parser]::ParseFile(
    $recoveryScriptPath, [ref]$recoveryTokens, [ref]$recoveryParseErrors)
Assert-Condition ($recoveryParseErrors.Count -eq 0) 'The long-run recovery script has PowerShell parse errors.'
$forbiddenRecoveryCommands = @(
    $recoveryAst.FindAll({
        param($node)
        $node -is [System.Management.Automation.Language.CommandAst] -and
            $node.GetCommandName() -in @('Start-Process', 'Stop-Process', 'Restart-Computer', 'Stop-Computer')
    }, $true)
)
$forbiddenRecoveryMembers = @(
    $recoveryAst.FindAll({
        param($node)
        $node -is [System.Management.Automation.Language.InvokeMemberExpressionAst] -and
            $node.Member.Extent.Text -in @('RequestShutdownForProcess', 'Set', 'OpenExisting')
    }, $true)
)
Assert-Condition ($forbiddenRecoveryCommands.Count -eq 0 -and $forbiddenRecoveryMembers.Count -eq 0) `
    'The recovery script must not start, stop, signal, or shut down a process.'
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

    $recoveryRunId = [guid]::NewGuid().ToString('N')
    $recoveryVersion = '9.9.9.9'
    $recoveryMonitorProcessId = 1000000
    while ($null -ne (Get-Process -Id $recoveryMonitorProcessId -ErrorAction SilentlyContinue)) {
        $recoveryMonitorProcessId++
    }
    $recoveryHostProcessId = $recoveryMonitorProcessId + 1
    while ($null -ne (Get-Process -Id $recoveryHostProcessId -ErrorAction SilentlyContinue)) {
        $recoveryHostProcessId++
    }
    $recoveryControlPath = Join-Path $stabilityDirectory "xiaok-attached-session-$recoveryRunId.control.json"
    $recoveryReportPath = Join-Path $stabilityDirectory "xiaok-attached-session-$recoveryVersion-$recoveryRunId.json"
    $recoveryControl = [ordered]@{
        schemaVersion = 1
        runId = $recoveryRunId
        state = 'running'
        observationMode = 'attached-session'
        hostOwnedByMonitor = $false
        monitorProcessId = $recoveryMonitorProcessId
        monitorStartedUtc = [datetime]::UtcNow.AddMinutes(-20).ToString('o')
        hostProcessId = $recoveryHostProcessId
        hostStartedUtc = [datetime]::UtcNow.AddMinutes(-25).ToString('o')
        reportPath = $recoveryReportPath
        stopEventName = "Local\XiaoKLongRun_$recoveryRunId"
    }
    $recoveryReport = [ordered]@{
        runId = $recoveryRunId
        reportState = 'checkpoint'
        checkpointUtc = [datetime]::UtcNow.AddMinutes(-1).ToString('o')
        packageVersion = $recoveryVersion
        observationMode = 'attached-session'
        hostLaunchedByMonitor = $false
        monitorProcessId = $recoveryMonitorProcessId
        monitorStartedUtc = $recoveryControl.monitorStartedUtc
        processId = $recoveryHostProcessId
        hostStartedUtc = $recoveryControl.hostStartedUtc
        outcome = 'monitoring'
        observationCompleted = $false
        endedUtc = $null
        expectedSampleCount = 1440
        sampleCount = 17
        visibleWindowSamples = 16
        responsiveSamples = 17
        counterFailureCount = 0
    }
    [IO.File]::WriteAllText($recoveryControlPath, ($recoveryControl | ConvertTo-Json -Compress), [Text.UTF8Encoding]::new($false))
    [IO.File]::WriteAllText($recoveryReportPath, ($recoveryReport | ConvertTo-Json -Compress), [Text.UTF8Encoding]::new($false))
    & $recoveryScriptPath -ControlFilePath $recoveryControlPath | Out-Null
    $recoveredReport = Get-Content -LiteralPath $recoveryReportPath -Raw | ConvertFrom-Json
    $recoveredControl = Get-Content -LiteralPath $recoveryControlPath -Raw | ConvertFrom-Json
    Assert-Condition ($recoveredReport.reportState -ceq 'interrupted' -and
        $recoveredReport.outcome -ceq 'monitor_interrupted' -and
        -not $recoveredReport.observationCompleted -and $null -eq $recoveredReport.endedUtc -and
        $recoveredReport.sampleCount -eq 17 -and $recoveredReport.monitorInterrupted) `
        'Recovery did not preserve the incomplete sample count and mark the checkpoint interrupted.'
    Assert-Condition ($recoveredControl.state -ceq 'interrupted' -and
        $recoveredControl.terminalOutcome -ceq 'monitor_interrupted' -and
        $recoveredControl.runId -ceq $recoveryRunId) `
        'Recovery did not atomically record the interrupted control state.'
    $recoveredControlHash = (Get-FileHash -LiteralPath $recoveryControlPath -Algorithm SHA256).Hash
    $recoveredReportHash = (Get-FileHash -LiteralPath $recoveryReportPath -Algorithm SHA256).Hash
    & $recoveryScriptPath -ControlFilePath $recoveryControlPath | Out-Null
    Assert-Condition ((Get-FileHash -LiteralPath $recoveryControlPath -Algorithm SHA256).Hash -ceq $recoveredControlHash -and
        (Get-FileHash -LiteralPath $recoveryReportPath -Algorithm SHA256).Hash -ceq $recoveredReportHash) `
        'Repeated recovery changed an already terminal report or control file.'

    $legacyRecoveryRunId = [guid]::NewGuid().ToString('N')
    $legacyRecoveryControlPath = Join-Path $stabilityDirectory "xiaok-attached-session-$legacyRecoveryRunId.control.json"
    $legacyRecoveryControl = [ordered]@{
        schemaVersion = 1
        runId = $legacyRecoveryRunId
        state = 'running'
        observationMode = 'attached-session'
        hostOwnedByMonitor = $false
        monitorProcessId = $recoveryMonitorProcessId
        monitorStartedUtc = [datetime]::UtcNow.AddMinutes(-30).ToString('o')
        hostProcessId = $recoveryHostProcessId
        hostStartedUtc = [datetime]::UtcNow.AddMinutes(-35).ToString('o')
        stopEventName = "Local\XiaoKLongRun_$legacyRecoveryRunId"
    }
    [IO.File]::WriteAllText($legacyRecoveryControlPath, ($legacyRecoveryControl | ConvertTo-Json -Compress), [Text.UTF8Encoding]::new($false))
    $legacyControlBefore = (Get-FileHash -LiteralPath $legacyRecoveryControlPath -Algorithm SHA256).Hash
    $legacyRecoveryRejected = $false
    try { & $recoveryScriptPath -ControlFilePath $legacyRecoveryControlPath | Out-Null }
    catch { $legacyRecoveryRejected = $true }
    Assert-Condition $legacyRecoveryRejected 'Recovery accepted a legacy run without a checkpoint path.'
    Assert-Condition ((Get-FileHash -LiteralPath $legacyRecoveryControlPath -Algorithm SHA256).Hash -ceq $legacyControlBefore) `
        'Recovery modified a legacy control file that had no checkpoint report.'

    $activeRecoveryRunId = [guid]::NewGuid().ToString('N')
    $activeRecoveryControlPath = Join-Path $stabilityDirectory "xiaok-attached-session-$activeRecoveryRunId.control.json"
    $activeRecoveryReportPath = Join-Path $stabilityDirectory "xiaok-attached-session-$recoveryVersion-$activeRecoveryRunId.json"
    $activeRecoveryControl = [ordered]@{
        schemaVersion = 1
        runId = $activeRecoveryRunId
        state = 'running'
        observationMode = 'attached-session'
        hostOwnedByMonitor = $false
        monitorProcessId = $PID
        monitorStartedUtc = $currentStartedUtc
        hostProcessId = $PID
        hostStartedUtc = $currentStartedUtc
        reportPath = $activeRecoveryReportPath
        stopEventName = "Local\XiaoKLongRun_$activeRecoveryRunId"
    }
    $activeRecoveryReport = [ordered]@{
        runId = $activeRecoveryRunId
        reportState = 'checkpoint'
        checkpointUtc = [datetime]::UtcNow.ToString('o')
        packageVersion = $recoveryVersion
        observationMode = 'attached-session'
        hostLaunchedByMonitor = $false
        monitorProcessId = $PID
        monitorStartedUtc = $currentStartedUtc
        processId = $PID
        hostStartedUtc = $currentStartedUtc
        outcome = 'monitoring'
        observationCompleted = $false
        endedUtc = $null
        expectedSampleCount = 1440
        sampleCount = 1
    }
    [IO.File]::WriteAllText($activeRecoveryControlPath, ($activeRecoveryControl | ConvertTo-Json -Compress), [Text.UTF8Encoding]::new($false))
    [IO.File]::WriteAllText($activeRecoveryReportPath, ($activeRecoveryReport | ConvertTo-Json -Compress), [Text.UTF8Encoding]::new($false))
    $activeControlBefore = Get-FileHash -LiteralPath $activeRecoveryControlPath -Algorithm SHA256
    $activeReportBefore = Get-FileHash -LiteralPath $activeRecoveryReportPath -Algorithm SHA256
    $activeRecoveryRejected = $false
    try { & $recoveryScriptPath -ControlFilePath $activeRecoveryControlPath | Out-Null }
    catch { $activeRecoveryRejected = $true }
    Assert-Condition $activeRecoveryRejected 'Recovery accepted an active monitor process identity.'
    Assert-Condition ((Get-FileHash -LiteralPath $activeRecoveryControlPath -Algorithm SHA256).Hash -ceq $activeControlBefore.Hash -and
        (Get-FileHash -LiteralPath $activeRecoveryReportPath -Algorithm SHA256).Hash -ceq $activeReportBefore.Hash) `
        'Recovery modified files after detecting an active monitor.'
    Assert-Condition ($null -ne (Get-Process -Id $PID -ErrorAction SilentlyContinue)) `
        'Recovery terminated the active test process.'
}
finally {
    $event.Dispose()
    Remove-Item -LiteralPath $controlPath, $requestPath -Force -ErrorAction SilentlyContinue
    if ($null -ne $recoveryControlPath) {
        Remove-Item -LiteralPath $recoveryControlPath, $recoveryReportPath -Force -ErrorAction SilentlyContinue
    }
    if ($null -ne $legacyRecoveryControlPath) {
        Remove-Item -LiteralPath $legacyRecoveryControlPath -Force -ErrorAction SilentlyContinue
    }
    if ($null -ne $activeRecoveryControlPath) {
        Remove-Item -LiteralPath $activeRecoveryControlPath, $activeRecoveryReportPath -Force -ErrorAction SilentlyContinue
    }
}

Write-Output "Long-run cooperative-stop and checkpoint-recovery checks: $script:assertionCount passed."
