using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Image = System.Windows.Controls.Image;

namespace XiaoK.Host;

internal enum XiaoKPetPose
{
    Idle,
    Listening,
    Thinking,
    Executing
}

/// <summary>以有界的单张WPF图像循环小K自有柯基帧，避免第三方桌宠布局影响窗口尺寸。</summary>
internal sealed class PetSpriteAnimationRuntime : IPetRenderer
{
    private const string PetAssetDirectory = "keke-corgi-v1";
    private const double PetCanvasSize = 300;
    private readonly Dispatcher _dispatcher;
    private readonly Image _image;
    private readonly DispatcherTimer _frameTimer;
    private IReadOnlyDictionary<XiaoKPetPose, AnimationFrame[]>? _animations;
    private XiaoKPetPose? _currentPose;
    private int _frameIndex;
    private bool _initialized;
    private bool _disposed;

    public FrameworkElement View => _image;
    public XiaoKPetPose? CurrentPose => _currentPose;

    public PetSpriteAnimationRuntime(Dispatcher dispatcher)
        : this(dispatcher, Path.Combine(AppContext.BaseDirectory, "Assets", "Pets"))
    {
    }

    internal PetSpriteAnimationRuntime(Dispatcher dispatcher, string assetRoot)
    {
        ArgumentNullException.ThrowIfNull(dispatcher);
        ArgumentException.ThrowIfNullOrWhiteSpace(assetRoot);
        _dispatcher = dispatcher;
        _dispatcher.VerifyAccess();

        _image = new Image
        {
            Width = PetCanvasSize,
            Height = PetCanvasSize,
            Stretch = Stretch.Uniform,
            IsHitTestVisible = false,
            Focusable = false,
            SnapsToDevicePixels = true
        };
        RenderOptions.SetBitmapScalingMode(_image, BitmapScalingMode.HighQuality);
        AutomationProperties.SetName(_image, "柯基动画桌宠小K");
        _image.IsVisibleChanged += Image_IsVisibleChanged;

        AssetRoot = Path.GetFullPath(assetRoot);
        _frameTimer = new DispatcherTimer(DispatcherPriority.Render, dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(420)
        };
        _frameTimer.Tick += FrameTimer_Tick;
    }

    private string AssetRoot { get; }

    public Task InitializeAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _dispatcher.VerifyAccess();
        cancellationToken.ThrowIfCancellationRequested();
        if (_initialized) return Task.CompletedTask;

        var petRoot = Path.GetFullPath(Path.Combine(AssetRoot, PetAssetDirectory));
        var boundary = petRoot.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!petRoot.StartsWith(AssetRoot.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase)
            || !Directory.Exists(petRoot))
            throw new DirectoryNotFoundException("小K柯基动画资源目录缺失。");

        var animations = new Dictionary<XiaoKPetPose, AnimationFrame[]>();
        foreach (var pose in Enum.GetValues<XiaoKPetPose>())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (directory, expectedFrameCount) = GetAnimationDefinition(pose);
            var animationPath = Path.GetFullPath(Path.Combine(petRoot, directory));
            if (!animationPath.StartsWith(boundary, StringComparison.OrdinalIgnoreCase)
                || !Directory.Exists(animationPath))
                throw new DirectoryNotFoundException($"小K柯基动画资源缺失：{directory}");

            var paths = Directory.GetFiles(animationPath, "*.png", SearchOption.TopDirectoryOnly)
                .OrderBy(static path => Path.GetFileName(path), StringComparer.Ordinal)
                .ToArray();
            if (paths.Length != expectedFrameCount)
                throw new InvalidDataException($"小K柯基动画 {directory} 应有 {expectedFrameCount} 帧，实际为 {paths.Length} 帧。");

            var frames = new AnimationFrame[paths.Length];
            for (var index = 0; index < paths.Length; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var fileName = Path.GetFileNameWithoutExtension(paths[index]);
                var separator = fileName.LastIndexOf('_');
                if (!fileName.StartsWith($"{index:D2}_", StringComparison.Ordinal)
                    || separator < 0
                    || !int.TryParse(fileName.AsSpan(separator + 1), out var duration)
                    || duration is < 50 or > 5_000)
                    throw new InvalidDataException($"小K柯基动画帧文件名或时长无效：{Path.GetFileName(paths[index])}");

                frames[index] = new AnimationFrame(LoadFrame(paths[index]), duration);
            }
            animations.Add(pose, frames);
        }

        cancellationToken.ThrowIfCancellationRequested();
        _animations = animations;
        _initialized = true;
        SetPose(XiaoKPetPose.Idle);
        return Task.CompletedTask;
    }

    public void SetPose(XiaoKPetPose pose)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _dispatcher.VerifyAccess();
        if (!_initialized || _currentPose == pose) return;
        if (_animations is null || !_animations.ContainsKey(pose))
            throw new ArgumentOutOfRangeException(nameof(pose));

        _currentPose = pose;
        _frameIndex = 0;
        ShowCurrentFrame();
        UpdateTimerState();
    }

    public void StopAnimations()
    {
        if (_disposed) return;
        _dispatcher.VerifyAccess();
        _frameTimer.Stop();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _dispatcher.VerifyAccess();
        _disposed = true;
        _frameTimer.Stop();
        _frameTimer.Tick -= FrameTimer_Tick;
        _image.IsVisibleChanged -= Image_IsVisibleChanged;
        _image.Source = null;
        _animations = null;
        _currentPose = null;
    }

    private void FrameTimer_Tick(object? sender, EventArgs e)
    {
        if (_disposed || !_initialized || _animations is null || _currentPose is null) return;
        var frames = _animations[_currentPose.Value];
        _frameIndex = (_frameIndex + 1) % frames.Length;
        ShowCurrentFrame();
    }

    private void ShowCurrentFrame()
    {
        if (_animations is null || _currentPose is null) return;
        var frame = _animations[_currentPose.Value][_frameIndex];
        _image.Source = frame.Source;
        _frameTimer.Interval = TimeSpan.FromMilliseconds(frame.DurationMilliseconds);
    }

    private void Image_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e) => UpdateTimerState();

    private void UpdateTimerState()
    {
        if (_disposed || !_initialized || _currentPose is null || !_image.IsVisible)
        {
            _frameTimer.Stop();
            return;
        }

        _frameTimer.Start();
    }

    private static BitmapImage LoadFrame(string path)
    {
        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.UriSource = new Uri(path, UriKind.Absolute);
        bitmap.EndInit();
        bitmap.Freeze();
        return bitmap;
    }

    private static (string Directory, int FrameCount) GetAnimationDefinition(XiaoKPetPose pose) => pose switch
    {
        XiaoKPetPose.Listening => ("waving", 4),
        XiaoKPetPose.Thinking => ("review", 6),
        XiaoKPetPose.Executing => ("running", 6),
        _ => ("idle", 6)
    };

    private sealed record AnimationFrame(BitmapSource Source, int DurationMilliseconds);
}
