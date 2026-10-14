Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

Import-Module (Join-Path $PSScriptRoot 'XiaoKMsixInstallPolicy.psm1') -Force

$hashA = 'A' * 64
$hashB = 'B' * 64
$checks = 0

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

Write-Output "通过：MSIX升级/回滚准入策略 $checks 项；回滚必须固定包散列、目标版本及唯一当前安装版本，并使用显式模式。"
