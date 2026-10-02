[CmdletBinding()]
param(
    [string] $BenchmarkRoot = 'D:\XiaoK\Evaluations\benchmarks'
)

$ErrorActionPreference = 'Stop'
$source = Join-Path $BenchmarkRoot 'coding-zh-v1'
$destination = Join-Path $BenchmarkRoot 'coding-zh-v2'
if (-not (Test-Path -LiteralPath $source -PathType Container)) {
    throw "找不到 v1 评测集：$source"
}
if (Test-Path -LiteralPath $destination) {
    throw "目标目录已存在；为保护已有评测数据，不覆盖：$destination"
}

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$baselineOutput = & git -C $repoRoot rev-parse --verify '1d47085^{commit}' 2>$null
if ($LASTEXITCODE -ne 0) { throw '无法解析评测基线提交 1d47085。' }
$baselineCommit = ([string]$baselineOutput).Trim()

$targetMap = @{
    R01 = @('src/XiaoK.Core/AppLaunchIntentResolver.cs')
    R02 = @('src/XiaoK.Core/MessageNoticePolicy.cs')
    R03 = @('src/XiaoK.Core/MessageNoticePolicy.cs')
    R04 = @('src/XiaoK.Tools/ToolBroker.cs')
    R05 = @('src/XiaoK.Tools/ToolBroker.cs')
    R06 = @('src/XiaoK.Tools/CodeTaskAgent.cs')
    R07 = @('src/XiaoK.Inference/ModelBroker.cs')
    R08 = @('src/XiaoK.Inference/GpuMemoryAdmission.cs')
    R09 = @('src/XiaoK.Core/TaskHistoryRecoveryPolicy.cs')
    R10 = @('src/XiaoK.Host/WindowsNotificationMonitor.cs')
    S01 = @('src/XiaoK.Core/AppLaunchIntentResolver.cs')
    S02 = @('src/XiaoK.Core/AppLaunchIntentResolver.cs')
    S03 = @('src/XiaoK.Core/AppLaunchIntentResolver.cs')
    S04 = @('src/XiaoK.Core/AppLaunchIntentResolver.cs')
    S05 = @('src/XiaoK.Core/AppLaunchIntentResolver.cs')
    S06 = @('src/XiaoK.Core/AppLaunchIntentResolver.cs')
    S07 = @('src/XiaoK.Core/AppLaunchIntentResolver.cs')
    S08 = @('src/XiaoK.Core/TaskHistoryRecoveryPolicy.cs')
    S09 = @('src/XiaoK.Core/MessageNoticePolicy.cs')
    S10 = @('src/XiaoK.Core/MessageNoticePolicy.cs')
    M01 = @('src/XiaoK.Core/AppLaunchIntentResolver.cs', 'src/XiaoK.Host/AssistantRuntime.cs', 'src/XiaoK.Adapters.Windows/WindowsDesktopTools.cs', 'tests/XiaoK.Tools.SafetyChecks/Program.cs')
    M02 = @('src/XiaoK.Core/AppLaunchIntentResolver.cs', 'src/XiaoK.Tools/ToolBroker.cs', 'src/XiaoK.Adapters.Windows/WindowsDesktopTools.cs', 'tests/XiaoK.Tools.SafetyChecks/Program.cs')
    M03 = @('src/XiaoK.Tools/ToolBroker.cs', 'src/XiaoK.Adapters.Windows/WindowsDesktopTools.cs', 'tests/XiaoK.Tools.SafetyChecks/Program.cs')
    M04 = @('src/XiaoK.Core/MessageNoticePolicy.cs', 'src/XiaoK.Host/WindowsNotificationMonitor.cs', 'tests/XiaoK.Tools.SafetyChecks/Program.cs')
    M05 = @('src/XiaoK.Tools/CodeTaskAgent.cs', 'tests/XiaoK.Tools.SafetyChecks/Program.cs')
    M06 = @('src/XiaoK.Inference/LlamaCppModelRuntime.cs', 'src/XiaoK.Host/AssistantRuntime.cs', 'tests/XiaoK.Tools.SafetyChecks/Program.cs')
    M07 = @('src/XiaoK.Core/TaskHistoryRecoveryPolicy.cs', 'src/XiaoK.Host/AssistantRuntime.cs', 'src/XiaoK.Host/TaskHistoryWindow.xaml.cs', 'tests/XiaoK.Tools.SafetyChecks/Program.cs')
    M08 = @('src/XiaoK.Host/AssistantRuntime.cs', 'src/XiaoK.Tools/ToolBroker.cs', 'src/XiaoK.Host/MainWindow.xaml.cs', 'tests/XiaoK.Tools.SafetyChecks/Program.cs')
    M09 = @('src/XiaoK.Inference/ModelBroker.cs', 'src/XiaoK.Inference/LlamaCppModelRuntime.cs', 'src/XiaoK.Inference/IManagedModelRuntime.cs', 'tests/XiaoK.Tools.SafetyChecks/Program.cs')
    M10 = @('src/XiaoK.Adapters.Windows/MessageNoticeAdapters.cs', 'src/XiaoK.Core/MessageNoticePolicy.cs', 'src/XiaoK.Host/WindowsNotificationMonitor.cs', 'tests/XiaoK.Tools.SafetyChecks/Program.cs')
    F01 = @('repair-fixture/RepairFunctions.cs')
    F02 = @('repair-fixture/RepairFunctions.cs')
    F03 = @('repair-fixture/RepairFunctions.cs')
    F04 = @('repair-fixture/RepairFunctions.cs')
    F05 = @('repair-fixture/RepairFunctions.cs')
    F06 = @('repair-fixture/RepairFunctions.cs')
    F07 = @('repair-fixture/RepairFunctions.cs')
    F08 = @('repair-fixture/RepairFunctions.cs')
    F09 = @('repair-fixture/RepairFunctions.cs')
    F10 = @('repair-fixture/RepairFunctions.cs')
}

