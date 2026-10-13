using System.IO;
using XiaoK.Host;

namespace XiaoK.DesktopExperience.Checks;

internal static class PetWindowDragProbe
{
    public static IReadOnlyList<string> Run(string repositoryRoot)
    {
        var failures = VerifyHitTestPolicy();
        VerifyCompletionPolicy(failures);
        VerifyNativeWindowInputWiring(repositoryRoot, failures);
        return failures;
    }

    private static List<string> VerifyHitTestPolicy()
    {
        var failures = new List<string>();
        if (PetWindowInteractionPolicy.GetHitTest(compact: true, resizeHandle: false)
                != PetWindowInteractionPolicy.HtCaption
            || PetWindowInteractionPolicy.GetHitTest(compact: true, resizeHandle: true)
                != PetWindowInteractionPolicy.HtClient
            || PetWindowInteractionPolicy.GetHitTest(compact: false, resizeHandle: false)
                != PetWindowInteractionPolicy.HtClient)
            failures.Add("紧凑桌宠没有把本体交给系统窗口拖动，或缩放手柄未保留客户端输入。");
        return failures;
    }

    private static void VerifyCompletionPolicy(List<string> failures)
    {
        if (PetWindowInteractionPolicy.GetCompletion(positionChanged: false, cancelled: false)
                != PetWindowMoveCompletion.Expand
            || PetWindowInteractionPolicy.GetCompletion(positionChanged: true, cancelled: false)
                != PetWindowMoveCompletion.SavePosition
            || PetWindowInteractionPolicy.GetCompletion(positionChanged: true, cancelled: true)
                != PetWindowMoveCompletion.SavePosition
            || PetWindowInteractionPolicy.GetCompletion(positionChanged: false, cancelled: true)
                != PetWindowMoveCompletion.Ignore)
            failures.Add("系统移动结束后没有正确区分单击、移动和取消。");
    }

    private static void VerifyNativeWindowInputWiring(string repositoryRoot, List<string> failures)
    {
        var xamlPath = Path.Combine(repositoryRoot, "src", "XiaoK.Host", "MainWindow.xaml");
        var codePath = Path.Combine(repositoryRoot, "src", "XiaoK.Host", "MainWindow.xaml.cs");
        var xaml = File.ReadAllText(xamlPath);
        var code = File.ReadAllText(codePath);
        if (xaml.Contains("PreviewMouseLeftButtonDown=\"PetView_PreviewMouseLeftButtonDown\"", StringComparison.Ordinal)
            || xaml.Contains("PreviewMouseWheel=\"PetView_PreviewMouseWheel\"", StringComparison.Ordinal)
            || !xaml.Contains("Cursor=\"SizeAll\"", StringComparison.Ordinal)
            || !xaml.Contains("x:Name=\"PetResizeHandle\"", StringComparison.Ordinal))
            failures.Add("桌宠没有使用统一的系统拖动区域，或自定义缩放手柄已丢失。");

        if (!code.Contains("msg == WmNcHitTest && !_expanded", StringComparison.Ordinal)
            || !code.Contains("PetWindowInteractionPolicy.GetHitTest", StringComparison.Ordinal)
            || !code.Contains("msg == WmSetCursor", StringComparison.Ordinal)
            || !code.Contains("SetCursor(moveCursor)", StringComparison.Ordinal)
            || !code.Contains("msg == WmEnterSizeMove", StringComparison.Ordinal)
            || !code.Contains("msg == WmExitSizeMove", StringComparison.Ordinal)
            || !code.Contains("msg == WmMouseActivate && !_expanded", StringComparison.Ordinal)
            || !code.Contains("return new IntPtr(MaNoActivate)", StringComparison.Ordinal)
            || !code.Contains("CompletePetNativeMove(start, cancelled)", StringComparison.Ordinal)
            || !code.Contains("FitWindowToMonitorWorkArea();", StringComparison.Ordinal)
            || !code.Contains("SaveWindowPosition();", StringComparison.Ordinal)
            || !code.Contains("new Action(RestoreFromTray)", StringComparison.Ordinal)
            || !code.Contains("new Action(OpenPetContextMenu)", StringComparison.Ordinal))
            failures.Add("原生移动循环、非激活点击、轻点展开、右键菜单或位置保存接线不完整。");

        var obsoleteInput = new[]
        {
            "SetWindowsHookEx(", "UnhookWindowsHookEx(", "DispatcherTimer",
            "GetAsyncKeyState(", "_petPointerPollTimer", "PetWindowDragOrigin",
            "PetWindowDragGesture"
        };
        if (obsoleteInput.Any(marker => code.Contains(marker, StringComparison.Ordinal)))
            failures.Add("桌宠仍依赖全局鼠标钩子、轮询或手工坐标拖动链路。");
    }
}
