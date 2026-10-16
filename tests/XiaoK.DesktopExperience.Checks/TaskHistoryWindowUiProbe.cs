using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Threading;
using WpfFocusManager = System.Windows.Input.FocusManager;
using XiaoK.Core;
using XiaoK.Host;

namespace XiaoK.DesktopExperience.Checks;

internal static class TaskHistoryWindowUiProbe
{
    private static readonly TimeSpan WindowLoadedTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan StaThreadTimeout = TimeSpan.FromSeconds(20);

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
            Name = "XiaoK task center offscreen UI check"
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        if (!thread.Join(StaThreadTimeout))
            return ["任务中心离屏UI检查超过20秒；工作线程设为后台且不会触碰用户窗口。"];
        if (threadFailure is not null)
            return [$"任务中心离屏UI检查异常：{threadFailure.GetType().Name}"];
        return failures;
    }

    private static void RunOnSta(List<string> failures)
    {
        var dispatcher = Dispatcher.CurrentDispatcher;
        var sample = new TaskHistoryEntry(
            "task:offscreen-probe", Guid.NewGuid(), "合成任务 · 1234abcd", "结果待核对",
            DateTimeOffset.UtcNow, "合成恢复说明：请核对目标状态；不会自动重试。", true,
            "合成当前步骤", "合成目标范围", "合成后台模式", "合成下一步");
        var currentSample = sample;
        var failCancellation = false;
        var failHistoryLoad = false;
        ApprovalInboxEntry? activeApproval = null;
        var failApprovalResolution = false;
        var cancelCalls = 0;
        var approvalCalls = 0;
        Guid? cancelledTaskId = null;
        Task<IReadOnlyList<TaskHistoryEntry>> LoadHistory(CancellationToken _)
        {
            if (failHistoryLoad) throw new InvalidOperationException("LOAD_PRIVATE_PATH");
            return Task.FromResult<IReadOnlyList<TaskHistoryEntry>>([currentSample]);
        }
        IReadOnlyList<ApprovalInboxEntry> LoadApprovals() => activeApproval is null ? [] : [activeApproval];
        Task<bool> CancelTask(Guid taskId)
        {
            cancelCalls++;
            cancelledTaskId = taskId;
            if (failCancellation) throw new InvalidOperationException("CANCEL_PRIVATE_PATH");
            currentSample = currentSample with
            {
                State = "取消请求中",
                CanCancel = false,
                CurrentStep = "正在安全停止"
            };
            return Task.FromResult(true);
        }
        bool ResolveApproval(Guid approvalId, ApprovalInboxChoice choice)
        {
            approvalCalls++;
            if (failApprovalResolution) throw new InvalidOperationException("APPROVAL_PRIVATE_PATH");
            activeApproval = null;
            return true;
        }

        var window = new TaskHistoryWindow(LoadHistory, CancelTask,
            LoadApprovals, ResolveApproval)
        {
            ShowActivated = false,
            ShowInTaskbar = false,
            Opacity = 0,
            WindowStyle = WindowStyle.None,
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = -32000,
            Top = -32000
        };

        var foregroundBefore = GetForegroundWindow();
        bool ProbeWindowOwnsForeground()
        {
            var handle = new System.Windows.Interop.WindowInteropHelper(window).Handle;
            return handle != IntPtr.Zero && GetForegroundWindow() == handle;
        }
        var foregroundChanges = new List<string>();
        void TrackForeground(string stage)
        {
            var current = GetForegroundWindow();
            if (current != foregroundBefore && foregroundChanges.Count == 0)
                foregroundChanges.Add($"{stage}: 0x{foregroundBefore.ToInt64():X} -> 0x{current.ToInt64():X}");
        }
        var completed = false;
        var watchdog = new DispatcherTimer(DispatcherPriority.ApplicationIdle, dispatcher)
        {
            Interval = WindowLoadedTimeout
        };
        watchdog.Tick += (_, _) =>
        {
            watchdog.Stop();
            if (!completed) failures.Add("任务中心离屏UI检查没有完成加载回调。");
            window.Close();
        };
        window.Loaded += (_, _) => dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() =>
        {
            try
            {
                TrackForeground("Loaded 回调开始");
                window.UpdateLayout();
                TrackForeground("初次 UpdateLayout 后");
                var historyList = window.FindName("HistoryList") as ListBox;
                if (historyList is null)
                {
                    failures.Add("任务中心没有找到任务列表控件。");
                    return;
                }
                var refreshStatusProbe = window.FindName("RefreshStatus") as TextBlock;
                if (AutomationProperties.GetName(historyList) != "任务历史"
                    || string.IsNullOrWhiteSpace(AutomationProperties.GetHelpText(historyList))
                    || refreshStatusProbe is null
                    || AutomationProperties.GetLiveSetting(refreshStatusProbe) != AutomationLiveSetting.Polite)
                    failures.Add("任务列表或刷新状态缺少可访问名称、操作说明或礼貌级实时播报标记。");
                if (ProbeWindowOwnsForeground()
                    && !ReferenceEquals(WpfFocusManager.GetFocusedElement(window), historyList))
                    failures.Add("初次打开任务中心后，键盘逻辑焦点没有落在任务列表。");

                historyList.UpdateLayout();
                var item = historyList.ItemContainerGenerator.ContainerFromIndex(0) as ListBoxItem;
                if (historyList.Items.Count != 1 || item is null)
                {
                    failures.Add("任务历史没有把合成记录加载到实际WPF列表。");
                    return;
                }

                item.UpdateLayout();
                var detailBox = Descendants(item).OfType<TextBox>().FirstOrDefault(control =>
                    BindingOperations.GetBindingExpression(control, TextBox.TextProperty)
                        ?.ParentBinding.Path?.Path == nameof(TaskHistoryEntry.Detail));
                if (detailBox is null || !detailBox.IsReadOnly
                    || !string.Equals(detailBox.Text, sample.Detail, StringComparison.Ordinal))
                    failures.Add("任务恢复说明没有通过只读Detail绑定显示在实际任务项中。");

                var texts = Descendants(item).OfType<TextBlock>()
                    .Select(control => control.Text + string.Concat(control.Inlines.Select(GetInlineText)))
                    .ToArray();
                foreach (var value in new[]
                {
                    sample.CurrentStep, sample.TargetScope, sample.ExecutionMode, sample.NextAction
                })
                    if (!texts.Any(text => text.Contains(value, StringComparison.Ordinal)))
                        failures.Add($"任务项没有显示合成字段：{value}");

                var cancelButton = Descendants(item).OfType<Button>()
                    .FirstOrDefault(button => string.Equals(button.Content as string, "取消", StringComparison.Ordinal));
                if (cancelButton is null || !cancelButton.IsEnabled)
                {
                    failures.Add("可取消任务没有显示已启用的取消按钮。");
                    return;
                }
                if (AutomationProperties.GetName(cancelButton) != "取消此任务")
                    failures.Add("任务取消按钮没有可访问名称。");
                currentSample = sample with { CurrentStep = "合成步骤已更新" };
                window.RefreshImmediately();
                TrackForeground("任务列表刷新后");
                window.UpdateLayout();
                historyList.UpdateLayout();
                TrackForeground("任务列表布局更新后");
                item = historyList.ItemContainerGenerator.ContainerFromIndex(0) as ListBoxItem;
                var refreshedCancelButton = item is null ? null : Descendants(item).OfType<Button>()
                    .FirstOrDefault(button => button.Tag is Guid);
                if (item?.DataContext is not TaskHistoryEntry refreshedTask
                    || refreshedTask.EntryKey != sample.EntryKey
                    || !string.Equals(refreshedTask.CurrentStep, "合成步骤已更新", StringComparison.Ordinal)
                    || refreshedCancelButton is null
                    || (ProbeWindowOwnsForeground()
                        && !ReferenceEquals(WpfFocusManager.GetFocusedElement(window), refreshedCancelButton)))
                    failures.Add("稳定任务条目刷新后没有保留选择并将键盘焦点还原到同一可用操作。");
                if (refreshedCancelButton is not null) cancelButton = refreshedCancelButton;
                cancelButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, cancelButton));
                TrackForeground("取消按钮回调后");
                if (cancelCalls != 1 || cancelledTaskId != sample.TaskId)
                    failures.Add("取消按钮没有把正确任务ID交给取消入口。");
                if (historyList.ItemsSource is not IEnumerable<TaskHistoryEntry> refreshed
                    || refreshed.SingleOrDefault()?.State != "取消请求中"
                    || refreshed.SingleOrDefault()?.CanCancel != false)
                    failures.Add("取消回调后任务列表没有刷新到当前取消状态。");
                if (ProbeWindowOwnsForeground()
                    && !ReferenceEquals(WpfFocusManager.GetFocusedElement(window), historyList))
                    failures.Add("任务变为不可取消后，键盘焦点没有回到任务列表。");
                var cancellationStatus = window.FindName("RefreshStatus") as TextBlock;
                if (cancellationStatus is null
                    || !cancellationStatus.Text.Contains("已接受取消请求", StringComparison.Ordinal)
                    || !cancellationStatus.Text.Contains("任务列表已刷新", StringComparison.Ordinal))
                    failures.Add("取消结果提示被自动刷新状态覆盖，或没有说明刷新结果。");

                failCancellation = true;
                currentSample = sample;
                window.RefreshImmediately();
                window.UpdateLayout();
                historyList.UpdateLayout();
                item = historyList.ItemContainerGenerator.ContainerFromIndex(0) as ListBoxItem;
                cancelButton = item is null ? null : Descendants(item).OfType<Button>()
                    .FirstOrDefault(button => string.Equals(button.Content as string, "取消", StringComparison.Ordinal));
                failHistoryLoad = true;
                cancelButton?.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, cancelButton));
                var refreshStatus = window.FindName("RefreshStatus") as TextBlock;
                if (cancelCalls != 2 || cancelButton is null || refreshStatus is null
                    || !refreshStatus.Text.Contains("取消结果待核对", StringComparison.Ordinal)
                    || !refreshStatus.Text.Contains("列表可能显示旧状态", StringComparison.Ordinal)
                    || refreshStatus.Text.Contains("CANCEL_PRIVATE_PATH", StringComparison.Ordinal)
                    || refreshStatus.Text.Contains("LOAD_PRIVATE_PATH", StringComparison.Ordinal)
                    || refreshStatus.Text.Contains("状态已刷新", StringComparison.Ordinal))
                    failures.Add("取消回调或状态刷新异常没有被诚实限制在任务中心内，或异常细节泄露到界面。");

                failHistoryLoad = false;
                activeApproval = new ApprovalInboxEntry(Guid.NewGuid(), ApprovalInboxKind.Confirmation,
                    "合成审批", "合成审批详情", DateTimeOffset.UtcNow, false, sample.TaskId);
                failApprovalResolution = true;
                window.RefreshImmediately();
                TrackForeground("待办列表刷新后");
                var tabs = Descendants(window).OfType<TabControl>().FirstOrDefault();
                if (tabs is not null) tabs.SelectedIndex = 1;
                window.UpdateLayout();
                TrackForeground("切换待办标签并布局后");
                var approvalList = window.FindName("ApprovalList") as ListBox;
                approvalList?.UpdateLayout();
                var approvalItem = approvalList?.ItemContainerGenerator.ContainerFromIndex(0) as ListBoxItem;
                var approveButton = approvalItem is null ? null : Descendants(approvalItem).OfType<Button>()
                    .FirstOrDefault(button => string.Equals(button.Content as string, "批准", StringComparison.Ordinal));
                approveButton?.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, approveButton));
                TrackForeground("审批失败回调后");
                if (approvalCalls != 1 || approveButton is null || refreshStatus is null
                    || !refreshStatus.Text.Contains("待办处理结果待核对", StringComparison.Ordinal)
                    || !refreshStatus.Text.Contains("状态已刷新", StringComparison.Ordinal)
                    || refreshStatus.Text.Contains("APPROVAL_PRIVATE_PATH", StringComparison.Ordinal))
                    failures.Add("审批回调异常没有被限制在任务中心内，或异常细节泄露到界面。");

                failApprovalResolution = false;
                activeApproval = new ApprovalInboxEntry(Guid.NewGuid(), ApprovalInboxKind.Confirmation,
                    "合成成功审批", "合成成功审批详情", DateTimeOffset.UtcNow, false, sample.TaskId);
                window.RefreshImmediately();
                TrackForeground("成功待办刷新后");
                if (tabs is not null) tabs.SelectedIndex = 1;
                window.UpdateLayout();
                approvalList?.UpdateLayout();
                approvalItem = approvalList?.ItemContainerGenerator.ContainerFromIndex(0) as ListBoxItem;
                approveButton = approvalItem is null ? null : Descendants(approvalItem).OfType<Button>()
                    .FirstOrDefault(button => string.Equals(button.Content as string, "批准", StringComparison.Ordinal));
                if (approveButton is null || AutomationProperties.GetName(approveButton) != "批准当前请求")
                    failures.Add("待办批准按钮没有提供明确的可访问名称。");
                if (approveButton is not null)
                {
                    approveButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, approveButton));
                    TrackForeground("审批成功回调后");
                }
                var emptyApprovals = window.FindName("EmptyApprovalsText") as TextBlock;
                if (approvalCalls != 2 || approvalList?.Items.Count != 0
                    || emptyApprovals?.Visibility != Visibility.Visible
                    || (ProbeWindowOwnsForeground()
                        && !ReferenceEquals(WpfFocusManager.GetFocusedElement(window), approvalList))
                    || refreshStatus is null || !refreshStatus.Text.Contains("已处理待办", StringComparison.Ordinal))
                    failures.Add("待办处理后没有显示空状态、更新反馈并把键盘焦点送回列表。");

                TrackForeground("探针完成前");
                window.RefreshImmediately();
                TrackForeground("后台刷新后");
                if (foregroundChanges.Count > 0)
                    failures.Add($"打开或后台刷新任务中心改变了前台窗口（{foregroundChanges[0]}）。");
                completed = true;
            }
            catch (Exception exception)
            {
                failures.Add($"任务中心WPF绑定检查异常：{exception.GetType().Name}");
            }
            finally
            {
                watchdog.Stop();
                window.Close();
            }
        }));
        window.Closed += (_, _) => dispatcher.BeginInvokeShutdown(DispatcherPriority.Background);
        window.Show();
        TrackForeground("Show 返回后");
        // Exclude synchronous HWND creation from the Loaded-event deadline.
        watchdog.Start();
        Dispatcher.Run();
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for (var index = 0; index < System.Windows.Media.VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(root, index);
            yield return child;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }

    private static string GetInlineText(Inline inline) => inline switch
    {
        Run run => run.Text,
        Span span => string.Concat(span.Inlines.Select(GetInlineText)),
        LineBreak => Environment.NewLine,
        _ => string.Empty
    };

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();
}
