using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace XiaoK.Host;

public partial class TaskHistoryWindow : Window
{
    private readonly Func<CancellationToken, Task<IReadOnlyList<TaskHistoryEntry>>> _loadHistory;
    private readonly DispatcherTimer _refreshTimer = new() { Interval = TimeSpan.FromSeconds(3) };
    private readonly CancellationTokenSource _lifetime = new();
    private bool _refreshInProgress;

    internal TaskHistoryWindow(Func<CancellationToken, Task<IReadOnlyList<TaskHistoryEntry>>> loadHistory)
    {
        InitializeComponent();
        _loadHistory = loadHistory ?? throw new ArgumentNullException(nameof(loadHistory));
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

    private async Task RefreshAsync()
    {
        if (_refreshInProgress || _lifetime.IsCancellationRequested) return;
        _refreshInProgress = true;
        try
        {
            var history = await _loadHistory(_lifetime.Token);
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
