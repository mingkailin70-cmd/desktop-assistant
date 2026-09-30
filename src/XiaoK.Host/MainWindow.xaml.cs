using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using Forms = System.Windows.Forms;
using XiaoK.Core;

namespace XiaoK.Host;

public partial class MainWindow : Window, IApprovalPresenter, ICodeTaskReviewPresenter
{
    private readonly AssistantRuntime _runtime;
    private readonly WindowsNotificationMonitor _notificationMonitor;
    private readonly Forms.NotifyIcon _tray;
    private HwndSource? _source;
    private bool _exiting;
    private bool _shutdownInProgress;
    private bool _shutdownComplete;
    private bool _expanded = true;
    private bool _hotkeyRegistered;

    public MainWindow()
    {
        InitializeComponent();
        _runtime = new AssistantRuntime(this);
        _notificationMonitor = new WindowsNotificationMonitor(Dispatcher);
        _notificationMonitor.StatusChanged += OnNotificationStatusChanged;
        _ = _notificationMonitor.ApplySettingsAsync(_runtime.CurrentSettings);
        FooterText.Text = _runtime.ModelStatus;
        StatusText.Text = _runtime.VoiceStatus;
        OutputText.Text = "小K已启动。闲置时不加载模型，也不采集麦克风。输入「打开小K项目」或「查找文件 关键词」试用本地工具。";
        _tray = new Forms.NotifyIcon
        {
            Text = "小K桌面助手",
            Icon = System.Drawing.SystemIcons.Information,
            Visible = true,
            ContextMenuStrip = BuildTrayMenu()
        };
        _tray.DoubleClick += (_, _) => RestoreFromTray();
        SourceInitialized += OnSourceInitialized;
        Closing += OnClosing;
        MouseLeftButtonDown += (_, e) => { if (e.ButtonState == System.Windows.Input.MouseButtonState.Pressed) DragMove(); };
    }

    public async Task<bool> ConfirmAsync(string actionId, string title, string details, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var answer = System.Windows.MessageBox.Show(this, details, title, System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Warning, System.Windows.MessageBoxResult.No);
        var confirmed = answer == System.Windows.MessageBoxResult.Yes;
        await _runtime.RecordApprovalAuditAsync(actionId,
            confirmed ? ApprovalAuditCatalog.Confirmed : ApprovalAuditCatalog.Declined, cancellationToken);
        return confirmed;
    }

    public async Task<CodeTaskReviewDecision> ReviewAsync(string projectPath, string workspacePath, string diff,
        string? dotNetTestTarget, string? commandPreview, CancellationToken cancellationToken)
    {
        var decision = !Dispatcher.CheckAccess()
            ? await Dispatcher.InvokeAsync(() => ShowCodeTaskReview(projectPath, workspacePath, diff,
                dotNetTestTarget, commandPreview, cancellationToken)).Task
            : ShowCodeTaskReview(projectPath, workspacePath, diff, dotNetTestTarget, commandPreview, cancellationToken);
        if (decision == CodeTaskReviewDecision.RunDotNetTests)
            await _runtime.RecordApprovalAuditAsync(ApprovalAuditCatalog.CodeTaskAction,
                ApprovalAuditCatalog.RunDotNetTests, cancellationToken);
        return decision;
    }

    private CodeTaskReviewDecision ShowCodeTaskReview(string projectPath, string workspacePath, string diff,
        string? dotNetTestTarget, string? commandPreview, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var dialog = new CodeTaskReviewWindow(projectPath, workspacePath, diff, dotNetTestTarget, commandPreview)
        {
            Owner = this
        };
        using var registration = cancellationToken.Register(() => Dispatcher.BeginInvoke(new Action(() =>
        {
            if (dialog.IsVisible) dialog.Close();
        })));
        dialog.Loaded += (_, _) =>
        {
            if (cancellationToken.IsCancellationRequested) dialog.Close();
        };
        dialog.ShowDialog();
        cancellationToken.ThrowIfCancellationRequested();
        return dialog.Decision;
    }

