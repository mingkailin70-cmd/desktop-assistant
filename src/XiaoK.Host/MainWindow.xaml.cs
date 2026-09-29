using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using Forms = System.Windows.Forms;
using XiaoK.Core;

namespace XiaoK.Host;

public partial class MainWindow : Window, IApprovalPresenter
{
    private readonly AssistantRuntime _runtime;
    private readonly Forms.NotifyIcon _tray;
    private HwndSource? _source;
    private bool _exiting;
    private bool _expanded = true;
    private bool _hotkeyRegistered;

    public MainWindow()
    {
        InitializeComponent();
        _runtime = new AssistantRuntime(this);
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

    public Task<bool> ConfirmAsync(string title, string details, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var answer = System.Windows.MessageBox.Show(this, details, title, System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Warning, System.Windows.MessageBoxResult.No);
        return Task.FromResult(answer == System.Windows.MessageBoxResult.Yes);
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
        _runtime.CancelCurrent();
        StatusText.Text = _runtime.VoiceStatus;
        OutputText.Text = "停麦信号已立即发出。当前语音运行时尚未安装，麦克风未采集。";
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

    private void ShowSettings()
    {
        if (!IsVisible) RestoreFromTray();
        var dialog = new SettingsWindow(_runtime.CurrentSettings) { Owner = this };
        if (dialog.ShowDialog() == true)
        {
            OutputText.Text = "设置已保存。登录启动立即生效；数据和推理路径将在重启小K后生效。";
        }
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
        Show();
        WindowState = WindowState.Normal;
        Activate();
        if (_expanded) RequestBox.Focus();
    }

    private Forms.ContextMenuStrip BuildTrayMenu()
    {
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("显示小K", null, (_, _) => RestoreFromTray());
        menu.Items.Add("设置", null, (_, _) => ShowSettings());
        menu.Items.Add("取消当前任务", null, (_, _) => _runtime.CancelCurrent());
        menu.Items.Add("退出", null, (_, _) => { _exiting = true; Close(); });
        return menu;
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (!_exiting) { e.Cancel = true; Hide(); return; }
        _runtime.Dispose();
        _tray.Visible = false;
        _tray.Dispose();
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
