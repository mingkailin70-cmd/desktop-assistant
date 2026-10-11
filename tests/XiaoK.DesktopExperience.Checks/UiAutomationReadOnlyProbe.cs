using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Threading;

internal static class UiAutomationReadOnlyProbe
{
    private const string AutomationId = "xiaok-synthetic-readonly-button";
    private const string AccessibleName = "小K合成只读探针按钮";

    public static async Task RunAsync()
    {
        var foregroundBefore = GetForegroundWindow();
        if (foregroundBefore == IntPtr.Zero)
            throw new InvalidOperationException("没有可核对的初始前台窗口；未创建探针窗口。");

        var created = new TaskCompletionSource<ProbeWindow>(TaskCreationOptions.RunContinuationsAsynchronously);
        var activationCount = 0;
        var windowThread = new Thread(() =>
        {
            try
            {
                var button = new Button { Content = "合成控件" };
                AutomationProperties.SetAutomationId(button, AutomationId);
                AutomationProperties.SetName(button, AccessibleName);
                button.Click += (_, _) => Interlocked.Increment(ref activationCount);

                var window = new Window
                {
                    Title = "小K UIA 只读合成探针",
                    Width = 280,
                    Height = 160,
                    Left = -32000,
                    Top = -32000,
                    WindowStyle = WindowStyle.ToolWindow,
                    ShowInTaskbar = false,
                    ShowActivated = false,
                    Content = new StackPanel { Children = { button } }
                };
                var dispatcher = window.Dispatcher;
                window.Closed += (_, _) => dispatcher.BeginInvokeShutdown(DispatcherPriority.Background);
                window.SourceInitialized += (_, _) =>
                {
                    var handle = new System.Windows.Interop.WindowInteropHelper(window).Handle;
                    created.TrySetResult(new ProbeWindow(handle, dispatcher, window));
                };
                window.Show();
                Dispatcher.Run();
            }
            catch (Exception exception)
            {
                created.TrySetException(exception);
            }
        }) { IsBackground = true, Name = "XiaoK UIA synthetic provider" };
        windowThread.SetApartmentState(ApartmentState.STA);
        windowThread.Start();

        ProbeWindow? probe = null;
        try
        {
            probe = await created.Task.WaitAsync(TimeSpan.FromSeconds(10));
            if (GetForegroundWindow() != foregroundBefore)
                throw new InvalidOperationException("显示离屏合成窗口改变了前台窗口。");

            var result = await Task.Run(() => ReadOnlyInspect(probe.Handle)).WaitAsync(TimeSpan.FromSeconds(10));
            if (result.AutomationId != AutomationId || result.Name != AccessibleName || !result.IsEnabled)
                throw new InvalidOperationException("UIA只读查询没有返回合成控件的预期标识属性。");
            if (Volatile.Read(ref activationCount) != 0)
                throw new InvalidOperationException("只读UIA查询触发了合成控件动作。");
            if (GetForegroundWindow() != foregroundBefore)
                throw new InvalidOperationException("UIA只读查询改变了前台窗口。");
        }
        finally
        {
            if (probe is not null)
            {
                await probe.Dispatcher.InvokeAsync(probe.Window.Close);
                if (!windowThread.Join(TimeSpan.FromSeconds(5)))
                    throw new InvalidOperationException("离屏合成探针窗口未在限定时间内退出。");
            }
        }

        if (GetForegroundWindow() != foregroundBefore)
            throw new InvalidOperationException("关闭离屏合成窗口后前台窗口句柄发生变化。");
    }

    private static (string AutomationId, string Name, bool IsEnabled) ReadOnlyInspect(IntPtr handle)
    {
        var root = AutomationElement.FromHandle(handle);
        var condition = new PropertyCondition(AutomationElement.AutomationIdProperty, AutomationId);
        var button = root.FindFirst(TreeScope.Descendants, condition)
            ?? throw new InvalidOperationException("UIA树中未找到合成按钮。");
        return (button.Current.AutomationId, button.Current.Name, button.Current.IsEnabled);
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    private sealed record ProbeWindow(IntPtr Handle, Dispatcher Dispatcher, Window Window);
}
