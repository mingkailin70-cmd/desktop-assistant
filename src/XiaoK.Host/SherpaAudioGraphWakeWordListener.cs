using System.Buffers;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Threading.Channels;
using Microsoft.Win32;
using XiaoK.Adapters.Windows;
using Windows.Foundation;
using Windows.Media;
using Windows.Media.Audio;
using Windows.Media.Capture;
using Windows.Media.MediaProperties;
using Windows.Media.Render;
using WinRT;
using XiaoK.Voice;

namespace XiaoK.Host;

/// <summary>
/// Experimental local KWS listener. AudioGraph owns microphone capture; its callback only copies
/// one bounded frame to a bounded channel. Decoding runs in a low-priority CPU worker process.
/// </summary>
internal sealed class SherpaAudioGraphWakeWordListener : ILocalWakeWordListener
{
    private const string ModelRelativePath = "speech/kws/sherpa-onnx-kws-zipformer-zh-en-3M-2025-12-20/afc4a534ef85f2db4f84f05ed78eaac385b3387e";
    private const int FrameQueueCapacity = 8;
    private static readonly IReadOnlyDictionary<string, string> ExpectedModelHashes = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["encoder-epoch-13-avg-2-chunk-16-left-64.int8.onnx"] = "408BBD740838C42D5BF6D1C5B80B3C88B616C7860B92D980328B5B068C76AE48",
        ["decoder-epoch-13-avg-2-chunk-16-left-64.onnx"] = "63A22DD60F40FFF082AC3E09AFA507F6787DA36DF76DED2FBE145FA233E22C21",
        ["joiner-epoch-13-avg-2-chunk-16-left-64.int8.onnx"] = "190D4067B4CC20B72A42A1916E69D92052000FB7051A427EBB1BC72A69207DC1",
        ["tokens.txt"] = "2D3F32311F9B692B964DA3C90E830258D3E78E013CB0C992DBFB15CD5A1A71B0",
        ["test_wavs/keywords.txt"] = "4878B5C2CDDD6605D228877A196137E2412F0FCDA1AF44A524D7265A1547835"
    };

    private enum PauseReason { None, Disabled, Dictation, WakeDetected, SessionLocked, Failed }
    private sealed record AudioFrameBlock(float[] Samples, int Count);

    private readonly SemaphoreSlim _lifecycleGate = new(1, 1);
    private readonly string _modelDirectory;
    private AudioGraph? _graph;
    private AudioDeviceInputNode? _inputNode;
    private AudioFrameOutputNode? _outputNode;
    private Channel<AudioFrameBlock>? _frames;
    private CancellationTokenSource? _captureStop;
    private Process? _workerProcess;
    private WindowsKillOnCloseJob? _workerJob;
    private Task? _frameForwardTask;
    private Task? _workerOutputTask;
    private int _pauseReason = (int)PauseReason.Disabled;
    private int _generation;
    private int _captureActive;
    private int _wakePending;
    private int _disposed;
    private string _status = "Sherpa KWS 候选未启用；麦克风未采集。";

    public SherpaAudioGraphWakeWordListener(string? modelDirectory)
    {
        _modelDirectory = modelDirectory ?? string.Empty;
    }

    public string Status => Volatile.Read(ref _status);
    public bool IsListening => Volatile.Read(ref _captureActive) == 1 && CurrentPauseReason == PauseReason.None;
    public event Action? WakeWordDetected;
    public event Action<string>? StatusChanged;

    public async Task<string> SetEnabledAsync(bool enabled, CancellationToken cancellationToken = default)
    {
        await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(true);
        try
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            if (!enabled)
            {
                SetPauseReason(PauseReason.Disabled);
                SystemEvents.SessionSwitch -= OnSessionSwitch;
                await StopCaptureCoreAsync();
                SetStatus("Sherpa KWS 唤醒已关闭；麦克风不再用于检测。");
                return _status;
            }

            if (IsListening) return _status;
            if (Volatile.Read(ref _wakePending) != 0)
                return "Sherpa KWS 正在完成唤醒后的停麦；请稍候。";
            SetPauseReason(PauseReason.None);
            if (!WindowsPackageIdentity.IsPresent)
            {
                SetPauseReason(PauseReason.Failed);
                SetStatus("Sherpa KWS 候选仅允许从已安装 MSIX 启用；当前开发实例没有打开麦克风。");
                return _status;
            }

            // Subscribe before the asynchronous graph/model startup. A lock can arrive while
            // AudioGraph is being created; subscribing only after StartCaptureCoreAsync would
            // leave a window in which the microphone could remain active on the locked desktop.
            SystemEvents.SessionSwitch -= OnSessionSwitch;
            SystemEvents.SessionSwitch += OnSessionSwitch;
            if (!WindowsNotificationMonitor.IsUnlockedInputDesktop())
            {
                SetPauseReason(PauseReason.SessionLocked);
                SetStatus("Windows 会话已锁定；Sherpa KWS 麦克风监听保持关闭。");
                return _status;
            }
            if (!Directory.Exists(_modelDirectory))
            {
                SystemEvents.SessionSwitch -= OnSessionSwitch;
                SetPauseReason(PauseReason.Failed);
                SetStatus("未找到本机 Sherpa KWS 评估模型；唤醒监听保持关闭。");
                return _status;
            }

            try
            {
                VerifyLockedModelFiles(_modelDirectory);
                await StartCaptureCoreAsync(cancellationToken);
                if (!WindowsNotificationMonitor.IsUnlockedInputDesktop())
                {
                    SetPauseReason(PauseReason.SessionLocked);
                    await StopCaptureCoreAsync();
                    SetStatus("Windows 会话在麦克风启动期间锁定；Sherpa KWS 已停麦，等待解锁。");
                    return _status;
                }
                SetStatus("Sherpa KWS 实验候选正在独立低优先级 CPU 进程中监听；音频经有界本地管道短时处理、不保存，唤醒后立即停麦。真实误唤醒率尚未验收。");
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                SystemEvents.SessionSwitch -= OnSessionSwitch;
                SetPauseReason(PauseReason.Disabled);
                await StopCaptureCoreAsync();
                throw;
            }
            catch (Exception error) when (error is not OutOfMemoryException and not StackOverflowException)
            {
                SystemEvents.SessionSwitch -= OnSessionSwitch;
                SetPauseReason(PauseReason.Failed);
                await StopCaptureCoreAsync();
                SetStatus($"Sherpa KWS 未能启动；麦克风已关闭（0x{error.HResult:X8}）。");
            }
            return _status;
        }
        finally { _lifecycleGate.Release(); }
    }

    public async Task PauseForDictationAsync(CancellationToken cancellationToken = default)
    {
        await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(true);
        try
        {
            if (!IsListening || Volatile.Read(ref _disposed) != 0) return;
            SetPauseReason(PauseReason.Dictation);
            await StopCaptureCoreAsync();
            SetStatus("正在使用麦克风录音；Sherpa KWS 唤醒监听已暂停。");
        }
        finally { _lifecycleGate.Release(); }
    }

    public async Task ResumeAfterDictationAsync(CancellationToken cancellationToken = default)
    {
        await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(true);
        try
        {
            if (CurrentPauseReason is not (PauseReason.Dictation or PauseReason.WakeDetected)
                || Volatile.Read(ref _disposed) != 0) return;
            if (!WindowsNotificationMonitor.IsUnlockedInputDesktop())
            {
                SetPauseReason(PauseReason.SessionLocked);
                SetStatus("Windows 会话已锁定；Sherpa KWS 麦克风监听保持关闭。");
                return;
            }
            SetPauseReason(PauseReason.None);
            try
            {
                VerifyLockedModelFiles(_modelDirectory);
                await StartCaptureCoreAsync(cancellationToken);
                SetStatus("Sherpa KWS 实验候选正在独立低优先级 CPU 进程中监听；音频经有界本地管道短时处理、不保存。");
            }
            catch (Exception error) when (error is not OutOfMemoryException and not StackOverflowException)
            {
                SetPauseReason(PauseReason.Failed);
                await StopCaptureCoreAsync();
                SetStatus($"Sherpa KWS 恢复失败；麦克风已关闭（0x{error.HResult:X8}）。");
            }
        }
        finally { _lifecycleGate.Release(); }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        await _lifecycleGate.WaitAsync().ConfigureAwait(false);
        try
        {
            SetPauseReason(PauseReason.Disabled);
            SystemEvents.SessionSwitch -= OnSessionSwitch;
            await StopCaptureCoreAsync();
            SetStatus("小K已退出；Sherpa KWS 麦克风监听已停止。");
        }
        finally { _lifecycleGate.Release(); }
    }

    private async Task StartCaptureCoreAsync(CancellationToken cancellationToken)
    {
        if (_graph is not null) return;
        if (_workerProcess is { } previousWorker)
        {
            if (!previousWorker.HasExited)
                throw new InvalidOperationException("上一次Sherpa KWS worker尚未退出；为避免并行占用CPU，拒绝重新打开麦克风。");
            await WaitForWorkerTasksAsync(_frameForwardTask, _workerOutputTask).ConfigureAwait(true);
            _frameForwardTask = null;
            _workerOutputTask = null;
            if (_frames is { } previousFrames)
                while (previousFrames.Reader.TryRead(out var pending)) ReturnFrame(pending.Samples);
            _frames = null;
            _captureStop?.Dispose();
            _captureStop = null;
            _workerJob?.Dispose();
            _workerJob = null;
            previousWorker.Dispose();
            _workerProcess = null;
        }

        cancellationToken.ThrowIfCancellationRequested();
        var worker = await StartKwsWorkerProcessAsync(_modelDirectory, cancellationToken).ConfigureAwait(true);
        _workerProcess = worker.Process;
        _workerJob = worker.Job;
        AudioGraph? graph = null;
        AudioDeviceInputNode? input = null;
        AudioFrameOutputNode? output = null;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var encoding = AudioEncodingProperties.CreatePcm(SherpaKeywordSpotter.SampleRate, 1, 32);
            encoding.Subtype = MediaEncodingSubtypes.Float;
            encoding.Bitrate = SherpaKeywordSpotter.SampleRate * sizeof(float) * 8;
            var settings = new AudioGraphSettings(AudioRenderCategory.Media)
            {
                EncodingProperties = encoding,
                QuantumSizeSelectionMode = Windows.Media.Audio.QuantumSizeSelectionMode.SystemDefault
            };
            var graphResult = await AudioGraph.CreateAsync(settings);
            if (graphResult.Status != AudioGraphCreationStatus.Success)
                throw new InvalidOperationException($"AudioGraph 创建失败：{graphResult.Status}");
            graph = graphResult.Graph;
            cancellationToken.ThrowIfCancellationRequested();

            var inputResult = await graph.CreateDeviceInputNodeAsync(MediaCategory.Speech, encoding);
            if (inputResult.Status != AudioDeviceNodeCreationStatus.Success)
                throw new UnauthorizedAccessException($"麦克风输入节点创建失败：{inputResult.Status}");
            input = inputResult.DeviceInputNode;
            cancellationToken.ThrowIfCancellationRequested();
            output = graph.CreateFrameOutputNode(encoding);
            output.ConsumeInput = true;
            input.AddOutgoingConnection(output);

            var channel = Channel.CreateBounded<AudioFrameBlock>(new BoundedChannelOptions(FrameQueueCapacity)
            {
                FullMode = BoundedChannelFullMode.Wait,
                SingleReader = true,
                SingleWriter = true,
                AllowSynchronousContinuations = false
            });
            var cancellation = new CancellationTokenSource();
            _graph = graph;
            graph = null;
            _inputNode = input;
            input = null;
            _outputNode = output;
            output = null;
            _frames = channel;
            _captureStop = cancellation;
            var generation = Interlocked.Increment(ref _generation);
            Interlocked.Exchange(ref _wakePending, 0);
            Volatile.Write(ref _captureActive, 1);
            var activeWorker = _workerProcess ?? throw new InvalidOperationException("Sherpa KWS worker未启动。");
            _frameForwardTask = Task.Run(() => ForwardFramesAsync(channel.Reader, activeWorker,
                generation, cancellation.Token));
            _workerOutputTask = Task.Run(() => ReadWorkerOutputAsync(activeWorker,
                generation, cancellation.Token));
            _graph!.QuantumStarted += OnQuantumStarted;
            _graph.UnrecoverableErrorOccurred += OnGraphError;
            _outputNode!.Start();
            _graph.Start();
        }
        catch
        {
            Volatile.Write(ref _captureActive, 0);
            if (_graph is not null || _workerProcess is not null) await StopCaptureCoreAsync();
            else
            {
                output?.Dispose();
                input?.Dispose();
                graph?.Dispose();
            }
            throw;
        }
        finally
        {
            output?.Dispose();
            input?.Dispose();
            graph?.Dispose();
        }
    }

    private unsafe void OnQuantumStarted(AudioGraph sender, object args)
    {
        if (Volatile.Read(ref _captureActive) != 1 || _outputNode is not { } output || _frames is not { } frames) return;
        float[]? samples = null;
        try
        {
            using var frame = output.GetFrame();
            using var buffer = frame.LockBuffer(AudioBufferAccessMode.Read);
            using var reference = buffer.CreateReference();
            var access = reference.As<IMemoryBufferByteAccess>();
            access.GetBuffer(out byte* data, out uint capacityBytes);
            var byteCount = checked((int)buffer.Length);
            if (byteCount <= 0 || byteCount > capacityBytes || byteCount % sizeof(float) != 0)
                throw new InvalidDataException("AudioGraph 麦克风帧格式无效。");

            var count = byteCount / sizeof(float);
            if (count > SherpaKeywordSpotter.MaximumSamplesPerCall)
                throw new InvalidDataException("AudioGraph 单帧超过 KWS 输入上限。");
            samples = ArrayPool<float>.Shared.Rent(count);
            new ReadOnlySpan<float>((float*)data, count).CopyTo(samples);
            if (!frames.Writer.TryWrite(new AudioFrameBlock(samples, count)))
            {
                ReturnFrame(samples);
                samples = null;
                throw new InvalidOperationException("Sherpa KWS 解码队列已满；为避免丢帧，麦克风监听已停止。");
            }
            samples = null;
        }
        catch (Exception error) when (error is not OutOfMemoryException and not StackOverflowException)
        {
            if (samples is not null) ReturnFrame(samples);
            Volatile.Write(ref _captureActive, 0);
            var generation = Volatile.Read(ref _generation);
            _ = Task.Run(() => FailCaptureAsync($"AudioGraph 音频采集失败（0x{error.HResult:X8}）。", generation));
        }
    }

    private async Task ForwardFramesAsync(ChannelReader<AudioFrameBlock> frames, Process worker,
        int generation, CancellationToken cancellationToken)
    {
        try
        {
            var input = worker.StandardInput.BaseStream;
            var header = new byte[sizeof(int)];
            await foreach (var frame in frames.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                byte[]? payload = null;
                try
                {
                    var byteCount = checked(frame.Count * sizeof(float));
                    payload = ArrayPool<byte>.Shared.Rent(byteCount);
                    SherpaKwsWorkerProtocol.WriteSampleCount(header, frame.Count);
                    MemoryMarshal.AsBytes(frame.Samples.AsSpan(0, frame.Count)).CopyTo(payload);
                    await input.WriteAsync(header, cancellationToken).ConfigureAwait(false);
                    await input.WriteAsync(payload.AsMemory(0, byteCount), cancellationToken).ConfigureAwait(false);
                    await input.FlushAsync(cancellationToken).ConfigureAwait(false);
                }
                finally
                {
                    if (payload is not null)
                    {
                        CryptographicOperations.ZeroMemory(payload);
                        ArrayPool<byte>.Shared.Return(payload);
                    }
                    ReturnFrame(frame.Samples);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception error) when (error is not OutOfMemoryException and not StackOverflowException)
        {
            Volatile.Write(ref _captureActive, 0);
            _ = Task.Run(() => FailCaptureAsync("Sherpa KWS worker音频管道失败；", generation));
        }
        finally
        {
            while (frames.TryRead(out var pending)) ReturnFrame(pending.Samples);
        }
    }

    private async Task ReadWorkerOutputAsync(Process worker, int generation, CancellationToken cancellationToken)
    {
        try
        {
            while (true)
            {
                var line = await worker.StandardOutput.ReadLineAsync(cancellationToken).ConfigureAwait(false);
                if (line is null)
                {
                    if (cancellationToken.IsCancellationRequested) return;
                    throw new InvalidDataException("KWS worker已提前退出。");
                }
                if (line.Length > 64)
                    throw new InvalidDataException("KWS worker协议行超过限制。");

                if (line.StartsWith("KEYWORD:", StringComparison.Ordinal))
                {
                    var label = line["KEYWORD:".Length..];
                    if (!SherpaKwsWorkerProtocol.IsAllowedKeywordLabel(label))
                        throw new InvalidDataException("KWS worker返回未知标签。");
                    if (Interlocked.Exchange(ref _wakePending, 1) == 0)
                    {
                        Volatile.Write(ref _captureActive, 0);
                        _ = Task.Run(() => PauseAfterWakeAsync(generation));
                    }
                    return;
                }

                if (line.StartsWith("ERROR:", StringComparison.Ordinal))
                    throw new InvalidDataException("KWS worker报告解码失败。");
                throw new InvalidDataException("KWS worker协议响应无效。");
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception error) when (error is not OutOfMemoryException and not StackOverflowException)
        {
            Volatile.Write(ref _captureActive, 0);
            _ = Task.Run(() => FailCaptureAsync("Sherpa KWS隔离worker异常退出；", generation));
        }
    }

    private static async Task<(Process Process, WindowsKillOnCloseJob Job)> StartKwsWorkerProcessAsync(
        string modelDirectory, CancellationToken cancellationToken)
    {
        if (!WindowsPackageIdentity.IsPresent)
            throw new InvalidOperationException("Sherpa KWS worker仅允许从已安装MSIX启动。");
        if (!Path.IsPathFullyQualified(modelDirectory))
            throw new InvalidOperationException("Sherpa KWS模型路径必须是本机绝对路径。");
        var executable = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(executable) || !File.Exists(executable))
            throw new FileNotFoundException("无法定位已安装Host可执行文件以启动KWS worker。");

        var start = new ProcessStartInfo
        {
            FileName = executable,
            WorkingDirectory = AppContext.BaseDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = new System.Text.UTF8Encoding(false),
            StandardOutputEncoding = new System.Text.UTF8Encoding(false),
            StandardErrorEncoding = new System.Text.UTF8Encoding(false)
        };
        start.ArgumentList.Add("--kws-worker");
        start.ArgumentList.Add("--model-dir");
        start.ArgumentList.Add(Path.GetFullPath(modelDirectory));

        var job = WindowsKillOnCloseJob.Create();
        var process = new Process { StartInfo = start, EnableRaisingEvents = true };
        try
        {
            if (!process.Start()) throw new InvalidOperationException("无法启动隔离Sherpa KWS worker。");
            job.Assign(process);
            process.PriorityClass = ProcessPriorityClass.BelowNormal;
            _ = DrainWorkerStandardErrorAsync(process);

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(45));
            var ready = await process.StandardOutput.ReadLineAsync(timeout.Token).ConfigureAwait(false);
            if (!string.Equals(ready, "READY", StringComparison.Ordinal))
                throw new InvalidDataException("Sherpa KWS worker没有完成握手。");
            await process.StandardInput.WriteLineAsync("START".AsMemory(), timeout.Token).ConfigureAwait(false);
            await process.StandardInput.FlushAsync(timeout.Token).ConfigureAwait(false);
            var started = await process.StandardOutput.ReadLineAsync(timeout.Token).ConfigureAwait(false);
            if (!string.Equals(started, "STARTED", StringComparison.Ordinal))
                throw new InvalidDataException("Sherpa KWS worker未通过模型检查和加载。");
            return (process, job);
        }
        catch
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception) { }
            job.Dispose();
            process.Dispose();
            throw;
        }
    }

    private static async Task DrainWorkerStandardErrorAsync(Process process)
    {
        try { await process.StandardError.BaseStream.CopyToAsync(Stream.Null).ConfigureAwait(false); }
        catch (Exception error) when (error is IOException or InvalidOperationException) { }
    }

    private async Task PauseAfterWakeAsync(int generation)
    {
        await _lifecycleGate.WaitAsync().ConfigureAwait(false);
        var detected = false;
        try
        {
            if (generation != Volatile.Read(ref _generation) || CurrentPauseReason != PauseReason.None
                || _graph is null || Volatile.Read(ref _disposed) != 0) return;
            SetPauseReason(PauseReason.WakeDetected);
            await StopCaptureCoreAsync();
            SetStatus("Sherpa KWS 检测到“小K”唤醒短语；麦克风已暂停，请点击语音按钮录入任务。");
            detected = true;
        }
        finally
        {
            Interlocked.Exchange(ref _wakePending, 0);
            _lifecycleGate.Release();
        }
        if (detected) WakeWordDetected?.Invoke();
    }

    private async Task FailCaptureAsync(string status, int generation)
    {
        await _lifecycleGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (generation != Volatile.Read(ref _generation) || CurrentPauseReason != PauseReason.None || _graph is null) return;
            SetPauseReason(PauseReason.Failed);
            await StopCaptureCoreAsync();
            SetStatus(status + "麦克风已关闭，请检查设备后在设置中重新启用。");
        }
        finally { _lifecycleGate.Release(); }
    }

    private void OnGraphError(AudioGraph sender, AudioGraphUnrecoverableErrorOccurredEventArgs args)
    {
        Volatile.Write(ref _captureActive, 0);
        var generation = Volatile.Read(ref _generation);
        _ = Task.Run(() => FailCaptureAsync($"Windows 音频设备报告不可恢复错误 {args.Error}；", generation));
    }

    private async Task StopCaptureCoreAsync()
    {
        Volatile.Write(ref _captureActive, 0);
        Interlocked.Increment(ref _generation);
        var graph = _graph;
        _graph = null;
        if (graph is not null)
        {
            graph.QuantumStarted -= OnQuantumStarted;
            graph.UnrecoverableErrorOccurred -= OnGraphError;
            try { graph.Stop(); } catch (Exception) { }
        }
        try { _outputNode?.Stop(); } catch (Exception) { }
        try { _inputNode?.Dispose(); } catch (Exception) { }
        _inputNode = null;
        try { _outputNode?.Dispose(); } catch (Exception) { }
        _outputNode = null;
        try { graph?.Dispose(); } catch (Exception) { }

        _frames?.Writer.TryComplete();
        var cancellation = _captureStop;
        _captureStop = null;
        try { cancellation?.Cancel(); } catch (ObjectDisposedException) { }
        try
        {
            var worker = _workerProcess;
            if (worker is not null)
            {
                var exited = await StopWorkerProcessAsync(worker, _workerJob).ConfigureAwait(false);
                _workerJob?.Dispose();
                _workerJob = null;
                if (!exited)
                    throw new TimeoutException("Sherpa KWS worker未能在强制停止后退出；拒绝重新打开麦克风。");
                worker.Dispose();
                _workerProcess = null;
            }

            await WaitForWorkerTasksAsync(_frameForwardTask, _workerOutputTask).ConfigureAwait(false);
            _frameForwardTask = null;
            _workerOutputTask = null;

            if (_frames is { } frames)
                while (frames.Reader.TryRead(out var pending)) ReturnFrame(pending.Samples);
            _frames = null;
        }
        finally
        {
            try { cancellation?.Dispose(); } catch (ObjectDisposedException) { }
        }
    }

    private static async Task<bool> StopWorkerProcessAsync(Process worker, WindowsKillOnCloseJob? job)
    {
        try { worker.StandardInput.BaseStream.Dispose(); }
        catch (Exception error) when (error is IOException or InvalidOperationException) { }

        try
        {
            if (!worker.HasExited)
            {
                var waitForGracefulExit = worker.WaitForExitAsync();
                var firstWait = await Task.WhenAny(waitForGracefulExit, Task.Delay(TimeSpan.FromMilliseconds(500)))
                    .ConfigureAwait(false);
                if (!ReferenceEquals(firstWait, waitForGracefulExit))
                {
                    try { worker.Kill(entireProcessTree: true); }
                    catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception) { }
                }
            }
        }
        finally { job?.Dispose(); }

        try
        {
            if (!worker.HasExited)
                await worker.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        }
        catch (TimeoutException) { return false; }
        catch (InvalidOperationException) { return true; }
        return true;
    }

    private static async Task WaitForWorkerTasksAsync(Task? frameForwardTask, Task? workerOutputTask)
    {
        var tasks = new[] { frameForwardTask, workerOutputTask }.Where(task => task is not null).Cast<Task>().ToArray();
        if (tasks.Length == 0) return;
        var all = Task.WhenAll(tasks);
        var completed = await Task.WhenAny(all, Task.Delay(TimeSpan.FromSeconds(2))).ConfigureAwait(false);
        if (!ReferenceEquals(completed, all))
            throw new TimeoutException("Sherpa KWS worker管道任务尚未退出；拒绝重新打开麦克风。");
        try { await all.ConfigureAwait(false); } catch (Exception) { }
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
            if (Volatile.Read(ref _disposed) != 0 || !_enabled) return;
            SetPauseReason(PauseReason.SessionLocked);
            await StopCaptureCoreAsync();
            SetStatus("Windows 会话已锁定；Sherpa KWS 麦克风监听已暂停。");
        }
        finally { _lifecycleGate.Release(); }
    }

    private async Task ResumeAfterSessionUnlockAsync()
    {
        await _lifecycleGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (Volatile.Read(ref _disposed) != 0 || !_enabled || CurrentPauseReason != PauseReason.SessionLocked
                || !WindowsNotificationMonitor.IsUnlockedInputDesktop()) return;
            SetPauseReason(PauseReason.None);
            VerifyLockedModelFiles(_modelDirectory);
            await StartCaptureCoreAsync(CancellationToken.None);
            SetStatus("Sherpa KWS 实验候选已在独立低优先级 CPU 进程中恢复监听；音频经有界本地管道短时处理、不保存。");
        }
        catch (Exception error) when (error is not OutOfMemoryException and not StackOverflowException)
        {
            SetPauseReason(PauseReason.Failed);
            await StopCaptureCoreAsync();
            SetStatus($"解锁后 Sherpa KWS 恢复失败；麦克风已关闭（0x{error.HResult:X8}）。");
        }
        finally { _lifecycleGate.Release(); }
    }

    private bool _enabled => CurrentPauseReason is PauseReason.None or PauseReason.SessionLocked or PauseReason.Dictation or PauseReason.WakeDetected;
    private PauseReason CurrentPauseReason => (PauseReason)Volatile.Read(ref _pauseReason);
    private void SetPauseReason(PauseReason reason) => Volatile.Write(ref _pauseReason, (int)reason);

    private void SetStatus(string value)
    {
        Volatile.Write(ref _status, value);
        StatusChanged?.Invoke(value);
    }

    private static void ReturnFrame(float[] samples)
    {
        Array.Clear(samples);
        ArrayPool<float>.Shared.Return(samples);
    }

    internal static void VerifyLockedModelFiles(string modelDirectory)
    {
        var root = Path.GetFullPath(modelDirectory);
        if ((File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0)
            throw new UnauthorizedAccessException("KWS 模型目录不能是重解析点。");
        foreach (var item in ExpectedModelHashes)
        {
            var path = Path.GetFullPath(Path.Combine(root, item.Key));
            if (!path.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar,
                    StringComparison.OrdinalIgnoreCase))
                throw new UnauthorizedAccessException("KWS 模型文件越出锁定目录。");
            var info = new FileInfo(path);
            if (!info.Exists || (info.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new FileNotFoundException($"KWS 锁定模型文件缺失或不是普通文件：{item.Key}");
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
                1024 * 1024, FileOptions.SequentialScan);
            var actual = Convert.ToHexString(SHA256.HashData(stream));
            if (!string.Equals(actual, item.Value, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"KWS 模型散列不匹配：{item.Key}");
        }
    }

    internal static string? ResolveModelDirectory(string? workspaceRoot)
    {
        var candidates = new List<string>();
        if (!string.IsNullOrWhiteSpace(workspaceRoot))
            candidates.Add(Path.Combine(workspaceRoot, "models", ModelRelativePath.Replace('/', Path.DirectorySeparatorChar)));
        candidates.Add(Path.Combine(@"D:\XiaoK\Models", ModelRelativePath.Replace('/', Path.DirectorySeparatorChar)));
        return candidates.FirstOrDefault(Directory.Exists);
    }

    [ComImport]
    [Guid("5B0D3235-4DBA-4D44-865E-8F1D0E4FD04D")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private unsafe interface IMemoryBufferByteAccess
    {
        void GetBuffer(out byte* buffer, out uint capacity);
    }
}
