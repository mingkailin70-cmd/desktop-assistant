using System.Diagnostics;
using System.Formats.Tar;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.ComponentModel;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using XiaoK.Adapters.Windows;
using XiaoK.Core;
using XiaoK.Inference;
using XiaoK.Tools;

namespace XiaoK.CodingBenchmark;

internal static class Program
{
    private const long MinimumRuntimeGpuFreeMiB = 1_024;
    private const int EvaluationContextTokens = 6144;
    private const string PipelineVersionNoThinking = "code-agent-inspect-source-rendered-mappings-v54-notice-policy-facts-v56-deterministic-source-guards-v52-compact-correction-context-v50-source-condition-outcome-coverage-v48-ordered-pre-call-guards-v47-source-mapping-target-coverage-v46-topic-enum-coverage-v45-dynamic-topic-counts-v44-citation-line-grounding-v43-compact-claims-v42-completeness-check-v41-json-schema-v40-v38-v37-inspect-operation-gates-v36-compact-line-citations-v35-structured-claim-citations-v34-specific-citation-feedback-v33-fixed-source-reference-line-v31-precise-claim-scope-v30-bounded-source-citation-correction-v29-absolute-line-citations-validated-v28-explicit-target-priority-cross-separator-selection-line-anchored-exact-edits-configured-app-id-alias-safety-decision-branch-context-anchors-bounded-validation-correction-extra-semantic-location-newline-normalized-target-path-noise-contained-nuget-paths-6144-no-thinking";
    private const string PipelineVersionThinking = "code-agent-inspect-source-rendered-mappings-v55-notice-policy-facts-v57-deterministic-source-guards-v53-compact-correction-context-v51-source-condition-outcome-coverage-v49-ordered-pre-call-guards-v47-source-mapping-target-coverage-v46-topic-enum-coverage-v45-dynamic-topic-counts-v44-citation-line-grounding-v43-compact-claims-v42-completeness-check-v41-json-schema-v40-v38-v37-inspect-operation-gates-v36-compact-line-citations-v35-structured-claim-citations-v34-specific-citation-feedback-v33-fixed-source-reference-line-v31-precise-claim-scope-v30-bounded-source-citation-correction-v29-absolute-line-citations-validated-v28-explicit-target-priority-cross-separator-selection-line-anchored-exact-edits-configured-app-id-alias-safety-decision-branch-context-anchors-bounded-validation-correction-extra-semantic-location-newline-normalized-target-path-noise-contained-nuget-paths-6144";
    private const string V3ManifestSha256 = "8a057c1fa935e0b2200cfa89fdce8328567b1a737e50fe2adf25e9b2ebf68ae4";
    private const string V4ManifestSha256 = "9d5e09034231d119895fcc029b0fd6119d8993e24cb5fe116c4de88322208d5f";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = new UTF8Encoding(false);
        Console.InputEncoding = new UTF8Encoding(false);
        try
        {
            var options = ParseArguments(args);

            if (Console.IsInputRedirected)
            {
                Console.Error.WriteLine("评测需要交互终端进行独立人工评分；检测到标准输入被重定向，未启动模型且未写入结果。");
                return 2;
            }

            var repoRoot = RequireLocalDirectory(options.RepositoryRoot, "仓库目录");
            var datasetVersion = options.DatasetVersion;
            var datasetLock = ResolveDatasetLock(datasetVersion);
            var taskId = options.TaskId.ToUpperInvariant();
            if (taskId.Length != 3 || taskId[0] is not ('R' or 'S' or 'M' or 'F')
                || !int.TryParse(taskId.AsSpan(1), out var taskNumber) || taskNumber is < 1 or > 10)
                throw new ArgumentException("题目 ID 必须是 R01–R10、S01–S10、M01–M10 或 F01–F10。");

            var datasetRoot = RequireLocalDirectory(Path.Combine("D:\\XiaoK\\Evaluations\\benchmarks", datasetVersion), "固定评测集");
            var manifest = ReadAndValidateManifest(datasetRoot, datasetVersion, datasetLock);
            var task = ReadTask(datasetRoot, taskId, datasetLock);
            if (!string.Equals(task.Category, taskId[..1], StringComparison.Ordinal))
                throw new InvalidDataException("题目类别与 ID 前缀不一致。");

            var baselineCommit = RequireGitCommit(manifest.BaselineCommit);
            await EnsureGitCommitExistsAsync(repoRoot, baselineCommit);
            var modelId = options.ModelId;
            var model = ResolveModel(repoRoot, modelId);
            var pipelineVersion = options.EnableThinking ? PipelineVersionThinking : PipelineVersionNoThinking;
            var resultFile = GetAggregateResultPath(manifest.Version);
            EnsureTaskNotAlreadyScored(resultFile, manifest.Version, baselineCommit, model.Id, model.Revision, pipelineVersion, taskId);
            var gpuBaseline = await ReadGpuSnapshotAsync();
            if (gpuBaseline.FreeMiB < model.MinimumInitialGpuFreeMiB)
                throw new InvalidOperationException($"启动前显存空闲 {gpuBaseline.FreeMiB} MiB，低于该模型预算 {model.ExpectedGpuMemoryMiB:N0} MiB 加 1,024 MiB 余量；本次未启动。");
            if (Process.GetProcessesByName("llama-server").Length != 0 || Process.GetProcessesByName("llama-bench").Length != 0)
                throw new InvalidOperationException("发现已有 llama.cpp 进程；为避免资源争用，本次未启动。");

            var endpoint = GetUnusedLoopbackEndpoint();
            var evaluationManifest = CreateEvaluationManifestJson(model);
            var runtime = (model.EvaluationCandidate
                ? LlamaCppModelRuntime.TryLoadEvaluationCandidate(model.Root, model.RuntimeRoot,
                    endpoint.AbsoluteUri, model.Id, contextTokensOverride: model.ContextTokens,
                    evaluationManifestJson: evaluationManifest)
                : LlamaCppModelRuntime.TryLoadPrimaryForEvaluation(model.Root, model.RuntimeRoot,
                    endpoint.AbsoluteUri, evaluationManifest, contextTokensOverride: model.ContextTokens))
                ?? throw new InvalidDataException("固定 llama.cpp 评测运行清单未能加载。");
            var broker = new ModelBroker(runtime);
            using var inference = new LocalInferenceClient(endpoint.AbsoluteUri);
            var responseDiagnostics = new List<LocalInferenceResponseDiagnostics>();
            inference.ResponseCompleted += responseDiagnostics.Add;
            using var cancellation = new CancellationTokenSource();
            var stopReason = new StopReason();
            ConsoleCancelEventHandler cancelHandler = (_, eventArgs) =>
            {
                eventArgs.Cancel = true;
                stopReason.UserCancelled = true;
                cancellation.Cancel();
            };
            Console.CancelKeyPress += cancelHandler;

            var runId = DateTimeOffset.UtcNow.ToString("yyyyMMddTHHmmssZ") + "-" + Guid.NewGuid().ToString("N")[..8];
            // AppContainer adds its own package/profile path below the verification root.
            // Keep the owned workspace under a short, dedicated D: evaluation scratch path
            // so Windows restore/build paths remain below legacy MAX_PATH limits.
            var scratchRoot = Path.Combine("D:\\XiaoK\\Evaluations", "scratch");
            Directory.CreateDirectory(scratchRoot);
            var tempRoot = Path.Combine(scratchRoot, "xk-" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(tempRoot);
            var gpuSamples = new GpuSampleAccumulator(gpuBaseline);
            var completed = false;
            var taskRoot = Path.Combine(tempRoot, "project");
            var workspaceRoot = Path.Combine(tempRoot, "w");
            var timer = Stopwatch.StartNew();
            Task? gpuMonitor = null;

            try
            {
                if (task.Category == "F")
                {
                    CopyRepairTarget(datasetRoot, taskRoot);
                }
                else
                {
                    var archivePath = Path.Combine(tempRoot, "baseline.tar");
                    await ExportGitArchiveAsync(repoRoot, baselineCommit, archivePath, cancellation.Token);
                    ExtractSafeGitArchive(archivePath, Path.Combine(tempRoot, "baseline"));
                    CopyTaskTargets(Path.Combine(tempRoot, "baseline"), taskRoot, task.TargetFiles);
                }

                Directory.CreateDirectory(workspaceRoot);
                var codeAgent = new CodeTaskAgent(inference, broker, repoRoot,
                    disableThinkingForInspection: !options.EnableThinking);
                var review = new KeepPatchAndDenyAllPresenter();
                var toolBroker = new ToolBroker(new WindowsDesktopTools([], []), inference, broker, review,
                    codeAgent, taskRoot, workspaceRoot);
                var toolId = task.Category == "R" ? "code.inspect.v1" : "code.task.create.v1";
                var expected = task.Category == "R"
                    ? ToolExpectedOutcome.CodeExplanationReturned
                    : ToolExpectedOutcome.ReviewablePatchCreated;
                var proposal = ToolBroker.Proposal(toolId,
                    [new KeyValuePair<string, string>("instruction", task.Prompt)], "configured-project", expected);

                gpuMonitor = MonitorGpuAsync(gpuSamples, cancellation, stopReason);
                Console.WriteLine($"固定评测题：{task.Id}（{task.Category}）；模型：{model.Id}；只读检索思考模式：{(options.EnableThinking ? "开启（显式选择）" : "关闭（生产默认）")}；管线：{pipelineVersion}；推理地址：127.0.0.1:{endpoint.Port}");
                Console.WriteLine("只发送本地模型的合成任务提示；不会访问账号、通知或发送任何消息。");
                Console.WriteLine("执行中……按 Ctrl+C 可取消；若显存余量低于 1 GiB，运行器会自动取消并停止。");
                var result = await toolBroker.ExecuteAsync(proposal, cancellation.Token);

                var workspaceHistory = CodeTaskAgent.ReadRetainedTasks(workspaceRoot);
                var latest = workspaceHistory.OrderByDescending(item => item.CreatedAtUtc).FirstOrDefault();
                IReadOnlyList<string> changedFiles = latest is null
                    ? []
                    : FindChangedFiles(Path.Combine(Path.GetDirectoryName(latest.WorkspacePath)!, "baseline"), latest.WorkspacePath);
                var inScope = task.Category == "R"
                    ? changedFiles.Count == 0
                    : changedFiles.All(path => task.TargetFiles.Contains(path, StringComparer.Ordinal));
                var fixtureResult = task.Category == "F" && latest is not null
                    ? await RunFixtureAsync(repoRoot, datasetRoot, latest.WorkspacePath, cancellation.Token)
                    : null;
                timer.Stop();

                Console.WriteLine();
                Console.WriteLine($"工具返回：{(result.Success ? "成功" : "失败")} / {result.ErrorCode ?? "无错误码"}");
                Console.WriteLine($"耗时：{timer.Elapsed.TotalSeconds:F1} 秒；显存最低空闲：{gpuSamples.MinimumFreeMiB} MiB；显存峰值占用：{gpuSamples.PeakUsedMiB} MiB");
                if (responseDiagnostics.Count != 0)
                {
                    Console.WriteLine("模型响应诊断（仅长度/token/结束原因，不含原文）：");
                    for (var index = 0; index < responseDiagnostics.Count; index++)
                    {
                        var item = responseDiagnostics[index];
                        Console.WriteLine($"  {index + 1}: 正文字符={item.ContentCharacters}，reasoning字符={item.ReasoningCharacters}，prompt/completion token={item.PromptTokens}/{item.CompletionTokens}，finish={item.FinishReason ?? "未知"}");
                    }
                }
                if (task.Category != "R")
                {
                    Console.WriteLine("检测到的补丁文件：" + (changedFiles.Count == 0 ? "（无）" : string.Join("、", changedFiles)));
                    Console.WriteLine($"目标范围：{(inScope ? "通过" : "失败，至少一个改动超出题目范围")}");
                }
                if (fixtureResult is not null)
                    Console.WriteLine($"隔离夹具：{(fixtureResult.RestoreExitCode == 0 && fixtureResult.TestExitCode == 0 ? "通过" : "失败")}（离线 AppContainer）\n{fixtureResult.Output}");

                PrintBounded("模型结果", result.Data ?? result.Summary, 24_000);
                var reviewKey = ReadReviewKeyAfterInference(datasetRoot, task.Id, datasetLock);
                Console.WriteLine();
                Console.WriteLine("独立评审依据（此内容在模型生成结束后才读取，未发送给模型）：");
                Console.WriteLine("期望：" + reviewKey.Expected);
                Console.WriteLine("证据：" + reviewKey.Evidence);

                var automaticallySafe = result.Success && inScope
                    && (fixtureResult is null || (fixtureResult.RestoreExitCode == 0 && fixtureResult.TestExitCode == 0));
                Console.Write(automaticallySafe
                    ? "请对照期望与证据，整题是否完全通过？输入 y 确认通过，其他输入记为未通过："
                    : "安全/执行门槛未通过，本题自动记为未通过。按 Enter 查看汇总：");
                var reviewerPassed = automaticallySafe && string.Equals(Console.ReadLine()?.Trim(), "y", StringComparison.OrdinalIgnoreCase);
                if (!automaticallySafe) _ = Console.ReadLine();

                var aggregate = new AggregateResult(3, pipelineVersion, responseDiagnostics.Count,
                    responseDiagnostics.Sum(item => item.PromptTokens), responseDiagnostics.Sum(item => item.CompletionTokens),
                    responseDiagnostics.Count(item => item.ContentCharacters == 0),
                    string.Join(",", responseDiagnostics.Select(item => item.FinishReason ?? "unknown")),
                    runId, DateTimeOffset.UtcNow, manifest.Version, baselineCommit,
                    task.Id, task.Category, model.Id, model.Revision, model.ModelSha256, model.RuntimeVersion,
                    timer.Elapsed.TotalMilliseconds, gpuBaseline.FreeMiB, gpuSamples.MinimumFreeMiB,
                    gpuSamples.PeakUsedMiB, result.Success, inScope, fixtureResult?.RestoreExitCode,
                    fixtureResult?.TestExitCode, reviewerPassed, Sha256(Encoding.UTF8.GetBytes(result.Data ?? result.Summary)));
                AppendAggregate(resultFile, aggregate);
                completed = true;
                Console.WriteLine();
                Console.WriteLine($"本题评分：{(reviewerPassed ? "通过" : "未通过")}；聚合结果已写入：{resultFile}");
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
            {
                timer.Stop();
                Console.Error.WriteLine(stopReason.LowGpuMemory
                    ? $"显存空闲低于 {MinimumRuntimeGpuFreeMiB} MiB，任务已取消。"
                    : stopReason.UserCancelled ? "已取消任务。" : "评测任务已取消。");
                return 130;
            }
            finally
            {
                cancellation.Cancel();
                if (gpuMonitor is not null)
                {
                    try { await gpuMonitor; } catch (OperationCanceledException) { }
                }
                Console.CancelKeyPress -= cancelHandler;
                try { await runtime.DisposeAsync(); }
                finally
                {
                    var temporaryDataRemoved = TryDeleteOwnedDirectory(tempRoot);
                    if (!temporaryDataRemoved)
                        Console.Error.WriteLine("临时评测工作区未能清理；合成任务输出可能仍位于：" + tempRoot);
                }
                if (!completed)
                    Console.WriteLine("评测摘要未包含模型原始输出；临时目录清理状态见上方信息。");
            }
            return 0;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException
            or ArgumentException or InvalidOperationException or JsonException or Win32Exception or HttpRequestException)
        {
            Console.Error.WriteLine("本次未完成评测：" + ex.Message);
            return 1;
        }
    }

    private static BenchmarkOptions ParseArguments(string[] args)
    {
        string? repositoryRoot = null;
        string? datasetVersion = null;
        string? taskId = null;
        var modelId = "qwen3.5-4b-q4km";
        var enableThinking = false;
        var seen = new HashSet<string>(StringComparer.Ordinal);

        for (var index = 0; index < args.Length; index++)
        {
            var option = args[index];
            if (!seen.Add(option)) throw new ArgumentException("命令行选项不可重复：" + option);
            if (option == "--enable-thinking")
            {
                enableThinking = true;
                continue;
            }

            if (option is not ("--repo" or "--dataset" or "--task" or "--model-id") || index + 1 >= args.Length)
                throw new ArgumentException("未知选项或缺少选项值：" + option);

            var value = args[++index];
            if (string.IsNullOrWhiteSpace(value) || value.StartsWith("--", StringComparison.Ordinal))
                throw new ArgumentException("选项缺少有效值：" + option);
            switch (option)
            {
                case "--repo": repositoryRoot = value; break;
                case "--dataset": datasetVersion = value; break;
                case "--task": taskId = value; break;
                case "--model-id": modelId = value; break;
            }
        }

        if (repositoryRoot is null || datasetVersion is null || taskId is null)
            throw new ArgumentException("缺少必需选项 --repo、--dataset 或 --task。");
        return new(repositoryRoot, datasetVersion, taskId, modelId, enableThinking);
    }

    private static BenchmarkTask ReadTask(string datasetRoot, string taskId, DatasetLock datasetLock)
    {
        var path = Path.Combine(datasetRoot, datasetLock.TaskFileName);
        foreach (var line in File.ReadLines(path, Encoding.UTF8))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            var task = JsonSerializer.Deserialize<BenchmarkTask>(line, JsonOptions)
                ?? throw new InvalidDataException("题目行无效。");
            if (task.Id == taskId) return task;
        }
        throw new InvalidDataException("评测集内找不到指定题目。");
    }

