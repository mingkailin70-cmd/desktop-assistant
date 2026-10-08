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

/// <summary>以小K自有图像驱动 VPet Core；禁用宠物侧窗口动作与工具栏。</summary>
internal sealed class VPetPortraitRuntime : IDisposable
{
    private readonly GraphCore _graph;
    private readonly Main _pet;
    private bool _initialized;
    private bool _disposed;
    private XiaoKPetPose? _currentPose;

    public System.Windows.FrameworkElement View => _pet;

    public VPetPortraitRuntime(Dispatcher dispatcher, string dataRoot)
    {
        var assetRoot = Path.Combine(AppContext.BaseDirectory, "Assets", "Pets");
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
            AddPose(graph, assetRoot, "xiaok-idle", "xiaok-silver-shaded-3d-v15.png");
            AddPose(graph, assetRoot, "xiaok-listening", "xiaok-silver-shaded-vpet-listening-v1.png");
            AddPose(graph, assetRoot, "xiaok-thinking", "xiaok-silver-shaded-vpet-thinking-v1.png");
            AddPose(graph, assetRoot, "xiaok-executing", "xiaok-silver-shaded-vpet-executing-v1.png");

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

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _pet.Dispose(); }
        finally { _graph.Dispose(); }
    }

    private static void AddPose(GraphCore graph, string assetRoot, string name, string fileName)
    {
        var path = Path.GetFullPath(Path.Combine(assetRoot, fileName));
        var boundary = Path.GetFullPath(assetRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!path.StartsWith(boundary, StringComparison.OrdinalIgnoreCase) || !File.Exists(path))
            throw new FileNotFoundException("桌宠姿态图像缺失；将保留静态形象。", fileName);

        graph.AddGraph(new Picture(
            graph,
            path,
            new GraphInfo(name, GraphType.Default, AnimatType.Single, IGameSave.ModeType.Nomal),
            length: 500,
            isloop: true));
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
