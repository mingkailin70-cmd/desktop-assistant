using System.IO;
using XiaoK.Host;

namespace XiaoK.DesktopExperience.Checks;

internal static class PetWindowDragProbe
{
    public static IReadOnlyList<string> Run(string repositoryRoot)
    {
        var failures = new List<string>();
        VerifyCompletionPolicy(failures);
        VerifyDragMoveInputWiring(repositoryRoot, failures);
        return failures;
    }

    private static void VerifyCompletionPolicy(List<string> failures)
    {
        if (!PetWindowInteractionPolicy.ShouldStartMove(isLeftButton: true, isPressed: true,
                isOverResizeHandle: false, isInteractiveSource: false)
            || PetWindowInteractionPolicy.ShouldStartMove(isLeftButton: false, isPressed: true,
                isOverResizeHandle: false, isInteractiveSource: false)
            || PetWindowInteractionPolicy.ShouldStartMove(isLeftButton: true, isPressed: false,
                isOverResizeHandle: false, isInteractiveSource: false)
            || PetWindowInteractionPolicy.ShouldStartMove(isLeftButton: true, isPressed: true,
                isOverResizeHandle: true, isInteractiveSource: false)
            || PetWindowInteractionPolicy.ShouldStartMove(isLeftButton: true, isPressed: true,
                isOverResizeHandle: false, isInteractiveSource: true)
            || PetWindowInteractionPolicy.GetCompletion(positionChanged: false, cancelled: false, expandOnClick: true)
                != PetWindowMoveCompletion.Expand
            || PetWindowInteractionPolicy.GetCompletion(positionChanged: false, cancelled: false, expandOnClick: false)
                != PetWindowMoveCompletion.Ignore
            || PetWindowInteractionPolicy.GetCompletion(positionChanged: true, cancelled: false, expandOnClick: true)
                != PetWindowMoveCompletion.SavePosition
            || PetWindowInteractionPolicy.GetCompletion(positionChanged: true, cancelled: true, expandOnClick: true)
                != PetWindowMoveCompletion.SavePosition
            || PetWindowInteractionPolicy.GetCompletion(positionChanged: false, cancelled: true, expandOnClick: true)
                != PetWindowMoveCompletion.Ignore)
            failures.Add("拖动入口未排除非左键、释放状态、缩放把手和交互控件，或结束时没有区分单击、移动和取消。");
    }

    private static void VerifyDragMoveInputWiring(string repositoryRoot, List<string> failures)
    {
        var xamlPath = Path.Combine(repositoryRoot, "src", "XiaoK.Host", "MainWindow.xaml");
        var codePath = Path.Combine(repositoryRoot, "src", "XiaoK.Host", "MainWindow.xaml.cs");
        var xaml = File.ReadAllText(xamlPath);
        var code = File.ReadAllText(codePath);
        if (!xaml.Contains("PreviewMouseLeftButtonDown=\"Window_PreviewMouseLeftButtonDown\"", StringComparison.Ordinal)
            || !xaml.Contains("ShowActivated=\"False\"", StringComparison.Ordinal)
            || !xaml.Contains("Cursor=\"SizeAll\"", StringComparison.Ordinal)
            || !xaml.Contains("按住桌宠任意位置并移动鼠标", StringComparison.Ordinal)
            || !xaml.Contains("按住猫图或面板空白区域拖动可移动小K窗口", StringComparison.Ordinal)
            || !xaml.Contains("x:Name=\"PetResizeHandle\"", StringComparison.Ordinal))
            failures.Add("窗口级桌宠拖动入口、紧凑/展开态提示、启动时不激活或独立缩放手柄接线不完整。");

        if (!code.Contains("private void Window_PreviewMouseLeftButtonDown", StringComparison.Ordinal)
            || !code.Contains("PetWindowInteractionPolicy.ShouldStartMove(", StringComparison.Ordinal)
            || !code.Contains("MoveWindowFromPointer(e, expandOnClick: !_expanded)", StringComparison.Ordinal)
            || !code.Contains("IsInteractiveDragSource(e.OriginalSource)", StringComparison.Ordinal)
            || !code.Contains("System.Windows.Controls.Primitives.ButtonBase", StringComparison.Ordinal)
            || !code.Contains("System.Windows.Controls.Primitives.TextBoxBase", StringComparison.Ordinal)
            || !code.Contains("System.Windows.Controls.Selector", StringComparison.Ordinal)
            || !code.Contains("System.Windows.Controls.ItemsControl", StringComparison.Ordinal)
            || !code.Contains("DragMove();", StringComparison.Ordinal)
            || !code.Contains("_petDragGestureActive = true", StringComparison.Ordinal)
            || !code.Contains("PetWindowInteractionPolicy.GetCompletion(moved, cancelled, expandOnClick)", StringComparison.Ordinal)
            || !code.Contains("FitWindowToMonitorWorkArea();", StringComparison.Ordinal)
            || !code.Contains("SaveWindowPosition();", StringComparison.Ordinal)
            || !code.Contains("case PetWindowMoveCompletion.Expand:", StringComparison.Ordinal))
            failures.Add("窗口级拖动、交互控件排除、轻点展开、工作区约束或位置保存接线不完整。");

        var obsoleteNativeMove = new[]
        {
            "WmNcHitTest", "WmNcLButtonDown", "WmEnterSizeMove", "WmExitSizeMove",
            "WmMouseActivate", "MaNoActivate", "SetWindowsHookEx(",
            "GetAsyncKeyState(", "_petPointerPollTimer"
        };
        if (obsoleteNativeMove.Any(marker => code.Contains(marker, StringComparison.Ordinal)))
            failures.Add("桌宠拖动仍抑制用户明确点击激活，或依赖旧的自定义Win32命中/钩子路径。");
    }
}
