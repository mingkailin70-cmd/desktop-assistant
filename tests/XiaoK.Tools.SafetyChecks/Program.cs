using System.Collections.Concurrent;
using System.Buffers.Binary;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;
using XiaoK.Adapters.Windows;
using XiaoK.Core;
using XiaoK.Inference;
using XiaoK.Tools;

var tempRoot = Path.Combine(Path.GetTempPath(), "XiaoK-SafetyChecks-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(tempRoot);
var passed = new List<string>();
var skipped = new List<string>();

try
{
    CheckAppResolverRejectsUnknownApplications();
    passed.Add("应用路由只接受已知别名，未知名称不会回退到 VS Code");

    await CheckAppLaunchCancellationIsTruthfulAsync();
    passed.Add("应用启动前取消不产生副作用，启动后取消显示结果待核对");

    CheckInterruptedTaskHistoryIsNotReplayed();
    passed.Add("重启前未结束的任务显示为结果待核对，不自动重试或泄露旧结果");

    await CheckValidPatchIsIsolatedAsync(tempRoot);
    passed.Add("有效补丁只写隔离工作区，保留 CRLF，并记录待审阅状态");

    await CheckModelCannotSelectOutsidePathAsync(tempRoot);
    passed.Add("文件选择阶段拒绝项目清单之外的路径");

    await CheckModelCannotPatchOutsidePathAsync(tempRoot);
    passed.Add("补丁阶段拒绝未选路径，外部哨兵文件未变化");

    await CheckLikelyCredentialIsNotSentAsync(tempRoot);
    passed.Add("疑似凭证源码未进入模型请求");

    await CheckCancellationPersistsAsync(tempRoot);
    passed.Add("取消中断本地模型调用并持久化 cancelled 状态");

    await CheckWorkspaceRetentionLimitAsync(tempRoot);
    passed.Add("达到五个任务目录上限后拒绝继续创建副本");

    await CheckRetainedCodeTaskHistoryAsync(tempRoot);
    passed.Add("重启后可安全读取隔离编程任务状态与工作区路径");

    await CheckHistoryRejectsHardLinkedStateAsync(tempRoot);
    passed.Add("任务历史拒绝读取指向工作区外的硬链接状态文件");

    await CheckInteractiveInferenceTakesPriorityBetweenBackgroundStepsAsync();
    passed.Add("交互推理在编程代理的后台步骤边界优先执行");

    await CheckModelRuntimeLeaseWrapsEachInferenceStepAsync();
    passed.Add("本地模型进程租约覆盖推理步骤并在成功、异常后释放");

    await CheckManagedRuntimeManifestIsStrictAsync(tempRoot);
    passed.Add("托管模型清单仅接受固定版本、模型、上下文与本机回环端点");

    CheckGpuMemoryAdmissionRequiresReserve();
    passed.Add("GPU 推理准入要求模型预算之外保留至少 1 GiB 显存");

    await CheckGpuPreflightBlocksBeforeRuntimeLaunchAsync(tempRoot);
    passed.Add("GPU 显存预检不足时在读取运行时文件前拒绝启动");

    var hardLinkSkip = await CheckHardLinkedSourceIsRejectedAsync(tempRoot);
    if (hardLinkSkip is null) passed.Add("项目内硬链接不会把目录外文件内容送入模型");
    else skipped.Add("硬链接夹具无法创建，用例跳过：" + hardLinkSkip);

    CheckNoticeSourceIsFilteredBeforeBodyRead();
    passed.Add("非允许发布者在读取通知正文前被拒绝");

    CheckUnknownNoticeConversationDoesNotReadBody();
    passed.Add("会话类型未知时不读取通知正文且不自动分析");

    CheckNoticePermissionAndLockState();
    passed.Add("权限缺失或锁屏时不读取通知正文");

    CheckConfirmedPrivateNoticeReadsOnlyOnceAndDeduplicates();
    passed.Add("已确认私聊才读取正文，重复通知不会再次读取");

    CheckNoticeWithoutVisibleBodyDoesNotAnalyze();
    passed.Add("无可见正文或正文超限时不触发分析且不保留正文");

    CheckStaleOrUnattributedPrivateNoticeDoesNotReadBody();
    passed.Add("过期通知或缺少会话身份的私聊在读取正文前被拦截");

    CheckNoticeRateLimitRunsBeforeBodyRead();
    passed.Add("会话限速在读取通知正文前生效");

    if (!CheckFileSearchRejectsReparsePoints(tempRoot, out var searchLinkSkipReason))
        skipped.Add("文件搜索重解析点夹具无法创建，用例跳过：" + searchLinkSkipReason);
    else passed.Add("文件搜索拒绝重解析搜索根并忽略根目录内的外部联接目标");

    await CheckHandleSearchContinuesAcrossDirectoryBatchesAsync(tempRoot);
    passed.Add("文件搜索通过稳定目录句柄读取多个枚举批次");

    if (!CheckDirectoryJunction(tempRoot, out var linkSkipReason)) skipped.Add("目录联接夹具无法创建，重解析点用例跳过：" + linkSkipReason);
    else passed.Add("目录联接不会被快照复制或读取");

    foreach (var item in passed) Console.WriteLine("通过：" + item);
    foreach (var item in skipped) Console.WriteLine("跳过：" + item);
    Console.WriteLine($"结果：{passed.Count} 项通过，{skipped.Count} 项跳过。");
}
finally
{
    if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, recursive: true);
}

