using System.Windows.Threading;
using Windows.UI.Notifications;
using Windows.UI.Notifications.Management;
using XiaoK.Adapters.Windows;
using XiaoK.Core;

namespace XiaoK.Host;

internal sealed record NotificationFormatSample(
    int Sequence,
    string ApplicationId,
    string PublisherDisplayName,
    string PublisherAppUserModelId,
    DateTimeOffset CreatedAtUtc,
    IReadOnlyList<string> TextElements,
    bool WasTruncated);

/// <summary>
/// A user-started, in-memory-only capture of new toast text from one previously observed publisher.
/// It never sends samples to inference, writes them to disk, or interacts with the source application.
/// </summary>
internal sealed class NotificationFormatSampleCapture : IDisposable
{
    public const int MaximumSamples = 30;
    private const int MaximumTrackedNotificationKeys = 4096;
    private static readonly TimeSpan MaximumDuration = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan SessionStateCheckInterval = TimeSpan.FromSeconds(2);

    private readonly Dispatcher _dispatcher;
    private readonly UserNotificationListener _listener;
    private readonly string _applicationId;
    private readonly string _publisherAppUserModelId;
    private readonly string _publisherDisplayName;
    private DateTimeOffset _startedAtUtc;
    private readonly BoundedNotificationKeySet _baselineKeys = new(MaximumTrackedNotificationKeys);
    private readonly BoundedNotificationKeySet _seenKeys = new(MaximumTrackedNotificationKeys);
    private readonly BoundedNotificationIdQueue _pendingIds = new(128);
    private readonly List<NotificationFormatSample> _samples = [];
    private readonly CancellationTokenSource _timeoutCancellation = new();
    private bool _baselineReady;
    private bool _processing;
    private bool _disposed;
    private int _pendingNotificationOverflow;

    public NotificationFormatSampleCapture(Dispatcher dispatcher, UserNotificationListener listener,
        string applicationId, string publisherDisplayName, string publisherAppUserModelId)
    {
        _dispatcher = dispatcher;
        _listener = listener;
        _applicationId = applicationId;
        _publisherDisplayName = publisherDisplayName;
        _publisherAppUserModelId = publisherAppUserModelId;
    }

    public bool IsActive { get; private set; }
    public string Status { get; private set; } = "尚未启动通知样本核验。";
    public IReadOnlyList<NotificationFormatSample> Samples => _samples.ToArray();
    public event Action<NotificationFormatSample>? SampleCaptured;
    public event Action<string>? StatusChanged;

    public async Task<string> StartAsync()
    {
        if (!_dispatcher.CheckAccess()) return "通知样本诊断必须从小K设置界面发起。";
        if (_disposed) return "通知样本诊断已关闭。";
        if (_listener.GetAccessStatus() != UserNotificationListenerAccessStatus.Allowed)
            return "Windows 通知访问权限当前未授予；没有开始采集。";

        try
        {
            _startedAtUtc = DateTimeOffset.UtcNow;
            IsActive = true;
            _listener.NotificationChanged += OnNotificationChanged;
            var existing = await _listener.GetNotificationsAsync(NotificationKinds.Toast);
            if (_listener.GetAccessStatus() != UserNotificationListenerAccessStatus.Allowed)
            {
                Stop("Windows 通知访问权限已撤销；样本采集已停止。", clearSamples: true);
                return "Windows 通知访问权限已撤销；样本采集未开始。";
            }

            if (!NotificationStartupBaselinePolicy.IsSafeBaselineCount(existing.Count))
            {
                Stop("当前 Toast 数量超过安全样本基线上限512条；本轮核验已停止，未读取正文。请稍后再试。", clearSamples: true);
                return Status;
            }

            foreach (var notification in existing)
            {
                var sourceAppId = notification.AppInfo.AppUserModelId;
                if (!string.Equals(sourceAppId, _publisherAppUserModelId, StringComparison.OrdinalIgnoreCase)) continue;
                if (NotificationStartupBaselinePolicy.ShouldAddToBaseline(notification.CreationTime, _startedAtUtc))
                {
                    var result = _baselineKeys.TryAdd(CreateKey(sourceAppId, notification.Id, notification.CreationTime));
                    if (result == NotificationKeyTrackResult.CapacityReached)
                    {
                        Stop("所选来源的现存通知超过安全跟踪上限；本轮核验已停止，未读取正文。", clearSamples: true);
                        return Status;
                    }
                }
                else
                    QueueAdded(notification.Id);
                if (!IsActive) return Status;
            }

            _baselineReady = true;
            _ = ExpireAfterMaximumDurationAsync(_timeoutCancellation.Token);
            var message = $"已开始{GetApplicationDisplayName()}通知样本核验。最长15分钟、最多采集30条新通知；正文只在内存和本机设置页显示，不送模型、不保存文件。小K每2秒检查一次通知权限和会话锁定状态，发现异常会停止并清除样本；关闭设置页也会清除。";
            RaiseStatus(message);
            StartDrain();
            return message;
        }
        catch (Exception ex) when (IsRecoverable(ex))
        {
            Stop("无法读取通知元数据；样本采集已停止。", clearSamples: true);
            return "无法开始通知样本核验；没有保存通知正文。请检查 MSIX 身份和 Windows 通知权限。";
        }
    }

