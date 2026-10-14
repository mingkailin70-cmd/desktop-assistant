Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

Import-Module (Join-Path $PSScriptRoot 'XiaoKMsixInstallPolicy.psm1') -Force

$hashA = 'A' * 64
$hashB = 'B' * 64
$checks = 0

function Assert-PackageLockBlocksReplacement {
    $tempPath = [System.IO.Path]::GetTempFileName()
    $readLock = $null
    try {
        $readLock = Open-XiaoKMsixPackageReadLock -Path $tempPath
        $writeRejected = $false
        try {
            $writer = [System.IO.File]::Open(
                $tempPath,
                [System.IO.FileMode]::Open,
                [System.IO.FileAccess]::Write,
                [System.IO.FileShare]::None)
            $writer.Dispose()
        } catch [System.IO.IOException] {
            $writeRejected = $true
        }
        if (-not $writeRejected) {
            throw 'The package read lock allowed an in-place write.'
        }

        $renameRejected = $false
        try {
            [System.IO.File]::Move($tempPath, "$tempPath.moved")
        } catch [System.IO.IOException] {
            $renameRejected = $true
        } catch [System.UnauthorizedAccessException] {
            $renameRejected = $true
        }
        if (-not $renameRejected) {
            throw 'The package read lock allowed the locked path to be renamed.'
        }
        $script:checks++

        $readLock.Dispose()
        $readLock = $null
        $reopened = [System.IO.File]::Open(
            $tempPath,
            [System.IO.FileMode]::Open,
            [System.IO.FileAccess]::Write,
            [System.IO.FileShare]::None)
        try { $reopened.Flush() } finally { $reopened.Dispose() }
        $script:checks++
    } finally {
        if ($null -ne $readLock) { $readLock.Dispose() }
        if (Test-Path -LiteralPath $tempPath) { Remove-Item -LiteralPath $tempPath -Force }
        if (Test-Path -LiteralPath "$tempPath.moved") { Remove-Item -LiteralPath "$tempPath.moved" -Force }
    }
}

function Assert-Policy([string]$Name, [scriptblock]$Action, [bool]$ExpectedForceUpdate) {
    $result = & $Action
    if ([bool]$result.UseForceUpdateFromAnyVersion -ne $ExpectedForceUpdate) {
        throw "Policy check '$Name' returned an unexpected ForceUpdateFromAnyVersion value."
    }
    $script:checks++
}

function Assert-PolicyRejects([string]$Name, [scriptblock]$Action) {
    $rejected = $false
    try {
        $null = & $Action
    } catch {
        $rejected = $true
    }
    if (-not $rejected) {
        throw "Policy check '$Name' should have failed closed."
    }
    $script:checks++
}

foreach ($case in @(
    @{ Name = 'same Windows session'; Process = 3; Current = 3; Expected = $true; Reject = $false },
    @{ Name = 'different Windows session'; Process = 4; Current = 3; Expected = $false; Reject = $false },
    @{ Name = 'negative process session'; Process = -1; Current = 3; Reject = $true },
    @{ Name = 'negative current session'; Process = 3; Current = -1; Reject = $true }
)) {
    $rejected = $false
    try {
        $eligible = Test-XiaoKMsixHostProcessSession -ProcessSessionId $case.Process -CurrentSessionId $case.Current
    } catch {
        $rejected = $true
    }
    if ([bool]$case.Reject) {
        if (-not $rejected) { throw "Session eligibility check '$($case.Name)' should have failed closed." }
    } elseif ($rejected -or [bool]$eligible -ne [bool]$case.Expected) {
        throw "Session eligibility check '$($case.Name)' returned an unexpected result."
    }
    $checks++
}

$installerSource = Get-Content (Join-Path $PSScriptRoot 'install_xiaok_msix.ps1') -Raw
$sessionChecks = @(
    '$currentSessionId = [System.Diagnostics.Process]::GetCurrentProcess().SessionId',
    '$hostProcess.SessionId',
    'Test-XiaoKMsixHostProcessSession',
    '$matchingHostProcesses += $hostProcess'
) | ForEach-Object { $installerSource.IndexOf($_, [System.StringComparison]::Ordinal) }
if (($sessionChecks | Where-Object { $_ -lt 0 }).Count -gt 0 -or $sessionChecks[0] -gt $sessionChecks[1] -or $sessionChecks[1] -gt $sessionChecks[2] -or $sessionChecks[2] -gt $sessionChecks[3]) {
    throw 'The installer must verify the XiaoK process session before allowing it into the shutdown candidate list.'
}
$checks++