static async Task CheckValidPatchIsIsolatedAsync(string root)
{
    var project = CreateProject(root, "valid", "class Sample {\r\n    int Value = 1;\r\n}\r\n");
    var workspaceRoot = Path.Combine(root, "valid-workspaces");
    var inference = new ScriptedInference(
        "{\"paths\":[\"Sample.cs\"]}",
        "{\"files\":[{\"path\":\"Sample.cs\",\"content\":\"class Sample {\\r\\n    int Value = 2;\\r\\n}\\r\\n\"}]}");
    var result = await NewAgent(inference).ExecuteAsync(project, workspaceRoot, "把 Value 改为 2", CancellationToken.None);

    Require(result.Success && result.FinalState == TaskLifecycleState.AwaitingApproval, "有效补丁未进入待审阅状态。");
    Require(File.ReadAllText(Path.Combine(project, "Sample.cs")) == "class Sample {\r\n    int Value = 1;\r\n}\r\n", "原项目被改动。");
    var taskDirectory = Directory.GetDirectories(workspaceRoot).Single();
    var workspaceFile = Path.Combine(taskDirectory, "workspace", "Sample.cs");
    var updated = File.ReadAllText(workspaceFile);
    Require(updated.Contains("Value = 2;\r\n", StringComparison.Ordinal), "隔离副本未得到模型补丁或 CRLF 未保留。");
    Require(result.Data?.Contains("-    int Value = 1;", StringComparison.Ordinal) == true
        && result.Data.Contains("+    int Value = 2;", StringComparison.Ordinal), "返回的差异没有显示修改前后内容。");
    var state = File.ReadAllText(Path.Combine(taskDirectory, "task-state.json"));
    Require(state.Contains("awaiting_approval", StringComparison.Ordinal), "任务状态没有写入 awaiting_approval。");
}

static void CheckInterruptedTaskHistoryIsNotReplayed()
{
    var processStartedAt = DateTimeOffset.UtcNow;
    var stale = new TaskRecord(Guid.NewGuid(), "app", "应用操作", TaskLifecycleState.Running,
        processStartedAt.AddMinutes(-1), processStartedAt.AddSeconds(-1), "暂存结果");
    var staleQueued = stale with { Status = TaskLifecycleState.Queued };
    var awaitingApproval = stale with { Status = TaskLifecycleState.AwaitingApproval };
    var current = stale with { UpdatedAtUtc = processStartedAt.AddSeconds(1) };

    var interrupted = TaskHistoryRecoveryPolicy.ForDisplay(stale, processStartedAt);
    Require(interrupted.Status == TaskLifecycleState.OutcomeUncertain
        && interrupted.ErrorCode == TaskHistoryRecoveryPolicy.HostRestartedErrorCode
        && interrupted.Result is null, "上次进程中未结束的任务没有被标为待核对，或保留了旧结果内容。");
    Require(TaskHistoryRecoveryPolicy.ForDisplay(staleQueued, processStartedAt).Status == TaskLifecycleState.OutcomeUncertain
        && TaskHistoryRecoveryPolicy.ForDisplay(awaitingApproval, processStartedAt) == awaitingApproval
        && TaskHistoryRecoveryPolicy.ForDisplay(current, processStartedAt) == current,
        "旧的排队任务、等待审阅任务或当前进程内任务状态投影错误。");
    Require(TaskHistoryRecoveryPolicy.IsInterruptedCodeTask("running", stale.UpdatedAtUtc, processStartedAt)
        && !TaskHistoryRecoveryPolicy.IsInterruptedCodeTask("awaiting_approval", stale.UpdatedAtUtc, processStartedAt),
        "隔离编程任务状态没有区分异常中断与等待审阅。");
}

static void CheckAppResolverRejectsUnknownApplications()
{
    var project = AppLaunchIntentResolver.Resolve("打开小K项目");
    var wechat = AppLaunchIntentResolver.Resolve("启动应用 微信");
    var edge = AppLaunchIntentResolver.Resolve("打开 Edge");
    var fullWidthQq = AppLaunchIntentResolver.Resolve("打开ＱＱ");
    var unknown = AppLaunchIntentResolver.Resolve("打开记事本");
    var unsupportedVariant = AppLaunchIntentResolver.Resolve("打开 QQ音乐");
    Require(project is { AppId: "vscode", WorkspaceId: "xiaok" }
        && wechat is { AppId: "wechat", WorkspaceId: null }
        && edge is { AppId: "edge", WorkspaceId: null }
        && fullWidthQq is { AppId: "qq", WorkspaceId: null },
        "已支持应用别名没有映射到预期的固定应用 ID。");
    Require(unknown is null && unsupportedVariant is null,
        "未知应用名称被错误映射到了某个已允许的应用。");
}

static async Task CheckAppLaunchCancellationIsTruthfulAsync()
{
    var appId = "test-app";
    var app = new DesktopApp(appId, Path.Combine(Environment.SystemDirectory, "notepad.exe"));
    var proposal = ToolBroker.Proposal("app.launch.v1",
        [new KeyValuePair<string, string>("app_id", appId)], appId, "目标窗口可见");

    using var beforeCancellation = new CancellationTokenSource();
    beforeCancellation.Cancel();
    var beforeController = new FakeDesktopAppProcessController(windowVisible: true);
    var beforeDesktop = new WindowsDesktopTools([app], [], beforeController);
    var cancelledBeforeLaunch = false;
    try { _ = await beforeDesktop.LaunchAsync(proposal, beforeCancellation.Token); }
    catch (OperationCanceledException) when (beforeCancellation.IsCancellationRequested) { cancelledBeforeLaunch = true; }
    Require(cancelledBeforeLaunch && beforeController.StartCount == 0,
        "应用启动前取消仍发出了进程启动请求。");

    using var afterCancellation = new CancellationTokenSource();
    var afterController = new FakeDesktopAppProcessController(() => afterCancellation.Cancel(), windowVisible: true);
    var afterDesktop = new WindowsDesktopTools([app], [], afterController);
    var result = await afterDesktop.LaunchAsync(proposal, afterCancellation.Token);
    Require(!result.Success && result.ErrorCode == "APP_LAUNCH_OUTCOME_UNCERTAIN"
        && result.FinalState == TaskLifecycleState.OutcomeUncertain
        && afterController.StartCount == 1 && afterController.WindowCheckCount == 0,
        "应用启动请求发出后取消被误报为完全取消，或仍继续了窗口轮询。");
}

