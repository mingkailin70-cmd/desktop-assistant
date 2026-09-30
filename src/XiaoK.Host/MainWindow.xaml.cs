using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
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
    private bool _changingWindowMode;
    private double _expandedWidth = 428;
    private double _expandedHeight = 590;

    public MainWindow()
    {
        InitializeComponent();
        _runtime = new AssistantRuntime(this);
        _notificationMonitor = new WindowsNotificationMonitor(Dispatcher);
        _notificationMonitor.StatusChanged += OnNotificationStatusChanged;
        _ = _notificationMonitor.ApplySettingsAsync(_runtime.CurrentSettings);
        FooterText.Text = _runtime.ModelStatus;
        SetStatus(_runtime.VoiceStatus);
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
        SetExpandedView(expanded: false);
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
        if (e.ButtonState == System.Windows.Input.MouseButtonState.Pressed)
        {
            try { DragMove(); }
            catch (InvalidOperationException) { return; }
            ClampWindowToMonitorWorkArea();
            SaveWindowPosition();
        }
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
        SetStatus("任务运行中");
        SetButtonsEnabled(false);
        try { OutputText.Text = await _runtime.SubmitAsync(request); }
        finally { SetStatus(_runtime.VoiceStatus); SetButtonsEnabled(true); }
    }

    private async void OpenProject_Click(object sender, RoutedEventArgs e)
    {
        OutputText.Text = "正在打开项目；可随时取消。";
        SetStatus("任务运行中");
        SetButtonsEnabled(false);
        try { OutputText.Text = await _runtime.SubmitAsync("打开小K项目"); }
        finally { SetStatus(_runtime.VoiceStatus); SetButtonsEnabled(true); }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        _runtime.CancelCurrent();
        SetStatus("已请求取消");
    }

    private void StopMic_Click(object sender, RoutedEventArgs e)
    {
        var wasCapturing = _runtime.StopMicrophone();
        SetStatus(_runtime.VoiceStatus);
        OutputText.Text = wasCapturing
            ? "麦克风已停止采集；当前桌面任务继续运行。"
            : "已发出停麦信号；麦克风当前未采集，桌面任务继续运行。";
    }

    private void Expand_Click(object sender, RoutedEventArgs e)
    {
        SetExpandedView(!_expanded);
    }

    private void Hide_Click(object sender, RoutedEventArgs e) => Hide();

    private void ShowPanel_Click(object sender, RoutedEventArgs e) => RestoreFromTray();

    private void Exit_Click(object sender, RoutedEventArgs e)
    {
        _exiting = true;
        Close();
    }

    private void Settings_Click(object sender, RoutedEventArgs e) => ShowSettings();

    private async void History_Click(object sender, RoutedEventArgs e) => await ShowTaskHistoryAsync();

    private async Task ShowTaskHistoryAsync()
    {
        if (_exiting) return;
        if (!IsVisible || !_expanded) RestoreFromTray();
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
        if (!IsVisible || !_expanded) RestoreFromTray();
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
        if (message.StartsWith("微信通知：", StringComparison.Ordinal)) SetStatus("收到微信通知");
        else if (message.StartsWith("QQ 通知：", StringComparison.Ordinal)) SetStatus("收到 QQ 通知");
        OutputText.Text = message;
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        _source = (HwndSource)PresentationSource.FromVisual(this)!;
        _source.AddHook(HotkeyHook);
        RestoreWindowPosition();
        _hotkeyRegistered = RegisterHotKey(_source.Handle, 1901, ModControl | ModShift, (uint)System.Windows.Forms.Keys.K);
        if (!_hotkeyRegistered) SetStatus("快捷键不可用 · 托盘可打开");
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
        else if (msg is WmDisplayChange or WmDpiChanged)
        {
            _ = Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background,
                new Action(ClampWindowToMonitorWorkArea));
        }
        return IntPtr.Zero;
    }

    private void RestoreFromTray()
    {
        if (_exiting) return;
        SetExpandedView(expanded: true);
        Show();
        WindowState = WindowState.Normal;
        Activate();
        RequestBox.Focus();
    }

    private void ShowPet()
    {
        if (_exiting) return;
        SetExpandedView(expanded: false);
        Show();
        WindowState = WindowState.Normal;
        Activate();
    }

    private Forms.ContextMenuStrip BuildTrayMenu()
    {
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("显示桌宠", null, (_, _) => ShowPet());
        menu.Items.Add("打开任务面板", null, (_, _) => RestoreFromTray());
        menu.Items.Add("最近任务", null, async (_, _) => await ShowTaskHistoryAsync());
        menu.Items.Add("设置", null, (_, _) => ShowSettings());
        menu.Items.Add("取消当前任务", null, (_, _) => CancelFromTray());
        menu.Items.Add("停麦", null, (_, _) => StopMicrophoneFromTray());
        menu.Items.Add("退出", null, (_, _) => Exit_Click(this, new RoutedEventArgs()));
        return menu;
    }

    private void StopMicrophoneFromTray()
    {
        var wasCapturing = _runtime.StopMicrophone();
        SetStatus(_runtime.VoiceStatus);
        OutputText.Text = wasCapturing
            ? "麦克风已停止采集；当前桌面任务继续运行。"
            : "已发出停麦信号；麦克风当前未采集，桌面任务继续运行。";
    }

    private void CancelFromTray()
    {
        _runtime.CancelCurrent();
        SetStatus("已请求取消");
    }

    private void SetExpandedView(bool expanded)
    {
        if (_expanded == expanded) return;
        _changingWindowMode = true;
        try
        {
            if (!expanded && _expanded)
            {
                _expandedWidth = Math.Max(330, Width);
                _expandedHeight = Math.Max(360, Height);
            }

            _expanded = expanded;
            ExpandedView.Visibility = expanded ? Visibility.Visible : Visibility.Collapsed;
            PetView.Visibility = expanded ? Visibility.Collapsed : Visibility.Visible;
            ResizeMode = expanded ? ResizeMode.CanResizeWithGrip : ResizeMode.NoResize;
            MinWidth = expanded ? 330 : 150;
            MinHeight = expanded ? 360 : 150;
            Width = expanded ? _expandedWidth : 164;
            Height = expanded ? _expandedHeight : 164;
            ClampWindowToMonitorWorkArea();
            if (expanded)
            {
                ExpandButton.Content = "收起";
                ExpandButton.ToolTip = "收起到桌宠";
            }
        }
        finally
        {
            _changingWindowMode = false;
        }
    }

    private void Window_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_expanded && !_changingWindowMode)
        {
            _expandedWidth = Width;
            _expandedHeight = Height;
            ClampWindowToMonitorWorkArea();
        }
    }

    private void PetView_MouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (e.ButtonState == System.Windows.Input.MouseButtonState.Pressed
            && e.OriginalSource is FrameworkElement element
            && element.Name is not ("PetStatusText" or "PetStatusDot"))
        {
            try
            {
                DragMove();
                ClampWindowToMonitorWorkArea();
                SaveWindowPosition();
            }
            catch (InvalidOperationException) { }
        }
    }

    private void RestoreWindowPosition()
    {
        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero || !GetWindowRect(handle, out var current)) return;

        var settings = _runtime.CurrentSettings;
        if (settings.PetWindowLeftPixels is int savedLeft && settings.PetWindowTopPixels is int savedTop)
        {
            SetWindowPosition(handle, savedLeft, savedTop);
        }
        else if (settings.PetWindowLeft is double legacyLeft && settings.PetWindowTop is double legacyTop
            && double.IsFinite(legacyLeft) && double.IsFinite(legacyTop))
        {
            var dpi = GetEffectiveDpi(handle);
            SetWindowPosition(handle, (int)Math.Round(legacyLeft * dpi / 96d),
                (int)Math.Round(legacyTop * dpi / 96d));
        }
        else
        {
            if (Forms.Screen.PrimaryScreen is { } primaryScreen)
            {
                var workArea = primaryScreen.WorkingArea;
                var margin = (int)Math.Round(24 * GetEffectiveDpi(handle) / 96d);
                var width = current.Right - current.Left;
                var height = current.Bottom - current.Top;
                SetWindowPosition(handle, Math.Max(workArea.Left, workArea.Right - width - margin),
                    Math.Max(workArea.Top, workArea.Bottom - height - margin));
            }
        }

        ClampWindowToMonitorWorkArea();
    }

    private void ClampWindowToMonitorWorkArea()
    {
        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero || !GetWindowRect(handle, out var rect)) return;

        var monitor = MonitorFromRect(ref rect, MonitorDefaultToNearest);
        if (monitor == IntPtr.Zero) return;
        var info = new MonitorInfo { Size = (uint)Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfo(monitor, ref info)) return;

        var width = rect.Right - rect.Left;
        var height = rect.Bottom - rect.Top;
        var rightmostLeft = Math.Max(info.Work.Left, info.Work.Right - width);
        var bottommostTop = Math.Max(info.Work.Top, info.Work.Bottom - height);
        var left = Math.Clamp(rect.Left, info.Work.Left, rightmostLeft);
        var top = Math.Clamp(rect.Top, info.Work.Top, bottommostTop);
        if (left != rect.Left || top != rect.Top) SetWindowPosition(handle, left, top);
    }

    private static void SetWindowPosition(IntPtr handle, int left, int top)
    {
        _ = SetWindowPos(handle, IntPtr.Zero, left, top, 0, 0, SwpNoSize | SwpNoZOrder | SwpNoActivate);
    }

    private static uint GetEffectiveDpi(IntPtr handle)
    {
        var dpi = GetDpiForWindow(handle);
        return dpi == 0 ? 96u : dpi;
    }

    private void SaveWindowPosition()
    {
        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero || !GetWindowRect(handle, out var rect)) return;
        try
        {
            var latest = XiaoKSettings.Load();
            (latest with
            {
                PetWindowLeft = null,
                PetWindowTop = null,
                PetWindowLeftPixels = rect.Left,
                PetWindowTopPixels = rect.Top
            }).Save();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
            or System.Security.SecurityException or InvalidOperationException or ArgumentException)
        {
            SetStatus("桌宠位置保存失败");
        }
    }

    private void SetStatus(string status)
    {
        StatusText.Text = status;
        PetStatusText.Text = status;
        PetView.ToolTip = $"{status} · 单击展开任务面板，右键打开菜单";
        var color = status.Contains("失败", StringComparison.Ordinal) || status.Contains("不可用", StringComparison.Ordinal)
            ? System.Windows.Media.Color.FromRgb(205, 69, 69)
            : status.Contains("确认", StringComparison.Ordinal) || status.Contains("取消", StringComparison.Ordinal)
                ? System.Windows.Media.Color.FromRgb(216, 144, 38)
                : status.Contains("运行", StringComparison.Ordinal) || status.Contains("正在", StringComparison.Ordinal)
                    ? System.Windows.Media.Color.FromRgb(63, 118, 232)
                    : status.Contains("未安装", StringComparison.Ordinal) || status.Contains("未采集", StringComparison.Ordinal)
                        ? System.Windows.Media.Color.FromRgb(123, 132, 152)
                        : System.Windows.Media.Color.FromRgb(87, 163, 112);
        PetStatusDot.Fill = new SolidColorBrush(color);
    }

    private async void OnClosing(object? sender, CancelEventArgs e)
    {
        if (!_exiting) { e.Cancel = true; Hide(); return; }
        if (!_shutdownComplete)
        {
            e.Cancel = true;
            if (_shutdownInProgress) return;
            _shutdownInProgress = true;
            SetStatus("正在停止任务…");
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
            catch (Exception) { SetStatus("退出清理未能完整确认"); }
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
    private const int WmDisplayChange = 0x007E;
    private const int WmDpiChanged = 0x02E0;
    private const uint ModControl = 0x0002;
    private const uint ModShift = 0x0004;
    private const uint MonitorDefaultToNearest = 2;
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpNoActivate = 0x0010;

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public uint Size;
        public NativeRect Monitor;
        public NativeRect Work;
        public uint Flags;
    }

    [DllImport("user32.dll")] private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);
    [DllImport("user32.dll")] private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr hWnd, out NativeRect rect);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr MonitorFromRect(ref NativeRect rect, uint flags);
    [DllImport("user32.dll", EntryPoint = "GetMonitorInfoW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MonitorInfo info);
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y,
        int width, int height, uint flags);
    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hWnd);
}