$inputPath = Join-Path $source 'coding_tasks_v1.jsonl'
$reviewPath = Join-Path $source 'review_key_v1.jsonl'
$inputTasks = @(Get-Content -Encoding UTF8 -LiteralPath $inputPath | Where-Object { -not [string]::IsNullOrWhiteSpace($_) } | ForEach-Object { $_ | ConvertFrom-Json })
$sourceReviews = @(Get-Content -Encoding UTF8 -LiteralPath $reviewPath | Where-Object { -not [string]::IsNullOrWhiteSpace($_) } | ForEach-Object { $_ | ConvertFrom-Json })
if ($inputTasks.Count -ne 40) { throw "v1 题目数不是40：$($inputTasks.Count)" }

$null = New-Item -ItemType Directory -Path $destination
$null = New-Item -ItemType Directory -Path (Join-Path $destination 'repair-fixture')
Copy-Item -LiteralPath (Join-Path $source 'repair-fixture/RepairFunctions.cs') -Destination (Join-Path $destination 'repair-fixture/RepairFunctions.cs')
Copy-Item -LiteralPath (Join-Path $source 'repair-fixture/RepairFixture.csproj') -Destination (Join-Path $destination 'repair-fixture/RepairFixture.csproj')

$fixtureProgram = @'
using RepairFixture;

var failed = new List<string>();
void Check(string id, Func<bool> assertion)
{
    try
    {
        if (!assertion()) failed.Add(id);
    }
    catch (Exception exception)
    {
        Console.WriteLine($"ERROR {id}: {exception.GetType().Name}");
        failed.Add(id);
    }
}

Check("F01", () => RepairFunctions.Clamp(5, 10, 20) == 10
    && RepairFunctions.Clamp(15, 10, 20) == 15
    && RepairFunctions.Clamp(25, 10, 20) == 20
    && Throws<ArgumentException>(() => RepairFunctions.Clamp(10, 20, 10)));
Check("F02", () => RepairFunctions.Divide(7, 2) == 3.5
    && RepairFunctions.Divide(-3, 2) == -1.5
    && RepairFunctions.Divide(1, 0) is null);
Check("F03", () => RepairFunctions.CountNonEmptyLines(null) == 0
    && RepairFunctions.CountNonEmptyLines("") == 0
    && RepairFunctions.CountNonEmptyLines("a\n \n b\n") == 2
    && RepairFunctions.CountNonEmptyLines("a\r\nb") == 2);
Check("F04", () => RepairFunctions.UniqueInOrder(new[] { 1, 2, 1, 3, 2 }).SequenceEqual(new[] { 1, 2, 3 }));
Check("F05", () => RepairFunctions.Page(new[] { "a", "b", "c", "d", "e" }, 2, 2).SequenceEqual(new[] { "c", "d" })
    && RepairFunctions.Page(new[] { "a", "b", "c", "d", "e" }, 1, 2).SequenceEqual(new[] { "a", "b" })
    && RepairFunctions.Page(new[] { "a", "b" }, 0, 2).Length == 0
    && RepairFunctions.Page(new[] { "a", "b" }, 1, 0).Length == 0
    && RepairFunctions.Page(new[] { "a", "b" }, -1, 2).Length == 0
    && RepairFunctions.Page(new[] { "a", "b" }, 1, -1).Length == 0);
