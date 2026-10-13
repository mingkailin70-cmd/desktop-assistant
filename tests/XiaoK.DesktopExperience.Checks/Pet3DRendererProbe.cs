using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using XiaoK.Host;

namespace XiaoK.DesktopExperience.Checks;

internal static class Pet3DRendererProbe
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
            Name = "XiaoK 3D pet renderer offscreen check"
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        if (!thread.Join(TimeSpan.FromSeconds(20)))
            return ["三维桌宠离屏渲染/压力检查超过20秒；后台STA窗口不会激活前台。"];
        if (threadFailure is not null)
            return [$"三维桌宠离屏检查异常：{threadFailure.GetType().Name}"];
        return failures;
    }

    private static void RunOnSta(List<string> failures)
    {
        var dispatcher = Dispatcher.CurrentDispatcher;
        var foregroundBefore = GetForegroundWindow();
        using var cancellation = new CancellationTokenSource();
        var window = new Window
        {
            Width = 512,
            Height = 512,
            Left = -32000,
            Top = -32000,
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
            failures.Add("三维桌宠离屏渲染未能在16秒内完成。");
        };
        window.Loaded += async (_, _) =>
        {
            ThreeDPetRenderer? renderer = null;
            try
            {
                renderer = await ThreeDPetRenderer.CreateAsync(dispatcher, cancellation.Token);
                window.Content = renderer.View;
                renderer.View.Measure(new Size(512, 512));
                renderer.View.Arrange(new Rect(0, 0, 512, 512));
                renderer.View.UpdateLayout();

                Require(renderer.CachedSceneCount == 4, "三维桌宠没有固定缓存四种姿态。", failures);
                Require(renderer.AreCachedScenesFrozen, "三维桌宠姿态模型未冻结，可能跨线程修改。", failures);
                Require(!renderer.View.IsHitTestVisible && !renderer.View.Focusable,
                    "三维桌宠视口可命中或可聚焦。", failures);

                var hashes = new HashSet<string>(StringComparer.Ordinal);
                var previewDirectory = Environment.GetEnvironmentVariable("XIAOK_PET3D_RENDER_DIR");
                foreach (var pose in Enum.GetValues<XiaoKPetPose>())
                {
                    renderer.SetPose(pose);
                    renderer.View.UpdateLayout();
                    await Task.Delay(100);
                    var bitmap = new RenderTargetBitmap(512, 512, 96, 96, PixelFormats.Pbgra32);
                    bitmap.Render(renderer.View);
                    var pixels = new byte[512 * 512 * 4];
                    bitmap.CopyPixels(pixels, 512 * 4, 0);
                    var visiblePixels = CountVisiblePixels(pixels);
                    var cornersTransparent = GetAlpha(pixels, 0, 0) == 0
                        && GetAlpha(pixels, 511, 0) == 0
                        && GetAlpha(pixels, 0, 511) == 0
                        && GetAlpha(pixels, 511, 511) == 0;
                    hashes.Add(Convert.ToHexString(SHA256.HashData(pixels)));
                    Require(visiblePixels > 10_000, $"三维桌宠姿态 {pose} 的渲染主体不完整。", failures);
                    Require(cornersTransparent, $"三维桌宠姿态 {pose} 的透明角落被填充。", failures);
                    SavePreviewIfRequested(bitmap, previewDirectory, pose);
                }

                Require(hashes.Count == 4, "四种桌宠姿态渲染结果没有全部区分开。", failures);
                for (var index = 0; index < 1_000; index++)
                    renderer.SetPose((XiaoKPetPose)(index % 4));
                Require(renderer.CurrentPose == XiaoKPetPose.Executing,
                    "高频姿态切换结束后当前状态与模型不一致。", failures);
                Require(renderer.CachedSceneCount == 4, "高频姿态切换改变了固定模型缓存数量。", failures);
            }
            catch (Exception exception)
            {
                failures.Add($"三维桌宠渲染/切换失败：{exception.GetType().Name}");
            }
            finally
            {
                watchdog.Stop();
                try { renderer?.Dispose(); }
                catch (Exception exception) { failures.Add($"三维桌宠释放失败：{exception.GetType().Name}"); }
                window.Content = null;
                if (GetForegroundWindow() != foregroundBefore)
                    failures.Add("三维桌宠离屏检查改变了前台窗口。");
                window.Close();
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

    private static byte GetAlpha(byte[] pixels, int x, int y) => pixels[(y * 512 + x) * 4 + 3];

    private static void SavePreviewIfRequested(BitmapSource bitmap, string? directory, XiaoKPetPose pose)
    {
        if (string.IsNullOrWhiteSpace(directory)) return;
        var fullDirectory = Path.GetFullPath(directory);
        Directory.CreateDirectory(fullDirectory);
        var path = Path.Combine(fullDirectory, $"xiaok-{pose.ToString().ToLowerInvariant()}.png");
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        encoder.Save(stream);
    }

    private static void Require(bool passed, string message, List<string> failures)
    {
        if (!passed) failures.Add(message);
    }

    private static nint GetForegroundWindow() => NativeMethods.GetForegroundWindow();

    private static class NativeMethods
    {
        [DllImport("user32.dll")]
        internal static extern nint GetForegroundWindow();
    }
}
