using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Media3D;
using System.Windows.Threading;
using XiaoK.Host.PetRendering;

namespace XiaoK.Host;

/// <summary>缓存四个冻结的原创网格姿态；切换状态只替换模型，不持续重建或动画化几何。</summary>
internal sealed class ThreeDPetRenderer : IPetRenderer
{
    private readonly Dispatcher _dispatcher;
    private readonly IReadOnlyDictionary<XiaoKPetPose, Model3DGroup> _scenes;
    private readonly ModelVisual3D _modelVisual;
    private bool _disposed;
    private XiaoKPetPose? _currentPose;

    private ThreeDPetRenderer(Dispatcher dispatcher,
        IReadOnlyDictionary<XiaoKPetPose, Model3DGroup> scenes)
    {
        _dispatcher = dispatcher;
        _scenes = scenes;
        View = ThreeDPetScene.CreateViewport(scenes[XiaoKPetPose.Idle]);
        _modelVisual = (ModelVisual3D)View.Children[0];
        SetPose(XiaoKPetPose.Idle);
    }

    public Viewport3D View { get; }
    FrameworkElement IPetRenderer.View => View;
    public XiaoKPetPose? CurrentPose => _currentPose;
    public int CachedSceneCount => _scenes.Count;
    public bool AreCachedScenesFrozen => _scenes.Values.All(scene => scene.IsFrozen);

    public static async Task<ThreeDPetRenderer> CreateAsync(Dispatcher dispatcher,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(dispatcher);
        dispatcher.VerifyAccess();
        cancellationToken.ThrowIfCancellationRequested();
        var scenes = await Task.Run(() => BuildFrozenScenes(cancellationToken), cancellationToken)
            .ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        return await dispatcher.InvokeAsync(() => new ThreeDPetRenderer(dispatcher, scenes),
                DispatcherPriority.Normal, cancellationToken)
            .Task.ConfigureAwait(false);
    }

    public Task InitializeAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }

    public void SetPose(XiaoKPetPose pose)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _dispatcher.VerifyAccess();
        if (_currentPose == pose) return;
        _modelVisual.Content = _scenes[pose];
        _currentPose = pose;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _dispatcher.VerifyAccess();
        _disposed = true;
        View.Children.Clear();
        _modelVisual.Content = null;
    }

    private static IReadOnlyDictionary<XiaoKPetPose, Model3DGroup> BuildFrozenScenes(
        CancellationToken cancellationToken)
    {
        var scenes = new Dictionary<XiaoKPetPose, Model3DGroup>();
        foreach (var pose in Enum.GetValues<XiaoKPetPose>())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var scene = ThreeDPetScene.CreateModel(ToScenePose(pose));
            if (!scene.CanFreeze)
                throw new InvalidOperationException("三维桌宠姿态包含无法冻结的模型资源。");
            scene.Freeze();
            scenes.Add(pose, scene);
        }
        if (scenes.Count != 4)
            throw new InvalidOperationException("三维桌宠姿态缓存不完整。");
        return new ReadOnlyDictionary<XiaoKPetPose, Model3DGroup>(scenes);
    }

    private static ThreeDPetPose ToScenePose(XiaoKPetPose pose) => pose switch
    {
        XiaoKPetPose.Listening => ThreeDPetPose.Listening,
        XiaoKPetPose.Thinking => ThreeDPetPose.Thinking,
        XiaoKPetPose.Executing => ThreeDPetPose.Executing,
        _ => ThreeDPetPose.Idle
    };
}