static async Task CheckModelCannotSelectOutsidePathAsync(string root)
{
    var project = CreateProject(root, "bad-selection", "class Sample {}\n");
    var inference = new ScriptedInference("{\"paths\":[\"../outside.txt\"]}");
    var result = await NewAgent(inference).ExecuteAsync(project, Path.Combine(root, "bad-selection-workspaces"), "修改项目", CancellationToken.None);
    Require(!result.Success && result.ErrorCode == "CODE_TASK_FAILED", "清单外文件选择未被拒绝。");
    Require(inference.CallCount == 1, "非法选择后仍继续调用了模型。");
    Require(File.ReadAllText(Path.Combine(project, "Sample.cs")) == "class Sample {}\n", "非法选择修改了原项目。");
}

static async Task CheckModelCannotPatchOutsidePathAsync(string root)
{
    var project = CreateProject(root, "bad-patch", "class Sample {}\n");
    var outside = Path.Combine(root, "outside-sentinel.txt");
    const string sentinel = "preserve-this-file";
    File.WriteAllText(outside, sentinel);
    var inference = new ScriptedInference(
        "{\"paths\":[\"Sample.cs\"]}",
        "{\"files\":[{\"path\":\"../../outside-sentinel.txt\",\"content\":\"overwritten\"}]}");
    var result = await NewAgent(inference).ExecuteAsync(project, Path.Combine(root, "bad-patch-workspaces"), "修改项目", CancellationToken.None);
    Require(!result.Success && result.ErrorCode == "CODE_TASK_FAILED", "未选路径补丁未被拒绝。");
    Require(File.ReadAllText(outside) == sentinel, "补丁覆盖了隔离目录外的哨兵文件。");
    Require(File.ReadAllText(Path.Combine(project, "Sample.cs")) == "class Sample {}\n", "拒绝补丁时原项目被改动。");
}

static async Task CheckLikelyCredentialIsNotSentAsync(string root)
{
    var project = CreateProject(root, "credential", "internal const string api_key = \"sk-12345678901234567890123456789012\";\n");
    var inference = new ScriptedInference("{\"paths\":[\"Program.cs\"]}");
    var result = await NewAgent(inference).ExecuteAsync(project, Path.Combine(root, "credential-workspaces"), "检查代码", CancellationToken.None);
    Require(!result.Success && result.ErrorCode == "NO_CODE_FILES", "包含已知格式凭证的文件未从模型清单中排除。");
    Require(inference.CallCount == 0, "疑似凭证文件进入了模型请求。");
}

static async Task CheckCancellationPersistsAsync(string root)
{
    var project = CreateProject(root, "cancel", "class Sample {}\n");
    var workspaces = Path.Combine(root, "cancel-workspaces");
    var inference = new ScriptedInference(blockOnFirstCall: true);
    using var cancellation = new CancellationTokenSource();
    var task = NewAgent(inference).ExecuteAsync(project, workspaces, "等待并取消", cancellation.Token);
    await inference.FirstCallStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
    cancellation.Cancel();
    try
    {
        await task;
        throw new InvalidOperationException("取消后代理没有传播 OperationCanceledException。");
    }
    catch (OperationCanceledException) { }

    var taskDirectory = Directory.GetDirectories(workspaces).Single();
    var state = File.ReadAllText(Path.Combine(taskDirectory, "task-state.json"));
    Require(state.Contains("cancelled", StringComparison.Ordinal), "取消后的任务状态没有持久化。");
    Require(File.ReadAllText(Path.Combine(project, "Sample.cs")) == "class Sample {}\n", "取消时原项目被改动。");
}

static async Task CheckWorkspaceRetentionLimitAsync(string root)
{
    var project = CreateProject(root, "retention", "class Sample {}\n");
    var workspace = Path.Combine(root, "retention-workspaces");
    var responses = Enumerable.Range(0, 5).SelectMany(_ => new[] { "{\"paths\":[]}" }).ToArray();
    var inference = new ScriptedInference(responses);
    var agent = NewAgent(inference);
    for (var i = 0; i < 5; i++)
    {
        var result = await agent.ExecuteAsync(project, workspace, "无效选择", CancellationToken.None);
        Require(result.ErrorCode == "NO_VALID_FILES_SELECTED", "预期的受控空选择失败状态不匹配。");
    }

    var blocked = await agent.ExecuteAsync(project, workspace, "第六个任务", CancellationToken.None);
    Require(!blocked.Success && blocked.ErrorCode == "CODE_WORKSPACE_FAILED", "超过保留上限后没有拒绝新任务。");
    Require(Directory.GetDirectories(workspace).Length == 5, "隔离工作区目录数量越过五个上限。");
    Require(inference.CallCount == 5, "第六个任务在被拒绝前仍调用了模型。");
}

static void CheckNoticeSourceIsFilteredBeforeBodyRead()
{
    var policy = CreateNoticePolicy();
    var bodyReads = 0;
    var result = policy.Inspect("wechat", "untrusted.publisher!App", "chat-1", "Alice", true,
        () => { bodyReads++; return "不得读取这段正文"; }, DateTimeOffset.UtcNow, "notice-source", true, true);

    Require(!result.Accepted && !result.AnalyzeBody && bodyReads == 0,
        "非允许发布者通知未能在正文读取前被拒绝。");
}

static void CheckUnknownNoticeConversationDoesNotReadBody()
{
    var policy = CreateNoticePolicy();
    var bodyReads = 0;
    var result = policy.Inspect("wechat", "wechat.package!Main", null, null, null,
        () => { bodyReads++; return "不应读到"; }, DateTimeOffset.UtcNow, "notice-unknown-chat", true, true);

    Require(result.Accepted && !result.AnalyzeBody && result.Notice?.Body is null && bodyReads == 0,
        "会话类型未知时读取了正文或触发了自动分析。");
}

