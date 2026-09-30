using System.IO;
using System.Net.Http;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Text.Json;
using XiaoK.Adapters.Windows;
using XiaoK.Core;
using XiaoK.Inference;
using XiaoK.Storage;
using XiaoK.Tools;
using XiaoK.Voice;

namespace XiaoK.Host;

internal sealed class AssistantRuntime : IAsyncDisposable
{
    private readonly DateTimeOffset _processStartedAtUtc = DateTimeOffset.UtcNow;
    private readonly XiaoKSettings _settings;
    private readonly SqliteTaskStore _store;
    private readonly LocalInferenceClient _inference;
    private readonly IManagedModelRuntime? _managedModelRuntime;
    private readonly DotNetTestRunner _dotNetTestRunner;
    private readonly AudioGateway _voice = new();
    private readonly ToolBroker _broker;
    private readonly ModelBroker _models;
    private readonly SemaphoreSlim _executionGate = new(1, 1);
    private CancellationTokenSource? _active;
    private int _stopping;

    public AssistantRuntime(IApprovalPresenter approval)
    {
        _settings = XiaoKSettings.Load();
        Directory.CreateDirectory(_settings.DataRoot);
        _store = new SqliteTaskStore(Path.Combine(_settings.DataRoot, "tasks.sqlite3"),
            Path.Combine(_settings.DataRoot, "tasks.json"), _settings.ContactReplyStyles,
            _settings.ContactStylesMigrationSourceAvailable);
        _inference = new LocalInferenceClient(_settings.InferenceEndpoint);
        IManagedModelRuntime? managedRuntime = null;
        try
        {
            managedRuntime = LlamaCppModelRuntime.TryLoad(_settings.ModelRoot, _settings.InferenceEndpoint);
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or InvalidDataException or JsonException or NotSupportedException)
        {
            managedRuntime = new UnavailableModelRuntime("托管模型配置无效或版本未锁定；本次模型请求已禁用。请检查模型目录中的 llama-runtime.json。");
        }
        _managedModelRuntime = managedRuntime;
        _models = new ModelBroker(managedRuntime);
        var apps = _settings.Applications.Select(x => new DesktopApp(x.Id, x.Executable, x.WorkingDirectory));
        var roots = _settings.SearchRoots.Select(x => new KeyValuePair<string, string>(x.Id, x.Path));
        _dotNetTestRunner = new DotNetTestRunner(XiaoKSettings.FindWorkspace(AppContext.BaseDirectory));
        var codeAgent = new CodeTaskAgent(_inference, _models, XiaoKSettings.FindWorkspace(AppContext.BaseDirectory), _dotNetTestRunner);
        _broker = new ToolBroker(new WindowsDesktopTools(apps, roots), _inference, _models, approval,
            codeAgent, _settings.CodeProjectRoot, _settings.CodeWorkspaceRoot);
    }

    public string ModelStatus => _managedModelRuntime?.Status
        ?? "本地模型：" + _settings.InferenceEndpoint + "（仅回环地址；手动运行本地服务；未连接时不会转云端）";
    public string VoiceStatus => _voice.Availability == VoiceAvailability.NotConfigured ? "语音：运行时尚未安装；麦克风未采集" : "语音：" + _voice.Availability;
    public string? StartupIsolationNotice => !_dotNetTestRunner.StartupIsolationRecovery.Success
        || _dotNetTestRunner.StartupIsolationRecovery.RecoveredProfiles > 0
        ? _dotNetTestRunner.StartupIsolationRecovery.Message
        : null;
    public XiaoKSettings CurrentSettings => _settings;
    public string ActiveDatabasePath => Path.Combine(_settings.DataRoot, "tasks.sqlite3");