    public void Stop(string message = "用户已停止通知样本核验。样本仍只保留在当前进程内存中。", bool clearSamples = false)
    {
        if (_listener is not null) _listener.NotificationChanged -= OnNotificationChanged;
        IsActive = false;
        _baselineReady = false;
        _pendingIds.Clear();
        Interlocked.Exchange(ref _pendingNotificationOverflow, 0);
        if (clearSamples) _samples.Clear();
        if (clearSamples || _samples.Count == 0) _timeoutCancellation.Cancel();
        RaiseStatus(message);
    }

    public void Clear()
    {
        Stop("通知样本已从内存中清除。", clearSamples: true);
        _baselineKeys.Clear();
        _seenKeys.Clear();
    }

    public void Dispose()
    {
        if (_disposed) return;
        Stop("小K已退出；未保存的通知样本已清除。", clearSamples: true);
        _disposed = true;
        _baselineKeys.Clear();
        _seenKeys.Clear();
        _timeoutCancellation.Dispose();
        SampleCaptured = null;
        StatusChanged = null;
    }

    private void OnNotificationChanged(UserNotificationListener sender, UserNotificationChangedEventArgs args)
    {
        if (args.ChangeKind != UserNotificationChangedKind.Added || !IsActive || _disposed
            || !ReferenceEquals(sender, _listener)) return;
        var id = args.UserNotificationId;
        if (_dispatcher.CheckAccess()) QueueAdded(id);
        else
        {
            var result = _pendingIds.TryEnqueue(id);
            if (result == NotificationIdEnqueueResult.CapacityReached)
                Interlocked.Exchange(ref _pendingNotificationOverflow, 1);
            if (result is NotificationIdEnqueueResult.Added or NotificationIdEnqueueResult.CapacityReached)
                ScheduleAddedQueueWake();
        }
    }

    private void ScheduleAddedQueueWake()
    {
        if (!_pendingIds.TryReserveDispatcherWake()) return;
        try
        {
            _dispatcher.BeginInvoke(new Action(() =>
            {
                _pendingIds.ReleaseDispatcherWake();
                if (_disposed || !IsActive)
                {
                    _pendingIds.Clear();
                    return;
                }
                if (Interlocked.Exchange(ref _pendingNotificationOverflow, 0) != 0)
                {
                    Stop("通知样本队列达到上限，已停止本轮采集；未读取未排队通知的正文。请清除样本后再开始新一轮。", clearSamples: false);
                    return;
                }
                if (_baselineReady) StartDrain();
            }));
        }
        catch (InvalidOperationException)
        {
            _pendingIds.ReleaseDispatcherWake();
        }
    }

    private void QueueAdded(uint id)
    {
        if (!IsActive || _disposed) return;
        var result = _pendingIds.TryEnqueue(id);
        if (result == NotificationIdEnqueueResult.CapacityReached)
        {
            Stop("通知样本队列达到上限，已停止本轮采集；未读取未排队通知的正文。请清除样本后再开始新一轮。", clearSamples: false);
            return;
        }
        if (result == NotificationIdEnqueueResult.Added && _baselineReady) StartDrain();
    }

