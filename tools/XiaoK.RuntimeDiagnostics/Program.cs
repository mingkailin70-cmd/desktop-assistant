using System.Diagnostics;
using System.Net;
using System.Text.Json;
using XiaoK.Inference;

if (args.Length == 4 && args[0] == "--voice-host-roundtrip")
    return await VoiceHostRoundTrip.RunAsync(args[1], args[2], args[3]);

if (args.Length == 5 && args[0] == "--voice-installed-idle-roundtrip")
    return await VoiceHostRoundTrip.RunInstalledAsync(args[1], args[2], args[3], args[4]);

if (args.Length == 5 && args[0] == "--voice-installed-batch-roundtrip")
    return await VoiceInstalledBatchRoundTrip.RunAsync(args[1], args[2], args[3], args[4]);

if (args.Length == 2 && args[0] == "--installed-content-search")
    return await InstalledPackageContentSearchCheck.RunAsync(args[1]);

if (args.Length != 4 || args[0] != "--managed-qwen")
{
    Console.Error.WriteLine("用法：");
    Console.Error.WriteLine("  XiaoK.RuntimeDiagnostics --managed-qwen <模型目录> <回环端点> <报告路径>");
    Console.Error.WriteLine("  XiaoK.RuntimeDiagnostics --voice-host-roundtrip <仓库目录> <语音环境目录> <仓库外报告路径>");
    Console.Error.WriteLine("  XiaoK.RuntimeDiagnostics --voice-installed-idle-roundtrip <已安装包目录> <模型目录> <语音环境目录> <仓库外报告路径>");
    Console.Error.WriteLine("  XiaoK.RuntimeDiagnostics --voice-installed-batch-roundtrip <已安装包目录> <模型目录> <语音环境目录> <仓库外报告路径>");
    Console.Error.WriteLine("  XiaoK.RuntimeDiagnostics --installed-content-search <小K已安装包目录>");
    return 2;
}

if (!OperatingSystem.IsWindows())
{
    Console.Error.WriteLine("托管模型诊断只支持 Windows。");
    return 2;
}

var modelRoot = Path.GetFullPath(args[1]);
var endpoint = args[2];
var reportPath = Path.GetFullPath(args[3]);
if (Process.GetProcessesByName("llama-server").Length != 0)
{
    Console.Error.WriteLine("已发现 llama-server 进程；为避免争用，本次诊断未启动。");
    return 3;
}

var runtime = LlamaCppModelRuntime.TryLoad(modelRoot, endpoint)
    ?? throw new InvalidDataException("托管运行清单不存在或模型目录不可安全读取。");
