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
        if (PetWindowInteractionPolicy.GetCompletion(positionChanged: false, cancelled: false, expandOnClick: true)
                != PetWindowMoveCompletion.Expand
            || PetWindowInteractionPolicy.GetCompletion(positionChanged: false, cancelled: false, expandOnClick: false)
                != PetWindowMoveCompletion.Ignore
            || PetWindowInteractionPolicy.GetCompletion(positionChanged: true, cancelled: false, expandOnClick: true)
                != PetWindowMoveCompletion.SavePosition
            || PetWindowInteractionPolicy.GetCompletion(positionChanged: true, cancelled: true, expandOnClick: true)
                != PetWindowMoveCompletion.SavePosition
            || PetWindowInteractionPolicy.GetCompletion(positionChanged: false, cancelled: true, expandOnClick: true)
                != PetWindowMoveCompletion.Ignore)
            failures.Add("拖动结束后没有按紧凑态/展开态区分单击、移动和取消。");
    }

    private static void VerifyDragMoveInputWiring(string repositoryRoot, List<string> failures)
    {
        var xamlPath = Path.Combine(repositoryRoot, "src", "XiaoK.Host", "MainWindow.xaml");
        var codePath = Path.Combine(repositoryRoot, "src", "XiaoK.Host", "MainWindow.xaml.cs");
        var xaml = File.ReadAllText(xamlPath);
        var code = File.ReadAllText(codePath);
        if (!xaml.Contains("PreviewMouseLeftButtonDown=\"PetView_PreviewMouseLeftButtonDown\"", StringComparison.Ordinal)
            || !xaml.Contains("PreviewMouseLeftButtonDown=\"ExpandedPetImage_PreviewMouseLeftButtonDown\"", StringComparison.Ordinal)
            || !xaml.Contains("ShowActivated=\"False\"", StringComparison.Ordinal)
            || !xaml.Contains("Cursor=\"SizeAll\"", StringComparison.Ordinal)
            || !xaml.Contains("AutomationProperties.HelpText=\"按住猫图拖动可移动小K窗口\"", StringComparison.Ordinal)
            || !xaml.Contains("x:Name=\"PetResizeHandle\"", StringComparison.Ordinal))
            failures.Add("紧凑桌宠/展开猫图拖动入口、提示、启动时不激活或独立缩放手柄接线不完整。");

        if (!code.Contains("private void PetView_PreviewMouseLeftButtonDown", StringComparison.Ordinal)
            || !code.Contains("private void ExpandedPetImage_PreviewMouseLeftButtonDown", StringComparison.Ordinal)
            || !code.Contains("MoveWindowFromPointer(e, expandOnClick: true)", StringComparison.Ordinal)
            || !code.Contains("MoveWindowFromPointer(e, expandOnClick: false)", StringComparison.Ordinal)
            || !code.Contains("DragMove();", StringComparison.Ordinal)
            || !code.Contains("PetResizeHandle.IsMouseOver", StringComparison.Ordinal)
            || !code.Contains("_petDragGestureActive = true", StringComparison.Ordinal)
            || !code.Contains("PetWindowInteractionPolicy.GetCompletion(moved, cancelled, expandOnClick)", StringComparison.Ordinal)
            || !code.Contains("FitWindowToMonitorWorkArea();", StringComparison.Ordinal)
            || !code.Contains("SaveWindowPosition();", StringComparison.Ordinal)
            || !code.Contains("case PetWindowMoveCompletion.Expand:", StringComparison.Ordinal))
            failures.Add("紧凑态任意位置/展开态猫图拖动、缩放排除、轻点展开或位置保存接线不完整。");

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
