[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$dotnetPath = Join-Path $repositoryRoot '.tools\dotnet\dotnet.exe'
$projectPath = Join-Path $repositoryRoot 'src\XiaoK.Host\XiaoK.Host.csproj'
$publishRoot = [System.IO.Path]::GetFullPath((Join-Path $repositoryRoot 'artifacts\publish'))
$publishPath = [System.IO.Path]::GetFullPath((Join-Path $publishRoot 'win-x64'))
$stagingName = '.win-x64-staging-' + [guid]::NewGuid().ToString('N')
$stagingPath = [System.IO.Path]::GetFullPath((Join-Path $publishRoot $stagingName))
$backupName = '.win-x64-backup-' + [guid]::NewGuid().ToString('N')
$backupPath = [System.IO.Path]::GetFullPath((Join-Path $publishRoot $backupName))
$publishProfile = 'Windows-x64-self-contained'

$publishPrefix = $publishRoot.TrimEnd([System.IO.Path]::DirectorySeparatorChar) + [System.IO.Path]::DirectorySeparatorChar
foreach ($outputPath in @($publishPath, $stagingPath, $backupPath)) {
    if (-not $outputPath.StartsWith($publishPrefix, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw 'Publish output is outside the repository artifacts\publish directory.'
    }
}

if (-not (Test-Path -LiteralPath $dotnetPath -PathType Leaf)) {
    throw "Fixed repository SDK not found: $dotnetPath"
}
$sdkVersion = (& $dotnetPath --version).Trim()
if ($LASTEXITCODE -ne 0 -or $sdkVersion -ne '10.0.401') {
    throw "Expected repository SDK 10.0.401; found '$sdkVersion'."
}

& $dotnetPath restore $projectPath --runtime win-x64 --locked-mode "-p:PublishProfile=$publishProfile"
if ($LASTEXITCODE -ne 0) { throw "Locked win-x64 restore failed with exit code $LASTEXITCODE." }

try {
    & $dotnetPath publish $projectPath --configuration Release --runtime win-x64 --no-restore `
        "-p:PublishProfile=$publishProfile" --output $stagingPath
    if ($LASTEXITCODE -ne 0) { throw "Self-contained publish failed with exit code $LASTEXITCODE." }

    $requiredFiles = @(
        'XiaoK.Host.exe', 'XiaoK.Host.dll', 'XiaoK.Host.deps.json', 'XiaoK.Host.runtimeconfig.json',
        'hostfxr.dll', 'hostpolicy.dll', 'coreclr.dll', 'System.Private.CoreLib.dll', 'PresentationFramework.dll'
    )
    foreach ($fileName in $requiredFiles) {
        if (-not (Test-Path -LiteralPath (Join-Path $stagingPath $fileName) -PathType Leaf)) {
            throw "Self-contained publish is missing required runtime/app file: $fileName"
        }
    }

    $runtimeConfigPath = Join-Path $stagingPath 'XiaoK.Host.runtimeconfig.json'
    $runtimeConfig = Get-Content -LiteralPath $runtimeConfigPath -Raw | ConvertFrom-Json
    if (-not $runtimeConfig.runtimeOptions.includedFrameworks) {
        throw 'Runtime config does not declare included frameworks; refusing a framework-dependent output.'
    }
    $frameworks = @($runtimeConfig.runtimeOptions.includedFrameworks | ForEach-Object { "$($_.name) $($_.version)" })
    if ($frameworks.Count -eq 0 -or ($frameworks | Where-Object { $_ -notmatch '10\.0\.12$' })) {
        throw "Published frameworks do not match the pinned 10.0.12 runtime: $($frameworks -join ', ')"
    }

    $stagingRelativePath = Join-Path 'artifacts\publish' $stagingName
    $validatorPath = Join-Path $PSScriptRoot 'package_msix_validation.ps1'
    & $validatorPath -PublishDirectory $stagingRelativePath
    if ($LASTEXITCODE -ne 0) { throw "MSIX package validation failed with exit code $LASTEXITCODE." }

    $previousOutputMoved = $false
    if (Test-Path -LiteralPath $publishPath) {
        $existingOutput = Get-Item -LiteralPath $publishPath -Force
        if (-not $existingOutput.PSIsContainer -or ($existingOutput.Attributes -band [System.IO.FileAttributes]::ReparsePoint)) {
            throw 'Existing publish output is not a regular directory; refusing to replace it.'
        }
        $existingItems = @(Get-ChildItem -LiteralPath $publishPath -Force -Recurse)
        if ($existingItems | Where-Object { $_.Attributes -band [System.IO.FileAttributes]::ReparsePoint }) {
            throw 'Existing publish output contains a reparse point; refusing to replace it.'
        }
        Move-Item -LiteralPath $publishPath -Destination $backupPath
        $previousOutputMoved = $true
    }
    try {
        Move-Item -LiteralPath $stagingPath -Destination $publishPath
    }
    catch {
        if ($previousOutputMoved -and -not (Test-Path -LiteralPath $publishPath) -and (Test-Path -LiteralPath $backupPath)) {
            try { Move-Item -LiteralPath $backupPath -Destination $publishPath }
            catch { throw "Publish replacement failed and the previous output could not be restored; it remains at $backupPath. Original error: $($_.Exception.Message)" }
        }
        throw
    }
    if ($previousOutputMoved -and (Test-Path -LiteralPath $backupPath)) {
        Remove-Item -LiteralPath $backupPath -Recurse -Force
    }
    Write-Output "Self-contained publish verified: $publishPath"
    Write-Output "Included frameworks: $($frameworks -join ', ')"
}
finally {
    if (Test-Path -LiteralPath $stagingPath) {
        Remove-Item -LiteralPath $stagingPath -Recurse -Force
    }
}