Check("F06", () => RepairFunctions.IsBalancedParentheses("()()")
    && RepairFunctions.IsBalancedParentheses("(())")
    && RepairFunctions.IsBalancedParentheses("")
    && !RepairFunctions.IsBalancedParentheses(")(")
    && !RepairFunctions.IsBalancedParentheses("(()")
    && !RepairFunctions.IsBalancedParentheses("())("));
Check("F07", () => RepairFunctions.ParsePort("80") == 80
    && RepairFunctions.ParsePort("65535") == 65535
    && RepairFunctions.ParsePort("0") is null
    && RepairFunctions.ParsePort("65536") is null
    && RepairFunctions.ParsePort("-1") is null
    && RepairFunctions.ParsePort("80.5") is null
    && RepairFunctions.ParsePort("abc") is null);
var left = new[] { 1, 4, 7 };
var right = new[] { 2, 3, 8 };
Check("F08", () => RepairFunctions.MergeSorted(left, right).SequenceEqual(new[] { 1, 2, 3, 4, 7, 8 })
    && RepairFunctions.MergeSorted(Array.Empty<int>(), right).SequenceEqual(right)
    && RepairFunctions.MergeSorted(left, Array.Empty<int>()).SequenceEqual(left)
    && left.SequenceEqual(new[] { 1, 4, 7 }) && right.SequenceEqual(new[] { 2, 3, 8 }));
Check("F09", () => RepairFunctions.GetOrDefault(new Dictionary<string, string> { ["a"] = "value" }, "a", "fallback") == "value"
    && RepairFunctions.GetOrDefault(new Dictionary<string, string>(), "missing", "fallback") == "fallback");
Check("F10", () => RepairFunctions.RetryDelayMs(0, 100, 1000) == 100
    && RepairFunctions.RetryDelayMs(20, 100, 1000) == 1000
    && Throws<ArgumentOutOfRangeException>(() => RepairFunctions.RetryDelayMs(-1, 100, 1000))
    && Throws<ArgumentOutOfRangeException>(() => RepairFunctions.RetryDelayMs(0, -1, 1000))
    && Throws<ArgumentOutOfRangeException>(() => RepairFunctions.RetryDelayMs(0, 100, -1)));

foreach (var id in failed) Console.WriteLine("FAIL " + id);
Console.WriteLine($"{10 - failed.Count}/10 cases pass");
return failed.Count == 0 ? 0 : 1;

static bool Throws<TException>(Action action) where TException : Exception
{
    try { action(); }
    catch (TException) { return true; }
    catch (Exception) { return false; }
    return false;
}
'@
$utf8NoBom = [System.Text.UTF8Encoding]::new($false)
[System.IO.File]::WriteAllText((Join-Path $destination 'repair-fixture/Program.cs'), $fixtureProgram, $utf8NoBom)

