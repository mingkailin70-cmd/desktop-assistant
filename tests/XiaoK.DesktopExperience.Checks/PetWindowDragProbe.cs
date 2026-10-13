using System.IO;
using XiaoK.Host;

namespace XiaoK.DesktopExperience.Checks;

internal static class PetWindowDragProbe
{
    public static IReadOnlyList<string> Run(string repositoryRoot)
    {
        var failures = VerifyGestureState();
        VerifyCoordinateMapping(failures);
        VerifyGlobalMouseMessageDecoding(failures);
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

    private static void VerifyGlobalMouseMessageDecoding(List<string> failures)
    {
        if (!PetGlobalMouseTracker.TryDecodeMouseMessage(0x0200, -1920, 175, out var move)
            || move != new PetGlobalMouseEvent(PetGlobalMouseEventKind.PointerMoved, -1920, 175))
            failures.Add("桌宠全局鼠标跟踪没有保留跨屏物理坐标。");

        if (!PetGlobalMouseTracker.TryDecodeMouseMessage(0x0202, 2560, 1080, out var release)
            || release != new PetGlobalMouseEvent(PetGlobalMouseEventKind.LeftButtonReleased, 2560, 1080))
            failures.Add("桌宠全局鼠标跟踪没有识别左键释放坐标。");

        if (PetGlobalMouseTracker.TryDecodeMouseMessage(0x020A, 100, 100, out _))
            failures.Add("桌宠拖动钩子错误地把滚轮消息当作移动或释放。");
    }

    private static void VerifyWindowInputWiring(string repositoryRoot, List<string> failures)
    {
        var xamlPath = Path.Combine(repositoryRoot, "src", "XiaoK.Host", "MainWindow.xaml");
        var codePath = Path.Combine(repositoryRoot, "src", "XiaoK.Host", "MainWindow.xaml.cs");
        var trackerPath = Path.Combine(repositoryRoot, "src", "XiaoK.Host", "PetGlobalMouseTracker.cs");
        var xaml = File.ReadAllText(xamlPath);
        var code = File.ReadAllText(codePath);
        var tracker = File.ReadAllText(trackerPath);
        if (!xaml.Contains("PreviewMouseLeftButtonDown=\"PetView_PreviewMouseLeftButtonDown\"", StringComparison.Ordinal)
            || xaml.Contains("PreviewMouseMove=\"PetView_PreviewMouseMove\"", StringComparison.Ordinal)
            || xaml.Contains("PreviewMouseLeftButtonUp=\"PetView_PreviewMouseLeftButtonUp\"", StringComparison.Ordinal)
            || xaml.Contains("PetDragSurface", StringComparison.Ordinal))
            failures.Add("桌宠按下事件未接到本体，或仍依赖WPF逐帧鼠标路由移动。");

        var dragStart = code.IndexOf("private void PetView_PreviewMouseLeftButtonDown", StringComparison.Ordinal);
        var dragEnd = code.IndexOf("private void PetView_PreviewMouseWheel", StringComparison.Ordinal);
        var dragCode = dragStart >= 0 && dragEnd > dragStart ? code[dragStart..dragEnd] : string.Empty;
        if (dragCode.Length == 0
            || !dragCode.Contains("_petGlobalMouseTracker.TryStart()", StringComparison.Ordinal)
            || !dragCode.Contains("GetCursorPos(out var cursor)", StringComparison.Ordinal)
            || !dragCode.Contains("SetWindowPos(handle, IntPtr.Zero, left, top", StringComparison.Ordinal)
            || !dragCode.Contains("PetResizeHandle.IsMouseOver", StringComparison.Ordinal)
            || !dragCode.Contains("FitWindowToMonitorWorkArea();", StringComparison.Ordinal)
            || !dragCode.Contains("SaveWindowPosition();", StringComparison.Ordinal)
            || dragCode.Contains("SetCapture(", StringComparison.Ordinal)
            || dragCode.Contains("Mouse.Capture(", StringComparison.Ordinal)
            || dragCode.Contains("DragMove(", StringComparison.Ordinal))
            failures.Add("桌宠没有建立按需的全局指针跟踪并按屏幕坐标移动，或未保留轻点/缩放/位置保存行为。");

        var hookStart = tracker.IndexOf("private IntPtr HookProcedure", StringComparison.Ordinal);
        var hookEnd = tracker.IndexOf("private void ReleaseHook", StringComparison.Ordinal);
        var hook = hookStart >= 0 && hookEnd > hookStart ? tracker[hookStart..hookEnd] : string.Empty;
        if (!tracker.Contains("SetWindowsHookEx(WhMouseLl", StringComparison.Ordinal)
            || !tracker.Contains("UnhookWindowsHookEx(hookHandle)", StringComparison.Ordinal)
            || !hook.Contains("CallNextHookEx(hookHandle, code, wParam, lParam)", StringComparison.Ordinal)
            || !hook.Contains("TryDecodeMouseMessage(message", StringComparison.Ordinal)
            || !hook.Contains("LeftButtonReleased?.Invoke", StringComparison.Ordinal)
            || hook.Contains("SetWindowPos(", StringComparison.Ordinal)
            || hook.Contains("SaveWindowPosition(", StringComparison.Ordinal)
            || !code.Contains("DispatcherPriority.Input", StringComparison.Ordinal)
            || !code.Contains("_petPointerDispatchScheduled", StringComparison.Ordinal)
            || !code.Contains("_petGlobalMouseTracker.Stop();", StringComparison.Ordinal)
            || code.Contains("SetCapture(", StringComparison.Ordinal)
            || code.Contains("GetCapture()", StringComparison.Ordinal))
            failures.Add("桌宠拖动没有使用短时全局鼠标跟踪、转发其他应用输入或在结束时释放钩子。");

        var windowHookStart = code.IndexOf("private IntPtr HotkeyHook", StringComparison.Ordinal);
        var windowHookEnd = code.IndexOf("private void RestoreFromTray", StringComparison.Ordinal);
        var windowHook = windowHookStart >= 0 && windowHookEnd > windowHookStart
            ? code[windowHookStart..windowHookEnd] : string.Empty;
        if (!windowHook.Contains("msg == WmCancelMode", StringComparison.Ordinal))
            failures.Add("桌宠收到窗口取消输入消息时没有结束拖动。");

        var releaseStart = code.IndexOf("private void OnPetGlobalLeftButtonReleased", StringComparison.Ordinal);
        var releaseEnd = code.IndexOf("private void QueuePetPointerUpdate", StringComparison.Ordinal);
        var releaseHandler = releaseStart >= 0 && releaseEnd > releaseStart
            ? code[releaseStart..releaseEnd] : string.Empty;
        if (!releaseHandler.Contains("TrackPetPointerMove(x, y);", StringComparison.Ordinal)
            || !releaseHandler.Contains("QueuePetPointerUpdate(x, y, released: true);", StringComparison.Ordinal))
            failures.Add("桌宠释放鼠标前没有先将窗口对齐到释放点，可能把抬起事件交给后方窗口。");

        if (!windowHook.Contains("if (!_petPointerGestureActive)", StringComparison.Ordinal)
            || !windowHook.Contains("new Action(FitWindowToMonitorWorkArea)", StringComparison.Ordinal))
            failures.Add("拖动跨屏触发DPI变化时可能被后台工作区校正打断。");
    }
}