static void CheckNoticePermissionAndLockState()
{
    var lockedPolicy = CreateNoticePolicy();
    var lockedReads = 0;
    var locked = lockedPolicy.Inspect("wechat", "wechat.package!Main", "chat-2", "Alice", true,
        () => { lockedReads++; return "不应读到"; }, DateTimeOffset.UtcNow, "notice-locked", false, true);
    Require(!locked.Accepted && lockedReads == 0, "锁屏时读取了通知正文。");

    var revokedPolicy = CreateNoticePolicy();
    var revokedReads = 0;
    var revoked = revokedPolicy.Inspect("wechat", "wechat.package!Main", "chat-3", "Alice", true,
        () => { revokedReads++; return "不应读到"; }, DateTimeOffset.UtcNow, "notice-revoked", true, false);
    Require(!revoked.Accepted && revokedReads == 0, "权限未授予时读取了通知正文。");
}

static void CheckConfirmedPrivateNoticeReadsOnlyOnceAndDeduplicates()
{
    var policy = CreateNoticePolicy();
    var bodyReads = 0;
    string? ReadBody() { bodyReads++; return "会议改到三点"; }

    var first = policy.Inspect("wechat", "wechat.package!Main", "chat-4", "Alice", true,
        ReadBody, DateTimeOffset.UtcNow, "notice-private", true, true);
    var duplicate = policy.Inspect("wechat", "wechat.package!Main", "chat-4", "Alice", true,
        ReadBody, DateTimeOffset.UtcNow, "notice-private", true, true);

    Require(first.Accepted && first.AnalyzeBody && first.Notice?.Body == "会议改到三点" && bodyReads == 1,
        "已确认私聊的可见正文未按预期读取一次并生成分析请求。");
    Require(!duplicate.Accepted && !duplicate.AnalyzeBody && bodyReads == 1,
        "重复通知再次读取了正文或触发了分析。");
}

static void CheckNoticeWithoutVisibleBodyDoesNotAnalyze()
{
    var policy = CreateNoticePolicy();
    var missing = policy.Inspect("qq", "qq.package!Main", "chat-5", "Bob", true,
        () => null, DateTimeOffset.UtcNow, "notice-no-body", true, true);
    Require(missing.Accepted && !missing.AnalyzeBody && missing.Notice?.Body is null,
        "无可见正文的通知触发了分析或保存了正文。");

    var oversized = policy.Inspect("qq", "qq.package!Main", "chat-6", "Bob", true,
        () => new string('x', 20_001), DateTimeOffset.UtcNow, "notice-large-body", true, true);
    Require(oversized.Accepted && !oversized.AnalyzeBody && oversized.Notice?.Body is null,
        "正文超限的通知触发了分析或保留了正文。");
}

static void CheckStaleOrUnattributedPrivateNoticeDoesNotReadBody()
{
    var stalePolicy = CreateNoticePolicy();
    var staleReads = 0;
    var stale = stalePolicy.Inspect("wechat", "wechat.package!Main", "chat-stale", "Alice", true,
        () => { staleReads++; return "过期正文"; }, DateTimeOffset.UtcNow.AddHours(-25), "notice-stale", true, true);
    Require(stale.Accepted && !stale.AnalyzeBody && stale.Notice?.Body is null && staleReads == 0,
        "过期通知在显式拒绝正文读取前未被拦截。");

    var unattributedPolicy = CreateNoticePolicy();
    var unattributedReads = 0;
    var unattributed = unattributedPolicy.Inspect("qq", "qq.package!Main", null, null, true,
        () => { unattributedReads++; return "无归属正文"; }, DateTimeOffset.UtcNow, "notice-unattributed", true, true);
    Require(unattributed.Accepted && !unattributed.AnalyzeBody && unattributed.Notice?.Body is null && unattributedReads == 0,
        "缺少会话ID和发送者的私聊通知读取了正文或触发了分析。");
}

static void CheckNoticeRateLimitRunsBeforeBodyRead()
{
    var policy = new MessageNoticePolicy(["wechat.package!Main"], [], maximumPrivateNoticesPerWindow: 1);
    var bodyReads = 0;
    string? ReadBody() { bodyReads++; return "单条可见消息"; }

    var first = policy.Inspect("wechat", "wechat.package!Main", "chat-rate", "Alice", true,
        ReadBody, DateTimeOffset.UtcNow, "notice-rate-1", true, true);
    var limited = policy.Inspect("wechat", "wechat.package!Main", "chat-rate", "Alice", true,
        ReadBody, DateTimeOffset.UtcNow, "notice-rate-2", true, true);

    Require(first.AnalyzeBody && bodyReads == 1, "首条会话通知未按预期读取。");
    Require(limited.Accepted && !limited.AnalyzeBody && limited.Notice?.Body is null && bodyReads == 1,
        "限速通知在限速决定之前读取了正文或触发了分析。");
}

static MessageNoticePolicy CreateNoticePolicy() => new(["wechat.package!Main"], ["qq.package!Main"]);

static async Task CheckRetainedCodeTaskHistoryAsync(string root)
{
    var workspaceRoot = Path.Combine(root, "history-valid");
    var taskId = "20260930-123456-" + Guid.NewGuid().ToString("N");
    var taskRoot = Path.Combine(workspaceRoot, taskId);
    var workspace = Path.Combine(taskRoot, "workspace");
    Directory.CreateDirectory(workspace);
    var statePath = Path.Combine(taskRoot, "task-state.json");
    var state = new
    {
        taskId,
        createdAtUtc = DateTimeOffset.Parse("2026-09-30T12:34:56Z"),
        updatedAtUtc = DateTimeOffset.Parse("2026-09-30T12:35:56Z"),
        state = "awaiting_approval",
        projectPath = Path.Combine(root, "private-project"),
        workspacePath = workspace,
        ignoredBody = "CHAT_BODY_MUST_NOT_BE_EXPOSED_2c7e"
    };
    await File.WriteAllTextAsync(statePath, System.Text.Json.JsonSerializer.Serialize(state), new UTF8Encoding(false));

    var history = CodeTaskAgent.ReadRetainedTasks(workspaceRoot);
    Require(history.Count == 1 && history[0].TaskId == taskId && history[0].State == "awaiting_approval",
        "隔离任务历史没有恢复已保存的审批状态。");
    Require(Path.GetFullPath(history[0].WorkspacePath) == Path.GetFullPath(workspace),
        "隔离任务历史返回的工作区路径不匹配实际工作区。");
    Require(!System.Text.Json.JsonSerializer.Serialize(history).Contains("CHAT_BODY_MUST_NOT_BE_EXPOSED_2c7e", StringComparison.Ordinal),
        "隔离任务历史暴露了状态文件中的非白名单字段。");
}

