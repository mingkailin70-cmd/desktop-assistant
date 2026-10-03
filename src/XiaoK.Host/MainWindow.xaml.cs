using System.ComponentModel;
using System.IO;
using System.Media;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Media;
using System.Windows.Interop;
using Forms = System.Windows.Forms;
using XiaoK.Core;
using XiaoK.Storage;

namespace XiaoK.Host;

public partial class MainWindow : Window, IApprovalPresenter, ICodeTaskReviewPresenter, IMessageSendPreviewPresenter
{
    private readonly AssistantRuntime _runtime;
    private readonly PetWindowPositionStore _petWindowPositionStore;
    private readonly WindowsNotificationMonitor _notificationMonitor;
    private readonly Forms.NotifyIcon _tray;
    private HwndSource? _source;
    private bool _exiting;
    private bool _shutdownInProgress;
    private bool _shutdownComplete;
    private bool _expanded = true;
    private bool _hotkeyRegistered;
    private bool _changingWindowMode;
    private bool _userTaskRunning;
    private bool _speechProcessing;
    private int _speechMaximumDurationPending;
    private CancellationTokenSource? _speechCaptureCancellation;
    private CancellationTokenSource? _speechPlaybackCancellation;
    private SoundPlayer? _soundPlayer;
    private readonly Queue<PrivateNoticeAnalysisResult> _pendingNoticeAnalyses = [];
    private double _expandedWidth = 500;
    private double _expandedHeight = 650;

    public MainWindow()
    {
        InitializeComponent();
        _runtime = new AssistantRuntime(this);
        _petWindowPositionStore = new PetWindowPositionStore(
            Path.GetDirectoryName(XiaoKSettings.GetSettingsPath())
            ?? throw new InvalidOperationException("无法确定小K本地设置目录。"));
        _runtime.SpeechCaptureMaximumDurationReached += OnSpeechCaptureMaximumDurationReached;
        _runtime.WakeWordDetected += OnWakeWordDetected;
        _runtime.WakeWordStatusChanged += OnWakeWordStatusChanged;
        _runtime.MicrophoneStoppedForSessionLock += OnMicrophoneStoppedForSessionLock;
        _notificationMonitor = new WindowsNotificationMonitor(Dispatcher);
        _notificationMonitor.StatusChanged += OnNotificationStatusChanged;
        _notificationMonitor.PrivateNoticeAccepted += OnPrivateNoticeAccepted;
        _runtime.PrivateNoticeAnalysisCompleted += OnPrivateNoticeAnalysisCompleted;
        _ = InitializeBackgroundCapabilitiesAsync();
        FooterText.Text = _runtime.ModelStatus;
        SetStatus(_runtime.VoiceStatus);
        OutputText.Text = (_runtime.StartupIsolationNotice is { } isolationNotice
                ? isolationNotice + Environment.NewLine + Environment.NewLine
                : string.Empty)
            + "小K已启动。闲置时不加载模型，也不采集麦克风。输入「打开小K项目」或「查找文件 关键词」试用本地工具。";
        _tray = new Forms.NotifyIcon
        {
            Text = "小K桌面助手",
            Icon = System.Drawing.SystemIcons.Information,
            Visible = true,
            ContextMenuStrip = BuildTrayMenu()
        };
        _tray.DoubleClick += (_, _) => RestoreFromTray();
        SourceInitialized += OnSourceInitialized;
        Closing += OnClosing;
        SetExpandedView(expanded: false);
    }

    public async Task<bool> ConfirmAsync(string actionId, string title, string details, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var answer = System.Windows.MessageBox.Show(this, details, title, System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Warning, System.Windows.MessageBoxResult.No);
        var confirmed = answer == System.Windows.MessageBoxResult.Yes;
        await _runtime.RecordApprovalAuditAsync(actionId,
            confirmed ? ApprovalAuditCatalog.Confirmed : ApprovalAuditCatalog.Declined, cancellationToken);
        return confirmed;
    }

    public async Task ShowMessageSendPreviewAsync(MessageSendPreview preview, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (Dispatcher.CheckAccess()) ShowMessageSendPreview(preview, cancellationToken);
        else await Dispatcher.InvokeAsync(() => ShowMessageSendPreview(preview, cancellationToken)).Task;
        cancellationToken.ThrowIfCancellationRequested();
    }

    private void ShowMessageSendPreview(MessageSendPreview preview, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var dialog = new MessageSendPreviewWindow(preview) { Owner = this };
        using var registration = cancellationToken.Register(() => Dispatcher.BeginInvoke(new Action(() =>
        {
            if (dialog.IsVisible) dialog.Close();
        })));
        dialog.Loaded += (_, _) =>
        {
            if (cancellationToken.IsCancellationRequested) dialog.Close();
        };
        dialog.ShowDialog();
        cancellationToken.ThrowIfCancellationRequested();
    }

