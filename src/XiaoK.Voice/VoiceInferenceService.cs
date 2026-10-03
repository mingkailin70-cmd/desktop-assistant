using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;
using XiaoK.Core;
using XiaoK.Inference;

namespace XiaoK.Voice;

public sealed record SpeechRecognition(string Text, string Language);

/// <summary>
/// 本地语音推理入口。ASR 与 TTS 均由 ModelBroker 排队；当前首测强制使用 CPU，避免未经测量占用独显。
/// </summary>
public sealed class VoiceInferenceService : IAsyncDisposable
{
    public const int MaximumAudioBytes = 12 * 1024 * 1024;
    private const int MaximumTextLength = 1000;
    private readonly ModelBroker _broker;
    private readonly PythonVoiceModelRuntime _asr;
    private readonly PythonVoiceModelRuntime _tts;
    private readonly object _lifecycleGate = new();
    private readonly CancellationTokenSource _shutdown = new();
    private TaskCompletionSource _operationsDrained = CompletedSource();
    private Task? _disposeTask;
    private int _activeOperations;
    private bool _disposing;

    private VoiceInferenceService(ModelBroker broker, PythonVoiceModelRuntime asr, PythonVoiceModelRuntime tts)
    {
        _broker = broker;
        _asr = asr;
        _tts = tts;
    }

    public string Status => "本地 ASR/TTS 工作进程已配置（CPU 按需加载）；合成往返单样例通过，麦克风未采集，设备与语音质量仍待验收。";

    /// <summary>Loads a verified local model only for the duration of this scheduled inference call.</summary>
    public Task<SpeechRecognition> TranscribeWavAsync(ReadOnlyMemory<byte> wav, bool forceChinese,
        CancellationToken cancellationToken)
    {
        if (wav.IsEmpty || wav.Length > MaximumAudioBytes)
            throw new ArgumentOutOfRangeException(nameof(wav), "音频必须是非空 WAV，且不能超过12 MiB。");
        var payload = wav.ToArray();
        return TrackAsync(async token =>
        {
            try
            {
                return await _broker.RunCompetingModelInteractiveAsync(_asr,
                    inner => _asr.TranscribeWavAsync(payload, forceChinese, inner), token).ConfigureAwait(false);
            }
            finally { CryptographicOperations.ZeroMemory(payload); }
        }, cancellationToken, () => CryptographicOperations.ZeroMemory(payload));
    }

