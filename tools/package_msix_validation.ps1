[CmdletBinding()]
param(
    [string]$PublishDirectory = 'artifacts\publish\win-x64',
    [string]$WindowsSdkVersion = '10.0.26100.0'
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
$assetsPath = Join-Path $repositoryRoot 'src\XiaoK.Host\Assets'
foreach ($assetName in @('StoreLogo.png', 'Square150x150Logo.png', 'Square44x44Logo.png')) {
    if (-not (Test-Path -LiteralPath (Join-Path $assetsPath $assetName) -PathType Leaf)) {
        throw "Required MSIX logo asset is missing: $assetName"
    }
}

$validationRoot = Join-Path $artifactRoot (Join-Path 'msix-validation' ([guid]::NewGuid().ToString('N')))
$layoutPath = Join-Path $validationRoot 'layout'
$packagePath = Join-Path $validationRoot 'XiaoK-unsigned-validation.msix'
New-Item -ItemType Directory -Path $layoutPath -Force | Out-Null
Copy-Item -Path (Join-Path $publishPath '*') -Destination $layoutPath -Recurse
New-Item -ItemType Directory -Path (Join-Path $layoutPath 'Assets') -Force | Out-Null
Copy-Item -Path (Join-Path $assetsPath '*') -Destination (Join-Path $layoutPath 'Assets')
Copy-Item -LiteralPath $manifestPath -Destination (Join-Path $layoutPath 'AppxManifest.xml')

& $makeAppxPath pack /v /h SHA256 /d $layoutPath /p $packagePath
if ($LASTEXITCODE -ne 0) {
    throw "MakeAppx failed with exit code $LASTEXITCODE. Review the preserved validation layout: $validationRoot"
}

$package = Get-Item -LiteralPath $packagePath
Write-Output "Unsigned validation package: $($package.FullName)"
Write-Output "Package bytes: $($package.Length)"
Write-Output 'This script does not sign, install, or launch the package.'