static async Task CheckHistoryRejectsHardLinkedStateAsync(string root)
{
    var fixtureRoot = Path.Combine(root, "history-hardlink");
    var workspaceRoot = Path.Combine(fixtureRoot, "workspaces");
    var taskId = "20260930-123456-" + Guid.NewGuid().ToString("N");
    var taskRoot = Path.Combine(workspaceRoot, taskId);
    Directory.CreateDirectory(Path.Combine(taskRoot, "workspace"));
    var externalState = Path.Combine(fixtureRoot, "outside-task-state.json");
    var linkedState = Path.Combine(taskRoot, "task-state.json");
    var state = new
    {
        taskId,
        createdAtUtc = DateTimeOffset.UtcNow,
        updatedAtUtc = DateTimeOffset.UtcNow,
        state = "awaiting_approval",
        workspacePath = Path.Combine(taskRoot, "workspace")
    };
    await File.WriteAllTextAsync(externalState, System.Text.Json.JsonSerializer.Serialize(state), new UTF8Encoding(false));
    Require(HardLinkFixture.TryCreate(linkedState, externalState, out var reason), "无法创建任务状态硬链接夹具：" + reason);

    var history = CodeTaskAgent.ReadRetainedTasks(workspaceRoot);
    Require(history.Count == 0, "任务历史读取了工作区之外的硬链接状态文件。");
    Require(File.Exists(externalState), "拒绝硬链接任务状态时删除了外部状态文件。");
}

static async Task CheckInteractiveInferenceTakesPriorityBetweenBackgroundStepsAsync()
{
    var broker = new ModelBroker();
    var firstStepStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var releaseFirstStep = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var order = new ConcurrentQueue<string>();

    var firstBackgroundStep = broker.RunBackgroundStepAsync(async token =>
    {
        order.Enqueue("后台步骤1开始");
        firstStepStarted.TrySetResult();
        await releaseFirstStep.Task.WaitAsync(token);
        order.Enqueue("后台步骤1结束");
        return "step-1";
    }, CancellationToken.None);

    await firstStepStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
    var secondBackgroundStep = broker.RunBackgroundStepAsync(token =>
    {
        order.Enqueue("后台步骤2");
        return Task.FromResult("step-2");
    }, CancellationToken.None);
    var interactive = broker.RunInteractiveAsync(token =>
    {
        order.Enqueue("交互请求");
        return Task.FromResult("interactive");
    }, CancellationToken.None);

    releaseFirstStep.TrySetResult();
    await Task.WhenAll(firstBackgroundStep, secondBackgroundStep, interactive).WaitAsync(TimeSpan.FromSeconds(5));
    var sequence = order.ToArray();
    Require(Array.IndexOf(sequence, "后台步骤1结束") < Array.IndexOf(sequence, "交互请求")
        && Array.IndexOf(sequence, "交互请求") < Array.IndexOf(sequence, "后台步骤2"),
        "后台步骤结束后，交互请求没有优先于下一后台步骤执行。");
}

static async Task CheckModelRuntimeLeaseWrapsEachInferenceStepAsync()
{
    var runtime = new TrackingModelRuntime();
    var broker = new ModelBroker(runtime);
    var observedLease = await broker.RunInteractiveAsync(token =>
        Task.FromResult(runtime.ActiveLeases == 1), CancellationToken.None);
    Require(observedLease && runtime.Acquisitions == 1 && runtime.ActiveLeases == 0,
        "模型调用没有在运行时租约内执行，或结束后未释放租约。");

    try
    {
        await broker.RunBackgroundStepAsync<bool>(_ => throw new IOException("synthetic"), CancellationToken.None);
        throw new InvalidOperationException("预期的失败模型步骤没有失败。");
    }
    catch (IOException ex) when (ex.Message == "synthetic") { }
    Require(runtime.ActiveLeases == 0, "失败模型步骤遗留了运行时租约。");

    var disabled = new ModelBroker(new UnavailableModelRuntime("测试配置未锁定"));
    var operationRan = false;
    try
    {
        await disabled.RunInteractiveAsync(_ => { operationRan = true; return Task.FromResult(true); }, CancellationToken.None);
        throw new InvalidOperationException("无效托管运行时没有拒绝模型请求。");
    }
    catch (ModelRuntimeUnavailableException) { }
    Require(!operationRan, "托管运行时校验失败后仍把请求发送到了推理端点。");
}