    private static ReviewKey ReadReviewKeyAfterInference(string datasetRoot, string taskId, DatasetLock datasetLock)
    {
        foreach (var line in File.ReadLines(Path.Combine(datasetRoot, datasetLock.ReviewFileName), Encoding.UTF8))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            using var item = JsonDocument.Parse(line);
            if (!item.RootElement.TryGetProperty("id", out var id) || id.GetString() != taskId) continue;
            return new(item.RootElement.GetProperty("expected").ToString(), item.RootElement.GetProperty("evidence").ToString());
        }
        throw new InvalidDataException("评测集缺少题目对应的独立评审依据。");
    }

    private static DatasetLock ResolveDatasetLock(string datasetVersion) => datasetVersion switch
    {
        "coding-zh-v3" => new(3, V3ManifestSha256, "coding_tasks_v3.jsonl", "review_key_v3.jsonl", 6),
        "coding-zh-v4" => new(4, V4ManifestSha256, "coding_tasks_v4.jsonl", "review_key_v4.jsonl", 8),
        _ => throw new ArgumentException("只支持固定评测集 coding-zh-v3 或 coding-zh-v4。")
    };

    private static BenchmarkManifest ReadAndValidateManifest(string datasetRoot, string expectedVersion, DatasetLock datasetLock)
    {
        var manifestPath = Path.Combine(datasetRoot, "manifest.json");
        if (!string.Equals(Sha256File(manifestPath), datasetLock.ManifestSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException(expectedVersion + " 清单哈希与代码内固定值不符；拒绝运行评测。");
        var manifest = JsonSerializer.Deserialize<BenchmarkManifest>(File.ReadAllText(manifestPath, Encoding.UTF8), JsonOptions)
            ?? throw new InvalidDataException("评测清单为空。");
        if (manifest.SchemaVersion != datasetLock.SchemaVersion || manifest.Version != expectedVersion || manifest.TaskCount != 40 || manifest.Categories.R != 10
            || manifest.Categories.S != 10 || manifest.Categories.M != 10 || manifest.Categories.F != 10
            || manifest.Files.Count != datasetLock.LockedFileCount)
            throw new InvalidDataException("评测集清单版本、题数或类别不符合固定结构。");

        foreach (var lockedFile in manifest.Files)
        {
            if (Path.IsPathRooted(lockedFile.Path) || lockedFile.Path.Split('/', '\\').Any(part => part is "" or "." or ".."))
                throw new InvalidDataException("评测清单中的文件路径无效。");
            var path = Path.GetFullPath(Path.Combine(datasetRoot, lockedFile.Path.Replace('/', Path.DirectorySeparatorChar)));
            if (!path.StartsWith(datasetRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                || !File.Exists(path)) throw new InvalidDataException("评测集锁定文件缺失或越界。");
            var info = new FileInfo(path);
            if (info.Length != lockedFile.SizeBytes || !string.Equals(Sha256File(path), RequireSha256(lockedFile.Sha256, "评测文件哈希"), StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("评测集锁定文件字节数或 SHA-256 不匹配：" + lockedFile.Path);
        }
        return manifest;
    }

    private static ModelConfig ResolveModel(string repoRoot, string modelId)
    {
        var modelLock = JsonSerializer.Deserialize<ModelLock>(File.ReadAllText(Path.Combine(repoRoot, "model-lock", "models.lock.json"), Encoding.UTF8), JsonOptions)
            ?? throw new InvalidDataException("本地模型锁文件为空。");
        var runtimeLock = JsonSerializer.Deserialize<RuntimeLock>(File.ReadAllText(Path.Combine(repoRoot, "model-lock", "runtimes.lock.json"), Encoding.UTF8), JsonOptions)
            ?? throw new InvalidDataException("本地运行时锁文件为空。");
        var profile = modelId switch
        {
            "qwen3.5-4b-q4km" => new ModelProfile("Qwen3.5-4B-Q4_K_M.gguf", 99, 5_000, 6_024, false),
            "mimo-v2.6-distill-qwen-9b-gguf-q8-0" => new ModelProfile("MiMo-V2.6-Distill-Qwen-9B-Q8_0.gguf", 8, 3_500, 4_524, true),
            "qwen3.5-9b-q4km-eval" => new ModelProfile("Qwen3.5-9B-Q4_K_M.gguf", 12, 3_500, 4_524, true),
            "autotrust-jev-9b-q4km-eval" => new ModelProfile("JEV-9B.Q4_K_M.gguf", 12, 3_500, 4_524, true),
            _ => throw new ArgumentException("评测仅允许锁定的 Qwen3.5-4B Q4_K_M、MiMo V2.6 Q8_0、Qwen3.5-9B Q4_K_M 或 AutoTrust JEV-9B Q4_K_M 模型。", nameof(modelId))
        };
        var model = modelLock.Models.Single(item => item.Id == modelId);
        var runtime = runtimeLock.Runtimes.Single(item => item.Id == "llama.cpp");
        var modelStatusAllowsEvaluation = profile.EvaluationCandidate
            ? model.Status is "downloaded_and_verified" or "locally_evaluated"
            : model.Status == "downloaded_and_verified";
        if (!modelStatusAllowsEvaluation || runtime.Version != "b11259"
            || runtime.Status != "locally_evaluated" || model.Files.Count != 1 || runtime.StagedFiles.Count == 0)
            throw new InvalidDataException("锁定模型尚未通过哈希校验，或 llama.cpp b11259 状态不符合评测要求。");
        var modelsRoot = Path.GetFullPath(Path.Combine(repoRoot, "models"));
        var root = Path.GetFullPath(Path.Combine(modelsRoot, model.LocalDirectory.Replace('/', Path.DirectorySeparatorChar)));
        if (!root.StartsWith(modelsRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("模型目录越过忽略的 models 根目录。");
        var modelFile = model.Files.Single(file => file.Name == profile.ModelFileName);
        var serverFile = runtime.StagedFiles.Single(file => file.Name == "llama-server.exe");
        var runtimeModel = modelId == "qwen3.5-4b-q4km"
            ? model
            : modelLock.Models.Single(item => item.Id == "qwen3.5-4b-q4km");
        var runtimeRoot = Path.GetFullPath(Path.Combine(modelsRoot,
            runtimeModel.LocalDirectory.Replace('/', Path.DirectorySeparatorChar), "Runtime"));
        if (!runtimeRoot.StartsWith(modelsRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("llama.cpp运行时目录越过忽略的 models 根目录。");
        return new(model.Id, model.Revision, root, profile.ModelFileName, modelFile.LocalVerifiedSha256,
            runtimeRoot, runtime.Version, serverFile.Sha256, runtime.StagedFiles,
            EvaluationContextTokens, profile.GpuLayers, profile.ExpectedGpuMemoryMiB,
            profile.MinimumInitialGpuFreeMiB, profile.EvaluationCandidate);
    }

    private static string CreateEvaluationManifestJson(ModelConfig model)
    {
        foreach (var file in model.RuntimeFiles)
        {
            var path = Path.Combine(model.RuntimeRoot, file.Name);
            if (!File.Exists(path) || new FileInfo(path).Length != file.SizeBytes
                || !string.Equals(Sha256File(path), file.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("锁定运行时文件缺失或哈希不符：" + file.Name);
        }
        var modelPath = Path.Combine(model.Root, model.ModelFile);
        if (!File.Exists(modelPath) || !string.Equals(Sha256File(modelPath), model.ModelSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("锁定模型文件缺失或哈希不符；本次未启动。");

        return JsonSerializer.Serialize(new RuntimeManifest(1, model.RuntimeVersion, model.RuntimeSha256,
            model.Id, model.ModelSha256, model.ContextTokens, model.GpuLayers, model.ExpectedGpuMemoryMiB), JsonOptions);
    }

    private static Uri GetUnusedLoopbackEndpoint()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return new Uri($"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}/");
    }

    private static async Task<GpuSnapshot> ReadGpuSnapshotAsync()
    {
        var executable = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "nvidia-smi.exe");
        if (!File.Exists(executable)) throw new InvalidOperationException("未找到 nvidia-smi；无法执行显存安全闸门。");
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        foreach (var argument in new[] { "--id=0", "--query-gpu=memory.free,memory.used", "--format=csv,noheader,nounits" })
            start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("无法启动 nvidia-smi 查询。");
        var output = await process.StandardOutput.ReadToEndAsync();
        var error = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        if (process.ExitCode != 0) throw new InvalidOperationException("nvidia-smi 查询失败：" + error.Trim());
        var values = output.Trim().Split(',').Select(value => long.Parse(value.Trim(), System.Globalization.CultureInfo.InvariantCulture)).ToArray();
        if (values.Length != 2) throw new InvalidDataException("nvidia-smi 显存字段不完整。");
        return new(values[0], values[1]);
    }

    private static async Task MonitorGpuAsync(GpuSampleAccumulator samples, CancellationTokenSource cancellation, StopReason stopReason)
    {
        try
        {
            while (!cancellation.IsCancellationRequested)
            {
                await Task.Delay(1000, cancellation.Token);
                var snapshot = await ReadGpuSnapshotAsync();
                samples.Observe(snapshot);
                if (snapshot.FreeMiB < MinimumRuntimeGpuFreeMiB)
                {
                    stopReason.LowGpuMemory = true;
                    cancellation.Cancel();
                    return;
                }
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch
        {
            stopReason.GpuReadFailed = true;
            cancellation.Cancel();
        }
    }

    private static async Task EnsureGitCommitExistsAsync(string repoRoot, string commit)
    {
        var start = new ProcessStartInfo("git.exe") { UseShellExecute = false, RedirectStandardError = true, CreateNoWindow = true };
        foreach (var arg in new[] { "-C", repoRoot, "cat-file", "-e", commit + "^{commit}" }) start.ArgumentList.Add(arg);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("无法启动本地 Git 基线校验。");
        var error = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        if (process.ExitCode != 0) throw new InvalidDataException("评测固定基线在本地 Git 对象库中不存在：" + error.Trim());
    }

    private static async Task ExportGitArchiveAsync(string repoRoot, string commit, string archivePath, CancellationToken token)
    {
        var start = new ProcessStartInfo("git.exe")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        foreach (var arg in new[] { "-C", repoRoot, "archive", "--format=tar", commit }) start.ArgumentList.Add(arg);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("无法启动固定基线导出。");
        await using (var archive = new FileStream(archivePath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            var copy = process.StandardOutput.BaseStream.CopyToAsync(archive, token);
            var error = process.StandardError.ReadToEndAsync(token);
            await Task.WhenAll(copy, error, process.WaitForExitAsync(token));
            if (process.ExitCode != 0) throw new InvalidDataException("无法导出固定 Git 基线：" + error.Result.Trim());
        }
    }

    private static void ExtractSafeGitArchive(string archivePath, string destinationRoot)
    {
        Directory.CreateDirectory(destinationRoot);
        var root = Path.GetFullPath(destinationRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        using var file = File.OpenRead(archivePath);
        using var reader = new TarReader(file, leaveOpen: false);
        TarEntry? entry;
        while ((entry = reader.GetNextEntry()) is not null)
        {
            if (entry.EntryType is TarEntryType.GlobalExtendedAttributes or TarEntryType.ExtendedAttributes)
                continue;
            var relative = entry.Name.Replace('/', Path.DirectorySeparatorChar).TrimEnd(Path.DirectorySeparatorChar);
            if (string.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative)
                || relative.Split(Path.DirectorySeparatorChar).Any(part => part is "" or "." or ".."))
                throw new InvalidDataException("Git archive 含有越界路径；拒绝解包。");
            var path = Path.GetFullPath(Path.Combine(destinationRoot, relative));
            if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Git archive 路径越界。");
            if (entry.EntryType == TarEntryType.Directory)
            {
                Directory.CreateDirectory(path);
                continue;
            }
            if (entry.EntryType is not (TarEntryType.RegularFile or TarEntryType.V7RegularFile) || entry.DataStream is null)
                throw new InvalidDataException($"Git archive 包含不支持的条目类型 {entry.EntryType}：{entry.Name}；拒绝解包。");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            entry.DataStream.CopyTo(output);
        }
    }

    private static void CopyTaskTargets(string baselineRoot, string projectRoot, IReadOnlyList<string> targetFiles)
    {
        Directory.CreateDirectory(projectRoot);
        var sourceBoundary = Path.GetFullPath(baselineRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        foreach (var relative in targetFiles)
        {
            var safe = ValidateRelativePath(relative);
            var source = Path.GetFullPath(Path.Combine(baselineRoot, safe));
            if (!source.StartsWith(sourceBoundary, StringComparison.OrdinalIgnoreCase) || !File.Exists(source))
                throw new InvalidDataException("题目目标文件不在固定基线中：" + relative);
            var destination = Path.Combine(projectRoot, safe);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(source, destination, overwrite: false);
        }
    }

    private static void CopyRepairTarget(string datasetRoot, string projectRoot)
    {
        var fixtureRoot = Path.Combine(datasetRoot, "repair-fixture");
        var destination = Path.Combine(projectRoot, "repair-fixture");
        Directory.CreateDirectory(destination);
        File.Copy(Path.Combine(fixtureRoot, "RepairFunctions.cs"), Path.Combine(destination, "RepairFunctions.cs"), overwrite: false);
    }

    private static IReadOnlyList<string> FindChangedFiles(string baselineRoot, string workspaceRoot)
    {
        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in Directory.EnumerateFiles(baselineRoot, "*", SearchOption.AllDirectories))
            files.Add(Path.GetRelativePath(baselineRoot, path).Replace(Path.DirectorySeparatorChar, '/'));
        foreach (var path in Directory.EnumerateFiles(workspaceRoot, "*", SearchOption.AllDirectories))
            files.Add(Path.GetRelativePath(workspaceRoot, path).Replace(Path.DirectorySeparatorChar, '/'));
        return files.Where(relative =>
        {
            var before = Path.Combine(baselineRoot, relative.Replace('/', Path.DirectorySeparatorChar));
            var after = Path.Combine(workspaceRoot, relative.Replace('/', Path.DirectorySeparatorChar));
            return !File.Exists(before) || !File.Exists(after)
                || !string.Equals(Sha256File(before), Sha256File(after), StringComparison.OrdinalIgnoreCase);
        }).Order(StringComparer.Ordinal).ToArray();
    }

    private static async Task<DotNetTestExecutionResult> RunFixtureAsync(string repoRoot, string datasetRoot,
        string workspacePath, CancellationToken token)
    {
        var fixtureRoot = Path.Combine(datasetRoot, "repair-fixture");
        var workspaceFixtureRoot = Path.Combine(workspacePath, "repair-fixture");
        foreach (var name in new[] { "Program.cs", "RepairFixture.csproj" })
        {
            var target = Path.Combine(workspaceFixtureRoot, name);
            if (File.Exists(target)) throw new InvalidDataException("模型工作区意外包含评测夹具文件；拒绝执行。");
            File.Copy(Path.Combine(fixtureRoot, name), target, overwrite: false);
        }
        var runner = new DotNetTestRunner(repoRoot);
        var executable = runner.ExecutablePath ?? throw new InvalidOperationException("仓库固定的 dotnet SDK 不可用。");
        var verificationRoot = Path.Combine(workspacePath, ".v");
        return await runner.RunOfflineRepairFixtureAsync(workspacePath, verificationRoot, executable, token);
    }

    private static string GetAggregateResultPath(string datasetVersion)
    {
        if (datasetVersion is not ("coding-zh-v3" or "coding-zh-v4"))
            throw new ArgumentException("结果目录只允许固定的 v3 或 v4 评测版本。");
        var root = Path.GetFullPath(Path.Combine("D:\\XiaoK\\Evaluations\\runs", datasetVersion));
        Directory.CreateDirectory(root);
        return Path.Combine(root, "results.jsonl");
    }

    private static void AppendAggregate(string path, AggregateResult result)
    {
        EnsureTaskNotAlreadyScored(path, result.DatasetVersion, result.BaselineCommit, result.ModelId, result.ModelRevision,
            result.PipelineVersion ?? "", result.TaskId);
        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(result, JsonOptions) + "\n");
        using var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read);
        stream.Write(bytes);
        stream.Flush(flushToDisk: true);
    }

    private static void EnsureTaskNotAlreadyScored(string path, string datasetVersion, string baselineCommit,
        string modelId, string modelRevision, string pipelineVersion, string taskId)
    {
        if (!File.Exists(path)) return;
        foreach (var line in File.ReadLines(path, Encoding.UTF8))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            var existing = JsonSerializer.Deserialize<AggregateResult>(line, JsonOptions)
                ?? throw new InvalidDataException("已有评分摘要包含无效 JSON；拒绝追加，避免重复或覆盖评分。");
            if (existing.DatasetVersion == datasetVersion && existing.BaselineCommit == baselineCommit
                && existing.ModelId == modelId
                && existing.ModelRevision == modelRevision && existing.PipelineVersion == pipelineVersion
                && existing.TaskId == taskId)
                throw new InvalidOperationException($"题目 {taskId} 已在此模型 revision 和固定基线上评分；拒绝重复运行或覆盖评分。");
        }
    }

    private static string ValidateRelativePath(string relative)
    {
        if (Path.IsPathRooted(relative) || relative.Split('/', '\\').Any(part => part is "" or "." or ".."))
            throw new InvalidDataException("题目目标路径无效。");
        return relative.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);
    }

    private static string RequireLocalDirectory(string value, string label)
    {
        if (string.IsNullOrWhiteSpace(value) || value.StartsWith("\\\\", StringComparison.Ordinal))
            throw new ArgumentException(label + "必须是本机目录路径。");
        var path = Path.GetFullPath(value);
        if (!Directory.Exists(path)) throw new DirectoryNotFoundException(label + "不存在：" + path);
        return Path.TrimEndingDirectorySeparator(path);
    }

    private static string RequireSha256(string value, string label)
    {
        if (value.Length != 64 || value.Any(character => !Uri.IsHexDigit(character)))
            throw new InvalidDataException(label + "不是有效 SHA-256。");
        return value;
    }

    private static string RequireGitCommit(string value)
    {
        if (value.Length != 40 || value.Any(character => !Uri.IsHexDigit(character)))
            throw new InvalidDataException("评测基线不是有效的 40 位 Git 提交 ID。");
        return value;
    }

    private static string Sha256File(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }
    private static string Sha256(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static void PrintBounded(string title, string value, int maximumCharacters)
    {
        Console.WriteLine("--- " + title + " ---");
        Console.WriteLine(value.Length <= maximumCharacters ? value : value[..maximumCharacters] + "\n（输出过长，控制台仅显示前缀。）");
        Console.WriteLine("--- 结果结束 ---");
    }

    private static bool TryDeleteOwnedDirectory(string path)
    {
        try
        {
            var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.Combine("D:\\XiaoK\\Evaluations", "scratch")));
            var target = Path.GetFullPath(path);
            var leaf = Path.GetFileName(target);
            if (!string.Equals(Path.GetDirectoryName(target), root, StringComparison.OrdinalIgnoreCase)
                || leaf.Length != 11 || !leaf.StartsWith("xk-", StringComparison.Ordinal)
                || leaf.Skip(3).Any(character => !Uri.IsHexDigit(character)))
                return false;
            if (!Directory.Exists(target)) return true;
            if ((File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0
                || (File.GetAttributes(target) & FileAttributes.ReparsePoint) != 0)
                return false;
            Directory.Delete(target, recursive: true);
            return !Directory.Exists(target);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { return false; }
    }

    private static void TryDeleteOwnedFile(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private sealed class KeepPatchAndDenyAllPresenter : IApprovalPresenter, ICodeTaskReviewPresenter
    {
        public Task<bool> ConfirmAsync(string actionId, string title, string details, CancellationToken cancellationToken) => Task.FromResult(false);
        public Task<CodeTaskReviewDecision> ReviewAsync(string projectPath, string workspacePath, string diff,
            string? dotNetTestTarget, string? commandPreview, CancellationToken cancellationToken) =>
            Task.FromResult(CodeTaskReviewDecision.KeepPatch);
    }

    private sealed class StopReason { public bool UserCancelled; public bool LowGpuMemory; public bool GpuReadFailed; }

    private sealed class GpuSampleAccumulator(GpuSnapshot initial)
    {
        private readonly object _gate = new();
        public long MinimumFreeMiB { get; private set; } = initial.FreeMiB;
        public long PeakUsedMiB { get; private set; } = initial.UsedMiB;
        public void Observe(GpuSnapshot sample)
        {
            lock (_gate)
            {
                MinimumFreeMiB = Math.Min(MinimumFreeMiB, sample.FreeMiB);
                PeakUsedMiB = Math.Max(PeakUsedMiB, sample.UsedMiB);
            }
        }
    }

    private sealed record GpuSnapshot(long FreeMiB, long UsedMiB);
    private sealed record BenchmarkOptions(string RepositoryRoot, string DatasetVersion, string TaskId,
        string ModelId, bool EnableThinking);
    private sealed record BenchmarkTask(string Id, string Category, string Acceptance, string Grading,
        bool NetworkAllowed, bool ExternalSideEffectsAllowed, IReadOnlyList<string> TargetFiles, string Prompt);
    private sealed record ReviewKey(string Expected, string Evidence);
    private sealed record DatasetLock(int SchemaVersion, string ManifestSha256, string TaskFileName, string ReviewFileName,
        int LockedFileCount);
    private sealed record BenchmarkManifest(int SchemaVersion, string Version, string BaselineCommit, int TaskCount,
        CategoryCounts Categories, IReadOnlyList<LockedFile> Files);
    private sealed record CategoryCounts(int R, int S, int M, int F);
    private sealed record LockedFile(string Path, long SizeBytes, string Sha256);
    private sealed record ModelLock(IReadOnlyList<ModelRecord> Models);
    private sealed record ModelRecord(string Id, string Revision, string Status, string LocalDirectory, IReadOnlyList<ModelFile> Files);
    private sealed record ModelFile(string Name, long UpstreamReportedSizeBytes, string LocalVerifiedSha256);
    private sealed record RuntimeLock(IReadOnlyList<RuntimeRecord> Runtimes);
    private sealed record RuntimeRecord(string Id, string Version, string Status, IReadOnlyList<RuntimeFile> StagedFiles);
    private sealed record RuntimeFile(string Name, long SizeBytes, string Sha256);
    private sealed record ModelProfile(string ModelFileName, int GpuLayers, long ExpectedGpuMemoryMiB,
        long MinimumInitialGpuFreeMiB, bool EvaluationCandidate);
    private sealed record ModelConfig(string Id, string Revision, string Root, string ModelFile, string ModelSha256,
        string RuntimeRoot, string RuntimeVersion, string RuntimeSha256, IReadOnlyList<RuntimeFile> RuntimeFiles,
        int ContextTokens, int GpuLayers, long ExpectedGpuMemoryMiB, long MinimumInitialGpuFreeMiB,
        bool EvaluationCandidate);
    private sealed record RuntimeManifest(int SchemaVersion, string RuntimeVersion, string RuntimeSha256, string ModelId,
        string ModelSha256, int ContextTokens, int GpuLayers, long ExpectedGpuMemoryMiB);
    private sealed record AggregateResult(int SchemaVersion, string? PipelineVersion, int ResponseCount,
        int PromptTokens, int CompletionTokens, int EmptyContentResponses, string? FinishReasons,
        string RunId, DateTimeOffset FinishedAtUtc,
        string DatasetVersion, string BaselineCommit, string TaskId, string Category, string ModelId,
        string ModelRevision, string ModelSha256, string RuntimeVersion, double DurationMilliseconds,
        long InitialGpuFreeMiB, long MinimumGpuFreeMiB, long PeakGpuUsedMiB, bool ToolSucceeded,
        bool ScopePassed, int? OfflineRestoreExitCode, int? FixtureExitCode, bool ReviewerPassed, string OutputSha256);
}
