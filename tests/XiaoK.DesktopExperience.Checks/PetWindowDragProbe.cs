using System.IO;
using XiaoK.Host;

namespace XiaoK.DesktopExperience.Checks;

internal static class PetWindowDragProbe
{
    public static IReadOnlyList<string> Run(string repositoryRoot)
    {
        var failures = new List<string>();
        var gesture = new PetWindowDragGesture();

        gesture.PointerDown(100, 100);
        if (gesture.TryStartDrag(103, 102, leftButtonPressed: true,
                minimumHorizontalDistance: 4, minimumVerticalDistance: 4))
            failures.Add("未达到系统拖动阈值的轻点被误判为桌宠移动。");
        if (!gesture.TryStartDrag(104.1, 100, leftButtonPressed: true,
                minimumHorizontalDistance: 4, minimumVerticalDistance: 4))
            failures.Add("横向越过系统拖动阈值后没有开始移动。");
        if (gesture.TryStartDrag(120, 100, leftButtonPressed: true,
                minimumHorizontalDistance: 4, minimumVerticalDistance: 4))
            failures.Add("同一次按住手势重复开始了桌宠移动。");
        if (!gesture.PointerUp() || gesture.PointerUp())
            failures.Add("拖动结束状态没有在鼠标抬起时准确重置。");

        gesture.PointerDown(50, 50);
        if (!gesture.TryStartDrag(50, 55, leftButtonPressed: true,
                minimumHorizontalDistance: 4, minimumVerticalDistance: 4) || !gesture.PointerUp())
            failures.Add("纵向越过系统拖动阈值后没有开始并结束移动。");

        gesture.PointerDown(10, 10);
        if (gesture.TryStartDrag(30, 30, leftButtonPressed: false,
                minimumHorizontalDistance: 4, minimumVerticalDistance: 4) || gesture.PointerUp())
            failures.Add("鼠标已释放时仍把手势保留为拖动。");

        gesture.PointerDown(double.NaN, 0);
        if (gesture.TryStartDrag(50, 50, leftButtonPressed: true,
                minimumHorizontalDistance: 4, minimumVerticalDistance: 4))
            failures.Add("无效按下位置仍可启动拖动。");

        var xamlPath = Path.Combine(repositoryRoot, "src", "XiaoK.Host", "MainWindow.xaml");
        var codePath = Path.Combine(repositoryRoot, "src", "XiaoK.Host", "MainWindow.xaml.cs");
        var xaml = File.ReadAllText(xamlPath);
        var code = File.ReadAllText(codePath);
        if (!xaml.Contains("PreviewMouseLeftButtonDown=\"PetView_PreviewMouseLeftButtonDown\"", StringComparison.Ordinal)
            || !xaml.Contains("PreviewMouseMove=\"PetView_PreviewMouseMove\"", StringComparison.Ordinal)
            || !xaml.Contains("PreviewMouseLeftButtonUp=\"PetView_PreviewMouseLeftButtonUp\"", StringComparison.Ordinal)
            || xaml.Contains("MouseLeftButtonDown=\"PetView_MouseLeftButtonDown\"", StringComparison.Ordinal))
            failures.Add("宠物拖动没有接在按钮处理之前的预览事件上，或旧冒泡事件仍在使用。");
        if (!code.Contains("SystemParameters.MinimumHorizontalDragDistance", StringComparison.Ordinal)
            || !code.Contains("SystemParameters.MinimumVerticalDragDistance", StringComparison.Ordinal)
            || !code.Contains("if (_petWindowDragGesture.PointerUp()) e.Handled = true;", StringComparison.Ordinal)
            || !code.Contains("PetResizeHandle.IsMouseOver", StringComparison.Ordinal))
            failures.Add("拖动阈值、轻点保留或缩放手柄排除逻辑没有接入窗口事件。");

        return failures;
    }
}
