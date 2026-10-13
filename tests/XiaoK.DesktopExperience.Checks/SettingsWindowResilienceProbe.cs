using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using XiaoK.Core;
using XiaoK.Host;

namespace XiaoK.DesktopExperience.Checks;

internal static class SettingsWindowResilienceProbe
{
    private const string TemporaryDirectoryPrefix = "XiaoK-SettingsResilienceProbe-";

    public static IReadOnlyList<string> Run()
    {
        var failures = new List<string>();
        Exception? threadFailure = null;
        var thread = new Thread(() =>
        {
            try { RunOnSta(failures); }
            catch (Exception exception) { threadFailure = exception; }
        })
        {
            IsBackground = true,
            Name = "XiaoK settings resilience offscreen UI check"
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        if (!thread.Join(TimeSpan.FromSeconds(12)))
            return ["设置页异步恢复离屏UI检查超过12秒；工作线程设为后台且不会触碰用户窗口。"];
        if (threadFailure is not null)
            return [$"设置页异步恢复离屏UI检查异常：{threadFailure.GetType().Name}"];
        return failures;
    }

    private static void RunOnSta(List<string> failures)
    {
        var dispatcher = Dispatcher.CurrentDispatcher;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
        string? temporaryRoot = null;
        try
        {
            var rootPath = Path.Combine(Path.GetTempPath(), TemporaryDirectoryPrefix + Guid.NewGuid().ToString("N"));
            temporaryRoot = rootPath;
            Directory.CreateDirectory(rootPath);
            string CreateRoot(string name)
            {
                var path = Path.Combine(rootPath, name);
                Directory.CreateDirectory(path);
                return path;
            }

            var settings = new XiaoKSettings
            {
                DataRoot = CreateRoot("data"),
                ModelRoot = CreateRoot("models"),
                VoiceEnvironmentRoot = CreateRoot("voice"),
                EvaluationRoot = CreateRoot("evaluations"),
                CodeWorkspaceRoot = CreateRoot("workspace"),
                Applications = [],
                SearchRoots = [new RootSetting("search-root-01", CreateRoot("search"))]
            };

            var replaceGate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var replaceCalls = 0;
            var failLoginStartupRead = false;
            var failContactStyleLoad = false;
            var failPublisherInspection = true;
            var failApprovalAuditRead = false;
            var failCleanupPreview = false;
            var clearLocalDataCalls = 0;
            Task ReplaceContactStyles(IEnumerable<ContactReplyStylePreference> _, CancellationToken __)
            {
                replaceCalls++;
                return replaceGate.Task;
            }

            Task<IReadOnlyList<ContactReplyStylePreference>> LoadContactStyles(CancellationToken _) =>
                failContactStyleLoad
                    ? Task.FromException<IReadOnlyList<ContactReplyStylePreference>>(new Exception("SYNTHETIC_PRIVATE_STYLE_LOAD_ERROR"))
                    : Task.FromResult<IReadOnlyList<ContactReplyStylePreference>>([]);
            Task<LoginStartupSnapshot> ReadLoginStartupState() =>
                failLoginStartupRead
                    ? Task.FromException<LoginStartupSnapshot>(new Exception("SYNTHETIC_PRIVATE_STARTUP_READ_ERROR"))
                    : Task.FromResult(new LoginStartupSnapshot(true, false, "合成启动状态"));
            Task<string> RequestNotificationAccess() =>
                Task.FromException<string>(new Exception("SYNTHETIC_PRIVATE_NOTIFICATION_PERMISSION_ERROR"));
            Task<string> InspectNotificationPublishers() => failPublisherInspection
                ? Task.FromException<string>(new Exception("SYNTHETIC_PRIVATE_PUBLISHER_INSPECTION_ERROR"))
                : Task.FromResult("合成来源元数据检查完成。");
            Task<string> CreateBackup(string path, CancellationToken _) => Task.FromResult(path);
            Task<string> RestoreBackup(string path, CancellationToken _) => Task.FromResult(path);
            Task<IReadOnlyList<ApprovalAuditRecord>> LoadApprovalAudit(int _, CancellationToken __) =>
                failApprovalAuditRead
                    ? Task.FromException<IReadOnlyList<ApprovalAuditRecord>>(new Exception("SYNTHETIC_PRIVATE_AUDIT_READ_ERROR"))
                    : Task.FromResult<IReadOnlyList<ApprovalAuditRecord>>([]);
            Task<LocalDataCleanupPreview> GetCleanupPreview(CancellationToken _) => failCleanupPreview
                ? Task.FromException<LocalDataCleanupPreview>(new Exception("SYNTHETIC_PRIVATE_CLEANUP_PREVIEW_ERROR"))
                : Task.FromResult<LocalDataCleanupPreview>(null!);
            Task<LocalDataCleanupResult> ClearLocalData(LocalDataCleanupPreview _, CancellationToken __)
            {
                clearLocalDataCalls++;
                return Task.FromResult<LocalDataCleanupResult>(null!);
            }

            var notificationMonitor = new WindowsNotificationMonitor(dispatcher);
            var window = new SettingsWindow(settings, notificationMonitor, LoadContactStyles, ReplaceContactStyles,
                Path.Combine(rootPath, "active.sqlite3"), CreateBackup, RestoreBackup,
                LoadApprovalAudit, GetCleanupPreview, ClearLocalData, ReadLoginStartupState,
                RequestNotificationAccess, InspectNotificationPublishers);
            SetPrivateField(window, "_startupStateLoaded", true);
            SetPrivateField(window, "_contactStylesLoaded", true);

            var saveButton = window.FindName("SaveButton") as Button;
            var cancelButton = window.FindName("CancelButton") as Button;
            var form = window.FindName("SettingsFormScrollViewer") as ScrollViewer;
            var status = window.FindName("StatusText") as TextBlock;
            if (saveButton is null || cancelButton is null || form is null || status is null)
            {
                failures.Add("设置页没有找到保存/取消按钮、表单或状态控件。");
                return;
            }

            var saveClickHandler = typeof(SettingsWindow).GetMethod("Save_Click",
                BindingFlags.Instance | BindingFlags.NonPublic);
            if (saveClickHandler is null)
            {
                failures.Add("设置页没有找到保存事件处理器。");
                return;
            }
            void InvokeSaveClick() => dispatcher.Invoke(new Action(() =>
                saveClickHandler.Invoke(window, [saveButton, new RoutedEventArgs(Button.ClickEvent, saveButton)])));

            InvokeSaveClick();
            if (replaceCalls != 1 || saveButton.IsEnabled || cancelButton.IsEnabled || form.IsEnabled)
            {
                var statusCategory = status.Text == "正在保存设置…" ? "saving"
                    : status.Text.Contains("启动状态尚未确认", StringComparison.Ordinal) ? "startup-wait"
                    : status.Text.Contains("目录", StringComparison.Ordinal)
                        || status.Text.Contains("路径", StringComparison.Ordinal)
                        || status.Text.Contains("地址", StringComparison.Ordinal) ? "validation"
                    : "other";
                failures.Add($"异步保存锁状态不符（联系人写入调用={replaceCalls}，保存/取消/表单仍启用={saveButton.IsEnabled}/{cancelButton.IsEnabled}/{form.IsEnabled}，启动状态已加载={GetPrivateField<bool>(window, "_startupStateLoaded")}，联系人偏好已加载={GetPrivateField<bool>(window, "_contactStylesLoaded")}，提示类别={statusCategory}）。");
            }

            InvokeSaveClick();
            if (replaceCalls != 1)
                failures.Add("保存等待期间的重复点击再次启动了联系人偏好写入。");

            var closingHandler = typeof(SettingsWindow).GetMethod("SettingsWindow_Closing",
                BindingFlags.Instance | BindingFlags.NonPublic);
            if (closingHandler is null)
            {
                failures.Add("设置页没有安装保存期间的关闭保护处理器。");
            }
            else
            {
                var closing = new CancelEventArgs();
                dispatcher.Invoke(new Action(() => closingHandler.Invoke(window, [window, closing])));
                if (!closing.Cancel)
                    failures.Add("保存仍在等待时关闭窗口未被拦截。");

                SetPrivateField(window, "_allowCloseAfterSave", true);
                var successfulClose = new CancelEventArgs();
                dispatcher.Invoke(new Action(() => closingHandler.Invoke(window, [window, successfulClose])));
                if (successfulClose.Cancel)
                    failures.Add("保存成功后的正常窗口关闭仍被阻止。");
                SetPrivateField(window, "_allowCloseAfterSave", false);
            }

            replaceGate.SetException(new Exception("SYNTHETIC_PRIVATE_SAVE_ERROR"));
            PumpUntil(dispatcher, () => !GetPrivateField<bool>(window, "_saveInProgress"), failures,
                "合成保存异常后，设置页异步收尾没有在4秒内结束。");
            if (!saveButton.IsEnabled || !cancelButton.IsEnabled || !form.IsEnabled)
                failures.Add("保存失败后设置控件没有恢复可用状态。");
            if (GetPrivateField<bool>(window, "_saveInProgress"))
                failures.Add("保存失败后单次保存锁没有释放。");
            if (!status.Text.Contains("设置保存未能完成", StringComparison.Ordinal)
                || status.Text.Contains("SYNTHETIC_PRIVATE_SAVE_ERROR", StringComparison.Ordinal))
                failures.Add("意外保存异常没有被转换为固定提示，或异常详情泄漏到设置页。");

            var idleClose = new CancelEventArgs();
            if (closingHandler is not null)
                dispatcher.Invoke(new Action(() => closingHandler.Invoke(window, [window, idleClose])));
            if (idleClose.Cancel)
                failures.Add("保存完成后关闭保护仍处于锁定状态。");

            failLoginStartupRead = true;
            failContactStyleLoad = false;
            SetPrivateField(window, "_startupStateLoaded", false);
            SetPrivateField(window, "_contactStylesLoaded", false);
            var loadHandler = typeof(SettingsWindow).GetMethod("SettingsWindow_Loaded",
                BindingFlags.Instance | BindingFlags.NonPublic);
            var startupStatus = window.FindName("StartupStatusText") as TextBlock;
            var startupCheck = window.FindName("StartupCheck") as CheckBox;
            var setContactControls = typeof(SettingsWindow).GetMethod("SetContactStyleControlsEnabled",
                BindingFlags.Instance | BindingFlags.NonPublic);
            var contactStatus = window.FindName("ContactStyleStatusText") as TextBlock;
            var contactName = window.FindName("ContactStyleNameBox") as TextBox;
            var contactStyle = window.FindName("ContactStyleBox") as ComboBox;
            var addStyleButton = window.FindName("AddContactStyleButton") as Button;
            var removeStyleButton = window.FindName("RemoveContactStyleButton") as Button;
            var contactStyles = window.FindName("ContactStylesList") as ListBox;
            if (loadHandler is null || setContactControls is null || startupStatus is null || startupCheck is null
                || contactStatus is null || contactName is null || contactStyle is null
                || addStyleButton is null || removeStyleButton is null || contactStyles is null)
            {
                failures.Add("设置页没有找到初始化处理器或联系人风格状态控件。");
                return;
            }

            startupCheck.IsEnabled = true;
            dispatcher.Invoke(new Action(() => loadHandler.Invoke(window,
                [window, new RoutedEventArgs(FrameworkElement.LoadedEvent, window)])));
            PumpUntil(dispatcher, () => GetPrivateField<bool>(window, "_contactStylesLoaded"), failures,
                "合成登录启动状态读取异常后，设置页未完成后续联系人偏好加载。");
            if (GetPrivateField<bool>(window, "_startupStateLoaded") || startupCheck.IsEnabled
                || !startupStatus.Text.Contains("无法读取 Windows 登录启动状态", StringComparison.Ordinal)
                || !status.Text.Contains("重新打开设置页后重试", StringComparison.Ordinal)
                || status.Text.Contains("SYNTHETIC_PRIVATE_STARTUP_READ_ERROR", StringComparison.Ordinal))
                failures.Add("登录启动状态读取异常未安全停用该选项，或异常详情泄漏到设置页。");
            if (!contactName.IsEnabled || !contactStyle.IsEnabled || !addStyleButton.IsEnabled
                || !removeStyleButton.IsEnabled || !contactStyles.IsEnabled)
                failures.Add("登录启动状态读取失败后，独立的联系人偏好加载未正常完成。");

            failLoginStartupRead = false;
            failContactStyleLoad = true;
            SetPrivateField(window, "_startupStateLoaded", false);
            SetPrivateField(window, "_contactStylesLoaded", false);
            startupCheck.IsEnabled = true;
            dispatcher.Invoke(new Action(() => setContactControls.Invoke(window, [true])));
            if (!contactName.IsEnabled || !contactStyle.IsEnabled || !addStyleButton.IsEnabled
                || !removeStyleButton.IsEnabled || !contactStyles.IsEnabled)
                failures.Add("联系人偏好加载异常探针的表单预置状态未能启用。");

            dispatcher.Invoke(new Action(() => loadHandler.Invoke(window,
                [window, new RoutedEventArgs(FrameworkElement.LoadedEvent, window)])));
            PumpUntil(dispatcher, () => GetPrivateField<bool>(window, "_startupStateLoaded")
                    && string.Equals(contactStatus.Text,
                        "无法读取 SQLite 联系人偏好；为避免覆盖现有数据，此区域已停用。", StringComparison.Ordinal),
                failures, "合成SQLite加载异常后，设置页初始化没有在4秒内结束。");
            if (GetPrivateField<bool>(window, "_contactStylesLoaded") || contactName.IsEnabled
                || contactStyle.IsEnabled || addStyleButton.IsEnabled || removeStyleButton.IsEnabled || contactStyles.IsEnabled)
                failures.Add("联系人偏好读取失败后，编辑控件未保持失败关闭。");
            if (status.Text.Contains("SYNTHETIC_PRIVATE_STYLE_LOAD_ERROR", StringComparison.Ordinal)
                || !status.Text.Contains("其他设置仍可继续保存", StringComparison.Ordinal))
                failures.Add("联系人偏好加载异常没有转换为固定提示，或异常详情泄漏到设置页。");

            var requestNotificationAccessHandler = typeof(SettingsWindow).GetMethod("RequestNotificationAccess_Click",
                BindingFlags.Instance | BindingFlags.NonPublic);
            var requestNotificationAccessButton = window.FindName("RequestNotificationAccessButton") as Button;
            var notificationStatus = window.FindName("NotificationStatusText") as TextBlock;
            if (requestNotificationAccessHandler is null || requestNotificationAccessButton is null || notificationStatus is null)
            {
                failures.Add("设置页没有找到通知权限请求处理器或状态控件。");
                return;
            }
            dispatcher.Invoke(new Action(() => requestNotificationAccessHandler.Invoke(window,
                [requestNotificationAccessButton, new RoutedEventArgs(Button.ClickEvent, requestNotificationAccessButton)])));
            PumpUntil(dispatcher, () => requestNotificationAccessButton.IsEnabled
                    && notificationStatus.Text == "无法请求 Windows 通知访问权限；请检查系统设置后重试。",
                failures, "合成通知权限请求异常后，按钮或固定提示没有在4秒内恢复。");
            if (notificationStatus.Text.Contains("SYNTHETIC_PRIVATE_NOTIFICATION_PERMISSION_ERROR", StringComparison.Ordinal))
                failures.Add("通知权限请求异常详情泄漏到设置页。");

            var inspectPublishersHandler = typeof(SettingsWindow).GetMethod("InspectNotificationPublishers_Click",
                BindingFlags.Instance | BindingFlags.NonPublic);
            var inspectPublishersButton = window.FindName("InspectNotificationPublishersButton") as Button;
            var publisherCandidates = window.FindName("NotificationPublisherCandidatesBox") as TextBox;
            var samplePublisher = window.FindName("NotificationSamplePublisherBox") as ComboBox;
            var startSampleButton = window.FindName("StartNotificationSampleCaptureButton") as Button;
            if (inspectPublishersHandler is null || inspectPublishersButton is null || publisherCandidates is null
                || samplePublisher is null || startSampleButton is null)
            {
                failures.Add("设置页没有找到通知来源检查处理器或样本状态控件。");
                return;
            }
            dispatcher.Invoke(new Action(() => inspectPublishersHandler.Invoke(window,
                [inspectPublishersButton, new RoutedEventArgs(Button.ClickEvent, inspectPublishersButton)])));
            PumpUntil(dispatcher, () => inspectPublishersButton.IsEnabled
                    && publisherCandidates.Text == "只读来源检查失败；未读取通知正文。请稍后重试。",
                failures, "合成只读来源检查异常后，按钮或固定提示没有在4秒内恢复。");
            if (publisherCandidates.Text.Contains("SYNTHETIC_PRIVATE_PUBLISHER_INSPECTION_ERROR", StringComparison.Ordinal)
                || !GetPrivateField<bool>(window, "_publisherInspectionFailed") || samplePublisher.SelectedIndex != -1
                || startSampleButton.IsEnabled)
                failures.Add("来源检查异常后未清除候选/阻止样本采集，或异常详情泄漏到设置页。");

            failPublisherInspection = false;
            dispatcher.Invoke(new Action(() => inspectPublishersHandler.Invoke(window,
                [inspectPublishersButton, new RoutedEventArgs(Button.ClickEvent, inspectPublishersButton)])));
            PumpUntil(dispatcher, () => inspectPublishersButton.IsEnabled
                    && publisherCandidates.Text == "合成来源元数据检查完成。",
                failures, "合成来源检查重试成功后，界面没有在4秒内恢复。");
            if (GetPrivateField<bool>(window, "_publisherInspectionFailed") || startSampleButton.IsEnabled)
                failures.Add("来源检查成功后失败锁未清除，或无已核验候选时错误开放了样本采集。");

            var auditHandler = typeof(SettingsWindow).GetMethod("ViewApprovalAudit_Click",
                BindingFlags.Instance | BindingFlags.NonPublic);
            var auditButton = window.FindName("ViewApprovalAuditButton") as Button;
            var maintenanceStatus = window.FindName("DatabaseMaintenanceStatusText") as TextBlock;
            if (auditHandler is null || auditButton is null || maintenanceStatus is null)
            {
                failures.Add("设置页没有找到审批审计查看处理器或维护状态控件。");
                return;
            }
            failApprovalAuditRead = true;
            dispatcher.Invoke(new Action(() => auditHandler.Invoke(window,
                [auditButton, new RoutedEventArgs(Button.ClickEvent, auditButton)])));
            PumpUntil(dispatcher, () => auditButton.IsEnabled
                    && maintenanceStatus.Text == "审批审计记录无法读取或显示；没有修改记录，请稍后重试。",
                failures, "合成审批审计读取异常后，按钮或固定提示没有在4秒内恢复。");
            if (maintenanceStatus.Text.Contains("SYNTHETIC_PRIVATE_AUDIT_READ_ERROR", StringComparison.Ordinal))
                failures.Add("审批审计读取异常详情泄漏到设置页。");

            var clearLocalDataHandler = typeof(SettingsWindow).GetMethod("ClearLocalData_Click",
                BindingFlags.Instance | BindingFlags.NonPublic);
            var clearLocalDataButton = window.FindName("ClearLocalDataButton") as Button;
            var createBackupButton = window.FindName("CreateDatabaseBackupButton") as Button;
            var restoreBackupButton = window.FindName("RestoreDatabaseBackupButton") as Button;
            if (clearLocalDataHandler is null || clearLocalDataButton is null
                || createBackupButton is null || restoreBackupButton is null)
            {
                failures.Add("设置页没有找到本地历史清理处理器或数据库维护按钮。");
                return;
            }
            failCleanupPreview = true;
            dispatcher.Invoke(new Action(() => clearLocalDataHandler.Invoke(window,
                [clearLocalDataButton, new RoutedEventArgs(Button.ClickEvent, clearLocalDataButton)])));
            PumpUntil(dispatcher, () => clearLocalDataButton.IsEnabled && createBackupButton.IsEnabled
                    && restoreBackupButton.IsEnabled && auditButton.IsEnabled
                    && maintenanceStatus.Text.StartsWith("本地历史清理尚未开始", StringComparison.Ordinal),
                failures, "合成清理预览异常后，维护按钮或固定提示没有在4秒内恢复。");
            if (maintenanceStatus.Text.Contains("SYNTHETIC_PRIVATE_CLEANUP_PREVIEW_ERROR", StringComparison.Ordinal)
                || clearLocalDataCalls != 0)
                failures.Add("清理预览异常泄漏了详情，或在确认前触发了本地数据清理。");

            var clearSamplesHandler = typeof(SettingsWindow).GetMethod("ClearSamples_Click",
                BindingFlags.Instance | BindingFlags.NonPublic);
            var clearSamplesButton = window.FindName("ClearSamplesButton") as Button;
            var privacyStatus = window.FindName("PrivacyStatusText") as TextBlock;
            var evaluationRoot = window.FindName("EvaluationRootBox") as TextBox;
            if (clearSamplesHandler is null || clearSamplesButton is null || privacyStatus is null || evaluationRoot is null)
            {
                failures.Add("设置页没有找到评测样本清理处理器或状态控件。");
                return;
            }
            evaluationRoot.Text = @"\\server\share";
            dispatcher.Invoke(new Action(() => clearSamplesHandler.Invoke(window,
                [clearSamplesButton, new RoutedEventArgs(Button.ClickEvent, clearSamplesButton)])));
            PumpUntil(dispatcher, () => clearSamplesButton.IsEnabled
                    && privacyStatus.Text == "评测样本清理尚未开始；目录验证或预览失败。请检查所选目录后重试。",
                failures, "评测目录验证失败后，清理按钮或固定提示没有在4秒内恢复。");
            evaluationRoot.Text = settings.EvaluationRoot;
        }
        catch (Exception exception)
        {
            failures.Add($"设置页异步恢复探针异常：{exception.GetType().Name}");
        }
        finally
        {
            if (temporaryRoot is not null)
            {
                var tempPrefix = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath()))
                    + Path.DirectorySeparatorChar;
                var target = Path.GetFullPath(temporaryRoot);
                if (target.StartsWith(tempPrefix, StringComparison.OrdinalIgnoreCase)
                    && Path.GetFileName(target).StartsWith(TemporaryDirectoryPrefix, StringComparison.Ordinal)
                    && Directory.Exists(target))
                    Directory.Delete(target, recursive: true);
            }
        }
    }

    private static void PumpUntil(Dispatcher dispatcher, Func<bool> isComplete, List<string> failures,
        string timeoutMessage)
    {
        var frame = new DispatcherFrame();
        var started = Stopwatch.GetTimestamp();
        var finished = false;
        var poll = new DispatcherTimer(DispatcherPriority.ApplicationIdle, dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(20)
        };
        poll.Tick += (_, _) =>
        {
            if (isComplete())
            {
                finished = true;
                poll.Stop();
                frame.Continue = false;
                return;
            }
            if (Stopwatch.GetElapsedTime(started) < TimeSpan.FromSeconds(4)) return;
            poll.Stop();
            frame.Continue = false;
        };
        poll.Start();
        Dispatcher.PushFrame(frame);
        if (!finished) failures.Add(timeoutMessage);
    }

    private static void SetPrivateField(object target, string name, object value) =>
        typeof(SettingsWindow).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(target, value);

    private static T GetPrivateField<T>(object target, string name) =>
        (T)typeof(SettingsWindow).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(target)!;
}
