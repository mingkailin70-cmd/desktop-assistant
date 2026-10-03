[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidatePattern('^(R|S|M|F)(0[1-9]|10)$')]
    [string]$TaskId,
    [ValidateSet('coding-zh-v3', 'coding-zh-v4')]
    [string]$DatasetVersion = 'coding-zh-v4',
    [ValidateSet('qwen3.5-4b-q4km', 'mimo-v2.6-distill-qwen-9b-gguf-q8-0')]
    [string]$ModelId = 'qwen3.5-4b-q4km'
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$sdk = Join-Path $repoRoot '.tools/dotnet/dotnet.exe'
$project = Join-Path $PSScriptRoot 'XiaoK.CodingBenchmark/XiaoK.CodingBenchmark.csproj'
if (-not (Test-Path -LiteralPath $sdk -PathType Leaf)) { throw 'The pinned .NET SDK is missing.' }

& $sdk run --project $project --configuration Release --no-build -- --repo $repoRoot --dataset $DatasetVersion --task $TaskId --model-id $ModelId
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