    public async Task<CodeTaskReviewDecision> ReviewAsync(string projectPath, string workspacePath, string diff,
        string? dotNetTestTarget, string? commandPreview, CancellationToken cancellationToken)
    {
        var decision = !Dispatcher.CheckAccess()
            ? await Dispatcher.InvokeAsync(() => ShowCodeTaskReview(projectPath, workspacePath, diff,
                dotNetTestTarget, commandPreview, cancellationToken)).Task
            : ShowCodeTaskReview(projectPath, workspacePath, diff, dotNetTestTarget, commandPreview, cancellationToken);
        if (decision == CodeTaskReviewDecision.RunDotNetTests)
            await _runtime.RecordApprovalAuditAsync(ApprovalAuditCatalog.CodeTaskAction,
                ApprovalAuditCatalog.RunDotNetTests, cancellationToken);
        else if (decision == CodeTaskReviewDecision.ApplyPatchToProject)
            await _runtime.RecordApprovalAuditAsync(ApprovalAuditCatalog.CodePatchApplyAction,
                ApprovalAuditCatalog.Confirmed, cancellationToken);
        return decision;
    }

    private CodeTaskReviewDecision ShowCodeTaskReview(string projectPath, string workspacePath, string diff,
        string? dotNetTestTarget, string? commandPreview, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var dialog = new CodeTaskReviewWindow(projectPath, workspacePath, diff, dotNetTestTarget, commandPreview)
        {
            Owner = this
        };
        using var registration = cancellationToken.Register(() => Dispatcher.BeginInvoke(new Action(() =>
        {
            if (dialog.IsVisible) dialog.Close();
        })));
        dialog.Loaded += (_, _) =>
        {
            if (cancellationToken.IsCancellationRequested) dialog.Close();
        };
        dialog.ShowDialog();
        cancellationToken.ThrowIfCancellationRequested();
        return dialog.Decision;
    }

    private async void Run_Click(object sender, RoutedEventArgs e) => await RunRequestAsync();

    private async void Speech_Click(object sender, RoutedEventArgs e)
    {
        if (_exiting || _speechProcessing) return;
        if (_speechPlaybackCancellation is not null)
        {
            OutputText.Text = "请先停止本地播报，再开始麦克风采集。";
            return;
        }
        if (Volatile.Read(ref _speechMaximumDurationPending) != 0)
        {
            OutputText.Text = "录音已到时限，正在排队转写；请稍候。";
            return;
        }
        if (_runtime.IsMicrophoneActive)
        {
            await FinishSpeechCaptureAsync(maximumDuration: false);
            return;
        }

        var cancellation = new CancellationTokenSource();
        _speechCaptureCancellation?.Dispose();
        _speechCaptureCancellation = cancellation;
        SpeechButton.IsEnabled = false;
        SpeechButton.Content = "正在启动麦克风…";
        try
        {
            await _runtime.StartMicrophoneAsync(cancellation.Token);
            SpeechButton.IsEnabled = true;
            SpeechButton.Content = "停止并转写";
            SetStatus("正在采集麦克风");
            OutputText.Text = "麦克风正在采集；再次点击“停止并转写”后才会调用本地语音识别。最长录音60秒。";
        }
        catch (OperationCanceledException)
        {
            SetStatus(_runtime.VoiceStatus);
            ResetSpeechCaptureState(cancellation);
        }
        catch (UnauthorizedAccessException)
        {
            OutputText.Text = "Windows 未授予小K麦克风权限。请在 Windows 隐私设置中允许访问后重试；当前没有录音提交给模型。";
            SetStatus("麦克风权限未授予");
            ResetSpeechCaptureState(cancellation);
        }
        catch (InvalidOperationException ex)
        {
            OutputText.Text = ex.Message;
            SetStatus("麦克风当前不可用");
            ResetSpeechCaptureState(cancellation);
        }
        catch (Exception)
        {
            OutputText.Text = "无法启动麦克风。请检查 Windows 麦克风隐私权限、输入设备和系统音频设置；没有保存录音。";
            SetStatus("麦克风设备不可用");
            ResetSpeechCaptureState(cancellation);
        }
    }

