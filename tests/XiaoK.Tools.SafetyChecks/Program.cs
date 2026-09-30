using System.Collections.Concurrent;
using System.Buffers.Binary;
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

    await CheckInteractiveInferenceTakesPriorityBetweenBackgroundStepsAsync();
    passed.Add("交互推理在编程代理的后台步骤边界优先执行");

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

static MessageNoticePolicy CreateNoticePolicy() => new(["wechat.package!Main"], ["qq.package!Main"]);

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