static async Task CheckManagedRuntimeManifestIsStrictAsync(string root)
{
    var modelRoot = Path.Combine(root, "managed-model-root");
    Directory.CreateDirectory(modelRoot);
    var manifestPath = Path.Combine(modelRoot, "llama-runtime.json");
    const string validManifest = """
        {
          "schemaVersion": 1,
          "runtimeVersion": "b11256",
          "runtimeSha256": "0000000000000000000000000000000000000000000000000000000000000000",
          "modelId": "qwen3.5-4b-q4km",
          "modelSha256": "1111111111111111111111111111111111111111111111111111111111111111",
          "contextTokens": 4096,
          "gpuLayers": 99,
          "expectedGpuMemoryMiB": 5000
        }
        """;
    await File.WriteAllTextAsync(manifestPath, validManifest);
    var runtime = LlamaCppModelRuntime.TryLoad(modelRoot, "http://127.0.0.1:8080/");
    if (runtime is null) throw new InvalidOperationException("有效的固定清单未能加载托管运行时。");
    await runtime.DisposeAsync();

    await File.WriteAllTextAsync(manifestPath, """
        {
          "schemaVersion": 1,
          "runtimeVersion": "latest",
          "runtimeSha256": "0000000000000000000000000000000000000000000000000000000000000000",
          "modelId": "qwen3.5-4b-q4km",
          "modelSha256": "1111111111111111111111111111111111111111111111111111111111111111",
          "contextTokens": 4096,
          "gpuLayers": 99,
          "expectedGpuMemoryMiB": 5000
        }
        """);
    try
    {
        _ = LlamaCppModelRuntime.TryLoad(modelRoot, "http://127.0.0.1:8080/");
        throw new InvalidOperationException("可变运行时版本没有被拒绝。");
    }
    catch (InvalidDataException) { }

    await File.WriteAllTextAsync(manifestPath, validManifest);
    try
    {
        _ = LlamaCppModelRuntime.TryLoad(modelRoot, "http://192.168.1.10:8080/");
        throw new InvalidOperationException("非回环托管端点没有被拒绝。");
    }
    catch (InvalidDataException) { }
}

static void CheckGpuMemoryAdmissionRequiresReserve()
{
    var fits = GpuMemoryAdmissionPolicy.Evaluate(new GpuMemorySnapshot(6144, 8192), 5120);
    var reserveMissing = GpuMemoryAdmissionPolicy.Evaluate(new GpuMemorySnapshot(6000, 8192), 5120);
    var unavailable = GpuMemoryAdmissionPolicy.Evaluate(null, 1000);
    Require(fits.Allowed, "预算和 1 GiB 余量均满足时未获准启动模型。");
    Require(!reserveMissing.Allowed && !unavailable.Allowed,
        "余量不足或显存读数缺失时仍允许启动模型。");
    Require(!GpuMemoryAdmissionPolicy.HasMinimumReserve(new GpuMemorySnapshot(1023, 8192)),
        "模型运行后低于 1 GiB 的余量仍通过校验。");
}

static async Task CheckGpuPreflightBlocksBeforeRuntimeLaunchAsync(string root)
{
    var modelRoot = Path.Combine(root, "gpu-preflight-model-root");
    Directory.CreateDirectory(modelRoot);
    await File.WriteAllTextAsync(Path.Combine(modelRoot, "llama-runtime.json"), """
        {
          "schemaVersion": 1,
          "runtimeVersion": "b11256",
          "runtimeSha256": "0000000000000000000000000000000000000000000000000000000000000000",
          "modelId": "qwen3.5-4b-q4km",
          "modelSha256": "1111111111111111111111111111111111111111111111111111111111111111",
          "contextTokens": 4096,
          "gpuLayers": 99,
          "expectedGpuMemoryMiB": 5000
        }
        """);

    var runtime = LlamaCppModelRuntime.TryLoad(modelRoot, "http://127.0.0.1:8080/",
        new FixedGpuMemoryProbe(new GpuMemorySnapshot(5500, 8192)));
    if (runtime is null) throw new InvalidOperationException("托管 GPU 清单未被加载。");
    var rejected = false;
    try
    {
        await using var lease = await runtime.AcquireAsync(CancellationToken.None);
    }
    catch (LowGpuMemoryException) { rejected = true; }
    finally { await runtime.DisposeAsync(); }
    Require(rejected, "GPU 显存预算不足时仍通过了托管运行时预检。");
}

static async Task<string?> CheckHardLinkedSourceIsRejectedAsync(string root)
{
    var fixtureRoot = Path.Combine(root, "hard-links");
    var project = Path.Combine(fixtureRoot, "project");
    Directory.CreateDirectory(project);
    var externalFile = Path.Combine(fixtureRoot, "outside-project.txt");
    var projectAlias = Path.Combine(project, "Sample.cs");
    const string externalContent = "HARDLINK_SENTINEL_7d63a2fa outside-project content";
    await File.WriteAllTextAsync(externalFile, externalContent, new UTF8Encoding(false));

    if (!HardLinkFixture.TryCreate(projectAlias, externalFile, out var reason)) return reason;

    var inference = new ScriptedInference("{\"paths\":[\"Sample.cs\"]}");
    var result = await NewAgent(inference).ExecuteAsync(project, Path.Combine(fixtureRoot, "workspaces"), "读取项目文件", CancellationToken.None);
    Require(!result.Success && inference.CallCount == 0,
        "包含硬链接的源项目在拒绝之前仍调用了模型。");
    Require(inference.Prompts.All(prompt => !prompt.Contains("HARDLINK_SENTINEL_7d63a2fa", StringComparison.Ordinal)),
        "项目外硬链接内容进入了模型提示。");
    Require(await File.ReadAllTextAsync(externalFile) == externalContent,
        "硬链接安全检查改变了项目外原始文件。");
    return null;
}

static bool CheckDirectoryJunction(string root, out string skipReason)
{
    var project = CreateProject(root, "links", "class Sample { int Value = 1; }\n");
    var outsideDirectory = Path.Combine(root, "junction-target");
    Directory.CreateDirectory(outsideDirectory);
    File.WriteAllText(Path.Combine(outsideDirectory, "leak.txt"), "directory-secret-marker");
    var junctionPath = Path.Combine(project, "external-dir");

    if (!JunctionFixture.TryCreate(outsideDirectory, junctionPath, out skipReason)) return false;

    try
    {
        var workspaceRoot = Path.Combine(root, "link-workspaces");
        var inference = new ScriptedInference(
            "{\"paths\":[\"Sample.cs\"]}",
            "{\"files\":[{\"path\":\"Sample.cs\",\"content\":\"class Sample { int Value = 2; }\\n\"}]}");
        var result = NewAgent(inference).ExecuteAsync(project, workspaceRoot, "改值", CancellationToken.None).GetAwaiter().GetResult();
        Require(result.Success, "普通文件代码任务在项目含目录联接时失败。");
        Require(inference.Prompts.All(prompt => !prompt.Contains("directory-secret-marker", StringComparison.Ordinal)), "目录联接目标的正文泄露给模型。");
        var taskRoot = Directory.GetDirectories(workspaceRoot).Single();
        Require(!File.Exists(Path.Combine(taskRoot, "workspace", "external-dir", "leak.txt")), "目录联接目标进入工作区。");
        skipReason = "";
        return true;
    }
    finally
    {
        Directory.Delete(junctionPath, recursive: false);
    }
}

