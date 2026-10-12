[CmdletBinding()]
param(
    [ValidateSet('qwen3.5-4b-q4km', 'mimo-v2.6-distill-qwen-9b-gguf-q8-0', 'qwen3.5-9b-q4km-eval', 'qwen3.8-27b-pi-q4km-eval', 'qwen3.8-27b-ud-q4km-eval', 'qwen3.8-27b-ud-q3kxl-eval', 'ternary-bonsai-2-27b-ptq1-eval', 'autotrust-jev-9b-q4km-eval', 'gemma-4-e4b-it-qat-q4-0-eval', 'ornith-1.5-9b-q4km-eval', 'ornith-1.0-9b-q4km-eval', 'oxcoder-9b-q4km-eval')]
    [string]$ModelId = 'qwen3.5-4b-q4km'
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$sdk = Join-Path $repoRoot '.tools/dotnet/dotnet.exe'
$assembly = Join-Path $PSScriptRoot 'XiaoK.CodingBenchmark/bin/Release/net10.0/XiaoK.CodingBenchmark.dll'
if (-not (Test-Path -LiteralPath $sdk -PathType Leaf)) { throw 'The pinned .NET SDK is missing.' }
if (-not (Test-Path -LiteralPath $assembly -PathType Leaf)) {
    throw 'The prebuilt benchmark DLL is missing. Build XiaoK.CodingBenchmark offline with the pinned SDK first.'
}

& $sdk exec $assembly --repo $repoRoot --model-id $ModelId --validate-model-only
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