using var inference = new LocalInferenceClient(endpoint);
var broker = new ModelBroker(runtime);
var warmLatenciesMs = new List<double>();
var responseLengths = new List<int>();
var startedUtc = DateTimeOffset.UtcNow;
var coldClock = Stopwatch.StartNew();
try
{
    var first = await broker.RunInteractiveAsync(
        token => inference.CompleteAsync("只输出 OK，不要解释。", "请只回复 OK。", token),
        CancellationToken.None);
    coldClock.Stop();
    responseLengths.Add(first.Length);
    var initialServerPid = GetSingleLlamaServerPid();
    using var initialServerProcess = Process.GetProcessById(initialServerPid);
    Console.WriteLine($"托管模型冷启动与首个请求耗时 {coldClock.Elapsed.TotalMilliseconds:F0} ms。");

    for (var index = 0; index < 10; index++)
    {
        var clock = Stopwatch.StartNew();
        var response = await broker.RunInteractiveAsync(
            token => inference.CompleteAsync("只输出 OK，不要解释。", $"第 {index + 1} 次合成请求，只回复 OK。", token),
            CancellationToken.None);
        clock.Stop();
        responseLengths.Add(response.Length);
        warmLatenciesMs.Add(clock.Elapsed.TotalMilliseconds);
    }

    Console.WriteLine("十次托管热请求完成；测试提示和模型响应不写入报告。");
    await Task.Delay(TimeSpan.FromSeconds(1));
    var cancellationClock = Stopwatch.StartNew();
    using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
    var cancellationObserved = false;
    try
    {
        _ = await broker.RunInteractiveAsync(
            token => inference.CompleteAsync(
                "按要求写长文，不能提前结束，也不要使用工具。",
                "请写一篇至少一千字的中文故事，持续输出完整正文。",
                token), cancellation.Token);
    }
    catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
    {
        cancellationObserved = true;
    }
    cancellationClock.Stop();

    var afterCancellation = await broker.RunInteractiveAsync(
        token => inference.CompleteAsync("只输出 OK，不要解释。", "取消后恢复探针，只回复 OK。", token),
        CancellationToken.None);
    responseLengths.Add(afterCancellation.Length);

    Console.WriteLine("等待托管运行时达到四分钟空闲卸载窗口。");
    var idleClock = Stopwatch.StartNew();
    await Task.Delay(TimeSpan.FromMinutes(4) + TimeSpan.FromSeconds(20));
    var healthReachableAfterIdle = await IsHealthyAsync(endpoint);
    initialServerProcess.Refresh();
    var initialProcessAliveAfterIdle = !initialServerProcess.HasExited;
    var llamaServerCountAfterIdle = Process.GetProcessesByName("llama-server").Length;
    idleClock.Stop();

    var restartClock = Stopwatch.StartNew();
    var afterIdle = await broker.RunInteractiveAsync(
        token => inference.CompleteAsync("只输出 OK，不要解释。", "空闲卸载后的重启探针，只回复 OK。", token),
        CancellationToken.None);
    restartClock.Stop();
    responseLengths.Add(afterIdle.Length);
    var restartedServerPid = GetSingleLlamaServerPid();
    using var restartedServerProcess = Process.GetProcessById(restartedServerPid);

    var disposeClock = Stopwatch.StartNew();
    await runtime.DisposeAsync();
    disposeClock.Stop();
    await Task.Delay(TimeSpan.FromSeconds(1));
    restartedServerProcess.Refresh();
    var restartedProcessAliveAfterDispose = !restartedServerProcess.HasExited;
    var llamaServerCountAfterDispose = Process.GetProcessesByName("llama-server").Length;

    var passed = responseLengths.All(length => length > 0)
        && cancellationObserved
        && !healthReachableAfterIdle
        && !initialProcessAliveAfterIdle
        && llamaServerCountAfterIdle == 0
        && afterIdle.Length > 0
        && !restartedProcessAliveAfterDispose
        && llamaServerCountAfterDispose == 0;

    var sorted = warmLatenciesMs.Order().ToArray();
    var report = new
    {
        schemaVersion = 1,
        startedAtUtc = startedUtc,
        completedAtUtc = DateTimeOffset.UtcNow,
        runtime = "llama.cpp b11259 managed LlamaCppModelRuntime + ModelBroker + LocalInferenceClient",
        endpoint = endpoint,
        successfulResponses = responseLengths.Count,
        nonEmptyResponses = responseLengths.Count(length => length > 0),
        coldStartAndFirstRequestMs = coldClock.Elapsed.TotalMilliseconds,
        warmRequestCount = warmLatenciesMs.Count,
        warmRequestLatenciesMs = warmLatenciesMs,
        warmP50Ms = PercentileNearestRank(sorted, 0.50),
        warmP95Ms = PercentileNearestRank(sorted, 0.95),
        cancellationRequestedAfterMs = 500,
        cancellationObserved = cancellationObserved,
        cancellationRoundTripMs = cancellationClock.Elapsed.TotalMilliseconds,
        recoveryRequestAfterCancellationSucceeded = afterCancellation.Length > 0,
        idleUnloadWaitMs = idleClock.Elapsed.TotalMilliseconds,
        healthEndpointReachableAfterIdle = healthReachableAfterIdle,
        initialServerPid = initialServerPid,
        initialServerProcessAliveAfterIdle = initialProcessAliveAfterIdle,
        llamaServerCountAfterIdle = llamaServerCountAfterIdle,
        restartAfterIdleMs = restartClock.Elapsed.TotalMilliseconds,
        recoveryRequestAfterIdleSucceeded = afterIdle.Length > 0,
        restartedServerPid = restartedServerPid,
        disposeMs = disposeClock.Elapsed.TotalMilliseconds,
        restartedServerProcessAliveAfterDispose = restartedProcessAliveAfterDispose,
        llamaServerCountAfterDispose = llamaServerCountAfterDispose,
        postDisposeSettleMs = 1000,
        diagnosticPassed = passed
    };
    Directory.CreateDirectory(Path.GetDirectoryName(reportPath)!);
    await File.WriteAllTextAsync(reportPath, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
    Console.WriteLine($"诊断报告：{reportPath}");

    Console.WriteLine(passed ? "通过：托管模型请求、取消、四分钟空闲卸载、重新加载与进程退出。"
        : "未通过：详见诊断报告；运行时状态不能标记为 accepted。");
    return passed ? 0 : 1;
}
finally
{
    await runtime.DisposeAsync();
}

static double PercentileNearestRank(IReadOnlyList<double> ordered, double percentile)
{
    if (ordered.Count == 0) return 0;
    var index = Math.Clamp((int)Math.Ceiling(percentile * ordered.Count) - 1, 0, ordered.Count - 1);
    return ordered[index];
}

static int GetSingleLlamaServerPid()
{
    var servers = Process.GetProcessesByName("llama-server");
    try
    {
        if (servers.Length != 1) throw new InvalidOperationException($"预期一个 llama-server，实际 {servers.Length} 个。");
        return servers[0].Id;
    }
    finally
    {
        foreach (var server in servers) server.Dispose();
    }
}

static async Task<bool> IsHealthyAsync(string endpoint)
{
    using var client = new HttpClient(new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        UseProxy = false,
        ConnectTimeout = TimeSpan.FromSeconds(2)
    }) { Timeout = TimeSpan.FromSeconds(3) };
    try
    {
        using var response = await client.GetAsync(new Uri(new Uri(endpoint), "health"));
        return response.StatusCode == HttpStatusCode.OK;
    }
    catch (HttpRequestException) { return false; }
    catch (TaskCanceledException) { return false; }
}
