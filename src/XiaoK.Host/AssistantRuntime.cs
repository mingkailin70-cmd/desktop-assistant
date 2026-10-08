using System.IO;
using System.Net.Http;
using System.Collections.Immutable;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Win32;
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
    private XiaoKSettings _settings;
    private readonly SqliteTaskStore _store;
    private readonly LocalInferenceClient _inference;
    private readonly IManagedModelRuntime? _managedModelRuntime;
    private readonly DotNetTestRunner _dotNetTestRunner;
    private readonly WindowsMicrophoneCapture _microphone = new();
    private readonly WindowsLocalWakeWordListener? _wakeWordListener;
    private readonly VoiceInferenceService? _voiceInference;
    private readonly string _voiceStatus;
    private readonly ToolBroker _broker;
    private readonly ModelBroker _models;
    private readonly SemaphoreSlim _executionGate = new(1, 1);
    private readonly Channel<UserTaskWork> _userTaskQueue = Channel.CreateBounded<UserTaskWork>(
        new BoundedChannelOptions(16) { FullMode = BoundedChannelFullMode.Wait, SingleReader = true, SingleWriter = false });
    private readonly ConcurrentDictionary<Guid, CancellationTokenSource> _userTaskOperations = new();
    private readonly ConcurrentDictionary<Guid, TaskCompletionSource<string>> _userTaskCompletions = new();
    private readonly ConcurrentDictionary<Guid, string> _transientUserTaskResults = new();
    private readonly ConcurrentDictionary<Guid, string> _transientUserTaskSteps = new();
    private readonly object _transientResultLock = new();
    private readonly object _userTaskAdmissionLock = new();
    private readonly Queue<Guid> _transientResultOrder = new();
    private readonly Channel<NoticeAnalysisWork> _noticeAnalysisQueue = Channel.CreateBounded<NoticeAnalysisWork>(
        new BoundedChannelOptions(32) { FullMode = BoundedChannelFullMode.Wait, SingleReader = true, SingleWriter = false });
    private readonly CancellationTokenSource _noticeAnalysisStop = new();
    private readonly Task _noticeAnalysisWorker;
    private readonly ConcurrentDictionary<CancellationTokenSource, byte> _noticeAnalysisWorkItems = new();
    private CancellationTokenSource? _active;
    private readonly Task _userTaskWorker;
    private int _stopping;
    private int _pendingUserTaskCount;

    public AssistantRuntime(IApprovalPresenter approval)
    {
        _settings = XiaoKSettings.Load();
        if (!XiaoKSettings.IsDiagnosticsMode)
        {
            _wakeWordListener = new WindowsLocalWakeWordListener();
            _wakeWordListener.WakeWordDetected += () => WakeWordDetected?.Invoke();
            _wakeWordListener.StatusChanged += message => WakeWordStatusChanged?.Invoke(message);
            SystemEvents.SessionSwitch += OnSessionSwitch;
        }
        _microphone.MaximumDurationReached += OnMicrophoneMaximumDurationReached;
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
        var workspaceRoot = XiaoKSettings.FindWorkspace(AppContext.BaseDirectory);
        if (!XiaoKSettings.IsDiagnosticsMode)
        {
            var bundledWorker = Path.Combine(AppContext.BaseDirectory, "voice_worker.py");
            if (workspaceRoot is not null)
            {
                var sourceWorker = Path.Combine(workspaceRoot, "src", "XiaoK.Voice", "voice_worker.py");
                var workerPath = File.Exists(bundledWorker) ? bundledWorker : sourceWorker;
                _voiceInference = VoiceInferenceService.TryCreateForWorkspace(workspaceRoot,
                    _settings.VoiceEnvironmentRoot, workerPath, _models, out _voiceStatus);
            }
            else if (WindowsPackageIdentity.IsPresent)
            {
                _voiceInference = VoiceInferenceService.TryCreateForInstallation(_settings.VoiceEnvironmentRoot,
                    _settings.ModelRoot, AppContext.BaseDirectory, packageIdentityVerified: true, _models, out _voiceStatus);
            }
            else
            {
                _voiceStatus = "语音：开发模式未找到小K仓库；请从本地项目启动。麦克风未采集。";
            }
        }
        else
        {
            _voiceStatus = "诊断模式：语音模型和麦克风均关闭。";
        }
        var apps = _settings.Applications.Select(x => new DesktopApp(x.Id, x.Executable, x.WorkingDirectory));
        var roots = _settings.SearchRoots.Select(x => new KeyValuePair<string, string>(x.Id, x.Path));
        var recoveryRoot = XiaoKSettings.IsDiagnosticsMode
            ? Path.Combine(_settings.DataRoot, "AppContainerRecovery")
            : null;
        _dotNetTestRunner = new DotNetTestRunner(XiaoKSettings.FindWorkspace(AppContext.BaseDirectory), recoveryRoot);
        var codeAgent = new CodeTaskAgent(_inference, _models, XiaoKSettings.FindWorkspace(AppContext.BaseDirectory), _dotNetTestRunner);
        _broker = new ToolBroker(new WindowsDesktopTools(apps, roots,
                exportRoot: Path.Combine(_settings.DataRoot, "Exports")), _inference, _models, approval,
            codeAgent, _settings.CodeProjectRoot, _settings.CodeWorkspaceRoot,
            approval as IMessageSendPreviewPresenter);
        _userTaskWorker = Task.Run(ProcessUserTaskQueueAsync);
        _noticeAnalysisWorker = ProcessNoticeAnalysisQueueAsync();
    }

    public string ModelStatus => XiaoKSettings.IsDiagnosticsMode
        ? "诊断模式：临时设置与数据库；通知、语音采集和模型推理均关闭"
        : _managedModelRuntime?.Status
            ?? "本地模型：" + _settings.InferenceEndpoint + "（仅回环地址；手动运行本地服务；未连接时不会转云端）";
    public string VoiceStatus => XiaoKSettings.IsDiagnosticsMode
        ? _voiceStatus
        : _microphone.IsActive ? "语音：正在采集麦克风（最长60秒）"
        : _settings.WakeWordEnabled && _wakeWordListener is not null ? _wakeWordListener.Status
        : _voiceInference is null ? _voiceStatus : _voiceInference.Status;
    public bool IsMicrophoneActive => _microphone.IsActive;
    public int PendingUserTaskCount => Volatile.Read(ref _pendingUserTaskCount);
    public bool HasActiveUserTask => Volatile.Read(ref _active) is not null;
    public string? StartupIsolationNotice => !_dotNetTestRunner.StartupIsolationRecovery.Success
        || _dotNetTestRunner.StartupIsolationRecovery.RecoveredProfiles > 0
        ? _dotNetTestRunner.StartupIsolationRecovery.Message
        : null;
    public XiaoKSettings CurrentSettings => _settings;
    public string ActiveDatabasePath => Path.Combine(_settings.DataRoot, "tasks.sqlite3");
    public event Action<PrivateNoticeAnalysisResult>? PrivateNoticeAnalysisCompleted;
    public event Action<byte[]>? SpeechCaptureMaximumDurationReached;
    public event Action? WakeWordDetected;
    public event Action<string>? WakeWordStatusChanged;
    public event Action? MicrophoneStoppedForSessionLock;
    public event Action<Guid, TaskLifecycleState>? UserTaskStateChanged;

    public Task StartMicrophoneAsync(CancellationToken cancellationToken)
    {
        if (_voiceInference is null) throw new InvalidOperationException(_voiceStatus);
        return StartMicrophoneCoreAsync(cancellationToken);
    }

    private async Task StartMicrophoneCoreAsync(CancellationToken cancellationToken)
    {
        EnsureUnlockedMicrophoneSession();
        try
        {
            if (_wakeWordListener is not null) await _wakeWordListener.PauseForDictationAsync(cancellationToken);
            EnsureUnlockedMicrophoneSession();
            await _microphone.StartAsync(cancellationToken);
        }
        catch
        {
            await ResumeWakeWordAfterDictationAsync();
            throw;
        }
    }

    public Task<byte[]> StopMicrophoneAndReadAsync() => _microphone.StopAndReadAsync();

    public void UpdateNotificationSettings(XiaoKSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        _settings = _settings with
        {
            MonitorWeChatNotifications = settings.MonitorWeChatNotifications,
            MonitorQQNotifications = settings.MonitorQQNotifications,
            WakeWordEnabled = settings.WakeWordEnabled,
            WeChatPublisherAppIds = [.. settings.WeChatPublisherAppIds],
            QQPublisherAppIds = [.. settings.QQPublisherAppIds]
        };
    }

    public async Task<string> ApplyWakeWordSettingAsync(bool enabled)
    {
        if (_wakeWordListener is null) return "诊断模式已禁用唤醒监听。";
        if (enabled && _microphone.IsActive)
            return "当前语音采集完成后才会启动唤醒词监听；录音期间不会并行监听。";
        return await _wakeWordListener.SetEnabledAsync(enabled);
    }

    public async Task ResumeWakeWordAfterDictationAsync()
    {
        if (_wakeWordListener is null || _microphone.IsActive || Volatile.Read(ref _stopping) != 0) return;
        await _wakeWordListener.SetEnabledAsync(_settings.WakeWordEnabled);
    }

    public async Task<bool> StopAllMicrophoneAsync()
    {
        var disableWakeWord = _settings.WakeWordEnabled;
        // Prevent an in-flight dictation completion from re-enabling wake detection mid-stop.
        if (disableWakeWord) _settings = _settings with { WakeWordEnabled = false };

        // Signal the foreground recorder before waiting on Windows wake-word teardown.
        var captureStop = _microphone.StopImmediatelyAndDiscardAsync();
        var failures = new List<Exception>();
        var issues = new List<string>();
        if (_wakeWordListener is not null)
        {
            try { await _wakeWordListener.SetEnabledAsync(false); }
            catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
            {
                failures.Add(ex);
                issues.Add("唤醒监听未能确认关闭");
            }
        }

        var wasCapturing = false;
        try { wasCapturing = await captureStop; }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        {
            failures.Add(ex);
            issues.Add("Windows 未能确认录音设备已释放");
        }

        if (disableWakeWord)
        {
            try { _settings.Save(); }
            catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
            {
                failures.Add(ex);
                issues.Add("唤醒监听关闭状态未能保存");
            }
        }

        if (failures.Count > 0)
            throw new InvalidOperationException(
                "已发出停麦请求，但操作未能完全确认：" + string.Join("；", issues)
                + "。请核对小K状态和 Windows 麦克风隐私指示。",
                new AggregateException(failures));
        return wasCapturing;
    }

    private static void EnsureUnlockedMicrophoneSession()
    {
        if (!WindowsNotificationMonitor.IsUnlockedInputDesktop())
            throw new InvalidOperationException("Windows 会话已锁定；小K不会启动麦克风采集。");
    }

    private void OnSessionSwitch(object? sender, SessionSwitchEventArgs args)
    {
        if (args.Reason != SessionSwitchReason.SessionLock) return;
        _microphone.StopImmediatelyAndDiscard();
        MicrophoneStoppedForSessionLock?.Invoke();
    }

    public bool QueueVerifiedPrivateNotice(MessageNotice notice, CancellationToken monitoringSession,
        out string status)
    {
        ArgumentNullException.ThrowIfNull(notice);
        if (Volatile.Read(ref _stopping) != 0)
        {
            status = "小K正在退出；通知没有排入分析。";
            return false;
        }

        var settings = _settings;
        var allowedPublisherIds = string.Equals(notice.ApplicationId, "wechat", StringComparison.OrdinalIgnoreCase)
            ? settings.MonitorWeChatNotifications ? settings.WeChatPublisherAppIds : []
            : string.Equals(notice.ApplicationId, "qq", StringComparison.OrdinalIgnoreCase)
                ? settings.MonitorQQNotifications ? settings.QQPublisherAppIds : []
                : [];
        if (!PrivateNoticeAnalysisPolicy.TryCreateProposal(notice, allowedPublisherIds,
                DateTimeOffset.UtcNow, out var proposal) || proposal is null)
        {
            status = "通知未通过本地私聊分析准入检查；正文没有送入模型。";
            return false;
        }

        CancellationTokenSource workLifetime;
        try { workLifetime = CancellationTokenSource.CreateLinkedTokenSource(_noticeAnalysisStop.Token, monitoringSession); }
        catch (ObjectDisposedException)
        {
            status = "通知监听已停止；正文没有排入分析。";
            return false;
        }
        if (workLifetime.IsCancellationRequested)
        {
            workLifetime.Dispose();
            status = "通知监听已停止；正文没有排入分析。";
            return false;
        }

        _noticeAnalysisWorkItems.TryAdd(workLifetime, 0);
        if (!_noticeAnalysisQueue.Writer.TryWrite(new NoticeAnalysisWork(notice.ApplicationId, proposal, workLifetime)))
        {
            _noticeAnalysisWorkItems.TryRemove(workLifetime, out _);
            workLifetime.Dispose();
            status = "本地通知分析队列已满；本条通知未排队，请手动查看会话。";
            return false;
        }

        status = string.Equals(notice.ApplicationId, "wechat", StringComparison.OrdinalIgnoreCase)
            ? "微信私聊通知已排入本地分析队列。"
            : "QQ 私聊通知已排入本地分析队列。";
        return true;
    }

    public async Task<IReadOnlyList<TaskHistoryEntry>> GetRecentTaskHistoryAsync(CancellationToken cancellationToken)
    {
        var records = (await _store.GetRecentAsync(30, cancellationToken))
            .Select(record => TaskHistoryRecoveryPolicy.ForDisplay(record, _processStartedAtUtc));
        var history = records.Select(record => new TaskHistoryEntry(
            record.Id,
            $"{record.Summary} · {record.Id.ToString("N")[..8]}",
            TaskStateLabel(record.Status), record.UpdatedAtUtc,
            _transientUserTaskResults.TryGetValue(record.Id, out var transientResult) ? transientResult
            : record.ErrorCode == TaskHistoryRecoveryPolicy.HostRestartedErrorCode
                ? "小K在上次任务完成前退出；不会自动重试。请手动核对相关应用或项目状态。"
                : record.ErrorCode is null ? "仅保留任务状态，不保存请求正文或模型回答。" : $"错误类别：{record.ErrorCode}",
            IsCancellableTaskState(record.Status) && _userTaskOperations.ContainsKey(record.Id),
            _transientUserTaskSteps.TryGetValue(record.Id, out var step) ? step : TaskStepForStoredState(record.Status)))
            .ToList();

        var codeTasks = await Task.Run(() => CodeTaskAgent.ReadRetainedTasks(_settings.CodeWorkspaceRoot), cancellationToken);
        history.AddRange(codeTasks.Select(task =>
        {
            var interrupted = TaskHistoryRecoveryPolicy.IsInterruptedCodeTask(
                task.State, task.UpdatedAtUtc, _processStartedAtUtc);
            return new TaskHistoryEntry(
                null,
                $"隔离编程任务 · {task.TaskId[^8..]}",
                interrupted ? "上次中断，需核对" : CodeTaskStateLabel(task.State), task.UpdatedAtUtc,
                interrupted
                    ? $"小K不会自动续跑。请检查隔离工作区后手动决定下一步：{task.WorkspacePath}"
                    : $"隔离工作区：{task.WorkspacePath}", false,
                interrupted ? "上次运行中断；不会自动恢复或重试。" : $"编程任务：{CodeTaskStateLabel(task.State)}");
        }));

        return history.OrderByDescending(item => item.UpdatedAtUtc).Take(35).ToArray();
    }

    public async Task<string> SubmitAsync(string input)
    {
        var completion = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var admission = await QueueUserTaskAsync(input, completion, CancellationToken.None);
        return admission.Accepted ? await completion.Task : admission.Message;
    }

    public Task<TaskQueueAdmission> QueueUserTaskAsync(string input, CancellationToken cancellationToken = default) =>
        QueueUserTaskAsync(input, null, cancellationToken);

    private async Task<TaskQueueAdmission> QueueUserTaskAsync(string input,
        TaskCompletionSource<string>? completion, CancellationToken cancellationToken)
    {
        if (XiaoKSettings.IsDiagnosticsMode)
            return new(false, null, "诊断模式只用于界面检查；桌面操作、文件访问和模型推理均未执行。");
        if (Volatile.Read(ref _stopping) != 0)
            return new(false, null, "小K正在退出，暂不接受新任务。");
        var request = input.Trim();
        if (request.Length == 0) return new(false, null, "先输入一句话，或用 Ctrl+Shift+K 打开小K。");
        if (request.Length > 8_000) return new(false, null, "单项任务文字不能超过 8,000 个字符。");

        var id = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var category = Classify(request);
        var task = new TaskRecord(id, category, CategoryLabel(category), TaskLifecycleState.Queued, now, now);
        if (!await TrySaveStateAsync(task, cancellationToken))
            return new(false, null, "无法保存本地任务状态，本次操作未排队。请检查 D 盘数据目录。");

        var operation = new CancellationTokenSource();
        var shuttingDown = false;
        var taskIdCollision = false;
        var queueFull = false;
        lock (_userTaskAdmissionLock)
        {
            if (Volatile.Read(ref _stopping) != 0)
            {
                shuttingDown = true;
                operation.Dispose();
            }
            else if (!_userTaskOperations.TryAdd(id, operation))
            {
                taskIdCollision = true;
                operation.Dispose();
            }
            else
            {
                if (completion is not null) _userTaskCompletions[id] = completion;
                Interlocked.Increment(ref _pendingUserTaskCount);
                SetTransientTaskStep(id, "等待本机交互任务执行权");
                if (!_userTaskQueue.Writer.TryWrite(new UserTaskWork(id, task.Kind, request, task.CreatedAtUtc, operation)))
                {
                    _transientUserTaskSteps.TryRemove(id, out _);
                    _userTaskOperations.TryRemove(id, out _);
                    _userTaskCompletions.TryRemove(id, out _);
                    Interlocked.Decrement(ref _pendingUserTaskCount);
                    operation.Dispose();
                    queueFull = true;
                }
            }
        }

        if (shuttingDown)
        {
            task = task with { Status = TaskLifecycleState.Cancelled, UpdatedAtUtc = DateTimeOffset.UtcNow, ErrorCode = "SHUTTING_DOWN" };
            await TrySaveStateAsync(task, CancellationToken.None);
            return new(false, null, "小K正在退出，本次操作没有排队或执行。");
        }
        if (taskIdCollision)
        {
            task = task with { Status = TaskLifecycleState.Failed, UpdatedAtUtc = DateTimeOffset.UtcNow, ErrorCode = "TASK_ID_COLLISION" };
            await TrySaveStateAsync(task, CancellationToken.None);
            return new(false, null, "无法创建唯一任务编号；本次操作未排队。");
        }
        if (queueFull)
        {
            task = task with { Status = TaskLifecycleState.Failed, UpdatedAtUtc = DateTimeOffset.UtcNow, ErrorCode = "QUEUE_FULL" };
            await TrySaveStateAsync(task, CancellationToken.None);
            PublishUserTaskStateChanged(id, task.Status);
            return new(false, null, "小K的本地任务队列已满，本次操作没有执行；请稍后再试。");
        }

        PublishUserTaskStateChanged(id, TaskLifecycleState.Queued);
        return new(true, id, $"任务已加入队列（{id.ToString("N")[..8]}）。运行和完成状态可在任务中心查看。");
    }

    public bool CancelTask(Guid taskId)
    {
        if (!_userTaskOperations.TryGetValue(taskId, out var operation)) return false;
        try
        {
            if (operation.IsCancellationRequested) return true;
            operation.Cancel();
            return true;
        }
        catch (ObjectDisposedException) { return false; }
    }

    private async Task ProcessUserTaskQueueAsync()
    {
        await foreach (var work in _userTaskQueue.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            try { await ExecuteUserTaskAsync(work).ConfigureAwait(false); }
            catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException
                and not AccessViolationException)
            {
                // One failed task must not terminate the serial worker and strand later queue entries.
                var uncertain = new TaskRecord(work.Id, work.Category, CategoryLabel(work.Category),
                    TaskLifecycleState.OutcomeUncertain, work.CreatedAtUtc, DateTimeOffset.UtcNow,
                    ErrorCode: "WORKER_EXCEPTION");
                await TrySaveStateAsync(uncertain, CancellationToken.None).ConfigureAwait(false);
                var operationWasTracked = _userTaskOperations.TryRemove(work.Id, out _);
                _userTaskCompletions.TryRemove(work.Id, out var completion);
                try { work.Lifetime.Dispose(); }
                catch (ObjectDisposedException) { }
                if (operationWasTracked) Interlocked.Decrement(ref _pendingUserTaskCount);
                completion?.TrySetResult("任务执行器异常退出；请打开任务中心核对状态。");
                PublishUserTaskStateChanged(work.Id, TaskLifecycleState.OutcomeUncertain);
            }
        }
    }

    private async Task ExecuteUserTaskAsync(UserTaskWork work)
    {
        var task = new TaskRecord(work.Id, work.Category, CategoryLabel(work.Category),
            TaskLifecycleState.Queued, work.CreatedAtUtc, work.CreatedAtUtc);
        var finalState = TaskLifecycleState.Failed;
        var output = "任务失败。为保护隐私，故障内容未写入日志。";
        var gateEntered = false;
        var finalStateSaved = false;
        CancellationTokenSource? timeout = null;
        CancellationTokenSource? linked = null;

        try
        {
            timeout = new CancellationTokenSource(TimeSpan.FromMinutes(5));
            linked = CancellationTokenSource.CreateLinkedTokenSource(work.Lifetime.Token, timeout.Token);
            Interlocked.Exchange(ref _active, work.Lifetime);
            await _executionGate.WaitAsync(linked.Token).ConfigureAwait(false);
            gateEntered = true;
            linked.Token.ThrowIfCancellationRequested();
            if (Volatile.Read(ref _stopping) != 0) throw new OperationCanceledException(linked.Token);

            task = task with { Status = TaskLifecycleState.Planning, UpdatedAtUtc = DateTimeOffset.UtcNow };
            SetTransientTaskStep(work.Id, "正在分析请求并选择本地处理路径");
            if (!await TrySaveStateAsync(task, linked.Token).ConfigureAwait(false))
            {
                finalState = TaskLifecycleState.Failed;
                task = task with { ErrorCode = "STATE_WRITE_FAILED" };
                output = "无法保存任务状态，本次操作未执行。请检查 D 盘数据目录。";
            }
            else
            {
                PublishUserTaskStateChanged(work.Id, task.Status);
                task = task with { Status = TaskLifecycleState.Running, UpdatedAtUtc = DateTimeOffset.UtcNow };
                SetTransientTaskStep(work.Id, "正在执行本地任务步骤；结束后会显示结果或待办");
                if (!await TrySaveStateAsync(task, linked.Token).ConfigureAwait(false))
                {
                    finalState = TaskLifecycleState.Failed;
                    task = task with { ErrorCode = "STATE_WRITE_FAILED" };
                    output = "无法更新任务状态，本次操作未执行。请检查 D 盘数据目录。";
                }
                else
                {
                    PublishUserTaskStateChanged(work.Id, task.Status);
                    var result = await RouteAsync(work.Category, work.Request, linked.Token).ConfigureAwait(false);
                    finalState = result.FinalState ?? (result.Success ? TaskLifecycleState.Completed : TaskLifecycleState.Failed);
                    SetTransientTaskStep(work.Id, TaskStepForStoredState(finalState));
                    output = result.Success
                        ? result.FinalState == TaskLifecycleState.AwaitingApproval
                            ? $"{result.Summary}{Environment.NewLine}{Environment.NewLine}{result.Data}"
                            : result.Data ?? result.Summary
                        : $"{result.Summary}{(result.ErrorCode is null ? "" : $" [{result.ErrorCode}]")}";
                    task = task with { Status = finalState, UpdatedAtUtc = DateTimeOffset.UtcNow, ErrorCode = result.ErrorCode };
                    finalStateSaved = await TrySaveStateAsync(task, CancellationToken.None).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException)
        {
            finalState = work.Lifetime.IsCancellationRequested || Volatile.Read(ref _stopping) != 0
                ? TaskLifecycleState.Cancelled : TaskLifecycleState.Failed;
            task = task with
            {
                Status = finalState,
                UpdatedAtUtc = DateTimeOffset.UtcNow,
                ErrorCode = finalState == TaskLifecycleState.Cancelled ? "CANCELLED" : "TIMEOUT"
            };
            output = finalState == TaskLifecycleState.Cancelled ? "已取消该任务；不会自动重试。"
                : "任务等待或执行超过 5 分钟，已停止；请在任务中心核对状态。";
        }
        catch (Exception)
        {
            finalState = TaskLifecycleState.Failed;
            task = task with { Status = finalState, UpdatedAtUtc = DateTimeOffset.UtcNow, ErrorCode = "INTERNAL" };
            output = "任务失败。为保护隐私，故障内容未写入日志。";
        }
        finally
        {
            if (linked is null || !finalStateSaved)
            {
                task = task with { Status = finalState, UpdatedAtUtc = DateTimeOffset.UtcNow };
                finalStateSaved = await TrySaveStateAsync(task, CancellationToken.None).ConfigureAwait(false);
                if (!finalStateSaved) output += "（最终状态未能写入本地历史）";
            }
            if (gateEntered) _executionGate.Release();
            Interlocked.CompareExchange(ref _active, null, work.Lifetime);
            linked?.Dispose();
            timeout?.Dispose();
            _userTaskOperations.TryRemove(work.Id, out _);
            work.Lifetime.Dispose();
            Interlocked.Decrement(ref _pendingUserTaskCount);
            SetTransientTaskStep(work.Id, TaskStepForStoredState(finalState));
            RememberTransientTaskResult(work.Id, output);
            PublishUserTaskStateChanged(work.Id, finalState);
            if (_userTaskCompletions.TryRemove(work.Id, out var completion)) completion.TrySetResult(output);
        }
    }

    private void RememberTransientTaskResult(Guid taskId, string result)
    {
        lock (_transientResultLock)
        {
            _transientUserTaskResults[taskId] = result.Length <= 16_000 ? result : result[..16_000] + "…（结果已截断）";
            _transientResultOrder.Enqueue(taskId);
            while (_transientResultOrder.Count > 20)
            {
                var expiredTaskId = _transientResultOrder.Dequeue();
                _transientUserTaskResults.TryRemove(expiredTaskId, out _);
                _transientUserTaskSteps.TryRemove(expiredTaskId, out _);
            }
        }
    }

    private void SetTransientTaskStep(Guid taskId, string step)
    {
        if (step.Length > 240) step = step[..240];
        _transientUserTaskSteps[taskId] = step;
    }

    private static string TaskStepForStoredState(TaskLifecycleState state) => state switch
    {
        TaskLifecycleState.Queued => "等待本机交互任务执行权",
        TaskLifecycleState.Planning => "正在分析请求并选择本地处理路径",
        TaskLifecycleState.AwaitingApproval => "等待你查看并处理任务中心待办",
        TaskLifecycleState.Running => "正在执行本地任务步骤；结束后会显示结果或待办",
        TaskLifecycleState.Verifying => "正在核验操作结果",
        TaskLifecycleState.Completed => "任务已完成",
        TaskLifecycleState.Failed => "任务失败；查看状态说明后决定下一步",
        TaskLifecycleState.Cancelled => "任务已取消；不会自动重试",
        TaskLifecycleState.OutcomeUncertain => "结果待人工核对；不会自动重试",
        _ => "阶段未知；请核对任务状态"
    };

    private void PublishUserTaskStateChanged(Guid taskId, TaskLifecycleState state)
    {
        var handlers = UserTaskStateChanged;
        if (handlers is null) return;
        foreach (Action<Guid, TaskLifecycleState> handler in handlers.GetInvocationList())
        {
            try { handler(taskId, state); }
            catch (Exception) { }
        }
    }

    private static bool IsCancellableTaskState(TaskLifecycleState state) => state is
        TaskLifecycleState.Queued or TaskLifecycleState.Planning or TaskLifecycleState.Running
        or TaskLifecycleState.Verifying or TaskLifecycleState.AwaitingApproval;

    public Task<bool> StopMicrophoneAndDiscardAsync() => _microphone.StopImmediatelyAndDiscardAsync();

    public void CancelCurrent(bool stopMicrophone = true)
    {
        if (stopMicrophone) StopMicrophone();
        try { Volatile.Read(ref _active)?.Cancel(); }
        catch (ObjectDisposedException) { }
        foreach (var work in _noticeAnalysisWorkItems.Keys)
        {
            try { work.Cancel(); }
            catch (ObjectDisposedException) { }
        }
    }

    public bool StopMicrophone()
    {
        return _microphone.StopImmediatelyAndDiscard();
    }

    public Task<SpeechRecognition> TranscribeLocalWavAsync(ReadOnlyMemory<byte> wav, bool forceChinese,
        CancellationToken cancellationToken) => _voiceInference?.TranscribeWavAsync(wav, forceChinese, cancellationToken)
        ?? Task.FromException<SpeechRecognition>(new InvalidOperationException(_voiceStatus));

    public Task<byte[]> SynthesizeChineseWavAsync(string text, CancellationToken cancellationToken) =>
        _voiceInference?.SynthesizeChineseWavAsync(text, cancellationToken)
        ?? Task.FromException<byte[]>(new InvalidOperationException(_voiceStatus));

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _stopping, 1) != 0) return;
        SystemEvents.SessionSwitch -= OnSessionSwitch;
        CancelCurrent();
        lock (_userTaskAdmissionLock)
        {
            foreach (var operation in _userTaskOperations.Values)
            {
                try { operation.Cancel(); }
                catch (ObjectDisposedException) { }
            }
            _userTaskQueue.Writer.TryComplete();
        }
        await _userTaskWorker.ConfigureAwait(false);
        _noticeAnalysisQueue.Writer.TryComplete();
        _noticeAnalysisStop.Cancel();
        await _noticeAnalysisWorker.ConfigureAwait(false);
        _noticeAnalysisStop.Dispose();
        await _executionGate.WaitAsync();
        try
        {
            if (_wakeWordListener is not null) await _wakeWordListener.DisposeAsync();
            await _microphone.DisposeAsync();
            if (_voiceInference is not null) await _voiceInference.DisposeAsync();
            if (_managedModelRuntime is not null) await _managedModelRuntime.DisposeAsync();
        }
        finally
        {
            _inference.Dispose();
            _transientUserTaskResults.Clear();
            _transientUserTaskSteps.Clear();
            _executionGate.Release();
        }
    }

    private void OnMicrophoneMaximumDurationReached(byte[] wav)
    {
        var handler = SpeechCaptureMaximumDurationReached;
        if (handler is null)
        {
            CryptographicOperations.ZeroMemory(wav);
            return;
        }
        try { handler(wav); }
        catch (Exception) { CryptographicOperations.ZeroMemory(wav); }
    }

    private async Task ProcessNoticeAnalysisQueueAsync()
    {
        await foreach (var work in _noticeAnalysisQueue.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            using (work.Lifetime)
            {
                try
                {
                    if (work.Lifetime.IsCancellationRequested) continue;
                    ToolResult result;
                    try { result = await _broker.ExecuteBackgroundAsync(work.Proposal, work.Lifetime.Token).ConfigureAwait(false); }
                    catch (OperationCanceledException) when (work.Lifetime.IsCancellationRequested) { continue; }
                    catch (Exception) { result = new(false, "本地通知分析失败；正文未写入历史或日志。", "NOTICE_ANALYSIS_FAILED"); }

                    if (!work.Lifetime.IsCancellationRequested)
                        PrivateNoticeAnalysisCompleted?.Invoke(new PrivateNoticeAnalysisResult(
                            work.ApplicationId, result.Success, result.Success ? result.Data ?? result.Summary : result.Summary));
                }
                finally { _noticeAnalysisWorkItems.TryRemove(work.Lifetime, out _); }
            }
        }
    }
    private async Task<ToolResult> RouteAsync(string category, string request, CancellationToken token)
    {
        var lower = request.ToLowerInvariant();
        if (category == "window")
        {
            var intent = AppLaunchIntentResolver.ResolveWindowActivation(request);
            if (intent is null)
                return new(false, "无法确定要切换到哪个受支持的已打开窗口。支持 VS Code 小K项目、Edge、资源管理器、微信和 QQ。", "WINDOW_TARGET_NOT_SUPPORTED");
            var args = ImmutableDictionary<string, string>.Empty.Add("app_id", intent.AppId);
            return await _broker.ExecuteAsync(new ToolProposal("window.activate.v1", args, intent.AppId,
                ToolPrecondition.ApplicationAllowlisted | ToolPrecondition.ExistingWindow,
                ToolExpectedOutcome.TargetWindowInForeground), token);
        }

        if (category == "app")
        {
            var intent = AppLaunchIntentResolver.Resolve(request);
            if (intent is null)
                return new(false, "无法确定要打开哪个受支持的应用。当前支持 VS Code 小K项目、Edge、资源管理器、微信和 QQ。", "APP_NOT_SUPPORTED");
            var args = ImmutableDictionary<string, string>.Empty.Add("app_id", intent.AppId);
            if (intent.WorkspaceId is not null) args = args.Add("workspace_id", intent.WorkspaceId);
            return await _broker.ExecuteAsync(new ToolProposal("app.launch.v1", args, intent.AppId,
                ToolPrecondition.ApplicationAllowlisted, ToolExpectedOutcome.ApplicationWindowVisible), token);
        }

        if (category == "file")
        {
            var query = request;
            foreach (var prefix in new[] { "帮我找文件", "搜索文件", "查找文件", "找文件", "搜索", "查找" })
                if (query.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) { query = query[prefix.Length..].Trim(' ', '：', ':', '“', '”', '"'); break; }
            return await _broker.ExecuteAsync(new ToolProposal("file.search.v1",
                ImmutableDictionary<string, string>.Empty.Add("query", query).Add("root_id", "user-files"), "user-files",
                ToolPrecondition.ConfiguredSearchRoot, ToolExpectedOutcome.MatchingFilesListed), token);
        }

        if (category == "file-copy")
        {
            var sourcePath = ExtractFileCopySource(request);
            if (!LocalFileCopyPolicy.IsValidSourcePath(sourcePath))
                return new(false, "请用“复制文件：完整源文件路径”指定一个本机文件。小K只会复制搜索范围内的单个文件到固定导出目录。", "INVALID_SOURCE_PATH");
            var arguments = ImmutableDictionary<string, string>.Empty.Add("source_path", sourcePath);
            return await _broker.ExecuteBackgroundAsync(new ToolProposal("file.copy.v1", arguments,
                "configured-export", ToolPrecondition.ConfiguredSearchRoot | ToolPrecondition.ConfiguredFileExportRoot,
                ToolExpectedOutcome.FileCopiedToConfiguredExport), token);
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
                "用户本次提供的单条消息", ToolPrecondition.UserProvidedSingleMessage,
                category == "draft" ? ToolExpectedOutcome.ReplyDraftOnly : ToolExpectedOutcome.LocalMessageAnalysis), token);
        }

        if (category == "send")
        {
            if (!MessageSendIntentResolver.TryResolve(request, out var intent, out var errorCode) || intent is null)
            {
                var message = errorCode == "SEND_ATTACHMENTS_UNSUPPORTED"
                    ? "当前只支持无附件的发送预览；附件选择与身份核验尚未接入，本次没有读取或发送文件。"
                    : "当前仅允许“发送微信给 L：正文”或“发送QQ给 K：正文”。其他收件人或平台组合会被拒绝；本版本只显示预览，不会发送。";
                return new(false, message, errorCode ?? "SEND_FORMAT_INVALID");
            }

            var arguments = ImmutableDictionary<string, string>.Empty
                .Add("application_id", intent.ApplicationId)
                .Add("recipient", intent.Recipient)
                .Add("text", intent.Text)
                .Add("attachments", "none");
            return await _broker.ExecuteAsync(new ToolProposal("message.send.v1", arguments,
                $"{intent.ApplicationId}:{intent.Recipient}", ToolPrecondition.CompleteMessagePreview,
                ToolExpectedOutcome.MessageSendPreviewShown), token);
        }

        if (category == "code-inspect")
        {
            var instruction = ExtractPayload(request, new[] { "查找代码", "搜索代码", "解释代码", "分析代码", "读代码" });
            if (instruction.Length == 0)
                return new(false, "请补充要在所选项目中查找或解释的内容。", "EMPTY_CODE_QUERY");
            return await _broker.ExecuteAsync(new ToolProposal("code.inspect.v1",
                ImmutableDictionary<string, string>.Empty.Add("instruction", instruction), "configured-project",
                ToolPrecondition.ConfiguredProjectAndIsolatedWorkspace,
                ToolExpectedOutcome.CodeExplanationReturned), token);
        }

        if (category == "code")
            return await _broker.ExecuteAsync(new ToolProposal("code.task.create.v1",
                ImmutableDictionary<string, string>.Empty.Add("instruction", request), "configured-project",
                ToolPrecondition.ConfiguredProjectAndIsolatedWorkspace,
                ToolExpectedOutcome.ReviewablePatchCreated), token);

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
        if (AppLaunchIntentResolver.IsWindowActivationRequest(request)) return "window";
        if (lower.StartsWith("复制文件") || lower.StartsWith("把文件复制到小k导出目录")) return "file-copy";
        if (lower.StartsWith("找文件") || lower.StartsWith("查找文件") || lower.StartsWith("搜索文件") || lower.StartsWith("搜索") || lower.StartsWith("帮我找文件")) return "file";
        if (lower.StartsWith("分析消息") || lower.StartsWith("分析聊天") || lower.StartsWith("理解聊天") || lower.StartsWith("解释这条消息") || lower.StartsWith("分析：") || lower.StartsWith("分析:")) return "analyze";
        if (lower.StartsWith("帮我回复") || lower.StartsWith("起草回复") || lower.StartsWith("回复草稿") || lower.StartsWith("帮我回")) return "draft";
        if (lower.StartsWith("发送") || lower.StartsWith("发给")
            || lower.StartsWith("发微信给") || lower.StartsWith("发qq给")) return "send";
        if (lower.StartsWith("查找代码") || lower.StartsWith("搜索代码") || lower.StartsWith("解释代码")
            || lower.StartsWith("分析代码") || lower.StartsWith("读代码")) return "code-inspect";
        if (lower.Contains("写代码") || lower.Contains("改代码") || lower.Contains("开发任务") || lower.Contains("编程任务")) return "code";
        if (lower.StartsWith("打开") || lower.StartsWith("启动")) return "app";
        return "chat";
    }

    private static string CategoryLabel(string category) => category switch
    {
        "app" => "应用操作", "window" => "窗口切换", "file" => "文件查找", "file-copy" => "文件复制", "analyze" => "消息分析", "draft" => "回复草稿",
        "send" => "发送请求", "code-inspect" => "只读代码检索", "code" => "本地编程任务", _ => "本地对话"
    };

    private sealed record NoticeAnalysisWork(string ApplicationId, ToolProposal Proposal,
        CancellationTokenSource Lifetime);
    private sealed record UserTaskWork(Guid Id, string Category, string Request, DateTimeOffset CreatedAtUtc,
        CancellationTokenSource Lifetime);

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
        "applying" => "应用中", "outcome_uncertain" => "结果待核对",
        "completed" => "已完成",
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

    private static string ExtractFileCopySource(string request)
    {
        var prefixes = new[] { "把文件复制到小K导出目录", "复制文件" };
        foreach (var prefix in prefixes)
        {
            if (!request.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
            return request[prefix.Length..].Trim(' ', '，', ',', '：', ':', '“', '”', '"', '\'');
        }
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
                settingsPropertyRemoved = LegacySettingsPrivacyCleanup.RemoveIfUnchanged(
                    XiaoKSettings.GetSettingsPath(), approvedPreview.LegacySettings);
            }
            catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException
                or InvalidDataException or InvalidOperationException or NotSupportedException or System.Text.Json.JsonException)
            {
                settingsCleanupError = ex.Message;
            }

            var fileResult = ManagedPrivacyFileCleanup.DeleteIfUnchanged(approvedPreview.ManagedFiles);
            return new(databaseCompacted, settingsPropertyRemoved,
                approvedPreview.LegacySettings.HasContactStylesProperty
                    || approvedPreview.LegacySettings.PetWindowPositionPropertyCount > 0,
                fileResult.DeletedCount, approvedPreview.ManagedFiles.SkippedEntries,
                fileResult.FailedFileNames, fileResult.PlanChanged, settingsCleanupError);
        }
        finally { _executionGate.Release(); }
    }

    private async Task<LocalDataCleanupPreview> BuildLocalDataCleanupPreviewAsync(CancellationToken cancellationToken)
    {
        var database = await _store.GetPersonalDataSummaryAsync(cancellationToken);
        var settings = LegacySettingsPrivacyCleanup.Preview(XiaoKSettings.GetSettingsPath());
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
    private static XiaoKSettings? _diagnosticsSettings;
    private static string? _diagnosticsSettingsPath;

    internal static bool IsDiagnosticsMode => _diagnosticsSettings is not null;

    internal bool ContactStylesMigrationSourceAvailable { get; init; } = true;
    public string DataRoot { get; init; } = @"D:\XiaoK\Data";
    public string ModelRoot { get; init; } = @"D:\XiaoK\Models";
    public string VoiceEnvironmentRoot { get; init; } = @"D:\XiaoK\Voice";
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
    public bool WakeWordEnabled { get; init; }
    public List<string> WeChatPublisherAppIds { get; init; } = [];
    public List<string> QQPublisherAppIds { get; init; } = [];
    public List<ContactReplyStylePreference> ContactReplyStyles { get; init; } = [];
    public List<AppSetting> Applications { get; init; } = [];
    public List<RootSetting> SearchRoots { get; init; } = [];

    internal static void EnableDiagnosticsProfile(string root)
    {
        if (IsDiagnosticsMode) throw new InvalidOperationException("诊断配置已经启用。");
        var fullRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var tempRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath()));
        var tempPrefix = tempRoot + Path.DirectorySeparatorChar;
        const string profilePrefix = "XiaoK-Diagnostics-";
        var profileName = Path.GetFileName(fullRoot);
        if (!fullRoot.StartsWith(tempPrefix, StringComparison.OrdinalIgnoreCase)
            || !profileName.StartsWith(profilePrefix, StringComparison.Ordinal)
            || !Guid.TryParseExact(profileName[profilePrefix.Length..], "N", out _)
            || Directory.Exists(fullRoot) || File.Exists(fullRoot))
            throw new ArgumentException("诊断配置必须位于临时目录中新建的唯一小K目录。", nameof(root));

        Directory.CreateDirectory(fullRoot);
        if ((File.GetAttributes(fullRoot) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("诊断配置目录不能是重解析点。");

        _diagnosticsSettingsPath = Path.Combine(fullRoot, "settings.json");
        _diagnosticsSettings = new XiaoKSettings
        {
            DataRoot = Path.Combine(fullRoot, "Data"),
            ModelRoot = Path.Combine(fullRoot, "Models"),
            VoiceEnvironmentRoot = Path.Combine(fullRoot, "Voice"),
            EvaluationRoot = Path.Combine(fullRoot, "Evaluations"),
            CodeProjectRoot = "",
            CodeWorkspaceRoot = Path.Combine(fullRoot, "Workspaces"),
            InferenceEndpoint = "http://127.0.0.1:0/",
            MonitorWeChatNotifications = false,
            MonitorQQNotifications = false,
            WakeWordEnabled = false,
            WeChatPublisherAppIds = [],
            QQPublisherAppIds = [],
            ContactReplyStyles = [],
            Applications = [],
            SearchRoots = []
        };
        _diagnosticsSettings.Save();
    }

    public static XiaoKSettings Load()
    {
        if (_diagnosticsSettings is { } diagnosticsSettings) return diagnosticsSettings;
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
        var path = GetSettingsPath();
        if (IsDiagnosticsMode) _diagnosticsSettings = this;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporaryPath = path + ".tmp";
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true };
        File.WriteAllText(temporaryPath, JsonSerializer.Serialize(this, options), new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        // File.Replace can fail with ERROR_INVALID_PARAMETER for an MSIX app's
        // redirected LocalCache settings path. Both files live in the same
        // directory, so an overwrite move keeps the replacement on one volume.
        File.Move(temporaryPath, path, overwrite: true);
    }

    internal static string GetSettingsPath() => _diagnosticsSettingsPath ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "XiaoK", "settings.json");

    private static XiaoKSettings CreateDefaults()
    {
        var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        var documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        var downloads = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
        var detectedWorkspace = FindWorkspace(AppContext.BaseDirectory);
        var environmentWorkspace = Environment.GetEnvironmentVariable("XIAOK_PROJECT_ROOT")?.Trim() ?? "";
        var workspace = detectedWorkspace is not null && LocalSearchRootPolicy.IsLocalDrivePath(detectedWorkspace)
            ? detectedWorkspace
            : (environmentWorkspace.Length > 0 && LocalSearchRootPolicy.IsLocalDrivePath(environmentWorkspace)
                && Directory.Exists(environmentWorkspace)
                && File.Exists(Path.Combine(environmentWorkspace, "XiaoK.sln"))
                    ? Path.GetFullPath(environmentWorkspace)
                    : "");
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        var code = FindExistingLocalExecutable(
            @"D:\VS Code\Code.exe",
            Path.Combine(localAppData, "Programs", "Microsoft VS Code", "Code.exe"),
            Path.Combine(programFiles, "Microsoft VS Code", "Code.exe"),
            Path.Combine(programFilesX86, "Microsoft VS Code", "Code.exe"));
        var edge = FindExistingLocalExecutable(
            Path.Combine(programFilesX86, "Microsoft", "Edge", "Application", "msedge.exe"),
            Path.Combine(programFiles, "Microsoft", "Edge", "Application", "msedge.exe"));
        var wechat = FindExistingLocalExecutable(@"D:\微信\Weixin\Weixin.exe");
        var qq = FindExistingLocalExecutable(@"D:\QQ\QQ.exe");
        var apps = new List<AppSetting> { new("explorer", "explorer.exe", null) };
        if (code.Length > 0 && workspace.Length > 0) apps.Add(new AppSetting("vscode", code, workspace));
        if (edge.Length > 0) apps.Add(new AppSetting("edge", edge, null));
        if (wechat.Length > 0) apps.Add(new AppSetting("wechat", wechat, null));
        if (qq.Length > 0) apps.Add(new AppSetting("qq", qq, null));
        var roots = new[] { ("Desktop", desktop), ("Documents", documents), ("Downloads", downloads) }
            .Where(x => !string.IsNullOrWhiteSpace(x.Item2)).Select(x => new RootSetting(x.Item1, x.Item2)).ToList();
        var workspaceModelRoot = FindWorkspace(AppContext.BaseDirectory);
        return new XiaoKSettings
        {
            ModelRoot = workspaceModelRoot is null
                ? @"D:\XiaoK\Models"
                : Path.Combine(workspaceModelRoot, "models", "llm", "qwen3.5-4b", "f9f88ac3e234be915e23811a6d28ea287bdb927e"),
            VoiceEnvironmentRoot = workspaceModelRoot is null
                ? @"D:\XiaoK\Voice"
                : Path.Combine(workspaceModelRoot, ".tools", "venvs"),
            Applications = apps,
            SearchRoots = roots
        };
    }

    private static string FindExistingLocalExecutable(params string[] candidates) => candidates
        .FirstOrDefault(path => File.Exists(path) && LocalSearchRootPolicy.IsLocalDrivePath(path)) ?? "";

    private XiaoKSettings WithDefaults(XiaoKSettings defaults) => this with
    {
        DataRoot = string.IsNullOrWhiteSpace(DataRoot) ? defaults.DataRoot : DataRoot,
        ModelRoot = string.IsNullOrWhiteSpace(ModelRoot) ? defaults.ModelRoot : ModelRoot,
        VoiceEnvironmentRoot = string.IsNullOrWhiteSpace(VoiceEnvironmentRoot) ? defaults.VoiceEnvironmentRoot : VoiceEnvironmentRoot,
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
internal sealed record TaskHistoryEntry(Guid? TaskId, string Title, string State, DateTimeOffset UpdatedAtUtc,
    string Detail, bool CanCancel, string CurrentStep);
internal sealed record TaskQueueAdmission(bool Accepted, Guid? TaskId, string Message);
internal sealed record LocalDataCleanupPreview(SqlitePersonalDataSummary Database,
    LegacySettingsCleanupSnapshot LegacySettings, ManagedPrivacyFilesPlan ManagedFiles);
internal sealed record LocalDataCleanupResult(bool DatabaseCompacted, bool LegacySettingsPropertyRemoved,
    bool LegacySettingsPropertiesWerePresent, int DeletedManagedFiles, int SkippedManagedFiles,
    IReadOnlyList<string> FailedManagedFileNames, bool ManagedFilePlanChanged, string? LegacySettingsCleanupError);
