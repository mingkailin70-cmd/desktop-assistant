using System.IO;
using System.Security;
using System.Diagnostics;
using System.Windows;
using Forms = System.Windows.Forms;

namespace XiaoK.Host;

public partial class SettingsWindow : Window
{
    private readonly XiaoKSettings _original;
    private readonly bool _startupWasEnabled;
    private TimeSpan _previousCpuTime;
    private long _previousCpuSampleTimestamp;

    internal SettingsWindow(XiaoKSettings settings)
    {
        InitializeComponent();
        _original = settings;
        using (var process = Process.GetCurrentProcess()) _previousCpuTime = process.TotalProcessorTime;
        _previousCpuSampleTimestamp = Stopwatch.GetTimestamp();
        DataRootBox.Text = settings.DataRoot;
        ModelRootBox.Text = settings.ModelRoot;
        EvaluationRootBox.Text = settings.EvaluationRoot;
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

    private void BrowseEvaluationRoot_Click(object sender, RoutedEventArgs e) => BrowseInto(EvaluationRootBox, "选择脱敏评测样本目录");

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

    private async void RefreshResources_Click(object sender, RoutedEventArgs e)
    {
        RefreshResourcesButton.IsEnabled = false;
        try
        {
            var processLine = LocalResourceReader.GetHostProcessUsage(_previousCpuTime, _previousCpuSampleTimestamp);
            using (var process = Process.GetCurrentProcess()) _previousCpuTime = process.TotalProcessorTime;
            _previousCpuSampleTimestamp = Stopwatch.GetTimestamp();
            var gpuLines = await Task.WhenAll(
                LocalResourceReader.GetNvidiaMemoryUsageAsync(),
                Task.Run(DxgiProcessMemoryReader.ReadNvidiaHostMemoryUsage));
            ResourceStatusText.Text = $"{processLine}\n{gpuLines[0]}\n{gpuLines[1]}\nHost 读数不包含独立启动的模型/语音服务进程；驱动与 WDDM 分配会使 DXGI 值与任务管理器略有差异。";
        }
        finally
        {
            RefreshResourcesButton.IsEnabled = true;
        }
    }

    private async void ClearSamples_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var dataRoot = ValidateLocalDirectory(DataRootBox.Text, "用户数据目录");
            var modelRoot = ValidateLocalDirectory(ModelRootBox.Text, "模型目录");
            var evaluationRoot = ValidateLocalDirectory(EvaluationRootBox.Text, "脱敏评测样本目录");
            EnsureSeparateRoots(dataRoot, modelRoot, evaluationRoot);
            var preview = EvaluationSampleCleanup.Preview(evaluationRoot);
            if (preview.Files.Count == 0)
            {
                PrivacyStatusText.Text = preview.IgnoredEntries == 0
                    ? "评测目录中没有可清理的顶层文件。"
                    : $"没有可清理的顶层普通文件；{preview.IgnoredEntries} 个子目录、链接或只读文件会保留。";
                return;
            }

            var sizeMiB = preview.TotalBytes / (1024d * 1024d);
            var confirmation = System.Windows.MessageBox.Show(
                this,
                $"将从以下目录删除 {preview.Files.Count} 个顶层普通文件（约 {sizeMiB:F1} MiB）：\n\n{preview.RootPath}\n\n子目录、链接、只读文件和任务记录会保留。此操作无法撤销。要继续吗？",
                "确认清理评测样本",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning,
                MessageBoxResult.No);
            if (confirmation != MessageBoxResult.Yes) return;

            ClearSamplesButton.IsEnabled = false;
            PrivacyStatusText.Text = "正在清理已确认的评测样本…";
            var deleted = await Task.Run(() => EvaluationSampleCleanup.DeleteIfUnchanged(preview));
            PrivacyStatusText.Text = $"已删除 {deleted} 个评测样本文件；忽略的目录、链接和只读文件仍保留。";
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or SecurityException or InvalidOperationException or NotSupportedException)
        {
            PrivacyStatusText.Text = ex.Message;
        }
        finally
        {
            ClearSamplesButton.IsEnabled = true;
        }
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
                EvaluationRoot = ValidateLocalDirectory(EvaluationRootBox.Text, "脱敏评测样本目录"),
                InferenceEndpoint = ValidateLoopbackEndpoint(InferenceEndpointBox.Text)
            };
            EnsureSeparateRoots(updated.DataRoot, updated.ModelRoot, updated.EvaluationRoot);

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
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or SecurityException or InvalidOperationException or NotSupportedException)
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

    private static void EnsureSeparateRoots(string dataRoot, string modelRoot, string evaluationRoot)
    {
        if (PathsOverlap(dataRoot, modelRoot) || PathsOverlap(dataRoot, evaluationRoot) || PathsOverlap(modelRoot, evaluationRoot))
            throw new ArgumentException("数据、模型和评测目录必须互相独立，避免误删任务数据或模型文件。");
    }

    private static bool PathsOverlap(string first, string second) => IsSameOrChildPath(first, second) || IsSameOrChildPath(second, first);

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