$taskLines = [System.Collections.Generic.List[string]]::new()
$reviewLines = [System.Collections.Generic.List[string]]::new()
$reviewById = @{}
foreach ($review in $sourceReviews) { $reviewById[$review.id] = $review }
foreach ($task in $inputTasks) {
    if (-not $targetMap.ContainsKey($task.id)) { throw "缺少目标映射：$($task.id)" }
    $files = [string[]]$targetMap[$task.id]
    $taskCopy = [ordered]@{}
    foreach ($property in $task.PSObject.Properties) {
        if ($property.Name -notin @('target', 'prompt')) { $taskCopy[$property.Name] = $property.Value }
    }
    $taskCopy['targetFiles'] = @($files)
    $scope = $files -join '、'
    if ($task.category -eq 'R') {
        $taskCopy['prompt'] = "只依据以下目标文件回答，不要猜测仓库外上下文：$scope`n$($task.prompt)"
    } else {
        $taskCopy['prompt'] = "本题允许修改的目标文件（超出此范围的修改按未通过）：$scope`n$($task.prompt)"
    }
    if ($task.id -eq 'F10') {
        $taskCopy['prompt'] = "本题允许修改的目标文件（超出此范围的修改按未通过）：$scope`n实现饱和指数退避。attempt=0 时返回 baseMs；大 attempt 不溢出且结果不超过 maxMs；attempt、baseMs 或 maxMs 任一为负数时抛 ArgumentOutOfRangeException。"
        $taskCopy['acceptance'] = 'attempt=0返回baseMs；attempt=20、baseMs=100、maxMs=1000返回1000且不溢出；attempt/baseMs/maxMs任一为负数时抛ArgumentOutOfRangeException。'
    }
    $taskLines.Add(($taskCopy | ConvertTo-Json -Compress -Depth 12))

    if ($task.category -eq 'R' -and $reviewById.ContainsKey($task.id)) {
        $expected = $reviewById[$task.id].expected
        $evidence = $reviewById[$task.id].evidence
    } else {
        $expected = $taskCopy['acceptance']
        if ($task.category -eq 'F') {
            $evidence = 'repair-fixture/Program.cs 中同 ID 的确定性断言通过；仅允许改动 targetFiles。'
        } else {
            $evidence = '逐项核对验收条件；差异只触及 targetFiles；合成安全回归检查通过。'
        }
    }
    $reviewLines.Add((([ordered]@{ id = $task.id; expected = $expected; evidence = $evidence; targetFiles = @($files) } | ConvertTo-Json -Compress -Depth 8)))
}

[System.IO.File]::WriteAllText((Join-Path $destination 'coding_tasks_v2.jsonl'), ($taskLines -join "`n") + "`n", $utf8NoBom)
[System.IO.File]::WriteAllText((Join-Path $destination 'review_key_v2.jsonl'), ($reviewLines -join "`n") + "`n", $utf8NoBom)
[System.IO.File]::WriteAllText((Join-Path $destination 'README.md'), @'
# coding-zh-v2 中文编码任务集

本版本由 v1 复制生成并固定每题的 `targetFiles`。R 类限定读取文件；S 类限定一个可修改文件；M 类限定 2–4 个文件；F 类只允许修改 `repair-fixture/RepairFunctions.cs`。评审答案只放在 `review_key_v2.jsonl`，严禁交给被测模型。

任务输入：`coding_tasks_v2.jsonl`。修复题在干净夹具副本运行 `dotnet run --project repair-fixture/RepairFixture.csproj`，确定性断言位于 `repair-fixture/Program.cs`。其余类别按目标文件、验收条件和合成安全检查人工判定。

此版本仍未运行本地模型，也没有成功率结论。请先用仓库的 `tools/validate_coding_benchmark.ps1` 检查清单、基线文件范围、评审答案覆盖和 SHA-256。
'@, $utf8NoBom)

$filesToLock = @(
    'coding_tasks_v2.jsonl', 'review_key_v2.jsonl', 'README.md',
    'repair-fixture/Program.cs', 'repair-fixture/RepairFunctions.cs', 'repair-fixture/RepairFixture.csproj'
)
$lockedFiles = foreach ($relativePath in $filesToLock) {
    $path = Join-Path $destination $relativePath
    $hash = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant()
    [ordered]@{ path = $relativePath.Replace('\', '/'); sizeBytes = (Get-Item -LiteralPath $path).Length; sha256 = $hash }
}
$manifest = [ordered]@{
    schemaVersion = 2
    version = 'coding-zh-v2'
    baselineCommit = $baselineCommit
    createdUtc = [DateTime]::UtcNow.ToString('yyyy-MM-dd')
    status = 'targets-and-fixture-audited-not-scored'
    taskCount = 40
    categories = [ordered]@{ R = 10; S = 10; M = 10; F = 10 }
    privacy = 'synthetic only; no user data, credentials, source copies or model outputs'
    targets = [ordered]@{ overall = '32/40'; multiFile = '7/10'; unauthorizedNetwork = 0; outOfScopeWrites = 0; unconfirmedSends = 0 }
    files = @($lockedFiles)
}
[System.IO.File]::WriteAllText((Join-Path $destination 'manifest.json'), ($manifest | ConvertTo-Json -Depth 10), $utf8NoBom)

Write-Output "已在 $destination 创建 coding-zh-v2；未改写 v1、未运行模型、未复制任何模型输出。"
Write-Output '下一步请执行：powershell.exe -NoProfile -ExecutionPolicy Bypass -File tools\validate_coding_benchmark.ps1 -DatasetRoot D:\XiaoK\Evaluations\benchmarks\coding-zh-v2'
