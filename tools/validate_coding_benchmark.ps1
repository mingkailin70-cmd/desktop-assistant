[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string] $DatasetRoot
)

$ErrorActionPreference = 'Stop'
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$root = (Resolve-Path -LiteralPath $DatasetRoot -ErrorAction Stop).Path
$manifestPath = Join-Path $root 'manifest.json'
$manifest = Get-Content -Raw -Encoding UTF8 -LiteralPath $manifestPath | ConvertFrom-Json
$taskFileName = switch ([string]$manifest.version) {
    'coding-zh-v2' { if ($manifest.schemaVersion -ne 2) { throw 'coding-zh-v2 必须使用 schemaVersion=2。' }; 'coding_tasks_v2.jsonl' }
    'coding-zh-v3' { if ($manifest.schemaVersion -ne 3) { throw 'coding-zh-v3 必须使用 schemaVersion=3。' }; 'coding_tasks_v3.jsonl' }
    default { throw '只支持 coding-zh-v2 或 coding-zh-v3 固定评测集。' }
}
if ($manifest.status -ne 'targets-and-fixture-audited-not-scored') {
    throw '评测集状态字段不符合未评分版本要求。'
}
if ($manifest.privacy -ne 'synthetic only; no user data, credentials, source copies or model outputs') {
    throw '评测集隐私声明不匹配。'
}

$baseline = [string]$manifest.baselineCommit
if ($baseline -notmatch '^[0-9a-f]{40}$') { throw '基线必须是完整的 40 位 Git 提交哈希。' }
$resolvedBaseline = & git -C $repoRoot rev-parse --verify "$baseline^{commit}" 2>$null
if ($LASTEXITCODE -ne 0 -or ([string]$resolvedBaseline).Trim() -ne $baseline) {
    throw '评测基线不存在或未解析为锁定提交。'
}

