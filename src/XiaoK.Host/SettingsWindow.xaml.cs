using System.ComponentModel;
using System.Security;
using System.Diagnostics;
using System.IO;
using System.Collections.ObjectModel;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using Forms = System.Windows.Forms;
using XiaoK.Adapters.Windows;
using XiaoK.Core;

namespace XiaoK.Host;

public partial class SettingsWindow : Window
{
    private sealed record NotificationPublisherCandidateChoice(string ApplicationId,
        NotificationPublisherCandidate Candidate, string DisplayText);

    private static readonly HashSet<string> ConfigurableApplicationIds = new(StringComparer.OrdinalIgnoreCase)
        { "vscode", "edge", "wechat", "qq", "explorer" };
    private XiaoKSettings _original;
    private readonly WindowsNotificationMonitor _notificationMonitor;
    private readonly Func<Task<string>> _requestNotificationAccess;
    private readonly Func<Task<string>> _inspectNotificationPublishers;
    private readonly Func<CancellationToken, Task<IReadOnlyList<ContactReplyStylePreference>>> _loadContactReplyStyles;
    private readonly Func<IEnumerable<ContactReplyStylePreference>, CancellationToken, Task> _replaceContactReplyStyles;
    private readonly Func<string, CancellationToken, Task<string>> _createDatabaseBackup;
    private readonly Func<string, CancellationToken, Task<string>> _restoreDatabaseBackup;
    private readonly Func<int, CancellationToken, Task<IReadOnlyList<ApprovalAuditRecord>>> _getRecentApprovalAudit;
    private readonly Func<CancellationToken, Task<LocalDataCleanupPreview>> _getLocalDataCleanupPreview;
    private readonly Func<LocalDataCleanupPreview, CancellationToken, Task<LocalDataCleanupResult>> _clearLocalData;
    private readonly Func<Task<LoginStartupSnapshot>> _readLoginStartup;
    private readonly string _activeDatabasePath;
    private IReadOnlyList<ContactReplyStylePreference> _loadedContactReplyStyles = [];
    private bool _startupWasEnabled;
    private bool _startupStateLoaded;
    private bool _contactStylesLoaded;
    private bool _saveInProgress;
    private bool _allowCloseAfterSave;
    private bool _publisherInspectionFailed;
    private readonly ObservableCollection<ContactReplyStylePreference> _contactReplyStyles;
    private readonly ObservableCollection<AppSetting> _customApplications;
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
        Func<LocalDataCleanupPreview, CancellationToken, Task<LocalDataCleanupResult>> clearLocalData,
        Func<Task<LoginStartupSnapshot>>? readLoginStartup = null,
        Func<Task<string>>? requestNotificationAccess = null,
        Func<Task<string>>? inspectNotificationPublishers = null)
    {
        InitializeComponent();
        _original = settings;
        _notificationMonitor = notificationMonitor;
        _notificationMonitor.NotificationSampleCaptured += OnNotificationSampleCaptured;
        _notificationMonitor.NotificationSampleStatusChanged += OnNotificationSampleStatusChanged;
        _contactReplyStyles = [];
        _customApplications = [];
        foreach (var app in settings.Applications.Where(app => app is not null
                     && CustomApplicationPolicy.IsValidId(app.Id)
                     && CustomApplicationPolicy.IsValidDisplayName(app.DisplayName)))
            _customApplications.Add(app);
        _loadContactReplyStyles = loadContactReplyStyles;
        _replaceContactReplyStyles = replaceContactReplyStyles;
        _activeDatabasePath = activeDatabasePath;
        _createDatabaseBackup = createDatabaseBackup;
        _restoreDatabaseBackup = restoreDatabaseBackup;
        _getRecentApprovalAudit = getRecentApprovalAudit;
        _getLocalDataCleanupPreview = getLocalDataCleanupPreview;
        _clearLocalData = clearLocalData;
        _readLoginStartup = readLoginStartup ?? LoginStartupRegistration.ReadAsync;
        _requestNotificationAccess = requestNotificationAccess ?? notificationMonitor.RequestPermissionAsync;
        _inspectNotificationPublishers = inspectNotificationPublishers ?? notificationMonitor.InspectRecentPublisherIdsAsync;
        using (var process = Process.GetCurrentProcess()) _previousCpuTime = process.TotalProcessorTime;
        _previousCpuSampleTimestamp = Stopwatch.GetTimestamp();
        DataRootBox.Text = settings.DataRoot;
        ActiveDatabasePathText.Text = activeDatabasePath;
        ModelRootBox.Text = settings.ModelRoot;
        VoiceEnvironmentRootBox.Text = settings.VoiceEnvironmentRoot;
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
        CustomApplicationsList.ItemsSource = _customApplications;
        CodeProjectRootBox.Text = settings.CodeProjectRoot;
        CodeWorkspaceRootBox.Text = settings.CodeWorkspaceRoot;
        InferenceEndpointBox.Text = settings.InferenceEndpoint;
        MonitorWeChatCheck.IsChecked = settings.MonitorWeChatNotifications;
        MonitorQQCheck.IsChecked = settings.MonitorQQNotifications;
        WakeWordCheck.IsChecked = settings.WakeWordEnabled;
        WakeWordEngineCombo.SelectedItem = WakeWordEngineCombo.Items
            .OfType<ComboBoxItem>()
            .FirstOrDefault(item => string.Equals(item.Tag?.ToString(), settings.WakeWordEngine, StringComparison.Ordinal));
        WeChatAppIdsBox.Text = string.Join(Environment.NewLine, settings.WeChatPublisherAppIds);
        QQAppIdsBox.Text = string.Join(Environment.NewLine, settings.QQPublisherAppIds);
        NotificationStatusText.Text = notificationMonitor.Status;
        NotificationSampleStatusText.Text = notificationMonitor.NotificationSampleStatus;
        NotificationSamplesBox.Text = FormatNotificationSamples(notificationMonitor.NotificationSamples);
        RefreshNotificationSamplePublisherCandidates();
        UpdateNotificationSampleButtons();
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
            var startup = await _readLoginStartup();
            _startupWasEnabled = startup.IsEnabled;
            _startupStateLoaded = true;
            StartupCheck.IsChecked = startup.IsEnabled;
            StartupCheck.IsEnabled = startup.IsSupported;
            StartupStatusText.Text = startup.Status;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not AccessViolationException)
        {
            _startupStateLoaded = false;
            StartupCheck.IsChecked = false;
            StartupCheck.IsEnabled = false;
            StartupStatusText.Text = "无法读取 Windows 登录启动状态；为避免误改系统设置，此项已停用。";
            StatusText.Text = "无法确认 Windows 登录启动状态；设置尚未保存，请重新打开设置页后重试。";
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
        catch (Exception ex) when (ex is not OutOfMemoryException and not AccessViolationException)
        {
            _contactStylesLoaded = false;
            SetContactStyleControlsEnabled(false);
            ContactStyleStatusText.Text = "无法读取 SQLite 联系人偏好；为避免覆盖现有数据，此区域已停用。";
            StatusText.Text = "联系人回复风格暂不可用；其他设置仍可继续保存。";
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

    private static bool IsNonFatalSettingsException(Exception exception) =>
        exception is not OutOfMemoryException and not AccessViolationException;

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
        catch (Exception ex) when (IsNonFatalSettingsException(ex))
        {
            DatabaseMaintenanceStatusText.Text = "数据库备份失败；没有执行数据库恢复或清理。请检查目标目录权限和可用空间后重试。";
        }
        finally { SetDatabaseMaintenanceButtonsEnabled(true); }
    }

    private async void RestoreDatabaseBackup_Click(object sender, RoutedEventArgs e)
    {
        var initialDirectory = Path.GetDirectoryName(_activeDatabasePath)!;
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "选择小K SQLite 备份（仅支持 v6）",
            Filter = "SQLite 备份 (*.sqlite3;*.bak)|*.sqlite3;*.bak",
            CheckFileExists = true,
            Multiselect = false,
            InitialDirectory = Directory.Exists(initialDirectory) ? initialDirectory : Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)
        };
        if (dialog.ShowDialog(this) != true) return;

        var selectedPath = Path.GetFullPath(dialog.FileName);
        if (string.Equals(selectedPath, Path.GetFullPath(_activeDatabasePath), StringComparison.OrdinalIgnoreCase))
        {
            DatabaseMaintenanceStatusText.Text = "请选择单独的 v9 备份文件；不能把活动数据库自身作为恢复源。";
            return;
        }
        var confirmation = System.Windows.MessageBox.Show(
            this,
            $"将用以下 SQLite v9 备份替换当前本地数据库：\n\n{selectedPath}\n\n当前数据库会先生成一个旁置保护备份。恢复后只恢复脱敏任务状态、联系人回复风格、审批审计和固定执行检查点；不会恢复或重放运行中的任务、命令或外发操作。操作期间不能有其他任务运行。要继续吗？",
            "确认恢复本地数据库",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No);
        if (confirmation != MessageBoxResult.Yes) return;

        SetDatabaseMaintenanceButtonsEnabled(false);
        var restoreCompleted = false;
        try
        {
            DatabaseMaintenanceStatusText.Text = "正在验证备份并创建当前数据库保护副本…";
            var recoveryPath = await _restoreDatabaseBackup(selectedPath, CancellationToken.None);
            restoreCompleted = true;
            if (_contactStylesLoaded)
            {
                try
                {
                    _loadedContactReplyStyles = await _loadContactReplyStyles(CancellationToken.None);
                    _contactReplyStyles.Clear();
                    foreach (var preference in _loadedContactReplyStyles) _contactReplyStyles.Add(preference);
                }
                catch (Exception refreshError) when (IsNonFatalSettingsException(refreshError))
                {
                    DatabaseMaintenanceStatusText.Text = $"数据库已恢复，保护副本保存在 {recoveryPath}；联系人偏好列表未能刷新，请关闭并重新打开设置页核对。";
                    return;
                }
            }
            DatabaseMaintenanceStatusText.Text = $"数据库已恢复。恢复前的活动数据库保护副本保存在：\n{recoveryPath}\n没有执行或重放任何旧任务。";
        }
        catch (Exception ex) when (IsNonFatalSettingsException(ex))
        {
            DatabaseMaintenanceStatusText.Text = restoreCompleted
                ? "数据库已恢复，但设置页后续状态未能完整刷新；请关闭并重新打开设置页核对。"
                : "数据库恢复未能完成或确认；请重新打开设置页核对当前状态。若已生成保护副本，请保留该副本；不要自动重试。";
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
        catch (Exception ex) when (IsNonFatalSettingsException(ex))
        {
            DatabaseMaintenanceStatusText.Text = "审批审计记录无法读取或显示；没有修改记录，请稍后重试。";
        }
        finally { ViewApprovalAuditButton.IsEnabled = true; }
    }

    private async void ClearLocalData_Click(object sender, RoutedEventArgs e)
    {
        var clearOperationStarted = false;
        SetDatabaseMaintenanceButtonsEnabled(false);
        try
        {
            var preview = await _getLocalDataCleanupPreview(CancellationToken.None);
            var database = preview.Database;
            var hasData = database.TaskRows > 0 || database.ContactPreferenceRows > 0 || database.ApprovalAuditRows > 0
                || preview.LegacySettings.HasContactStylesProperty || preview.LegacySettings.PetWindowPositionPropertyCount > 0
                || preview.ManagedFiles.Files.Count > 0;
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
                    + $"\n旧设置文件中检测到 {preview.LegacySettings.ContactStyleRows} 条联系人偏好副本和 {preview.LegacySettings.PetWindowPositionPropertyCount} 个旧桌宠坐标字段；数据目录内有 {preview.ManagedFiles.Files.Count} 个小K管理文件（含桌宠位置、旧任务、备份和暂存文件，共 {preview.ManagedFiles.TotalBytes / (1024d * 1024d):F1} MiB）。"
                    + $"\n当前数据目录：{Path.GetDirectoryName(_activeDatabasePath)}"
                    + $"\n\n此操作不可撤销。迁移标记会保留，避免重启后从旧文件重新导入。只会从当前设置中移除旧联系人偏好和桌宠坐标字段，并删除预览列出的受管文件；其他设置、隔离编程工作区、评测样本和用户另存到其他位置或自定义名称的备份不会删除。{(preview.ManagedFiles.SkippedEntries == 0 ? "" : $"\n另有 {preview.ManagedFiles.SkippedEntries} 个链接或只读文件会保留。")}\n\n要继续吗？",
                "确认清理小K本地历史数据",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning,
                MessageBoxResult.No);
            if (confirmation != MessageBoxResult.Yes) return;

            DatabaseMaintenanceStatusText.Text = "正在清除数据库记录并清理旧迁移副本…";
            clearOperationStarted = true;
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
                catch (Exception refreshError) when (IsNonFatalSettingsException(refreshError))
                {
                    DatabaseMaintenanceStatusText.Text = "本地历史数据已清理，但联系人偏好列表未能刷新；请关闭并重新打开设置页核对。";
                    return;
                }
            }

            var status = result.DatabaseCompacted
                ? "数据库记录已清除，WAL已检查点并完成空间整理。"
                : "数据库逻辑记录已清除，但 WAL 检查点或空间整理未完成；关闭其他数据库查看工具后可重试清理。";
            status += result.LegacySettingsPropertiesWerePresent
                ? result.LegacySettingsPropertyRemoved ? "旧设置中的临时偏好和桌宠坐标字段已移除。"
                    : $"旧设置中的临时字段未能移除：{result.LegacySettingsCleanupError ?? "请检查设置文件后重试。"}"
                : "没有检测到旧设置临时字段。";
            status += $"已删除 {result.DeletedManagedFiles} 个小K管理文件。";
            if (result.SkippedManagedFiles > 0) status += $"保留了 {result.SkippedManagedFiles} 个只读或链接文件。";
            if (result.FailedManagedFileNames.Count > 0)
                status += "未能删除：" + string.Join("、", result.FailedManagedFileNames) + "。可关闭占用程序后重试。";
            if (result.ManagedFilePlanChanged)
                status += "数据目录在确认后发生变化；未能按原预览清理部分文件，请重新预览后重试。";
            status += "用户另存到其他位置的备份不会自动删除。";
            DatabaseMaintenanceStatusText.Text = status;
        }
        catch (Exception ex) when (IsNonFatalSettingsException(ex))
        {
            DatabaseMaintenanceStatusText.Text = clearOperationStarted
                ? "本地历史清理结果未能完整确认；数据可能已部分清除。请重新打开设置并查看当前记录与清理预览，再决定后续操作；不要自动重试。"
                : "本地历史清理尚未开始；预览或验证失败，没有进入清理。请检查当前状态后重新预览。";
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

    private void BrowseVoiceEnvironmentRoot_Click(object sender, RoutedEventArgs e) =>
        BrowseInto(VoiceEnvironmentRootBox, "选择独立的语音 Python 环境目录");

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

    private void BrowseCustomApplicationExecutable_Click(object sender, RoutedEventArgs e) =>
        BrowseExecutableInto(CustomApplicationExecutableBox, "选择要加入白名单的本机应用", "*.exe");

    private void AddCustomApplication_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var displayName = CustomApplicationPolicy.ValidateDisplayName(CustomApplicationNameBox.Text);
            if (_customApplications.Count >= CustomApplicationPolicy.MaximumApplications)
                throw new ArgumentException($"最多登记 {CustomApplicationPolicy.MaximumApplications} 个自定义应用。");
            var alias = NormalizeApplicationAlias(displayName);
            if (_customApplications.Any(app => NormalizeApplicationAlias(app.DisplayName ?? "") == alias))
                throw new ArgumentException("这个应用名称已经登记。请使用唯一名称。");

            var executable = CustomApplicationPolicy.ValidateExecutablePath(CustomApplicationExecutableBox.Text);
            if (_original.Applications.Any(app => app is not null
                    && string.Equals(NormalizeConfiguredApplicationPath(app), executable, StringComparison.OrdinalIgnoreCase)))
                throw new ArgumentException("这个程序路径已经作为固定应用登记，不能重复添加。");
            if (_customApplications.Any(app => string.Equals(Path.GetFullPath(app.Executable), executable,
                    StringComparison.OrdinalIgnoreCase)))
                throw new ArgumentException("这个程序路径已经登记。");

            _customApplications.Add(new AppSetting(CustomApplicationPolicy.CreateId(), executable, null, displayName));
            CustomApplicationNameBox.Clear();
            CustomApplicationExecutableBox.Clear();
            CustomApplicationsList.SelectedIndex = _customApplications.Count - 1;
            CustomApplicationStatusText.Text = "已加入待保存列表；保存并重启小K后，可用“查询应用状态：<名称>”“打开 <名称>”或“切换到 <名称>”。不会向应用传入启动参数。";
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException)
        {
            CustomApplicationStatusText.Text = exception.Message;
        }
    }

    private void RemoveCustomApplication_Click(object sender, RoutedEventArgs e)
    {
        if (CustomApplicationsList.SelectedItem is not AppSetting selected)
        {
            CustomApplicationStatusText.Text = "请先选择要移除的自定义应用。";
            return;
        }

        _customApplications.Remove(selected);
        CustomApplicationStatusText.Text = "已从待保存列表移除；保存并重启小K后，该应用不再属于启动白名单。";
    }

    private static void BrowseExecutableInto(System.Windows.Controls.TextBox target, string title, string expectedFileName)
    {
        var currentDirectory = Path.GetDirectoryName(target.Text.Trim());
        using var picker = new Forms.OpenFileDialog
        {
            Title = title,
            Filter = expectedFileName == "*.exe" ? "Windows 应用程序 (*.exe)|*.exe" : $"{expectedFileName}|{expectedFileName}",
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
        try
        {
            RequestNotificationAccessButton.IsEnabled = false;
            NotificationStatusText.Text = await _requestNotificationAccess();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not AccessViolationException)
        {
            NotificationStatusText.Text = "无法请求 Windows 通知访问权限；请检查系统设置后重试。";
        }
        finally
        {
            RequestNotificationAccessButton.IsEnabled = true;
        }
    }

    private async void InspectNotificationPublishers_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            InspectNotificationPublishersButton.IsEnabled = false;
            _publisherInspectionFailed = true;
            NotificationSamplePublisherBox.SelectedIndex = -1;
            StartNotificationSampleCaptureButton.IsEnabled = false;
            NotificationPublisherCandidatesBox.Text = "正在读取应用来源元数据；不会读取通知正文……";
            NotificationPublisherCandidatesBox.Text = await _inspectNotificationPublishers();
            _publisherInspectionFailed = false;
            RefreshNotificationSamplePublisherCandidates();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not AccessViolationException)
        {
            _publisherInspectionFailed = true;
            NotificationPublisherCandidatesBox.Text = "只读来源检查失败；未读取通知正文。请稍后重试。";
            NotificationSamplePublisherBox.SelectedIndex = -1;
        }
        finally
        {
            UpdateNotificationSampleButtons();
        }
    }

    private async void StartNotificationSampleCapture_Click(object sender, RoutedEventArgs e)
    {
        if (MonitorWeChatCheck.IsChecked == true || MonitorQQCheck.IsChecked == true)
        {
            NotificationSampleStatusText.Text = "请先取消微信和 QQ 的持续监听勾选并保存设置，再开始独立样本核验。";
            return;
        }

        var applicationId = (NotificationSampleAppBox.SelectedItem as System.Windows.Controls.ComboBoxItem)?.Tag as string;
        var selectedPublisher = NotificationSamplePublisherBox.SelectedItem as NotificationPublisherCandidateChoice;
        if (string.IsNullOrWhiteSpace(applicationId) || selectedPublisher is null
            || !string.Equals(selectedPublisher.ApplicationId, applicationId, StringComparison.OrdinalIgnoreCase)
            || !_notificationMonitor.TryGetSamplePublisherCandidate(applicationId,
                selectedPublisher.Candidate.AppUserModelId, out var candidate)
            || candidate is null)
        {
            NotificationSampleStatusText.Text = "请先运行上方只读来源检查，再从与所选平台匹配的候选列表中选择一项；没有采集通知正文。";
            return;
        }

        var applicationDisplayName = applicationId.Equals("wechat", StringComparison.OrdinalIgnoreCase) ? "微信" : "QQ";
        var confirmation = System.Windows.MessageBox.Show(
            this,
            $"即将只对以下来源开始一次性新通知样本核验：\n\n平台：{applicationDisplayName}\nWindows 来源显示名：{candidate.DisplayName}\nAUMID：{candidate.AppUserModelId}\n\n小K会读取该来源之后新到达 Toast 的全部可见文本，并在本机设置页内存中显示，最多30条、最长15分钟。目前无法区分私聊、群聊、服务提醒等类型，因此样本可能包含所选应用的群聊或其他通知正文。文本不会发送到模型/网络，不会写入日志、数据库或文件；采集停止后最迟在本轮15分钟截止时清除。小K每2秒检查通知权限和会话锁定状态，发现异常会停止并清除；点击清除或关闭设置页也会提前删除。它不会打开微信/QQ或改变鼠标、键盘、剪贴板和前台焦点。\n\n只在你确认可以检查此平台新到达通知的完整可见内容时继续。",
            "确认开始本地通知样本核验",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No);
        if (confirmation != MessageBoxResult.Yes) return;

        StartNotificationSampleCaptureButton.IsEnabled = false;
        InspectNotificationPublishersButton.IsEnabled = false;
        NotificationSampleAppBox.IsEnabled = false;
        NotificationSamplePublisherBox.IsEnabled = false;
        NotificationSampleStatusText.Text = "正在建立通知基线；不会处理开始前已有的通知。";
        try
        {
            NotificationSampleStatusText.Text = await _notificationMonitor.StartNotificationSampleCaptureAsync(
                applicationId, candidate.AppUserModelId);
        }
        finally
        {
            UpdateNotificationSampleButtons();
        }
    }

    private void NotificationSampleApp_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (_notificationMonitor is null) return;
        RefreshNotificationSamplePublisherCandidates();
    }

    private void NotificationSamplePublisher_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (_notificationMonitor is null) return;
        if (NotificationSamplePublisherBox.SelectedItem is NotificationPublisherCandidateChoice selected)
            NotificationSamplePublisherStatusText.Text =
                $"已选中 {selected.DisplayText}。选择仅用于本次核验；开始前仍会再次展示采集范围并等待确认。";
        UpdateNotificationSampleButtons();
    }

    private void RefreshNotificationSamplePublisherCandidates()
    {
        if (NotificationSamplePublisherBox is null || NotificationSampleAppBox is null
            || NotificationSamplePublisherStatusText is null || _notificationMonitor is null) return;

        var applicationId = (NotificationSampleAppBox.SelectedItem as System.Windows.Controls.ComboBoxItem)?.Tag as string;
        var candidates = _notificationMonitor.GetSamplePublisherCandidates(applicationId ?? string.Empty)
            .Select(candidate =>
            {
                var latestUtc = candidate.LatestNotificationTimeUtc.ToUniversalTime();
                var timeLabel = latestUtc > DateTimeOffset.UtcNow
                    ? $"时间戳在未来，需核对（UTC {latestUtc:yyyy-MM-dd HH:mm:ss}Z）"
                    : $"检出子集内最近出现（UTC {latestUtc:yyyy-MM-dd HH:mm:ss}Z）";
                return new NotificationPublisherCandidateChoice(applicationId!, candidate,
                    $"{candidate.DisplayName} | AUMID: {candidate.AppUserModelId} | 本次检出数：{candidate.NotificationCount} | {timeLabel}");
            })
            .ToArray();
        NotificationSamplePublisherBox.ItemsSource = candidates;
        NotificationSamplePublisherBox.SelectedIndex = -1;
        var applicationDisplayName = applicationId?.Equals("wechat", StringComparison.OrdinalIgnoreCase) == true
            ? "微信" : applicationId?.Equals("qq", StringComparison.OrdinalIgnoreCase) == true ? "QQ" : "所选平台";
        NotificationSamplePublisherStatusText.Text = !_notificationMonitor.NotificationPublisherDiagnosticCompleted
            ? "请先运行上方只读来源检查；若检查失败，请按上方状态处理。不会手动输入 AUMID，也不会自动启用监听。"
            : candidates.Length == 0
                ? $"只读检查已完成，但没有发现与{applicationDisplayName}匹配的来源候选。请等待该应用自然显示系统通知后重试；不要手动填写 AUMID。"
                : $"只读检查发现 {candidates.Length} 个与{applicationDisplayName}名称匹配的来源候选。来源名称不证明联系人或私聊身份；选择仅用于一次性样本核验。";
        UpdateNotificationSampleButtons();
    }

    private void StopNotificationSampleCapture_Click(object sender, RoutedEventArgs e)
    {
        _notificationMonitor.StopNotificationSampleCapture();
        NotificationSampleStatusText.Text = _notificationMonitor.NotificationSampleStatus;
        UpdateNotificationSampleButtons();
    }

    private void ClearNotificationSamples_Click(object sender, RoutedEventArgs e)
    {
        _notificationMonitor.ClearNotificationSamples();
        NotificationSamplesBox.Clear();
        NotificationSampleStatusText.Text = _notificationMonitor.NotificationSampleStatus;
        UpdateNotificationSampleButtons();
    }

    private void OnNotificationSampleCaptured(NotificationFormatSample sample)
    {
        if (!Dispatcher.CheckAccess())
        {
            try { Dispatcher.BeginInvoke(new Action(() => OnNotificationSampleCaptured(sample))); }
            catch (InvalidOperationException) { }
            return;
        }
        NotificationSamplesBox.Text = FormatNotificationSamples(_notificationMonitor.NotificationSamples);
        NotificationSamplesBox.ScrollToEnd();
        NotificationSampleStatusText.Text = _notificationMonitor.NotificationSampleStatus;
        UpdateNotificationSampleButtons();
    }

    private void OnNotificationSampleStatusChanged(string status)
    {
        if (!Dispatcher.CheckAccess())
        {
            try { Dispatcher.BeginInvoke(new Action(() => OnNotificationSampleStatusChanged(status))); }
            catch (InvalidOperationException) { }
            return;
        }
        NotificationSampleStatusText.Text = status;
        NotificationSamplesBox.Text = FormatNotificationSamples(_notificationMonitor.NotificationSamples);
        UpdateNotificationSampleButtons();
    }

    private void SettingsWindow_Closed(object? sender, EventArgs e)
    {
        _notificationMonitor.NotificationSampleCaptured -= OnNotificationSampleCaptured;
        _notificationMonitor.NotificationSampleStatusChanged -= OnNotificationSampleStatusChanged;
        _notificationMonitor.ClearNotificationSamples();
        NotificationSamplesBox.Clear();
    }

    private void UpdateNotificationSampleButtons()
    {
        var active = _notificationMonitor.NotificationSampleCaptureActive;
        var sessionExists = _notificationMonitor.NotificationSampleCaptureSessionExists;
        var applicationId = (NotificationSampleAppBox.SelectedItem as System.Windows.Controls.ComboBoxItem)?.Tag as string;
        var selectedPublisher = NotificationSamplePublisherBox.SelectedItem as NotificationPublisherCandidateChoice;
        var hasSelectedPublisher = selectedPublisher is not null
            && string.Equals(selectedPublisher.ApplicationId, applicationId, StringComparison.OrdinalIgnoreCase);
        StartNotificationSampleCaptureButton.IsEnabled = !active && !sessionExists
            && !_publisherInspectionFailed && hasSelectedPublisher;
        StopNotificationSampleCaptureButton.IsEnabled = active;
        ClearNotificationSamplesButton.IsEnabled = sessionExists;
        InspectNotificationPublishersButton.IsEnabled = !active;
        NotificationSampleAppBox.IsEnabled = !active;
        NotificationSamplePublisherBox.IsEnabled = !active && NotificationSamplePublisherBox.Items.Count > 0;
    }

    private static string FormatNotificationSamples(IEnumerable<NotificationFormatSample> samples)
    {
        var entries = samples.ToArray();
        if (entries.Length == 0) return "尚无样本。样本仅在你主动开始后采集。";

        var output = new StringBuilder();
        foreach (var sample in entries)
        {
            if (output.Length > 0) output.AppendLine().AppendLine();
            output.Append("样本 #").Append(sample.Sequence).Append(" | ")
                .Append(sample.ApplicationId.Equals("wechat", StringComparison.OrdinalIgnoreCase) ? "微信" : "QQ")
                .Append(" | ").Append(sample.CreatedAtUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"))
                .AppendLine();
            output.Append("来源：").Append(sample.PublisherDisplayName)
                .Append(" | AUMID：").Append(sample.PublisherAppUserModelId).AppendLine();
            if (sample.TextElements.Count == 0)
                output.AppendLine("ToastGeneric 没有可见文本元素。");
            else
            {
                for (var index = 0; index < sample.TextElements.Count; index++)
                    output.Append("文本元素[").Append(index).Append("]：").AppendLine(sample.TextElements[index]);
            }
            if (sample.WasTruncated) output.AppendLine("（样本文本达到诊断上限，已截断。）");
        }
        return output.ToString();
    }

    private async void ClearSamples_Click(object sender, RoutedEventArgs e)
    {
        var deletionStarted = false;
        try
        {
            var dataRoot = ValidateLocalDirectory(DataRootBox.Text, "用户数据目录");
            var modelRoot = ModelRootPathPolicy.Validate(ModelRootBox.Text,
                XiaoKSettings.FindWorkspace(AppContext.BaseDirectory));
            var voiceEnvironmentRoot = ValidateVoiceEnvironmentDirectory(VoiceEnvironmentRootBox.Text);
            var evaluationRoot = ValidateLocalDirectory(EvaluationRootBox.Text, "脱敏评测样本目录");
            var codeWorkspaceRoot = ValidateLocalDirectory(CodeWorkspaceRootBox.Text, "隔离工作区目录");
            EnsureSeparateRoots(dataRoot, modelRoot, voiceEnvironmentRoot, evaluationRoot, codeWorkspaceRoot);
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
            deletionStarted = true;
            var deleted = await Task.Run(() => EvaluationSampleCleanup.DeleteIfUnchanged(preview));
            PrivacyStatusText.Text = $"已删除 {deleted} 个评测样本文件；忽略的目录、链接和只读文件仍保留。";
        }
        catch (Exception ex) when (IsNonFatalSettingsException(ex))
        {
            PrivacyStatusText.Text = deletionStarted
                ? "评测样本清理结果未能完整确认；文件可能已部分删除。请重新检查所选目录后再决定是否重试。"
                : "评测样本清理尚未开始；目录验证或预览失败。请检查所选目录后重试。";
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
        if (_saveInProgress) return;
        if (!_startupStateLoaded)
        {
            StatusText.Text = "Windows 登录启动状态尚未确认；请等待状态读取完成后再保存。";
            return;
        }

        var startupRequested = StartupCheck.IsEnabled && StartupCheck.IsChecked == true;
        var startupChanged = StartupCheck.IsEnabled && startupRequested != _startupWasEnabled;
        _saveInProgress = true;

        try
        {
            SetSaveInProgress(true);
            var updated = _original with
            {
                DataRoot = ValidateLocalDirectory(DataRootBox.Text, "用户数据目录"),
                ModelRoot = ModelRootPathPolicy.Validate(ModelRootBox.Text,
                    XiaoKSettings.FindWorkspace(AppContext.BaseDirectory)),
                VoiceEnvironmentRoot = ValidateVoiceEnvironmentDirectory(VoiceEnvironmentRootBox.Text),
                EvaluationRoot = ValidateLocalDirectory(EvaluationRootBox.Text, "脱敏评测样本目录"),
                SearchRoots = LocalSearchRootPolicy.Parse(SearchRootsBox.Text)
                    .Select(root => new RootSetting(root.Id, root.Path)).ToList(),
                Applications = BuildDesktopApplications(),
                CodeProjectRoot = ValidateOptionalProjectDirectory(CodeProjectRootBox.Text),
                CodeWorkspaceRoot = ValidateLocalDirectory(CodeWorkspaceRootBox.Text, "隔离工作区目录"),
                InferenceEndpoint = ValidateLoopbackEndpoint(InferenceEndpointBox.Text),
                MonitorWeChatNotifications = MonitorWeChatCheck.IsChecked == true,
                MonitorQQNotifications = MonitorQQCheck.IsChecked == true,
                WakeWordEnabled = WakeWordCheck.IsChecked == true,
                WakeWordEngine = (WakeWordEngineCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString() == "sherpa"
                    ? "sherpa"
                    : "windows",
                WeChatPublisherAppIds = ParseAppIds(WeChatAppIdsBox.Text, "微信"),
                QQPublisherAppIds = ParseAppIds(QQAppIdsBox.Text, "QQ")
            };
            EnsureSeparateRoots(updated.DataRoot, updated.ModelRoot, updated.VoiceEnvironmentRoot,
                updated.EvaluationRoot, updated.CodeWorkspaceRoot);
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
                    catch
                    {
                        rollbackFailed = true;
                        ContactStyleStatusText.Text = "设置文件保存失败，且联系人偏好回滚未完成；请勿继续编辑，先检查本机数据库备份。";
                    }
                    if (!rollbackFailed)
                        ContactStyleStatusText.Text = "设置文件保存失败；联系人偏好已回滚到打开设置页时的内容。";
                }
                throw;
            }

            if (_contactStylesLoaded) _loadedContactReplyStyles = _contactReplyStyles.ToArray();
            if (startupChanged) _startupWasEnabled = startupRequested;
            _allowCloseAfterSave = true;
            DialogResult = true;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not AccessViolationException)
        {
            if (startupChanged)
            {
                try
                {
                    var rollback = await LoginStartupRegistration.SetEnabledAsync(_startupWasEnabled);
                    StartupStatusText.Text = rollback.Status;
                }
                catch
                {
                    StartupStatusText.Text = "保存失败，且登录启动状态回滚未完成；请手动核对登录启动设置。";
                }
            }
            StatusText.Text = ex is ArgumentException or IOException or UnauthorizedAccessException or SecurityException
                or InvalidOperationException or NotSupportedException or System.Runtime.InteropServices.COMException
                ? ex.Message
                : "设置保存未能完成；请核对本机设置和数据库状态后重试。";
        }
        finally
        {
            _saveInProgress = false;
            _allowCloseAfterSave = false;
            SetSaveInProgress(false);
        }
    }

    private void SettingsWindow_Closing(object? sender, CancelEventArgs e)
    {
        if (!_saveInProgress || _allowCloseAfterSave) return;
        e.Cancel = true;
        StatusText.Text = "设置正在保存；请等待保存完成后再关闭窗口。";
    }

    private void SetSaveInProgress(bool isSaving)
    {
        SettingsFormScrollViewer.IsEnabled = !isSaving;
        SaveButton.IsEnabled = !isSaving;
        CancelButton.IsEnabled = !isSaving;
        if (isSaving) StatusText.Text = "正在保存设置…";
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
            .Where(app => app is not null && !ConfigurableApplicationIds.Contains(app.Id)
                && !app.Id.StartsWith("custom-", StringComparison.Ordinal)).ToList();
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

        if (_customApplications.Count > CustomApplicationPolicy.MaximumApplications)
            throw new ArgumentException($"最多登记 {CustomApplicationPolicy.MaximumApplications} 个自定义应用。");
        var aliases = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var applicationIds = new HashSet<string>(applications.Select(app => app.Id), StringComparer.OrdinalIgnoreCase);
        var executablePaths = new HashSet<string>(applications.Select(NormalizeConfiguredApplicationPath), StringComparer.OrdinalIgnoreCase);
        foreach (var app in _customApplications)
        {
            if (!CustomApplicationPolicy.IsValidId(app.Id))
                throw new ArgumentException("自定义应用标识无效；请移除后重新添加。");
            if (!applicationIds.Add(app.Id))
                throw new ArgumentException("检测到重复的自定义应用标识；请移除重复项后重新添加。");
            var displayName = CustomApplicationPolicy.ValidateDisplayName(app.DisplayName);
            if (!aliases.Add(NormalizeApplicationAlias(displayName)))
                throw new ArgumentException($"自定义应用名称“{displayName}”重复；请保留唯一名称。");
            var executable = CustomApplicationPolicy.ValidateExecutablePath(app.Executable);
            if (!executablePaths.Add(executable))
                throw new ArgumentException($"程序路径“{executable}”已经登记；一个程序只能对应一个应用名称。");
            applications.Add(app with { Executable = executable, WorkingDirectory = null, DisplayName = displayName });
        }
        return applications;
    }

    private static string NormalizeApplicationAlias(string value) => value.Trim().ToLowerInvariant()
        .Replace(" ", "", StringComparison.Ordinal).Replace("　", "", StringComparison.Ordinal);

    private static string NormalizeConfiguredApplicationPath(AppSetting app) =>
        app.Id.Equals("explorer", StringComparison.OrdinalIgnoreCase)
        && app.Executable.Equals("explorer.exe", StringComparison.OrdinalIgnoreCase)
            ? Path.Combine(Environment.SystemDirectory, "explorer.exe")
            : Path.GetFullPath(app.Executable);

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

    private static string ValidateVoiceEnvironmentDirectory(string value)
    {
        var trimmed = value.Trim();
        var workspace = XiaoKSettings.FindWorkspace(AppContext.BaseDirectory);
        if (workspace is not null && LocalSearchRootPolicy.IsLocalDrivePath(trimmed))
        {
            var expectedDevelopmentRoot = Path.GetFullPath(Path.Combine(workspace, ".tools", "venvs"));
            var selected = Path.GetFullPath(trimmed);
            if (string.Equals(selected, expectedDevelopmentRoot, StringComparison.OrdinalIgnoreCase))
                return selected;
        }
        return ValidateLocalDirectory(trimmed, "语音 Python 环境目录");
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

    private static void EnsureSeparateRoots(string dataRoot, string modelRoot, string voiceEnvironmentRoot,
        string evaluationRoot, string codeWorkspaceRoot)
    {
        var roots = new[] { dataRoot, modelRoot, voiceEnvironmentRoot, evaluationRoot, codeWorkspaceRoot };
        for (var i = 0; i < roots.Length; i++)
        for (var j = i + 1; j < roots.Length; j++)
            if (PathsOverlap(roots[i], roots[j]))
                throw new ArgumentException("用户数据、模型、语音环境、评测和隔离工作区目录必须互相独立，避免混放或误删。");
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