    public async Task<IReadOnlyList<TaskHistoryEntry>> GetRecentTaskHistoryAsync(CancellationToken cancellationToken)
    {
        var records = (await _store.GetRecentAsync(30, cancellationToken))
            .Select(record => TaskHistoryRecoveryPolicy.ForDisplay(record, _processStartedAtUtc));
        var history = records.Select(record => new TaskHistoryEntry(
            $"{record.Summary} · {record.Id.ToString("N")[..8]}",
            TaskStateLabel(record.Status), record.UpdatedAtUtc,
            record.ErrorCode == TaskHistoryRecoveryPolicy.HostRestartedErrorCode
                ? "小K在上次任务完成前退出；不会自动重试。请手动核对相关应用或项目状态。"
                : record.ErrorCode is null ? "仅保留任务状态，不保存请求正文或模型回答。" : $"错误类别：{record.ErrorCode}"))
            .ToList();

        var codeTasks = await Task.Run(() => CodeTaskAgent.ReadRetainedTasks(_settings.CodeWorkspaceRoot), cancellationToken);
        history.AddRange(codeTasks.Select(task =>
        {
            var interrupted = TaskHistoryRecoveryPolicy.IsInterruptedCodeTask(
                task.State, task.UpdatedAtUtc, _processStartedAtUtc);
            return new TaskHistoryEntry(
                $"隔离编程任务 · {task.TaskId[^8..]}",
                interrupted ? "上次中断，需核对" : CodeTaskStateLabel(task.State), task.UpdatedAtUtc,
                interrupted
                    ? $"小K不会自动续跑。请检查隔离工作区后手动决定下一步：{task.WorkspacePath}"
                    : $"隔离工作区：{task.WorkspacePath}");
        }));

        return history.OrderByDescending(item => item.UpdatedAtUtc).Take(35).ToArray();
    }

    public async Task<string> SubmitAsync(string input)
    {
        if (Volatile.Read(ref _stopping) != 0) return "小K正在退出，暂不接受新任务。";
        var request = input.Trim();
        if (request.Length == 0) return "先输入一句话，或用 Ctrl+Shift+K 打开小K。";

        var operation = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        var previous = Interlocked.Exchange(ref _active, operation);
        try { previous?.Cancel(); }
        catch (ObjectDisposedException) { }
        var token = operation.Token;
        var gateEntered = false;
        TaskRecord? task = null;

        try
        {
            await _executionGate.WaitAsync(token);
            gateEntered = true;
            if (Volatile.Read(ref _stopping) != 0) return "小K正在退出，暂不接受新任务。";
            var now = DateTimeOffset.UtcNow;
            var category = Classify(request);
            task = new TaskRecord(Guid.NewGuid(), category, CategoryLabel(category), TaskLifecycleState.Planning, now, now);
            if (!await TrySaveStateAsync(task, token))
                return "无法保存本地任务状态，本次操作未执行。请检查 D 盘数据目录。";

            var result = await RouteAsync(category, request, token);
            task = task with
            {
                Status = result.FinalState ?? (result.Success ? TaskLifecycleState.Completed : TaskLifecycleState.Failed),
                UpdatedAtUtc = DateTimeOffset.UtcNow,
                // The detailed chat body/result remains transient and is never persisted.
                ErrorCode = result.ErrorCode
            };
            var saved = await TrySaveStateAsync(task, CancellationToken.None);
            var output = result.Success
                ? result.FinalState == TaskLifecycleState.AwaitingApproval ? $"{result.Summary}{Environment.NewLine}{Environment.NewLine}{result.Data}" : result.Data ?? result.Summary
                : $"{result.Summary}{(result.ErrorCode is null ? "" : $" [{result.ErrorCode}]")}";
            return saved ? output : output + "（任务结果未写入本地历史）";
        }
        catch (OperationCanceledException)
        {
            if (task is not null)
            {
                task = task with { Status = TaskLifecycleState.Cancelled, UpdatedAtUtc = DateTimeOffset.UtcNow, ErrorCode = "CANCELLED" };
                await TrySaveStateAsync(task, CancellationToken.None);
            }
            return "已取消当前任务。";
        }
        catch (Exception)
        {
            if (task is not null)
            {
                task = task with { Status = TaskLifecycleState.Failed, UpdatedAtUtc = DateTimeOffset.UtcNow, ErrorCode = "INTERNAL" };
                await TrySaveStateAsync(task, CancellationToken.None);
            }
            return "任务失败。为保护隐私，故障内容未写入日志。";
        }
        finally
        {
            if (gateEntered) _executionGate.Release();
            Interlocked.CompareExchange(ref _active, null, operation);
            operation.Dispose();
        }
    }

