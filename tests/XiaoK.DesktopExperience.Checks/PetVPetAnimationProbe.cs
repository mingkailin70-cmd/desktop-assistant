using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using XiaoK.Host;

namespace XiaoK.DesktopExperience.Checks;

internal static class PetVPetAnimationProbe
{
    public static IReadOnlyList<string> Run(string repositoryRoot)
    {
        var failures = new List<string>();
        Exception? threadFailure = null;
        var thread = new Thread(() =>
        {
            try { RunOnSta(repositoryRoot, failures); }
            catch (Exception exception) { threadFailure = exception; }
        })
        {
            IsBackground = true,
            Name = "XiaoK VPet Corgi animation offscreen check"
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        if (!thread.Join(TimeSpan.FromSeconds(20)))
            return ["VPet柯基动画离屏检查超过20秒；后台STA窗口不会激活前台。"];
        if (threadFailure is not null)
            return [$"VPet柯基动画离屏检查异常：{threadFailure.GetType().Name}"];
        return failures;
    }

    private static void RunOnSta(string repositoryRoot, List<string> failures)
    {
        var dispatcher = Dispatcher.CurrentDispatcher;
        var foregroundBefore = GetForegroundWindow();
        using var cancellation = new CancellationTokenSource();
        var dataRoot = Path.Combine(Path.GetTempPath(), "XiaoK-corgi-pet-check-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dataRoot);
        var assetRoot = Path.Combine(repositoryRoot, "src", "XiaoK.Host", "Assets", "Pets");
        var window = new Window
        {
            Width = 360,
            Height = 360,
            Left = -32_000,
            Top = -32_000,
            Opacity = 0,
            WindowStyle = WindowStyle.None,
            ResizeMode = ResizeMode.NoResize,
            ShowActivated = false,
            ShowInTaskbar = false,
            Background = Brushes.Transparent
        };
        var watchdog = new DispatcherTimer(DispatcherPriority.Send, dispatcher)
        {
            Interval = TimeSpan.FromSeconds(16)
        };
        watchdog.Tick += (_, _) =>
        {
            watchdog.Stop();
            cancellation.Cancel();
            failures.Add("VPet柯基动画离屏渲染未能在16秒内完成。");
        };
        window.Loaded += async (_, _) =>
        {
            VPetPortraitRuntime? renderer = null;
            try
            {
                renderer = new VPetPortraitRuntime(dispatcher, dataRoot, assetRoot);
                await renderer.InitializeAsync(cancellation.Token);
                window.Content = renderer.View;
                renderer.View.Measure(new Size(360, 360));
                renderer.View.Arrange(new Rect(0, 0, 360, 360));
                renderer.View.UpdateLayout();

                Require(renderer.View is FrameworkElement element
                        && !element.IsHitTestVisible && !element.Focusable,
                    "VPet柯基动画视图可命中或可聚焦。", failures);

                var hashes = new HashSet<string>(StringComparer.Ordinal);
                foreach (var pose in Enum.GetValues<XiaoKPetPose>())
                {
                    renderer.SetPose(pose);
                    await Task.Delay(450, cancellation.Token);
                    renderer.View.UpdateLayout();
                    var bitmap = new RenderTargetBitmap(360, 360, 96, 96, PixelFormats.Pbgra32);
                    bitmap.Render(renderer.View);
                    var pixels = new byte[360 * 360 * 4];
                    bitmap.CopyPixels(pixels, 360 * 4, 0);
                    var visiblePixels = CountVisiblePixels(pixels);
                    var cornersTransparent = GetAlpha(pixels, 0, 0) == 0
                        && GetAlpha(pixels, 359, 0) == 0
                        && GetAlpha(pixels, 0, 359) == 0
                        && GetAlpha(pixels, 359, 359) == 0;
                    hashes.Add(Convert.ToHexString(SHA256.HashData(pixels)));
                    Require(renderer.CurrentPose == pose, $"VPet柯基桌宠状态切换没有保存 {pose}。", failures);
                    Require(visiblePixels > 8_000, $"VPet柯基桌宠姿态 {pose} 没有渲染出完整主体。", failures);
                    Require(cornersTransparent, $"VPet柯基桌宠姿态 {pose} 填充了透明角落。", failures);
                }

                Require(hashes.Count == Enum.GetValues<XiaoKPetPose>().Length,
                    "VPet柯基动画四种姿态的渲染结果没有全部区分开。", failures);
                renderer.SetPose(XiaoKPetPose.Idle);
                await Task.Delay(120, cancellation.Token);
                Require(renderer.CurrentPose == XiaoKPetPose.Idle, "VPet柯基动画没有恢复到待命姿态。", failures);
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
            {
                // The watchdog already recorded the failure; cancellation keeps the probe bounded.
            }
            catch (Exception exception)
            {
                failures.Add($"VPet柯基动画渲染/切换失败：{exception.GetType().Name}");
            }
            finally
            {
                watchdog.Stop();
                try { renderer?.Dispose(); }
                catch (Exception exception) { failures.Add($"VPet柯基动画释放失败：{exception.GetType().Name}"); }
                window.Content = null;
                if (GetForegroundWindow() != foregroundBefore)
                    failures.Add("VPet柯基动画离屏检查改变了前台窗口。");
                window.Close();
                try { Directory.Delete(dataRoot, recursive: true); }
                catch (IOException) { failures.Add("VPet柯基动画检查未能清理临时缓存目录。"); }
                catch (UnauthorizedAccessException) { failures.Add("VPet柯基动画检查未能清理临时缓存目录。"); }
            }
        };
        window.Closed += (_, _) => dispatcher.BeginInvokeShutdown(DispatcherPriority.Background);
        watchdog.Start();
        window.Show();
        Dispatcher.Run();
    }

    private static int CountVisiblePixels(byte[] pixels)
    {
        var count = 0;
        for (var offset = 3; offset < pixels.Length; offset += 4)
            if (pixels[offset] > 8) count++;
        return count;
    }

    private static byte GetAlpha(byte[] pixels, int x, int y) => pixels[(y * 360 + x) * 4 + 3];

    private static nint GetForegroundWindow() => NativeMethods.GetForegroundWindow();

    private static void Require(bool passed, string message, List<string> failures)
    {
        if (!passed) failures.Add(message);
    }

    private static class NativeMethods
    {
        [DllImport("user32.dll")]
        internal static extern nint GetForegroundWindow();
    }
}
