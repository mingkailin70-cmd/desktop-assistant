using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Threading;
using Windows.UI.Notifications;
using Windows.UI.Notifications.Management;
using XiaoK.Adapters.Windows;
using XiaoK.Core;

namespace XiaoK.Host;

/// <summary>
/// Foreground-only notification listener. Only source identity metadata is inspected;
/// conversation classification remains fail-closed until verified against client samples.
/// </summary>
internal sealed class WindowsNotificationMonitor : IDisposable
{
    private const int MaximumRememberedNotifications = 4096;
    private const int MaximumPendingAddedNotifications = 256;
    private readonly Dispatcher _dispatcher;
    private readonly HashSet<string> _seenNotificationKeys = new(StringComparer.Ordinal);
    private readonly Queue<string> _seenNotificationOrder = [];
    private readonly Queue<uint> _pendingAddedIds = [];
    private UserNotificationListener? _listener;
    private MessageNoticePolicy? _policy;
    private IMessageNoticeAdapter? _wechatAdapter;
    private IMessageNoticeAdapter? _qqAdapter;
    private HashSet<string> _allowedAppIds = new(StringComparer.OrdinalIgnoreCase);
    private HashSet<string> _wechatPublisherIds = new(StringComparer.OrdinalIgnoreCase);
    private HashSet<string> _qqPublisherIds = new(StringComparer.OrdinalIgnoreCase);
    private CancellationTokenSource? _sessionCancellation;
    private bool _baselineReady;
    private bool _disposed;

    public WindowsNotificationMonitor(Dispatcher dispatcher) => _dispatcher = dispatcher;

    public string Status { get; private set; } = "通知监听关闭。";
    public event Action<string>? StatusChanged;
    public event Action<MessageNotice, CancellationToken>? PrivateNoticeAccepted;

    public async Task<string> RequestPermissionAsync()
    {
        if (!_dispatcher.CheckAccess())
            return "通知权限请求必须从小K的界面线程发起。";
        if (_disposed) return "小K正在退出。";
        if (XiaoKSettings.IsDiagnosticsMode)
            return SetStatus("诊断模式关闭通知监控和系统通知授权。");

        try
        {
            if (!WindowsPackageIdentity.IsPresent)
                return SetStatus("需要先以具有 MSIX 身份的安装包安装小K，才能请求 Windows 通知权限。当前普通目录运行不具备该身份。");

            var listener = UserNotificationListener.Current;
            var access = listener.GetAccessStatus();
            if (access != UserNotificationListenerAccessStatus.Allowed)
                access = await listener.RequestAccessAsync();
            return SetStatus(FormatAccessStatus(access));
        }
        catch (Exception ex) when (IsRecoverable(ex))
        {
            return SetStatus("无法请求 Windows 通知权限；请确认 MSIX 安装和 Windows 通知服务状态。没有读取通知内容。");
        }
    }

