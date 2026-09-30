using System.IO;
using System.Security;
using System.Diagnostics;
using System.Collections.ObjectModel;
using System.Windows;
using Forms = System.Windows.Forms;
using XiaoK.Core;

namespace XiaoK.Host;

public partial class SettingsWindow : Window
{
    private readonly XiaoKSettings _original;
    private readonly Func<Task<string>> _requestNotificationAccess;
    private readonly Func<CancellationToken, Task<IReadOnlyList<ContactReplyStylePreference>>> _loadContactReplyStyles;
    private readonly Func<IEnumerable<ContactReplyStylePreference>, CancellationToken, Task> _replaceContactReplyStyles;
    private IReadOnlyList<ContactReplyStylePreference> _loadedContactReplyStyles = [];
    private bool _startupWasEnabled;
    private bool _startupStateLoaded;
    private bool _contactStylesLoaded;
    private readonly ObservableCollection<ContactReplyStylePreference> _contactReplyStyles;
    private TimeSpan _previousCpuTime;
    private long _previousCpuSampleTimestamp;

    internal SettingsWindow(XiaoKSettings settings, WindowsNotificationMonitor notificationMonitor,
        Func<CancellationToken, Task<IReadOnlyList<ContactReplyStylePreference>>> loadContactReplyStyles,
        Func<IEnumerable<ContactReplyStylePreference>, CancellationToken, Task> replaceContactReplyStyles)
    {
        InitializeComponent();
        _original = settings;
        _contactReplyStyles = [];
        _loadContactReplyStyles = loadContactReplyStyles;
        _replaceContactReplyStyles = replaceContactReplyStyles;
        _requestNotificationAccess = notificationMonitor.RequestPermissionAsync;
        using (var process = Process.GetCurrentProcess()) _previousCpuTime = process.TotalProcessorTime;
        _previousCpuSampleTimestamp = Stopwatch.GetTimestamp();
        DataRootBox.Text = settings.DataRoot;
        ModelRootBox.Text = settings.ModelRoot;
        EvaluationRootBox.Text = settings.EvaluationRoot;
        CodeProjectRootBox.Text = settings.CodeProjectRoot;
        CodeWorkspaceRootBox.Text = settings.CodeWorkspaceRoot;
        InferenceEndpointBox.Text = settings.InferenceEndpoint;
        MonitorWeChatCheck.IsChecked = settings.MonitorWeChatNotifications;
        MonitorQQCheck.IsChecked = settings.MonitorQQNotifications;
        WeChatAppIdsBox.Text = string.Join(Environment.NewLine, settings.WeChatPublisherAppIds);
        QQAppIdsBox.Text = string.Join(Environment.NewLine, settings.QQPublisherAppIds);
        NotificationStatusText.Text = notificationMonitor.Status;
        ContactStylesList.ItemsSource = _contactReplyStyles;
        ContactStyleBox.ItemsSource = ContactReplyStyleCatalog.Options;
        ContactStyleBox.DisplayMemberPath = nameof(ContactReplyStyleOption.DisplayName);
        ContactStyleBox.SelectedValuePath = nameof(ContactReplyStyleOption.Id);
        ContactStyleBox.SelectedValue = ContactReplyStyleCatalog.DefaultStyleId;
        SetContactStyleControlsEnabled(false);
        ContactStyleStatusText.Text = "正在从本机 SQLite 数据库读取联系人回复风格…";

        StartupCheck.IsEnabled = false;
        StartupStatusText.Text = "正在读取 Windows 登录启动状态…";
    }