    public void CancelCurrent()
    {
        StopMicrophone();
        try { Volatile.Read(ref _active)?.Cancel(); }
        catch (ObjectDisposedException) { }
    }

    public bool StopMicrophone()
    {
        var wasCapturing = _voice.IsCapturing;
        _voice.StopImmediately();
        return wasCapturing;
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _stopping, 1) != 0) return;
        CancelCurrent();
        await _executionGate.WaitAsync();
        try
        {
            if (_managedModelRuntime is not null) await _managedModelRuntime.DisposeAsync();
        }
        finally
        {
            _inference.Dispose();
            _executionGate.Release();
        }
    }
    private async Task<ToolResult> RouteAsync(string category, string request, CancellationToken token)
    {
        var lower = request.ToLowerInvariant();
        if (category == "app")
        {
            var intent = AppLaunchIntentResolver.Resolve(request);
            if (intent is null)
                return new(false, "无法确定要打开哪个受支持的应用。当前支持 VS Code 小K项目、Edge、资源管理器、微信和 QQ。", "APP_NOT_SUPPORTED");
            var args = ImmutableDictionary<string, string>.Empty.Add("app_id", intent.AppId);
            if (intent.WorkspaceId is not null) args = args.Add("workspace_id", intent.WorkspaceId);
            return await _broker.ExecuteAsync(new ToolProposal("app.launch.v1", args, intent.AppId, "对应应用窗口可见"), token);
        }

        if (category == "file")
        {
            var query = request;
            foreach (var prefix in new[] { "帮我找文件", "搜索文件", "查找文件", "找文件", "搜索", "查找" })
                if (query.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) { query = query[prefix.Length..].Trim(' ', '：', ':', '“', '”', '"'); break; }
            return await _broker.ExecuteAsync(new ToolProposal("file.search.v1",
                ImmutableDictionary<string, string>.Empty.Add("query", query).Add("root_id", "user-files"), "user-files", "列出最多十个文件名匹配项"), token);
        }

        if (category == "analyze" || category == "draft")
        {
            var body = ExtractPayload(request, category == "draft"
                ? new[] { "帮我回复", "起草回复", "回复草稿", "帮我回" }
                : new[] { "分析消息", "分析聊天", "理解聊天", "解释这条消息", "分析" });
            if (body.Length == 0) return new(false, "请在冒号后粘贴要分析的单条消息。后台通知接入后，小K只会分析可见正文。", "EMPTY_MESSAGE");
            var tool = category == "draft" ? "message.draft.v1" : "message.analyze.v1";
            var key = category == "draft" ? "draft" : "message";
            var arguments = ImmutableDictionary<string, string>.Empty.Add(key, body);
            if (category == "draft")
            {
                var styleId = ContactReplyStyleCatalog.DefaultStyleId;
                if (TryExtractDraftContact(request, out var contactName))
                {
                    if (!ContactReplyStyleCatalog.TryNormalizeContactName(contactName, out var normalizedContact))
                        return new(false, "联系人名称无效；请使用设置中保存的名称。", "INVALID_DRAFT_CONTACT");
                    var preferences = await _store.GetContactReplyStylesAsync(token);
                    styleId = ContactReplyStyleCatalog.FindStyleForContact(preferences, normalizedContact)
                        ?? string.Empty;
                    if (styleId.Length == 0)
                        return new(false, $"没有为“{normalizedContact}”保存回复风格。请先到设置中添加并确认该联系人的风格。", "DRAFT_STYLE_NOT_CONFIGURED");
                }
                arguments = arguments.Add("style_id", styleId);
            }

            return await _broker.ExecuteAsync(new ToolProposal(tool, arguments,
                "用户本次提供的单条消息", "本地生成分析或草稿；不发送"), token);
        }

        if (category == "send")
            return new(false, "发送必须经过最终预览，列出收件人、正文和附件并确认。微信/QQ发送适配器尚未接入，因此当前不会发送。", "SEND_ADAPTER_UNAVAILABLE");

        if (category == "code")
            return await _broker.ExecuteAsync(new ToolProposal("code.task.create.v1",
                ImmutableDictionary<string, string>.Empty.Add("instruction", request), "configured-project", "在仓库外隔离副本中生成可审阅 diff"), token);

        try
        {
            var answer = await _models.RunInteractiveAsync(
                inner => _inference.CompleteAsync("你是运行在用户本机的小K桌面助手。只回答或提出建议，不声称已操作电脑。没有工具授权时不要声称操作完成。", request, inner), token);
            return new(true, answer);
        }
        catch (ModelQueueFullException) { return new(false, "本地模型请求过多；当前请求未排队。", "RESOURCE_BUSY"); }
        catch (LowGpuMemoryException) { return new(false, "可用独显显存不足或读数不可用；小K已拒绝启动模型，避免挤占系统至少 1 GiB 显存余量。", "LOW_VRAM"); }
        catch (ModelRuntimeUnavailableException) { return new(false, "本地模型清单、程序或权重校验失败；没有向模型发送请求。", "MODEL_RUNTIME_UNAVAILABLE"); }
        catch (HttpRequestException) { return new(false, "本地推理服务未运行或不可用；没有云端回退。", "LOCAL_MODEL_OFFLINE"); }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { return new(false, "本地推理超时；没有调用云端服务。", "LOCAL_MODEL_TIMEOUT"); }
        catch (OperationCanceledException) { throw; }
        catch (Exception) { return new(false, "无法读取本地模型响应。", "LOCAL_MODEL_INVALID_RESPONSE"); }
    }

    private async Task<bool> TrySaveStateAsync(TaskRecord task, CancellationToken token)
    {
        // Category only: never persist user prompts, notification bodies, model replies or attachments.
        try
        {
            await _store.SaveAsync(task, token);
            return true;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception) { return false; }
    }

    private static string Classify(string request)
    {
        var lower = request.ToLowerInvariant();
        if (lower.StartsWith("找文件") || lower.StartsWith("查找文件") || lower.StartsWith("搜索文件") || lower.StartsWith("搜索") || lower.StartsWith("帮我找文件")) return "file";
        if (lower.StartsWith("分析消息") || lower.StartsWith("分析聊天") || lower.StartsWith("理解聊天") || lower.StartsWith("解释这条消息") || lower.StartsWith("分析：") || lower.StartsWith("分析:")) return "analyze";
        if (lower.StartsWith("帮我回复") || lower.StartsWith("起草回复") || lower.StartsWith("回复草稿") || lower.StartsWith("帮我回")) return "draft";
        if (lower.StartsWith("发送") || lower.StartsWith("发给")) return "send";
        if (lower.Contains("写代码") || lower.Contains("改代码") || lower.Contains("开发任务") || lower.Contains("编程任务")) return "code";
        if (lower.StartsWith("打开") || lower.StartsWith("启动")) return "app";
        return "chat";
    }

    private static string CategoryLabel(string category) => category switch
    {
        "app" => "应用操作", "file" => "文件查找", "analyze" => "消息分析", "draft" => "回复草稿",
        "send" => "发送请求", "code" => "本地编程任务", _ => "本地对话"
    };

    private static string TaskStateLabel(TaskLifecycleState state) => state switch
    {
        TaskLifecycleState.Queued => "排队中", TaskLifecycleState.Planning => "规划中",
        TaskLifecycleState.AwaitingApproval => "等待审阅", TaskLifecycleState.Running => "运行中",
        TaskLifecycleState.Verifying => "核验中", TaskLifecycleState.Completed => "已完成",
        TaskLifecycleState.Failed => "失败", TaskLifecycleState.Cancelled => "已取消",
        TaskLifecycleState.OutcomeUncertain => "结果待核对", _ => "未知状态"
    };

    private static string CodeTaskStateLabel(string state) => state switch
    {
        "planning" => "规划中", "running" => "生成中", "awaiting_approval" => "等待审阅",
        "failed" => "失败", "cancelled" => "已取消", _ => "未知状态"
    };

    private static string ExtractPayload(string request, IEnumerable<string> prefixes)
    {
        var colon = request.IndexOfAny(['：', ':']);
        if (colon >= 0) return request[(colon + 1)..].Trim();
        foreach (var prefix in prefixes)
            if (request.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return request[prefix.Length..].Trim(' ', '，', ',', '：', ':');
        return string.Empty;
    }

    public Task<IReadOnlyList<ContactReplyStylePreference>> GetContactReplyStylesAsync(CancellationToken cancellationToken) =>
        _store.GetContactReplyStylesAsync(cancellationToken);

    public Task ReplaceContactReplyStylesAsync(IEnumerable<ContactReplyStylePreference> preferences,
        CancellationToken cancellationToken) => _store.ReplaceContactReplyStylesAsync(preferences, cancellationToken);

    public Task RecordApprovalAuditAsync(string actionId, string outcome, CancellationToken cancellationToken) =>
        _store.AppendApprovalAuditAsync(actionId, outcome, cancellationToken);

    public Task<IReadOnlyList<ApprovalAuditRecord>> GetRecentApprovalAuditAsync(int count, CancellationToken cancellationToken) =>
        _store.GetRecentApprovalAuditAsync(count, cancellationToken);

    public Task<string> CreateDatabaseBackupAsync(string backupPath, CancellationToken cancellationToken) =>
        _store.CreateBackupAsync(backupPath, cancellationToken);

    public async Task<string> RestoreDatabaseBackupAsync(string backupPath, CancellationToken cancellationToken)
    {
        if (!await _executionGate.WaitAsync(0, cancellationToken))
            throw new InvalidOperationException("当前有任务运行；请等待任务结束后再恢复数据库。");
        try { return await _store.RestoreBackupAsync(backupPath, cancellationToken); }
        finally { _executionGate.Release(); }
    }

    public async Task<LocalDataCleanupPreview> GetLocalDataCleanupPreviewAsync(CancellationToken cancellationToken)
    {
        if (!await _executionGate.WaitAsync(0, cancellationToken))
            throw new InvalidOperationException("当前有任务运行；请等待任务结束后再预览本地数据清理。");
        try { return await BuildLocalDataCleanupPreviewAsync(cancellationToken); }
        finally { _executionGate.Release(); }
    }

    public async Task<LocalDataCleanupResult> ClearLocalDataAsync(LocalDataCleanupPreview approvedPreview,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(approvedPreview);
        if (!await _executionGate.WaitAsync(0, cancellationToken))
            throw new InvalidOperationException("当前有任务运行；请等待任务结束后再清理本地数据。");
        try
        {
            var currentPreview = await BuildLocalDataCleanupPreviewAsync(cancellationToken);
            if (!CleanupPreviewMatches(approvedPreview, currentPreview))
                throw new InvalidOperationException("本地数据在确认期间发生变化；没有清理，请重新预览。");

            var databaseCompacted = await _store.ClearPersonalDataAsync(cancellationToken);
            var settingsPropertyRemoved = false;
            string? settingsCleanupError = null;
            try
            {
                settingsPropertyRemoved = LegacyContactStylesPrivacyCleanup.RemoveIfUnchanged(
                    XiaoKSettings.GetSettingsPath(), approvedPreview.LegacySettings);
            }
            catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException
                or InvalidDataException or InvalidOperationException or NotSupportedException or System.Text.Json.JsonException)
            {
                settingsCleanupError = ex.Message;
            }

            var fileResult = ManagedPrivacyFileCleanup.DeleteIfUnchanged(approvedPreview.ManagedFiles);
            return new(databaseCompacted, settingsPropertyRemoved,
                approvedPreview.LegacySettings.HasContactStylesProperty,
                fileResult.DeletedCount, approvedPreview.ManagedFiles.SkippedEntries,
                fileResult.FailedFileNames, fileResult.PlanChanged, settingsCleanupError);
        }
        finally { _executionGate.Release(); }
    }

    private async Task<LocalDataCleanupPreview> BuildLocalDataCleanupPreviewAsync(CancellationToken cancellationToken)
    {
        var database = await _store.GetPersonalDataSummaryAsync(cancellationToken);
        var settings = LegacyContactStylesPrivacyCleanup.Preview(XiaoKSettings.GetSettingsPath());
        var managedFiles = ManagedPrivacyFileCleanup.Preview(_settings.DataRoot,
            Path.Combine(_settings.DataRoot, "tasks.json"));
        return new(database, settings, managedFiles);
    }

    private static bool CleanupPreviewMatches(LocalDataCleanupPreview approved, LocalDataCleanupPreview current) =>
        approved.Database == current.Database
        && approved.LegacySettings == current.LegacySettings
        && string.Equals(approved.ManagedFiles.RootPath, current.ManagedFiles.RootPath, StringComparison.OrdinalIgnoreCase)
        && string.Equals(approved.ManagedFiles.LegacyTasksJsonPath, current.ManagedFiles.LegacyTasksJsonPath, StringComparison.OrdinalIgnoreCase)
        && approved.ManagedFiles.SkippedEntries == current.ManagedFiles.SkippedEntries
        && approved.ManagedFiles.Files.SequenceEqual(current.ManagedFiles.Files);

    private static bool TryExtractDraftContact(string request, out string? contactName)
    {
        contactName = null;
        var colon = request.IndexOfAny(['：', ':']);
        if (colon < 0) return false;
        var header = request[..colon].Trim();
        foreach (var prefix in new[] { "帮我回复给", "帮我回给", "起草回复给", "回复草稿给" })
        {
            if (!header.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
            contactName = header[prefix.Length..].Trim();
            return true;
        }
        return false;
    }
}

