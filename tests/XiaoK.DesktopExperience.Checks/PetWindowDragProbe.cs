using System.IO;
using XiaoK.Host;

namespace XiaoK.DesktopExperience.Checks;

internal static class PetWindowDragProbe
{
    public static IReadOnlyList<string> Run(string repositoryRoot)
    {
        var failures = VerifyGestureState();
        VerifyCoordinateMapping(failures);
        VerifyMouseButtonPolling(failures);
        VerifyWindowInputWiring(repositoryRoot, failures);
        return failures;
    }

    private static List<string> VerifyGestureState()
    {
        var failures = new List<string>();
        var gesture = new PetWindowDragGesture();

        gesture.PointerDown(100, 100);
        if (gesture.TryStartDrag(103, 102, leftButtonPressed: true, 4, 4))
            failures.Add("未达到系统拖动阈值的轻点被误判为桌宠移动。");
        if (!gesture.TryStartDrag(104, 100, leftButtonPressed: true, 4, 4)
            || !gesture.IsDragging)
            failures.Add("横向越过系统拖动阈值后没有开始移动。");
        if (gesture.TryStartDrag(120, 100, leftButtonPressed: true, 4, 4))
            failures.Add("同一次按住手势重复开始了桌宠移动。");
        if (!gesture.PointerUp() || gesture.PointerUp() || gesture.IsDragging)
            failures.Add("拖动状态没有在鼠标抬起时准确重置。");

        gesture.PointerDown(50, 50);
        if (!gesture.TryStartDrag(50, 54, leftButtonPressed: true, 4, 4)
            || !gesture.PointerUp())
            failures.Add("纵向达到系统拖动阈值后没有开始并结束移动。");

        gesture.PointerDown(10, 10);
        if (gesture.TryStartDrag(30, 30, leftButtonPressed: false, 4, 4)
            || gesture.PointerUp())
            failures.Add("鼠标已释放时仍把手势保留为拖动。");

        var boundaries = new[]
        {
            (DeltaX: 3.99, DeltaY: 0d, Expected: false),
            (DeltaX: 4d, DeltaY: 0d, Expected: true),
            (DeltaX: -4d, DeltaY: 0d, Expected: true),
            (DeltaX: 0d, DeltaY: -4d, Expected: true),
            (DeltaX: 3.99, DeltaY: 3.99, Expected: false),
            (DeltaX: 3.99, DeltaY: -4d, Expected: true)
        };
        foreach (var boundary in boundaries)
        {
            gesture.PointerDown(20, 30);
            var started = gesture.TryStartDrag(20 + (int)boundary.DeltaX,
                30 + (int)boundary.DeltaY, leftButtonPressed: true, 4, 4);
            var ended = gesture.PointerUp();
            if (started != boundary.Expected || ended != boundary.Expected)
                failures.Add("桌宠拖动阈值边界或抬起后的状态不正确。");
        }

        var invalidThresholds = new[] { 0d, -4d, double.NaN, double.PositiveInfinity };
        foreach (var invalid in invalidThresholds)
        {
            gesture.PointerDown(0, 0);
            if (gesture.TryStartDrag(3, 0, leftButtonPressed: true, invalid, 4)
                || !gesture.TryStartDrag(4, 0, leftButtonPressed: true, invalid, 4)
                || !gesture.PointerUp())
                failures.Add("无效系统拖动阈值没有安全回退。");
        }

        return failures;
    }

    private static void VerifyCoordinateMapping(List<string> failures)
    {
        var origin = new PetWindowDragOrigin(100, 100, -1920, 500);
        if (!origin.TryGetTargetPosition(-50, 175, out var targetLeft, out var targetTop)
            || targetLeft != -2070 || targetTop != 575)
            failures.Add("桌宠跨屏负坐标移动没有按屏幕光标位移计算。");

        var overflow = new PetWindowDragOrigin(0, 0, int.MaxValue, int.MinValue);
        if (overflow.TryGetTargetPosition(1, -1, out _, out _))
            failures.Add("极端坐标溢出时仍允许移动桌宠窗口。");
    }

    private static void VerifyMouseButtonPolling(List<string> failures)
    {
        if (!PetWindowDragGesture.IsMouseButtonPressed(unchecked((short)0x8000))
            || !PetWindowDragGesture.IsMouseButtonPressed(-1))
            failures.Add("桌宠指针轮询没有识别鼠标左键的高位按下状态。");
        if (PetWindowDragGesture.IsMouseButtonPressed(0)
            || PetWindowDragGesture.IsMouseButtonPressed(1))
            failures.Add("桌宠指针轮询把鼠标左键的低位历史标记误判为仍按下。");
    }

