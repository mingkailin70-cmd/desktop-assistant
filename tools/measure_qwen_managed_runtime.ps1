[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$modelLockPath = Join-Path $repoRoot 'model-lock/models.lock.json'
$runtimeLockPath = Join-Path $repoRoot 'model-lock/runtimes.lock.json'
$modelLock = [System.IO.File]::ReadAllText($modelLockPath, [System.Text.Encoding]::UTF8) | ConvertFrom-Json
$runtimeLock = [System.IO.File]::ReadAllText($runtimeLockPath, [System.Text.Encoding]::UTF8) | ConvertFrom-Json
$modelRecord = @($modelLock.models | Where-Object id -eq 'qwen3.5-4b-q4km')
$runtimeRecord = @($runtimeLock.runtimes | Where-Object id -eq 'llama.cpp')
if ($modelRecord.Count -ne 1 -or $runtimeRecord.Count -ne 1 -or $runtimeRecord[0].version -ne 'b11259') {
    throw '固定的 Qwen 或 llama.cpp 清单缺失或有歧义。'
}
$modelRecord = $modelRecord[0]
$runtimeRecord = $runtimeRecord[0]
if ($modelRecord.status -ne 'downloaded_and_verified' -or $runtimeRecord.status -ne 'locally_evaluated') {
    throw '模型或运行时未处于预期的本机校验状态。'
}

$modelsRoot = [System.IO.Path]::GetFullPath((Join-Path $repoRoot 'models'))
$modelRoot = [System.IO.Path]::GetFullPath((Join-Path $modelsRoot $modelRecord.localDirectory))
if (-not $modelRoot.StartsWith($modelsRoot + [System.IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
    throw '模型锁定目录越过仓库 models 根目录。'
}
$runtimeRoot = Join-Path $modelRoot 'Runtime'
$modelFile = @($modelRecord.files | Where-Object name -eq 'Qwen3.5-4B-Q4_K_M.gguf')
$serverFile = @($runtimeRecord.stagedFiles | Where-Object name -eq 'llama-server.exe')
if ($modelFile.Count -ne 1 -or $serverFile.Count -ne 1) { throw '锁清单缺少唯一 Qwen 权重或 llama-server。' }

function Assert-LockedFile([string] $Path, [long] $ExpectedSize, [string] $ExpectedSha256) {
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { throw "锁定文件缺失：$Path" }
    $item = Get-Item -LiteralPath $Path -Force
    if (($item.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0 -or $item.Length -ne $ExpectedSize) {
        throw "锁定文件属性或大小不符：$Path"
    }
    if ((Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash -ne $ExpectedSha256) { throw "锁定文件 SHA-256 不符：$Path" }
}

Assert-LockedFile (Join-Path $modelRoot $modelFile[0].name) $modelFile[0].upstreamReportedSizeBytes $modelFile[0].localVerifiedSha256
foreach ($file in $runtimeRecord.stagedFiles) {
    Assert-LockedFile (Join-Path $runtimeRoot $file.name) $file.sizeBytes $file.sha256
}

$nvidiaSmi = Join-Path $env:SystemRoot 'System32/nvidia-smi.exe'
if (-not (Test-Path -LiteralPath $nvidiaSmi -PathType Leaf)) { throw '未找到 nvidia-smi；无法执行显存保护。' }
function Read-GpuSnapshot {
    $line = & $nvidiaSmi --id=0 --query-gpu=memory.total,memory.used,memory.free,utilization.gpu,temperature.gpu,power.draw --format=csv,noheader,nounits 2>$null
    if ($LASTEXITCODE -ne 0 -or @($line).Count -lt 1) { throw '无法读取 NVIDIA GPU 状态；本次未启动。' }
    $parts = ([string]@($line)[0]).Split(',') | ForEach-Object { $_.Trim() }
    if ($parts.Count -lt 6) { throw 'nvidia-smi 返回字段不完整。' }
    $values = foreach ($part in $parts) {
        $parsed = 0.0
        if ([double]::TryParse($part, [Globalization.NumberStyles]::Float,
                [Globalization.CultureInfo]::InvariantCulture, [ref]$parsed)) { $parsed } else { $null }
    }
    if ($null -eq $values[0] -or $null -eq $values[1] -or $null -eq $values[2]) { throw '显存读数无效；本次未启动。' }
    [pscustomobject]@{
        totalMiB = [long]$values[0]; usedMiB = [long]$values[1]; freeMiB = [long]$values[2]
        utilizationPercent = $values[3]; temperatureC = $values[4]; powerW = $values[5]
    }
}

$baseline = Read-GpuSnapshot
if ($baseline.freeMiB -lt 6024) { throw "当前显卡仅空闲 $($baseline.freeMiB) MiB；低于5000 MiB预算加1024 MiB余量，未启动。" }
if (@(Get-Process -Name 'llama-server','llama-bench' -ErrorAction SilentlyContinue).Count -gt 0) {
    throw '发现已有 llama.cpp 进程；为避免资源争用，本次未启动。'
}

$endpoint = 'http://127.0.0.1:18080/'
$probe = [System.Net.Sockets.TcpClient]::new()
try {
    $connect = $probe.BeginConnect('127.0.0.1', 18080, $null, $null)
    if ($connect.AsyncWaitHandle.WaitOne(250) -and $probe.Connected) { throw '端口18080已被占用；本次未启动。' }
} finally { $probe.Dispose() }

$manifestPath = Join-Path $modelRoot 'llama-runtime.json'
if (Test-Path -LiteralPath $manifestPath) { throw '模型目录已存在 llama-runtime.json；为避免覆盖用户设置，本次未启动。' }
$manifest = [ordered]@{
    schemaVersion = 1
    runtimeVersion = 'b11259'
    runtimeSha256 = $serverFile[0].sha256
    modelId = 'qwen3.5-4b-q4km'
    modelSha256 = $modelFile[0].localVerifiedSha256
    contextTokens = 4096
    gpuLayers = 99
    expectedGpuMemoryMiB = 5000
}
$manifestText = $manifest | ConvertTo-Json -Depth 4
$manifestBytes = [System.Text.UTF8Encoding]::new($false).GetBytes($manifestText)
$manifestHash = [BitConverter]::ToString([System.Security.Cryptography.SHA256]::Create().ComputeHash($manifestBytes)).Replace('-', '')
$manifestStream = [System.IO.File]::Open($manifestPath, [System.IO.FileMode]::CreateNew, [System.IO.FileAccess]::Write, [System.IO.FileShare]::None)
try { $manifestStream.Write($manifestBytes, 0, $manifestBytes.Length) } finally { $manifestStream.Dispose() }

$runId = [DateTime]::UtcNow.ToString('yyyyMMddTHHmmssZ') + '-' + [Guid]::NewGuid().ToString('N').Substring(0, 8)
$outputDirectory = Join-Path $repoRoot '.tools/benchmarks'
$emptyNugetSource = Join-Path $repoRoot '.tools/nuget-empty'
$buildDirectory = Join-Path $repoRoot '.tools/runtime-diagnostics'
New-Item -ItemType Directory -Path $outputDirectory,$emptyNugetSource -Force | Out-Null
$stdoutPath = Join-Path $outputDirectory "managed-qwen-$runId.stdout.txt"
$stderrPath = Join-Path $outputDirectory "managed-qwen-$runId.stderr.txt"
$runtimeReportPath = Join-Path $outputDirectory "managed-qwen-$runId.runtime.json"
$combinedReportPath = Join-Path $outputDirectory "managed-qwen-$runId.json"
$projectPath = Join-Path $PSScriptRoot 'XiaoK.RuntimeDiagnostics/XiaoK.RuntimeDiagnostics.csproj'
$sdk = Join-Path $repoRoot '.tools/dotnet/dotnet.exe'
if (-not (Test-Path -LiteralPath $sdk -PathType Leaf)) { $sdk = 'dotnet.exe' }
$process = $null
$samples = [System.Collections.Generic.List[object]]::new()
$failure = $null
$previousRunnerCpu = $null
$previousRunnerAt = $null
$previousServerCpu = @{}
$maxRunnerCpuPercent = 0.0
$maxServerCpuPercent = 0.0
$maxRunnerWorkingSetMiB = 0.0
$maxServerWorkingSetMiB = 0.0
$minimumSystemFreeGiB = $null
$startedAt = [DateTime]::UtcNow
try {
    & $sdk restore $projectPath --source $emptyNugetSource -p:NuGetAudit=false --verbosity quiet
    if ($LASTEXITCODE -ne 0) { throw "诊断程序离线还原失败，退出码 $LASTEXITCODE。" }
    & $sdk build $projectPath --configuration Release --no-restore --output $buildDirectory --verbosity quiet
    if ($LASTEXITCODE -ne 0) { throw "诊断程序构建失败，退出码 $LASTEXITCODE。" }
    $diagnosticDll = Join-Path $buildDirectory 'XiaoK.RuntimeDiagnostics.dll'
    $process = Start-Process -FilePath $sdk -ArgumentList @($diagnosticDll,'--managed-qwen',$modelRoot,$endpoint,$runtimeReportPath) `
        -WorkingDirectory $repoRoot -PassThru -WindowStyle Hidden -RedirectStandardOutput $stdoutPath -RedirectStandardError $stderrPath
    $lastSystemSampleAt = [DateTime]::MinValue
    $lastProgressMinute = -1
    while (-not $process.HasExited) {
        $gpu = Read-GpuSnapshot
        if ($gpu.freeMiB -lt 1024) {
            throw "测试期间显卡空闲仅 $($gpu.freeMiB) MiB，低于1 GiB保护线。"
        }
        $now = [DateTime]::UtcNow
        $runner = Get-Process -Id $process.Id -ErrorAction SilentlyContinue
        $runnerCpuPercent = $null
        $runnerWorkingSetMiB = $null
        if ($null -ne $runner) {
            $runnerWorkingSetMiB = [Math]::Round($runner.WorkingSet64 / 1MB, 1)
            $maxRunnerWorkingSetMiB = [Math]::Max($maxRunnerWorkingSetMiB, $runnerWorkingSetMiB)
            if ($null -ne $previousRunnerCpu -and $null -ne $previousRunnerAt) {
                $elapsed = ($now - $previousRunnerAt).TotalSeconds
                if ($elapsed -gt 0) {
                    $runnerCpuPercent = [Math]::Round((($runner.TotalProcessorTime.TotalSeconds - $previousRunnerCpu) / $elapsed / [Environment]::ProcessorCount) * 100, 1)
                    $maxRunnerCpuPercent = [Math]::Max($maxRunnerCpuPercent, $runnerCpuPercent)
                }
            }
            $previousRunnerCpu = $runner.TotalProcessorTime.TotalSeconds
            $previousRunnerAt = $now
        }
        $serverCpuPercent = $null
        $serverWorkingSetMiB = $null
        $server = Get-Process -Name 'llama-server' -ErrorAction SilentlyContinue | Select-Object -First 1
        if ($null -ne $server) {
            $serverWorkingSetMiB = [Math]::Round($server.WorkingSet64 / 1MB, 1)
            $maxServerWorkingSetMiB = [Math]::Max($maxServerWorkingSetMiB, $serverWorkingSetMiB)
            $prior = $previousServerCpu[$server.Id]
            if ($null -ne $prior) {
                $elapsed = ($now - $prior.at).TotalSeconds
                if ($elapsed -gt 0) {
                    $serverCpuPercent = [Math]::Round((($server.TotalProcessorTime.TotalSeconds - $prior.cpu) / $elapsed / [Environment]::ProcessorCount) * 100, 1)
                    $maxServerCpuPercent = [Math]::Max($maxServerCpuPercent, $serverCpuPercent)
                }
            }
            $previousServerCpu[$server.Id] = @{ cpu = $server.TotalProcessorTime.TotalSeconds; at = $now }
        }
        $systemFreeGiB = $null
        if (($now - $lastSystemSampleAt).TotalSeconds -ge 2) {
            try {
                $os = Get-CimInstance Win32_OperatingSystem -ErrorAction Stop
                $systemFreeGiB = [Math]::Round($os.FreePhysicalMemory / 1MB, 2)
                if ($null -eq $minimumSystemFreeGiB -or $systemFreeGiB -lt $minimumSystemFreeGiB) { $minimumSystemFreeGiB = $systemFreeGiB }
            } catch { }
            $lastSystemSampleAt = $now
        }
        $samples.Add([pscustomobject]@{
            atUtc = $now.ToString('o'); gpuUsedMiB = $gpu.usedMiB; gpuFreeMiB = $gpu.freeMiB
            gpuUtilizationPercent = $gpu.utilizationPercent; gpuTemperatureC = $gpu.temperatureC; gpuPowerW = $gpu.powerW
            runnerCpuPercent = $runnerCpuPercent; runnerWorkingSetMiB = $runnerWorkingSetMiB
            serverCpuPercent = $serverCpuPercent; serverWorkingSetMiB = $serverWorkingSetMiB
            systemFreeGiB = $systemFreeGiB
        })
        $elapsedMinutes = [int][Math]::Floor(([DateTime]::UtcNow - $startedAt).TotalMinutes)
        if ($elapsedMinutes -gt $lastProgressMinute) {
            Write-Host ("托管模型诊断已运行 {0} 分钟；可能正在等待四分钟空闲卸载。" -f $elapsedMinutes)
            $lastProgressMinute = $elapsedMinutes
        }
        Start-Sleep -Milliseconds 500
        $process.Refresh()
    }
    $process.WaitForExit()
    $process.Refresh()
    $diagnosticExitCode = $null
    try { $diagnosticExitCode = [int]$process.ExitCode } catch { }
    if (-not (Test-Path -LiteralPath $runtimeReportPath -PathType Leaf)) {
        $stderr = [System.IO.File]::ReadAllText($stderrPath, [System.Text.Encoding]::UTF8)
        throw "托管模型诊断未写出报告；退出码 $diagnosticExitCode。$stderr"
    }
    $runtimeReport = [System.IO.File]::ReadAllText($runtimeReportPath, [System.Text.Encoding]::UTF8) | ConvertFrom-Json
    $diagnosticPassed = [bool]$runtimeReport.diagnosticPassed -and ($null -eq $diagnosticExitCode -or $diagnosticExitCode -eq 0)
    $finalGpu = Read-GpuSnapshot
    $combined = [ordered]@{
        schemaVersion = 1; runId = $runId; diagnosticExitCode = $diagnosticExitCode; diagnosticPassed = $diagnosticPassed
        startedAtUtc = $startedAt.ToString('o'); finishedAtUtc = [DateTime]::UtcNow.ToString('o')
        modelId = $modelRecord.id; modelRevision = $modelRecord.revision; runtimeVersion = $runtimeRecord.version
        modelSha256 = $modelFile[0].localVerifiedSha256; serverSha256 = $serverFile[0].sha256
        gpuBaseline = $baseline; gpuFinal = $finalGpu
        minimumGpuFreeMiB = ($samples | Measure-Object -Property gpuFreeMiB -Minimum).Minimum
        peakGpuUsedMiB = ($samples | Measure-Object -Property gpuUsedMiB -Maximum).Maximum
        peakGpuUtilizationPercent = ($samples | Measure-Object -Property gpuUtilizationPercent -Maximum).Maximum
        peakGpuTemperatureC = ($samples | Measure-Object -Property gpuTemperatureC -Maximum).Maximum
        peakGpuPowerW = ($samples | Measure-Object -Property gpuPowerW -Maximum).Maximum
        peakRunnerCpuPercent = $maxRunnerCpuPercent; peakRunnerWorkingSetMiB = $maxRunnerWorkingSetMiB
        peakServerCpuPercent = $maxServerCpuPercent; peakServerWorkingSetMiB = $maxServerWorkingSetMiB
        minimumSystemFreeGiB = $minimumSystemFreeGiB; resourceSamples = @($samples)
        managedRuntime = $runtimeReport
    }
    $combined | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $combinedReportPath -Encoding UTF8
    Write-Output "托管运行时报告：$combinedReportPath"
    Write-Output ("启动前GPU空闲 {0} MiB；采样最低空闲 {1} MiB；占用峰值 {2} MiB；模型进程工作集峰值 {3} MiB" -f `
        $baseline.freeMiB,$combined.minimumGpuFreeMiB,$combined.peakGpuUsedMiB,$maxServerWorkingSetMiB)
    if (-not $diagnosticPassed) { throw '托管运行时诊断未通过；请按报告字段检查结果。报告已保留。' }
}
catch {
    $failure = $_
    if ($null -ne $process) {
        try { $process.Refresh() } catch { }
        if (-not $process.HasExited) {
            try { & (Join-Path $env:SystemRoot 'System32/taskkill.exe') /PID $process.Id /T /F | Out-Null } catch { }
            try { $process.WaitForExit(10000) | Out-Null } catch { }
        }
    }
    throw
}
finally {
    if ($null -ne $process) { $process.Dispose() }
    if (Test-Path -LiteralPath $manifestPath -PathType Leaf) {
        $currentHash = (Get-FileHash -LiteralPath $manifestPath -Algorithm SHA256).Hash
        if ($currentHash -eq $manifestHash) { Remove-Item -LiteralPath $manifestPath -Force }
        else { Write-Warning '临时清单已被外部修改，未删除；请检查并手动恢复。' }
    }
    if ($failure -and (Test-Path -LiteralPath $stderrPath)) {
        Write-Warning "本机诊断错误输出保留在忽略目录：$stderrPath"
    }
}
