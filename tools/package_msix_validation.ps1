[CmdletBinding()]
param(
    [string]$PublishDirectory = 'artifacts\publish\win-x64',
    [string]$WindowsSdkVersion = '10.0.26100.0',
    [string]$CertificateThumbprint = ''
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$artifactRoot = [System.IO.Path]::GetFullPath((Join-Path $repositoryRoot 'artifacts'))

if ([System.IO.Path]::IsPathRooted($PublishDirectory) -or $PublishDirectory -match '^[A-Za-z]:') {
    throw 'PublishDirectory must be a repository-relative path under src\XiaoK.Host\bin\Release or artifacts\publish.'
}

$publishPath = [System.IO.Path]::GetFullPath((Join-Path $repositoryRoot $PublishDirectory))
$allowedPublishRoots = @(
    [System.IO.Path]::GetFullPath((Join-Path $repositoryRoot 'src\XiaoK.Host\bin\Release')),
    [System.IO.Path]::GetFullPath((Join-Path $artifactRoot 'publish'))
)
$isAllowedSource = $false
foreach ($allowedRoot in $allowedPublishRoots) {
    $allowedPrefix = $allowedRoot.TrimEnd([System.IO.Path]::DirectorySeparatorChar) + [System.IO.Path]::DirectorySeparatorChar
    if ($publishPath.StartsWith($allowedPrefix, [System.StringComparison]::OrdinalIgnoreCase)) {
        $isAllowedSource = $true
        break
    }
}
if (-not $isAllowedSource) {
    throw 'PublishDirectory is outside the permitted Release/publish output folders.'
}
if (-not (Test-Path -LiteralPath $publishPath -PathType Container)) {
    throw "PublishDirectory does not exist: $publishPath"
}

$requiredFiles = @('XiaoK.Host.exe', 'XiaoK.Host.dll', 'XiaoK.Host.deps.json', 'XiaoK.Host.runtimeconfig.json')
foreach ($fileName in $requiredFiles) {
    if (-not (Test-Path -LiteralPath (Join-Path $publishPath $fileName) -PathType Leaf)) {
        throw "Required Host publish file is missing: $fileName"
    }
}

$sourceItems = @(Get-ChildItem -LiteralPath $publishPath -Force -Recurse)
if ($sourceItems | Where-Object { $_.Attributes -band [System.IO.FileAttributes]::ReparsePoint }) {
    throw 'PublishDirectory contains a reparse point; refusing to package it.'
}

$makeAppxPath = Join-Path ${env:ProgramFiles(x86)} "Windows Kits\10\bin\$WindowsSdkVersion\x64\MakeAppx.exe"
if (-not (Test-Path -LiteralPath $makeAppxPath -PathType Leaf)) {
    throw "MakeAppx.exe not found for Windows SDK $WindowsSdkVersion. No tool was downloaded."
}

$manifestPath = Join-Path $repositoryRoot 'src\XiaoK.Host\Package.appxmanifest'
$manifest = [xml](Get-Content -LiteralPath $manifestPath -Raw)
$publisher = [string]$manifest.Package.Identity.Publisher
if ([string]::IsNullOrWhiteSpace($publisher) -or $publisher -match 'TODO|PLACEHOLDER') {
    throw 'Package.appxmanifest must use the configured local publisher before packaging.'
}
$assetsPath = Join-Path $repositoryRoot 'src\XiaoK.Host\Assets'
foreach ($assetName in @('StoreLogo.png', 'Square150x150Logo.png', 'Square44x44Logo.png')) {
    if (-not (Test-Path -LiteralPath (Join-Path $assetsPath $assetName) -PathType Leaf)) {
        throw "Required MSIX logo asset is missing: $assetName"
    }
}

$validationRoot = Join-Path $artifactRoot (Join-Path 'msix-validation' ([guid]::NewGuid().ToString('N')))
$layoutPath = Join-Path $validationRoot 'layout'
$packageName = if ([string]::IsNullOrWhiteSpace($CertificateThumbprint)) { 'XiaoK-unsigned-validation.msix' } else { 'XiaoK-signed-validation.msix' }
$packagePath = Join-Path $validationRoot $packageName
New-Item -ItemType Directory -Path $layoutPath -Force | Out-Null
Copy-Item -Path (Join-Path $publishPath '*') -Destination $layoutPath -Recurse
New-Item -ItemType Directory -Path (Join-Path $layoutPath 'Assets') -Force | Out-Null
Copy-Item -Path (Join-Path $assetsPath '*') -Destination (Join-Path $layoutPath 'Assets')
Copy-Item -LiteralPath $manifestPath -Destination (Join-Path $layoutPath 'AppxManifest.xml')

& $makeAppxPath pack /v /h SHA256 /d $layoutPath /p $packagePath
if ($LASTEXITCODE -ne 0) {
    throw "MakeAppx failed with exit code $LASTEXITCODE. Review the preserved validation layout: $validationRoot"
}

if (-not [string]::IsNullOrWhiteSpace($CertificateThumbprint)) {
    $thumbprint = ($CertificateThumbprint -replace '\s', '').ToUpperInvariant()
    if ($thumbprint -notmatch '^[0-9A-F]{40}$') {
        throw 'CertificateThumbprint must be the 40-character SHA-1 thumbprint of the local development certificate.'
    }

    $certificate = Get-Item -LiteralPath "Cert:\CurrentUser\My\$thumbprint" -ErrorAction Stop
    if (-not $certificate.HasPrivateKey -or $certificate.Subject -cne $publisher -or $certificate.NotBefore -gt (Get-Date) -or $certificate.NotAfter -le (Get-Date)) {
        throw 'The selected certificate must be current, include its private key, and exactly match the manifest Publisher.'
    }

    $hasCodeSigningEku = $false
    $hasDigitalSignatureUsage = $false
    foreach ($extension in $certificate.Extensions) {
        if ($extension -is [System.Security.Cryptography.X509Certificates.X509EnhancedKeyUsageExtension]) {
            $hasCodeSigningEku = @($extension.EnhancedKeyUsages | Where-Object { $_.Value -eq '1.3.6.1.5.5.7.3.3' }).Count -gt 0
        }
        if ($extension -is [System.Security.Cryptography.X509Certificates.X509KeyUsageExtension]) {
            $digitalSignatureFlag = [System.Security.Cryptography.X509Certificates.X509KeyUsageFlags]::DigitalSignature
            $hasDigitalSignatureUsage = ([int]$extension.KeyUsages -band [int]$digitalSignatureFlag) -ne 0
        }
    }
    if (-not $hasCodeSigningEku -or -not $hasDigitalSignatureUsage) {
        throw 'The selected certificate must include the Code Signing EKU and Digital Signature key usage.'
    }

    $signToolPath = Join-Path ${env:ProgramFiles(x86)} "Windows Kits\10\bin\$WindowsSdkVersion\x64\signtool.exe"
    if (-not (Test-Path -LiteralPath $signToolPath -PathType Leaf)) {
        throw "SignTool.exe not found for Windows SDK $WindowsSdkVersion. No tool was downloaded."
    }
    & $signToolPath sign /fd SHA256 /sha1 $thumbprint $packagePath
    if ($LASTEXITCODE -ne 0) {
        throw "SignTool failed with exit code $LASTEXITCODE. The package remains in $validationRoot"
    }
    Write-Output "Package signed with CurrentUser certificate $thumbprint. Certificate trust and installation were not changed."
}

$package = Get-Item -LiteralPath $packagePath
Write-Output "Validation package: $($package.FullName)"
Write-Output "Package bytes: $($package.Length)"
if (-not [string]::IsNullOrWhiteSpace($CertificateThumbprint)) {
    Write-Output 'The package was signed but not installed, launched, or added to a trusted certificate store.'
} else {
    Write-Output 'This script does not sign, install, or launch the package.'
}
