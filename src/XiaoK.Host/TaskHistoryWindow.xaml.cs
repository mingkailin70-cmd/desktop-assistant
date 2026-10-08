using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using XiaoK.Core;

namespace XiaoK.Host;

public partial class TaskHistoryWindow : Window
{
    private readonly Func<CancellationToken, Task<IReadOnlyList<TaskHistoryEntry>>> _loadHistory;
    private readonly Func<IReadOnlyList<ApprovalInboxEntry>> _loadApprovals;
    private readonly Func<Guid, ApprovalInboxChoice, bool> _resolveApproval;
    private readonly Func<Guid, Task<bool>> _cancelTask;
    private readonly DispatcherTimer _refreshTimer = new() { Interval = TimeSpan.FromSeconds(3) };
    private readonly CancellationTokenSource _lifetime = new();
    private bool _refreshInProgress;

    internal TaskHistoryWindow(Func<CancellationToken, Task<IReadOnlyList<TaskHistoryEntry>>> loadHistory,
        Func<Guid, Task<bool>> cancelTask, Func<IReadOnlyList<ApprovalInboxEntry>> loadApprovals,
        Func<Guid, ApprovalInboxChoice, bool> resolveApproval)
    {
        InitializeComponent();
        _loadHistory = loadHistory ?? throw new ArgumentNullException(nameof(loadHistory));
        _cancelTask = cancelTask ?? throw new ArgumentNullException(nameof(cancelTask));
        _loadApprovals = loadApprovals ?? throw new ArgumentNullException(nameof(loadApprovals));
        _resolveApproval = resolveApproval ?? throw new ArgumentNullException(nameof(resolveApproval));
        _refreshTimer.Tick += RefreshTimer_Tick;
        Loaded += Window_Loaded;
        Closed += Window_Closed;
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        await RefreshAsync();
        if (!_lifetime.IsCancellationRequested) _refreshTimer.Start();
    }

    private async void RefreshTimer_Tick(object? sender, EventArgs e) => await RefreshAsync();

    private async void CancelTask_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Button { Tag: Guid taskId } button) return;
        button.IsEnabled = false;
        RefreshStatus.Text = "正在撤销排队任务或请求运行任务停止…";
        try
        {
            if (await _cancelTask(taskId)) RefreshStatus.Text = "排队任务已撤销，或已向运行任务请求安全停止。";
            else RefreshStatus.Text = "该任务已结束或无法取消；状态将在下一次刷新时更新。";
            await RefreshAsync();
        }
        finally
        {
            if (button.IsLoaded) button.IsEnabled = true;
        }
    }

    private async void ResolveApproval_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Button { DataContext: ApprovalInboxEntry entry, Tag: string choiceText }
            || !Enum.TryParse<ApprovalInboxChoice>(choiceText, out var choice)) return;
        if (_resolveApproval(entry.Id, choice))
            RefreshStatus.Text = "已处理待办；相关任务会继续或结束。";
        else
            RefreshStatus.Text = "待办已结束或该操作不可用；正在刷新状态。";
        await RefreshAsync();
    }

    internal void RefreshImmediately()
    {
        if (!Dispatcher.CheckAccess())
        {
            if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished) return;
            try { _ = Dispatcher.BeginInvoke(new Action(() => _ = RefreshAsync())); }
            catch (InvalidOperationException) { }
            return;
        }
        if (!IsLoaded || _lifetime.IsCancellationRequested) return;
        _ = RefreshAsync();
    }

    private async Task RefreshAsync()
    {
        if (_refreshInProgress || _lifetime.IsCancellationRequested) return;
        _refreshInProgress = true;
        try
        {
            var history = await _loadHistory(_lifetime.Token);
            if (_lifetime.IsCancellationRequested) return;
            var approvals = _loadApprovals();
            if (_lifetime.IsCancellationRequested) return;

            var currentHistory = HistoryList.ItemsSource as IEnumerable<TaskHistoryEntry>;
            if (currentHistory is null || !currentHistory.SequenceEqual(history))
            {
                var selectedTitle = (HistoryList.SelectedItem as TaskHistoryEntry)?.Title;
                var scrollViewer = FindVisualChild<ScrollViewer>(HistoryList);
                var scrollOffset = scrollViewer?.VerticalOffset ?? 0;
                HistoryList.ItemsSource = history;
                EmptyText.Visibility = history.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
                var selected = selectedTitle is null ? null : history.FirstOrDefault(item => item.Title == selectedTitle);
                if (selected is not null) HistoryList.SelectedItem = selected;
                else if (scrollViewer is not null)
                    _ = Dispatcher.BeginInvoke(() => scrollViewer.ScrollToVerticalOffset(scrollOffset), DispatcherPriority.Loaded);
            }
            var currentApprovals = ApprovalList.ItemsSource as IEnumerable<ApprovalInboxEntry>;
            if (currentApprovals is null || !currentApprovals.SequenceEqual(approvals))
            {
                ApprovalList.ItemsSource = approvals;
                EmptyApprovalsText.Visibility = approvals.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            }
            RefreshStatus.Text = $"已更新 {DateTime.Now:HH:mm:ss} · {history.Count} 项";
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception exception) when (exception is not OutOfMemoryException
            and not StackOverflowException and not AccessViolationException)
        {
            RefreshStatus.Text = "读取任务状态失败；上次显示的数据仍保留。";
        }
        finally { _refreshInProgress = false; }
    }

    private void Window_Closed(object? sender, EventArgs e)
    {
        _refreshTimer.Stop();
        _refreshTimer.Tick -= RefreshTimer_Tick;
        Loaded -= Window_Loaded;
        Closed -= Window_Closed;
        _lifetime.Cancel();
        _lifetime.Dispose();
    }

    private static T? FindVisualChild<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is T match) return match;
            if (FindVisualChild<T>(child) is { } nested) return nested;
        }
        return null;
    }
}
