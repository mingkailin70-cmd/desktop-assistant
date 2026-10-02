[CmdletBinding(SupportsShouldProcess = $true, ConfirmImpact = 'High')]
param(
    [switch]$RemoveTrustedCertificate
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$packageName = 'MingKaiLin.XiaoK'
$publisher = 'CN=XiaoK Local Development'
$matchingPackages = @(Get-AppxPackage -Name $packageName | Where-Object { $_.Publisher -ceq $publisher })
if ($matchingPackages.Count -gt 1) {
    throw 'More than one XiaoK package version is registered for this account. Review package state manually before removal.'
}

if ($matchingPackages.Count -eq 1) {
    $installedPackage = $matchingPackages[0]
    if ($PSCmdlet.ShouldProcess($installedPackage.PackageFullName, 'Remove XiaoK for the current user')) {
        Remove-AppxPackage -Package $installedPackage.PackageFullName
        $remainingCurrentUserPackages = @(Get-AppxPackage -Name $packageName | Where-Object { $_.Publisher -ceq $publisher })
        if ($remainingCurrentUserPackages.Count -gt 0) {
            throw 'XiaoK remains registered for the current user after Remove-AppxPackage. Keep the trust certificate and inspect Windows deployment state.'
        }
        Write-Output 'XiaoK was removed for the current user.'
    } else {
        Write-Output 'XiaoK package removal was not performed.'
    }
} else {
    Write-Output 'XiaoK is not installed for the current user.'
}

if (-not $RemoveTrustedCertificate) {
    Write-Output 'The machine trust certificate was retained. Use -RemoveTrustedCertificate only when no installed packages rely on this publisher.'
    return
}

$principal = New-Object -TypeName Security.Principal.WindowsPrincipal -ArgumentList ([Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'Removing the LocalMachine trust certificate requires an elevated PowerShell session.'
}

$remainingCurrentUserPackages = @(Get-AppxPackage -Name $packageName | Where-Object { $_.Publisher -ceq $publisher })
if ($remainingCurrentUserPackages.Count -gt 0) {
    throw 'The machine trust certificate will be retained because XiaoK remains installed for the current user.'
}

$remainingPublisherPackages = @(Get-AppxPackage -AllUsers | Where-Object { $_.Publisher -ceq $publisher })
if ($remainingPublisherPackages.Count -gt 0) {
    $remainingNames = ($remainingPublisherPackages | Select-Object -ExpandProperty PackageFullName) -join ', '
    throw "The machine trust certificate will be retained because packages from this publisher remain installed: $remainingNames"
}

$repositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$metadataPath = Join-Path $repositoryRoot 'artifacts\signing\xiaok-development-certificate.json'
if (-not (Test-Path -LiteralPath $metadataPath -PathType Leaf)) {
    throw 'Local development certificate metadata is missing; refusing to search or remove certificates by subject alone.'
}
$metadata = Get-Content -LiteralPath $metadataPath -Raw | ConvertFrom-Json
$thumbprint = ([string]$metadata.thumbprint -replace '\s', '').ToUpperInvariant()
if ($thumbprint -notmatch '^[0-9A-F]{40}$') {
    throw 'Local development certificate metadata contains an invalid thumbprint.'
}

$trustedCertificate = Get-Item -LiteralPath "Cert:\LocalMachine\TrustedPeople\$thumbprint" -ErrorAction SilentlyContinue
if ($null -eq $trustedCertificate) {
    Write-Output 'The XiaoK development certificate is not in LocalMachine\TrustedPeople.'
    return
}
if ($trustedCertificate.Subject -cne $publisher) {
    throw 'The certificate thumbprint resolved to an unexpected subject; no certificate was removed.'
}

if ($PSCmdlet.ShouldProcess("Cert:\LocalMachine\TrustedPeople\$thumbprint", 'Remove XiaoK local development trust')) {
    Remove-Item -LiteralPath "Cert:\LocalMachine\TrustedPeople\$thumbprint"
    Write-Output 'The XiaoK development certificate was removed from LocalMachine\TrustedPeople.'
}

Write-Output 'The private certificate remains in CurrentUser\My for future development signing.'
Write-Output 'This script does not remove D:\XiaoK user data, model files, or the private signing key.'