    private async Task InitializeBackgroundCapabilitiesAsync()
    {
        try
        {
            var settings = _runtime.CurrentSettings;
            await _notificationMonitor.ApplySettingsAsync(settings);
            if (settings.WakeWordEnabled)
            {
                var status = await _runtime.ApplyWakeWordSettingAsync(enabled: true);
                OnWakeWordStatusChanged(status);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or UnauthorizedAccessException
            or System.Runtime.InteropServices.COMException or NotSupportedException)
        {
            SetStatus("小K后台能力初始化失败；通知与唤醒监听状态请在设置中检查。");
        }
    }

    private async Task FinishSpeechCaptureAsync(bool maximumDuration, byte[]? capturedWav = null)
    {
        if (_speechProcessing || _exiting)
        {
            if (capturedWav is not null) CryptographicOperations.ZeroMemory(capturedWav);
            return;
        }
        _speechProcessing = true;
        SpeechButton.IsEnabled = false;
        byte[]? wav = capturedWav;
        var cancellation = _speechCaptureCancellation;
        if (cancellation is null && capturedWav is not null)
        {
            cancellation = new CancellationTokenSource();
            _speechCaptureCancellation = cancellation;
        }
        try
        {
            wav ??= await _runtime.StopMicrophoneAndReadAsync();
            if (wav.Length < 44)
            {
                OutputText.Text = "录音过短或没有音频；没有发送任何动作。请重新点击“开始说话”。";
                return;
            }

            SetStatus("本地语音识别中");
            OutputText.Text = "正在本机识别；识别完成后只填入输入框，不会自动执行任务。可随时取消。";
            var recognition = await _runtime.TranscribeLocalWavAsync(wav, forceChinese: true,
                cancellation?.Token ?? CancellationToken.None);
            var transcript = recognition.Text.Trim();
            if (transcript.Length == 0)
            {
                OutputText.Text = "本地语音识别没有得到文字；没有执行任何动作。可以重试或使用文本输入。";
                return;
            }

            RequestBox.Text = string.IsNullOrWhiteSpace(RequestBox.Text)
                ? transcript
                : RequestBox.Text.TrimEnd() + Environment.NewLine + transcript;
            RequestBox.CaretIndex = RequestBox.Text.Length;
            OutputText.Text = (maximumDuration ? "已达到60秒录音上限。" : "语音转写完成。")
                + "请检查下面的识别文本，再手动点击“运行任务”。" + Environment.NewLine + Environment.NewLine + transcript;
            RequestBox.Focus();
        }
        catch (OperationCanceledException)
        {
            OutputText.Text = "语音识别已取消；录音和识别文字仅在内存中处理，没有执行任何动作。";
            SetStatus("语音识别已取消");
        }
        catch (Exception)
        {
            OutputText.Text = "本地语音识别失败。可以检查模型环境后重试，也可以使用文本输入；录音没有写入历史。";
            SetStatus("本地语音识别失败");
        }
        finally
        {
            if (wav is not null) CryptographicOperations.ZeroMemory(wav);
            if (cancellation is not null) ResetSpeechCaptureState(cancellation);
            _speechProcessing = false;
            SpeechButton.IsEnabled = true;
            if (!_exiting)
            {
                try { await _runtime.ResumeWakeWordAfterDictationAsync(); }
                catch (Exception) { OutputText.Text += Environment.NewLine + "唤醒监听未能恢复；请在设置中查看状态。"; }
            }
            SetStatus(_runtime.VoiceStatus);
        }
    }

    private void ResetSpeechCaptureState(CancellationTokenSource cancellation)
    {
        if (!ReferenceEquals(_speechCaptureCancellation, cancellation))
        {
            cancellation.Dispose();
            return;
        }
        _speechCaptureCancellation = null;
        cancellation.Dispose();
        SpeechButton.Content = "开始说话";
        SpeechButton.IsEnabled = true;
    }

    private void OnSpeechCaptureMaximumDurationReached(byte[] wav)
    {
        Interlocked.Exchange(ref _speechMaximumDurationPending, 1);
        if (_exiting || Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished)
        {
            CryptographicOperations.ZeroMemory(wav);
            Interlocked.Exchange(ref _speechMaximumDurationPending, 0);
            return;
        }
        try
        {
            Dispatcher.BeginInvoke(new Action(async () =>
            {
                try
                {
                    OutputText.Text = "已达到60秒录音上限，正在转写这段录音。";
                    await FinishSpeechCaptureAsync(maximumDuration: true, capturedWav: wav);
                }
                finally { Interlocked.Exchange(ref _speechMaximumDurationPending, 0); }
            }));
        }
        catch (InvalidOperationException)
        {
            CryptographicOperations.ZeroMemory(wav);
            Interlocked.Exchange(ref _speechMaximumDurationPending, 0);
        }
    }

    private async void SpeakOutput_Click(object sender, RoutedEventArgs e)
    {
        if (_exiting || _speechPlaybackCancellation is not null) return;
        if (_runtime.IsMicrophoneActive || _speechProcessing
            || Volatile.Read(ref _speechMaximumDurationPending) != 0)
        {
            OutputText.Text = "请先停止麦克风采集或等待语音转写结束，再开始播报。";
            return;
        }
        var text = OutputText.Text.Trim();
        if (string.IsNullOrWhiteSpace(text)) return;
        if (text.Length > 1000)
        {
            OutputText.Text = "播报文本最多1000个字符。请先选择或复制需要播报的短内容。";
            return;
        }

        var cancellation = new CancellationTokenSource();
        _speechPlaybackCancellation = cancellation;
        byte[]? wav = null;
        MemoryStream? stream = null;
        SoundPlayer? player = null;
        SpeakOutputButton.IsEnabled = false;
        StopSpeakingButton.IsEnabled = true;
        try
        {
            SetStatus("本地语音合成中");
            wav = await _runtime.SynthesizeChineseWavAsync(text, cancellation.Token);
            cancellation.Token.ThrowIfCancellationRequested();
            stream = new MemoryStream(wav, writable: false);
            player = new SoundPlayer(stream);
            player.Load();
            _soundPlayer = player;
            SetStatus("正在本地播报");
            await Task.Run(player.PlaySync, cancellation.Token);
        }
        catch (OperationCanceledException)
        {
            SetStatus("播报已停止");
        }
        catch (Exception)
        {
            OutputText.Text = "本地播报失败。请检查音频输出设备；文本和生成音频不会保存。";
            SetStatus("本地播报失败");
        }
        finally
        {
            try { player?.Stop(); }
            catch (Exception) { }
            _soundPlayer = null;
            player?.Dispose();
            stream?.Dispose();
            if (wav is not null) CryptographicOperations.ZeroMemory(wav);
            StopSpeakingButton.IsEnabled = false;
            SpeakOutputButton.IsEnabled = true;
            if (ReferenceEquals(_speechPlaybackCancellation, cancellation)) _speechPlaybackCancellation = null;
            cancellation.Dispose();
            SetStatus(_runtime.VoiceStatus);
        }
    }

    private void StopSpeaking_Click(object sender, RoutedEventArgs e) => StopSpeechPlayback();

    private void StopSpeechPlayback()
    {
        try { _speechPlaybackCancellation?.Cancel(); }
        catch (ObjectDisposedException) { }
        try { _soundPlayer?.Stop(); }
        catch (Exception) { }
    }

    private void Header_MouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (e.ButtonState == System.Windows.Input.MouseButtonState.Pressed)
        {
            try { DragMove(); }
            catch (InvalidOperationException) { return; }
            ClampWindowToMonitorWorkArea();
            SaveWindowPosition();
        }
    }
    private async void RequestBox_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.Enter && !System.Windows.Input.Keyboard.Modifiers.HasFlag(System.Windows.Input.ModifierKeys.Shift))
        {
            e.Handled = true;
            await RunRequestAsync();
        }
    }

    private void QuickPrompt_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string prefix } || string.IsNullOrWhiteSpace(prefix)) return;
        RequestBox.Text = prefix;
        RequestBox.CaretIndex = RequestBox.Text.Length;
        RequestBox.Focus();
        RequestBox.ScrollToEnd();
    }

    private async Task RunRequestAsync()
    {
        if (_runtime.IsMicrophoneActive)
        {
            OutputText.Text = "麦克风仍在采集。请先停止并转写，或使用“停麦”丢弃录音，再运行任务。";
            return;
        }
        var request = RequestBox.Text;
        if (string.IsNullOrWhiteSpace(request)) return;
        RequestBox.Clear();
        _userTaskRunning = true;
        OutputText.Text = "正在处理；可随时取消。聊天内容和模型回答仅保留在内存中。";
        SetStatus("任务运行中");
        SetButtonsEnabled(false);
        try { OutputText.Text = await _runtime.SubmitAsync(request); }
        finally { FinishUserTask(); }
    }

    private async void OpenProject_Click(object sender, RoutedEventArgs e)
    {
        if (_runtime.IsMicrophoneActive)
        {
            OutputText.Text = "麦克风仍在采集。请先结束或丢弃录音，再打开项目。";
            return;
        }
        _userTaskRunning = true;
        OutputText.Text = "正在打开项目；可随时取消。";
        SetStatus("任务运行中");
        SetButtonsEnabled(false);
        try { OutputText.Text = await _runtime.SubmitAsync("打开小K项目"); }
        finally { FinishUserTask(); }
    }

    private void FinishUserTask()
    {
        _userTaskRunning = false;
        SetStatus(_runtime.VoiceStatus);
        SetButtonsEnabled(true);
        if (_pendingNoticeAnalyses.Count == 0) return;

        var analyses = _pendingNoticeAnalyses.ToArray();
        _pendingNoticeAnalyses.Clear();
        OutputText.Text += Environment.NewLine + Environment.NewLine
            + "后台私聊通知分析（只基于各条通知中可见的文字）：" + Environment.NewLine
            + string.Join(Environment.NewLine + Environment.NewLine, analyses.Select(FormatNoticeAnalysis));
    }

    private void OnPrivateNoticeAccepted(MessageNotice notice, CancellationToken monitoringSession)
    {
        if (_exiting) return;
        if (_runtime.QueueVerifiedPrivateNotice(notice, monitoringSession, out var status))
        {
            SetStatus(status);
            ShowTrayNotice("收到可见正文的私聊通知，正在本地分析；分析结果不会自动发送。");
            return;
        }

        OutputText.Text = status;
        SetStatus(status);
    }

    private void OnPrivateNoticeAnalysisCompleted(PrivateNoticeAnalysisResult result)
    {
        if (_exiting) return;
        if (!Dispatcher.CheckAccess())
        {
            if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished) return;
            try { Dispatcher.BeginInvoke(new Action(() => OnPrivateNoticeAnalysisCompleted(result))); }
            catch (InvalidOperationException) { }
            return;
        }

        if (_userTaskRunning)
        {
            if (_pendingNoticeAnalyses.Count == 5) _pendingNoticeAnalyses.Dequeue();
            _pendingNoticeAnalyses.Enqueue(result);
            SetStatus(result.Success ? "后台私聊分析完成" : "后台私聊分析未完成");
            return;
        }

        OutputText.Text = FormatNoticeAnalysis(result);
        SetStatus(result.Success ? "私聊通知已完成本地分析" : "私聊通知本地分析未完成");
        ShowTrayNotice(result.Success
            ? "私聊通知已完成本地分析；请打开小K查看结果。"
            : "私聊通知未能完成本地分析；请打开小K查看状态。");
    }

    private static string FormatNoticeAnalysis(PrivateNoticeAnalysisResult result)
    {
        var application = result.ApplicationId.Equals("wechat", StringComparison.OrdinalIgnoreCase) ? "微信" : "QQ";
        return result.Success
            ? $"{application}私聊通知分析：{Environment.NewLine}{result.Text}"
            : $"{application}私聊通知分析未完成：{Environment.NewLine}{result.Text}";
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        var wasCapturing = StopMicrophoneAndDiscard();
        try { _speechCaptureCancellation?.Cancel(); }
        catch (ObjectDisposedException) { }
        StopSpeechPlayback();
        _runtime.CancelCurrent();
        if (wasCapturing) _ = _runtime.ResumeWakeWordAfterDictationAsync();
        SpeechButton.Content = "开始说话";
        SetStatus("已请求取消");
    }

    private async void StopMic_Click(object sender, RoutedEventArgs e)
    {
        bool wasCapturing;
        try { wasCapturing = await StopAllMicrophoneAndDiscardAsync(); }
        catch (InvalidOperationException ex)
        {
            OutputText.Text = ex.Message;
            SetStatus("唤醒监听已停止，但设置保存失败");
            return;
        }
        SetStatus(_runtime.VoiceStatus);
        OutputText.Text = wasCapturing
            ? "麦克风已立即停止，唤醒词已关闭并保存；未完成的录音已丢弃，未送入识别。桌面任务继续运行。"
            : "唤醒词已关闭并保存；麦克风当前未采集，桌面任务继续运行。";
    }

    private bool StopMicrophoneAndDiscard()
    {
        var wasActive = _runtime.StopMicrophone();
        if (wasActive)
        {
            try { _speechCaptureCancellation?.Cancel(); }
            catch (ObjectDisposedException) { }
            if (_speechCaptureCancellation is { } cancellation) ResetSpeechCaptureState(cancellation);
        }
        return wasActive;
    }

    private async Task<bool> StopAllMicrophoneAndDiscardAsync()
    {
        var wasActive = await _runtime.StopAllMicrophoneAsync();
        if (wasActive)
        {
            try { _speechCaptureCancellation?.Cancel(); }
            catch (ObjectDisposedException) { }
            if (_speechCaptureCancellation is { } cancellation) ResetSpeechCaptureState(cancellation);
        }
        return wasActive;
    }

    private void Expand_Click(object sender, RoutedEventArgs e)
    {
        SetExpandedView(!_expanded);
    }

    private void Hide_Click(object sender, RoutedEventArgs e) => Hide();

    private void ShowPanel_Click(object sender, RoutedEventArgs e) => RestoreFromTray();

    private void Exit_Click(object sender, RoutedEventArgs e)
    {
        RequestExit();
    }

    private void RequestExit()
    {
        if (_exiting) return;
        _exiting = true;
        Close();
    }

    private void Settings_Click(object sender, RoutedEventArgs e) => ShowSettings();

    private async void History_Click(object sender, RoutedEventArgs e) => await ShowTaskHistoryAsync();

    private async Task ShowTaskHistoryAsync()
    {
        if (_exiting) return;
        if (!IsVisible || !_expanded) RestoreFromTray();
        try
        {
            var history = await _runtime.GetRecentTaskHistoryAsync(CancellationToken.None);
            var dialog = new TaskHistoryWindow(history) { Owner = this };
            dialog.ShowDialog();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException
            or InvalidOperationException or ArgumentException)
        {
            OutputText.Text = "无法读取本地任务记录；未修改或删除任何任务数据。请检查数据目录和隔离工作区。";
        }
    }

    private void ShowSettings()
    {
        if (!IsVisible || !_expanded) RestoreFromTray();
        var dialog = new SettingsWindow(XiaoKSettings.Load(), _notificationMonitor,
            _runtime.GetContactReplyStylesAsync, _runtime.ReplaceContactReplyStylesAsync, _runtime.ActiveDatabasePath,
            _runtime.CreateDatabaseBackupAsync, _runtime.RestoreDatabaseBackupAsync,
            _runtime.GetRecentApprovalAuditAsync, _runtime.GetLocalDataCleanupPreviewAsync,
            _runtime.ClearLocalDataAsync) { Owner = this };
        if (dialog.ShowDialog() == true)
        {
            _ = ApplySettingsAndReportAsync();
        }
    }

    private async Task ApplySettingsAndReportAsync()
    {
        var settings = XiaoKSettings.Load();
        _runtime.UpdateNotificationSettings(settings);
        var notificationStatus = await _notificationMonitor.ApplySettingsAsync(settings);
        var wakeWordStatus = await _runtime.ApplyWakeWordSettingAsync(settings.WakeWordEnabled);
        OutputText.Text = $"设置已保存。登录启动、唤醒词和通知监听立即生效；数据与推理路径将在重启小K后生效。\n{wakeWordStatus}\n{notificationStatus}";
        SetStatus(wakeWordStatus);
    }

    private void OnWakeWordStatusChanged(string message)
    {
        if (_exiting || Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished) return;
        if (!Dispatcher.CheckAccess())
        {
            try { Dispatcher.BeginInvoke(new Action(() => OnWakeWordStatusChanged(message))); }
            catch (InvalidOperationException) { }
            return;
        }
        if (!_runtime.IsMicrophoneActive && !_speechProcessing && !_userTaskRunning) SetStatus(message);
    }

    private void OnWakeWordDetected()
    {
        if (_exiting || Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished) return;
        if (!Dispatcher.CheckAccess())
        {
            try { Dispatcher.BeginInvoke(new Action(OnWakeWordDetected)); }
            catch (InvalidOperationException) { }
            return;
        }
        RestoreFromTray();
        OutputText.Text = "小K已唤醒。点击“开始说话”录入任务，识别文字仍需你检查并手动运行；没有自动录音或执行操作。";
        SetStatus("已听到唤醒短语；麦克风监听已暂停");
    }

    private void OnMicrophoneStoppedForSessionLock()
    {
        if (_exiting || Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished) return;
        try
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (_exiting) return;
                try { _speechCaptureCancellation?.Cancel(); }
                catch (ObjectDisposedException) { }
                if (_speechCaptureCancellation is { } cancellation) ResetSpeechCaptureState(cancellation);
                OutputText.Text = "Windows 会话已锁定；麦克风已停止，未完成录音已丢弃，语音转写已取消。解锁后不会自动继续录音。";
                SetStatus("会话锁定；麦克风和语音任务已停止");
            }));
        }
        catch (InvalidOperationException) { }
    }

    private void OnNotificationStatusChanged(string message)
    {
        if (_exiting) return;
        if (message.StartsWith("微信通知：", StringComparison.Ordinal))
        {
            SetStatus("收到微信通知");
            if (!message.Contains("正在本地分析", StringComparison.Ordinal))
                ShowTrayNotice("收到微信通知；请查看小K提示。");
        }
        else if (message.StartsWith("QQ 通知：", StringComparison.Ordinal))
        {
            SetStatus("收到 QQ 通知");
            if (!message.Contains("正在本地分析", StringComparison.Ordinal))
                ShowTrayNotice("收到 QQ 通知；请查看小K提示。");
        }
        OutputText.Text = message;
    }

    private void ShowTrayNotice(string message)
    {
        if (_exiting || !_tray.Visible) return;
        _tray.BalloonTipTitle = "小K桌面助手";
        _tray.BalloonTipText = message;
        _tray.ShowBalloonTip(4000);
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        _source = (HwndSource)PresentationSource.FromVisual(this)!;
        _source.AddHook(HotkeyHook);
        RestoreWindowPosition();
        _hotkeyRegistered = RegisterHotKey(_source.Handle, 1901, ModControl | ModShift, (uint)System.Windows.Forms.Keys.K);
        if (!_hotkeyRegistered) SetStatus("快捷键不可用 · 托盘可打开");
    }

    private IntPtr HotkeyHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (App.RestoreMessageId != 0 && msg == (int)App.RestoreMessageId)
        {
            RestoreFromTray();
            handled = true;
        }
        else if (App.ShutdownMessageId != 0 && msg == (int)App.ShutdownMessageId)
        {
            RequestExit();
            handled = true;
        }
        else if (msg == WmHotkey && wParam.ToInt32() == 1901)
        {
            RestoreFromTray();
            handled = true;
        }
        else if (msg is WmDisplayChange or WmDpiChanged)
        {
            _ = Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background,
                new Action(ClampWindowToMonitorWorkArea));
        }
        return IntPtr.Zero;
    }

    private void RestoreFromTray()
    {
        if (_exiting) return;
        SetExpandedView(expanded: true);
        Show();
        WindowState = WindowState.Normal;
        Activate();
        RequestBox.Focus();
    }

    private void ShowPet()
    {
        if (_exiting) return;
        SetExpandedView(expanded: false);
        Show();
        WindowState = WindowState.Normal;
        Activate();
    }

    private Forms.ContextMenuStrip BuildTrayMenu()
    {
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("显示桌宠", null, (_, _) => ShowPet());
        menu.Items.Add("打开任务面板", null, (_, _) => RestoreFromTray());
        menu.Items.Add("最近任务", null, async (_, _) => await ShowTaskHistoryAsync());
        menu.Items.Add("设置", null, (_, _) => ShowSettings());
        menu.Items.Add("取消当前任务", null, (_, _) => CancelFromTray());
        menu.Items.Add("停麦", null, (_, _) => StopMicrophoneFromTray());
        menu.Items.Add("退出", null, (_, _) => Exit_Click(this, new RoutedEventArgs()));
        return menu;
    }

    private async void StopMicrophoneFromTray()
    {
        bool wasCapturing;
        try { wasCapturing = await StopAllMicrophoneAndDiscardAsync(); }
        catch (InvalidOperationException ex)
        {
            OutputText.Text = ex.Message;
            SetStatus("唤醒监听已停止，但设置保存失败");
            return;
        }
        SetStatus(_runtime.VoiceStatus);
        OutputText.Text = wasCapturing
            ? "麦克风已立即停止，唤醒词已关闭并保存；未完成的录音已丢弃，未送入识别。桌面任务继续运行。"
            : "唤醒词已关闭并保存；麦克风当前未采集，桌面任务继续运行。";
    }

    private async void CancelFromTray()
    {
        var wasCapturing = StopMicrophoneAndDiscard();
        try { _speechCaptureCancellation?.Cancel(); }
        catch (ObjectDisposedException) { }
        StopSpeechPlayback();
        _runtime.CancelCurrent();
        if (wasCapturing) await _runtime.ResumeWakeWordAfterDictationAsync();
        SpeechButton.Content = "开始说话";
        SetStatus("已请求取消");
    }

    private void SetExpandedView(bool expanded)
    {
        if (_expanded == expanded) return;
        _changingWindowMode = true;
        try
        {
            if (!expanded && _expanded)
            {
                _expandedWidth = Math.Max(440, Width);
                _expandedHeight = Math.Max(560, Height);
            }

            _expanded = expanded;
            ExpandedView.Visibility = expanded ? Visibility.Visible : Visibility.Collapsed;
            PetView.Visibility = expanded ? Visibility.Collapsed : Visibility.Visible;
            ResizeMode = expanded ? ResizeMode.CanResizeWithGrip : ResizeMode.NoResize;
            ShellBorder.Background = expanded
                ? new SolidColorBrush(System.Windows.Media.Color.FromRgb(247, 248, 252))
                : System.Windows.Media.Brushes.Transparent;
            ShellBorder.BorderBrush = expanded
                ? new SolidColorBrush(System.Windows.Media.Color.FromRgb(220, 226, 239))
                : System.Windows.Media.Brushes.Transparent;
            ShellBorder.BorderThickness = expanded ? new Thickness(1) : new Thickness(0);
            ShellBorder.Padding = expanded ? new Thickness(14) : new Thickness(0);
            ShellBorder.Margin = expanded ? new Thickness(5) : new Thickness(0);
            MinWidth = expanded ? 440 : 176;
            MinHeight = expanded ? 560 : 176;
            Width = expanded ? _expandedWidth : 176;
            Height = expanded ? _expandedHeight : 176;
            ClampWindowToMonitorWorkArea();
            if (expanded)
            {
                ExpandButton.Content = "收起";
                ExpandButton.ToolTip = "收起到桌宠";
            }
        }
        finally
        {
            _changingWindowMode = false;
        }
    }

    private void Window_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_expanded && !_changingWindowMode)
        {
            _expandedWidth = Width;
            _expandedHeight = Height;
            ClampWindowToMonitorWorkArea();
        }
    }

    private void PetView_MouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (e.ButtonState == System.Windows.Input.MouseButtonState.Pressed
            && e.OriginalSource is FrameworkElement element
            && element.Name is not ("PetStatusText" or "PetStatusDot"))
        {
            try
            {
                DragMove();
                ClampWindowToMonitorWorkArea();
                SaveWindowPosition();
            }
            catch (InvalidOperationException) { }
        }
    }

    private void RestoreWindowPosition()
    {
        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero || !GetWindowRect(handle, out var current)) return;

        var settings = _runtime.CurrentSettings;
        if (_petWindowPositionStore.TryLoad(out var savedPosition))
        {
            SetWindowPosition(handle, savedPosition.LeftPixels, savedPosition.TopPixels);
        }
        else if (settings.PetWindowLeftPixels is int savedLeft && settings.PetWindowTopPixels is int savedTop)
        {
            SetWindowPosition(handle, savedLeft, savedTop);
        }
        else if (settings.PetWindowLeft is double legacyLeft && settings.PetWindowTop is double legacyTop
            && double.IsFinite(legacyLeft) && double.IsFinite(legacyTop))
        {
            var dpi = GetEffectiveDpi(handle);
            SetWindowPosition(handle, (int)Math.Round(legacyLeft * dpi / 96d),
                (int)Math.Round(legacyTop * dpi / 96d));
        }
        else
        {
            if (Forms.Screen.PrimaryScreen is { } primaryScreen)
            {
                var workArea = primaryScreen.WorkingArea;
                var margin = (int)Math.Round(24 * GetEffectiveDpi(handle) / 96d);
                var width = current.Right - current.Left;
                var height = current.Bottom - current.Top;
                SetWindowPosition(handle, Math.Max(workArea.Left, workArea.Right - width - margin),
                    Math.Max(workArea.Top, workArea.Bottom - height - margin));
            }
        }

        ClampWindowToMonitorWorkArea();
    }

    private void ClampWindowToMonitorWorkArea()
    {
        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero || !GetWindowRect(handle, out var rect)) return;

        var monitor = MonitorFromRect(ref rect, MonitorDefaultToNearest);
        if (monitor == IntPtr.Zero) return;
        var info = new MonitorInfo { Size = (uint)Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfo(monitor, ref info)) return;

        var width = rect.Right - rect.Left;
        var height = rect.Bottom - rect.Top;
        var rightmostLeft = Math.Max(info.Work.Left, info.Work.Right - width);
        var bottommostTop = Math.Max(info.Work.Top, info.Work.Bottom - height);
        var left = Math.Clamp(rect.Left, info.Work.Left, rightmostLeft);
        var top = Math.Clamp(rect.Top, info.Work.Top, bottommostTop);
        if (left != rect.Left || top != rect.Top) SetWindowPosition(handle, left, top);
    }

    private static void SetWindowPosition(IntPtr handle, int left, int top)
    {
        _ = SetWindowPos(handle, IntPtr.Zero, left, top, 0, 0, SwpNoSize | SwpNoZOrder | SwpNoActivate);
    }

    private static uint GetEffectiveDpi(IntPtr handle)
    {
        var dpi = GetDpiForWindow(handle);
        return dpi == 0 ? 96u : dpi;
    }

    private void SaveWindowPosition()
    {
        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero || !GetWindowRect(handle, out var rect)) return;
        try
        {
            _petWindowPositionStore.Save(rect.Left, rect.Top);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
            or System.Security.SecurityException or InvalidOperationException or ArgumentException)
        {
            SetStatus("桌宠位置保存失败");
        }
    }

    private void SetStatus(string status)
    {
        StatusText.Text = status;
        PetStatusText.Text = status;
        PetView.ToolTip = $"{status} · 单击展开任务面板，右键打开菜单";
        var color = status.Contains("失败", StringComparison.Ordinal) || status.Contains("不可用", StringComparison.Ordinal)
            || status.Contains("未授予", StringComparison.Ordinal) || status.Contains("未能启动", StringComparison.Ordinal)
            ? System.Windows.Media.Color.FromRgb(205, 69, 69)
            : status.Contains("确认", StringComparison.Ordinal) || status.Contains("取消", StringComparison.Ordinal)
                || status.Contains("暂停", StringComparison.Ordinal) || status.Contains("锁定", StringComparison.Ordinal)
                ? System.Windows.Media.Color.FromRgb(216, 144, 38)
                : status.Contains("运行", StringComparison.Ordinal) || status.Contains("正在", StringComparison.Ordinal)
                    ? System.Windows.Media.Color.FromRgb(63, 118, 232)
                : status.Contains("未安装", StringComparison.Ordinal) || status.Contains("未采集", StringComparison.Ordinal)
                    || status.Contains("保持关闭", StringComparison.Ordinal)
                        ? System.Windows.Media.Color.FromRgb(123, 132, 152)
                        : System.Windows.Media.Color.FromRgb(87, 163, 112);
        PetStatusDot.Fill = new SolidColorBrush(color);
    }

    private async void OnClosing(object? sender, CancelEventArgs e)
    {
        if (!_exiting) { e.Cancel = true; Hide(); return; }
        if (!_shutdownComplete)
        {
            e.Cancel = true;
            if (_shutdownInProgress) return;
            _shutdownInProgress = true;
            SetStatus("正在停止任务…");
            SetButtonsEnabled(false);
            CancelButton.IsEnabled = false;
            StopMicrophoneAndDiscard();
            try { _speechCaptureCancellation?.Cancel(); }
            catch (ObjectDisposedException) { }
            StopSpeechPlayback();
            _tray.Visible = false;
            _notificationMonitor.Dispose();
            if (_source is not null && _hotkeyRegistered)
            {
                UnregisterHotKey(_source.Handle, 1901);
                _hotkeyRegistered = false;
            }
            try { await _runtime.DisposeAsync(); }
            catch (Exception) { SetStatus("退出清理未能完整确认"); }
            _shutdownComplete = true;
            Close();
            return;
        }

        _tray.Visible = false;
        _tray.Dispose();
        _notificationMonitor.StatusChanged -= OnNotificationStatusChanged;
        _notificationMonitor.PrivateNoticeAccepted -= OnPrivateNoticeAccepted;
        _runtime.SpeechCaptureMaximumDurationReached -= OnSpeechCaptureMaximumDurationReached;
        _runtime.PrivateNoticeAnalysisCompleted -= OnPrivateNoticeAnalysisCompleted;
        if (_source is not null)
        {
            if (_hotkeyRegistered) UnregisterHotKey(_source.Handle, 1901);
            _source.RemoveHook(HotkeyHook);
        }
    }

    private void SetButtonsEnabled(bool enabled)
    {
        RequestBox.IsEnabled = enabled;
        RunButton.IsEnabled = enabled;
        CancelButton.IsEnabled = !enabled;
        OpenProjectButton.IsEnabled = enabled;
        StopMicButton.IsEnabled = true;
    }

    private const int WmHotkey = 0x0312;
    private const int WmDisplayChange = 0x007E;
    private const int WmDpiChanged = 0x02E0;
    private const uint ModControl = 0x0002;
    private const uint ModShift = 0x0004;
    private const uint MonitorDefaultToNearest = 2;
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpNoActivate = 0x0010;

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public uint Size;
        public NativeRect Monitor;
        public NativeRect Work;
        public uint Flags;
    }

    [DllImport("user32.dll")] private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);
    [DllImport("user32.dll")] private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr hWnd, out NativeRect rect);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr MonitorFromRect(ref NativeRect rect, uint flags);
    [DllImport("user32.dll", EntryPoint = "GetMonitorInfoW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MonitorInfo info);
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y,
        int width, int height, uint flags);
    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hWnd);
}