    private async void SettingsWindow_Loaded(object sender, RoutedEventArgs e)
    {
        try
        {
            var startup = await LoginStartupRegistration.ReadAsync();
            _startupWasEnabled = startup.IsEnabled;
            _startupStateLoaded = true;
            StartupCheck.IsChecked = startup.IsEnabled;
            StartupCheck.IsEnabled = startup.IsSupported;
            StartupStatusText.Text = startup.Status;
        }
        catch (Exception ex) when (ex is InvalidOperationException or UnauthorizedAccessException or SecurityException
            or System.Runtime.InteropServices.COMException or NotSupportedException)
        {
            _startupStateLoaded = false;
            StartupCheck.IsChecked = false;
            StartupCheck.IsEnabled = false;
            StartupStatusText.Text = "无法读取 Windows 登录启动状态；为避免误改系统设置，此项已停用。";
            StatusText.Text = ex.Message;
        }

        try
        {
            _loadedContactReplyStyles = await _loadContactReplyStyles(CancellationToken.None);
            _contactReplyStyles.Clear();
            foreach (var preference in _loadedContactReplyStyles) _contactReplyStyles.Add(preference);
            _contactStylesLoaded = true;
            SetContactStyleControlsEnabled(true);
            ContactStyleStatusText.Text = "联系人偏好已从本机 SQLite 加载；更改后点击底部“保存”生效。";
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or InvalidDataException or UnauthorizedAccessException or SecurityException
            or InvalidOperationException or NotSupportedException)
        {
            _contactStylesLoaded = false;
            SetContactStyleControlsEnabled(false);
            ContactStyleStatusText.Text = "无法读取 SQLite 联系人偏好；为避免覆盖现有数据，此区域已停用。";
            StatusText.Text = ex.Message;
        }
    }

    private void SetContactStyleControlsEnabled(bool enabled)
    {
        ContactStyleNameBox.IsEnabled = enabled;
        ContactStyleBox.IsEnabled = enabled;
        AddContactStyleButton.IsEnabled = enabled;
        RemoveContactStyleButton.IsEnabled = enabled;
        ContactStylesList.IsEnabled = enabled;
    }

    private void BrowseDataRoot_Click(object sender, RoutedEventArgs e) => BrowseInto(DataRootBox, "选择用户数据目录");

    private void BrowseModelRoot_Click(object sender, RoutedEventArgs e) => BrowseInto(ModelRootBox, "选择本地模型目录");

    private void BrowseEvaluationRoot_Click(object sender, RoutedEventArgs e) => BrowseInto(EvaluationRootBox, "选择脱敏评测样本目录");

    private void BrowseCodeProject_Click(object sender, RoutedEventArgs e) => BrowseInto(CodeProjectRootBox, "选择本地编程项目目录", allowNewFolder: false);

    private void BrowseCodeWorkspace_Click(object sender, RoutedEventArgs e) => BrowseInto(CodeWorkspaceRootBox, "选择隔离编程工作区目录");