internal sealed record XiaoKSettings
{
    internal bool ContactStylesMigrationSourceAvailable { get; init; } = true;
    public string DataRoot { get; init; } = @"D:\XiaoK\Data";
    public string ModelRoot { get; init; } = @"D:\XiaoK\Models";
    public string EvaluationRoot { get; init; } = @"D:\XiaoK\Evaluations";
    public string CodeProjectRoot { get; init; } = "";
    public string CodeWorkspaceRoot { get; init; } = @"D:\XiaoK\Workspaces";
    public double? PetWindowLeft { get; init; }
    public double? PetWindowTop { get; init; }
    public int? PetWindowLeftPixels { get; init; }
    public int? PetWindowTopPixels { get; init; }
    public string InferenceEndpoint { get; init; } = "http://127.0.0.1:8080/";
    public bool MonitorWeChatNotifications { get; init; }
    public bool MonitorQQNotifications { get; init; }
    public List<string> WeChatPublisherAppIds { get; init; } = [];
    public List<string> QQPublisherAppIds { get; init; } = [];
    public List<ContactReplyStylePreference> ContactReplyStyles { get; init; } = [];
    public List<AppSetting> Applications { get; init; } = [];
    public List<RootSetting> SearchRoots { get; init; } = [];

    public static XiaoKSettings Load()
    {
        var defaults = CreateDefaults();
        var path = GetSettingsPath();
        if (!File.Exists(path)) return defaults;
        try
        {
            var loaded = JsonSerializer.Deserialize<XiaoKSettings>(File.ReadAllText(path), new JsonSerializerOptions(JsonSerializerDefaults.Web));
            if (loaded is null) return defaults;
            if (!Uri.TryCreate(loaded.InferenceEndpoint, UriKind.Absolute, out var uri) || !uri.IsLoopback)
                return defaults with { ContactReplyStyles = SanitizeContactReplyStyles(loaded.ContactReplyStyles) };
            return loaded.WithDefaults(defaults);
        }
        catch (Exception) { return defaults with { ContactStylesMigrationSourceAvailable = false }; }
    }

