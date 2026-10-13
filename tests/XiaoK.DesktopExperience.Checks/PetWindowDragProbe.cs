using System.IO;

namespace XiaoK.DesktopExperience.Checks;

internal static class PetWindowDragProbe
{
    public static IReadOnlyList<string> Run(string repositoryRoot)
    {
        var failures = new List<string>();
        var xamlPath = Path.Combine(repositoryRoot, "src", "XiaoK.Host", "MainWindow.xaml");
        var codePath = Path.Combine(repositoryRoot, "src", "XiaoK.Host", "MainWindow.xaml.cs");
        var xaml = File.ReadAllText(xamlPath);
        var code = File.ReadAllText(codePath);
        var resizeHandleIndex = xaml.IndexOf("<Thumb x:Name=\"PetResizeHandle\"", StringComparison.Ordinal);
        if (!xaml.Contains("PreviewMouseLeftButtonDown=\"PetView_PreviewMouseLeftButtonDown\"", StringComparison.Ordinal)
            || xaml.Contains("PetDragSurface", StringComparison.Ordinal)
            || resizeHandleIndex < 0)
            failures.Add("桌宠左键预览事件没有覆盖宠物本体，或缩放手柄没有保留。");

        if (!code.Contains("msg == WmMouseActivate && !_expanded", StringComparison.Ordinal)
            || !code.Contains("return new IntPtr(MaNoActivate);", StringComparison.Ordinal)
            || !code.Contains("Topmost = !expanded;", StringComparison.Ordinal))
            failures.Add("紧凑桌宠没有保持浮在普通窗口上方且不激活用户窗口。");

        var dragStart = code.IndexOf("private void PetView_PreviewMouseLeftButtonDown", StringComparison.Ordinal);
        var dragEnd = code.IndexOf("private void PetView_PreviewMouseWheel", StringComparison.Ordinal);
        var dragBody = dragStart >= 0 && dragEnd > dragStart
            ? code[dragStart..dragEnd]
            : string.Empty;
        if (dragBody.Length == 0 || !dragBody.Contains("DragMove();", StringComparison.Ordinal)
            || !dragBody.Contains("PetResizeHandle.IsMouseOver", StringComparison.Ordinal)
            || !dragBody.Contains("RestoreFromTray();", StringComparison.Ordinal)
            || !dragBody.Contains("FitWindowToMonitorWorkArea();", StringComparison.Ordinal)
            || !dragBody.Contains("SaveWindowPosition();", StringComparison.Ordinal))
            failures.Add("原生窗口拖动、短按展开、缩放排除或拖动后位置保存没有接入。");

        return failures;
    }
}
