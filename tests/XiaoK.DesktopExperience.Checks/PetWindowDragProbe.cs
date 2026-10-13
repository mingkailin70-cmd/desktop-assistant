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

    private static void VerifyDragMoveInputWiring(string repositoryRoot, List<string> failures)
    {
        var xamlPath = Path.Combine(repositoryRoot, "src", "XiaoK.Host", "MainWindow.xaml");
        var codePath = Path.Combine(repositoryRoot, "src", "XiaoK.Host", "MainWindow.xaml.cs");
        var xaml = File.ReadAllText(xamlPath);
        var code = File.ReadAllText(codePath);
        if (!xaml.Contains("PreviewMouseLeftButtonDown=\"PetView_PreviewMouseLeftButtonDown\"", StringComparison.Ordinal)
            || !xaml.Contains("ShowActivated=\"False\"", StringComparison.Ordinal)
            || !xaml.Contains("Cursor=\"SizeAll\"", StringComparison.Ordinal)
            || !xaml.Contains("x:Name=\"PetResizeHandle\"", StringComparison.Ordinal))
            failures.Add("桌宠拖动入口、启动时不激活或独立缩放手柄接线不完整。");

        if (!code.Contains("private void PetView_PreviewMouseLeftButtonDown", StringComparison.Ordinal)
            || !code.Contains("DragMove();", StringComparison.Ordinal)
            || !code.Contains("PetResizeHandle.IsMouseOver", StringComparison.Ordinal)
            || !code.Contains("_petDragGestureActive = true", StringComparison.Ordinal)
            || !code.Contains("PetWindowInteractionPolicy.GetCompletion(moved, cancelled)", StringComparison.Ordinal)
            || !code.Contains("FitWindowToMonitorWorkArea();", StringComparison.Ordinal)
            || !code.Contains("SaveWindowPosition();", StringComparison.Ordinal)
            || !code.Contains("case PetWindowMoveCompletion.Expand:", StringComparison.Ordinal))
            failures.Add("任意位置拖动、缩放排除、短按展开或位置保存接线不完整。");

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