Assert-Policy 'first install' {
    Resolve-XiaoKMsixInstallPolicy -InstallRequested -ActualSha256 $hashA -TargetVersion '0.1.164.0'
} $false

Assert-Policy 'normal upgrade' {
    Resolve-XiaoKMsixInstallPolicy -InstallRequested -ActualSha256 $hashA -TargetVersion '0.1.165.0' -InstalledVersions @('0.1.164.0')
} $false

Assert-Policy 'pinned rollback' {
    Resolve-XiaoKMsixInstallPolicy -InstallRequested -RollbackRequested -ExpectedSha256 $hashA -ActualSha256 $hashA -TargetVersion '0.1.159.0' -InstalledVersions @('0.1.164.0')
} $true

Assert-PolicyRejects 'rollback without install' {
    Resolve-XiaoKMsixInstallPolicy -RollbackRequested -ExpectedSha256 $hashA -ActualSha256 $hashA -TargetVersion '0.1.159.0' -InstalledVersions @('0.1.164.0')
}

Assert-PolicyRejects 'ordinary downgrade' {
    Resolve-XiaoKMsixInstallPolicy -InstallRequested -ActualSha256 $hashA -TargetVersion '0.1.159.0' -InstalledVersions @('0.1.164.0')
}

Assert-PolicyRejects 'rollback without package hash pin' {
    Resolve-XiaoKMsixInstallPolicy -InstallRequested -RollbackRequested -ActualSha256 $hashA -TargetVersion '0.1.159.0' -InstalledVersions @('0.1.164.0')
}

Assert-PolicyRejects 'mismatched package hash' {
    Resolve-XiaoKMsixInstallPolicy -InstallRequested -RollbackRequested -ExpectedSha256 $hashA -ActualSha256 $hashB -TargetVersion '0.1.159.0' -InstalledVersions @('0.1.164.0')
}

Assert-PolicyRejects 'malformed expected hash' {
    Resolve-XiaoKMsixInstallPolicy -InstallRequested -RollbackRequested -ExpectedSha256 'not-a-hash' -ActualSha256 $hashA -TargetVersion '0.1.159.0' -InstalledVersions @('0.1.164.0')
}

Assert-PolicyRejects 'rollback with no installed package' {
    Resolve-XiaoKMsixInstallPolicy -InstallRequested -RollbackRequested -ExpectedSha256 $hashA -ActualSha256 $hashA -TargetVersion '0.1.159.0'
}

Assert-PolicyRejects 'rollback with ambiguous installed packages' {
    Resolve-XiaoKMsixInstallPolicy -InstallRequested -RollbackRequested -ExpectedSha256 $hashA -ActualSha256 $hashA -TargetVersion '0.1.159.0' -InstalledVersions @('0.1.163.0', '0.1.164.0')
}

Assert-PolicyRejects 'rollback target equal to installed version' {
    Resolve-XiaoKMsixInstallPolicy -InstallRequested -RollbackRequested -ExpectedSha256 $hashA -ActualSha256 $hashA -TargetVersion '0.1.164.0' -InstalledVersions @('0.1.164.0')
}

Assert-PolicyRejects 'rollback target higher than installed version' {
    Resolve-XiaoKMsixInstallPolicy -InstallRequested -RollbackRequested -ExpectedSha256 $hashA -ActualSha256 $hashA -TargetVersion '0.1.165.0' -InstalledVersions @('0.1.164.0')
}

Assert-PolicyRejects 'invalid installed version' {
    Resolve-XiaoKMsixInstallPolicy -InstallRequested -ActualSha256 $hashA -TargetVersion '0.1.165.0' -InstalledVersions @('unknown')
}

Assert-PackageLockBlocksReplacement

Write-Output "通过：MSIX升级/回滚准入、文件锁与Windows会话隔离检查 $checks 项；回滚固定包散列、目标版本和唯一当前版本，校验与安装期间锁定同一MSIX文件，且安装器只向当前Windows会话中的旧Host发送关闭请求。"
