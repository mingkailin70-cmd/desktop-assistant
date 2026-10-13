using System.IO;

namespace XiaoK.DesktopExperience.Checks;

internal static class PetWindowDragProbe
{
    public static IReadOnlyList<string> Run(string repositoryRoot)
    {
        var failures = new List<string>();
        var origin = new XiaoK.Host.PetWindowDragOrigin(100, 100, -1920, 500);
        if (!origin.TryGetTargetPosition(-50, 175, out var targetLeft, out var targetTop)
            || targetLeft != -2070 || targetTop != 575)
            failures.Add("屏幕光标的跨屏负坐标位移没有映射到正确窗口位置。");

        var overflowOrigin = new XiaoK.Host.PetWindowDragOrigin(0, 0, int.MaxValue, int.MinValue);
        if (overflowOrigin.TryGetTargetPosition(1, -1, out _, out _))
            failures.Add("极端光标坐标溢出时仍允许设置桌宠位置。");

        var xamlPath = Path.Combine(repositoryRoot, "src", "XiaoK.Host", "MainWindow.xaml");
        var codePath = Path.Combine(repositoryRoot, "src", "XiaoK.Host", "MainWindow.xaml.cs");
        var xaml = File.ReadAllText(xamlPath);
        var code = File.ReadAllText(codePath);
        var dragSurfaceIndex = xaml.IndexOf("<Thumb x:Name=\"PetDragSurface\"", StringComparison.Ordinal);
        var resizeHandleIndex = xaml.IndexOf("<Thumb x:Name=\"PetResizeHandle\"", StringComparison.Ordinal);
        var visualEndIndex = dragSurfaceIndex >= 0
            ? xaml.LastIndexOf("</Viewbox>", dragSurfaceIndex, StringComparison.Ordinal)
            : -1;
        if (dragSurfaceIndex < 0 || resizeHandleIndex <= dragSurfaceIndex || visualEndIndex < 0
            || !xaml.Contains("Background=\"Transparent\" Focusable=\"False\" Cursor=\"SizeAll\"", StringComparison.Ordinal)
            || !xaml.Contains("ContextMenu=\"{Binding ContextMenu, ElementName=PetView}\"", StringComparison.Ordinal)
            || !xaml.Contains("<Border Background=\"{TemplateBinding Background}\"/>", StringComparison.Ordinal))
            failures.Add("宠物没有覆盖角色区域且位于缩放手柄下方的透明专用拖动层。");

        if (!xaml.Contains("PreviewMouseLeftButtonDown=\"PetDragSurface_PreviewMouseLeftButtonDown\"", StringComparison.Ordinal)
            || !xaml.Contains("PreviewMouseLeftButtonUp=\"PetDragSurface_PreviewMouseLeftButtonUp\"", StringComparison.Ordinal)
            || !xaml.Contains("DragStarted=\"PetDragSurface_DragStarted\"", StringComparison.Ordinal)
            || !xaml.Contains("DragDelta=\"PetDragSurface_DragDelta\"", StringComparison.Ordinal)
            || !xaml.Contains("DragCompleted=\"PetDragSurface_DragCompleted\"", StringComparison.Ordinal)
            || xaml.Contains("PreviewMouseLeftButtonDown=\"PetView_PreviewMouseLeftButtonDown\"", StringComparison.Ordinal)
            || xaml.Contains("PreviewMouseMove=\"PetView_PreviewMouseMove\"", StringComparison.Ordinal))
            failures.Add("专用拖动层的按下、拖动、抬起事件没有完整接线，或旧容器捕获仍在使用。");

        if (!code.Contains("msg == WmMouseActivate && !_expanded", StringComparison.Ordinal)
            || !code.Contains("return new IntPtr(MaNoActivate);", StringComparison.Ordinal)
            || !code.Contains("SwpNoSize | SwpNoZOrder | SwpNoActivate", StringComparison.Ordinal))
            failures.Add("桌宠拖动没有保持窗口不激活策略。");

        var dragDeltaStart = code.IndexOf("private void PetDragSurface_DragDelta", StringComparison.Ordinal);
        var dragCompletedStart = code.IndexOf("private void PetDragSurface_DragCompleted", StringComparison.Ordinal);
        var dragDeltaBody = dragDeltaStart >= 0 && dragCompletedStart > dragDeltaStart
            ? code[dragDeltaStart..dragCompletedStart]
            : string.Empty;
        if (dragDeltaBody.Length == 0 || !dragDeltaBody.Contains("MovePetWindowWithPointer()", StringComparison.Ordinal)
            || !dragDeltaBody.Contains("e.Handled = true", StringComparison.Ordinal))
            failures.Add("WPF Thumb 的系统拖动增量没有驱动屏幕坐标窗口移动。");

        var clickUpStart = code.IndexOf("private void PetDragSurface_PreviewMouseLeftButtonUp", StringComparison.Ordinal);
        var moveStart = code.IndexOf("private bool MovePetWindowWithPointer", StringComparison.Ordinal);
        var clickUpBody = clickUpStart >= 0 && moveStart > clickUpStart ? code[clickUpStart..moveStart] : string.Empty;
        if (clickUpBody.Length == 0 || !clickUpBody.Contains("Dispatcher.BeginInvoke", StringComparison.Ordinal)
            || !clickUpBody.Contains("RestoreFromTray();", StringComparison.Ordinal)
            || !code.Contains("FitWindowToMonitorWorkArea();\n        SaveWindowPosition();", StringComparison.Ordinal))
            failures.Add("短按展开、拖动结束约束和坐标保存路径没有接入。");

        return failures;
    }
}
