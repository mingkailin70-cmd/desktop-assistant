using System.Runtime.InteropServices;
using System.Diagnostics;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Threading;
using XiaoK.Adapters.Windows;

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
            var desktop = new WindowsDesktopTools(
                [new DesktopApp("uia-probe", Environment.ProcessPath!)],
                [], new SyntheticWindowProcessController(probe.Handle));
            var isolated = await new WindowsUiAutomationSnapshotAdapter(desktop)
                .ReadSnapshotAsync("uia-probe", CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(15));
            if (!isolated.Success || isolated.Data is null
                || !isolated.Data.Contains(AutomationId, StringComparison.Ordinal)
                || !isolated.Data.Contains(AccessibleName, StringComparison.Ordinal))
                throw new InvalidOperationException($"隔离UIA工作进程未返回合成控件快照：{isolated.ErrorCode ?? isolated.Summary}");
            await VerifyWorkerCancellationAndTimeoutAsync(desktop);
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

    private sealed class SyntheticWindowProcessController(IntPtr handle) : IDesktopAppProcessController
    {
        public IDisposable? Start(System.Diagnostics.ProcessStartInfo startInfo) => null;
        public IReadOnlyCollection<IntPtr> GetVisibleWindowHandles(DesktopApp app) => [handle];
    }

    private static async Task VerifyWorkerCancellationAndTimeoutAsync(WindowsDesktopTools desktop)
    {
        var cancellationStarter = new BlockingWorkerProcessStarter();
        var cancellationAdapter = new WindowsUiAutomationSnapshotAdapter(desktop, cancellationStarter);
        using (var cancellation = new CancellationTokenSource())
        {
            var operation = cancellationAdapter.ReadSnapshotAsync("uia-probe", cancellation.Token);
            await cancellationStarter.Started.WaitAsync(TimeSpan.FromSeconds(5));
            cancellation.Cancel();
            var cancelled = false;
            try { await operation; }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { cancelled = true; }
            if (!cancelled) throw new InvalidOperationException("用户取消没有中止UIA子进程。");
            await RequireProcessExitedAsync(cancellationStarter.ProcessId);
        }

        var timeoutStarter = new BlockingWorkerProcessStarter();
        var timeoutAdapter = new WindowsUiAutomationSnapshotAdapter(desktop, timeoutStarter);
        var timedOperation = timeoutAdapter.ReadSnapshotAsync("uia-probe", CancellationToken.None);
        await timeoutStarter.Started.WaitAsync(TimeSpan.FromSeconds(5));
        var timed = await timedOperation.WaitAsync(TimeSpan.FromSeconds(8));
        if (timed.Success || timed.ErrorCode != "UIA_WORKER_TIMEOUT")
            throw new InvalidOperationException("无响应UIA子进程没有在5秒后被终止。");
        await RequireProcessExitedAsync(timeoutStarter.ProcessId);

        await VerifyWorkerKilledWhenHostExitsAsync();
    }

    private static async Task VerifyWorkerKilledWhenHostExitsAsync()
    {
        var executablePath = Environment.ProcessPath
            ?? throw new InvalidOperationException("无法定位UIA工作进程测试宿主。");
        var ownerStartInfo = new ProcessStartInfo(executablePath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        ownerStartInfo.ArgumentList.Add("--uia-worker-job-owner-test");
        using var owner = Process.Start(ownerStartInfo)
            ?? throw new InvalidOperationException("无法启动Job Object所有者测试进程。");
        var workerProcessId = 0;
        try
        {
            var processIdLine = await owner.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(8));
            if (!int.TryParse(processIdLine, out workerProcessId))
            {
                await owner.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
                var error = await owner.StandardError.ReadToEndAsync().WaitAsync(TimeSpan.FromSeconds(2));
                throw new InvalidOperationException($"Job Object所有者没有启动工作进程：{error}");
            }

            owner.Kill(entireProcessTree: false);
            await owner.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            await RequireProcessExitedAsync(workerProcessId);
        }
        finally
        {
            if (!owner.HasExited) owner.Kill(entireProcessTree: true);
            if (workerProcessId > 0) TryKillProcess(workerProcessId);
        }
    }

    private static void TryKillProcess(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException
            or System.ComponentModel.Win32Exception or NotSupportedException) { }
    }

    private static async Task RequireProcessExitedAsync(int processId)
    {
        for (var attempt = 0; attempt < 30; attempt++)
        {
            try
            {
                using var process = Process.GetProcessById(processId);
                if (process.HasExited) return;
            }
            catch (ArgumentException) { return; }
            await Task.Delay(100);
        }
        throw new InvalidOperationException("UIA子进程在取消或超时后仍在运行。");
    }

    private sealed class BlockingWorkerProcessStarter : IUiAutomationWorkerProcessStarter
    {
        private readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task Started => _started.Task;
        public int ProcessId { get; private set; }

        public UiAutomationWorkerProcess? Start(ProcessStartInfo startInfo)
        {
            startInfo.ArgumentList.Add("--test-block");
            var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
            if (!process.Start())
            {
                process.Dispose();
                return null;
            }
            ProcessId = process.Id;
            _started.TrySetResult();
            return new UiAutomationWorkerProcess(process, NoopLifetime.Instance);
        }
    }

    private sealed class NoopLifetime : IDisposable
    {
        public static NoopLifetime Instance { get; } = new();
        public void Dispose() { }
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    private sealed record ProbeWindow(IntPtr Handle, Dispatcher Dispatcher, Window Window);
}
