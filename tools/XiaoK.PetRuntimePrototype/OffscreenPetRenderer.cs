using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using LinePutScript;
using VPet_Simulator.Core;
using static VPet_Simulator.Core.GraphInfo;

namespace XiaoK.PetRuntimePrototype;

internal static class OffscreenPetRenderer
{
    private const int PixelSize = 500;
    private static readonly (string State, string FileName)[] Poses =
    [
        ("xiaok-idle", "xiaok-silver-shaded-3d-v15.png"),
        ("xiaok-listening", "xiaok-silver-shaded-vpet-listening-v1.png"),
        ("xiaok-thinking", "xiaok-silver-shaded-vpet-thinking-v1.png"),
        ("xiaok-executing", "xiaok-silver-shaded-vpet-executing-v1.png")
    ];

    public static async Task<int> RunAsync()
    {
        var outputDirectory = Path.Combine("D:\\XiaoK", "PetRuntimePrototype", "offscreen");
        Directory.CreateDirectory(outputDirectory);
        foreach (var staleReport in new[] { "render-report.json", "render-error.json", "cleanup-errors.txt" })
        {
            var stalePath = Path.Combine(outputDirectory, staleReport);
            if (File.Exists(stalePath))
            {
                File.Delete(stalePath);
            }
        }

        var dispatcher = Application.Current.Dispatcher;
        GraphCore.CachePath = Path.Combine("D:\\XiaoK", "PetRuntimePrototype", "cache");
        Directory.CreateDirectory(GraphCore.CachePath);

        GraphCore? graph = null;
        Main? pet = null;
        Window? hiddenHostWindow = null;
        var exitCode = 1;
        var cleanupErrors = new List<string>();
        var foregroundBeforeShow = GetForegroundWindow();
        try
        {
            graph = new GraphCore(1000, dispatcher)
            {
                GraphConfig = new GraphCore.Config(new LpsDocument())
            };
            foreach (var (state, fileName) in Poses)
            {
                var imagePath = Path.Combine(AppContext.BaseDirectory, "Assets", "Pets", fileName);
                if (!File.Exists(imagePath))
                {
                    throw new FileNotFoundException($"姿态素材不存在：{fileName}", imagePath);
                }

                graph.AddGraph(new Picture(
                    graph,
                    imagePath,
                    new GraphInfo(state, GraphType.Default, AnimatType.Single, IGameSave.ModeType.Nomal),
                    length: 500,
                    isloop: true));
            }

            var save = new GameSave("小K") { Mode = IGameSave.ModeType.Nomal };
            pet = new Main(new GameCore
            {
                Controller = new OffscreenController(),
                Graph = graph,
                Save = save
            })
            {
                Width = PixelSize,
                Height = PixelSize
            };
            hiddenHostWindow = new Window
            {
                Content = pet,
                Width = PixelSize,
                Height = PixelSize,
                Left = -10000,
                Top = -10000,
                Opacity = 0,
                IsHitTestVisible = false,
                WindowStyle = WindowStyle.None,
                ResizeMode = ResizeMode.NoResize,
                AllowsTransparency = true,
                Background = Brushes.Transparent,
                ShowActivated = false,
                ShowInTaskbar = false,
                Topmost = false
            };
            hiddenHostWindow.Show();
            await dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.Loaded);
            pet.LoadALL();
            await pet.Load_2_WaitGraph();
            await WaitForInitialGraphAsync(pet);
            var foregroundAfterLoad = GetForegroundWindow();

            var renderResults = new List<PoseRenderResult>();
            foreach (var (state, _) in Poses)
            {
                var resolvedGraph = graph.FindGraph(state, AnimatType.Single, save.Mode)
                    ?? throw new InvalidOperationException($"VPet未按名称/姿态/模式找到图层：state={state}; mode={save.Mode}; registered={string.Join(',', graph.GraphsName.Keys)}");
                if (!resolvedGraph.IsReady)
                {
                    throw new InvalidOperationException($"VPet图层尚未就绪：state={state}; failure={resolvedGraph.FailMessage}");
                }

                pet.Display(resolvedGraph, static () => { });
                pet.Measure(new Size(PixelSize, PixelSize));
                pet.Arrange(new Rect(0, 0, PixelSize, PixelSize));
                pet.UpdateLayout();
                await dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.Render);
                await Task.Delay(150);

                var bitmap = new RenderTargetBitmap(PixelSize, PixelSize, 96, 96, PixelFormats.Pbgra32);
                bitmap.Render(pet);
                var pixels = new byte[PixelSize * PixelSize * 4];
                bitmap.CopyPixels(pixels, PixelSize * 4, 0);
                var alphaPixels = 0;
                var maxAlpha = 0;
                for (var index = 3; index < pixels.Length; index += 4)
                {
                    var alpha = pixels[index];
                    if (alpha > 0)
                    {
                        alphaPixels++;
                    }

                    maxAlpha = Math.Max(maxAlpha, alpha);
                }

                var cornerAlphas = new[]
                {
                    GetAlpha(pixels, 0, 0),
                    GetAlpha(pixels, PixelSize - 1, 0),
                    GetAlpha(pixels, 0, PixelSize - 1),
                    GetAlpha(pixels, PixelSize - 1, PixelSize - 1)
                };
                var imagePath = Path.Combine(outputDirectory, $"{state}.png");
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(bitmap));
                using (var stream = File.Create(imagePath))
                {
                    encoder.Save(stream);
                }