    private static void VerifyWindowInputWiring(string repositoryRoot, List<string> failures)
    {
        var xamlPath = Path.Combine(repositoryRoot, "src", "XiaoK.Host", "MainWindow.xaml");
        var codePath = Path.Combine(repositoryRoot, "src", "XiaoK.Host", "MainWindow.xaml.cs");
        var xaml = File.ReadAllText(xamlPath);
        var code = File.ReadAllText(codePath);
        if (!xaml.Contains("PreviewMouseLeftButtonDown=\"PetView_PreviewMouseLeftButtonDown\"", StringComparison.Ordinal)
            || xaml.Contains("PreviewMouseMove=\"PetView_PreviewMouseMove\"", StringComparison.Ordinal)
            || xaml.Contains("PreviewMouseLeftButtonUp=\"PetView_PreviewMouseLeftButtonUp\"", StringComparison.Ordinal)
            || xaml.Contains("PetDragSurface", StringComparison.Ordinal))
            failures.Add("桌宠按下事件未接到本体，或仍依赖WPF逐帧鼠标路由移动。");

        var dragStart = code.IndexOf("private void PetView_PreviewMouseLeftButtonDown", StringComparison.Ordinal);
        var dragEnd = code.IndexOf("private void PetView_PreviewMouseWheel", StringComparison.Ordinal);
        var dragCode = dragStart >= 0 && dragEnd > dragStart ? code[dragStart..dragEnd] : string.Empty;
        if (dragCode.Length == 0
            || !dragCode.Contains("_petPointerPollTimer.Start()", StringComparison.Ordinal)
            || !dragCode.Contains("GetCursorPos(out var cursor)", StringComparison.Ordinal)
            || !dragCode.Contains("PetResizeHandle.IsMouseOver", StringComparison.Ordinal)
            || dragCode.Contains("SetCapture(", StringComparison.Ordinal)
            || dragCode.Contains("Mouse.Capture(", StringComparison.Ordinal)
            || dragCode.Contains("DragMove(", StringComparison.Ordinal))
            failures.Add("桌宠没有在本体按下时启动轻量指针轮询，或事件区错误覆盖缩放手柄。");

        var pollStart = code.IndexOf("private void PetPointerPollTimer_Tick", StringComparison.Ordinal);
        var pollEnd = code.IndexOf("private void TrackPetPointerMove", StringComparison.Ordinal);
        var pollCode = pollStart >= 0 && pollEnd > pollStart ? code[pollStart..pollEnd] : string.Empty;
        if (!pollCode.Contains("GetAsyncKeyState(VkLeftButton)", StringComparison.Ordinal)
            || !pollCode.Contains("GetCursorPos(out var cursor)", StringComparison.Ordinal)
            || !pollCode.Contains("TrackPetPointerMove(cursor.X, cursor.Y)", StringComparison.Ordinal)
            || !pollCode.Contains("FinishPetPointerGesture(expandOnClick: true)", StringComparison.Ordinal)
            || !code.Contains("_petPointerPollTimer.Stop();", StringComparison.Ordinal)
            || code.Contains("SetWindowsHookEx(", StringComparison.Ordinal)
            || code.Contains("CallNextHookEx(", StringComparison.Ordinal)
            || code.Contains("SetCapture(", StringComparison.Ordinal)
            || code.Contains("GetCapture()", StringComparison.Ordinal))
            failures.Add("桌宠按住期间的指针轮询、松开收尾或定时器清理不完整，或仍注册系统级鼠标钩子。");

        var windowHookStart = code.IndexOf("private IntPtr HotkeyHook", StringComparison.Ordinal);
        var windowHookEnd = code.IndexOf("private void RestoreFromTray", StringComparison.Ordinal);
        var windowHook = windowHookStart >= 0 && windowHookEnd > windowHookStart
            ? code[windowHookStart..windowHookEnd] : string.Empty;
        if (!windowHook.Contains("msg == WmCancelMode", StringComparison.Ordinal))
            failures.Add("桌宠收到窗口取消输入消息时没有结束拖动。");

        var finishStart = code.IndexOf("private void FinishPetPointerGesture", StringComparison.Ordinal);
        var finishEnd = code.IndexOf("private void ReportPetWindowDragUnavailable", StringComparison.Ordinal);
        var finishCode = finishStart >= 0 && finishEnd > finishStart
            ? code[finishStart..finishEnd] : string.Empty;
        if (!finishCode.Contains("_petPointerPollTimer.Stop();", StringComparison.Ordinal)
            || !finishCode.Contains("FitWindowToMonitorWorkArea();", StringComparison.Ordinal)
            || !finishCode.Contains("SaveWindowPosition();", StringComparison.Ordinal)
            || !finishCode.Contains("if (expandOnClick) RestoreFromTray();", StringComparison.Ordinal)
            || !code.Contains("Interval = TimeSpan.FromMilliseconds(16)", StringComparison.Ordinal))
            failures.Add("桌宠释放时没有停止轮询、限制工作区、保存位置或保留轻点展开行为。");

        if (!windowHook.Contains("if (!_petPointerGestureActive)", StringComparison.Ordinal)
            || !windowHook.Contains("new Action(FitWindowToMonitorWorkArea)", StringComparison.Ordinal))
            failures.Add("拖动跨屏触发DPI变化时可能被后台工作区校正打断。");
    }
}
