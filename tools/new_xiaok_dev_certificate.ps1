[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$manifestPath = Join-Path $repositoryRoot 'src\XiaoK.Host\Package.appxmanifest'
$manifest = [xml](Get-Content -LiteralPath $manifestPath -Raw)
$publisher = [string]$manifest.Package.Identity.Publisher
$expectedPublisher = 'CN=XiaoK Local Development'
if ($publisher -cne $expectedPublisher) {
    throw "Manifest Publisher must be '$expectedPublisher'; found '$publisher'."
}

$signingDirectory = Join-Path $repositoryRoot 'artifacts\signing'
$metadataPath = Join-Path $signingDirectory 'xiaok-development-certificate.json'
$publicCertificatePath = Join-Path $signingDirectory 'xiaok-development-certificate.cer'
New-Item -ItemType Directory -Path $signingDirectory -Force | Out-Null

if (Test-Path -LiteralPath $metadataPath -PathType Leaf) {
    $metadata = Get-Content -LiteralPath $metadataPath -Raw | ConvertFrom-Json
    $thumbprint = ([string]$metadata.thumbprint -replace '\s', '').ToUpperInvariant()
    $certificate = Get-Item -LiteralPath "Cert:\CurrentUser\My\$thumbprint" -ErrorAction SilentlyContinue
    if ($null -eq $certificate -or -not $certificate.HasPrivateKey -or $certificate.Subject -cne $expectedPublisher) {
        throw "Local certificate metadata exists but its matching private certificate is unavailable. Review $metadataPath before creating a replacement."
    }
    if (-not (Test-Path -LiteralPath $publicCertificatePath -PathType Leaf)) {
        Export-Certificate -Cert $certificate -FilePath $publicCertificatePath -Type CERT -Force | Out-Null
    }
    Write-Output "Reusing local development certificate: $($certificate.Thumbprint)"
    Write-Output "Public certificate: $publicCertificatePath"
    Write-Output 'The private key remains in CurrentUser\My and is not exported.'
    return
}

$sameSubjectCertificates = @(Get-ChildItem Cert:\CurrentUser\My | Where-Object { $_.Subject -ceq $expectedPublisher })
if ($sameSubjectCertificates.Count -gt 0) {
    throw 'A certificate with this publisher already exists but has no XiaoK local metadata. Inspect it manually; the script will not adopt or overwrite it.'
}

$certificate = New-SelfSignedCertificate `
    -Type Custom `
    -Subject $expectedPublisher `
    -FriendlyName 'XiaoK Local Development Signing' `
    -CertStoreLocation 'Cert:\CurrentUser\My' `
    -KeyAlgorithm RSA `
    -KeyLength 3072 `
    -HashAlgorithm SHA256 `
    -KeyUsage DigitalSignature `
    -KeyExportPolicy NonExportable `
    -NotAfter (Get-Date).AddYears(3) `
    -TextExtension @('2.5.29.37={text}1.3.6.1.5.5.7.3.3', '2.5.29.19={text}')

Export-Certificate -Cert $certificate -FilePath $publicCertificatePath -Type CERT -Force | Out-Null
$metadata = [ordered]@{
    schemaVersion = 1
    subject = $certificate.Subject
    thumbprint = $certificate.Thumbprint
    notAfter = $certificate.NotAfter.ToUniversalTime().ToString('o')
    publicCertificateFile = 'xiaok-development-certificate.cer'
    privateKeyStore = 'Cert:\CurrentUser\My'
    privateKeyExported = $false
}
$metadata | ConvertTo-Json | Set-Content -LiteralPath $metadataPath -Encoding UTF8

Write-Output "Created local development certificate: $($certificate.Thumbprint)"
Write-Output "Subject: $($certificate.Subject)"
Write-Output "Expires: $($certificate.NotAfter.ToString('u'))"
Write-Output "Public certificate: $publicCertificatePath"
Write-Output 'The private key is non-exportable and remains in CurrentUser\My. The certificate was not added to a trusted store.'
