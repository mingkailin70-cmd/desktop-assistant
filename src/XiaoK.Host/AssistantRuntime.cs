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
    private readonly XiaoKSettings _settings;
    private readonly JsonTaskStore _store;
    private readonly LocalInferenceClient _inference;
    private readonly AudioGateway _voice = new();
    private readonly ToolBroker _broker;
    private readonly ModelBroker _models = new();
    private readonly SemaphoreSlim _executionGate = new(1, 1);
    private CancellationTokenSource? _active;
    private int _stopping;

    public AssistantRuntime(IApprovalPresenter approval)
    {
        _settings = XiaoKSettings.Load();
        Directory.CreateDirectory(_settings.DataRoot);
        _store = new JsonTaskStore(Path.Combine(_settings.DataRoot, "tasks.json"));
        _inference = new LocalInferenceClient(_settings.InferenceEndpoint);
        var apps = _settings.Applications.Select(x => new DesktopApp(x.Id, x.Executable, x.WorkingDirectory));
        var roots = _settings.SearchRoots.Select(x => new KeyValuePair<string, string>(x.Id, x.Path));
        var codeAgent = new CodeTaskAgent(_inference, _models, XiaoKSettings.FindWorkspace(AppContext.BaseDirectory));
        _broker = new ToolBroker(new WindowsDesktopTools(apps, roots), _inference, _models, approval,
            codeAgent, _settings.CodeProjectRoot, _settings.CodeWorkspaceRoot);
    }

    public string ModelStatus => "本地模型：" + _settings.InferenceEndpoint + "（仅回环地址；未连接时不会转云端）";
    public string VoiceStatus => _voice.Availability == VoiceAvailability.NotConfigured ? "语音：运行时尚未安装；麦克风未采集" : "语音：" + _voice.Availability;
    public XiaoKSettings CurrentSettings => _settings;

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
        try { _inference.Dispose(); }
        finally { _executionGate.Release(); }
    }
    private async Task<ToolResult> RouteAsync(string category, string request, CancellationToken token)
    {
        var lower = request.ToLowerInvariant();
        if (category == "app")
        {
            var appId = lower.Contains("微信") || lower.Contains("wechat") ? "wechat" :
                        lower.Contains("qq") || request.Contains("ＱＱ", StringComparison.OrdinalIgnoreCase) ? "qq" :
                        lower.Contains("edge") || lower.Contains("浏览器") ? "edge" :
                        lower.Contains("资源管理器") || lower.Contains("文件夹") ? "explorer" : "vscode";
            var args = ImmutableDictionary<string, string>.Empty.Add("app_id", appId);
            if (appId == "vscode") args = args.Add("workspace_id", "xiaok");
            return await _broker.ExecuteAsync(new ToolProposal("app.launch.v1", args, appId, "对应应用窗口可见"), token);
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
            return await _broker.ExecuteAsync(new ToolProposal(tool, ImmutableDictionary<string, string>.Empty.Add(key, body),
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
        if (lower.StartsWith("打开") || lower.Contains("打开小k项目") || lower.Contains("启动应用")) return "app";
        return "chat";
    }

    private static string CategoryLabel(string category) => category switch
    {
        "app" => "应用操作", "file" => "文件查找", "analyze" => "消息分析", "draft" => "回复草稿",
        "send" => "发送请求", "code" => "本地编程任务", _ => "本地对话"
    };

    private static string ExtractPayload(string request, IEnumerable<string> prefixes)
    {
        var colon = request.IndexOfAny(['：', ':']);
        if (colon >= 0) return request[(colon + 1)..].Trim();
        foreach (var prefix in prefixes)
            if (request.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return request[prefix.Length..].Trim(' ', '，', ',', '：', ':');
        return string.Empty;
    }
}

internal sealed record XiaoKSettings
{
    public string DataRoot { get; init; } = @"D:\XiaoK\Data";
    public string ModelRoot { get; init; } = @"D:\XiaoK\Models";
    public string EvaluationRoot { get; init; } = @"D:\XiaoK\Evaluations";
    public string CodeProjectRoot { get; init; } = "";
    public string CodeWorkspaceRoot { get; init; } = @"D:\XiaoK\Workspaces";
    public string InferenceEndpoint { get; init; } = "http://127.0.0.1:8080/";
    public bool MonitorWeChatNotifications { get; init; }
    public bool MonitorQQNotifications { get; init; }
    public List<string> WeChatPublisherAppIds { get; init; } = [];
    public List<string> QQPublisherAppIds { get; init; } = [];
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
            if (loaded is null || !Uri.TryCreate(loaded.InferenceEndpoint, UriKind.Absolute, out var uri) || !uri.IsLoopback) return defaults;
            return loaded.WithDefaults(defaults);
        }
        catch (Exception) { return defaults; }
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

    private static string GetSettingsPath() => Path.Combine(
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
        QQPublisherAppIds = QQPublisherAppIds ?? []
    };

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
