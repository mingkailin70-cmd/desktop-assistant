using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using XiaoK.PetRuntimePrototype.PetRendering;

namespace XiaoK.PetRuntimePrototype;

internal static class ThreeDPetOffscreenRenderer
{
    private const int PixelSize = 512;
    private static readonly (ThreeDPetPose Pose, string Name)[] Poses =
    [
        (ThreeDPetPose.Idle, "idle"),
        (ThreeDPetPose.Listening, "listening"),
        (ThreeDPetPose.Thinking, "thinking"),
        (ThreeDPetPose.Executing, "executing")
    ];

    public static async Task<int> RunAsync()
    {
        var outputDirectory = Path.Combine("D:\\XiaoK", "Pet3DPrototype", "offscreen");
        Directory.CreateDirectory(outputDirectory);
        foreach (var file in Directory.EnumerateFiles(outputDirectory, "xiaok-*.png").ToArray()) File.Delete(file);
        foreach (var file in new[] { "render-report.json", "render-error.json" })
        {
            var path = Path.Combine(outputDirectory, file);
            if (File.Exists(path)) File.Delete(path);
        }

        var dispatcher = Application.Current.Dispatcher;
        var foregroundBefore = GetForegroundWindow();
        Window? host = null;
        var results = new List<PoseRenderResult>();
        try
        {
            host = new Window
            {
                Width = PixelSize,
                Height = PixelSize,
                Left = -10000,
                Top = -10000,
                Opacity = 0,
                WindowStyle = WindowStyle.None,
                ResizeMode = ResizeMode.NoResize,
                AllowsTransparency = true,
                Background = Brushes.Transparent,
                ShowActivated = false,
                ShowInTaskbar = false,
                Topmost = false
            };
            host.Show();
            await dispatcher.InvokeAsync(() => { }, DispatcherPriority.Loaded);

            foreach (var (pose, name) in Poses)
            {
                var viewport = ThreeDPetScene.Create(pose);
                host.Content = viewport;
                viewport.Measure(new Size(PixelSize, PixelSize));
                viewport.Arrange(new Rect(0, 0, PixelSize, PixelSize));
                viewport.UpdateLayout();
                await dispatcher.InvokeAsync(() => { }, DispatcherPriority.Render);
                await Task.Delay(120);

                var bitmap = new RenderTargetBitmap(PixelSize, PixelSize, 96, 96, PixelFormats.Pbgra32);
                bitmap.Render(viewport);
                var pixels = new byte[PixelSize * PixelSize * 4];
                bitmap.CopyPixels(pixels, PixelSize * 4, 0);
                var bounds = FindAlphaBounds(pixels, PixelSize, PixelSize);
                var corners = new int[]
                {
                    GetAlpha(pixels, PixelSize, 0, 0),
                    GetAlpha(pixels, PixelSize, PixelSize - 1, 0),
                    GetAlpha(pixels, PixelSize, 0, PixelSize - 1),
                    GetAlpha(pixels, PixelSize, PixelSize - 1, PixelSize - 1)
                };
                var imagePath = Path.Combine(outputDirectory, $"xiaok-{name}.png");
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(bitmap));
                using (var stream = File.Create(imagePath)) encoder.Save(stream);
                results.Add(new PoseRenderResult(name, bounds.PixelCount, bounds, corners,
                    Convert.ToHexString(SHA256.HashData(pixels)), new FileInfo(imagePath).Length, imagePath));
            }

            host.Close();
            host = null;
            var foregroundAfter = GetForegroundWindow();
            var report = new ThreeDPetRenderReport(
                DateTimeOffset.Now,
                Environment.ProcessId,
                "WPF Viewport3D procedural silver kitten; no external model or renderer package",
                foregroundBefore == foregroundAfter,
                results.Select(result => result.PixelSha256).Distinct(StringComparer.Ordinal).Count() == Poses.Length,
                results.All(result => result.NonTransparentPixelCount > 10_000),
                results.All(result => result.CornerAlpha.All(alpha => alpha == 0)),
                results);
            File.WriteAllText(Path.Combine(outputDirectory, "render-report.json"), JsonSerializer.Serialize(report,
                new JsonSerializerOptions { WriteIndented = true }));
            return report.ForegroundPreserved && report.AllStatesDistinct && report.AllStatesVisible && report.AllCornersTransparent ? 0 : 2;
        }
        catch (Exception exception)
        {
            try { host?.Close(); } catch { }
            File.WriteAllText(Path.Combine(outputDirectory, "render-error.json"), JsonSerializer.Serialize(new
            {
                at = DateTimeOffset.Now,
                type = exception.GetType().FullName,
                message = exception.Message
            }, new JsonSerializerOptions { WriteIndented = true }));
            return 1;
        }
    }

    private static PixelBounds FindAlphaBounds(byte[] pixels, int width, int height)
    {
        var left = width;
        var top = height;
        var right = -1;
        var bottom = -1;
        var count = 0;
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
        {
            if (pixels[(y * width + x) * 4 + 3] <= 8) continue;
            left = Math.Min(left, x);
            top = Math.Min(top, y);
            right = Math.Max(right, x);
            bottom = Math.Max(bottom, y);
            count++;
        }
        return new PixelBounds(left, top, right, bottom, count);
    }

    private static byte GetAlpha(byte[] pixels, int width, int x, int y) => pixels[(y * width + x) * 4 + 3];

    private static nint GetForegroundWindow() => NativeMethods.GetForegroundWindow();

    private sealed record PixelBounds(int Left, int Top, int Right, int Bottom, int PixelCount);

    private sealed record PoseRenderResult(string Pose, int NonTransparentPixelCount,
        PixelBounds Bounds, IReadOnlyList<int> CornerAlpha,
        string PixelSha256, long PngBytes, string OutputPath);

    private sealed record ThreeDPetRenderReport(DateTimeOffset At, int ProcessId, string Renderer,
        bool ForegroundPreserved, bool AllStatesDistinct, bool AllStatesVisible, bool AllCornersTransparent,
        IReadOnlyList<PoseRenderResult> States);

    private static class NativeMethods
    {
        [DllImport("user32.dll")]
        public static extern nint GetForegroundWindow();
    }
}
