using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Threading;
using System.Windows.Automation.Peers;
using WpfButton = System.Windows.Controls.Button;
using WpfFocusManager = System.Windows.Input.FocusManager;
using WpfListBox = System.Windows.Controls.ListBox;
using WpfTextBox = System.Windows.Controls.TextBox;
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
        SetInitialKeyboardFocus();
        await RefreshAsync();
        if (_lifetime.IsCancellationRequested) return;
        _refreshTimer.Start();
    }

    private async void RefreshTimer_Tick(object? sender, EventArgs e) => await RefreshAsync();

    private async void CancelTask_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Button { Tag: Guid taskId } button) return;
        button.SetCurrentValue(UIElement.IsEnabledProperty, false);
        SetRefreshStatus("正在撤销排队任务或请求运行任务停止…", announce: true);
        try
        {
            var cancellationAccepted = await _cancelTask(taskId);
            var refreshed = await RefreshAsync();
            SetRefreshStatus(cancellationAccepted
                ? (refreshed ? "已接受取消请求；任务列表已刷新，继续查看任务状态。"
                    : "已接受取消请求；状态未能刷新，列表可能显示旧状态，请核对。")
                : (refreshed ? "该任务已结束或无法取消；状态已刷新。"
                    : "任务无法取消且状态未能刷新；列表可能显示旧状态，请核对。"), announce: true);
        }
        catch (Exception exception) when (IsNonFatal(exception))
        {
            var refreshed = await RefreshAsync();
            SetRefreshStatus(refreshed
                ? "取消结果待核对；任务状态已刷新，请先核对目标状态；小K不会自动重试。"
                : "取消结果待核对；任务状态尚未确认，列表可能显示旧状态；请人工核对目标后再操作；小K不会自动重试。", announce: true);
        }
        finally
        {
            if (button.IsLoaded)
                button.SetCurrentValue(UIElement.IsEnabledProperty,
                    button.DataContext is TaskHistoryEntry entry && entry.CanCancel);
        }
    }

    private async void ResolveApproval_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Button { DataContext: ApprovalInboxEntry entry, Tag: string choiceText }
            || !Enum.TryParse<ApprovalInboxChoice>(choiceText, out var choice)) return;
        try
        {
            var resolved = _resolveApproval(entry.Id, choice);
            var refreshed = await RefreshAsync();
            SetRefreshStatus(resolved
                ? (refreshed ? "已处理待办；任务列表已刷新，相关任务会继续或结束。"
                    : "已处理待办；状态未能刷新，请核对相关任务。")
                : (refreshed ? "待办已结束或该操作不可用；状态已刷新。"
                    : "待办不可用且状态未能刷新；列表可能显示旧状态，请核对。"), announce: true);
        }
        catch (Exception exception) when (IsNonFatal(exception))
        {
            var refreshed = await RefreshAsync();
            SetRefreshStatus(refreshed
                ? "待办处理结果待核对；任务状态已刷新，请先核对目标状态；小K不会自动重试或重放。"
                : "待办处理结果待核对；任务状态尚未确认，列表可能显示旧状态；请人工核对目标后再操作；小K不会自动重试或重放。", announce: true);
        }
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

    private async Task<bool> RefreshAsync()
    {
        if (_refreshInProgress || _lifetime.IsCancellationRequested) return false;
        _refreshInProgress = true;
        try
        {
            var history = await _loadHistory(_lifetime.Token);
            if (_lifetime.IsCancellationRequested) return false;
            var approvals = _loadApprovals();
            if (_lifetime.IsCancellationRequested) return false;
            ApprovalTab.Header = TaskHistoryDisplayPolicy.ApprovalTabHeader(approvals.Count);

            var currentHistory = HistoryList.ItemsSource as IEnumerable<TaskHistoryEntry>;
            if (currentHistory is null || !currentHistory.SequenceEqual(history))
            {
                var focus = CaptureListFocus(HistoryList, GetHistoryEntryKey);
                var selectedEntryKey = (HistoryList.SelectedItem as TaskHistoryEntry)?.EntryKey;
                var previousSelectedIndex = HistoryList.SelectedIndex;
                var scrollViewer = FindVisualChild<ScrollViewer>(HistoryList);
                var scrollOffset = scrollViewer?.VerticalOffset ?? 0;
                HistoryList.ItemsSource = history;
                SetEmptyState(EmptyText, history.Count == 0);
                var keyToPreserve = selectedEntryKey ?? focus?.EntryKey;
                var selected = keyToPreserve is null ? null : history.FirstOrDefault(item => item.EntryKey == keyToPreserve);
                var selectedNearestReplacement = selected is null && previousSelectedIndex >= 0 && history.Count > 0;
                if (selected is not null) HistoryList.SelectedItem = selected;
                else if (selectedNearestReplacement)
                    HistoryList.SelectedIndex = Math.Min(previousSelectedIndex, history.Count - 1);
                HistoryList.UpdateLayout();
                RestoreListFocus(HistoryList, history.Cast<object>().ToArray(), GetHistoryEntryKey, focus);
                if (selected is null && !selectedNearestReplacement && scrollViewer is not null)
                    _ = Dispatcher.BeginInvoke(() => scrollViewer.ScrollToVerticalOffset(scrollOffset), DispatcherPriority.Loaded);
            }
            var currentApprovals = ApprovalList.ItemsSource as IEnumerable<ApprovalInboxEntry>;
            if (currentApprovals is null || !currentApprovals.SequenceEqual(approvals))
            {
                var focus = CaptureListFocus(ApprovalList, GetApprovalEntryKey);
                var selectedApprovalKey = (ApprovalList.SelectedItem as ApprovalInboxEntry) is { } selectedApproval
                    ? GetApprovalEntryKey(selectedApproval) : null;
                var previousSelectedIndex = ApprovalList.SelectedIndex;
                ApprovalList.ItemsSource = approvals;
                SetEmptyState(EmptyApprovalsText, approvals.Count == 0);
                var keyToPreserve = selectedApprovalKey ?? focus?.EntryKey;
                var selected = keyToPreserve is null ? null
                    : approvals.FirstOrDefault(item => GetApprovalEntryKey(item) == keyToPreserve);
                if (selected is not null) ApprovalList.SelectedItem = selected;
                else if (previousSelectedIndex >= 0 && approvals.Count > 0)
                    ApprovalList.SelectedIndex = Math.Min(previousSelectedIndex, approvals.Count - 1);
                ApprovalList.UpdateLayout();
                RestoreListFocus(ApprovalList, approvals.Cast<object>().ToArray(), GetApprovalEntryKey, focus);
            }
            SetRefreshStatus($"已更新 {DateTime.Now:HH:mm:ss} · {history.Count} 项");
            return true;
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { return false; }
        catch (Exception exception) when (IsNonFatal(exception))
        {
            SetRefreshStatus("读取任务状态失败；当前页面可能显示旧状态，请先核对后再操作。");
            return false;
        }
        finally { _refreshInProgress = false; }
    }

    private static bool IsNonFatal(Exception exception) => exception is not OutOfMemoryException
        and not StackOverflowException and not AccessViolationException;

    private void SetRefreshStatus(string message, bool announce = false)
    {
        if (string.Equals(RefreshStatus.Text, message, StringComparison.Ordinal)) return;
        RefreshStatus.Text = message;
        if (announce) RaiseLiveRegionChanged(RefreshStatus);
    }

    private static void SetEmptyState(TextBlock indicator, bool isEmpty)
    {
        var visibility = isEmpty ? Visibility.Visible : Visibility.Collapsed;
        if (indicator.Visibility == visibility) return;
        indicator.Visibility = visibility;
        if (isEmpty) RaiseLiveRegionChanged(indicator);
    }

    private static void RaiseLiveRegionChanged(FrameworkElement element)
    {
        if (!AutomationPeer.ListenerExists(AutomationEvents.LiveRegionChanged)) return;
        var peer = UIElementAutomationPeer.CreatePeerForElement(element);
        peer?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
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

    private void SetInitialKeyboardFocus()
    {
        if (IsActive)
        {
            if (HistoryList.Focus()) return;
            WpfFocusManager.SetFocusedElement(this, HistoryList);
            return;
        }
        if (WpfFocusManager.GetFocusedElement(this) is null)
            WpfFocusManager.SetFocusedElement(this, HistoryList);
    }

    private FocusSnapshot? CaptureListFocus(WpfListBox list, Func<object, string?> getEntryKey)
    {
        var focused = WpfFocusManager.GetFocusedElement(this) as DependencyObject
            ?? (IsActive ? System.Windows.Input.Keyboard.FocusedElement as DependencyObject : null);
        if (focused is null || !IsInList(list, focused)) return null;
        var container = ItemsControl.ContainerFromElement(list, focused) as ListBoxItem;
        var entryKey = container?.DataContext is { } data ? getEntryKey(data) : null;
        return new FocusSnapshot(entryKey, GetFocusRole(focused, list));
    }

    private void RestoreListFocus(WpfListBox list, IReadOnlyList<object> items,
        Func<object, string?> getEntryKey, FocusSnapshot? snapshot)
    {
        if (snapshot is null || !list.IsVisible) return;

        UIElement target = list;
        if (snapshot.EntryKey is not null)
        {
            var item = items.FirstOrDefault(candidate => getEntryKey(candidate) == snapshot.EntryKey);
            if (item is not null && list.ItemContainerGenerator.ContainerFromItem(item) is ListBoxItem container)
            {
                container.UpdateLayout();
                target = ResolveFocusTarget(container, snapshot.FocusRole) ?? list;
            }
        }

        if (!target.IsEnabled || !target.IsVisible || !target.Focusable) target = list;
        try
        {
            if (IsActive)
            {
                if (!target.Focus())
                {
                    WpfFocusManager.SetFocusedElement(this, list);
                    _ = list.Focus();
                }
            }
            else
            {
                // Keep only WPF logical focus while inactive; a background refresh must not activate this window.
                WpfFocusManager.SetFocusedElement(this, target);
            }
        }
        catch (InvalidOperationException)
        {
            WpfFocusManager.SetFocusedElement(this, list);
            if (IsActive) _ = list.Focus();
        }
    }

    private static UIElement? ResolveFocusTarget(ListBoxItem container, string focusRole)
    {
        if (focusRole == "list") return null;
        if (focusRole == "button:cancel")
            return FindVisualDescendant<WpfButton>(container, button => button.Tag is Guid);
        if (focusRole.StartsWith("button:", StringComparison.Ordinal))
        {
            var tag = focusRole["button:".Length..];
            return FindVisualDescendant<WpfButton>(container, button =>
                string.Equals(button.Tag as string, tag, StringComparison.Ordinal));
        }
        if (focusRole is "text:history-detail" or "text:approval-details")
        {
            var bindingPath = focusRole == "text:history-detail"
                ? nameof(TaskHistoryEntry.Detail) : nameof(ApprovalInboxEntry.Details);
            return FindVisualDescendant<WpfTextBox>(container, textBox =>
                BindingOperations.GetBindingExpression(textBox, WpfTextBox.TextProperty)
                    ?.ParentBinding.Path?.Path == bindingPath);
        }
        return null;
    }

    private static string GetFocusRole(DependencyObject focused, WpfListBox list)
    {
        if (ReferenceEquals(focused, list)) return "list";
        if (focused is WpfButton { Tag: Guid }) return "button:cancel";
        if (focused is WpfButton { Tag: string tag }) return $"button:{tag}";
        if (focused is WpfTextBox textBox)
        {
            var bindingPath = BindingOperations.GetBindingExpression(textBox, WpfTextBox.TextProperty)
                ?.ParentBinding.Path?.Path;
            return bindingPath switch
            {
                nameof(TaskHistoryEntry.Detail) => "text:history-detail",
                nameof(ApprovalInboxEntry.Details) => "text:approval-details",
                _ => "text"
            };
        }
        return "list";
    }

    private static bool IsInList(WpfListBox list, DependencyObject element) =>
        ReferenceEquals(element, list) || ItemsControl.ContainerFromElement(list, element) is not null;

    private static string? GetHistoryEntryKey(object item) =>
        item is TaskHistoryEntry entry ? entry.EntryKey : null;

    private static string? GetApprovalEntryKey(object item) =>
        item is ApprovalInboxEntry entry ? $"approval:{entry.Id:N}" : null;

    private static T? FindVisualDescendant<T>(DependencyObject parent, Func<T, bool> predicate)
        where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is T match && predicate(match)) return match;
            if (FindVisualDescendant(child, predicate) is { } nested) return nested;
        }
        return null;
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

    private sealed record FocusSnapshot(string? EntryKey, string FocusRole);
}
