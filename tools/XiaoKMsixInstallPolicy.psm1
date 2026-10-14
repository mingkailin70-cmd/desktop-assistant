Set-StrictMode -Version Latest

function Open-XiaoKMsixPackageReadLock {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path
    )

    return [System.IO.File]::Open(
        $Path,
        [System.IO.FileMode]::Open,
        [System.IO.FileAccess]::Read,
        [System.IO.FileShare]::Read)
}

function Resolve-XiaoKMsixInstallPolicy {
    [CmdletBinding()]
    param(
        [switch]$InstallRequested,
        [switch]$RollbackRequested,
        [AllowNull()]
        [string]$ExpectedSha256,
        [Parameter(Mandatory = $true)]
        [string]$ActualSha256,
        [Parameter(Mandatory = $true)]
        [string]$TargetVersion,
        [AllowEmptyCollection()]
        [string[]]$InstalledVersions = @()
    )

    if ($RollbackRequested -and -not $InstallRequested) {
        throw 'Rollback requires -Install.'
    }

    if ($ActualSha256 -notmatch '^[0-9A-Fa-f]{64}$') {
        throw 'The package SHA-256 must be a 64-character hexadecimal value.'
    }

    if (-not [string]::IsNullOrWhiteSpace($ExpectedSha256)) {
        if ($ExpectedSha256 -notmatch '^[0-9A-Fa-f]{64}$') {
            throw 'ExpectedSha256 must be a 64-character hexadecimal value.'
        }
        if (-not [string]::Equals($ExpectedSha256, $ActualSha256, [System.StringComparison]::OrdinalIgnoreCase)) {
            throw 'The package SHA-256 does not match ExpectedSha256.'
        }
    }

    if ($RollbackRequested -and [string]::IsNullOrWhiteSpace($ExpectedSha256)) {
        throw 'A rollback requires -ExpectedSha256 to pin the exact retained package.'
    }

    $target = $null
    if (-not [version]::TryParse($TargetVersion, [ref]$target)) {
        throw 'The package manifest contains an invalid version.'
    }

    $parsedInstalledVersions = @()
    foreach ($installedVersionText in $InstalledVersions) {
        $installedVersion = $null
        if (-not [version]::TryParse($installedVersionText, [ref]$installedVersion)) {
            throw 'An installed XiaoK package has an invalid version; refusing installation.'
        }
        $parsedInstalledVersions += $installedVersion
    }

    if ($RollbackRequested) {
        if ($parsedInstalledVersions.Count -ne 1) {
            throw 'A rollback requires exactly one currently installed XiaoK package for this user.'
        }

        $currentVersion = $parsedInstalledVersions[0]
        if ($target -ge $currentVersion) {
            throw "Rollback target $target must be lower than the installed version $currentVersion."
        }

        return [pscustomobject]@{
            UseForceUpdateFromAnyVersion = $true
            CurrentVersion = $currentVersion
            TargetVersion = $target
        }
    }

    if ($parsedInstalledVersions.Count -gt 0) {
        $highestInstalledVersion = $parsedInstalledVersions | Sort-Object -Descending | Select-Object -First 1
        if ($target -lt $highestInstalledVersion) {
            throw "Refusing to downgrade from $highestInstalledVersion to $target without explicit -Rollback and -ExpectedSha256."
        }
    }

    $currentVersion = $null
    if ($parsedInstalledVersions.Count -gt 0) {
        $currentVersion = $parsedInstalledVersions | Sort-Object -Descending | Select-Object -First 1
    }

    return [pscustomobject]@{
        UseForceUpdateFromAnyVersion = $false
        CurrentVersion = $currentVersion
        TargetVersion = $target
    }
}

Export-ModuleMember -Function Open-XiaoKMsixPackageReadLock, Resolve-XiaoKMsixInstallPolicy