    private void StartDrain()
    {
        if (_disposed || !IsActive || !_baselineReady || _processing) return;
        _processing = true;
        _ = DrainAsync();
    }

    private async Task DrainAsync()
    {
        try
        {
            while (!_disposed && IsActive && _baselineReady && _pendingIds.TryDequeue(out var id))
            {
                try { await ProcessAddedAsync(id); }
                finally { _pendingIds.Complete(id); }
            }
        }
        finally
        {
            _processing = false;
            if (!_disposed && IsActive && _baselineReady && _pendingIds.Count > 0) StartDrain();
        }
    }

    private async Task ProcessAddedAsync(uint id)
    {
        try
        {
            // Keep added-ID draining asynchronous while resolving only the event's notification.
            await Task.Yield();

            if (!IsActive || _listener.GetAccessStatus() != UserNotificationListenerAccessStatus.Allowed)
            {
                Stop("Windows 通知访问权限已撤销；样本采集已停止并清除。", clearSamples: true);
                return;
            }

            var notification = _listener.GetNotification(id);
            if (!IsActive) return;
            if (_listener.GetAccessStatus() != UserNotificationListenerAccessStatus.Allowed)
            {
                Stop("读取期间 Windows 通知访问权限已撤销；样本采集已停止并清除。", clearSamples: true);
                return;
            }
            if (notification is null || notification.Id != id) return;

            // Read only source metadata until the exact AUMID selected in the consent dialog matches.
            var sourceAppId = notification.AppInfo.AppUserModelId;
            if (!string.Equals(sourceAppId, _publisherAppUserModelId, StringComparison.OrdinalIgnoreCase)) return;
            var key = CreateKey(sourceAppId, notification.Id, notification.CreationTime);
            if (_baselineKeys.Contains(key)) return;
            var trackingResult = _seenKeys.TryAdd(key);
            if (trackingResult == NotificationKeyTrackResult.AlreadyTracked) return;
            if (trackingResult == NotificationKeyTrackResult.CapacityReached)
            {
                var hasSamples = _samples.Count > 0;
                Stop(hasSamples
                        ? "本轮唯一通知数量达到安全跟踪上限；采集已停止，已有样本仍按15分钟时限清除。"
                        : "本轮唯一通知数量达到安全跟踪上限；采集已停止，没有保留通知正文。",
                    clearSamples: !hasSamples);
                return;
            }
            if (notification.CreationTime < _startedAtUtc) return;
            if (notification.CreationTime > DateTimeOffset.UtcNow)
            {
                RaiseStatus("通知时间戳晚于本机当前时间；该通知未采集，请核对系统时间。已有样本仍只在内存中保留。");
                return;
            }

            if (_listener.GetAccessStatus() != UserNotificationListenerAccessStatus.Allowed)
            {
                Stop("读取通知正文前 Windows 通知访问权限已撤销；正文未读取，已有样本已清除。", clearSamples: true);
                return;
            }
            if (!WindowsNotificationMonitor.IsUnlockedInputDesktop())
            {
                Stop("Windows 会话已锁定；已停止样本核验并清除，没有读取正文。", clearSamples: true);
                return;
            }

            var (elements, truncated) = ReadVisibleTextElements(notification);
            if (!IsActive) return;
            if (_listener.GetAccessStatus() != UserNotificationListenerAccessStatus.Allowed)
            {
                Stop("读取期间 Windows 通知访问权限已撤销；该样本和已有样本均已丢弃。", clearSamples: true);
                return;
            }
            if (!WindowsNotificationMonitor.IsUnlockedInputDesktop())
            {
                Stop("Windows 会话已锁定；该样本和已有样本已清除并停止核验。", clearSamples: true);
                return;
            }

            var sample = new NotificationFormatSample(_samples.Count + 1, _applicationId,
                _publisherDisplayName, _publisherAppUserModelId, notification.CreationTime,
                elements, truncated);
            _samples.Add(sample);
            SampleCaptured?.Invoke(sample);
            RaiseStatus($"通知样本核验：已在内存中采集 {_samples.Count}/{MaximumSamples} 条新通知；没有送模型或保存文件。样本最迟在本轮15分钟截止时清除。" );

            if (_samples.Count >= MaximumSamples)
            {
                Stop($"已达到单次上限 {MaximumSamples} 条；监听已停止。样本只在内存中保留，最多到本轮15分钟截止；关闭设置页或点击清除可提前删除。", clearSamples: false);
                return;
            }
        }
        catch (Exception ex) when (IsRecoverable(ex))
        {
            RaiseStatus("读取通知元数据失败；样本核验继续等待后续通知。没有保存正文。" );
        }
    }