static bool CheckFileSearchRejectsReparsePoints(string root, out string skipReason)
{
    var fixtureRoot = Path.Combine(root, "file-search-links");
    var allowedRoot = Path.Combine(fixtureRoot, "allowed");
    var outsideRoot = Path.Combine(fixtureRoot, "outside");
    Directory.CreateDirectory(allowedRoot);
    Directory.CreateDirectory(outsideRoot);
    File.WriteAllText(Path.Combine(allowedRoot, "allowed-query-result.txt"), "inside allowed search root");
    File.WriteAllText(Path.Combine(outsideRoot, "external-query-secret.txt"), "outside allowed search root");

    var nestedJunction = Path.Combine(allowedRoot, "external-link");
    if (!JunctionFixture.TryCreate(outsideRoot, nestedJunction, out skipReason)) return false;
    var linkedSearchRoot = Path.Combine(fixtureRoot, "linked-root");
    if (!JunctionFixture.TryCreate(outsideRoot, linkedSearchRoot, out skipReason))
    {
        Directory.Delete(nestedJunction, recursive: false);
        return false;
    }

    try
    {
        var desktop = new WindowsDesktopTools(Array.Empty<DesktopApp>(),
            [new KeyValuePair<string, string>("user-files", allowedRoot)]);
        var search = ToolBroker.Proposal("file.search.v1",
            [new("query", "query-"), new("root_id", "user-files")], "user-files", "返回允许目录内的文件名匹配项");
        var result = desktop.SearchFilesAsync(search, CancellationToken.None).GetAwaiter().GetResult();
        var resultData = result.Data ?? "";
        Require(result.Success && resultData.Contains("allowed-query-result.txt", StringComparison.Ordinal),
            "文件搜索没有返回普通范围内的匹配文件。");
        Require(!resultData.Contains("external-query-secret.txt", StringComparison.Ordinal),
            "文件搜索通过目录联接返回了允许范围外的文件。");

        var linkedDesktop = new WindowsDesktopTools(Array.Empty<DesktopApp>(),
            [new KeyValuePair<string, string>("user-files", linkedSearchRoot)]);
        var linkedResult = linkedDesktop.SearchFilesAsync(search, CancellationToken.None).GetAwaiter().GetResult();
        Require(!linkedResult.Success && linkedResult.ErrorCode == "SEARCH_ROOT_NOT_LOCAL_DIRECTORY",
            "文件搜索接受了指向范围外的重解析搜索根。");

        skipReason = "";
        return true;
    }
    finally
    {
        Directory.Delete(nestedJunction, recursive: false);
        Directory.Delete(linkedSearchRoot, recursive: false);
    }
}

static async Task CheckHandleSearchContinuesAcrossDirectoryBatchesAsync(string root)
{
    var searchRoot = Path.Combine(root, "file-search-batches");
    Directory.CreateDirectory(searchRoot);
    for (var index = 0; index < 1_200; index++)
    {
        var file = Path.Combine(searchRoot, $"filler-{index:D4}-directory-enumeration-batch-check.txt");
        await File.WriteAllTextAsync(file, "synthetic filename search fixture", new UTF8Encoding(false));
    }
    var targetName = "unique-target-result.txt";
    await File.WriteAllTextAsync(Path.Combine(searchRoot, targetName), "synthetic target", new UTF8Encoding(false));

    var desktop = new WindowsDesktopTools(Array.Empty<DesktopApp>(),
        [new KeyValuePair<string, string>("user-files", searchRoot)]);
    var proposal = ToolBroker.Proposal("file.search.v1",
        [new("query", targetName), new("root_id", "user-files")], "user-files", "返回精确名称匹配项");
    var result = await desktop.SearchFilesAsync(proposal, CancellationToken.None);
    Require(result.Success && result.Data?.Contains(targetName, StringComparison.Ordinal) == true,
        "目录句柄枚举没有继续读取后续文件批次。");
}

static CodeTaskAgent NewAgent(IInferenceClient inference) => new(inference, new ModelBroker(), repositoryRoot: null);

static string CreateProject(string root, string name, string content)
{
    var path = Path.Combine(root, name);
    Directory.CreateDirectory(path);
    File.WriteAllText(Path.Combine(path, name == "credential" ? "Program.cs" : "Sample.cs"), content, new UTF8Encoding(false));
    return path;
}