    private async void Run_Click(object sender, RoutedEventArgs e) => await RunRequestAsync();

    private void Header_MouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (e.ButtonState == System.Windows.Input.MouseButtonState.Pressed) DragMove();
    }
    private async void RequestBox_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.Enter && !System.Windows.Input.Keyboard.Modifiers.HasFlag(System.Windows.Input.ModifierKeys.Shift))
        {
            e.Handled = true;
            await RunRequestAsync();
        }
    }

    private async Task RunRequestAsync()
    {
        var request = RequestBox.Text;
        if (string.IsNullOrWhiteSpace(request)) return;
        RequestBox.Clear();
        OutputText.Text = "正在处理；可随时取消。聊天内容和模型回答仅保留在内存中。";
        StatusText.Text = "任务运行中";
        SetButtonsEnabled(false);
        try { OutputText.Text = await _runtime.SubmitAsync(request); }
        finally { StatusText.Text = _runtime.VoiceStatus; SetButtonsEnabled(true); }
    }

    private async void OpenProject_Click(object sender, RoutedEventArgs e)
    {
        OutputText.Text = "正在打开项目；可随时取消。";
        StatusText.Text = "任务运行中";
        SetButtonsEnabled(false);
        try { OutputText.Text = await _runtime.SubmitAsync("打开小K项目"); }
        finally { StatusText.Text = _runtime.VoiceStatus; SetButtonsEnabled(true); }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        _runtime.CancelCurrent();
        StatusText.Text = "已请求取消";
    }

    private void StopMic_Click(object sender, RoutedEventArgs e)
    {
        var wasCapturing = _runtime.StopMicrophone();
        StatusText.Text = _runtime.VoiceStatus;
        OutputText.Text = wasCapturing
            ? "麦克风已停止采集；当前桌面任务继续运行。"
            : "已发出停麦信号；麦克风当前未采集，桌面任务继续运行。";
    }

    private void Expand_Click(object sender, RoutedEventArgs e)
    {
        _expanded = !_expanded;
        ExpandedPanel.Visibility = _expanded ? Visibility.Visible : Visibility.Collapsed;
        Height = _expanded ? 590 : 176;
        ExpandButton.Content = _expanded ? "—" : "＋";
        ExpandButton.ToolTip = _expanded ? "收起" : "展开";
    }

    private void Hide_Click(object sender, RoutedEventArgs e) => Hide();

    private void Settings_Click(object sender, RoutedEventArgs e) => ShowSettings();

    private async void History_Click(object sender, RoutedEventArgs e) => await ShowTaskHistoryAsync();

    private async Task ShowTaskHistoryAsync()
    {
        if (_exiting) return;
        if (!IsVisible) RestoreFromTray();
        try
        {
            var history = await _runtime.GetRecentTaskHistoryAsync(CancellationToken.None);
            var dialog = new TaskHistoryWindow(history) { Owner = this };
            dialog.ShowDialog();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException
            or InvalidOperationException or ArgumentException)
        {
            OutputText.Text = "无法读取本地任务记录；未修改或删除任何任务数据。请检查数据目录和隔离工作区。";
        }
    }

    private void ShowSettings()
    {
        if (!IsVisible) RestoreFromTray();
        var dialog = new SettingsWindow(XiaoKSettings.Load(), _notificationMonitor,
            _runtime.GetContactReplyStylesAsync, _runtime.ReplaceContactReplyStylesAsync, _runtime.ActiveDatabasePath,
            _runtime.CreateDatabaseBackupAsync, _runtime.RestoreDatabaseBackupAsync,
            _runtime.GetRecentApprovalAuditAsync, _runtime.GetLocalDataCleanupPreviewAsync,
            _runtime.ClearLocalDataAsync) { Owner = this };
        if (dialog.ShowDialog() == true)
        {
            _ = ApplySettingsAndReportAsync();
        }
    }

    private async Task ApplySettingsAndReportAsync()
    {
        var notificationStatus = await _notificationMonitor.ApplySettingsAsync(XiaoKSettings.Load());
        OutputText.Text = $"设置已保存。登录启动和通知监听立即生效；数据与推理路径将在重启小K后生效。\n{notificationStatus}";
    }

    private void OnNotificationStatusChanged(string message)
    {
        if (_exiting) return;
        StatusText.Text = message.StartsWith("微信通知：", StringComparison.Ordinal) ? "收到微信通知" :
            message.StartsWith("QQ 通知：", StringComparison.Ordinal) ? "收到 QQ 通知" : StatusText.Text;
        OutputText.Text = message;
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        _source = (HwndSource)PresentationSource.FromVisual(this)!;
        _source.AddHook(HotkeyHook);
        _hotkeyRegistered = RegisterHotKey(_source.Handle, 1901, ModControl | ModShift, (uint)System.Windows.Forms.Keys.K);
        if (!_hotkeyRegistered) StatusText.Text = "待命 · Ctrl+Shift+K 不可用，可从托盘打开";
    }

    private IntPtr HotkeyHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (App.RestoreMessageId != 0 && msg == (int)App.RestoreMessageId)
        {
            RestoreFromTray();
            handled = true;
        }
        else if (msg == WmHotkey && wParam.ToInt32() == 1901)
        {
            RestoreFromTray();
            handled = true;
        }
        return IntPtr.Zero;
    }

    private void RestoreFromTray()
    {
        if (_exiting) return;
        Show();
        WindowState = WindowState.Normal;
        Activate();
        if (_expanded) RequestBox.Focus();
    }

    private Forms.ContextMenuStrip BuildTrayMenu()
    {
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("显示小K", null, (_, _) => RestoreFromTray());
        menu.Items.Add("最近任务", null, async (_, _) => await ShowTaskHistoryAsync());
        menu.Items.Add("设置", null, (_, _) => ShowSettings());
        menu.Items.Add("取消当前任务", null, (_, _) => _runtime.CancelCurrent());
        menu.Items.Add("退出", null, (_, _) => { _exiting = true; Close(); });
        return menu;
    }

    private async void OnClosing(object? sender, CancelEventArgs e)
    {
        if (!_exiting) { e.Cancel = true; Hide(); return; }
        if (!_shutdownComplete)
        {
            e.Cancel = true;
            if (_shutdownInProgress) return;
            _shutdownInProgress = true;
            StatusText.Text = "正在停止任务并保存状态…";
            SetButtonsEnabled(false);
            CancelButton.IsEnabled = false;
            _tray.Visible = false;
            _notificationMonitor.Dispose();
            if (_source is not null && _hotkeyRegistered)
            {
                UnregisterHotKey(_source.Handle, 1901);
                _hotkeyRegistered = false;
            }
            try { await _runtime.DisposeAsync(); }
            catch (Exception) { StatusText.Text = "退出清理未能完整确认；任务不会自动重放。"; }
            _shutdownComplete = true;
            Close();
            return;
        }

        _tray.Visible = false;
        _tray.Dispose();
        _notificationMonitor.StatusChanged -= OnNotificationStatusChanged;
        if (_source is not null)
        {
            if (_hotkeyRegistered) UnregisterHotKey(_source.Handle, 1901);
            _source.RemoveHook(HotkeyHook);
        }
    }

    private void SetButtonsEnabled(bool enabled)
    {
        RequestBox.IsEnabled = enabled;
        RunButton.IsEnabled = enabled;
        CancelButton.IsEnabled = !enabled;
        OpenProjectButton.IsEnabled = enabled;
        StopMicButton.IsEnabled = true;
    }

    private const int WmHotkey = 0x0312;
    private const uint ModControl = 0x0002;
    private const uint ModShift = 0x0004;
    [DllImport("user32.dll")] private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);
    [DllImport("user32.dll")] private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
}
