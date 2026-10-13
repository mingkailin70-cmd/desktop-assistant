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

        var boundaryCases = new[]
        {
            (Name: "横向正方向阈值内", DeltaX: 3.999, DeltaY: 0d, Expected: false),
            (Name: "横向正方向恰好达到阈值", DeltaX: 4d, DeltaY: 0d, Expected: true),
            (Name: "横向负方向越过阈值", DeltaX: -4.001, DeltaY: 0d, Expected: true),
            (Name: "纵向正方向恰好达到阈值", DeltaX: 0d, DeltaY: 4d, Expected: true),
            (Name: "纵向负方向恰好达到阈值", DeltaX: 0d, DeltaY: -4d, Expected: true),
            (Name: "两个方向都在阈值内", DeltaX: -3.999, DeltaY: 3.999, Expected: false),
            (Name: "单轴达到阈值即可拖动", DeltaX: 3.999, DeltaY: -4d, Expected: true)
        };
        foreach (var boundaryCase in boundaryCases)
        {
            gesture.PointerDown(20, 30);
            var started = gesture.TryStartDrag(20 + boundaryCase.DeltaX, 30 + boundaryCase.DeltaY,
                leftButtonPressed: true, minimumHorizontalDistance: 4, minimumVerticalDistance: 4);
            var ended = gesture.PointerUp();
            if (started != boundaryCase.Expected || ended != boundaryCase.Expected)
                failures.Add($"{boundaryCase.Name}的拖动启动或抬起状态不正确。");
            if (gesture.TryStartDrag(30, 40, leftButtonPressed: true,
                    minimumHorizontalDistance: 4, minimumVerticalDistance: 4))
                failures.Add($"{boundaryCase.Name}在抬起后仍保留了拖动状态。");
        }

        var invalidThresholds = new[] { 0d, -4d, double.NaN, double.PositiveInfinity, double.NegativeInfinity };
        foreach (var invalidThreshold in invalidThresholds)
        {
            gesture.PointerDown(0, 0);
            if (gesture.TryStartDrag(3.999, 0, leftButtonPressed: true,
                    minimumHorizontalDistance: invalidThreshold, minimumVerticalDistance: 4)
                || !gesture.TryStartDrag(4, 0, leftButtonPressed: true,
                    minimumHorizontalDistance: invalidThreshold, minimumVerticalDistance: 4)
                || !gesture.PointerUp())
                failures.Add("无效横向系统阈值未安全回退到默认值。");

            gesture.PointerDown(0, 0);
            if (gesture.TryStartDrag(0, -3.999, leftButtonPressed: true,
                    minimumHorizontalDistance: 4, minimumVerticalDistance: invalidThreshold)
                || !gesture.TryStartDrag(0, -4, leftButtonPressed: true,
                    minimumHorizontalDistance: 4, minimumVerticalDistance: invalidThreshold)
                || !gesture.PointerUp())
                failures.Add("无效纵向系统阈值未安全回退到默认值。");
        }

        gesture.PointerDown(1, 1);
        gesture.Reset();
        if (gesture.TryStartDrag(10, 10, leftButtonPressed: true,
                minimumHorizontalDistance: 4, minimumVerticalDistance: 4) || gesture.PointerUp())
            failures.Add("显式重置后仍可从旧按下位置启动拖动。");

        gesture.PointerDown(1, 1);
        if (gesture.TryStartDrag(double.PositiveInfinity, 1, leftButtonPressed: true,
                minimumHorizontalDistance: 4, minimumVerticalDistance: 4) || gesture.PointerUp())
            failures.Add("无效移动坐标没有清除拖动手势状态。");

        var routingFailure = VerifyButtonPointerRouting();
        if (routingFailure is not null) failures.Add(routingFailure);

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

    private static string? VerifyButtonPointerRouting()
    {
        var sequence = 0;
        var parentPreviewSequence = 0;
        var buttonHandledSequence = 0;
        var bubbleReachedParent = false;
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var parent = new System.Windows.Controls.Border();
                var button = new System.Windows.Controls.Button();
                parent.Child = button;
                parent.PreviewMouseLeftButtonDown += (_, e) =>
                {
                    if (e.ChangedButton == System.Windows.Input.MouseButton.Left)
                        parentPreviewSequence = ++sequence;
                };
                button.AddHandler(System.Windows.UIElement.MouseLeftButtonDownEvent,
                    (System.Windows.Input.MouseButtonEventHandler)((_, e) =>
                    {
                        if (e.Handled) buttonHandledSequence = ++sequence;
                    }), handledEventsToo: true);
                parent.MouseLeftButtonDown += (_, _) => bubbleReachedParent = true;

                button.RaiseEvent(new System.Windows.Input.MouseButtonEventArgs(
                    System.Windows.Input.Mouse.PrimaryDevice, Environment.TickCount,
                    System.Windows.Input.MouseButton.Left)
                {
                    RoutedEvent = System.Windows.Input.Mouse.PreviewMouseDownEvent
                });
                button.RaiseEvent(new System.Windows.Input.MouseButtonEventArgs(
                    System.Windows.Input.Mouse.PrimaryDevice, Environment.TickCount,
                    System.Windows.Input.MouseButton.Left)
                {
                    RoutedEvent = System.Windows.Input.Mouse.MouseDownEvent
                });
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        })
        {
            IsBackground = true
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        if (!thread.Join(TimeSpan.FromSeconds(5)))
            return "WPF按钮事件路由检查超时。";
        if (failure is not null)
            return $"WPF按钮事件路由检查失败：{failure.GetType().Name}。";
        return parentPreviewSequence > 0
            && buttonHandledSequence > parentPreviewSequence
            && !bubbleReachedParent
            ? null
            : "按钮未收到已处理的左键事件，或父容器预览没有先发生/冒泡处理后仍收到事件。";
    }
}
