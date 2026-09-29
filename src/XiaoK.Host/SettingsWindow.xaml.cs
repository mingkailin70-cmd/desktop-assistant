using System.IO;
using System.Security;
using System.Windows;
using Forms = System.Windows.Forms;

namespace XiaoK.Host;

public partial class SettingsWindow : Window
{
    private readonly XiaoKSettings _original;
    private readonly bool _startupWasEnabled;

    internal SettingsWindow(XiaoKSettings settings)
    {
        InitializeComponent();
        _original = settings;
        DataRootBox.Text = settings.DataRoot;
        ModelRootBox.Text = settings.ModelRoot;
        InferenceEndpointBox.Text = settings.InferenceEndpoint;

        var startupSupported = false;
        var startupStatus = "MSIX 登录启动任务尚未接入；此打包版本不能通过注册表设置自启动。";
        try
        {
            startupSupported = LoginStartupRegistration.IsSupported;
            _startupWasEnabled = startupSupported && LoginStartupRegistration.IsEnabled;
            if (startupSupported) startupStatus = "仅为当前用户创建或移除登录启动项，不需要管理员权限。";
        }
        catch (Exception ex) when (ex is InvalidOperationException or UnauthorizedAccessException or SecurityException)
        {
            _startupWasEnabled = false;
            startupStatus = ex.Message;
        }
        StartupCheck.IsChecked = _startupWasEnabled;
        StartupCheck.IsEnabled = startupSupported;
        StartupStatusText.Text = startupStatus;
    }

    private void BrowseDataRoot_Click(object sender, RoutedEventArgs e) => BrowseInto(DataRootBox, "选择用户数据目录");

    private void BrowseModelRoot_Click(object sender, RoutedEventArgs e) => BrowseInto(ModelRootBox, "选择本地模型目录");

    private void BrowseInto(System.Windows.Controls.TextBox target, string description)
    {
        using var picker = new Forms.FolderBrowserDialog
        {
            Description = description,
            UseDescriptionForTitle = true,
            ShowNewFolderButton = true,
            SelectedPath = Directory.Exists(target.Text) ? target.Text : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
        };

        if (picker.ShowDialog() == Forms.DialogResult.OK) target.Text = picker.SelectedPath;
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var startupRequested = StartupCheck.IsEnabled && StartupCheck.IsChecked == true;
        var startupChanged = StartupCheck.IsEnabled && startupRequested != _startupWasEnabled;

        try
        {
            var updated = _original with
            {
                DataRoot = ValidateLocalDirectory(DataRootBox.Text, "用户数据目录"),
                ModelRoot = ValidateLocalDirectory(ModelRootBox.Text, "模型目录"),
                InferenceEndpoint = ValidateLoopbackEndpoint(InferenceEndpointBox.Text)
            };

            if (startupChanged) LoginStartupRegistration.SetEnabled(startupRequested);
            try
            {
                updated.Save();
            }
            catch
            {
                if (startupChanged) LoginStartupRegistration.SetEnabled(_startupWasEnabled);
                throw;
            }

            DialogResult = true;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or InvalidOperationException or NotSupportedException)
        {
            StatusText.Text = ex.Message;
        }
    }

    private static string ValidateLocalDirectory(string value, string label)
    {
        var trimmed = value.Trim();
        if (!Path.IsPathFullyQualified(trimmed) || trimmed.StartsWith("\\\\", StringComparison.Ordinal))
            throw new ArgumentException($"{label}必须是本机上的完整目录路径。", nameof(value));

        var fullPath = Path.GetFullPath(trimmed).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var pathRoot = Path.GetPathRoot(fullPath)?.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (string.Equals(fullPath, pathRoot, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException($"{label}不能直接指向磁盘根目录。", nameof(value));

        var repository = XiaoKSettings.FindWorkspace(AppContext.BaseDirectory);
        if (repository is not null && IsSameOrChildPath(fullPath, repository))
            throw new ArgumentException($"{label}不能放在 Git 仓库内。", nameof(value));

        return fullPath;
    }

    private static bool IsSameOrChildPath(string path, string root)
    {
        var normalizedRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return path.Equals(normalizedRoot, StringComparison.OrdinalIgnoreCase)
            || path.StartsWith(normalizedRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            || path.StartsWith(normalizedRoot + Path.AltDirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    private static string ValidateLoopbackEndpoint(string value)
    {
        if (!Uri.TryCreate(value.Trim(), UriKind.Absolute, out var endpoint)
            || (endpoint.Scheme != Uri.UriSchemeHttp && endpoint.Scheme != Uri.UriSchemeHttps)
            || !endpoint.IsLoopback
            || endpoint.UserInfo.Length != 0
            || endpoint.Query.Length != 0
            || endpoint.Fragment.Length != 0)
            throw new ArgumentException("推理服务地址必须是无凭据的本机 HTTP 或 HTTPS 地址。", nameof(value));

        return endpoint.ToString();
    }
}
