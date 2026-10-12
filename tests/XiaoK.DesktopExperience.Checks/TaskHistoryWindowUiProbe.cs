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
        if (!thread.Join(TimeSpan.FromSeconds(8)))
            return ["任务中心离屏UI检查超过8秒；工作线程设为后台且不会触碰用户窗口。"];
        if (threadFailure is not null)
            return [$"任务中心离屏UI检查异常：{threadFailure.GetType().Name}"];
        return failures;
    }

    private static void RunOnSta(List<string> failures)
    {
        var dispatcher = Dispatcher.CurrentDispatcher;
        var sample = new TaskHistoryEntry(
            "task:offscreen-probe", Guid.NewGuid(), "合成任务 · 1234abcd", "结果待核对",
            DateTimeOffset.UtcNow, "合成恢复说明：请核对目标状态；不会自动重试。", false,
            "合成当前步骤", "合成目标范围", "合成后台模式", "合成下一步");
        Task<IReadOnlyList<TaskHistoryEntry>> LoadHistory(CancellationToken _) =>
            Task.FromResult<IReadOnlyList<TaskHistoryEntry>>([sample]);
        IReadOnlyList<ApprovalInboxEntry> LoadApprovals() => [];
        var window = new TaskHistoryWindow(LoadHistory, _ => Task.FromResult(false),
            LoadApprovals, (_, _) => false)
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
            Interval = TimeSpan.FromSeconds(4)
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
