using Microsoft.Win32;
using Windows.Globalization;
using Windows.Media.SpeechRecognition;
using XiaoK.Core;

namespace XiaoK.Host;

/// <summary>
/// Optional Windows on-device phrase grammar. It never exposes arbitrary recognition text and
/// stops listening for dictation, after a wake, when disabled, or while the session is locked.
/// </summary>
internal sealed class WindowsLocalWakeWordListener : IAsyncDisposable
{
    private enum PauseReason { None, Disabled, Dictation, WakeDetected, SessionLocked, Failed }

    private readonly SemaphoreSlim _lifecycleGate = new(1, 1);
    private SpeechRecognizer? _recognizer;
    private SpeechContinuousRecognitionSession? _session;
    private int _pauseReason = (int)PauseReason.Disabled;
    private volatile bool _enabled;
    private volatile bool _disposed;
    private int _disposeStarted;
    private int _generation;
    private int _wakePending;
    private int _restartAttempts;
    private DateTimeOffset _restartWindowStarted;
    private string _status = "唤醒词监听已关闭。";

    public string Status => Volatile.Read(ref _status);
    public bool IsListening => Volatile.Read(ref _session) is not null && CurrentPauseReason == PauseReason.None;
    public event Action? WakeWordDetected;
    public event Action<string>? StatusChanged;

    public async Task<string> SetEnabledAsync(bool enabled, CancellationToken cancellationToken = default)
    {
        await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!enabled)
            {
                _enabled = false;
                SetPauseReason(PauseReason.Disabled);
                SystemEvents.SessionSwitch -= OnSessionSwitch;
                await StopRecognizerCoreAsync().ConfigureAwait(false);
                SetStatus("唤醒词监听已关闭；麦克风不再用于唤醒检测。");
                return _status;
            }

