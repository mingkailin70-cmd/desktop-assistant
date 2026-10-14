using System.IO;
using System.Windows.Threading;
using LinePutScript;
using VPet_Simulator.Core;
using static VPet_Simulator.Core.GraphInfo;

namespace XiaoK.Host;

internal enum XiaoKPetPose
{
    Idle,
    Listening,
    Thinking,
    Executing
}

/// <summary>以小K自有动画帧驱动 VPet Core；禁用宠物侧窗口动作与工具栏。</summary>
internal sealed class VPetPortraitRuntime : IPetRenderer
{
    private const string PetAssetDirectory = "keke-corgi-v1";
    private readonly GraphCore _graph;
    private readonly Main _pet;
    private bool _initialized;
    private bool _disposed;
    private XiaoKPetPose? _currentPose;

    public System.Windows.FrameworkElement View => _pet;
    public XiaoKPetPose? CurrentPose => _currentPose;

    public VPetPortraitRuntime(Dispatcher dispatcher, string dataRoot)
        : this(dispatcher, dataRoot, Path.Combine(AppContext.BaseDirectory, "Assets", "Pets"))
    {
    }

    internal VPetPortraitRuntime(Dispatcher dispatcher, string dataRoot, string assetRoot)
    {
        ArgumentNullException.ThrowIfNull(dispatcher);
        ArgumentException.ThrowIfNullOrWhiteSpace(dataRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(assetRoot);
        var cacheRoot = Path.Combine(dataRoot, "PetRenderCache");
        Directory.CreateDirectory(cacheRoot);
        GraphCore.CachePath = cacheRoot;

        var graph = new GraphCore(800, dispatcher)
        {
            GraphConfig = new GraphCore.Config(new LpsDocument())
        };
        Main? pet = null;
        try
        {
            AddAnimation(graph, assetRoot, "xiaok-idle", "idle", 6, 420);
            AddAnimation(graph, assetRoot, "xiaok-listening", "waving", 4, 240);
            AddAnimation(graph, assetRoot, "xiaok-thinking", "review", 6, 260);
            AddAnimation(graph, assetRoot, "xiaok-executing", "running", 6, 170);

            pet = new Main(new GameCore
            {
                Controller = new InertPetController(),
                Graph = graph,
                Save = new GameSave("小K")
            })
            {
                Width = 500,
                Height = 500,
                IsHitTestVisible = false
            };

            _graph = graph;
            _pet = pet;
        }
        catch
        {
            try { pet?.Dispose(); }
            finally { graph.Dispose(); }
            throw;
        }
    }

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();
        _pet.LoadALL();
        await _pet.Load_2_WaitGraph();
        await WaitForInitialGraphAsync(_pet, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        _initialized = true;
        SetPose(XiaoKPetPose.Idle);
    }

    public void SetPose(XiaoKPetPose pose)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_initialized || _currentPose == pose) return;

        var name = pose switch
        {
            XiaoKPetPose.Listening => "xiaok-listening",
            XiaoKPetPose.Thinking => "xiaok-thinking",
            XiaoKPetPose.Executing => "xiaok-executing",
            _ => "xiaok-idle"
        };
        _pet.Display(name, AnimatType.Single, GraphType.Default, static () => { });
        _currentPose = pose;
    }

    public void StopAnimations()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        StopAnimationsCore();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try
        {
            StopAnimationsCore();
            _pet.Dispose();
        }
        finally { _graph.Dispose(); }
    }

    private void StopAnimationsCore()
    {
        StopAnimation(_pet.PetGrid);
        StopAnimation(_pet.PetGrid2);
    }

    private static void StopAnimation(object? petContainer)
    {
        if (petContainer is not System.Windows.FrameworkElement element) return;
        if (element.Tag is IGraph taggedAnimation)
            taggedAnimation.Stop(true);
        if (element is System.Windows.Controls.Decorator decorator
            && decorator.Child is IGraph childAnimation)
            childAnimation.Stop(true);
    }

    private static void AddAnimation(GraphCore graph, string assetRoot, string name, string state,
        int expectedFrameCount, int frameDurationMilliseconds)
    {
        var petRoot = Path.GetFullPath(Path.Combine(assetRoot, PetAssetDirectory));
        var animationPath = Path.GetFullPath(Path.Combine(petRoot, state));
        var boundary = petRoot.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!animationPath.StartsWith(boundary, StringComparison.OrdinalIgnoreCase)
            || !Directory.Exists(animationPath))
            throw new DirectoryNotFoundException($"柯基桌宠动画资源缺失：{state}");

        var frames = Directory.GetFiles(animationPath, "*.png", SearchOption.TopDirectoryOnly)
            .Select(path => new FileInfo(path))
            .OrderBy(file => file.Name, StringComparer.Ordinal)
            .ToArray();
        if (frames.Length != expectedFrameCount)
            throw new InvalidDataException($"柯基桌宠 {state} 动画帧数应为 {expectedFrameCount}，实际为 {frames.Length}。");

        for (var index = 0; index < frames.Length; index++)
        {
            var expectedPrefix = $"{index:D2}_";
            var fileName = Path.GetFileNameWithoutExtension(frames[index].Name);
            var separator = fileName.LastIndexOf('_');
            if (!fileName.StartsWith(expectedPrefix, StringComparison.Ordinal)
                || separator < 0
                || !int.TryParse(fileName.AsSpan(separator + 1), out var duration)
                || duration is < 50 or > 5_000)
                throw new InvalidDataException($"柯基桌宠 {state} 动画帧文件名或时长无效：{frames[index].Name}");
        }

        var graphInfo = new GraphInfo(name, GraphType.Default, AnimatType.Single, IGameSave.ModeType.Nomal);
        graph.AddGraph(new PNGAnimation(graph, animationPath, frames, graphInfo, isLoop: true));
    }

    private static async Task WaitForInitialGraphAsync(Main pet, CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(10);
        while (pet.PetGrid.Tag is not IGraph && pet.PetGrid2.Tag is not IGraph)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (DateTimeOffset.UtcNow >= deadline)
                throw new TimeoutException("VPet桌宠姿态未能在10秒内启动；将保留静态形象。");
            await Task.Delay(50, cancellationToken);
        }
    }

    private sealed class InertPetController : IController
    {
        public double ZoomRatio => 1.0;
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
