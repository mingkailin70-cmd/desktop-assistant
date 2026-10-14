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
        ValidateTransparentAssetCorners(repositoryRoot, failures);
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
                    var cornerAlpha = new[]
                    {
                        GetAlpha(pixels, 0, 0),
                        GetAlpha(pixels, 359, 0),
                        GetAlpha(pixels, 0, 359),
                        GetAlpha(pixels, 359, 359)
                    };
                    var cornerLeakCount = CountCornerLeaks(pixels, 360, 360);
                    var surfaceNotFilled = visiblePixels < 360 * 360 * 98 / 100;
                    hashes.Add(Convert.ToHexString(SHA256.HashData(pixels)));
                    Require(renderer.CurrentPose == pose, $"VPet柯基桌宠状态切换没有保存 {pose}。", failures);
                    Require(visiblePixels > 8_000, $"VPet柯基桌宠姿态 {pose} 没有渲染出完整主体。", failures);
                    Require(surfaceNotFilled,
                        $"VPet柯基桌宠姿态 {pose} 几乎填满整个画布，疑似渲染了不透明背景，非透明像素数={visiblePixels}。", failures);
                    Require(cornerLeakCount == 0,
                        $"VPet柯基姿态 {pose} 的离屏渲染在四角12x12区域出现非透明像素，数量={cornerLeakCount}，角点alpha={string.Join(",", cornerAlpha)}。", failures);
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
                if (renderer is not null)
                {
                    try
                    {
                        renderer.StopAnimations();
                        await Task.Delay(TimeSpan.FromMilliseconds(500));
                    }
                    catch (Exception exception) { failures.Add($"VPet柯基动画停止失败：{exception.GetType().Name}"); }
                    try { ClearImageSources(renderer.View); }
                    catch (Exception exception) { failures.Add($"VPet柯基动画图片资源释放失败：{exception.GetType().Name}"); }
                    try { renderer.Dispose(); }
                    catch (Exception exception) { failures.Add($"VPet柯基动画释放失败：{exception.GetType().Name}"); }
                }
                window.Content = null;
                if (GetForegroundWindow() != foregroundBefore)
                    failures.Add("VPet柯基动画离屏检查改变了前台窗口。");
                window.Close();
            }
        };
        window.Closed += (_, _) => dispatcher.BeginInvokeShutdown(DispatcherPriority.Background);
        try
        {
            watchdog.Start();
            window.Show();
            Dispatcher.Run();
        }
        finally
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            if (!TryDeleteDirectory(dataRoot, out var cleanupFailure))
                failures.Add($"VPet柯基动画检查在Dispatcher关闭后仍未能清理临时缓存目录（{cleanupFailure}）。");
        }
    }

    private static int CountVisiblePixels(byte[] pixels)
    {
        var count = 0;
        for (var offset = 3; offset < pixels.Length; offset += 4)
            if (pixels[offset] > 8) count++;
        return count;
    }

    private static void ClearImageSources(DependencyObject parent)
    {
        if (parent is System.Windows.Controls.Image image)
            image.Source = null;

        for (var childIndex = 0; childIndex < VisualTreeHelper.GetChildrenCount(parent); childIndex++)
            ClearImageSources(VisualTreeHelper.GetChild(parent, childIndex));
    }

    private static void ValidateTransparentAssetCorners(string repositoryRoot, List<string> failures)
    {
        var petRoot = Path.Combine(repositoryRoot, "src", "XiaoK.Host", "Assets", "Pets", "keke-corgi-v1");
        string[] framePaths = Directory.Exists(petRoot)
            ? Directory.GetFiles(petRoot, "*.png", SearchOption.AllDirectories)
            : Array.Empty<string>();
        Require(framePaths.Length == 22, $"柯基动画应包含22张PNG帧，实际为{framePaths.Length}张。", failures);

        foreach (var path in framePaths)
        {
            try
            {
                var source = BitmapFrame.Create(new Uri(path), BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
                var bitmap = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
                var pixels = new byte[bitmap.PixelWidth * bitmap.PixelHeight * 4];
                bitmap.CopyPixels(pixels, bitmap.PixelWidth * 4, 0);
                var corners = new[]
                {
                    GetAlpha(pixels, bitmap.PixelWidth, 0, 0),
                    GetAlpha(pixels, bitmap.PixelWidth, bitmap.PixelWidth - 1, 0),
                    GetAlpha(pixels, bitmap.PixelWidth, 0, bitmap.PixelHeight - 1),
                    GetAlpha(pixels, bitmap.PixelWidth, bitmap.PixelWidth - 1, bitmap.PixelHeight - 1)
                };
                Require(corners.All(alpha => alpha <= 8),
                    $"柯基原始帧 {Path.GetFileName(path)} 的画布角落不透明，alpha={string.Join(",", corners)}。", failures);
                var cornerLeakCount = CountCornerLeaks(pixels, bitmap.PixelWidth, bitmap.PixelHeight);
                Require(cornerLeakCount == 0,
                    $"柯基原始帧 {Path.GetFileName(path)} 的四角12x12区域出现非透明像素，数量={cornerLeakCount}。", failures);
            }
            catch (Exception exception)
            {
                failures.Add($"柯基原始帧 {Path.GetFileName(path)} 无法进行透明角落检查：{exception.GetType().Name}。");
            }
        }
    }

    private static int CountCornerLeaks(byte[] pixels, int width, int height)
    {
        const int cornerProbeSize = 12;
        var count = 0;
        for (var y = 0; y < cornerProbeSize; y++)
        for (var x = 0; x < cornerProbeSize; x++)
        {
            if (GetAlpha(pixels, width, x, y) > 8) count++;
            if (GetAlpha(pixels, width, width - 1 - x, y) > 8) count++;
            if (GetAlpha(pixels, width, x, height - 1 - y) > 8) count++;
            if (GetAlpha(pixels, width, width - 1 - x, height - 1 - y) > 8) count++;
        }

        return count;
    }

    private static byte GetAlpha(byte[] pixels, int width, int x, int y) => pixels[(y * width + x) * 4 + 3];

    private static bool TryDeleteDirectory(string path, out string lastFailure)
    {
        lastFailure = "unknown";
        for (var attempt = 0; attempt < 4; attempt++)
        {
            try
            {
                if (!Directory.Exists(path)) return true;
                Directory.Delete(path, recursive: true);
                return !Directory.Exists(path);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                lastFailure = exception.GetType().Name;
                if (attempt == 3) return false;
                Thread.Sleep(TimeSpan.FromMilliseconds(50 << attempt));
            }
        }

        return false;
    }

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
