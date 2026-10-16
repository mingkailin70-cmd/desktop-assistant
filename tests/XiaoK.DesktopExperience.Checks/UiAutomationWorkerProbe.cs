using System.Runtime.InteropServices;
using System.Diagnostics;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Threading;
using XiaoK.Adapters.Windows;

internal static class UiAutomationWorkerProbe
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
        var successfulInvocationCount = 0;
        var windowThread = new Thread(() =>
        {
            try
            {
                var button = new Button { Content = "合成控件", IsEnabled = false };
                AutomationProperties.SetAutomationId(button, AutomationId);
                AutomationProperties.SetName(button, AccessibleName);
                button.Click += (_, _) => Interlocked.Increment(ref activationCount);
                var invokableButton = new Button { Content = "合成可调用按钮", IsEnabled = true };
                AutomationProperties.SetAutomationId(invokableButton, "xiaok-synthetic-invoke-button");
                invokableButton.Click += (_, _) => Interlocked.Increment(ref successfulInvocationCount);
                var waitButton = new Button { Content = "等待控件", IsEnabled = false };
                AutomationProperties.SetAutomationId(waitButton, "xiaok-synthetic-never-enabled");
                var duplicateWaitButton = new Button { Content = "重复目标", IsEnabled = true };
                AutomationProperties.SetAutomationId(duplicateWaitButton, "xiaok-synthetic-duplicate");
                var secondDuplicateWaitButton = new Button { Content = "重复目标二", IsEnabled = true };
                AutomationProperties.SetAutomationId(secondDuplicateWaitButton, "xiaok-synthetic-duplicate");
                var wideControlPanel = new StackPanel();
                for (var index = 0; index < 65; index++)
                {
                    var wideButton = new Button { Content = $"宽树项目{index}", IsEnabled = true };
                    if (index is 0 or 64)
                        AutomationProperties.SetAutomationId(wideButton, "xiaok-synthetic-wide-duplicate");
                    wideControlPanel.Children.Add(wideButton);
                }
                var scopedButton = new Button { Content = "SCOPED_TEXT_SENTINEL", IsEnabled = true };
                AutomationProperties.SetAutomationId(scopedButton, "xiaok-synthetic-child-id");
                AutomationProperties.SetName(scopedButton, "SCOPED_NAME_SENTINEL");
                var scopedRoot = new GroupBox
                {
                    Header = "SCOPED_HEADER_SENTINEL",
                    Content = new StackPanel { Children = { scopedButton } }
                };
                AutomationProperties.SetAutomationId(scopedRoot, "xiaok-synthetic-scope");
                var wrongTypeRoot = new Button { Content = "错误根类型" };
                AutomationProperties.SetAutomationId(wrongTypeRoot, "xiaok-synthetic-wrong-root-type");
                var duplicateScopeOne = new GroupBox { Header = "重复区域一", Content = new Button { Content = "A" } };
                var duplicateScopeTwo = new GroupBox { Header = "重复区域二", Content = new Button { Content = "B" } };
                AutomationProperties.SetAutomationId(duplicateScopeOne, "xiaok-synthetic-duplicate-scope");
                AutomationProperties.SetAutomationId(duplicateScopeTwo, "xiaok-synthetic-duplicate-scope");
                var oversizedChildren = new StackPanel();
                for (var index = 0; index < 40; index++)
                    oversizedChildren.Children.Add(new Button { Content = $"项目{index}" });
                var oversizedScope = new GroupBox { Header = "超限区域", Content = oversizedChildren };
                AutomationProperties.SetAutomationId(oversizedScope, "xiaok-synthetic-oversized-scope");

                var window = new Window
                {
                    Title = "小K UIA 只读合成探针",
                    Width = 280,
                    Height = 160,
                    Left = 24,
                    Top = 24,
                    WindowStyle = WindowStyle.ToolWindow,
                    ShowInTaskbar = false,
                    ShowActivated = false,
                    Content = new StackPanel
                    {
                        Children =
                        {
                            button, waitButton, duplicateWaitButton, secondDuplicateWaitButton, scopedRoot,
                            wrongTypeRoot, duplicateScopeOne, duplicateScopeTwo, oversizedScope
                        }
                    }
                };
                var wideWindow = new Window
                {
                    Title = "小K UIA 宽树合成探针",
                    Width = 280,
                    Height = 160,
                    Left = 320,
                    Top = 24,
                    WindowStyle = WindowStyle.ToolWindow,
                    ShowInTaskbar = false,
                    ShowActivated = false,
                    Content = wideControlPanel
                };
                var invokeWindow = new Window
                {
                    Title = "小K UIA 按钮调用合成探针",
                    Width = 280,
                    Height = 120,
                    Left = 620,
                    Top = 24,
                    WindowStyle = WindowStyle.ToolWindow,
                    ShowInTaskbar = false,
                    ShowActivated = false,
                    Content = invokableButton
                };
                var dispatcher = window.Dispatcher;
                void ShutdownWhenAllWindowsClose()
                {
                    if (!window.IsVisible && !wideWindow.IsVisible && !invokeWindow.IsVisible)
                        dispatcher.BeginInvokeShutdown(DispatcherPriority.Background);
                }
                window.Closed += (_, _) => ShutdownWhenAllWindowsClose();
                wideWindow.Closed += (_, _) => ShutdownWhenAllWindowsClose();
                invokeWindow.Closed += (_, _) => ShutdownWhenAllWindowsClose();
                IntPtr mainHandle = IntPtr.Zero;
                IntPtr wideHandle = IntPtr.Zero;
                IntPtr invokeHandle = IntPtr.Zero;
                void CompleteWhenReady()
                {
                    if (mainHandle != IntPtr.Zero && wideHandle != IntPtr.Zero && invokeHandle != IntPtr.Zero)
                        created.TrySetResult(new ProbeWindow(mainHandle, wideHandle, invokeHandle, dispatcher, window,
                            wideWindow, invokeWindow, button));
                }
                window.SourceInitialized += (_, _) =>
                {
                    mainHandle = new System.Windows.Interop.WindowInteropHelper(window).Handle;
                    CompleteWhenReady();
                };
                wideWindow.SourceInitialized += (_, _) =>
                {
                    wideHandle = new System.Windows.Interop.WindowInteropHelper(wideWindow).Handle;
                    CompleteWhenReady();
                };
                invokeWindow.SourceInitialized += (_, _) =>
                {
                    invokeHandle = new System.Windows.Interop.WindowInteropHelper(invokeWindow).Handle;
                    CompleteWhenReady();
                };
                window.Show();
                wideWindow.Show();
                invokeWindow.Show();
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
                throw new InvalidOperationException("显示非激活合成窗口改变了前台窗口。");

            var result = await Task.Run(() => ReadOnlyInspect(probe.Handle)).WaitAsync(TimeSpan.FromSeconds(10));
            if (result.AutomationId != AutomationId || result.Name != AccessibleName || result.IsEnabled)
                throw new InvalidOperationException("UIA只读查询没有返回合成控件的预期标识与禁用状态。");
            var desktop = new WindowsDesktopTools(
                [new DesktopApp("uia-probe", Environment.ProcessPath!)],
                [], new SyntheticWindowProcessController(probe.Handle));
            using var preCancelledAction = new CancellationTokenSource();
            preCancelledAction.Cancel();
            var preCancelledStarter = new BlockingWorkerProcessStarter();
            var preCancelledResult = await new WindowsUiAutomationSnapshotAdapter(desktop, preCancelledStarter)
                .InvokeButtonAsync("uia-probe", AutomationId, preCancelledAction.Token)
                .WaitAsync(TimeSpan.FromSeconds(5));
            if (preCancelledResult.Success || preCancelledResult.ErrorCode != "UIA_ACTION_CANCELLED_BEFORE_OPERATION"
                || preCancelledResult.FinalState != XiaoK.Core.TaskLifecycleState.Cancelled
                || preCancelledStarter.Started.IsCompleted || Volatile.Read(ref activationCount) != 0)
                throw new InvalidOperationException("工作进程启动前已取消的按钮调用没有明确报告未执行。");
            var blockedButtonStarter = new BlockingWorkerProcessStarter();
            var blockedButton = await new WindowsUiAutomationSnapshotAdapter(desktop, blockedButtonStarter)
                .InvokeButtonAsync("uia-probe", "Button.DeleteItem", CancellationToken.None)
                .WaitAsync(TimeSpan.FromSeconds(5));
            if (blockedButton.Success || blockedButton.ErrorCode != "UIA_BUTTON_ACTION_BLOCKED"
                || blockedButtonStarter.Started.IsCompleted || Volatile.Read(ref activationCount) != 0)
                throw new InvalidOperationException("高风险AutomationId没有在访问目标窗口或启动工作进程前被拒绝。");
            var unlisted = await new WindowsUiAutomationSnapshotAdapter(new WindowsDesktopTools([], []))
                .WaitForEnabledControlAsync("uia-probe", AutomationId, CancellationToken.None);
            if (unlisted.Success || unlisted.ErrorCode != "APP_NOT_ALLOWLISTED")
                throw new InvalidOperationException("UIA状态等待未在启动辅助进程前拒绝未登记应用。");
            var isolated = await new WindowsUiAutomationSnapshotAdapter(desktop)
                .ReadSnapshotAsync("uia-probe", CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(15));
            if (!isolated.Success || isolated.Data is null
                || !isolated.Data.Contains(AutomationId, StringComparison.Ordinal)
                || !isolated.Data.Contains(AccessibleName, StringComparison.Ordinal)
                || !isolated.Data.Contains("禁用", StringComparison.Ordinal))
                throw new InvalidOperationException($"隔离UIA工作进程未返回合成控件快照：{isolated.ErrorCode ?? isolated.Summary}");
            var buttonAction = await new WindowsUiAutomationSnapshotAdapter(desktop)
                .InvokeButtonAsync("uia-probe", AutomationId, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(15));
            if (buttonAction.Success || buttonAction.ErrorCode != "UIA_BUTTON_NOT_INVOKABLE"
                || Volatile.Read(ref activationCount) != 0 || Volatile.Read(ref successfulInvocationCount) != 0
                || GetForegroundWindow() != foregroundBefore)
                throw new InvalidOperationException("隔离UIA worker没有拒绝禁用的合成按钮或改变了前台/控件状态。");
            var ambiguousButton = await new WindowsUiAutomationSnapshotAdapter(desktop)
                .InvokeButtonAsync("uia-probe", "xiaok-synthetic-duplicate", CancellationToken.None)
                .WaitAsync(TimeSpan.FromSeconds(15));
            if (ambiguousButton.Success || ambiguousButton.ErrorCode != "UIA_AUTOMATION_ID_AMBIGUOUS"
                || Volatile.Read(ref successfulInvocationCount) != 0 || GetForegroundWindow() != foregroundBefore)
                throw new InvalidOperationException("隔离UIA worker没有拒绝重复AutomationId或改变了控件/前台状态。");
            var wideDesktop = new WindowsDesktopTools(
                [new DesktopApp("uia-probe", Environment.ProcessPath!)],
                [], new SyntheticWindowProcessController(probe.WideHandle));
            var oversizedSearch = await new WindowsUiAutomationSnapshotAdapter(wideDesktop)
                .InvokeButtonAsync("uia-probe", "xiaok-synthetic-wide-duplicate", CancellationToken.None)
                .WaitAsync(TimeSpan.FromSeconds(15));
            if (oversizedSearch.Success || oversizedSearch.ErrorCode != "UIA_TREE_LIMIT"
                || Volatile.Read(ref successfulInvocationCount) != 0 || GetForegroundWindow() != foregroundBefore)
                throw new InvalidOperationException($"UIA控件搜索超过同级上限时仍把被截断的AutomationId误判为唯一或改变了界面：{oversizedSearch.ErrorCode ?? oversizedSearch.Summary}");
            var invokeDesktop = new WindowsDesktopTools(
                [new DesktopApp("uia-probe", Environment.ProcessPath!)],
                [], new SyntheticWindowProcessController(probe.InvokeHandle));
            var invokedButton = await new WindowsUiAutomationSnapshotAdapter(invokeDesktop)
                .InvokeButtonAsync("uia-probe", "xiaok-synthetic-invoke-button", CancellationToken.None)
                .WaitAsync(TimeSpan.FromSeconds(15));
            var invocationCount = Volatile.Read(ref successfulInvocationCount);
            var foregroundUnchanged = GetForegroundWindow() == foregroundBefore;
            if (!invokedButton.Success || invocationCount != 1 || !foregroundUnchanged)
                throw new InvalidOperationException(
                    $"隔离UIA worker唯一可见按钮调用检查失败：error={invokedButton.ErrorCode ?? "NONE"}; "
                    + $"state={invokedButton.FinalState}; calls={invocationCount}; "
                    + $"foregroundUnchanged={foregroundUnchanged}; summary={invokedButton.Summary}");
            var scoped = await new WindowsUiAutomationSnapshotAdapter(desktop)
                .ReadScopedSnapshotAsync("uia-probe", "xiaok-synthetic-scope", "Group", CancellationToken.None)
                .WaitAsync(TimeSpan.FromSeconds(15));
            if (!scoped.Success || scoped.Data is null
                || !scoped.Data.Contains("ControlType.Group", StringComparison.Ordinal)
                || !scoped.Data.Contains("ControlType.Button", StringComparison.Ordinal)
                || scoped.Data.Contains("SCOPED_TEXT_SENTINEL", StringComparison.Ordinal)
                || scoped.Data.Contains("SCOPED_NAME_SENTINEL", StringComparison.Ordinal)
                || scoped.Data.Contains("SCOPED_HEADER_SENTINEL", StringComparison.Ordinal)
                || scoped.Data.Contains("xiaok-synthetic-child-id", StringComparison.Ordinal))
                throw new InvalidOperationException($"窄范围UIA没有只返回控件类型和状态元数据：{scoped.ErrorCode ?? scoped.Summary}");
            var missingScope = await new WindowsUiAutomationSnapshotAdapter(desktop)
                .ReadScopedSnapshotAsync("uia-probe", "xiaok-synthetic-missing-scope", "Group", CancellationToken.None)
                .WaitAsync(TimeSpan.FromSeconds(10));
            if (missingScope.Success || missingScope.ErrorCode != "UIA_SCOPED_ROOT_NOT_FOUND")
                throw new InvalidOperationException("不存在的窄范围根控件没有失败关闭。");
            var wrongRootType = await new WindowsUiAutomationSnapshotAdapter(desktop)
                .ReadScopedSnapshotAsync("uia-probe", "xiaok-synthetic-wrong-root-type", "Group", CancellationToken.None)
                .WaitAsync(TimeSpan.FromSeconds(10));
            if (wrongRootType.Success || wrongRootType.ErrorCode != "UIA_SCOPED_ROOT_TYPE_MISMATCH")
                throw new InvalidOperationException("根控件类型与用户指定值不一致时没有失败关闭。");
            var ambiguousScope = await new WindowsUiAutomationSnapshotAdapter(desktop)
                .ReadScopedSnapshotAsync("uia-probe", "xiaok-synthetic-duplicate-scope", "Group", CancellationToken.None)
                .WaitAsync(TimeSpan.FromSeconds(10));
            if (ambiguousScope.Success || ambiguousScope.ErrorCode != "UIA_SCOPED_ROOT_AMBIGUOUS")
                throw new InvalidOperationException("重复根AutomationId没有失败关闭。");
            var oversizedScopeResult = await new WindowsUiAutomationSnapshotAdapter(desktop)
                .ReadScopedSnapshotAsync("uia-probe", "xiaok-synthetic-oversized-scope", "Group", CancellationToken.None)
                .WaitAsync(TimeSpan.FromSeconds(10));
            if (oversizedScopeResult.Success || oversizedScopeResult.ErrorCode != "UIA_SCOPED_TREE_LIMIT")
                throw new InvalidOperationException("超过有界子树限制时仍返回了部分UIA结果。");
            var enableSyntheticControl = Task.Run(async () =>
            {
                await Task.Delay(350);
                await probe.Dispatcher.InvokeAsync(() => probe.Button.IsEnabled = true);
            });
            var waitForEnabled = await new WindowsUiAutomationSnapshotAdapter(desktop)
                .WaitForEnabledControlAsync("uia-probe", AutomationId, CancellationToken.None)
                .WaitAsync(TimeSpan.FromSeconds(8));
            await enableSyntheticControl;
            if (!waitForEnabled.Success || waitForEnabled.Data != "控件可用")
                throw new InvalidOperationException($"唯一合成控件启用后的只读状态等待失败：{waitForEnabled.ErrorCode ?? waitForEnabled.Summary}");
            var timedCondition = await new WindowsUiAutomationSnapshotAdapter(desktop)
                .WaitForEnabledControlAsync("uia-probe", "xiaok-synthetic-never-enabled", CancellationToken.None)
                .WaitAsync(TimeSpan.FromSeconds(8));
            if (timedCondition.Success || timedCondition.ErrorCode != "UIA_CONDITION_NOT_MET")
                throw new InvalidOperationException("控件未启用时没有按固定时限失败关闭。");
            var ambiguousCondition = await new WindowsUiAutomationSnapshotAdapter(desktop)
                .WaitForEnabledControlAsync("uia-probe", "xiaok-synthetic-duplicate", CancellationToken.None)
                .WaitAsync(TimeSpan.FromSeconds(8));
            if (ambiguousCondition.Success || ambiguousCondition.ErrorCode != "UIA_AUTOMATION_ID_AMBIGUOUS")
                throw new InvalidOperationException($"重复AutomationId没有失败关闭：{ambiguousCondition.ErrorCode ?? "NO_ERROR"} / {ambiguousCondition.Summary}");
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
                await probe.Dispatcher.InvokeAsync(() =>
                {
                    if (probe.InvokeWindow.IsVisible) probe.InvokeWindow.Close();
                    if (probe.WideWindow.IsVisible) probe.WideWindow.Close();
                    if (probe.Window.IsVisible) probe.Window.Close();
                });
                if (!windowThread.Join(TimeSpan.FromSeconds(5)))
                    throw new InvalidOperationException("合成探针窗口未在限定时间内退出。");
            }
        }

        if (GetForegroundWindow() != foregroundBefore)
            throw new InvalidOperationException("关闭合成窗口后前台窗口句柄发生变化。");
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

        var actionTimeoutStarter = new BlockingWorkerProcessStarter();
        var actionTimeoutAdapter = new WindowsUiAutomationSnapshotAdapter(desktop, actionTimeoutStarter);
        var actionTimeoutOperation = actionTimeoutAdapter.InvokeButtonAsync("uia-probe", AutomationId,
            CancellationToken.None);
        await actionTimeoutStarter.Started.WaitAsync(TimeSpan.FromSeconds(5));
        var uncertainAction = await actionTimeoutOperation.WaitAsync(TimeSpan.FromSeconds(8));
        if (uncertainAction.Success || uncertainAction.ErrorCode != "UIA_ACTION_OUTCOME_UNCERTAIN"
            || uncertainAction.FinalState != XiaoK.Core.TaskLifecycleState.OutcomeUncertain)
            throw new InvalidOperationException("按钮工作进程无响应后没有将可能已开始的动作标记为结果待核对。");
        await RequireProcessExitedAsync(actionTimeoutStarter.ProcessId);

        var actionCancellationStarter = new BlockingWorkerProcessStarter();
        var actionCancellationAdapter = new WindowsUiAutomationSnapshotAdapter(desktop, actionCancellationStarter);
        using var actionCancellation = new CancellationTokenSource();
        var actionCancellationOperation = actionCancellationAdapter.InvokeButtonAsync("uia-probe", AutomationId,
            actionCancellation.Token);
        await actionCancellationStarter.Started.WaitAsync(TimeSpan.FromSeconds(5));
        actionCancellation.Cancel();
        var cancelledAction = await actionCancellationOperation.WaitAsync(TimeSpan.FromSeconds(5));
        if (cancelledAction.Success || cancelledAction.ErrorCode != "UIA_ACTION_OUTCOME_UNCERTAIN"
            || cancelledAction.FinalState != XiaoK.Core.TaskLifecycleState.OutcomeUncertain)
            throw new InvalidOperationException("按钮调用开始后取消没有按结果待核对处理。");
        await RequireProcessExitedAsync(actionCancellationStarter.ProcessId);

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

    private sealed record ProbeWindow(IntPtr Handle, IntPtr WideHandle, IntPtr InvokeHandle, Dispatcher Dispatcher,
        Window Window, Window WideWindow, Window InvokeWindow, Button Button);
}