            if (_enabled && _session is not null && CurrentPauseReason == PauseReason.None) return _status;
            _enabled = true;
            SetPauseReason(PauseReason.None);
            _restartAttempts = 0;
            _restartWindowStarted = DateTimeOffset.UtcNow;
            SystemEvents.SessionSwitch -= OnSessionSwitch;
            SystemEvents.SessionSwitch += OnSessionSwitch;
            if (!WindowsNotificationMonitor.IsUnlockedInputDesktop())
            {
                SetPauseReason(PauseReason.SessionLocked);
                SetStatus("Windows 会话已锁定；唤醒词监听已暂停，解锁后会恢复。");
                return _status;
            }
            await StartRecognizerCoreAsync(cancellationToken).ConfigureAwait(false);
            return _status;
        }
        finally { _lifecycleGate.Release(); }
    }

    public async Task PauseForDictationAsync(CancellationToken cancellationToken = default)
    {
        await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!_enabled || _disposed || CurrentPauseReason == PauseReason.SessionLocked) return;
            SetPauseReason(PauseReason.Dictation);
            Interlocked.Increment(ref _generation);
            await StopRecognizerCoreAsync().ConfigureAwait(false);
            SetStatus("正在使用麦克风录音；唤醒词监听已暂停。");
        }
        finally { _lifecycleGate.Release(); }
    }

    public async Task ResumeAfterDictationAsync(CancellationToken cancellationToken = default)
    {
        await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!_enabled || _disposed || CurrentPauseReason is not (PauseReason.Dictation or PauseReason.WakeDetected)) return;
            if (!WindowsNotificationMonitor.IsUnlockedInputDesktop())
            {
                SetPauseReason(PauseReason.SessionLocked);
                SetStatus("Windows 会话已锁定；唤醒词监听已暂停。");
                return;
            }
            SetPauseReason(PauseReason.None);
            await StartRecognizerCoreAsync(cancellationToken).ConfigureAwait(false);
        }
        finally { _lifecycleGate.Release(); }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposeStarted, 1) != 0) return;
        await _lifecycleGate.WaitAsync().ConfigureAwait(false);
        try
        {
            _disposed = true;
            _enabled = false;
            SetPauseReason(PauseReason.Disabled);
            SystemEvents.SessionSwitch -= OnSessionSwitch;
            await StopRecognizerCoreAsync().ConfigureAwait(false);
            SetStatus("小K已退出；唤醒词监听已停止。");
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    private async Task<bool> StartRecognizerCoreAsync(CancellationToken cancellationToken)
    {
        if (_session is not null) return true;
        cancellationToken.ThrowIfCancellationRequested();
        bool hasPackageIdentity;
        try { hasPackageIdentity = WindowsPackageIdentity.IsPresent; }
        catch (InvalidOperationException)
        {
            SetPauseReason(PauseReason.Failed);
            SetStatus("无法确认 MSIX 身份；唤醒词监听保持关闭。");
            return false;
        }
        if (!hasPackageIdentity)
        {
            SetPauseReason(PauseReason.Failed);
            SetStatus("唤醒词需要以 MSIX 身份运行；当前开发目录实例不会开启麦克风。");
            return false;
        }

        SpeechRecognizer? recognizer = null;
        try
        {
            var language = SpeechRecognizer.SupportedGrammarLanguages.FirstOrDefault(item =>
                string.Equals(item.LanguageTag, "zh-CN", StringComparison.OrdinalIgnoreCase))
                ?? SpeechRecognizer.SupportedGrammarLanguages.FirstOrDefault(item =>
                    string.Equals(item.LanguageTag, "zh-Hans-CN", StringComparison.OrdinalIgnoreCase));
            if (language is null)
            {
                SetPauseReason(PauseReason.Failed);
                SetStatus("唤醒词需要 Windows 中文语音识别语言组件；当前未检测到可用的 zh-CN 本地语法。");
                return false;
            }

            recognizer = new SpeechRecognizer(language);
            recognizer.Constraints.Add(new SpeechRecognitionListConstraint(WakeWordPhrasePolicy.Phrases, "xiaok-wake"));
            var compilation = await recognizer.CompileConstraintsAsync();
            cancellationToken.ThrowIfCancellationRequested();
            if (compilation.Status != SpeechRecognitionResultStatus.Success)
            {
                SetPauseReason(PauseReason.Failed);
                SetStatus("Windows 无法编译本地唤醒短语；唤醒监听保持关闭。");
                recognizer.Dispose();
                return false;
            }

            var session = recognizer.ContinuousRecognitionSession;
            session.AutoStopSilenceTimeout = TimeSpan.FromHours(12);
            session.ResultGenerated += OnResultGenerated;
            session.Completed += OnSessionCompleted;
            _recognizer = recognizer;
            _session = session;
            SetPauseReason(PauseReason.None);
            Interlocked.Exchange(ref _wakePending, 0);
            var generation = Interlocked.Increment(ref _generation);
            await session.StartAsync();
            cancellationToken.ThrowIfCancellationRequested();
            if (generation != Volatile.Read(ref _generation))
            {
                await StopRecognizerCoreAsync().ConfigureAwait(false);
                return false;
            }

            SetStatus("唤醒词正在监听（Windows 本地中文语法）；小K不保存唤醒录音。");
            return true;
        }
        catch (OperationCanceledException)
        {
            await ReleaseFailedRecognizerAsync(recognizer).ConfigureAwait(false);
            throw;
        }
        catch (UnauthorizedAccessException)
        {
            await ReleaseFailedRecognizerAsync(recognizer).ConfigureAwait(false);
            SetPauseReason(PauseReason.Failed);
            SetStatus("Windows 未授予麦克风权限；唤醒监听保持关闭，请检查隐私设置。");
            return false;
        }
        catch (Exception error) when (error is not OutOfMemoryException and not StackOverflowException)
        {
            await ReleaseFailedRecognizerAsync(recognizer).ConfigureAwait(false);
            SetPauseReason(PauseReason.Failed);
            SetStatus($"Windows 本地唤醒服务未能启动（0x{error.HResult:X8}）；麦克风监听已关闭。");
            return false;
        }
    }

    private async Task ReleaseFailedRecognizerAsync(SpeechRecognizer? recognizer)
    {
        if (recognizer is not null && ReferenceEquals(_recognizer, recognizer))
            await StopRecognizerCoreAsync().ConfigureAwait(false);
        else
            recognizer?.Dispose();
    }

    private async Task StopRecognizerCoreAsync()
    {
        var session = _session;
        var recognizer = _recognizer;
        _session = null;
        _recognizer = null;
        Interlocked.Increment(ref _generation);
        Interlocked.Exchange(ref _wakePending, 0);
        if (session is not null)
        {
            session.ResultGenerated -= OnResultGenerated;
            session.Completed -= OnSessionCompleted;
            try { await session.CancelAsync(); }
            catch (Exception) { }
        }
        recognizer?.Dispose();
    }

    private void OnResultGenerated(SpeechContinuousRecognitionSession sender,
        SpeechContinuousRecognitionResultGeneratedEventArgs args)
    {
        if (args.Result.Status != SpeechRecognitionResultStatus.Success
            || args.Result.Confidence is not (SpeechRecognitionConfidence.Medium or SpeechRecognitionConfidence.High)
            || !WakeWordPhrasePolicy.IsWakePhrase(args.Result.Text)
            || CurrentPauseReason != PauseReason.None)
            return;

        if (Interlocked.Exchange(ref _wakePending, 1) != 0) return;
        var generation = Volatile.Read(ref _generation);
        _ = PauseAfterWakeAsync(sender, generation);
    }

    private async Task PauseAfterWakeAsync(SpeechContinuousRecognitionSession sender, int generation)
    {
        var detected = false;
        await _lifecycleGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_enabled && !_disposed && CurrentPauseReason == PauseReason.None
                && generation == Volatile.Read(ref _generation) && ReferenceEquals(_session, sender))
            {
                SetPauseReason(PauseReason.WakeDetected);
                await StopRecognizerCoreAsync().ConfigureAwait(false);
                SetStatus("已听到唤醒短语；麦克风监听已暂停，请点击语音按钮开始录入任务。");
                detected = true;
            }
        }
        finally
        {
            Interlocked.Exchange(ref _wakePending, 0);
            _lifecycleGate.Release();
        }
        if (detected) WakeWordDetected?.Invoke();
    }

    private void OnSessionCompleted(SpeechContinuousRecognitionSession sender,
        SpeechContinuousRecognitionCompletedEventArgs args)
    {
        if (!_enabled || _disposed || CurrentPauseReason != PauseReason.None) return;
        _ = RestartCompletedSessionAsync(sender, args.Status);
    }

    private async Task RestartCompletedSessionAsync(SpeechContinuousRecognitionSession sender,
        SpeechRecognitionResultStatus resultStatus)
    {
        await Task.Delay(TimeSpan.FromSeconds(1)).ConfigureAwait(false);
        await _lifecycleGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (!_enabled || _disposed || CurrentPauseReason != PauseReason.None || !ReferenceEquals(_session, sender)) return;
            if (!WindowsNotificationMonitor.IsUnlockedInputDesktop())
            {
                SetPauseReason(PauseReason.SessionLocked);
                await StopRecognizerCoreAsync().ConfigureAwait(false);
                SetStatus("Windows 会话已锁定；唤醒词监听已暂停。");
                return;
            }

            if (DateTimeOffset.UtcNow - _restartWindowStarted > TimeSpan.FromMinutes(10))
            {
                _restartWindowStarted = DateTimeOffset.UtcNow;
                _restartAttempts = 0;
            }
            if (++_restartAttempts > 3)
            {
                SetPauseReason(PauseReason.Failed);
                await StopRecognizerCoreAsync().ConfigureAwait(false);
                SetStatus($"Windows 唤醒会话连续结束（{resultStatus}）；监听已停止，请在设置中重新启用。");
                return;
            }

            await StopRecognizerCoreAsync().ConfigureAwait(false);
            await StartRecognizerCoreAsync(CancellationToken.None).ConfigureAwait(false);
        }
        finally { _lifecycleGate.Release(); }
    }

    private void OnSessionSwitch(object? sender, SessionSwitchEventArgs args)
    {
        if (args.Reason == SessionSwitchReason.SessionLock) _ = PauseForSessionLockAsync();
        else if (args.Reason == SessionSwitchReason.SessionUnlock) _ = ResumeAfterSessionUnlockAsync();
    }

    private async Task PauseForSessionLockAsync()
    {
        await _lifecycleGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (!_enabled || _disposed) return;
            SetPauseReason(PauseReason.SessionLocked);
            await StopRecognizerCoreAsync().ConfigureAwait(false);
            SetStatus("Windows 会话已锁定；唤醒词监听已暂停。");
        }
        finally { _lifecycleGate.Release(); }
    }

    private async Task ResumeAfterSessionUnlockAsync()
    {
        await _lifecycleGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (!_enabled || _disposed || CurrentPauseReason != PauseReason.SessionLocked) return;
            if (!WindowsNotificationMonitor.IsUnlockedInputDesktop()) return;
            SetPauseReason(PauseReason.None);
            await StartRecognizerCoreAsync(CancellationToken.None).ConfigureAwait(false);
        }
        finally { _lifecycleGate.Release(); }
    }

    private void SetStatus(string value)
    {
        Volatile.Write(ref _status, value);
        StatusChanged?.Invoke(value);
    }

    private PauseReason CurrentPauseReason => (PauseReason)Volatile.Read(ref _pauseReason);

    private void SetPauseReason(PauseReason reason) => Volatile.Write(ref _pauseReason, (int)reason);
}