    private void BrowseInto(System.Windows.Controls.TextBox target, string description, bool allowNewFolder = true)
    {
        using var picker = new Forms.FolderBrowserDialog
        {
            Description = description,
            UseDescriptionForTitle = true,
            ShowNewFolderButton = allowNewFolder,
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

    private async void RequestNotificationAccess_Click(object sender, RoutedEventArgs e)
    {
        RequestNotificationAccessButton.IsEnabled = false;
        try
        {
            NotificationStatusText.Text = await _requestNotificationAccess();
        }
        finally
        {
            RequestNotificationAccessButton.IsEnabled = true;
        }
    }

    private async void ClearSamples_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var dataRoot = ValidateLocalDirectory(DataRootBox.Text, "用户数据目录");
            var modelRoot = ValidateLocalDirectory(ModelRootBox.Text, "模型目录");
            var evaluationRoot = ValidateLocalDirectory(EvaluationRootBox.Text, "脱敏评测样本目录");
            var codeWorkspaceRoot = ValidateLocalDirectory(CodeWorkspaceRootBox.Text, "隔离工作区目录");
            EnsureSeparateRoots(dataRoot, modelRoot, evaluationRoot, codeWorkspaceRoot);
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

    private void AddContactStyle_Click(object sender, RoutedEventArgs e)
    {
        if (!ContactReplyStyleCatalog.TryNormalizeContactName(ContactStyleNameBox.Text, out var contactName))
        {
            ContactStyleStatusText.Text = "名称不能为空，最长80个字符，且不能包含控制字符或冒号。";
            return;
        }
        if (ContactStyleBox.SelectedValue is not string styleId || !ContactReplyStyleCatalog.IsSupportedStyle(styleId))
        {
            ContactStyleStatusText.Text = "请选择列表中的固定回复风格。";
            return;
        }

        var existing = _contactReplyStyles.FirstOrDefault(item =>
            string.Equals(item.ContactName, contactName, StringComparison.OrdinalIgnoreCase));
        if (existing is not null) _contactReplyStyles.Remove(existing);
        else if (_contactReplyStyles.Count >= 200)
        {
            ContactStyleStatusText.Text = "最多保存200条联系人风格偏好。";
            return;
        }

        var preference = new ContactReplyStylePreference(contactName, styleId,
            ContactReplyStyleCatalog.UserConfirmedSource, DateTimeOffset.UtcNow);
        _contactReplyStyles.Insert(0, preference);
        ContactStylesList.SelectedItem = preference;
        ContactStyleNameBox.Clear();
        ContactStyleStatusText.Text = "风格已记录在待保存列表中；点击设置底部的“保存”后生效。";
    }

    private void RemoveContactStyle_Click(object sender, RoutedEventArgs e)
    {
        if (ContactStylesList.SelectedItem is not ContactReplyStylePreference preference)
        {
            ContactStyleStatusText.Text = "请先选择要删除的联系人风格偏好。";
            return;
        }
        _contactReplyStyles.Remove(preference);
        ContactStyleStatusText.Text = "已从待保存列表移除；点击设置底部的“保存”后删除生效。";
    }

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        if (!_startupStateLoaded)
        {
            StatusText.Text = "Windows 登录启动状态尚未确认；请等待状态读取完成后再保存。";
            return;
        }

        var startupRequested = StartupCheck.IsEnabled && StartupCheck.IsChecked == true;
        var startupChanged = StartupCheck.IsEnabled && startupRequested != _startupWasEnabled;

        try
        {
            var updated = _original with
            {
                DataRoot = ValidateLocalDirectory(DataRootBox.Text, "用户数据目录"),
                ModelRoot = ValidateLocalDirectory(ModelRootBox.Text, "模型目录"),
                EvaluationRoot = ValidateLocalDirectory(EvaluationRootBox.Text, "脱敏评测样本目录"),
                CodeProjectRoot = ValidateOptionalProjectDirectory(CodeProjectRootBox.Text),
                CodeWorkspaceRoot = ValidateLocalDirectory(CodeWorkspaceRootBox.Text, "隔离工作区目录"),
                InferenceEndpoint = ValidateLoopbackEndpoint(InferenceEndpointBox.Text),
                MonitorWeChatNotifications = MonitorWeChatCheck.IsChecked == true,
                MonitorQQNotifications = MonitorQQCheck.IsChecked == true,
                WeChatPublisherAppIds = ParseAppIds(WeChatAppIdsBox.Text, "微信"),
                QQPublisherAppIds = ParseAppIds(QQAppIdsBox.Text, "QQ")
            };
            EnsureSeparateRoots(updated.DataRoot, updated.ModelRoot, updated.EvaluationRoot, updated.CodeWorkspaceRoot);
            if (updated.CodeProjectRoot.Length > 0 && PathsOverlap(updated.CodeProjectRoot, updated.CodeWorkspaceRoot))
                throw new ArgumentException("隔离工作区目录不能与编程项目目录相同或互相包含。");

            if (startupChanged)
            {
                var startup = await LoginStartupRegistration.SetEnabledAsync(startupRequested);
                StartupStatusText.Text = startup.Status;
                if (startup.IsEnabled != startupRequested)
                {
                    StartupCheck.IsChecked = startup.IsEnabled;
                    StartupCheck.IsEnabled = startup.IsSupported;
                    StatusText.Text = "Windows 未应用所请求的登录启动状态；其他设置尚未保存。请按上方说明处理后重试。";
                    return;
                }
            }

            var contactStylesSaved = false;
            try
            {
                if (_contactStylesLoaded)
                {
                    await _replaceContactReplyStyles(_contactReplyStyles.ToArray(), CancellationToken.None);
                    contactStylesSaved = true;
                }
                updated.Save();
            }
            catch
            {
                if (contactStylesSaved)
                {
                    var rollbackFailed = false;
                    try { await _replaceContactReplyStyles(_loadedContactReplyStyles, CancellationToken.None); }
                    catch (Exception rollbackError)
                    {
                        rollbackFailed = true;
                        ContactStyleStatusText.Text = "设置文件保存失败，且联系人偏好回滚未完成；请勿继续编辑，先检查本机数据库备份。";
                        StatusText.Text = rollbackError.Message;
                    }
                    if (!rollbackFailed)
                        ContactStyleStatusText.Text = "设置文件保存失败；联系人偏好已回滚到打开设置页时的内容。";
                }
                throw;
            }

            if (_contactStylesLoaded) _loadedContactReplyStyles = _contactReplyStyles.ToArray();
            if (startupChanged) _startupWasEnabled = startupRequested;
            DialogResult = true;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or SecurityException
            or InvalidOperationException or NotSupportedException or System.Runtime.InteropServices.COMException)
        {
            if (startupChanged)
            {
                try
                {
                    var rollback = await LoginStartupRegistration.SetEnabledAsync(_startupWasEnabled);
                    StartupStatusText.Text = rollback.Status;
                }
                catch (Exception rollbackError)
                {
                    StartupStatusText.Text = "保存失败，且登录启动状态回滚未完成；请手动核对登录启动设置。";
                    StatusText.Text = rollbackError.Message;
                }
            }
            StatusText.Text = ex.Message;
        }
    }

    private static List<string> ParseAppIds(string value, string application)
    {
        var ids = value.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (ids.Any(id => id.Length > 256 || !id.Contains('!')))
            throw new ArgumentException($"{application} 发布者 AUMID 格式无效；每行填写一个完整的 PackageFamily!ApplicationId。", nameof(value));
        return ids;
    }

    private static string ValidateLocalDirectory(string value, string label, bool allowRepository = false)
    {
        var trimmed = value.Trim();
        if (!Path.IsPathFullyQualified(trimmed) || trimmed.StartsWith("\\\\", StringComparison.Ordinal))
            throw new ArgumentException($"{label}必须是本机上的完整目录路径。", nameof(value));

        var fullPath = Path.GetFullPath(trimmed).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var pathRoot = Path.GetPathRoot(fullPath)?.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (string.Equals(fullPath, pathRoot, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException($"{label}不能直接指向磁盘根目录。", nameof(value));

        var repository = XiaoKSettings.FindWorkspace(AppContext.BaseDirectory);
        if (!allowRepository && repository is not null && IsSameOrChildPath(fullPath, repository))
            throw new ArgumentException($"{label}不能放在 Git 仓库内。", nameof(value));

        return fullPath;
    }

    private static string ValidateOptionalProjectDirectory(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "";
        var path = ValidateLocalDirectory(value, "编程项目目录", allowRepository: true);
        if (!Directory.Exists(path)) throw new DirectoryNotFoundException("所选编程项目目录不存在。");
        return path;
    }

    private static bool IsSameOrChildPath(string path, string root)
    {
        var normalizedRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return path.Equals(normalizedRoot, StringComparison.OrdinalIgnoreCase)
            || path.StartsWith(normalizedRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            || path.StartsWith(normalizedRoot + Path.AltDirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    private static void EnsureSeparateRoots(string dataRoot, string modelRoot, string evaluationRoot, string codeWorkspaceRoot)
    {
        var roots = new[] { dataRoot, modelRoot, evaluationRoot, codeWorkspaceRoot };
        for (var i = 0; i < roots.Length; i++)
        for (var j = i + 1; j < roots.Length; j++)
            if (PathsOverlap(roots[i], roots[j]))
                throw new ArgumentException("数据、模型、评测和隔离工作区目录必须互相独立，避免混放或误删。");
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
