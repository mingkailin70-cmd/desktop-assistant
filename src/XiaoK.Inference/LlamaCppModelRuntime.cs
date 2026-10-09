using System.Diagnostics;
using System.ComponentModel;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Win32.SafeHandles;

namespace XiaoK.Inference;

/// <summary>
/// Starts one pinned llama.cpp server on demand. It accepts no model-provided arguments,
/// never enables server tools, and keeps read-only handles open on both pinned files.
/// </summary>
public sealed class LlamaCppModelRuntime : IManagedModelRuntime
{
    private const string ManifestName = "llama-runtime.json";
    private const string RuntimeFileName = "llama-server.exe";
    private const string RuntimeVersion = "b11259";
    private const string PrimaryModelId = "qwen3.5-4b-q4km";
    private const string PrimaryModelFileName = "Qwen3.5-4B-Q4_K_M.gguf";
    private const string PrimaryModelRevision = "f9f88ac3e234be915e23811a6d28ea287bdb927e";
    private const string MiMoEvaluationModelId = "mimo-v2.6-distill-qwen-9b-gguf-q8-0";
    private const string MiMoEvaluationModelFileName = "MiMo-V2.6-Distill-Qwen-9B-Q8_0.gguf";
    private const string Qwen9BEvaluationModelId = "qwen3.5-9b-q4km-eval";
    private const string Qwen9BEvaluationModelFileName = "Qwen3.5-9B-Q4_K_M.gguf";
    private const string Jev9BEvaluationModelId = "autotrust-jev-9b-q4km-eval";
    private const string Jev9BEvaluationModelFileName = "JEV-9B.Q4_K_M.gguf";
    private const string Gemma4E4BEvaluationModelId = "gemma-4-e4b-it-qat-q4-0-eval";
    private const string Gemma4E4BEvaluationModelFileName = "gemma-4-E4B_q4_0-it.gguf";
    private const string OrnithEvaluationModelId = "ornith-1.5-9b-q4km-eval";
    private const string OrnithEvaluationModelFileName = "Ornith-1.5-9B-Q4_K_M.gguf";
    private static readonly TimeSpan IdleUnloadDelay = TimeSpan.FromMinutes(4);
    private static readonly TimeSpan StartupTimeout = TimeSpan.FromMinutes(3);
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly string _modelRoot;
    private readonly string _runtimePath;
    private readonly string _modelPath;
    private readonly string _runtimeSha256;
    private readonly string _modelSha256;
    private readonly int _contextTokens;
    private readonly int _gpuLayers;
    private readonly long _expectedGpuMemoryMiB;
    private readonly Uri _endpoint;
    private readonly IGpuMemoryProbe _gpuMemoryProbe;
    private readonly HttpClient _healthClient;
    private Process? _process;
    private SafeJobHandle? _job;
    private FileStream? _runtimePin;
    private FileStream? _modelPin;
    private CancellationTokenSource? _idleCancellation;
    private Task? _idleStopTask;
    private TaskCompletionSource _leasesDrained = CompletedSource();
    private int _leases;
    private bool _disposeRequested;
    private bool _disposed;
    private string? _lastError;

