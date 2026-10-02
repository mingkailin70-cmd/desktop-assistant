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
    private static readonly HashSet<string> ConfigurableApplicationIds = new(StringComparer.OrdinalIgnoreCase)
        { "vscode", "edge", "wechat", "qq", "explorer" };
    private XiaoKSettings _original;
    private readonly Func<Task<string>> _requestNotificationAccess;
    private readonly Func<CancellationToken, Task<IReadOnlyList<ContactReplyStylePreference>>> _loadContactReplyStyles;
    private readonly Func<IEnumerable<ContactReplyStylePreference>, CancellationToken, Task> _replaceContactReplyStyles;
    private readonly Func<string, CancellationToken, Task<string>> _createDatabaseBackup;
    private readonly Func<string, CancellationToken, Task<string>> _restoreDatabaseBackup;
    private readonly Func<int, CancellationToken, Task<IReadOnlyList<ApprovalAuditRecord>>> _getRecentApprovalAudit;
    private readonly Func<CancellationToken, Task<LocalDataCleanupPreview>> _getLocalDataCleanupPreview;
    private readonly Func<LocalDataCleanupPreview, CancellationToken, Task<LocalDataCleanupResult>> _clearLocalData;
    private readonly string _activeDatabasePath;
    private IReadOnlyList<ContactReplyStylePreference> _loadedContactReplyStyles = [];
    private bool _startupWasEnabled;
    private bool _startupStateLoaded;
    private bool _contactStylesLoaded;
    private readonly ObservableCollection<ContactReplyStylePreference> _contactReplyStyles;
    private TimeSpan _previousCpuTime;
    private long _previousCpuSampleTimestamp;

    internal SettingsWindow(XiaoKSettings settings, WindowsNotificationMonitor notificationMonitor,
        Func<CancellationToken, Task<IReadOnlyList<ContactReplyStylePreference>>> loadContactReplyStyles,
        Func<IEnumerable<ContactReplyStylePreference>, CancellationToken, Task> replaceContactReplyStyles,
        string activeDatabasePath,
        Func<string, CancellationToken, Task<string>> createDatabaseBackup,
        Func<string, CancellationToken, Task<string>> restoreDatabaseBackup,
        Func<int, CancellationToken, Task<IReadOnlyList<ApprovalAuditRecord>>> getRecentApprovalAudit,
        Func<CancellationToken, Task<LocalDataCleanupPreview>> getLocalDataCleanupPreview,
        Func<LocalDataCleanupPreview, CancellationToken, Task<LocalDataCleanupResult>> clearLocalData)
    {
        InitializeComponent();
        _original = settings;
        _contactReplyStyles = [];
        _loadContactReplyStyles = loadContactReplyStyles;
        _replaceContactReplyStyles = replaceContactReplyStyles;
        _activeDatabasePath = activeDatabasePath;
        _createDatabaseBackup = createDatabaseBackup;
        _restoreDatabaseBackup = restoreDatabaseBackup;
        _getRecentApprovalAudit = getRecentApprovalAudit;
        _getLocalDataCleanupPreview = getLocalDataCleanupPreview;
        _clearLocalData = clearLocalData;
        _requestNotificationAccess = notificationMonitor.RequestPermissionAsync;
        using (var process = Process.GetCurrentProcess()) _previousCpuTime = process.TotalProcessorTime;
        _previousCpuSampleTimestamp = Stopwatch.GetTimestamp();
        DataRootBox.Text = settings.DataRoot;
        ActiveDatabasePathText.Text = activeDatabasePath;
        ModelRootBox.Text = settings.ModelRoot;
        EvaluationRootBox.Text = settings.EvaluationRoot;
        SearchRootsBox.Text = string.Join(Environment.NewLine, settings.SearchRoots.Select(root => root.Path));
        var vscode = settings.Applications.FirstOrDefault(app => app is not null && string.Equals(app.Id, "vscode", StringComparison.OrdinalIgnoreCase));
        var edge = settings.Applications.FirstOrDefault(app => app is not null && string.Equals(app.Id, "edge", StringComparison.OrdinalIgnoreCase));
        var wechat = settings.Applications.FirstOrDefault(app => app is not null && string.Equals(app.Id, "wechat", StringComparison.OrdinalIgnoreCase));
        var qq = settings.Applications.FirstOrDefault(app => app is not null && string.Equals(app.Id, "qq", StringComparison.OrdinalIgnoreCase));
        VscodeExecutableBox.Text = vscode?.Executable ?? string.Empty;
        VscodeProjectRootBox.Text = vscode?.WorkingDirectory ?? string.Empty;
        EdgeExecutableBox.Text = edge?.Executable ?? string.Empty;
        WeChatExecutableBox.Text = wechat?.Executable ?? string.Empty;
        QQExecutableBox.Text = qq?.Executable ?? string.Empty;
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

    private async void CreateDatabaseBackup_Click(object sender, RoutedEventArgs e)
    {
        var initialDirectory = Path.GetDirectoryName(_activeDatabasePath)!;
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "选择数据库备份位置",
            Filter = "小K SQLite 备份 (*.sqlite3)|*.sqlite3",
            DefaultExt = ".sqlite3",
            AddExtension = true,
            OverwritePrompt = false,
            InitialDirectory = Directory.Exists(initialDirectory) ? initialDirectory : Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            FileName = $"xiaok-backup-{DateTimeOffset.Now:yyyyMMdd-HHmmss}.sqlite3"
        };
        if (dialog.ShowDialog(this) != true) return;

        SetDatabaseMaintenanceButtonsEnabled(false);
        try
        {
            var backupPath = Path.GetFullPath(dialog.FileName);
            _ = ValidateLocalDirectory(Path.GetDirectoryName(backupPath)!, "数据库备份目录");
            var createdPath = await _createDatabaseBackup(backupPath, CancellationToken.None);
            DatabaseMaintenanceStatusText.Text = $"已创建包含任务状态、联系人偏好和审批审计的一致性备份：\n{createdPath}";
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or SecurityException
            or InvalidOperationException or NotSupportedException)
        {
            DatabaseMaintenanceStatusText.Text = $"备份失败；未覆盖现有备份：{ex.Message}";
        }
        finally { SetDatabaseMaintenanceButtonsEnabled(true); }
    }

    private async void RestoreDatabaseBackup_Click(object sender, RoutedEventArgs e)
    {
        var initialDirectory = Path.GetDirectoryName(_activeDatabasePath)!;
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "选择小K SQLite 备份（仅支持 v4）",
            Filter = "SQLite 备份 (*.sqlite3;*.bak)|*.sqlite3;*.bak",
            CheckFileExists = true,
            Multiselect = false,
            InitialDirectory = Directory.Exists(initialDirectory) ? initialDirectory : Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)
        };
        if (dialog.ShowDialog(this) != true) return;

        var selectedPath = Path.GetFullPath(dialog.FileName);
        if (string.Equals(selectedPath, Path.GetFullPath(_activeDatabasePath), StringComparison.OrdinalIgnoreCase))
        {
            DatabaseMaintenanceStatusText.Text = "请选择单独的 v4 备份文件；不能把活动数据库自身作为恢复源。";
            return;
        }
        var confirmation = System.Windows.MessageBox.Show(
            this,
            $"将用以下 SQLite v4 备份替换当前本地数据库：\n\n{selectedPath}\n\n当前数据库会先生成一个旁置保护备份。恢复后只恢复脱敏任务状态、联系人回复风格和审批审计；不会恢复或重放运行中的任务、命令或外发操作。操作期间不能有其他任务运行。要继续吗？",
            "确认恢复本地数据库",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No);
        if (confirmation != MessageBoxResult.Yes) return;

        SetDatabaseMaintenanceButtonsEnabled(false);
        try
        {
            DatabaseMaintenanceStatusText.Text = "正在验证备份并创建当前数据库保护副本…";
            var recoveryPath = await _restoreDatabaseBackup(selectedPath, CancellationToken.None);
            if (_contactStylesLoaded)
            {
                try
                {
                    _loadedContactReplyStyles = await _loadContactReplyStyles(CancellationToken.None);
                    _contactReplyStyles.Clear();
                    foreach (var preference in _loadedContactReplyStyles) _contactReplyStyles.Add(preference);
                }
                catch (Exception refreshError) when (refreshError is IOException or InvalidDataException or InvalidOperationException)
                {
                    DatabaseMaintenanceStatusText.Text = $"数据库已恢复，保护副本在 {recoveryPath}；但偏好列表刷新失败，请重新打开设置核对：{refreshError.Message}";
                    return;
                }
            }
            DatabaseMaintenanceStatusText.Text = $"数据库已恢复。恢复前的活动数据库保护副本保存在：\n{recoveryPath}\n没有执行或重放任何旧任务。";
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or InvalidDataException or UnauthorizedAccessException or SecurityException
            or InvalidOperationException or NotSupportedException)
        {
            DatabaseMaintenanceStatusText.Text = $"恢复未完成：{ex.Message}";
        }
        finally { SetDatabaseMaintenanceButtonsEnabled(true); }
    }

    private async void ViewApprovalAudit_Click(object sender, RoutedEventArgs e)
    {
        ViewApprovalAuditButton.IsEnabled = false;
        try
        {
            var records = await _getRecentApprovalAudit(100, CancellationToken.None);
            new ApprovalAuditWindow(records) { Owner = this }.ShowDialog();
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or SecurityException
            or InvalidOperationException or NotSupportedException)
        {
            DatabaseMaintenanceStatusText.Text = $"无法读取审批审计记录：{ex.Message}";
        }
        finally { ViewApprovalAuditButton.IsEnabled = true; }
    }

    private async void ClearLocalData_Click(object sender, RoutedEventArgs e)
    {
        SetDatabaseMaintenanceButtonsEnabled(false);
        try
        {
            var preview = await _getLocalDataCleanupPreview(CancellationToken.None);
            var database = preview.Database;
            var hasData = database.TaskRows > 0 || database.ContactPreferenceRows > 0 || database.ApprovalAuditRows > 0
                || preview.LegacySettings.HasContactStylesProperty || preview.ManagedFiles.Files.Count > 0;
            if (!hasData)
            {
                DatabaseMaintenanceStatusText.Text = preview.ManagedFiles.SkippedEntries == 0
                    ? "没有发现可清理的本地历史记录或小K管理文件。"
                    : $"没有可清理的数据；{preview.ManagedFiles.SkippedEntries} 个链接或只读文件按安全规则保留。";
                return;
            }

            var confirmation = System.Windows.MessageBox.Show(
                this,
                $"将清除本地数据库中的任务记录（{database.TaskRows}）、联系人回复偏好（{database.ContactPreferenceRows}）和审批记录（{database.ApprovalAuditRows}）。"
                    + $"\n旧设置文件中检测到 {preview.LegacySettings.ContactStyleRows} 条联系人偏好副本；数据目录内有 {preview.ManagedFiles.Files.Count} 个小K管理的旧任务/备份/暂存文件（{preview.ManagedFiles.TotalBytes / (1024d * 1024d):F1} MiB）。"
                    + $"\n当前数据目录：{Path.GetDirectoryName(_activeDatabasePath)}"
                    + $"\n\n此操作不可撤销。迁移标记会保留，避免重启后从旧文件重新导入。只会从当前设置中移除联系人偏好字段；其他设置、隔离编程工作区、评测样本和用户另存到其他位置或自定义名称的备份不会删除。{(preview.ManagedFiles.SkippedEntries == 0 ? "" : $"\n另有 {preview.ManagedFiles.SkippedEntries} 个链接或只读文件会保留。")}\n\n要继续吗？",
                "确认清理小K本地历史数据",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning,
                MessageBoxResult.No);
            if (confirmation != MessageBoxResult.Yes) return;

            DatabaseMaintenanceStatusText.Text = "正在清除数据库记录并清理旧迁移副本…";
            var result = await _clearLocalData(preview, CancellationToken.None);
            _original = _original with { ContactReplyStyles = [] };
            if (_contactStylesLoaded)
            {
                try
                {
                    _loadedContactReplyStyles = await _loadContactReplyStyles(CancellationToken.None);
                    _contactReplyStyles.Clear();
                    foreach (var preference in _loadedContactReplyStyles) _contactReplyStyles.Add(preference);
                    ContactStyleNameBox.Clear();
                    ContactStylesList.SelectedItem = null;
                }
                catch (Exception refreshError) when (refreshError is IOException or InvalidDataException or InvalidOperationException)
                {
                    DatabaseMaintenanceStatusText.Text = $"历史数据已清除，但联系人偏好列表刷新失败；请重新打开设置核对：{refreshError.Message}";
                    return;
                }
            }

            var status = result.DatabaseCompacted
                ? "数据库记录已清除，WAL已检查点并完成空间整理。"
                : "数据库逻辑记录已清除，但 WAL 检查点或空间整理未完成；关闭其他数据库查看工具后可重试清理。";
            status += result.LegacySettingsPropertyWasPresent
                ? result.LegacySettingsPropertyRemoved ? "旧设置中的联系人偏好副本已移除。"
                    : $"旧设置偏好副本未能移除：{result.LegacySettingsCleanupError ?? "请检查设置文件后重试。"}"
                : "没有旧设置联系人偏好副本。";
            status += $"已删除 {result.DeletedManagedFiles} 个小K管理文件。";
            if (result.SkippedManagedFiles > 0) status += $"保留了 {result.SkippedManagedFiles} 个只读或链接文件。";
            if (result.FailedManagedFileNames.Count > 0)
                status += "未能删除：" + string.Join("、", result.FailedManagedFileNames) + "。可关闭占用程序后重试。";
            if (result.ManagedFilePlanChanged)
                status += "数据目录在确认后发生变化；未能按原预览清理部分文件，请重新预览后重试。";
            status += "用户另存到其他位置的备份不会自动删除。";
            DatabaseMaintenanceStatusText.Text = status;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or SecurityException
            or InvalidDataException or InvalidOperationException or NotSupportedException or System.Text.Json.JsonException)
        {
            DatabaseMaintenanceStatusText.Text = $"没有完成本地历史清理：{ex.Message}";
        }
        finally { SetDatabaseMaintenanceButtonsEnabled(true); }
    }

    private void SetDatabaseMaintenanceButtonsEnabled(bool enabled)
    {
        CreateDatabaseBackupButton.IsEnabled = enabled;
        RestoreDatabaseBackupButton.IsEnabled = enabled;
        ViewApprovalAuditButton.IsEnabled = enabled;
        ClearLocalDataButton.IsEnabled = enabled;
    }

    private void BrowseDataRoot_Click(object sender, RoutedEventArgs e) => BrowseInto(DataRootBox, "选择用户数据目录");

    private void BrowseModelRoot_Click(object sender, RoutedEventArgs e) => BrowseInto(ModelRootBox, "选择本地模型目录");

    private void BrowseEvaluationRoot_Click(object sender, RoutedEventArgs e) => BrowseInto(EvaluationRootBox, "选择脱敏评测样本目录");

    private void BrowseVscodeExecutable_Click(object sender, RoutedEventArgs e) =>
        BrowseExecutableInto(VscodeExecutableBox, "选择 VS Code 程序", "Code.exe");

    private void BrowseVscodeProject_Click(object sender, RoutedEventArgs e) =>
        BrowseInto(VscodeProjectRootBox, "选择包含 XiaoK.sln 的本地项目目录", allowNewFolder: false);

    private void BrowseEdgeExecutable_Click(object sender, RoutedEventArgs e) =>
        BrowseExecutableInto(EdgeExecutableBox, "选择 Microsoft Edge 程序", "msedge.exe");

    private void BrowseWeChatExecutable_Click(object sender, RoutedEventArgs e) =>
        BrowseExecutableInto(WeChatExecutableBox, "选择微信程序", "Weixin.exe");

    private void BrowseQQExecutable_Click(object sender, RoutedEventArgs e) =>
        BrowseExecutableInto(QQExecutableBox, "选择 QQ 程序", "QQ.exe");

    private static void BrowseExecutableInto(System.Windows.Controls.TextBox target, string title, string expectedFileName)
    {
        var currentDirectory = Path.GetDirectoryName(target.Text.Trim());
        using var picker = new Forms.OpenFileDialog
        {
            Title = title,
            Filter = $"{expectedFileName}|{expectedFileName}",
            CheckFileExists = true,
            CheckPathExists = true,
            Multiselect = false,
            InitialDirectory = !string.IsNullOrWhiteSpace(currentDirectory) && Directory.Exists(currentDirectory)
                ? currentDirectory
                : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
        };
        if (picker.ShowDialog() == Forms.DialogResult.OK) target.Text = picker.FileName;
    }

    private void BrowseCodeProject_Click(object sender, RoutedEventArgs e) => BrowseInto(CodeProjectRootBox, "选择本地编程项目目录", allowNewFolder: false);

    private void BrowseCodeWorkspace_Click(object sender, RoutedEventArgs e) => BrowseInto(CodeWorkspaceRootBox, "选择隔离编程工作区目录");

    private void AddSearchRoot_Click(object sender, RoutedEventArgs e)
    {
        var existing = SearchRootsBox.Text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var initialPath = existing.FirstOrDefault(Directory.Exists) ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        using var picker = new Forms.FolderBrowserDialog
        {
            Description = "选择要加入文件搜索范围的本机目录",
            UseDescriptionForTitle = true,
            ShowNewFolderButton = false,
            SelectedPath = initialPath
        };
        if (picker.ShowDialog() != Forms.DialogResult.OK) return;
        if (existing.Contains(picker.SelectedPath, StringComparer.OrdinalIgnoreCase)) return;
        SearchRootsBox.Text = string.Join(Environment.NewLine, existing.Append(picker.SelectedPath));
    }

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
                SearchRoots = LocalSearchRootPolicy.Parse(SearchRootsBox.Text)
                    .Select(root => new RootSetting(root.Id, root.Path)).ToList(),
                Applications = BuildDesktopApplications(),
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
        if (ids.Any(id => !AppUserModelIdPolicy.IsValid(id)))
            throw new ArgumentException($"{application} 通知来源 AUMID 无效；每行填写一个完整标识（最多 {AppUserModelIdPolicy.MaximumLength} 个字符），并在读取真实通知元数据后核实。", nameof(value));
        return ids;
    }

    private List<AppSetting> BuildDesktopApplications()
    {
        var applications = _original.Applications
            .Where(app => app is not null && !ConfigurableApplicationIds.Contains(app.Id)).ToList();
        applications.Add(new AppSetting("explorer", "explorer.exe", null));

        var vscodePath = VscodeExecutableBox.Text.Trim();
        var vscodeProjectPath = VscodeProjectRootBox.Text.Trim();
        if (vscodePath.Length > 0 || vscodeProjectPath.Length > 0)
        {
            if (vscodePath.Length == 0 || vscodeProjectPath.Length == 0)
                throw new ArgumentException("VS Code 程序路径和小K项目目录必须同时填写；不使用时请同时留空。");
            var executable = LocalDesktopAppPathPolicy.ValidateExecutablePath(vscodePath, "Code.exe", "VS Code");
            applications.Add(new AppSetting("vscode", executable, ValidateVscodeProjectDirectory(vscodeProjectPath)));
        }

        AddOptionalApplication(applications, EdgeExecutableBox.Text, "msedge.exe", "edge", "Microsoft Edge");
        AddOptionalApplication(applications, WeChatExecutableBox.Text, "Weixin.exe", "wechat", "微信");
        AddOptionalApplication(applications, QQExecutableBox.Text, "QQ.exe", "qq", "QQ");
        return applications;
    }

    private static void AddOptionalApplication(List<AppSetting> applications, string configuredPath,
        string expectedFileName, string id, string displayName)
    {
        if (string.IsNullOrWhiteSpace(configuredPath)) return;
        var executable = LocalDesktopAppPathPolicy.ValidateExecutablePath(configuredPath, expectedFileName, displayName);
        applications.Add(new AppSetting(id, executable, null));
    }

    private static string ValidateVscodeProjectDirectory(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || !LocalSearchRootPolicy.IsLocalDrivePath(value.Trim()))
            throw new ArgumentException("VS Code 小K项目必须位于本机磁盘，并选择包含 XiaoK.sln 的目录。", nameof(value));
        var fullPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(value.Trim()));
        if (!Directory.Exists(fullPath)) throw new DirectoryNotFoundException("VS Code 小K项目目录不存在。");
        if (!File.Exists(Path.Combine(fullPath, "XiaoK.sln")))
            throw new FileNotFoundException("所选 VS Code 项目目录不包含 XiaoK.sln。", Path.Combine(fullPath, "XiaoK.sln"));
        return fullPath;
    }

    private static string ValidateLocalDirectory(string value, string label, bool allowRepository = false)
    {
        var trimmed = value.Trim();
        if (!LocalSearchRootPolicy.IsLocalDrivePath(trimmed))
            throw new ArgumentException($"{label}必须位于本机固定盘、可移动盘或 RAM 盘，不能使用 UNC 或映射网络盘。", nameof(value));

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
