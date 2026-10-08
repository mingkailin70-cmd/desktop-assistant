using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;
using LinePutScript;
using VPet_Simulator.Core;
using static VPet_Simulator.Core.GraphInfo;

namespace XiaoK.PetRuntimePrototype;

public partial class MainWindow : Window
{
    private readonly nint _foregroundBeforeShow;
    private readonly string _logPath;
    private GraphCore? _graph;
    private Main? _pet;
    private readonly DispatcherTimer _closeTimer;

    public MainWindow()
    {
        _foregroundBeforeShow = GetForegroundWindow();
        _logPath = Path.Combine(Path.GetTempPath(), "xiaok-vpet-runtime-probe.log");
        InitializeComponent();

        GraphCore.CachePath = Path.Combine("D:\\XiaoK", "PetRuntimePrototype", "cache");
        Directory.CreateDirectory(GraphCore.CachePath);

        _graph = new GraphCore(1000, Dispatcher)
        {
            GraphConfig = new GraphCore.Config(new LpsDocument())
        };
        var imagePath = Path.Combine(AppContext.BaseDirectory, "Assets", "Pets", "xiaok-silver-shaded-3d-v15.png");
        var animation = new Picture(
            _graph,
            imagePath,
            new GraphInfo("xiaok-idle", GraphType.Default, AnimatType.Single, IGameSave.ModeType.Nomal),
            length: 500,
            isloop: true);
        _graph.AddGraph(animation);

        var core = new GameCore
        {
            Controller = new ProbeController(),
            Graph = _graph,
            Save = new GameSave("小K")
        };
        _pet = new Main(core)
        {
            Width = 500,
            Height = 500
        };
        PetViewport.Content = _pet;

        _closeTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(15) };
        _closeTimer.Tick += (_, _) => Close();

        File.WriteAllText(_logPath, $"started={DateTimeOffset.Now:O}{Environment.NewLine}focus-before=0x{_foregroundBeforeShow:X}{Environment.NewLine}");
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _closeTimer.Start();
        try
        {
            _pet?.LoadALL();
            File.AppendAllText(_logPath, $"renderer-initialized={_pet is not null}{Environment.NewLine}");
        }
        catch (Exception exception)
        {
            File.AppendAllText(_logPath, $"renderer-error={exception.GetType().Name}{Environment.NewLine}");
        }
        _ = Dispatcher.InvokeAsync(async () =>
        {
            await Task.Delay(500);
            var foregroundAfterShow = GetForegroundWindow();
            File.AppendAllText(_logPath,
                $"renderer-started={_pet is not null}{Environment.NewLine}" +
                $"window-visible={IsVisible}{Environment.NewLine}" +
                $"foreground-preserved={foregroundAfterShow == _foregroundBeforeShow}{Environment.NewLine}");
        }, DispatcherPriority.Background);
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _closeTimer.Stop();
        try
        {
            _pet?.Dispose();
        }
        catch (Exception exception)
        {
            File.AppendAllText(_logPath, $"renderer-dispose-error={exception.GetType().Name}{Environment.NewLine}");
        }
        finally
        {
            _graph?.Dispose();
            var foregroundAfterClose = GetForegroundWindow();
            File.AppendAllText(_logPath,
                $"closed={DateTimeOffset.Now:O}{Environment.NewLine}" +
                $"foreground-preserved-after-close={foregroundAfterClose == _foregroundBeforeShow}{Environment.NewLine}");
        }
    }

    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();

    private sealed class ProbeController : IController
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
