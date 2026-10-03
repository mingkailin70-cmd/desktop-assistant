[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$PackagePath,
    [switch]$Install
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$artifactRoot = [System.IO.Path]::GetFullPath((Join-Path $repositoryRoot 'artifacts'))
$package = Get-Item -LiteralPath $PackagePath -ErrorAction Stop
$packagePath = [System.IO.Path]::GetFullPath($package.FullName)
$allowedRoots = @(
    [System.IO.Path]::GetFullPath((Join-Path $artifactRoot 'msix-validation')),
    [System.IO.Path]::GetFullPath((Join-Path $artifactRoot 'packages'))
)
$packageAllowed = $false
foreach ($allowedRoot in $allowedRoots) {
    $prefix = $allowedRoot.TrimEnd([System.IO.Path]::DirectorySeparatorChar) + [System.IO.Path]::DirectorySeparatorChar
    if ($packagePath.StartsWith($prefix, [System.StringComparison]::OrdinalIgnoreCase)) {
        $packageAllowed = $true
        break
    }
}
if (-not $packageAllowed -or (($package.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0)) {
    throw 'PackagePath must be a regular file under the repository artifacts\msix-validation or artifacts\packages directory.'
}

$signingDirectory = Join-Path $artifactRoot 'signing'
$metadataPath = Join-Path $signingDirectory 'xiaok-development-certificate.json'
$publicCertificatePath = Join-Path $signingDirectory 'xiaok-development-certificate.cer'
if (-not (Test-Path -LiteralPath $metadataPath -PathType Leaf) -or -not (Test-Path -LiteralPath $publicCertificatePath -PathType Leaf)) {
    throw 'Local development certificate metadata or public certificate is missing. Run tools\new_xiaok_dev_certificate.ps1 first.'
}
$metadata = Get-Content -LiteralPath $metadataPath -Raw | ConvertFrom-Json
$certificate = New-Object -TypeName System.Security.Cryptography.X509Certificates.X509Certificate2 -ArgumentList $publicCertificatePath
$expectedThumbprint = ([string]$metadata.thumbprint -replace '\s', '').ToUpperInvariant()
if ($expectedThumbprint -notmatch '^[0-9A-F]{40}$' -or $certificate.Thumbprint -cne $expectedThumbprint) {
    throw 'The public certificate does not match the locally recorded development certificate thumbprint.'
}
if ($certificate.Subject -cne 'CN=XiaoK Local Development') {
    throw "Unexpected development certificate subject: $($certificate.Subject)"
}

Add-Type -AssemblyName System.IO.Compression.FileSystem
$archive = [System.IO.Compression.ZipFile]::OpenRead($packagePath)
try {
    $manifestEntry = $archive.GetEntry('AppxManifest.xml')
    if ($null -eq $manifestEntry) { throw 'The MSIX package does not contain AppxManifest.xml.' }
    $reader = [System.IO.StreamReader]::new($manifestEntry.Open())
    try { $packageManifest = [xml]$reader.ReadToEnd() } finally { $reader.Dispose() }
} finally {
    $archive.Dispose()
}
$packageIdentity = $packageManifest.Package.Identity
$manifestPublisher = [string]$packageIdentity.Publisher
$manifestName = [string]$packageIdentity.Name
if ($manifestPublisher -cne $certificate.Subject -or $manifestName -cne 'MingKaiLin.XiaoK') {
    throw 'The package identity does not match the expected XiaoK development publisher and package name.'
}

$signature = Get-AuthenticodeSignature -LiteralPath $packagePath
if ($null -eq $signature.SignerCertificate -or $signature.SignerCertificate.Thumbprint -cne $certificate.Thumbprint) {
    throw 'The package signer is missing or does not match the pinned local public certificate.'
}
if ($signature.Status.ToString() -notin @('Valid', 'UnknownError')) {
    throw "Package signature status is $($signature.Status): $($signature.StatusMessage)"
}

$packageHash = (Get-FileHash -LiteralPath $packagePath -Algorithm SHA256).Hash
$certificateHash = (Get-FileHash -LiteralPath $publicCertificatePath -Algorithm SHA256).Hash
$trustedCertificate = Get-ChildItem Cert:\LocalMachine\TrustedPeople | Where-Object { $_.Thumbprint -ceq $certificate.Thumbprint } | Select-Object -First 1

Write-Output "Package: $packagePath"
Write-Output "Package SHA-256: $packageHash"
Write-Output "Publisher: $manifestPublisher"
Write-Output "Signer subject: $($certificate.Subject)"
Write-Output "Signer SHA-1 thumbprint: $($certificate.Thumbprint)"
Write-Output "Public certificate SHA-256: $certificateHash"
Write-Output "Signature status before trust: $($signature.Status) ($($signature.StatusMessage))"
Write-Output "Trusted People store: $(if ($null -eq $trustedCertificate) { 'certificate not present' } else { 'certificate already present' })"

if (-not $Install) {
    Write-Output 'Preflight only. No certificate trust, installation, or app launch was changed.'
    return
}

$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = New-Object -TypeName Security.Principal.WindowsPrincipal -ArgumentList $identity
if ($null -eq $trustedCertificate -and -not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'The development certificate is not in LocalMachine\TrustedPeople. Run an elevated PowerShell once to add machine-level trust, or have an administrator import the public certificate.'
}

$addedTrustedCertificate = $false
if ($null -eq $trustedCertificate) {
    Import-Certificate -FilePath $publicCertificatePath -CertStoreLocation Cert:\LocalMachine\TrustedPeople | Out-Null
    $addedTrustedCertificate = $true
}

$signToolPath = Join-Path ${env:ProgramFiles(x86)} 'Windows Kits\10\bin\10.0.26100.0\x64\signtool.exe'
if (-not (Test-Path -LiteralPath $signToolPath -PathType Leaf)) {
    if ($addedTrustedCertificate) { Remove-Item -LiteralPath "Cert:\LocalMachine\TrustedPeople\$($certificate.Thumbprint)" }
    throw "SignTool.exe not found: $signToolPath"
}
& $signToolPath verify /pa /v $packagePath
if ($LASTEXITCODE -ne 0) {
    if ($addedTrustedCertificate) { Remove-Item -LiteralPath "Cert:\LocalMachine\TrustedPeople\$($certificate.Thumbprint)" }
    throw "Signature verification failed with exit code $LASTEXITCODE. A newly added trust entry has been removed."
}

try {
    Add-AppxPackage -Path $packagePath
} catch {
    if ($addedTrustedCertificate) {
        Remove-Item -LiteralPath "Cert:\LocalMachine\TrustedPeople\$($certificate.Thumbprint)" -ErrorAction SilentlyContinue
    }
    throw
}

$installedPackage = Get-AppxPackage -Name $manifestName | Where-Object { $_.Publisher -ceq $manifestPublisher } | Select-Object -First 1
if ($null -eq $installedPackage) {
    throw 'Add-AppxPackage returned without an error, but the XiaoK package could not be found for the current user. Inspect Windows deployment logs before retrying.'
}
Write-Output "Installed for current user: $($installedPackage.PackageFullName)"
$trustResult = if ($addedTrustedCertificate) { 'The certificate was added to LocalMachine\TrustedPeople.' } else { 'The certificate was already trusted in LocalMachine\TrustedPeople.' }
Write-Output "The app was not launched. $trustResult See the rollback commands in docs\开发与发布\MSIX打包说明.md."
