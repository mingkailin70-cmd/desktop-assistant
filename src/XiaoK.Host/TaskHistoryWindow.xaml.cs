using System.Windows;

namespace XiaoK.Host;

public partial class TaskHistoryWindow : Window
{
    internal TaskHistoryWindow(IReadOnlyList<TaskHistoryEntry> history)
    {
        InitializeComponent();
        HistoryList.ItemsSource = history;
        EmptyText.Visibility = history.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }
}
