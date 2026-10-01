using System.IO;
using System.Runtime.InteropServices;
using System.Security;
using System.Security.Principal;
using System.Threading;
using System.Windows;

namespace XiaoK.Host;

public partial class App : System.Windows.Application
{
    private const string RestoreMessageName = "XiaoK.DesktopAssistant.Restore.v1";
    private Mutex? _instanceMutex;
    private bool _ownsMutex;

    static App()
    {
        _ = System.Windows.Forms.Application.SetHighDpiMode(System.Windows.Forms.HighDpiMode.PerMonitorV2);
    }

    internal static uint RestoreMessageId { get; private set; }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        RestoreMessageId = RegisterWindowMessage(RestoreMessageName);

        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            var userId = identity.User?.Value ?? Environment.UserName;
            _instanceMutex = new Mutex(initiallyOwned: true, $"Local\\XiaoK.DesktopAssistant.{userId}", out _ownsMutex);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or SecurityException)
        {
            System.Windows.MessageBox.Show("无法检查小K是否已运行。为避免重复启动，应用将退出。", "小K", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
            Shutdown();
            return;
        }

        var startInTray = e.Args.Contains("--background", StringComparer.OrdinalIgnoreCase);
        if (!_ownsMutex)
        {
            if (!startInTray) RestoreExistingInstance();
            Shutdown();
            return;
        }

        if (e.Args.Contains("--diagnostics-profile", StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                var profileRoot = Path.Combine(Path.GetTempPath(), "XiaoK-Diagnostics-" + Guid.NewGuid().ToString("N"));
                XiaoKSettings.EnableDiagnosticsProfile(profileRoot);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                System.Windows.MessageBox.Show(
                    $"无法创建隔离诊断配置。小K不会读取常规设置或数据目录。{Environment.NewLine}{ex.Message}",
                    "小K诊断模式", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
                Shutdown();
                return;
            }
        }

        MainWindow = new MainWindow();
        if (startInTray) MainWindow.Opacity = 0;
        MainWindow.Show();
        if (startInTray)
        {
            MainWindow.Hide();
            MainWindow.Opacity = 1;
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (_ownsMutex)
        {
            try { _instanceMutex?.ReleaseMutex(); }
            catch (ApplicationException) { }
        }
        _instanceMutex?.Dispose();
        base.OnExit(e);
    }

    private static void RestoreExistingInstance()
    {
        if (RestoreMessageId == 0) return;
        for (var attempt = 0; attempt < 20; attempt++)
        {
            var window = FindWindow(null, "小K");
            if (window != IntPtr.Zero && PostMessage(window, RestoreMessageId, IntPtr.Zero, IntPtr.Zero)) return;
            Thread.Sleep(50);
        }
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern uint RegisterWindowMessage(string message);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindow(string? className, string windowName);

    [DllImport("user32.dll")]
    private static extern bool PostMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
}
