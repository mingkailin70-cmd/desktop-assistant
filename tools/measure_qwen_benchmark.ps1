[CmdletBinding()]
param(
    [ValidateSet('cpu', 'gpu', 'compare')]
    [string] $Mode = 'compare',

    [ValidateRange(1, 10)]
    [int] $Repetitions = 3,

    [ValidateRange(1, 24)]
    [int] $Threads = 8,

    [switch] $VerboseDiagnostics
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$runtimeLockPath = Join-Path $repoRoot 'model-lock/runtimes.lock.json'
$modelLockPath = Join-Path $repoRoot 'model-lock/models.lock.json'
$runtimeLock = [System.IO.File]::ReadAllText($runtimeLockPath, [System.Text.Encoding]::UTF8) | ConvertFrom-Json
$modelLock = [System.IO.File]::ReadAllText($modelLockPath, [System.Text.Encoding]::UTF8) | ConvertFrom-Json
$runtimeRecord = @($runtimeLock.runtimes | Where-Object id -eq 'llama.cpp')
$modelRecord = @($modelLock.models | Where-Object id -eq 'qwen3.5-4b-q4km')
if ($runtimeRecord.Count -ne 1 -or $modelRecord.Count -ne 1 -or $runtimeRecord[0].version -ne 'b11259') {
    throw '固定 llama.cpp 或 Qwen 清单不存在或有歧义。'
}
$runtimeRecord = $runtimeRecord[0]
$modelRecord = $modelRecord[0]
if ($modelRecord.status -ne 'downloaded_and_verified') { throw 'Qwen 权重未处于已校验状态。' }

$modelsRoot = [System.IO.Path]::GetFullPath((Join-Path $repoRoot 'models'))
$modelRoot = [System.IO.Path]::GetFullPath((Join-Path $modelsRoot $modelRecord.localDirectory))
if (-not $modelRoot.StartsWith($modelsRoot + [System.IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
    throw '模型锁目录越过仓库 models 根目录。'
}
$runtimeRoot = Join-Path $modelRoot 'Runtime'
$modelFile = @($modelRecord.files | Where-Object { $_.name -eq 'Qwen3.5-4B-Q4_K_M.gguf' })
$serverRecord = @($runtimeRecord.stagedFiles | Where-Object { $_.name -eq 'llama-server.exe' })
$benchRecord = @($runtimeRecord.stagedFiles | Where-Object { $_.name -eq 'llama-bench.exe' })
if ($modelFile.Count -ne 1 -or $serverRecord.Count -ne 1 -or $benchRecord.Count -ne 1) {
    throw '锁清单缺少唯一模型、llama-server 或 llama-bench 条目。'
}
$modelPath = Join-Path $modelRoot $modelFile[0].name
$benchPath = Join-Path $runtimeRoot $benchRecord[0].name

function Assert-LockedFile([string] $Path, [long] $ExpectedSize, [string] $ExpectedSha256) {
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { throw "锁定文件缺失：$Path" }
    $item = Get-Item -LiteralPath $Path -Force
    if (($item.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0 -or $item.Length -ne $ExpectedSize) {
        throw "锁定文件属性或大小不符：$Path"
    }
    $hash = (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash
    if ($hash -ne $ExpectedSha256) { throw "SHA-256 与锁清单不符：$Path" }
}

Assert-LockedFile $modelPath $modelFile[0].upstreamReportedSizeBytes $modelFile[0].localVerifiedSha256
foreach ($file in $runtimeRecord.stagedFiles) {
    $path = Join-Path $runtimeRoot $file.name
    Assert-LockedFile $path $file.sizeBytes $file.sha256
}

$gpuLayers = switch ($Mode) {
    'cpu' { '0' }
    'gpu' { '99' }
    'compare' { '0,99' }
}
$expectedGpuLayerSet = switch ($Mode) {
    'cpu' { @(0) }
    'gpu' { @(99) }
    'compare' { @(0, 99) }
}
$nvidiaSmi = Join-Path $env:SystemRoot 'System32/nvidia-smi.exe'
if (-not (Test-Path -LiteralPath $nvidiaSmi -PathType Leaf)) { throw '未找到 nvidia-smi；无法执行有显存余量保护的测量。' }

function Read-GpuSnapshot {
    $lines = & $nvidiaSmi --id=0 --query-gpu=memory.total,memory.used,memory.free,utilization.gpu,temperature.gpu,power.draw --format=csv,noheader,nounits 2>$null
    if ($LASTEXITCODE -ne 0 -or $lines.Count -lt 1) { throw '无法读取 NVIDIA GPU 资源；已停止测量。' }
    $parts = ([string]@($lines)[0]).Split(',') | ForEach-Object { $_.Trim() }
    if ($parts.Count -lt 6) { throw 'nvidia-smi 返回字段不完整。' }
    $values = @()
    foreach ($part in $parts) {
        $parsed = 0.0
        if ([double]::TryParse($part, [Globalization.NumberStyles]::Float,
                [Globalization.CultureInfo]::InvariantCulture, [ref]$parsed)) { $values += $parsed }
        else { $values += $null }
    }
    if ($null -eq $values[0] -or $null -eq $values[1] -or $null -eq $values[2]) {
        throw 'nvidia-smi 显存读数无效；已停止测量。'
    }
    [pscustomobject]@{
        totalMiB = [long]$values[0]; usedMiB = [long]$values[1]; freeMiB = [long]$values[2]
        utilizationPercent = $values[3]; temperatureC = $values[4]; powerW = $values[5]
    }
}

$baseline = Read-GpuSnapshot
if ($Mode -ne 'cpu' -and $baseline.freeMiB -lt 6024) {
    throw "GPU 测试前可用显存仅 $($baseline.freeMiB) MiB；低于5000 MiB模型预算加1024 MiB余量，未启动测试。"
}
if (@(Get-Process -Name 'llama-server','llama-bench' -ErrorAction SilentlyContinue).Count -gt 0) {
    throw '发现已有 llama.cpp 进程；为避免资源竞争，本次测量未启动。'
}

$runId = [DateTime]::UtcNow.ToString('yyyyMMddTHHmmssZ') + '-' + [Guid]::NewGuid().ToString('N').Substring(0, 8)
$outputDirectory = Join-Path $repoRoot '.tools/benchmarks'
New-Item -ItemType Directory -Path $outputDirectory -Force | Out-Null
$stdoutPath = Join-Path $outputDirectory "qwen-$runId.stdout.txt"
$stderrPath = Join-Path $outputDirectory "qwen-$runId.stderr.txt"
$reportPath = Join-Path $outputDirectory "qwen-$runId.json"
$arguments = @('-m', $modelPath, '-p', '64', '-n', '32', '-r', "$Repetitions",
    '-ngl', $gpuLayers, '-t', "$Threads", '-o', 'json', '--offline')
if ($VerboseDiagnostics) { $arguments += '-v' }
$process = $null
$samples = [System.Collections.Generic.List[object]]::new()
$previousCpuSeconds = $null
$previousSampleAt = $null
$maxProcessCpuPercent = 0.0
$peakWorkingSetMiB = 0.0
$minimumSystemFreeGiB = $null
$startedAt = [DateTime]::UtcNow
$failure = $null

try {
    $process = Start-Process -FilePath $benchPath -ArgumentList $arguments -WorkingDirectory $runtimeRoot `
        -PassThru -WindowStyle Hidden -RedirectStandardOutput $stdoutPath -RedirectStandardError $stderrPath
    while (-not $process.HasExited) {
        $gpu = Read-GpuSnapshot
        if ($Mode -ne 'cpu' -and $gpu.freeMiB -lt 1024) {
            throw "测试期间 GPU 可用显存跌到 $($gpu.freeMiB) MiB，低于 1024 MiB 安全余量。"
        }
        $now = [DateTime]::UtcNow
        $proc = Get-Process -Id $process.Id -ErrorAction SilentlyContinue
        $cpuPercent = $null
        $workingSetMiB = $null
        if ($null -ne $proc) {
            $cpuSeconds = $proc.TotalProcessorTime.TotalSeconds
            $workingSetMiB = [Math]::Round($proc.WorkingSet64 / 1MB, 1)
            $peakWorkingSetMiB = [Math]::Max($peakWorkingSetMiB, $workingSetMiB)
            if ($null -ne $previousCpuSeconds -and $null -ne $previousSampleAt) {
                $elapsed = ($now - $previousSampleAt).TotalSeconds
                if ($elapsed -gt 0) {
                    $cpuPercent = [Math]::Round((($cpuSeconds - $previousCpuSeconds) / $elapsed / [Environment]::ProcessorCount) * 100, 1)
                    $maxProcessCpuPercent = [Math]::Max($maxProcessCpuPercent, $cpuPercent)
                }
            }
            $previousCpuSeconds = $cpuSeconds
            $previousSampleAt = $now
        }
        $systemFreeGiB = $null
        try {
            $os = Get-CimInstance Win32_OperatingSystem -ErrorAction Stop
            $systemFreeGiB = [Math]::Round($os.FreePhysicalMemory / 1MB, 2)
            if ($null -eq $minimumSystemFreeGiB -or $systemFreeGiB -lt $minimumSystemFreeGiB) { $minimumSystemFreeGiB = $systemFreeGiB }
        } catch { }
        $samples.Add([pscustomobject]@{
            atUtc = $now.ToString('o'); gpuUsedMiB = $gpu.usedMiB; gpuFreeMiB = $gpu.freeMiB
            gpuUtilizationPercent = $gpu.utilizationPercent; gpuTemperatureC = $gpu.temperatureC; gpuPowerW = $gpu.powerW
            processCpuPercent = $cpuPercent; processWorkingSetMiB = $workingSetMiB; systemFreeGiB = $systemFreeGiB
        })
        Start-Sleep -Milliseconds 500
        $process.Refresh()
    }
    $process.WaitForExit()
    $process.Refresh()
    $processExitCode = $null
    try { $processExitCode = $process.ExitCode } catch { }
    $stdout = [System.IO.File]::ReadAllText($stdoutPath, [System.Text.Encoding]::UTF8)
    $stderr = [System.IO.File]::ReadAllText($stderrPath, [System.Text.Encoding]::UTF8)
    $benchmarkCases = @(ConvertFrom-Json -InputObject $stdout)
    while ($benchmarkCases.Count -eq 1 -and $benchmarkCases[0] -is [array]) {
        $benchmarkCases = @($benchmarkCases[0])
    }
    $expectedCaseCount = 2 * @($expectedGpuLayerSet).Count
    if ($benchmarkCases.Count -lt $expectedCaseCount) {
        throw "llama-bench 返回的配置数不足；预期 $expectedCaseCount 组，实际 $($benchmarkCases.Count) 组。$stderr"
    }
    foreach ($case in $benchmarkCases) {
        if ([double]$case.avg_ts -le 0 -or $case.n_gpu_layers -notin $expectedGpuLayerSet) {
            throw "llama-bench 返回的吞吐值或 GPU 层配置无效。$stderr"
        }
    }
    if ($null -ne $processExitCode -and $processExitCode -ne 0) { throw "llama-bench 退出码 $processExitCode。$stderr" }
    $finalGpu = Read-GpuSnapshot
    $report = [ordered]@{
        schemaVersion = 1; runId = $runId; startedAtUtc = $startedAt.ToString('o')
        finishedAtUtc = [DateTime]::UtcNow.ToString('o'); mode = $Mode; gpuLayers = $gpuLayers
        verboseDiagnostics = [bool]$VerboseDiagnostics
        processExitCode = $processExitCode; processOutputValidated = $true
        repetitions = $Repetitions; threads = $Threads; promptTokens = 64; generationTokens = 32
        baselineGpu = $baseline; finalGpu = $finalGpu
        minimumGpuFreeMiB = ($samples | Measure-Object -Property gpuFreeMiB -Minimum).Minimum
        peakGpuUsedMiB = ($samples | Measure-Object -Property gpuUsedMiB -Maximum).Maximum
        peakProcessCpuPercent = $maxProcessCpuPercent; peakProcessWorkingSetMiB = $peakWorkingSetMiB
        minimumSystemFreeGiB = $minimumSystemFreeGiB; resourceSamples = @($samples)
        benchmarkCases = @($benchmarkCases | ForEach-Object {
            [pscustomobject]@{ gpuLayers = $_.n_gpu_layers; phase = if ($_.n_prompt -gt 0) { 'prompt-processing' } else { 'token-generation' }
                averageTokensPerSecond = $_.avg_ts; standardDeviationTokensPerSecond = $_.stddev_ts; samplesTokensPerSecond = $_.samples_ts }
        })
        benchmarkStdout = $stdout; benchmarkStderr = $stderr
    }
    $report | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $reportPath -Encoding UTF8
    Write-Output "测量报告：$reportPath"
    Write-Output ("基线GPU空闲 {0} MiB；采样最低空闲 {1} MiB；峰值GPU占用 {2} MiB；峰值模型进程工作集 {3} MiB；峰值CPU {4}%" -f `
        $baseline.freeMiB, $report.minimumGpuFreeMiB, $report.peakGpuUsedMiB, $peakWorkingSetMiB, $maxProcessCpuPercent)
    Write-Output 'llama-bench 原始 CSV/JSON 表格保存在报告中；本测量使用本地模型和合成 token，不访问网络或聊天账号。'
}
catch {
    $failure = $_
    throw
}
finally {
    if ($null -ne $process) {
        try { $process.Refresh() } catch { }
        if (-not $process.HasExited) {
            try { & (Join-Path $env:SystemRoot 'System32/taskkill.exe') /PID $process.Id /T /F | Out-Null } catch { }
            try { $process.WaitForExit(10000) | Out-Null } catch { }
        }
        $process.Dispose()
    }
    if ($failure -and (Test-Path -LiteralPath $stderrPath)) {
        Write-Warning "诊断日志保留在本机忽略目录：$stderrPath"
    }
}