                renderResults.Add(new PoseRenderResult(
                    state,
                    PixelSize,
                    PixelSize,
                    alphaPixels,
                    maxAlpha,
                    cornerAlphas,
                    Convert.ToHexString(SHA256.HashData(pixels)),
                    new FileInfo(imagePath).Length,
                    imagePath));
            }

            hiddenHostWindow.Close();
            hiddenHostWindow = null;
            var foregroundAfterClose = GetForegroundWindow();
            var hashesAreDistinct = renderResults.Select(result => result.PixelHash).Distinct(StringComparer.Ordinal).Count() == Poses.Length;
            var transparentCornersForEveryPose = renderResults.All(result => result.CornerAlpha.All(alpha => alpha == 0));
            var output = new OffscreenRenderReport(
                DateTimeOffset.Now,
                Environment.ProcessId,
                true,
                foregroundAfterLoad == foregroundBeforeShow && foregroundAfterClose == foregroundBeforeShow,
                hashesAreDistinct,
                transparentCornersForEveryPose,
                renderResults);
            File.WriteAllText(Path.Combine(outputDirectory, "render-report.json"), JsonSerializer.Serialize(output, new JsonSerializerOptions { WriteIndented = true }));
            exitCode = hashesAreDistinct
                && transparentCornersForEveryPose
                && output.ForegroundPreserved
                && renderResults.All(result => result.NonTransparentPixels > 0 && result.MaximumAlpha > 0)
                ? 0
                : 2;
        }
        catch (Exception exception)
        {
            var output = new
            {
                createdAt = DateTimeOffset.Now,
                error = exception.ToString(),
                mainFields = DescribeFields(pet),
                graphFields = DescribeFields(graph)
            };
            File.WriteAllText(Path.Combine(outputDirectory, "render-error.json"), JsonSerializer.Serialize(output, new JsonSerializerOptions { WriteIndented = true }));
            exitCode = 1;
        }
        finally
        {
            try
            {
                hiddenHostWindow?.Close();
            }
            catch (Exception exception)
            {
                cleanupErrors.Add($"host-window-close:{exception.GetType().Name}");
            }

            try
            {
                pet?.Dispose();
            }
            catch (Exception exception)
            {
                cleanupErrors.Add($"pet-dispose:{exception.GetType().Name}");
            }

            try
            {
                graph?.Dispose();
            }
            catch (Exception exception)
            {
                cleanupErrors.Add($"graph-dispose:{exception.GetType().Name}");
            }

            if (cleanupErrors.Count > 0)
            {
                File.WriteAllLines(Path.Combine(outputDirectory, "cleanup-errors.txt"), cleanupErrors);
                if (exitCode == 0)
                {
                    exitCode = 3;
                }
            }
        }

        return exitCode;
    }

    private static byte GetAlpha(byte[] pixels, int x, int y) => pixels[(y * PixelSize + x) * 4 + 3];

    private static async Task WaitForInitialGraphAsync(Main pet)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(10);
        while (pet.PetGrid.Tag is not IGraph && pet.PetGrid2.Tag is not IGraph)
        {
            if (DateTimeOffset.UtcNow >= deadline)
            {
                throw new TimeoutException("VPet默认姿态未能在10秒内启动。");
            }

            await Task.Delay(50);
        }
    }

    private static IReadOnlyList<string> DescribeFields(object? instance) => instance is null
        ? ["instance=null"]
        : instance.GetType()
            .GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .Select(field => $"{field.Name}={(field.GetValue(instance) is { } value ? value.GetType().FullName : "null")}")
            .ToArray();

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();

    private sealed record PoseRenderResult(
        string State,
        int Width,
        int Height,
        int NonTransparentPixels,
        int MaximumAlpha,
        byte[] CornerAlpha,
        string PixelHash,
        long PngBytes,
        string OutputPath);

    private sealed record OffscreenRenderReport(
        DateTimeOffset CreatedAt,
        int ProcessId,
        bool HiddenHostWindowCreated,
        bool ForegroundPreserved,
        bool AllPosePixelHashesDistinct,
        bool TransparentCornersForEveryPose,
        IReadOnlyList<PoseRenderResult> Poses);

    private sealed class OffscreenController : IController
    {
        public double ZoomRatio => 0.6;
        public int PressLength => 500;
        public bool EnableFunction => false;
        public int InteractionCycle => 0;
        public bool RePostionActive { get; set; }

        public void MoveWindows(double x, double y) { }
        public double GetWindowsDistanceLeft() => 0;
        public double GetWindowsDistanceRight() => 0;
        public double GetWindowsDistanceUp() => 0;
        public double GetWindowsDistanceDown() => 0;
        public void ShowPanel() { }
        public void ResetPosition() { }
        public bool CheckPosition() => false;
    }

}