static void Require(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

internal sealed class ScriptedInference : IInferenceClient
{
    private readonly ConcurrentQueue<string> _responses;
    private readonly bool _blockOnFirstCall;
    private int _callCount;

    public ScriptedInference(params string[] responses) : this(false, responses) { }

    private ScriptedInference(bool blockOnFirstCall, params string[] responses)
    {
        _blockOnFirstCall = blockOnFirstCall;
        _responses = new ConcurrentQueue<string>(responses);
    }

    public ScriptedInference(bool blockOnFirstCall) : this(blockOnFirstCall, []) { }
    public TaskCompletionSource FirstCallStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public ConcurrentBag<string> Prompts { get; } = [];
    public int CallCount => Volatile.Read(ref _callCount);

    public async Task<string> CompleteAsync(string systemPrompt, string userPrompt, CancellationToken cancellationToken)
    {
        Prompts.Add(userPrompt);
        var call = Interlocked.Increment(ref _callCount);
        if (_blockOnFirstCall && call == 1)
        {
            FirstCallStarted.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }
        if (!_responses.TryDequeue(out var response)) throw new InvalidOperationException("No scripted inference response remains.");
        return response;
    }
}

internal sealed class TrackingModelRuntime : IManagedModelRuntime
{
    private int _activeLeases;
    public string Status => "测试运行时";
    public int Acquisitions { get; private set; }
    public int ActiveLeases => Volatile.Read(ref _activeLeases);

    public ValueTask<IAsyncDisposable> AcquireAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Acquisitions++;
        Interlocked.Increment(ref _activeLeases);
        return ValueTask.FromResult<IAsyncDisposable>(new Lease(this));
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private sealed class Lease(TrackingModelRuntime owner) : IAsyncDisposable
    {
        private TrackingModelRuntime? _owner = owner;
        public ValueTask DisposeAsync()
        {
            var current = Interlocked.Exchange(ref _owner, null);
            if (current is not null) Interlocked.Decrement(ref current._activeLeases);
            return ValueTask.CompletedTask;
        }
    }
}

internal sealed class FakeDesktopAppProcessController(Action? onStart = null, bool windowVisible = true)
    : IDesktopAppProcessController
{
    public int StartCount { get; private set; }
    public int WindowCheckCount { get; private set; }

    public IDisposable? Start(ProcessStartInfo startInfo)
    {
        StartCount++;
        onStart?.Invoke();
        return new EmptyDisposable();
    }

    public bool HasVisibleWindow(DesktopApp app)
    {
        WindowCheckCount++;
        return windowVisible;
    }

    private sealed class EmptyDisposable : IDisposable
    {
        public void Dispose() { }
    }
}

internal sealed class FixedGpuMemoryProbe(GpuMemorySnapshot? snapshot) : IGpuMemoryProbe
{
    public Task<GpuMemorySnapshot?> ReadAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(snapshot);
    }
}

internal static class JunctionFixture
{
    private const uint IoReparseTagMountPoint = 0xA0000003;
    private const uint FileDeviceFileSystem = 9;
    private const uint FsctlSetReparsePoint = (FileDeviceFileSystem << 16) | (41u << 2);
    private const uint GenericWrite = 0x40000000;
    private const uint ShareRead = 0x00000001;
    private const uint ShareWrite = 0x00000002;
    private const uint ShareDelete = 0x00000004;
    private const uint OpenExisting = 3;
    private const uint OpenReparsePoint = 0x00200000;
    private const uint BackupSemantics = 0x02000000;

    public static bool TryCreate(string targetDirectory, string junctionPath, out string reason)
    {
        Directory.CreateDirectory(junctionPath);
        var fullTarget = Path.GetFullPath(targetDirectory);
        var substituteName = "\\??\\" + fullTarget;
        var substituteBytes = Encoding.Unicode.GetBytes(substituteName);
        var printBytes = Encoding.Unicode.GetBytes(fullTarget);
        var dataLength = checked((ushort)(8 + substituteBytes.Length + sizeof(char) + printBytes.Length + sizeof(char)));
        var buffer = new byte[8 + dataLength];
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(0, 4), IoReparseTagMountPoint);
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(4, 2), dataLength);
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(6, 2), 0);
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(8, 2), 0);
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(10, 2), checked((ushort)substituteBytes.Length));
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(12, 2), checked((ushort)(substituteBytes.Length + sizeof(char))));
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(14, 2), checked((ushort)printBytes.Length));
        substituteBytes.CopyTo(buffer, 16);
        printBytes.CopyTo(buffer, 16 + substituteBytes.Length + sizeof(char));

        using var handle = CreateFileW(junctionPath, GenericWrite, ShareRead | ShareWrite | ShareDelete,
            IntPtr.Zero, OpenExisting, OpenReparsePoint | BackupSemantics, IntPtr.Zero);
        if (handle.IsInvalid)
        {
            reason = $"CreateFileW failed: {Marshal.GetLastWin32Error()}";
            Directory.Delete(junctionPath, recursive: false);
            return false;
        }

        var pointer = Marshal.AllocHGlobal(buffer.Length);
        try
        {
            Marshal.Copy(buffer, 0, pointer, buffer.Length);
            if (!DeviceIoControl(handle, FsctlSetReparsePoint, pointer, (uint)buffer.Length, IntPtr.Zero, 0, out _, IntPtr.Zero))
            {
                reason = $"FSCTL_SET_REPARSE_POINT failed: {Marshal.GetLastWin32Error()}";
                Directory.Delete(junctionPath, recursive: false);
                return false;
            }
        }
        finally { Marshal.FreeHGlobal(pointer); }

        if ((File.GetAttributes(junctionPath) & FileAttributes.ReparsePoint) == 0)
        {
            reason = "创建的目录没有重解析点属性。";
            Directory.Delete(junctionPath, recursive: false);
            return false;
        }
        reason = "";
        return true;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CreateFileW")]
    private static extern SafeFileHandle CreateFileW(string fileName, uint desiredAccess, uint shareMode,
        IntPtr securityAttributes, uint creationDisposition, uint flagsAndAttributes, IntPtr templateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(SafeFileHandle device, uint controlCode, IntPtr inputBuffer,
        uint inputBufferSize, IntPtr outputBuffer, uint outputBufferSize, out uint bytesReturned, IntPtr overlapped);
}

internal static class HardLinkFixture
{
    public static bool TryCreate(string linkPath, string existingPath, out string reason)
    {
        if (CreateHardLinkW(linkPath, existingPath, IntPtr.Zero))
        {
            reason = "";
            return true;
        }
        reason = $"CreateHardLinkW failed: {Marshal.GetLastWin32Error()}";
        return false;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CreateHardLinkW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLinkW(string newFileName, string existingFileName, IntPtr securityAttributes);
}