    /// <summary>Generates a WAV using the pinned CustomVoice model and its fixed Chinese preset speaker.</summary>
    public Task<byte[]> SynthesizeChineseWavAsync(string text, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length > MaximumTextLength)
            throw new ArgumentOutOfRangeException(nameof(text), "播报文本长度必须为1到1000个字符。");
        return TrackAsync(token => _broker.RunCompetingModelInteractiveAsync(_tts,
            inner => _tts.SynthesizeChineseWavAsync(text, inner), token), cancellationToken);
    }

    /// <summary>Creates development runtimes from the repository's locked models and Python environments.</summary>
    public static VoiceInferenceService? TryCreateForWorkspace(string workspaceRoot, string voiceEnvironmentRoot,
        string workerScriptPath, ModelBroker broker, out string status)
    {
        ArgumentNullException.ThrowIfNull(broker);
        try
        {
            var root = Path.GetFullPath(workspaceRoot);
            return TryCreateFromLayout(Path.Combine(root, "model-lock", "models.lock.json"), root,
                Path.Combine(root, "models"), voiceEnvironmentRoot, workerScriptPath, root, broker, out status);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException
                                   or ArgumentException or NotSupportedException)
        {
            return Missing("语音：开发仓库路径无效；语音请求已关闭，麦克风未采集。", out status);
        }
    }

    /// <summary>Creates installed runtimes from the signed package manifest and configured external roots.</summary>
    public static VoiceInferenceService? TryCreateForInstallation(string voiceEnvironmentRoot, string modelsRoot,
        string packageRoot, ModelBroker broker, out string status)
    {
        ArgumentNullException.ThrowIfNull(broker);
        try
        {
            var package = Path.GetFullPath(packageRoot);
            return TryCreateFromLayout(Path.Combine(package, "model-lock", "models.lock.json"), package,
                modelsRoot, voiceEnvironmentRoot, Path.Combine(package, "voice_worker.py"), package, broker, out status);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException
                                   or ArgumentException or NotSupportedException)
        {
            return Missing("语音：安装包目录无效；语音请求已关闭，麦克风未采集。", out status);
        }
    }

    private static VoiceInferenceService? TryCreateFromLayout(string modelsLockPath, string manifestRoot,
        string modelsRoot, string voiceEnvironmentRoot, string workerScriptPath, string workerRoot,
        ModelBroker broker, out string status)
    {
        try
        {
            var lockRoot = Path.GetFullPath(manifestRoot);
            var modelRoot = Path.GetFullPath(modelsRoot);
            var environmentRoot = Path.GetFullPath(voiceEnvironmentRoot);
            var lockPath = Path.GetFullPath(modelsLockPath);
            var worker = Path.GetFullPath(workerScriptPath);
            var allowedWorkerRoot = Path.GetFullPath(workerRoot);
            if (!LocalSearchRootPolicy.IsLocalDrivePath(lockRoot)
                || !LocalSearchRootPolicy.IsLocalDrivePath(modelRoot)
                || !LocalSearchRootPolicy.IsLocalDrivePath(environmentRoot)
                || !LocalSearchRootPolicy.IsLocalDrivePath(allowedWorkerRoot))
                throw new InvalidDataException("语音清单、模型、运行环境与工作进程必须位于本机磁盘。");
            var asrPython = Path.Combine(environmentRoot, "asr", "Scripts", "python.exe");
            var ttsPython = Path.Combine(environmentRoot, "tts", "Scripts", "python.exe");
            if (!File.Exists(lockPath) || !File.Exists(worker) || !File.Exists(asrPython) || !File.Exists(ttsPython))
                return Missing("语音：模型、工作进程或独立 Python 环境不完整；麦克风未采集。", out status);

            var asrDefinition = ReadDefinition(lockPath, lockRoot, modelRoot, environmentRoot,
                asrPython, allowedWorkerRoot, worker, "qwen3-asr-0.6b",
                "5eb144179a02acc5e5ba31e748d22b0cf3e303b0");
            var ttsDefinition = ReadDefinition(lockPath, lockRoot, modelRoot, environmentRoot,
                ttsPython, allowedWorkerRoot, worker, "qwen3-tts-12hz-0.6b-customvoice",
                "85e237c12c027371202489a0ec509ded67b5e4b5");
            var asr = new PythonVoiceModelRuntime("asr", asrDefinition);
            var tts = new PythonVoiceModelRuntime("tts", ttsDefinition);
            status = "本地 ASR/TTS 工作进程已配置（CPU 按需加载）；合成往返单样例通过，麦克风未采集，设备与语音质量仍待验收。";
            return new VoiceInferenceService(broker, asr, tts);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException or JsonException
                                   or InvalidDataException or ArgumentException or NotSupportedException
                                   or InvalidOperationException or KeyNotFoundException or FormatException)
        {
            return Missing("语音：模型锁清单或本地运行环境无效；语音请求已关闭，麦克风未采集。", out status);
        }
    }

    public ValueTask DisposeAsync()
    {
        TaskCompletionSource completion;
        Task activeOperationsDrained;
        lock (_lifecycleGate)
        {
            if (_disposeTask is not null) return new ValueTask(_disposeTask);
            _disposing = true;
            activeOperationsDrained = _activeOperations == 0 ? Task.CompletedTask : _operationsDrained.Task;
            completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _disposeTask = completion.Task;
        }
        Exception? cancellationFailure = null;
        try { _shutdown.Cancel(); }
        catch (Exception ex) { cancellationFailure = ex; }
        _ = CompleteDisposeAsync(completion, activeOperationsDrained, cancellationFailure);
        return new ValueTask(completion.Task);
    }

    private async Task CompleteDisposeAsync(TaskCompletionSource completion, Task activeOperationsDrained,
        Exception? cancellationFailure)
    {
        try
        {
            await activeOperationsDrained.ConfigureAwait(false);
            try { await _asr.DisposeAsync().ConfigureAwait(false); }
            finally
            {
                try { await _tts.DisposeAsync().ConfigureAwait(false); }
                finally { _shutdown.Dispose(); }
            }
            if (cancellationFailure is not null) throw cancellationFailure;
            completion.TrySetResult();
        }
        catch (Exception ex) { completion.TrySetException(ex); }
    }

    private async Task<T> TrackAsync<T>(Func<CancellationToken, Task<T>> operation,
        CancellationToken cancellationToken, Action? rejectedCleanup = null)
    {
        lock (_lifecycleGate)
        {
            if (_disposing)
            {
                rejectedCleanup?.Invoke();
                throw new ObjectDisposedException(nameof(VoiceInferenceService));
            }
            if (_activeOperations++ == 0)
                _operationsDrained = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        CancellationTokenSource? linked = null;
        try
        {
            linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _shutdown.Token);
            return await operation(linked.Token).ConfigureAwait(false);
        }
        finally
        {
            linked?.Dispose();
            lock (_lifecycleGate)
            {
                if (--_activeOperations == 0) _operationsDrained.TrySetResult();
            }
        }
    }

    private static TaskCompletionSource CompletedSource()
    {
        var source = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        source.SetResult();
        return source;
    }

    private static VoiceInferenceService? Missing(string message, out string status)
    {
        status = message;
        return null;
    }

    private static VoiceModelDefinition ReadDefinition(string lockPath, string lockRoot, string modelsRoot,
        string environmentRoot, string pythonPath, string workerRoot, string workerPath, string modelId,
        string expectedRevision)
    {
        EnsureContained(lockRoot, lockPath);
        EnsureContained(environmentRoot, pythonPath);
        EnsureContained(workerRoot, workerPath);
        EnsureNoReparseComponents(lockPath);
        using var stream = new FileStream(lockPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length is <= 0 or > 2 * 1024 * 1024) throw new InvalidDataException("模型锁清单大小无效。");
        using var document = JsonDocument.Parse(stream, new JsonDocumentOptions { MaxDepth = 32 });
        if (!document.RootElement.TryGetProperty("models", out var models) || models.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("模型锁清单缺少 models 列表。");

        JsonElement selected = default;
        foreach (var item in models.EnumerateArray())
        {
            if (item.TryGetProperty("id", out var id) && id.GetString() == modelId)
            {
                selected = item;
                break;
            }
        }
        if (selected.ValueKind != JsonValueKind.Object
            || GetString(selected, "status") != "downloaded_and_verified"
            || GetString(selected, "revision") != expectedRevision
            || GetString(selected, "license") != "apache-2.0"
            || !selected.TryGetProperty("requiredForP0", out var required) || !required.GetBoolean())
            throw new InvalidDataException("语音模型未通过固定版本、许可或下载状态检查。");

        var localDirectory = GetString(selected, "localDirectory");
        if (!IsSafeRelativePath(localDirectory)) throw new InvalidDataException("语音模型锁定路径无效。");
        var modelDirectory = Path.GetFullPath(Path.Combine(modelsRoot, localDirectory));
        EnsureContained(modelsRoot, modelDirectory);
        EnsureNoReparseComponents(modelDirectory);

        if (!selected.TryGetProperty("files", out var filesElement) || filesElement.ValueKind != JsonValueKind.Array
            || filesElement.GetArrayLength() is < 1 or > 128)
            throw new InvalidDataException("语音模型文件清单无效。");
        var files = new List<VoiceModelFile>();
        foreach (var file in filesElement.EnumerateArray())
        {
            var name = GetString(file, "name");
            if (!IsSafeRelativePath(name)) throw new InvalidDataException("语音权重文件路径无效。");
            var size = file.GetProperty("upstreamReportedSizeBytes").GetInt64();
            var expected = GetString(file, "expectedUpstreamSha256");
            var local = GetString(file, "localVerifiedSha256");
            if (size is <= 0 or > 20L * 1024 * 1024 * 1024 || !IsSha256(expected)
                || !string.Equals(expected, local, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("语音模型文件散列未锁定。");
            var filePath = Path.GetFullPath(Path.Combine(modelDirectory, name));
            EnsureContained(modelDirectory, filePath);
            EnsureNoReparseComponents(filePath);
            var info = new FileInfo(filePath);
            if (!info.Exists || info.Length != size) throw new InvalidDataException("语音模型文件缺失或大小不符。");
            files.Add(new VoiceModelFile(name, size, expected));
        }

        EnsureNoReparseComponents(pythonPath);
        EnsureNoReparseComponents(workerPath);
        if (!File.Exists(pythonPath) || !File.Exists(workerPath)) throw new FileNotFoundException("语音运行时文件缺失。");
        return new VoiceModelDefinition(modelId, modelDirectory, pythonPath, workerPath, files);
    }

    private static string GetString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString() ?? string.Empty
            : string.Empty;

    private static bool IsSafeRelativePath(string value) =>
        !string.IsNullOrWhiteSpace(value) && !Path.IsPathRooted(value)
        && value.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries)
            .All(part => part is not "." and not ".." && part.IndexOfAny(Path.GetInvalidFileNameChars()) < 0);

    private static void EnsureContained(string root, string candidate)
    {
        var fullRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var fullCandidate = Path.GetFullPath(candidate);
        if (!fullCandidate.StartsWith(fullRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("语音模型路径越出模型目录。");
    }

    private static void EnsureNoReparseComponents(string path)
    {
        var full = Path.GetFullPath(path);
        var root = Path.GetPathRoot(full) ?? throw new InvalidDataException("本地路径无效。");
        var current = root;
        foreach (var component in full[root.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, component);
            if ((File.Exists(current) || Directory.Exists(current))
                && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("语音运行路径不能包含重解析点。");
        }
    }

    private static bool IsSha256(string value) => value.Length == 64 && value.All(Uri.IsHexDigit);
}

internal sealed record VoiceModelFile(string Name, long Size, string Sha256);
internal sealed record VoiceModelDefinition(string ModelId, string ModelDirectory, string PythonPath,
    string WorkerPath, IReadOnlyList<VoiceModelFile> Files);

internal sealed class PythonVoiceModelRuntime(string task, VoiceModelDefinition definition) : IManagedModelRuntime
{
    private const int MaximumProtocolLineCharacters = 20 * 1024 * 1024;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private Process? _process;
    private Task? _stderrDrain;
    private KillOnCloseJobHandle? _job;
    private readonly List<FileStream> _modelPins = [];
    private int _leases;
    private bool _disposed;
    private readonly StringBuilder _stderrTail = new();

    public string Status => $"本地 {task.ToUpperInvariant()} 模型：固定版本、CPU 工作进程、任务结束即卸载。";

    public async ValueTask<IAsyncDisposable> AcquireAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_disposed) throw new ObjectDisposedException(nameof(PythonVoiceModelRuntime));
            if (_leases != 0) throw new InvalidOperationException("语音模型租约不可重入。");
            if (_process is null || _process.HasExited)
            {
                await StopProcessLockedAsync().ConfigureAwait(false);
                await VerifyFilesAsync(cancellationToken).ConfigureAwait(false);
                await StartProcessLockedAsync(cancellationToken).ConfigureAwait(false);
            }
            _leases = 1;
            return new RuntimeLease(this);
        }
        finally { _gate.Release(); }
    }

    public async ValueTask UnloadIfIdleAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_leases != 0) throw new InvalidOperationException("语音工作进程仍持有模型租约。");
            await StopProcessLockedAsync().ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    public async Task<SpeechRecognition> TranscribeWavAsync(byte[] wav, bool forceChinese,
        CancellationToken cancellationToken)
    {
        var response = await SendRequestAsync(new
        {
            command = "transcribe",
            language = forceChinese ? "Chinese" : "Auto",
            wavBase64 = Convert.ToBase64String(wav)
        }, cancellationToken).ConfigureAwait(false);
        if (!response.RootElement.GetProperty("ok").GetBoolean())
            throw new VoiceWorkerException(ReadErrorCode(response.RootElement));
        return new SpeechRecognition(
            response.RootElement.GetProperty("text").GetString() ?? string.Empty,
            response.RootElement.GetProperty("language").GetString() ?? string.Empty);
    }

    public async Task<byte[]> SynthesizeChineseWavAsync(string text, CancellationToken cancellationToken)
    {
        var response = await SendRequestAsync(new { command = "synthesize", text }, cancellationToken).ConfigureAwait(false);
        if (!response.RootElement.GetProperty("ok").GetBoolean())
            throw new VoiceWorkerException(ReadErrorCode(response.RootElement));
        var encoded = response.RootElement.GetProperty("wavBase64").GetString() ?? string.Empty;
        byte[] wav;
        try { wav = Convert.FromBase64String(encoded); }
        catch (FormatException) { throw new VoiceWorkerException("INVALID_WORKER_AUDIO"); }
        if (wav.Length is 0 or > VoiceInferenceService.MaximumAudioBytes)
            throw new VoiceWorkerException("WORKER_AUDIO_TOO_LARGE");
        return wav;
    }

    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed) return;
            if (_leases != 0) throw new InvalidOperationException("不能在语音推理租约活动时释放工作进程。");
            await StopProcessLockedAsync().ConfigureAwait(false);
            _disposed = true;
        }
        finally { _gate.Release(); }
    }

    private async Task<JsonDocument> SendRequestAsync(object request, CancellationToken cancellationToken)
    {
        var process = _process;
        if (_leases == 0 || process is null || process.HasExited)
            throw new VoiceWorkerException("VOICE_WORKER_NOT_READY");
        var line = JsonSerializer.Serialize(request);
        if (line.Length > MaximumProtocolLineCharacters) throw new VoiceWorkerException("REQUEST_TOO_LARGE");
        try
        {
            await process.StandardInput.WriteLineAsync(line.AsMemory(), cancellationToken).ConfigureAwait(false);
            await process.StandardInput.FlushAsync(cancellationToken).ConfigureAwait(false);
            var responseLine = await process.StandardOutput.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            if (responseLine is null || responseLine.Length > MaximumProtocolLineCharacters)
                throw new VoiceWorkerException("INVALID_WORKER_RESPONSE");
            return JsonDocument.Parse(responseLine, new JsonDocumentOptions { MaxDepth = 8 });
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await TerminateAfterCancellationAsync().ConfigureAwait(false);
            throw;
        }
        catch (Exception ex) when (ex is IOException or JsonException or InvalidOperationException)
        {
            await TerminateAfterCancellationAsync().ConfigureAwait(false);
            throw new VoiceWorkerException("VOICE_WORKER_FAILED", ex);
        }
    }

    private async Task StartProcessLockedAsync(CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo
        {
            FileName = definition.PythonPath,
            WorkingDirectory = definition.ModelDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false),
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false)
        };
        start.ArgumentList.Add("-u");
        start.ArgumentList.Add(definition.WorkerPath);
        start.ArgumentList.Add("--task");
        start.ArgumentList.Add(task);
        start.ArgumentList.Add("--model-dir");
        start.ArgumentList.Add(definition.ModelDirectory);
        ConfigureEnvironment(start);

        var job = CreateKillOnCloseJob();
        var process = new Process { StartInfo = start, EnableRaisingEvents = true };
        try
        {
            if (!process.Start()) throw new InvalidOperationException("无法启动本地语音工作进程。");
            var processHandle = OpenProcess(0x001F0FFF, false, (uint)process.Id);
            if (processHandle == IntPtr.Zero)
                throw new Win32Exception(Marshal.GetLastWin32Error(), "无法打开语音工作进程句柄。");
            try
            {
                if (!AssignProcessToJobObject(job, processHandle))
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "无法配置 Host 退出时回收语音工作进程。");
            }
            finally { CloseHandle(processHandle); }
            _process = process;
            _job = job;
            _stderrTail.Clear();
            _stderrDrain = DrainStandardErrorAsync(process);

            using var startupTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            startupTimeout.CancelAfter(TimeSpan.FromMinutes(3));
            var readyLine = await process.StandardOutput.ReadLineAsync(startupTimeout.Token).ConfigureAwait(false);
            if (readyLine is null || readyLine.Length > 4096)
                throw new VoiceWorkerException("VOICE_WORKER_START_FAILED");
            using var ready = JsonDocument.Parse(readyLine, new JsonDocumentOptions { MaxDepth = 4 });
            if (!ready.RootElement.TryGetProperty("type", out var type) || type.GetString() != "ready"
                || !ready.RootElement.TryGetProperty("ok", out var ok) || !ok.GetBoolean())
                throw new VoiceWorkerException("VOICE_MODEL_LOAD_FAILED");
        }
        catch
        {
            if (_process is null) _process = process;
            if (_job is null) _job = job;
            await StopProcessLockedAsync().ConfigureAwait(false);
            throw;
        }
    }

    private async Task VerifyFilesAsync(CancellationToken cancellationToken)
    {
        var opened = new List<FileStream>(definition.Files.Count);
        try
        {
            foreach (var file in definition.Files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var path = Path.GetFullPath(Path.Combine(definition.ModelDirectory, file.Name));
                var info = new FileInfo(path);
                if (!info.Exists || info.Length != file.Size || (info.Attributes & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException("语音模型文件缺失、大小不符或路径不安全。");
                var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
                    1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
                opened.Add(stream);
                var hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
                if (!CryptographicOperations.FixedTimeEquals(hash, Convert.FromHexString(file.Sha256)))
                    throw new InvalidDataException("语音模型文件 SHA-256 不匹配。");
                stream.Position = 0;
            }
            _modelPins.AddRange(opened);
        }
        catch
        {
            foreach (var stream in opened) await stream.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private static void ConfigureEnvironment(ProcessStartInfo start)
    {
        start.Environment.Clear();
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var system32 = Path.Combine(windows, "System32");
        var temp = Path.Combine(Path.GetTempPath(), "XiaoK", "VoiceTemp");
        Directory.CreateDirectory(temp);
        start.Environment["SystemRoot"] = windows;
        start.Environment["WINDIR"] = windows;
        start.Environment["PATH"] = $"{Path.GetDirectoryName(start.FileName)};{system32}";
        start.Environment["TEMP"] = temp;
        start.Environment["TMP"] = temp;
        start.Environment["USERPROFILE"] = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        start.Environment["LOCALAPPDATA"] = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        start.Environment["APPDATA"] = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        start.Environment["PYTHONNOUSERSITE"] = "1";
        start.Environment["PYTHONUTF8"] = "1";
        start.Environment["PYTHONIOENCODING"] = "utf-8";
        start.Environment["HF_HUB_OFFLINE"] = "1";
        start.Environment["TRANSFORMERS_OFFLINE"] = "1";
        start.Environment["HF_HUB_DISABLE_TELEMETRY"] = "1";
        start.Environment["TOKENIZERS_PARALLELISM"] = "false";
        start.Environment["CUDA_VISIBLE_DEVICES"] = "-1";
        var cache = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "XiaoK", "Cache");
        start.Environment["HF_HOME"] = Path.Combine(cache, "huggingface");
        start.Environment["TORCH_HOME"] = Path.Combine(cache, "torch");
    }

    private async Task DrainStandardErrorAsync(Process process)
    {
        try
        {
            while (await process.StandardError.ReadLineAsync().ConfigureAwait(false) is { } line)
            {
                lock (_stderrTail)
                {
                    _stderrTail.AppendLine(line);
                    if (_stderrTail.Length > 8192) _stderrTail.Remove(0, _stderrTail.Length - 8192);
                }
            }
        }
        catch (IOException) { }
        catch (ObjectDisposedException) { }
    }

    private async Task TerminateAfterCancellationAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try { await StopProcessLockedAsync().ConfigureAwait(false); }
        finally { _gate.Release(); }
    }

    private async Task StopProcessLockedAsync()
    {
        var process = _process;
        var job = _job;
        _process = null;
        _job = null;
        job?.Dispose(); // Closing a kill-on-close job terminates the worker tree.
        if (process is not null)
        {
            try
            {
                if (!process.HasExited)
                {
                    var exit = process.WaitForExitAsync();
                    if (await Task.WhenAny(exit, Task.Delay(TimeSpan.FromSeconds(5))).ConfigureAwait(false) != exit)
                        process.Kill(entireProcessTree: true);
                    await process.WaitForExitAsync().ConfigureAwait(false);
                }
            }
            catch (InvalidOperationException) { }
            catch (Win32Exception) { }
            catch (NotSupportedException) { }
            if (_stderrDrain is not null)
            {
                try { await _stderrDrain.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); }
                catch (TimeoutException) { }
                catch (IOException) { }
            }
            process.Dispose();
        }
        _stderrDrain = null;
        foreach (var pin in _modelPins) await pin.DisposeAsync().ConfigureAwait(false);
        _modelPins.Clear();
    }

    private async ValueTask ReleaseLeaseAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_leases != 1) throw new InvalidOperationException("语音模型租约状态无效。");
            _leases = 0;
        }
        finally { _gate.Release(); }
    }

    private static string ReadErrorCode(JsonElement response)
    {
        if (!response.TryGetProperty("code", out var code) || code.ValueKind != JsonValueKind.String)
            return "INFERENCE_FAILED";
        var value = code.GetString() ?? "INFERENCE_FAILED";
        return value.Length <= 64 ? value : value[..64];
    }

    private static KillOnCloseJobHandle CreateKillOnCloseJob()
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("本地语音工作进程只支持 Windows。");
        var job = CreateJobObjectW(IntPtr.Zero, null);
        if (job.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error(), "无法创建语音工作进程回收作业。");
        var limits = new JobObjectExtendedLimitInformation
        {
            BasicLimitInformation = new JobObjectBasicLimitInformation { LimitFlags = 0x00002000 }
        };
        if (!SetInformationJobObject(job, 9, ref limits, (uint)Marshal.SizeOf<JobObjectExtendedLimitInformation>()))
        {
            var error = Marshal.GetLastWin32Error();
            job.Dispose();
            throw new Win32Exception(error, "无法配置 Host 退出时回收语音工作进程。");
        }
        return job;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectBasicLimitInformation
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize;
        public UIntPtr MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectExtendedLimitInformation
    {
        public JobObjectBasicLimitInformation BasicLimitInformation;
        public IoCounters IoInfo;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }

    private sealed class KillOnCloseJobHandle() : SafeHandleZeroOrMinusOneIsInvalid(true)
    {
        protected override bool ReleaseHandle() => CloseHandle(handle);
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CreateJobObjectW")]
    private static extern KillOnCloseJobHandle CreateJobObjectW(IntPtr jobAttributes, string? name);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetInformationJobObject(KillOnCloseJobHandle job, int informationClass,
        ref JobObjectExtendedLimitInformation information, uint informationLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AssignProcessToJobObject(KillOnCloseJobHandle job, IntPtr process);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint desiredAccess, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle,
        uint processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);

    private sealed class RuntimeLease(PythonVoiceModelRuntime owner) : IAsyncDisposable
    {
        private PythonVoiceModelRuntime? _owner = owner;
        public ValueTask DisposeAsync() => Interlocked.Exchange(ref _owner, null)?.ReleaseLeaseAsync() ?? ValueTask.CompletedTask;
    }
}

public sealed class VoiceWorkerException(string code, Exception? innerException = null)
    : Exception($"本地语音工作进程失败（{code}）。", innerException)
{
    public string Code { get; } = code;
}
