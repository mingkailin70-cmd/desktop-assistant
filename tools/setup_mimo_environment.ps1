[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$ToolsRoot = Join-Path $RepoRoot '.tools'
$PythonExe = Join-Path $ToolsRoot 'python\cpython-3.12.15+20261001\python.exe'
$UvExe = Join-Path $ToolsRoot 'bin\uv.exe'
$VenvRoot = Join-Path $ToolsRoot 'venvs\mimo'
$VenvPython = Join-Path $VenvRoot 'Scripts\python.exe'
$LockFile = Join-Path $RepoRoot 'model-lock\requirements-mimo.lock.txt'
$Wheelhouse = Join-Path $ToolsRoot 'wheelhouse'
$Cache = Join-Path $ToolsRoot 'uv-cache'
$ModelRoot = Join-Path $RepoRoot 'models\llm\mimo-v2.6-distill-qwen-9b\2367e865d009c13ac81713a2878291d33ab28177'

foreach ($requiredPath in @($PythonExe, $UvExe, $LockFile, $ModelRoot)) {
    if (-not (Test-Path -LiteralPath $requiredPath)) { throw "缺少固定的本地评测资产：$requiredPath" }
}
if (-not (Test-Path -LiteralPath $VenvPython -PathType Leaf)) {
    & $PythonExe -m venv $VenvRoot
    if ($LASTEXITCODE -ne 0) { throw '创建独立 MiMo 虚拟环境失败。' }
}

$PreviousHubOffline = $env:HF_HUB_OFFLINE
$PreviousTransformersOffline = $env:TRANSFORMERS_OFFLINE
try {
    $env:HF_HUB_OFFLINE = '1'
    $env:TRANSFORMERS_OFFLINE = '1'
    & $UvExe pip sync $LockFile --python $VenvPython --find-links $Wheelhouse --cache-dir $Cache --no-python-downloads
    if ($LASTEXITCODE -ne 0) { throw '按 SHA-256 锁文件同步 MiMo 评测环境失败。' }
    & $UvExe pip check --python $VenvPython
    if ($LASTEXITCODE -ne 0) { throw 'MiMo 评测环境依赖一致性检查失败。' }

    $env:XIAOK_MIMO_MODEL_ROOT = $ModelRoot
    & $VenvPython -c "import os, torch, transformers; from transformers import AutoConfig; from transformers.models.qwen3_5 import Qwen3_5ForConditionalGeneration; p=os.environ['XIAOK_MIMO_MODEL_ROOT']; c=AutoConfig.from_pretrained(p, local_files_only=True); assert transformers.__version__ == '5.12.1'; assert c.model_type == 'qwen3_5'; assert c.architectures == ['Qwen3_5ForConditionalGeneration']; print(f'MiMo config/import OK; transformers={transformers.__version__}; torch={torch.__version__}; architecture={c.architectures[0]}')"
    if ($LASTEXITCODE -ne 0) { throw 'MiMo Transformers 架构或离线模型配置检查失败。' }
    Write-Host '独立 MiMo 环境已锁定并验证架构/本地配置；本脚本没有读取权重张量、初始化 CUDA 或启动模型推理。'
}
finally {
    Remove-Item Env:XIAOK_MIMO_MODEL_ROOT -ErrorAction SilentlyContinue
    if ($null -eq $PreviousHubOffline) { Remove-Item Env:HF_HUB_OFFLINE -ErrorAction SilentlyContinue }
    else { Set-Item Env:HF_HUB_OFFLINE $PreviousHubOffline }
    if ($null -eq $PreviousTransformersOffline) { Remove-Item Env:TRANSFORMERS_OFFLINE -ErrorAction SilentlyContinue }
    else { Set-Item Env:TRANSFORMERS_OFFLINE $PreviousTransformersOffline }
}