    public async Task<string> ApplySettingsAsync(XiaoKSettings settings)
    {
        if (!_dispatcher.CheckAccess())
            throw new InvalidOperationException("通知监听配置必须在小K界面线程应用。");
        if (_disposed) return SetStatus("通知监听已停止。");

        if (XiaoKSettings.IsDiagnosticsMode)
        {
            StopListening();
            _allowedAppIds.Clear();
            return SetStatus("诊断模式关闭微信 / QQ 通知监听。");
        }

        StopListening();
        var wechatIds = settings.MonitorWeChatNotifications ? settings.WeChatPublisherAppIds : [];
        var qqIds = settings.MonitorQQNotifications ? settings.QQPublisherAppIds : [];
        _wechatPublisherIds = new HashSet<string>(wechatIds, StringComparer.OrdinalIgnoreCase);
        _qqPublisherIds = new HashSet<string>(qqIds, StringComparer.OrdinalIgnoreCase);
        if (MessageNoticePublisherAssignments.HasOverlap(_wechatPublisherIds, _qqPublisherIds))
        {
            _allowedAppIds.Clear();
            return SetStatus("同一个发布者 AUMID 不能同时配置为微信和 QQ；通知监听保持关闭。");
        }
        _allowedAppIds = new HashSet<string>(wechatIds.Concat(qqIds), StringComparer.OrdinalIgnoreCase);
        if (_allowedAppIds.Count == 0) return SetStatus("微信 / QQ 通知监听关闭，或尚未配置已核实的发布者 AUMID。");

        try
        {
            if (!WindowsPackageIdentity.IsPresent)
                return SetStatus("监控开关已保存；运行通知监听需要具有 MSIX 身份的安装包。当前普通目录运行不会读取通知。");

            _listener = UserNotificationListener.Current;
            var access = _listener.GetAccessStatus();
            if (access != UserNotificationListenerAccessStatus.Allowed)
                return SetStatus(FormatAccessStatus(access));

            _policy = new MessageNoticePolicy(wechatIds, qqIds);
            _wechatAdapter = new WeChatNoticeAdapter(_policy);
            _qqAdapter = new QQNoticeAdapter(_policy);
            _sessionCancellation = new CancellationTokenSource();
            _baselineReady = false;
            _pendingAddedIds.Clear();
            _seenNotificationKeys.Clear();
            _seenNotificationOrder.Clear();
            _listener.NotificationChanged += OnNotificationChanged;

            // Establish a baseline so notifications already present at startup are not treated as new.
            var current = await _listener.GetNotificationsAsync(NotificationKinds.Toast);
            foreach (var notification in current)
            {
                var sourceAppId = notification.AppInfo.AppUserModelId;
                if (_allowedAppIds.Contains(sourceAppId)) RememberNotification(sourceAppId, notification.Id);
            }
            _baselineReady = true;
            var pending = _pendingAddedIds.ToArray();
            _pendingAddedIds.Clear();
            foreach (var id in pending.Distinct()) await ProcessAddedAsync(id);

            return SetStatus("Windows 通知监听已启用；只筛选配置的微信 / QQ 发布者。私聊格式仍待验证，当前不会读取正文或送模型。");
        }
        catch (Exception ex) when (IsRecoverable(ex))
        {
            StopListening();
            return SetStatus("Windows 通知监听未能启动；当前不会读取通知。请检查 MSIX 身份、权限和 Windows 版本。");
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        StopListening();
        _allowedAppIds.Clear();
        _wechatPublisherIds.Clear();
        _qqPublisherIds.Clear();
        Status = "通知监听已停止。";
    }

    private void OnNotificationChanged(UserNotificationListener sender, UserNotificationChangedEventArgs args)
    {
        if (args.ChangeKind != UserNotificationChangedKind.Added || _disposed) return;
        var id = args.UserNotificationId;
        if (!_dispatcher.CheckAccess())
        {
            _dispatcher.BeginInvoke(new Action(() => QueueOrProcessAdded(id)));
            return;
        }
        QueueOrProcessAdded(id);
    }

    private void QueueOrProcessAdded(uint id)
    {
        if (_disposed) return;
        if (!_baselineReady)
        {
            if (_pendingAddedIds.Count < MaximumPendingAddedNotifications) _pendingAddedIds.Enqueue(id);
            else RaiseStatus("通知变化暂存数量达到上限；本次未处理更多通知。请手动查看会话。");
            return;
        }
        _ = ProcessAddedAsync(id);
    }

    private async Task ProcessAddedAsync(uint id)
    {
        try
        {
            var listener = _listener;
            var policy = _policy;
            var sessionCancellation = _sessionCancellation;
            var wechatAdapter = _wechatAdapter;
            var qqAdapter = _qqAdapter;
            if (_disposed || listener is null || policy is null || sessionCancellation is null) return;
            if (listener.GetAccessStatus() != UserNotificationListenerAccessStatus.Allowed)
            {
                StopForRevokedPermission(listener);
                return;
            }

            var notifications = await listener.GetNotificationsAsync(NotificationKinds.Toast);
            if (_disposed || !ReferenceEquals(_listener, listener) || !ReferenceEquals(_policy, policy)
                || !ReferenceEquals(_sessionCancellation, sessionCancellation)) return;
            if (listener.GetAccessStatus() != UserNotificationListenerAccessStatus.Allowed)
            {
                StopForRevokedPermission(listener);
                return;
            }

            foreach (var notification in notifications.Where(item => item.Id == id))
            {
                // AppUserModelId is the only notification content metadata read before the publisher allowlist check.
                var sourceAppId = notification.AppInfo.AppUserModelId;
                if (string.IsNullOrWhiteSpace(sourceAppId) || !_allowedAppIds.Contains(sourceAppId)) continue;
                if (!RememberNotification(sourceAppId, notification.Id)) continue;

                var key = $"{notification.Id}:{notification.CreationTime.UtcDateTime.Ticks}";
                var unlocked = IsUnlockedInputDesktop();
                if (!unlocked)
                {
                    RaiseStatus("Windows 会话已锁定；通知正文未读取，请手动查看会话。");
                    continue;
                }

                var adapter = _wechatPublisherIds.Contains(sourceAppId) ? wechatAdapter
                    : _qqPublisherIds.Contains(sourceAppId) ? qqAdapter
                    : null;
                if (adapter is null) continue;

                var bodyGateFailure = NoticeBodyAccessFailure.None;
                string? ReadBodyIfStillAllowed() => NoticeBodyReadGate.ReadIfAllowed(
                    () => !_disposed && ReferenceEquals(_listener, listener)
                        && listener.GetAccessStatus() == UserNotificationListenerAccessStatus.Allowed,
                    IsUnlockedInputDesktop,
                    () => ReadVisibleText(notification),
                    out bodyGateFailure);

                var decision = adapter.Inspect(sourceAppId, null, null, null, ReadBodyIfStillAllowed,
                    notification.CreationTime, key, true, true);

                if (bodyGateFailure == NoticeBodyAccessFailure.PermissionUnavailable)
                {
                    StopForRevokedPermission(listener);
                    return;
                }
                if (bodyGateFailure == NoticeBodyAccessFailure.SessionLocked)
                {
                    RaiseStatus("Windows 会话已锁定；通知正文未读取，请手动查看会话。");
                    continue;
                }

                if (!decision.Accepted) continue;

                var status = adapter.ApplicationId == "wechat"
                    ? $"微信通知：{decision.UserMessage}"
                    : $"QQ 通知：{decision.UserMessage}";
                RaiseStatus(status);
                if (decision.AnalyzeBody && decision.Notice is { IsPrivateConversation: true, Body: { Length: > 0 } noticeBody }
                    && !sessionCancellation.IsCancellationRequested)
                {
                    // The body is handed off synchronously so the receiver can bind work to this monitor session.
                    PrivateNoticeAccepted?.Invoke(decision.Notice with { Body = noticeBody }, sessionCancellation.Token);
                }
            }
        }
        catch (Exception ex) when (IsRecoverable(ex))
        {
            RaiseStatus("读取 Windows 通知元数据失败；未保存正文或转发通知内容。");
        }
    }

    private void StopForRevokedPermission(UserNotificationListener listener)
    {
        if (!ReferenceEquals(_listener, listener)) return;
        SetStatus("Windows 通知访问权限已撤销；已暂停通知处理。");
        StopListening();
    }

    private void StopListening()
    {
        var sessionCancellation = _sessionCancellation;
        _sessionCancellation = null;
        if (sessionCancellation is not null)
        {
            try { sessionCancellation.Cancel(); }
            finally { sessionCancellation.Dispose(); }
        }
        if (_listener is not null) _listener.NotificationChanged -= OnNotificationChanged;
        _listener = null;
        _policy = null;
        _wechatAdapter = null;
        _qqAdapter = null;
        _baselineReady = false;
        _pendingAddedIds.Clear();
        _seenNotificationKeys.Clear();
        _seenNotificationOrder.Clear();
    }

    private string SetStatus(string value)
    {
        Status = value;
        RaiseStatus(value);
        return value;
    }

    private bool RememberNotification(string sourceAppId, uint notificationId)
    {
        var key = $"{sourceAppId}\0{notificationId}";
        if (!_seenNotificationKeys.Add(key)) return false;
        _seenNotificationOrder.Enqueue(key);
        if (_seenNotificationOrder.Count > MaximumRememberedNotifications)
            _seenNotificationKeys.Remove(_seenNotificationOrder.Dequeue());
        return true;
    }

    private void RaiseStatus(string value)
    {
        Status = value;
        if (_dispatcher.CheckAccess()) StatusChanged?.Invoke(value);
        else _dispatcher.BeginInvoke(new Action(() => StatusChanged?.Invoke(value)));
    }

    private static string FormatAccessStatus(UserNotificationListenerAccessStatus access) => access switch
    {
        UserNotificationListenerAccessStatus.Allowed => "Windows 通知访问权限已授予。",
        UserNotificationListenerAccessStatus.Denied => "Windows 通知访问权限被拒绝或已撤销；请到 Windows 设置中手动允许。",
        _ => "Windows 通知权限尚未决定。请使用按钮发起请求。"
    };

    private static string? ReadVisibleText(UserNotification notification)
    {
        var binding = notification.Notification.Visual.GetBinding(KnownNotificationBindings.ToastGeneric);
        if (binding is null) return null;
        var elements = binding.GetTextElements();
        return string.Join(Environment.NewLine, elements.Skip(1).Select(element => element.Text));
    }

    private static bool IsUnlockedInputDesktop()
    {
        var desktop = OpenInputDesktop(0, false, DesktopReadObjects);
        if (desktop == IntPtr.Zero) return false;
        try
        {
            _ = GetUserObjectInformation(desktop, UserObjectName, null, 0, out var requiredBytes);
            if (requiredBytes < 4 || requiredBytes > 4096) return false;
            var name = new StringBuilder((int)(requiredBytes / 2));
            return GetUserObjectInformation(desktop, UserObjectName, name, requiredBytes, out _)
                && string.Equals(name.ToString(), "Default", StringComparison.OrdinalIgnoreCase);
        }
        finally { _ = CloseDesktop(desktop); }
    }

    private static bool IsRecoverable(Exception ex) => ex is not OutOfMemoryException and not AccessViolationException;

    private const uint DesktopReadObjects = 0x0001;
    private const int UserObjectName = 2;
    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr OpenInputDesktop(uint flags, bool inherit, uint desiredAccess);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool CloseDesktop(IntPtr desktop);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool GetUserObjectInformation(IntPtr handle, int index, StringBuilder? information, uint length, out uint needed);
}
