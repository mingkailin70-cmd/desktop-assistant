[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidatePattern('^(R|S|M|F)(0[1-9]|10)$')]
    [string]$TaskId,
    [ValidateSet('coding-zh-v3', 'coding-zh-v4')]
    [string]$DatasetVersion = 'coding-zh-v4',
    [ValidateSet('qwen3.5-4b-q4km', 'mimo-v2.6-distill-qwen-9b-gguf-q8-0', 'qwen3.5-9b-q4km-eval', 'autotrust-jev-9b-q4km-eval', 'gemma-4-e4b-it-qat-q4-0-eval', 'ornith-1.5-9b-q4km-eval', 'oxcoder-9b-q4km-eval')]
    [string]$ModelId = 'qwen3.5-4b-q4km',
    [switch]$EnableThinking
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$sdk = Join-Path $repoRoot '.tools/dotnet/dotnet.exe'
$assembly = Join-Path $PSScriptRoot 'XiaoK.CodingBenchmark/bin/Release/net10.0/XiaoK.CodingBenchmark.dll'
if (-not (Test-Path -LiteralPath $sdk -PathType Leaf)) { throw 'The pinned .NET SDK is missing.' }
if (-not (Test-Path -LiteralPath $assembly -PathType Leaf)) {
    throw 'The prebuilt benchmark DLL is missing. Build XiaoK.CodingBenchmark offline with the pinned SDK first.'
}

$arguments = @('exec', $assembly, '--repo', $repoRoot, '--dataset', $DatasetVersion, '--task', $TaskId, '--model-id', $ModelId)
if ($EnableThinking) { $arguments += '--enable-thinking' }
& $sdk @arguments
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
