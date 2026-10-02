[CmdletBinding()]
param(
    [string]$Proxy
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
$PreviousHttpsProxy = $env:HTTPS_PROXY
$PreviousHttpProxy = $env:HTTP_PROXY
$PreviousHubOffline = $env:HF_HUB_OFFLINE
$PreviousTransformersOffline = $env:TRANSFORMERS_OFFLINE
if ($Proxy) {
    # 只设置当前脚本进程环境，供 uv 子进程使用，不写入系统或用户设置。
    $env:HTTPS_PROXY = $Proxy
    $env:HTTP_PROXY = $Proxy
}

try {
$RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$ToolsRoot = Join-Path $RepoRoot '.tools'
$DownloadsRoot = Join-Path $ToolsRoot 'downloads'
$PythonVersion = '3.12.15+20261001'
$PythonArchiveName = 'cpython-3.12.15+20261001-x86_64-pc-windows-msvc-install_only.tar.gz'
$PythonArchive = Join-Path $DownloadsRoot $PythonArchiveName
$PythonSha256 = '62faca756c1b2e9b2454b6ad9396a6890496aebf7479a0c93e1ed615c67f2985'
$PythonRoot = Join-Path $ToolsRoot "python\cpython-$PythonVersion"
$PythonExe = Join-Path $PythonRoot 'python.exe'

$UvVersion = '0.12.20'
$UvArchiveName = 'uv-x86_64-pc-windows-msvc.zip'
$UvArchive = Join-Path $DownloadsRoot $UvArchiveName
$UvSha256 = '95f9bc30fbb3574d276e28ac4a6de932d25153645853d13da8c21eec3bc88d06'
$UvExe = Join-Path $ToolsRoot 'bin\uv.exe'

$TorchWheelName = 'torch-2.14.1+cu132-cp312-cp312-win_amd64.whl'
$TorchWheel = Join-Path $ToolsRoot "wheelhouse\$TorchWheelName"
$TorchSha256 = '882a670a7e2a17f2eb351406dc4a235455fd9256ac737752de5f1279e2a1df8c'
$TorchBytes = 1993688363L

$Artifacts = @(
    [pscustomobject]@{
        Name = $PythonArchiveName
        Path = $PythonArchive
        Url = "https://github.com/astral-sh/python-build-standalone/releases/download/20261001/$PythonArchiveName"
        Bytes = 46415849L
        Sha256 = $PythonSha256
    },
    [pscustomobject]@{
        Name = $UvArchiveName
        Path = $UvArchive
        Url = "https://github.com/astral-sh/uv/releases/download/$UvVersion/$UvArchiveName"
        Bytes = 18039150L
        Sha256 = $UvSha256
    },
    [pscustomobject]@{
        Name = $TorchWheelName
        Path = $TorchWheel
        Url = "https://download.pytorch.org/whl/cu132/$([uri]::EscapeDataString($TorchWheelName))"
        Bytes = $TorchBytes
        Sha256 = $TorchSha256
    }
)

function Get-FileSha256([string]$Path) {
    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
}

function Assert-Artifact([string]$Path, [long]$ExpectedBytes, [string]$ExpectedSha256, [string]$Name) {
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "缺少锁定资产：$Name ($Path)"
    }
    $actualBytes = (Get-Item -LiteralPath $Path).Length
    if ($actualBytes -ne $ExpectedBytes) {
        throw "资产大小不匹配：$Name；预期 $ExpectedBytes 字节，实际 $actualBytes 字节。"
    }
    $actualSha256 = Get-FileSha256 $Path
    if ($actualSha256 -ne $ExpectedSha256) {
        throw "资产 SHA-256 不匹配：$Name；预期 $ExpectedSha256，实际 $actualSha256。"
    }
}

function Get-LockedArtifact($Artifact) {
    $parent = Split-Path -Parent $Artifact.Path
    New-Item -ItemType Directory -Path $parent -Force | Out-Null
    if (-not (Test-Path -LiteralPath $Artifact.Path -PathType Leaf)) {
        $partialPath = "$($Artifact.Path).partial"
        if (Test-Path -LiteralPath $partialPath) {
            throw "发现未完成下载文件 $partialPath；请检查后手动处理，再重新运行脚本。"
        }
        Write-Host "下载固定资产：$($Artifact.Name)"
        $request = @{ Uri = $Artifact.Url; OutFile = $partialPath }
        if ($Proxy) { $request.Proxy = $Proxy }
        Invoke-WebRequest @request
        Assert-Artifact $partialPath $Artifact.Bytes $Artifact.Sha256 $Artifact.Name
        Move-Item -LiteralPath $partialPath -Destination $Artifact.Path
    }
    Assert-Artifact $Artifact.Path $Artifact.Bytes $Artifact.Sha256 $Artifact.Name
}

foreach ($artifact in $Artifacts) { Get-LockedArtifact $artifact }

if (-not (Test-Path -LiteralPath $PythonExe -PathType Leaf)) {
    $pythonParent = Split-Path -Parent $PythonRoot
    New-Item -ItemType Directory -Path $pythonParent -Force | Out-Null
    $pythonStage = Join-Path $pythonParent ".python-stage-$([guid]::NewGuid().ToString('N'))"
    New-Item -ItemType Directory -Path $pythonStage | Out-Null
    try {
        & tar.exe -xzf $PythonArchive -C $pythonStage --strip-components=1
        if ($LASTEXITCODE -ne 0) { throw "CPython 解包失败，tar 退出码 $LASTEXITCODE。" }
        $stagedPython = Join-Path $pythonStage 'python.exe'
        if (-not (Test-Path -LiteralPath $stagedPython -PathType Leaf)) { throw 'CPython 归档内未找到 python.exe。' }
        Move-Item -LiteralPath $pythonStage -Destination $PythonRoot
    }
    finally {
        if (Test-Path -LiteralPath $pythonStage) { Remove-Item -LiteralPath $pythonStage -Recurse -Force }
    }
}
$pythonInfo = & $PythonExe -c "import platform,sys; print(f'{sys.version_info.major}.{sys.version_info.minor}.{sys.version_info.micro}|{platform.architecture()[0]}')"
if ($LASTEXITCODE -ne 0 -or $pythonInfo -ne '3.12.15|64bit') { throw "项目 CPython 版本或架构不符合锁定值：$pythonInfo" }

$uvStage = Join-Path $ToolsRoot "downloads\uv-stage-$([guid]::NewGuid().ToString('N'))"
New-Item -ItemType Directory -Path $uvStage -Force | Out-Null
try {
    Expand-Archive -LiteralPath $UvArchive -DestinationPath $uvStage
    $stagedUv = Join-Path $uvStage 'uv.exe'
    if (-not (Test-Path -LiteralPath $stagedUv -PathType Leaf)) { throw 'uv 压缩包内未找到 uv.exe。' }
    $uvInfo = (& $stagedUv --version | Out-String).Trim()
    if ($LASTEXITCODE -ne 0 -or $uvInfo -notmatch "^uv $([regex]::Escape($UvVersion))(\s|$)") { throw "uv 版本不符合锁定值：$uvInfo" }
    New-Item -ItemType Directory -Path (Split-Path -Parent $UvExe) -Force | Out-Null
    Copy-Item -LiteralPath $stagedUv -Destination $UvExe -Force
}
finally {
    if (Test-Path -LiteralPath $uvStage) { Remove-Item -LiteralPath $uvStage -Recurse -Force }
}

$installedUv = (& $UvExe --version | Out-String).Trim()
if ($LASTEXITCODE -ne 0 -or $installedUv -notmatch "^uv $([regex]::Escape($UvVersion))(\s|$)") { throw "安装后的 uv 版本不符合锁定值：$installedUv" }

foreach ($kind in @('asr', 'tts')) {
    $venv = Join-Path $ToolsRoot "venvs\$kind"
    $venvPython = Join-Path $venv 'Scripts\python.exe'
    if (-not (Test-Path -LiteralPath $venvPython -PathType Leaf)) {
        New-Item -ItemType Directory -Path (Split-Path -Parent $venv) -Force | Out-Null
        & $PythonExe -m venv $venv
        if ($LASTEXITCODE -ne 0) { throw "创建 $kind 虚拟环境失败。" }
    }
    $lockFile = Join-Path $RepoRoot "model-lock\requirements-$kind.lock.txt"
    if (-not (Test-Path -LiteralPath $lockFile -PathType Leaf)) { throw "缺少哈希锁文件：$lockFile" }
    Write-Host "按哈希锁同步 $kind 环境"
    & $UvExe pip sync $lockFile --python $venvPython --find-links (Join-Path $ToolsRoot 'wheelhouse') --cache-dir (Join-Path $ToolsRoot 'uv-cache') --no-python-downloads
    if ($LASTEXITCODE -ne 0) { throw "$kind 依赖同步失败。" }
    & $UvExe pip check --python $venvPython
    if ($LASTEXITCODE -ne 0) { throw "$kind 依赖一致性检查失败。" }
}

$env:HF_HUB_OFFLINE = '1'
$env:TRANSFORMERS_OFFLINE = '1'
& (Join-Path $ToolsRoot 'venvs\asr\Scripts\python.exe') -c 'import torch, torchaudio, qwen_asr; print("ASR imports OK", torch.__version__, torchaudio.__version__)'
if ($LASTEXITCODE -ne 0) { throw 'ASR 离线导入检查失败。' }
& (Join-Path $ToolsRoot 'venvs\tts\Scripts\python.exe') -c 'import torch, torchaudio, qwen_tts, onnxruntime; print("TTS imports OK", torch.__version__, torchaudio.__version__)'
if ($LASTEXITCODE -ne 0) { throw 'TTS 离线导入检查失败。' }

Write-Host '语音 Python 环境已按固定哈希安装并完成包一致性及离线导入检查。没有加载语音模型、调用 CUDA 设备或访问麦克风。'
}
finally {
    foreach ($name in @('HTTPS_PROXY', 'HTTP_PROXY', 'HF_HUB_OFFLINE', 'TRANSFORMERS_OFFLINE')) {
        $prior = switch ($name) {
            'HTTPS_PROXY' { $PreviousHttpsProxy }
            'HTTP_PROXY' { $PreviousHttpProxy }
            'HF_HUB_OFFLINE' { $PreviousHubOffline }
            'TRANSFORMERS_OFFLINE' { $PreviousTransformersOffline }
        }
        if ($null -eq $prior) { Remove-Item "Env:$name" -ErrorAction SilentlyContinue }
        else { Set-Item "Env:$name" $prior }
    }
}