foreach ($entry in $manifest.files) {
    $relativePath = [string]$entry.path
    if ($relativePath -notmatch '^[A-Za-z0-9_.-]+(/[A-Za-z0-9_.-]+)*$') { throw "清单文件路径无效：$relativePath" }
    $fullPath = Join-Path $root ($relativePath.Replace('/', [System.IO.Path]::DirectorySeparatorChar))
    if (-not (Test-Path -LiteralPath $fullPath -PathType Leaf)) { throw "缺少锁定文件：$relativePath" }
    $item = Get-Item -LiteralPath $fullPath
    if ($item.Length -ne [long]$entry.sizeBytes) { throw "文件大小不匹配：$relativePath" }
    $actualHash = (Get-FileHash -LiteralPath $fullPath -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($actualHash -ne ([string]$entry.sha256).ToLowerInvariant()) { throw "SHA-256 不匹配：$relativePath" }
}

$taskPath = Join-Path $root $taskFileName
$reviewFileName = if ($manifest.version -eq 'coding-zh-v3') { 'review_key_v3.jsonl' } else { 'review_key_v2.jsonl' }
$reviewPath = Join-Path $root $reviewFileName
$tasks = @(Get-Content -Encoding UTF8 -LiteralPath $taskPath | Where-Object { -not [string]::IsNullOrWhiteSpace($_) } | ForEach-Object { $_ | ConvertFrom-Json })
$reviews = @(Get-Content -Encoding UTF8 -LiteralPath $reviewPath | Where-Object { -not [string]::IsNullOrWhiteSpace($_) } | ForEach-Object { $_ | ConvertFrom-Json })
if ($tasks.Count -ne 40 -or [int]$manifest.taskCount -ne 40) { throw "必須恰好40题；当前为 $($tasks.Count)" }
if ($reviews.Count -ne $tasks.Count) { throw "评审答案数($($reviews.Count))与题目数不一致。" }

$taskIds = @{}
$counts = @{ R = 0; S = 0; M = 0; F = 0 }
$reviewIds = @{}
foreach ($review in $reviews) {
    if ([string]::IsNullOrWhiteSpace([string]$review.id) -or $reviewIds.ContainsKey($review.id)) { throw "评审答案 ID 缺失或重复：$($review.id)" }
    if ([string]::IsNullOrWhiteSpace([string]$review.expected) -or [string]::IsNullOrWhiteSpace([string]$review.evidence)) {
        throw "评审答案缺少期望结果或证据：$($review.id)"
    }
    if ($null -eq $review.targetFiles -or @($review.targetFiles).Count -eq 0) { throw "评审答案缺少目标文件：$($review.id)" }
    $reviewIds[$review.id] = $review
}

foreach ($task in $tasks) {
    $id = [string]$task.id
    if ($id -notmatch '^(R|S|M|F)(0[1-9]|10)$' -or $taskIds.ContainsKey($id)) { throw "题目 ID 无效或重复：$id" }
    $taskIds[$id] = $true
    $category = [string]$task.category
    if (-not $counts.ContainsKey($category) -or -not $id.StartsWith($category, [StringComparison]::Ordinal)) { throw "题目分类与 ID 前缀不一致：$id" }
    $counts[$category]++
    if ($null -eq $task.targetFiles -or @($task.targetFiles).Count -eq 0) { throw "题目缺少 targetFiles：$id" }
    if ($task.PSObject.Properties.Name -contains 'target') { throw "题目仍保留含糊的旧 target 字段：$id" }
    if ([string]::IsNullOrWhiteSpace([string]$task.prompt) -or [string]::IsNullOrWhiteSpace([string]$task.acceptance)) { throw "题目提示或验收条件为空：$id" }
    if ($manifest.version -eq 'coding-zh-v3' -and $category -eq 'S' -and ([string]$task.prompt).IndexOf([string]$task.acceptance, [StringComparison]::Ordinal) -lt 0) {
        throw "v3 单文件修改题提示必须包含实际任务要求：$id"
    }
    if ($task.networkAllowed -ne $false -or $task.externalSideEffectsAllowed -ne $false) { throw "题目意外允许联网或外部副作用：$id" }

    $targetCount = @($task.targetFiles).Count
    $badTargetCount = (($category -in @('R', 'S', 'F')) -and $targetCount -ne 1) -or (($category -eq 'M') -and ($targetCount -lt 2 -or $targetCount -gt 4))
    if ($badTargetCount) {
        throw "目标文件数量不符合类别要求：$id ($targetCount)"
    }
    $targetSeen = @{}
    foreach ($target in $task.targetFiles) {
        $relativePath = [string]$target
        if ($relativePath -notmatch '^[A-Za-z0-9_.-]+(/[A-Za-z0-9_.-]+)*$') { throw "目标路径无效：$id $relativePath" }
        if ($targetSeen.ContainsKey($relativePath)) { throw "题目目标文件重复：$id $relativePath" }
        $targetSeen[$relativePath] = $true
        if (-not ([string]$task.prompt).Contains($relativePath)) { throw "题目提示没有明示目标文件：$id $relativePath" }

        if ($category -eq 'F') {
            $fixturePath = Join-Path $root ($relativePath.Replace('/', [System.IO.Path]::DirectorySeparatorChar))
            $invalidFixtureTarget = -not $relativePath.StartsWith('repair-fixture/', [StringComparison]::Ordinal) -or -not (Test-Path -LiteralPath $fixturePath -PathType Leaf)
            if ($invalidFixtureTarget) {
                throw "修复题目标不在本地夹具目录或文件不存在：$id $relativePath"
            }
        } else {
            $gitPath = "${baseline}:$relativePath"
            $null = & git -C $repoRoot cat-file -e $gitPath 2>$null
            if ($LASTEXITCODE -ne 0) { throw "目标文件不在固定基线中：$id $relativePath" }
        }
    }
    if (-not $reviewIds.ContainsKey($id)) { throw "评审答案缺少题目 ID：$id" }
    $reviewTargets = @($reviewIds[$id].targetFiles)
    if ($reviewTargets.Count -ne $targetCount) { throw "题目与评审答案的目标文件数不一致：$id" }
    for ($targetIndex = 0; $targetIndex -lt $targetCount; $targetIndex++) {
        if ([string]$reviewTargets[$targetIndex] -cne [string]$task.targetFiles[$targetIndex]) {
            throw "题目与评审答案的目标文件顺序或内容不一致：$id"
        }
    }
}

foreach ($category in @('R', 'S', 'M', 'F')) {
    if ($counts[$category] -ne 10 -or [int]$manifest.categories.$category -ne 10) { throw "$category 类必须恰好10题。" }
}
foreach ($reviewId in $reviewIds.Keys) {
    if (-not $taskIds.ContainsKey($reviewId)) { throw "存在没有对应题目的评审答案：$reviewId" }
}

$fixtureProject = Join-Path $root 'repair-fixture/RepairFixture.csproj'
$fixtureSource = Join-Path $root 'repair-fixture/Program.cs'
if (-not (Test-Path -LiteralPath $fixtureProject -PathType Leaf) -or -not (Test-Path -LiteralPath $fixtureSource -PathType Leaf)) {
    throw '修复夹具缺少项目文件或断言程序。'
}
foreach ($id in 1..10) {
    $taskId = 'F{0:D2}' -f $id
    if ((Get-Content -Raw -Encoding UTF8 -LiteralPath $fixtureSource) -notmatch ('Check\("' + $taskId + '",\s*\(\)\s*=>')) { throw "修复夹具缺少隔离的确定性断言：$taskId" }
}
if ((Get-Content -Raw -Encoding UTF8 -LiteralPath $fixtureProject) -match '<PackageReference\b') { throw '修复夹具不允许新增第三方 NuGet 依赖。' }

Write-Output "结构验证通过：$($manifest.version) 40题（R/S/M/F 各10题），40份评审答案，目标范围锁定到 $baseline。"
Write-Output '未运行本地模型或夹具；本结果不代表模型成功率或 P0 发布门槛通过。'