    public void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(GetSettingsPath())!);
        var path = GetSettingsPath();
        var temporaryPath = path + ".tmp";
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true };
        File.WriteAllText(temporaryPath, JsonSerializer.Serialize(this, options), new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        if (File.Exists(path)) File.Replace(temporaryPath, path, destinationBackupFileName: null);
        else File.Move(temporaryPath, path);
    }

    internal static string GetSettingsPath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "XiaoK", "settings.json");

    private static XiaoKSettings CreateDefaults()
    {
        var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        var documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        var downloads = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
        var workspace = FindWorkspace(AppContext.BaseDirectory) ?? Environment.GetEnvironmentVariable("XIAOK_PROJECT_ROOT") ?? "";
        var code = @"D:\VS Code\Code.exe";
        var edge = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Microsoft", "Edge", "Application", "msedge.exe");
        var apps = new List<AppSetting>
        {
            new("vscode", code, workspace), new("edge", edge, null), new("explorer", "explorer.exe", null),
            new("wechat", @"D:\微信\Weixin\Weixin.exe", null), new("qq", @"D:\QQ\QQ.exe", null)
        };
        var roots = new[] { ("Desktop", desktop), ("Documents", documents), ("Downloads", downloads) }
            .Where(x => !string.IsNullOrWhiteSpace(x.Item2)).Select(x => new RootSetting(x.Item1, x.Item2)).ToList();
        return new XiaoKSettings { Applications = apps, SearchRoots = roots };
    }

    private XiaoKSettings WithDefaults(XiaoKSettings defaults) => this with
    {
        DataRoot = string.IsNullOrWhiteSpace(DataRoot) ? defaults.DataRoot : DataRoot,
        ModelRoot = string.IsNullOrWhiteSpace(ModelRoot) ? defaults.ModelRoot : ModelRoot,
        EvaluationRoot = string.IsNullOrWhiteSpace(EvaluationRoot) ? defaults.EvaluationRoot : EvaluationRoot,
        CodeProjectRoot = CodeProjectRoot ?? "",
        CodeWorkspaceRoot = string.IsNullOrWhiteSpace(CodeWorkspaceRoot) ? defaults.CodeWorkspaceRoot : CodeWorkspaceRoot,
        Applications = Applications.Count == 0 ? defaults.Applications : Applications,
        SearchRoots = SearchRoots.Count == 0 ? defaults.SearchRoots : SearchRoots,
        WeChatPublisherAppIds = WeChatPublisherAppIds ?? [],
        QQPublisherAppIds = QQPublisherAppIds ?? [],
        ContactReplyStyles = SanitizeContactReplyStyles(ContactReplyStyles)
    };

    private static List<ContactReplyStylePreference> SanitizeContactReplyStyles(IEnumerable<ContactReplyStylePreference>? preferences)
    {
        var result = new List<ContactReplyStylePreference>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var preference in preferences ?? [])
        {
            if (preference is null
                || !ContactReplyStyleCatalog.TryNormalizeContactName(preference.ContactName, out var name)
                || !ContactReplyStyleCatalog.IsSupportedStyle(preference.StyleId)
                || !seen.Add(name)) continue;
            result.Add(preference with { ContactName = name, Source = ContactReplyStyleCatalog.UserConfirmedSource });
            if (result.Count >= 200) break;
        }
        return result;
    }

    internal static string? FindWorkspace(string start)
    {
        var current = new DirectoryInfo(start);
        for (var i = 0; current is not null && i < 8; i++, current = current.Parent)
            if (File.Exists(Path.Combine(current.FullName, "XiaoK.sln"))) return current.FullName;
        return null;
    }
}

internal sealed record AppSetting(string Id, string Executable, string? WorkingDirectory);
internal sealed record RootSetting(string Id, string Path);
internal sealed record TaskHistoryEntry(string Title, string State, DateTimeOffset UpdatedAtUtc, string Detail);
internal sealed record LocalDataCleanupPreview(SqlitePersonalDataSummary Database,
    LegacyContactStylesCleanupSnapshot LegacySettings, ManagedPrivacyFilesPlan ManagedFiles);
internal sealed record LocalDataCleanupResult(bool DatabaseCompacted, bool LegacySettingsPropertyRemoved,
    bool LegacySettingsPropertyWasPresent, int DeletedManagedFiles, int SkippedManagedFiles,
    IReadOnlyList<string> FailedManagedFileNames, bool ManagedFilePlanChanged, string? LegacySettingsCleanupError);
