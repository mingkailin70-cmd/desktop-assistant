using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Threading;
using XiaoK.Core;
using XiaoK.Host;

namespace XiaoK.DesktopExperience.Checks;

internal static class TaskHistoryWindowUiProbe
{
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
        if (!thread.Join(TimeSpan.FromSeconds(12)))
            return ["任务中心离屏UI检查超过12秒；工作线程设为后台且不会触碰用户窗口。"];
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
        ApprovalInboxEntry? activeApproval = null;
        var failApprovalResolution = false;
        var cancelCalls = 0;
        var approvalCalls = 0;
        Guid? cancelledTaskId = null;
        Task<IReadOnlyList<TaskHistoryEntry>> LoadHistory(CancellationToken _) =>
            Task.FromResult<IReadOnlyList<TaskHistoryEntry>>([currentSample]);
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
            return false;
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
        var completed = false;
        var watchdog = new DispatcherTimer(DispatcherPriority.ApplicationIdle, dispatcher)
        {
            Interval = TimeSpan.FromSeconds(8)
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
                window.UpdateLayout();
                var historyList = window.FindName("HistoryList") as ListBox;
                if (historyList is null)
                {
                    failures.Add("任务中心没有找到任务列表控件。");
                    return;
                }

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
                cancelButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, cancelButton));
                if (cancelCalls != 1 || cancelledTaskId != sample.TaskId)
                    failures.Add("取消按钮没有把正确任务ID交给取消入口。");
                if (historyList.ItemsSource is not IEnumerable<TaskHistoryEntry> refreshed
                    || refreshed.SingleOrDefault()?.State != "取消请求中"
                    || refreshed.SingleOrDefault()?.CanCancel != false)
                    failures.Add("取消回调后任务列表没有刷新到当前取消状态。");

                failCancellation = true;
                currentSample = sample;
                window.RefreshImmediately();
                window.UpdateLayout();
                historyList.UpdateLayout();
                item = historyList.ItemContainerGenerator.ContainerFromIndex(0) as ListBoxItem;
                cancelButton = item is null ? null : Descendants(item).OfType<Button>()
                    .FirstOrDefault(button => string.Equals(button.Content as string, "取消", StringComparison.Ordinal));
                cancelButton?.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, cancelButton));
                var refreshStatus = window.FindName("RefreshStatus") as TextBlock;
                if (cancelCalls != 2 || cancelButton is null || refreshStatus is null
                    || !refreshStatus.Text.Contains("取消结果待核对", StringComparison.Ordinal)
                    || refreshStatus.Text.Contains("CANCEL_PRIVATE_PATH", StringComparison.Ordinal))
                    failures.Add("取消回调异常没有被限制在任务中心内，或异常细节泄露到界面。");

                activeApproval = new ApprovalInboxEntry(Guid.NewGuid(), ApprovalInboxKind.Confirmation,
                    "合成审批", "合成审批详情", DateTimeOffset.UtcNow, false, sample.TaskId);
                failApprovalResolution = true;
                window.RefreshImmediately();
                var tabs = Descendants(window).OfType<TabControl>().FirstOrDefault();
                if (tabs is not null) tabs.SelectedIndex = 1;
                window.UpdateLayout();
                var approvalList = window.FindName("ApprovalList") as ListBox;
                approvalList?.UpdateLayout();
                var approvalItem = approvalList?.ItemContainerGenerator.ContainerFromIndex(0) as ListBoxItem;
                var approveButton = approvalItem is null ? null : Descendants(approvalItem).OfType<Button>()
                    .FirstOrDefault(button => string.Equals(button.Content as string, "批准", StringComparison.Ordinal));
                approveButton?.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, approveButton));
                if (approvalCalls != 1 || approveButton is null || refreshStatus is null
                    || !refreshStatus.Text.Contains("待办处理结果待核对", StringComparison.Ordinal)
                    || refreshStatus.Text.Contains("APPROVAL_PRIVATE_PATH", StringComparison.Ordinal))
                    failures.Add("审批回调异常没有被限制在任务中心内，或异常细节泄露到界面。");

                if (GetForegroundWindow() != foregroundBefore)
                    failures.Add("打开离屏任务中心改变了前台窗口。");
                window.RefreshImmediately();
                if (GetForegroundWindow() != foregroundBefore)
                    failures.Add("后台刷新任务中心改变了前台窗口。");
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
        watchdog.Start();
        window.Show();
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