    private async Task ExpireAfterMaximumDurationAsync(CancellationToken cancellationToken)
    {
        var expiresAtUtc = _startedAtUtc + MaximumDuration;
        while (true)
        {
            var remaining = expiresAtUtc - DateTimeOffset.UtcNow;
            var delay = remaining < SessionStateCheckInterval ? remaining : SessionStateCheckInterval;
            if (delay > TimeSpan.Zero)
            {
                try { await Task.Delay(delay, cancellationToken).ConfigureAwait(false); }
                catch (OperationCanceledException) { return; }
            }

            if (_dispatcher.HasShutdownStarted || _dispatcher.HasShutdownFinished) return;
            try
            {
                var shouldExit = await _dispatcher.InvokeAsync(() =>
                {
                    if (_disposed) return true;

                    UserNotificationListenerAccessStatus accessStatus;
                    try { accessStatus = _listener.GetAccessStatus(); }
                    catch (Exception ex) when (IsRecoverable(ex))
                    {
                        Stop("无法核实 Windows 通知权限；已停止并清除通知样本。", clearSamples: true);
                        return true;
                    }

                    if (accessStatus != UserNotificationListenerAccessStatus.Allowed)
                    {
                        Stop("Windows 通知访问权限已撤销；已停止并清除通知样本。", clearSamples: true);
                        return true;
                    }
                    if (!WindowsNotificationMonitor.IsUnlockedInputDesktop())
                    {
                        Stop("Windows 会话已锁定；已停止并清除通知样本。", clearSamples: true);
                        return true;
                    }

                    if (DateTimeOffset.UtcNow >= expiresAtUtc)
                    {
                        if (IsActive)
                            Stop("通知样本核验达到15分钟上限；监听已停止并清除内存样本。", clearSamples: true);
                        else if (_samples.Count > 0)
                        {
                            _samples.Clear();
                            RaiseStatus("通知样本核验达到15分钟上限；内存样本已清除。");
                        }
                        return true;
                    }

                    return !IsActive && _samples.Count == 0;
                });
                if (shouldExit) return;
            }
            catch (InvalidOperationException) { return; }
        }
    }

    private static (IReadOnlyList<string> Elements, bool Truncated) ReadVisibleTextElements(UserNotification notification)
    {
        var binding = notification.Notification.Visual.GetBinding(KnownNotificationBindings.ToastGeneric);
        if (binding is null) return ([], false);
        return NotificationSampleTextPolicy.Project(
            binding.GetTextElements().Select(element => element.Text));
    }

    private static string CreateKey(string? sourceAppId, uint notificationId, DateTimeOffset creationTime) =>
        $"{sourceAppId}\0{notificationId}\0{creationTime.UtcDateTime.Ticks}";

    private static string GetApplicationDisplayName(string applicationId) =>
        applicationId.Equals("wechat", StringComparison.OrdinalIgnoreCase) ? "微信" : "QQ";

    private string GetApplicationDisplayName() => GetApplicationDisplayName(_applicationId);

    private void RaiseStatus(string message)
    {
        Status = message;
        if (_dispatcher.CheckAccess()) StatusChanged?.Invoke(message);
        else
        {
            try { _dispatcher.BeginInvoke(new Action(() => StatusChanged?.Invoke(message))); }
            catch (InvalidOperationException) { }
        }
    }

    private static bool IsRecoverable(Exception ex) => ex is not OutOfMemoryException and not AccessViolationException;
}