    private LlamaCppModelRuntime(string modelRoot, string runtimePath, string modelPath,
        string runtimeSha256, string modelSha256, int contextTokens, int gpuLayers,
        long expectedGpuMemoryMiB, Uri endpoint, IGpuMemoryProbe gpuMemoryProbe)
    {
        _modelRoot = modelRoot;
        _runtimePath = runtimePath;
        _modelPath = modelPath;
        _runtimeSha256 = runtimeSha256;
        _modelSha256 = modelSha256;
        _contextTokens = contextTokens;
        _gpuLayers = gpuLayers;
        _expectedGpuMemoryMiB = expectedGpuMemoryMiB;
        _endpoint = endpoint;
        _gpuMemoryProbe = gpuMemoryProbe;
        _healthClient = new HttpClient(new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseProxy = false,
            ConnectTimeout = TimeSpan.FromSeconds(2)
        }) { Timeout = TimeSpan.FromSeconds(3) };
    }

    public string Status => _lastError is null
        ? "托管 llama.cpp：首次推理时按需启动；空闲 4 分钟后卸载。"
        : "托管 llama.cpp 最近一次卸载未能确认；下次请求会先检查运行进程。";

    /// <summary>Returns null only when no manifest is present. An invalid present manifest must not silently fall back.</summary>
    public static LlamaCppModelRuntime? TryLoad(string modelRoot, string endpoint, IGpuMemoryProbe? gpuMemoryProbe = null,
        int? contextTokensOverride = null)
    {
        var configuredRoot = Path.GetFullPath(modelRoot);
        var directManifestPath = Path.Combine(configuredRoot, ManifestName);
        var resolvedRoot = configuredRoot;
        if (!File.Exists(directManifestPath) && !Directory.Exists(directManifestPath))
        {
            var installedPrimaryRoot = Path.Combine(configuredRoot, "llm", "qwen3.5-4b", PrimaryModelRevision);
            var installedManifestPath = Path.Combine(installedPrimaryRoot, ManifestName);
            if (File.Exists(installedManifestPath) || Directory.Exists(installedManifestPath))
                resolvedRoot = installedPrimaryRoot;
        }

        return TryLoadCore(resolvedRoot, Path.Combine(resolvedRoot, "Runtime"), endpoint,
            PrimaryModelId, PrimaryModelFileName, gpuMemoryProbe, contextTokensOverride, manifestJsonOverride: null);
    }

    /// <summary>
    /// Loads the primary model for an offline evaluation using a transient manifest,
    /// leaving the production model manifest untouched.
    /// </summary>
    public static LlamaCppModelRuntime? TryLoadPrimaryForEvaluation(string modelRoot, string runtimeDirectory,
        string endpoint, string evaluationManifestJson, IGpuMemoryProbe? gpuMemoryProbe = null,
        int? contextTokensOverride = null)
    {
        if (!Path.IsPathFullyQualified(runtimeDirectory) || runtimeDirectory.StartsWith("\\\\", StringComparison.Ordinal))
            throw new InvalidDataException("评测运行时目录必须是本机绝对路径。");
        if (string.IsNullOrWhiteSpace(evaluationManifestJson))
            throw new InvalidDataException("离线评测运行清单不能为空。");
        return TryLoadCore(modelRoot, runtimeDirectory, endpoint, PrimaryModelId, PrimaryModelFileName,
            gpuMemoryProbe, contextTokensOverride, evaluationManifestJson);
    }

    /// <summary>
    /// Loads an explicitly supported non-primary GGUF candidate for offline coding evaluation.
    /// Production Host startup must continue to use <see cref="TryLoad"/>.
    /// </summary>
    public static LlamaCppModelRuntime? TryLoadEvaluationCandidate(string modelRoot, string runtimeDirectory,
        string endpoint, string modelId, IGpuMemoryProbe? gpuMemoryProbe = null, int? contextTokensOverride = null,
        string? evaluationManifestJson = null)
    {
        if (!Path.IsPathFullyQualified(runtimeDirectory) || runtimeDirectory.StartsWith("\\\\", StringComparison.Ordinal))
            throw new InvalidDataException("评测运行时目录必须是本机绝对路径。");
        var candidate = modelId switch
        {
            MiMoEvaluationModelId => (MiMoEvaluationModelId, MiMoEvaluationModelFileName),
            Qwen9BEvaluationModelId => (Qwen9BEvaluationModelId, Qwen9BEvaluationModelFileName),
            Jev9BEvaluationModelId => (Jev9BEvaluationModelId, Jev9BEvaluationModelFileName),
            Gemma4E4BEvaluationModelId => (Gemma4E4BEvaluationModelId, Gemma4E4BEvaluationModelFileName),
            OrnithEvaluationModelId => (OrnithEvaluationModelId, OrnithEvaluationModelFileName),
            _ => throw new InvalidDataException("评测入口仅允许加载锁定的 MiMo Q8_0、Qwen3.5-9B Q4、JEV-9B Q4、Gemma 4 E4B QAT Q4_0 或 Ornith-1.5-9B Q4_K_M 候选模型。")
        };
        return TryLoadCore(modelRoot, runtimeDirectory, endpoint, candidate.Item1,
            candidate.Item2, gpuMemoryProbe, contextTokensOverride, evaluationManifestJson);
    }

    private static LlamaCppModelRuntime? TryLoadCore(string modelRoot, string runtimeDirectory, string endpoint,
        string expectedModelId, string expectedModelFileName, IGpuMemoryProbe? gpuMemoryProbe,
        int? contextTokensOverride, string? manifestJsonOverride)
    {
        if (!Path.IsPathFullyQualified(modelRoot) || modelRoot.StartsWith("\\\\", StringComparison.Ordinal))
            throw new InvalidDataException("模型目录必须是本机绝对路径。");
        var root = Path.GetFullPath(modelRoot);
        var pathRoot = Path.GetPathRoot(root) ?? throw new InvalidDataException("模型目录路径无效。");
        if (string.Equals(root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                pathRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("模型目录不能是磁盘根目录。");
        root = Path.TrimEndingDirectorySeparator(root);
        if (!TryEnsureNoReparseComponents(root)) return null;
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
        };
        RuntimeManifest manifest;
        if (manifestJsonOverride is null)
        {
            var manifestPath = Path.Combine(root, ManifestName);
            if (!TryEnsureNoReparseComponents(manifestPath)) return null;
            using var manifestStream = new FileStream(manifestPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (manifestStream.Length is <= 0 or > 16 * 1024)
                throw new InvalidDataException("llama.cpp 清单文件大小无效。");
            manifest = JsonSerializer.Deserialize<RuntimeManifest>(manifestStream, options)
                ?? throw new InvalidDataException("llama.cpp 清单为空。");
        }
        else
        {
            if (Encoding.UTF8.GetByteCount(manifestJsonOverride) is <= 0 or > 16 * 1024)
                throw new InvalidDataException("离线评测运行清单大小无效。");
            manifest = JsonSerializer.Deserialize<RuntimeManifest>(manifestJsonOverride, options)
                ?? throw new InvalidDataException("离线评测运行清单为空。");
        }
        if (manifest.SchemaVersion != 1 || manifest.RuntimeVersion != RuntimeVersion
            || !string.Equals(manifest.ModelId, expectedModelId, StringComparison.Ordinal))
            throw new InvalidDataException("llama.cpp 清单版本或模型 ID 不受支持。");
        if (!IsSha256(manifest.RuntimeSha256) || !IsSha256(manifest.ModelSha256))
            throw new InvalidDataException("llama.cpp 清单必须包含两个 64 位十六进制 SHA-256。");
        if (manifest.ContextTokens is < 1024 or > 8192 || manifest.GpuLayers is < 0 or > 99)
            throw new InvalidDataException("llama.cpp 上下文长度或 GPU 层数超出首版范围。");
        var contextTokens = contextTokensOverride ?? manifest.ContextTokens;
        if (contextTokens is < 1024 or > 8192)
            throw new InvalidDataException("llama.cpp 请求上下文长度超出首版范围。");
        if (manifest.GpuLayers == 0 ? manifest.ExpectedGpuMemoryMiB != 0
                : manifest.ExpectedGpuMemoryMiB is <= 0 or > 16384)
            throw new InvalidDataException("GPU 层数与已评估的显存预算不匹配。CPU 模式预算必须为 0；GPU 模式必须提供 1–16,384 MiB 预算。");

        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri)
            || uri.Scheme != Uri.UriSchemeHttp
            || !string.Equals(uri.Host, IPAddress.Loopback.ToString(), StringComparison.OrdinalIgnoreCase)
            || uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0
            || uri.AbsolutePath != "/" || uri.Port is < 1 or > 65535)
            throw new InvalidDataException("托管 llama.cpp 只允许使用根路径上的 127.0.0.1 HTTP 地址。");

        var runtimeRoot = Path.GetFullPath(runtimeDirectory);
        var runtimePath = Path.Combine(runtimeRoot, RuntimeFileName);
        var modelPath = Path.Combine(root, expectedModelFileName);
        return new LlamaCppModelRuntime(root, runtimePath, modelPath,
            manifest.RuntimeSha256, manifest.ModelSha256, contextTokens, manifest.GpuLayers,
            manifest.ExpectedGpuMemoryMiB, uri, gpuMemoryProbe ?? new NvidiaSmiGpuMemoryProbe());
    }

    public int ContextTokens => _contextTokens;

    public async ValueTask<IAsyncDisposable> AcquireAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_disposeRequested || _disposed) throw new ObjectDisposedException(nameof(LlamaCppModelRuntime));
            CancelIdleStop();
            try
            {
                if (_process is null || _process.HasExited)
                {
                    await StopCoreAsync().ConfigureAwait(false);
                    await StartCoreAsync(cancellationToken).ConfigureAwait(false);
                }

                cancellationToken.ThrowIfCancellationRequested();
                if (_leases == 0) _leasesDrained = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                _leases++;
                _lastError = null;
                return new RuntimeLease(this);
            }
            catch
            {
                if (_leases == 0) await StopCoreAsync().ConfigureAwait(false);
                throw;
            }
        }
        finally { _gate.Release(); }
    }

    public async ValueTask UnloadIfIdleAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_disposeRequested || _disposed) throw new ObjectDisposedException(nameof(LlamaCppModelRuntime));
            if (_leases != 0)
                throw new InvalidOperationException("主模型仍有活动租约；拒绝与其他大型模型并存。");

            CancelIdleStop();
            await StopCoreAsync().ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    public async ValueTask DisposeAsync()
    {
        Task? idleStopTask;
        Task noLeases;
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed) return;
            _disposeRequested = true;
            CancelIdleStop();
            idleStopTask = _idleStopTask;
            noLeases = _leasesDrained.Task;
        }
        finally { _gate.Release(); }

        if (idleStopTask is not null)
        {
            try { await idleStopTask.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
        }
        await noLeases.ConfigureAwait(false);

        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            await StopCoreAsync().ConfigureAwait(false);
            _disposed = true;
            _healthClient.Dispose();
        }
        finally { _gate.Release(); }
    }

    private async Task StartCoreAsync(CancellationToken cancellationToken)
    {
        if (_gpuLayers > 0)
        {
            var admission = GpuMemoryAdmissionPolicy.Evaluate(
                await _gpuMemoryProbe.ReadAsync(cancellationToken).ConfigureAwait(false), _expectedGpuMemoryMiB);
            if (!admission.Allowed) throw new LowGpuMemoryException(admission.Reason);
        }

        EnsureNoReparseComponents(_modelRoot);
        EnsureNoReparseComponents(_runtimePath);
        EnsureNoReparseComponents(_modelPath);
        EnsurePortAvailable();

        _runtimePin = OpenPinnedFile(_runtimePath);
        _modelPin = OpenPinnedFile(_modelPath);
        try
        {
            await VerifyHashAsync(_runtimePin, _runtimeSha256, "llama.cpp 运行时", cancellationToken).ConfigureAwait(false);
            await VerifyHashAsync(_modelPin, _modelSha256, "本地模型", cancellationToken).ConfigureAwait(false);

            var startInfo = new ProcessStartInfo
            {
                FileName = _runtimePath,
                WorkingDirectory = Path.GetDirectoryName(_runtimePath)!,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            startInfo.ArgumentList.Add("--model");
            startInfo.ArgumentList.Add(_modelPath);
            startInfo.ArgumentList.Add("--host");
            startInfo.ArgumentList.Add(IPAddress.Loopback.ToString());
            startInfo.ArgumentList.Add("--port");
            startInfo.ArgumentList.Add(_endpoint.Port.ToString(System.Globalization.CultureInfo.InvariantCulture));
            startInfo.ArgumentList.Add("--ctx-size");
            startInfo.ArgumentList.Add(_contextTokens.ToString(System.Globalization.CultureInfo.InvariantCulture));
            startInfo.ArgumentList.Add("--n-gpu-layers");
            startInfo.ArgumentList.Add(_gpuLayers.ToString(System.Globalization.CultureInfo.InvariantCulture));
            ConfigureEnvironment(startInfo, Path.GetDirectoryName(_runtimePath)!);

            _job = CreateKillOnCloseJob();
            _process = Process.Start(startInfo) ?? throw new HttpRequestException("无法启动本地模型运行时。");
            var processHandle = OpenProcess(0x0001 | 0x0100, false, (uint)_process.Id);
            if (processHandle == IntPtr.Zero)
                throw new Win32Exception(Marshal.GetLastWin32Error(), "无法取得模型进程回收权限。");
            try
            {
                if (!AssignProcessToJobObject(_job, processHandle))
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "无法将本地模型进程置于 Host 崩溃自动回收范围。");
            }
            finally { _ = CloseHandle(processHandle); }
            _ = DrainAndDiscardAsync(_process.StandardOutput);
            _ = DrainAndDiscardAsync(_process.StandardError);
            await WaitUntilReadyAsync(cancellationToken).ConfigureAwait(false);
            if (_gpuLayers > 0
                && !GpuMemoryAdmissionPolicy.HasMinimumReserve(
                    await _gpuMemoryProbe.ReadAsync(cancellationToken).ConfigureAwait(false)))
                throw new LowGpuMemoryException("模型加载后独显可用显存低于 1 GiB；已停止本地模型进程。");
        }
        catch (Win32Exception ex)
        {
            await StopCoreAsync().ConfigureAwait(false);
            throw new HttpRequestException("无法安全启动本地模型进程。", ex);
        }
        catch
        {
            await StopCoreAsync().ConfigureAwait(false);
            throw;
        }
    }

    private async Task WaitUntilReadyAsync(CancellationToken cancellationToken)
    {
        using var timeout = new CancellationTokenSource(StartupTimeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        var healthUri = new Uri(_endpoint, "health");
        while (true)
        {
            linked.Token.ThrowIfCancellationRequested();
            if (_process is null || _process.HasExited)
                throw new HttpRequestException("llama.cpp 在模型就绪前退出。");
            try
            {
                using var response = await _healthClient.GetAsync(healthUri, HttpCompletionOption.ResponseHeadersRead, linked.Token).ConfigureAwait(false);
                if (response.StatusCode == HttpStatusCode.OK) return;
            }
            catch (HttpRequestException) { }
            catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested && !timeout.IsCancellationRequested) { }

            if (timeout.IsCancellationRequested)
                throw new HttpRequestException("llama.cpp 在 3 分钟内未报告模型就绪。");
            await Task.Delay(TimeSpan.FromMilliseconds(300), linked.Token).ConfigureAwait(false);
        }
    }

    private async ValueTask ReleaseAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_leases <= 0) return;
            _leases--;
            if (_leases == 0)
            {
                _leasesDrained.TrySetResult();
                if (!_disposeRequested) ScheduleIdleStop();
            }
        }
        finally { _gate.Release(); }
    }

    private void ScheduleIdleStop()
    {
        CancelIdleStop();
        var cancellation = new CancellationTokenSource();
        _idleCancellation = cancellation;
        _idleStopTask = StopAfterIdleAsync(cancellation);
    }

    private async Task StopAfterIdleAsync(CancellationTokenSource cancellation)
    {
        try
        {
            await Task.Delay(IdleUnloadDelay, cancellation.Token).ConfigureAwait(false);
            await _gate.WaitAsync(cancellation.Token).ConfigureAwait(false);
            try
            {
                if (!_disposeRequested && _leases == 0 && ReferenceEquals(_idleCancellation, cancellation))
                    await StopCoreAsync().ConfigureAwait(false);
            }
            finally { _gate.Release(); }
        }
        catch (OperationCanceledException) { }
        catch (Exception)
        {
            _lastError = "idle-stop-failed";
        }
        finally
        {
            if (ReferenceEquals(_idleCancellation, cancellation)) _idleCancellation = null;
            cancellation.Dispose();
        }
    }

    private void CancelIdleStop()
    {
        var cancellation = _idleCancellation;
        _idleCancellation = null;
        if (cancellation is not null)
        {
            try { cancellation.Cancel(); }
            catch (ObjectDisposedException) { }
        }
    }

    private async Task StopCoreAsync()
    {
        var process = _process;
        if (process is not null)
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                    await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
                }
            }
            catch (InvalidOperationException) { }
            catch (System.ComponentModel.Win32Exception) { }
            if (!process.HasExited) throw new IOException("无法确认 llama.cpp 进程已停止；保留模型文件锁以防止权重被替换。");
            process.Dispose();
            _process = null;
        }

        _job?.Dispose();
        _job = null;

        _modelPin?.Dispose();
        _modelPin = null;
        _runtimePin?.Dispose();
        _runtimePin = null;
    }

    private static FileStream OpenPinnedFile(string path, FileShare share = FileShare.Read)
    {
        try
        {
            return new FileStream(path, FileMode.Open, FileAccess.Read, share,
                bufferSize: 1024 * 1024, FileOptions.SequentialScan);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new ModelRuntimeUnavailableException("托管模型文件缺失或无法只读锁定；未启动进程。");
        }
    }

    private static async Task VerifyHashAsync(FileStream stream, string expectedHash, string label, CancellationToken cancellationToken)
    {
        stream.Position = 0;
        var actual = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        stream.Position = 0;
        var expected = Convert.FromHexString(expectedHash);
        if (!CryptographicOperations.FixedTimeEquals(actual, expected))
            throw new ModelRuntimeUnavailableException($"{label} SHA-256 与清单不符；未启动进程。");
    }

    private static void EnsureNoReparseComponents(string path)
    {
        if (!TryEnsureNoReparseComponents(path))
            throw new ModelRuntimeUnavailableException("托管模型目录、运行时或权重文件缺失；未启动进程。");
    }

    private static bool TryEnsureNoReparseComponents(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var root = Path.GetPathRoot(fullPath) ?? throw new InvalidDataException("本机文件路径无效。");
        var current = root;
        foreach (var segment in fullPath[root.Length..].Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            FileAttributes attributes;
            try { attributes = File.GetAttributes(current); }
            catch (FileNotFoundException) { return false; }
            catch (DirectoryNotFoundException) { return false; }
            if ((attributes & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("托管模型目录、程序或权重包含重解析点；已拒绝启动。");
        }
        return true;
    }

    private void EnsurePortAvailable()
    {
        using var listener = new TcpListener(IPAddress.Loopback, _endpoint.Port);
        try { listener.Start(backlog: 1); }
        catch (SocketException)
        {
            throw new ModelRuntimeUnavailableException("托管推理端口已被占用；为避免连接到其他进程，未启动模型。");
        }
    }

    private static void ConfigureEnvironment(ProcessStartInfo startInfo, string runtimeDirectory)
    {
        startInfo.Environment.Clear();
        CopyIfPresent(startInfo.Environment, "SystemRoot");
        CopyIfPresent(startInfo.Environment, "WINDIR");
        CopyIfPresent(startInfo.Environment, "USERPROFILE");
        CopyIfPresent(startInfo.Environment, "LOCALAPPDATA");
        CopyIfPresent(startInfo.Environment, "APPDATA");
        var temp = Path.GetTempPath();
        startInfo.Environment["TEMP"] = temp;
        startInfo.Environment["TMP"] = temp;
        startInfo.Environment["PATH"] = runtimeDirectory + Path.PathSeparator + Environment.SystemDirectory;
        startInfo.Environment["CUDA_VISIBLE_DEVICES"] = "0";
        // In particular, do not inherit LLAMA_ARG_MODEL_URL, LLAMA_ARG_TOOLS, proxies, or user command overrides.
    }

    private static void CopyIfPresent(IDictionary<string, string?> target, string name)
    {
        var value = Environment.GetEnvironmentVariable(name);
        if (!string.IsNullOrWhiteSpace(value)) target[name] = value;
    }

    private static async Task DrainAndDiscardAsync(StreamReader reader)
    {
        try { while (await reader.ReadLineAsync().ConfigureAwait(false) is not null) { } }
        catch (IOException) { }
        catch (ObjectDisposedException) { }
    }

    private static bool IsSha256(string value) => value.Length == 64 && value.All(Uri.IsHexDigit);
    private static TaskCompletionSource CompletedSource()
    {
        var result = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        result.SetResult();
        return result;
    }

    private sealed record RuntimeManifest(int SchemaVersion, string RuntimeVersion, string RuntimeSha256,
        string ModelId, string ModelSha256, int ContextTokens, int GpuLayers, long ExpectedGpuMemoryMiB);

    private static SafeJobHandle CreateKillOnCloseJob()
    {
        var job = CreateJobObjectW(IntPtr.Zero, null);
        if (job.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error(), "无法创建模型进程回收作业。");
        var limits = new JobObjectExtendedLimitInformation
        {
            BasicLimitInformation = new JobObjectBasicLimitInformation { LimitFlags = 0x00002000 }
        };
        if (!SetInformationJobObject(job, 9, ref limits, (uint)Marshal.SizeOf<JobObjectExtendedLimitInformation>()))
        {
            var error = Marshal.GetLastWin32Error();
            job.Dispose();
            throw new Win32Exception(error, "无法配置 Host 退出时回收模型进程。");
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

    private sealed class SafeJobHandle() : SafeHandleZeroOrMinusOneIsInvalid(true)
    {
        protected override bool ReleaseHandle() => CloseHandle(handle);
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CreateJobObjectW")]
    private static extern SafeJobHandle CreateJobObjectW(IntPtr jobAttributes, string? name);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetInformationJobObject(SafeJobHandle job, int informationClass,
        ref JobObjectExtendedLimitInformation information, uint informationLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AssignProcessToJobObject(SafeJobHandle job, IntPtr process);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint desiredAccess, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, uint processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);

    private sealed class RuntimeLease(LlamaCppModelRuntime owner) : IAsyncDisposable
    {
        private LlamaCppModelRuntime? _owner = owner;
        public ValueTask DisposeAsync() => Interlocked.Exchange(ref _owner, null)?.ReleaseAsync() ?? ValueTask.CompletedTask;
    }
}
