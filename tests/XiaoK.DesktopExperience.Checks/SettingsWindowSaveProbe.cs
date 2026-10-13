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

internal static class SettingsWindowSaveProbe
{
    private const string TemporaryDirectoryPrefix = "XiaoK-SettingsSaveProbe-";

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
            Name = "XiaoK settings save offscreen UI check"
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        if (!thread.Join(TimeSpan.FromSeconds(12)))
            return ["设置页保存离屏UI检查超过12秒；工作线程设为后台且不会触碰用户窗口。"];
        if (threadFailure is not null)
            return [$"设置页保存离屏UI检查异常：{threadFailure.GetType().Name}"];
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
                SearchRoots = []
            };

            var replaceGate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var replaceCalls = 0;
            Task ReplaceContactStyles(IEnumerable<ContactReplyStylePreference> _, CancellationToken __)
            {
                replaceCalls++;
                return replaceGate.Task;
            }

            Task<IReadOnlyList<ContactReplyStylePreference>> LoadContactStyles(CancellationToken _) =>
                Task.FromResult<IReadOnlyList<ContactReplyStylePreference>>([]);
            Task<string> CreateBackup(string path, CancellationToken _) => Task.FromResult(path);
            Task<string> RestoreBackup(string path, CancellationToken _) => Task.FromResult(path);
            Task<IReadOnlyList<ApprovalAuditRecord>> LoadApprovalAudit(int _, CancellationToken __) =>
                Task.FromResult<IReadOnlyList<ApprovalAuditRecord>>([]);
            Task<LocalDataCleanupPreview> GetCleanupPreview(CancellationToken _) =>
                Task.FromResult<LocalDataCleanupPreview>(null!);
            Task<LocalDataCleanupResult> ClearLocalData(LocalDataCleanupPreview _, CancellationToken __) =>
                Task.FromResult<LocalDataCleanupResult>(null!);

            var notificationMonitor = new WindowsNotificationMonitor(dispatcher);
            var window = new SettingsWindow(settings, notificationMonitor, LoadContactStyles, ReplaceContactStyles,
                Path.Combine(rootPath, "active.sqlite3"), CreateBackup, RestoreBackup,
                LoadApprovalAudit, GetCleanupPreview, ClearLocalData);
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
            PumpUntilSaveFinishes(dispatcher, window, failures);
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
        }
        catch (Exception exception)
        {
            failures.Add($"设置页保存探针异常：{exception.GetType().Name}");
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

    private static void PumpUntilSaveFinishes(Dispatcher dispatcher, SettingsWindow window, List<string> failures)
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
            if (!GetPrivateField<bool>(window, "_saveInProgress"))
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
        if (!finished) failures.Add("合成保存异常后，设置页异步收尾没有在4秒内结束。");
    }

    private static void SetPrivateField(object target, string name, object value) =>
        typeof(SettingsWindow).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(target, value);

    private static T GetPrivateField<T>(object target, string name) =>
        (T)typeof(SettingsWindow).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(target)!;
}
