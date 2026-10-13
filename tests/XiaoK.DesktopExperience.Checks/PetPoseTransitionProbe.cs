using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using XiaoK.Host;

namespace XiaoK.DesktopExperience.Checks;

internal static class PetPoseTransitionProbe
{
    public static IReadOnlyList<string> Run()
    {
        var failures = new List<string>();
        Exception? threadFailure = null;
        var thread = new Thread(() =>
        {
            try { RunOnSta(failures); }
            catch (Exception exception) { threadFailure = exception; }
        })
        {
            IsBackground = true,
            Name = "XiaoK pet pose transition offscreen check"
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        if (!thread.Join(TimeSpan.FromSeconds(5)))
            return ["桌宠姿态动画离屏检查超过5秒；工作线程设为后台且窗口不激活。"];
        if (threadFailure is not null)
            return [$"桌宠姿态动画离屏检查异常：{threadFailure.GetType().Name}"];
        return failures;
    }

    private static void RunOnSta(List<string> failures)
    {
        var dispatcher = Dispatcher.CurrentDispatcher;
        var scale = new ScaleTransform();
        var offset = new TranslateTransform();
        var transform = new TransformGroup();
        transform.Children.Add(scale);
        transform.Children.Add(offset);
        var window = new Window
        {
            Width = 64,
            Height = 64,
            Left = -32000,
            Top = -32000,
            Opacity = 0,
            WindowStyle = WindowStyle.None,
            ResizeMode = ResizeMode.NoResize,
            ShowActivated = false,
            ShowInTaskbar = false,
            Background = Brushes.Transparent,
            Content = new Border
            {
                Width = 48,
                Height = 48,
                RenderTransformOrigin = new Point(0.5, 0.5),
                RenderTransform = transform,
                Background = Brushes.White
            }
        };

        var foregroundBefore = GetForegroundWindow();
        var sampledMotion = false;
        var sampleTimer = new DispatcherTimer(DispatcherPriority.Render, dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(100)
        };
        var finishTimer = new DispatcherTimer(DispatcherPriority.Render, dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(420)
        };

        sampleTimer.Tick += (_, _) =>
        {
            sampleTimer.Stop();
            sampledMotion = scale.ScaleX > 1.0001 && Math.Abs(offset.Y) > 0.01;
            if (!sampledMotion) failures.Add("WPF姿态过渡没有在短时采样中产生轻微变化。");
            finishTimer.Start();
        };
        finishTimer.Tick += (_, _) =>
        {
            finishTimer.Stop();
            if (Math.Abs(scale.ScaleX - 1) > 0.0001 || Math.Abs(scale.ScaleY - 1) > 0.0001
                || Math.Abs(offset.Y) > 0.0001)
                failures.Add("WPF姿态过渡结束后没有恢复基准变换。");
            if (GetForegroundWindow() != foregroundBefore)
                failures.Add("桌宠姿态离屏动画改变了前台窗口。");
            window.Close();
        };
        window.Loaded += (_, _) =>
        {
            PetPoseTransitionAnimator.Play(scale, offset,
                PetPosePresentationPolicy.GetMotionProfile(XiaoKPetPose.Listening));
            sampleTimer.Start();
        };
        window.Closed += (_, _) => dispatcher.BeginInvokeShutdown(DispatcherPriority.Background);
        window.Show();
        Dispatcher.Run();
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();
}
