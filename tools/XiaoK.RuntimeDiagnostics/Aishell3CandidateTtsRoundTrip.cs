using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SherpaOnnx;
using XiaoK.Inference;
using XiaoK.Voice;

internal static class Aishell3CandidateTtsRoundTrip
{
    private const string ModelId = "sherpa-vits-icefall-aishell3-2024-04-06";
    private const string Revision = "29953c80423f4c37918217808ecb308c1b9b7196";
    private const string SherpaVersion = "1.13.5";
    private const int CpuThreads = 2;
    private const int MinimumStartAvailableMemoryMiB = 8 * 1024;
    private const int MinimumRuntimeAvailableMemoryMiB = 4 * 1024;
    private const int SpeakerId = 0;
    private static readonly TimeSpan MaximumObserverStartTimeSkew = TimeSpan.FromSeconds(2);
    private static readonly string[] ExpectedAssetNames =
    [
        "date.fst",
        "lexicon.txt",
        "model.onnx",
        "new_heteronym.fst",
        "number.fst",
        "phone.fst",
        "rule.far",
        "speakers.txt",
        "tokens.txt"
    ];
    private static readonly string[] Phrases =
    [
        "你好，小K，请打开本地项目。",
        "我先检查文件内容，再告诉你结果。",
        "任务已经取消，我没有执行后续操作。",
        "下午三点请提醒我保存文档。",
        "发送前请再次核对联系人、正文和附件。",
        "操作结果无法确认时，不要自动重试。"
    ];

    public static int RunContractChecks(string workspaceArgument)
    {
        try
        {
            var workspace = FindWorkspaceRoot(workspaceArgument);
            Require(Directory.Exists(workspace) && !HasReparsePointInPath(workspace),
                "The workspace must be an existing directory without reparse points.");
            var profile = ReadProfile(workspace);
            Require(profile.Revision == Revision && profile.RequiredForP0 is false,
                "AISHELL-3 must remain a pinned, isolated-only candidate.");
            Require(profile.Files.Count == ExpectedAssetNames.Length && profile.Files.Select(file => file.Name)
                    .ToHashSet(StringComparer.Ordinal).SetEquals(ExpectedAssetNames),
                "The nine-file Sherpa runtime bundle must remain locked.");
            Require(Phrases.Length == 6 && Phrases.All(phrase => !string.IsNullOrWhiteSpace(phrase)),
                "The fixed round-trip corpus must contain six non-empty synthetic phrases.");
            Require(Normalize("你好， 小K！") == Normalize(" 你好小K。"),
                "Transcript normalization must ignore punctuation and whitespace only.");
            Require(Normalize("A+B") != Normalize("AB"),
                "Transcript normalization must preserve symbols that can change meaning.");

            var wave = BuildWave([0f, 0.5f, -0.5f], 22050);
            Require(wave.Length == 50
                    && Encoding.ASCII.GetString(wave, 0, 4) == "RIFF"
                    && Encoding.ASCII.GetString(wave, 8, 4) == "WAVE"
                    && BinaryPrimitives.ReadInt32LittleEndian(wave.AsSpan(40, 4)) == 6,
                "In-memory WAV packaging must emit a bounded PCM16 mono RIFF payload.");
            CryptographicOperations.ZeroMemory(wave);
            var clipped = MeasureAudio([1.1f], 22050);
            Require(clipped.ClippedSampleCount == 1 && clipped.Peak > 1f,
                "Audio diagnostics must expose samples that would be clipped during PCM conversion.");

            var started = DateTimeOffset.Parse("2026-10-14T09:12:04.2704444Z", CultureInfo.InvariantCulture);
            using var running = JsonDocument.Parse("""
                {"state":"running","monitorProcessId":1234,"monitorStartedUtc":"2026-10-14T09:12:04.2704444Z"}
                """);
            Require(IsLiveObserver(running.RootElement, _ => started),
                "A matching live observer identity must refuse candidate model loading.");
            Require(!IsLiveObserver(running.RootElement, _ => started.AddSeconds(5)),
                "A reused PID with a different process start time must not be treated as the observer.");
            using var completed = JsonDocument.Parse("""
                {"state":"completed","monitorProcessId":1234,"monitorStartedUtc":"2026-10-14T09:12:04.2704444Z"}
                """);
            Require(!IsLiveObserver(completed.RootElement, _ => started),
                "A completed observation must not block later evaluation.");
            using var malformedControl = JsonDocument.Parse("""
                {"state":42,"monitorProcessId":1234,"monitorStartedUtc":"2026-10-14T09:12:04.2704444Z"}
                """);
            var malformedControlRejected = false;
            try { _ = IsLiveObserver(malformedControl.RootElement, _ => started); }
            catch (DiagnosticRefusalException) { malformedControlRejected = true; }
            Require(malformedControlRejected,
                "Malformed observer state must fail closed before candidate loading.");

            Console.WriteLine("AISHELL-3 isolated CPU evaluator contract checks passed; no model was loaded.");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"AISHELL-3 evaluator contract check failed: {exception.GetType().Name}");
            return 1;
        }
    }

    public static async Task<int> RunAsync(string workspaceArgument, string reportArgument)
    {
        if (!OperatingSystem.IsWindows() || !Environment.Is64BitProcess)
        {
            Console.Error.WriteLine("AISHELL-3 evaluation requires the locked Windows x64 runtime.");
            return 2;
        }

        var workspace = Path.TrimEndingDirectorySeparator(Path.GetFullPath(workspaceArgument));
        if (!Directory.Exists(workspace) || HasReparsePointInPath(workspace))
        {
            Console.Error.WriteLine("The workspace must be an existing directory without reparse points.");
            return 2;
        }
        var modelRoot = Path.GetFullPath(Path.Combine(workspace, "models"));
        var environmentRoot = Path.GetFullPath(Path.Combine(workspace, ".tools", "venvs"));
        var reportPath = Path.GetFullPath(reportArgument);
        var reportDirectory = Path.GetDirectoryName(reportPath);
        if (IsWithinRoot(reportPath, workspace) || IsWithinRoot(reportPath, modelRoot)
            || IsWithinRoot(reportPath, environmentRoot) || reportDirectory is null
            || !Directory.Exists(reportDirectory) || HasReparsePointInPath(reportDirectory))
        {
            Console.Error.WriteLine("The metrics report must be a new file in an existing, non-reparse directory outside the repository, models, and runtime environments.");
            return 2;
        }
        if (File.Exists(reportPath) || Directory.Exists(reportPath))
        {
            Console.Error.WriteLine("The report already exists; this diagnostic never overwrites prior results.");
            return 2;
        }

        var startedAtUtc = DateTimeOffset.UtcNow;
        var ttsLatenciesMs = new List<double>(Phrases.Length);
        var asrLatenciesMs = new List<double>(Phrases.Length);
        var audioSummaries = new List<AudioSummary>(Phrases.Length);
        var transcriptMatches = new List<bool>(Phrases.Length);
        var waveSamples = new List<byte[]>(Phrases.Length);
        var initialAvailableMemoryMiB = 0;
        var minimumAvailableMemoryMiB = 0;
        var peakWorkingSetMiB = 0L;
        var asrModelBrokerUsed = false;
        var asrWorkersAfterDispose = -1;
        var memorySamplingFailed = false;
        string? errorCode = null;
        string result = "FAIL";
        VoiceInferenceService? voice = null;
        AvailableMemorySampler? memorySampler = null;
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(12));
        using var diagnosticProcess = Process.GetCurrentProcess();

        try
        {
            RefuseWhileObservationIsLive(workspace);
            var profile = ReadProfile(workspace);
            VerifyLockedRuntime(workspace, profile);
            VerifyCandidateFiles(modelRoot, profile);
            RefuseForCompetingModelProcesses();
            if (!Directory.Exists(environmentRoot) || HasReparsePointInPath(environmentRoot))
                throw new DiagnosticRefusalException("VOICE_ENVIRONMENT_UNAVAILABLE");

            initialAvailableMemoryMiB = GetAvailableMemoryMiB();
            if (initialAvailableMemoryMiB < MinimumStartAvailableMemoryMiB)
                throw new DiagnosticRefusalException("RAM_ADMISSION_REJECTED");

            diagnosticProcess.PriorityClass = ProcessPriorityClass.BelowNormal;
            memorySampler = new AvailableMemorySampler(initialAvailableMemoryMiB);
            var modelDirectory = Path.GetFullPath(Path.Combine(modelRoot, profile.LocalDirectory));
            using (var tts = new OfflineTts(CreateTtsConfig(modelDirectory)))
            {
                if (tts.NumSpeakers <= SpeakerId)
                    throw new DiagnosticRefusalException("SPEAKER_ID_UNAVAILABLE");

                foreach (var phrase in Phrases)
                {
                    deadline.Token.ThrowIfCancellationRequested();
                    var clock = Stopwatch.StartNew();
                    var generated = tts.Generate(phrase, speed: 1.0f, speakerId: SpeakerId);
                    clock.Stop();
                    try
                    {
                        var samples = generated.Samples;
                        try
                        {
                            var summary = MeasureAudio(samples, generated.SampleRate);
                            var wav = BuildWave(samples, generated.SampleRate);
                            audioSummaries.Add(summary);
                            waveSamples.Add(wav);
                            ttsLatenciesMs.Add(clock.Elapsed.TotalMilliseconds);
                        }
                        finally
                        {
                            CryptographicOperations.ZeroMemory(MemoryMarshal.AsBytes(samples.AsSpan()));
                        }
                    }
                    finally
                    {
                        generated.Dispose();
                    }

                    EnsureMemoryFloor(memorySampler);
                    peakWorkingSetMiB = Math.Max(peakWorkingSetMiB,
                        diagnosticProcess.PeakWorkingSet64 / (1024 * 1024));
                }
            }

            var asrPython = Path.Combine(environmentRoot, "asr", "Scripts", "python.exe");
            if (GetWorkerProcessIds(asrPython).Length != 0)
                throw new DiagnosticRefusalException("ASR_WORKER_ALREADY_RUNNING");
            var broker = new ModelBroker();
            voice = VoiceInferenceService.TryCreateForWorkspace(workspace, environmentRoot,
                Path.Combine(workspace, "src", "XiaoK.Voice", "voice_worker.py"), broker, out _);
            if (voice is null)
                throw new DiagnosticRefusalException("LOCKED_ASR_SERVICE_UNAVAILABLE");

            for (var index = 0; index < waveSamples.Count; index++)
            {
                deadline.Token.ThrowIfCancellationRequested();
                EnsureMemoryFloor(memorySampler);
                var clock = Stopwatch.StartNew();
                var recognition = await voice.TranscribeWavAsync(waveSamples[index], forceChinese: true,
                    deadline.Token).ConfigureAwait(false);
                clock.Stop();
                asrLatenciesMs.Add(clock.Elapsed.TotalMilliseconds);
                transcriptMatches.Add(Normalize(recognition.Text)
                    .Equals(Normalize(Phrases[index]), StringComparison.Ordinal));
                EnsureMemoryFloor(memorySampler);
                peakWorkingSetMiB = Math.Max(peakWorkingSetMiB,
                    diagnosticProcess.PeakWorkingSet64 / (1024 * 1024));
            }

            asrModelBrokerUsed = broker.LastUseUtc is not null;
            result = transcriptMatches.Count == Phrases.Length && transcriptMatches.All(matched => matched)
                ? "PASS" : "TRANSCRIPT_MISMATCH";
        }
        catch (DiagnosticRefusalException exception)
        {
            errorCode = exception.Code;
            result = "REFUSED";
        }
        catch (OperationCanceledException)
        {
            errorCode = "DIAGNOSTIC_TIMEOUT";
        }
        catch (Exception exception)
        {
            errorCode = exception.GetType().Name;
        }
        finally
        {
            foreach (var wav in waveSamples)
                CryptographicOperations.ZeroMemory(wav);
            if (voice is not null)
            {
                try { await voice.DisposeAsync().ConfigureAwait(false); }
                catch (Exception exception) { errorCode ??= exception.GetType().Name; result = "FAIL"; }
            }
            if (environmentRoot.Length != 0)
            {
                try { asrWorkersAfterDispose = GetWorkerProcessIds(Path.Combine(environmentRoot, "asr", "Scripts", "python.exe")).Length; }
                catch { asrWorkersAfterDispose = -1; }
            }
            if (memorySampler is not null)
            {
                try { await memorySampler.DisposeAsync().ConfigureAwait(false); }
                catch { memorySamplingFailed = true; }
                minimumAvailableMemoryMiB = memorySampler.MinimumAvailableMiB;
                memorySamplingFailed |= memorySampler.SamplingFailed;
            }
        }

        var completedAtUtc = DateTimeOffset.UtcNow;
        var allWavsZeroed = waveSamples.Count == Phrases.Length
            && waveSamples.All(wav => wav.All(value => value == 0));
        var passed = result == "PASS" && errorCode is null && asrModelBrokerUsed
            && ttsLatenciesMs.Count == Phrases.Length && asrLatenciesMs.Count == Phrases.Length
            && audioSummaries.Count == Phrases.Length && allWavsZeroed && asrWorkersAfterDispose == 0
            && minimumAvailableMemoryMiB >= MinimumRuntimeAvailableMemoryMiB && !memorySamplingFailed;
        if (memorySamplingFailed)
        {
            errorCode ??= "MEMORY_SAMPLING_FAILED";
            result = "FAIL";
        }
        else if (!passed && result == "PASS")
        {
            result = "FAIL";
        }

        var report = new
        {
            schemaVersion = 1,
            startedAtUtc,
            completedAtUtc,
            result,
            errorCode,
            candidate = new { id = ModelId, revision = Revision, role = "isolated-cpu-evaluation-only" },
            runtime = new { package = "org.k2fsa.sherpa.onnx", version = SherpaVersion, target = "net10.0/win-x64", provider = "cpu", threads = CpuThreads },
            path = "direct Sherpa CPU candidate generation in this diagnostic process; locked local Qwen ASR through VoiceInferenceService and ModelBroker; no Host integration",
            sampleCount = Phrases.Length,
            speakerId = SpeakerId,
            ttsLatenciesMs,
            asrLatenciesMs,
            ttsP50Ms = PercentileNearestRank(ttsLatenciesMs, 0.50),
            ttsP95Ms = PercentileNearestRank(ttsLatenciesMs, 0.95),
            asrP50Ms = PercentileNearestRank(asrLatenciesMs, 0.50),
            asrP95Ms = PercentileNearestRank(asrLatenciesMs, 0.95),
            audioSummaries,
            normalizedTranscriptMatchCount = transcriptMatches.Count(matched => matched),
            transcriptMatchBySample = transcriptMatches.Select((matched, index) => new { sample = $"S{index + 1:00}", matched }),
            ttsGenerateIsSynchronous = true,
            ttsGenerateCanBeInterrupted = false,
            syntheticTextAndAudioSaved = false,
            transcriptSaved = false,
            wavsZeroedAfterUse = allWavsZeroed,
            asrModelBrokerUsed,
            asrWorkersAfterDispose,
            initialAvailableMemoryMiB,
            minimumAvailableMemoryMiB,
            memorySamplingFailed,
            requiredStartAvailableMemoryMiB = MinimumStartAvailableMemoryMiB,
            requiredRuntimeAvailableMemoryMiB = MinimumRuntimeAvailableMemoryMiB,
            peakDiagnosticProcessWorkingSetMiB = peakWorkingSetMiB,
            cpuOnly = true,
            microphoneUsed = false,
            audioPlaybackUsed = false,
            hostTouched = false,
            productionAdmitted = false,
            passed
        };

        try
        {
            await WriteNewReportAsync(reportPath, report).ConfigureAwait(false);
            Console.WriteLine($"AISHELL-3 isolated CPU round-trip: {result}; metrics saved outside the repository.");
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"AISHELL-3 diagnostic report could not be saved: {exception.GetType().Name}");
            return 1;
        }
        return passed ? 0 : result == "REFUSED" ? 3 : 1;
    }

    private static CandidateProfile ReadProfile(string workspace)
    {
        var lockPath = Path.Combine(workspace, "model-lock", "models.lock.json");
        if (!File.Exists(lockPath) || HasReparsePointInPath(lockPath))
            throw new DiagnosticRefusalException("MODEL_LOCK_UNAVAILABLE");
        using var document = JsonDocument.Parse(File.ReadAllBytes(lockPath), new JsonDocumentOptions { MaxDepth = 32 });
        var models = document.RootElement.GetProperty("models").EnumerateArray();
        JsonElement model = default;
        var found = false;
        foreach (var candidate in models)
        {
            if (candidate.TryGetProperty("id", out var id) && id.GetString() == ModelId)
            {
                if (found) throw new DiagnosticRefusalException("MODEL_LOCK_DUPLICATE_ID");
                model = candidate;
                found = true;
            }
        }
        if (!found) throw new DiagnosticRefusalException("MODEL_LOCK_ENTRY_MISSING");
        if (GetString(model, "revision") != Revision
            || GetString(model, "role") != "tts-isolated-cpu-evaluation-candidate"
            || GetString(model, "status") != "downloaded_and_verified"
            || GetString(model, "license") != "apache-2.0"
            || model.GetProperty("requiredForP0").GetBoolean()
            || GetString(model, "dataSource") != "AISHELL-3"
            || GetString(model, "dataSourceEvidenceStatus") != "exact_binary_match_official_aishell3_release"
            || GetString(model, "dataLicenseScopeStatus") != "apache2_sources_verified_provider_notice_not_specific_to_aishell3")
            throw new DiagnosticRefusalException("MODEL_LOCK_ADMISSION_MISMATCH");

        var localDirectory = GetString(model, "localDirectory");
        if (string.IsNullOrWhiteSpace(localDirectory) || Path.IsPathRooted(localDirectory))
            throw new DiagnosticRefusalException("MODEL_DIRECTORY_INVALID");
        var files = new List<LockedFile>();
        foreach (var file in model.GetProperty("upstreamArchive").GetProperty("verifiedContents").EnumerateArray())
        {
            var name = GetString(file, "name");
            var sha256 = GetString(file, "sha256");
            var size = file.GetProperty("sizeBytes").GetInt64();
            if (string.IsNullOrWhiteSpace(name) || Path.IsPathRooted(name)
                || name.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Any(part => part is ".." or ""))
                throw new DiagnosticRefusalException("MODEL_FILE_NAME_INVALID");
            if (sha256.Length != 64 || !sha256.All(Uri.IsHexDigit) || size <= 0)
                throw new DiagnosticRefusalException("MODEL_FILE_LOCK_INVALID");
            files.Add(new LockedFile(name, size, sha256.ToLowerInvariant()));
        }
        if (files.Count != ExpectedAssetNames.Length
            || !files.Select(file => file.Name).ToHashSet(StringComparer.Ordinal).SetEquals(ExpectedAssetNames))
            throw new DiagnosticRefusalException("MODEL_BUNDLE_INCOMPLETE");
        return new CandidateProfile(localDirectory, files, Revision, requiredForP0: false);
    }

    private static void VerifyLockedRuntime(string workspace, CandidateProfile profile)
    {
        if (profile.RequiredForP0)
            throw new DiagnosticRefusalException("CANDIDATE_PRODUCTION_ROUTE_FORBIDDEN");
        var packageLockPath = Path.Combine(workspace, "src", "XiaoK.Voice", "packages.lock.json");
        if (!File.Exists(packageLockPath) || HasReparsePointInPath(packageLockPath))
            throw new DiagnosticRefusalException("SHERPA_PACKAGE_LOCK_UNAVAILABLE");
        using var packageLock = JsonDocument.Parse(File.ReadAllBytes(packageLockPath), new JsonDocumentOptions { MaxDepth = 16 });
        var resolved = packageLock.RootElement.GetProperty("dependencies").GetProperty("net10.0")
            .GetProperty("org.k2fsa.sherpa.onnx").GetProperty("resolved").GetString();
        if (resolved != SherpaVersion)
            throw new DiagnosticRefusalException("SHERPA_PACKAGE_VERSION_MISMATCH");
    }

    private static void VerifyCandidateFiles(string modelRoot, CandidateProfile profile)
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(modelRoot));
        var modelDirectory = Path.GetFullPath(Path.Combine(root, profile.LocalDirectory));
        if (!IsWithinRoot(modelDirectory, root) || HasReparsePointInPath(modelDirectory))
            throw new DiagnosticRefusalException("MODEL_PATH_ESCAPES_ROOT");
        foreach (var file in profile.Files)
        {
            var path = Path.GetFullPath(Path.Combine(modelDirectory, file.Name));
            if (!IsWithinRoot(path, modelDirectory) || HasReparsePointInPath(path) || !File.Exists(path))
                throw new DiagnosticRefusalException("MODEL_FILE_UNAVAILABLE");
            var info = new FileInfo(path);
            if (info.Length != file.SizeBytes)
                throw new DiagnosticRefusalException("MODEL_FILE_SIZE_MISMATCH");
            using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
                bufferSize: 1024 * 1024, FileOptions.SequentialScan);
            var digest = Convert.ToHexString(SHA256.HashData(input)).ToLowerInvariant();
            if (!CryptographicOperations.FixedTimeEquals(Convert.FromHexString(digest), Convert.FromHexString(file.Sha256)))
                throw new DiagnosticRefusalException("MODEL_FILE_HASH_MISMATCH");
        }
    }

    private static OfflineTtsConfig CreateTtsConfig(string modelDirectory)
    {
        var vits = new OfflineTtsVitsModelConfig
        {
            Model = Path.Combine(modelDirectory, "model.onnx"),
            Lexicon = Path.Combine(modelDirectory, "lexicon.txt"),
            Tokens = Path.Combine(modelDirectory, "tokens.txt"),
            DataDir = "",
            NoiseScale = 0.667f,
            NoiseScaleW = 0.8f,
            LengthScale = 1.0f,
            DictDir = ""
        };
        var model = new OfflineTtsModelConfig
        {
            Vits = vits,
            NumThreads = CpuThreads,
            Debug = 0,
            Provider = "cpu"
        };
        return new OfflineTtsConfig
        {
            Model = model,
            RuleFsts = string.Join(',', new[] { "phone.fst", "date.fst", "number.fst" }
                .Select(name => Path.Combine(modelDirectory, name))),
            RuleFars = "",
            MaxNumSentences = 1,
            SilenceScale = 0.2f
        };
    }

    private static AudioSummary MeasureAudio(float[] samples, int sampleRate)
    {
        if (sampleRate is < 8000 or > 96000 || samples.Length is 0 || samples.Length > sampleRate * 60L)
            throw new DiagnosticRefusalException("GENERATED_AUDIO_BOUNDS_INVALID");
        double squareSum = 0;
        var peak = 0f;
        var clippedSampleCount = 0;
        foreach (var sample in samples)
        {
            if (!float.IsFinite(sample)) throw new DiagnosticRefusalException("GENERATED_AUDIO_NOT_FINITE");
            peak = Math.Max(peak, Math.Abs(sample));
            if (Math.Abs(sample) > 1f) clippedSampleCount++;
            var value = Math.Clamp(sample, -1f, 1f);
            squareSum += value * value;
        }
        var rms = Math.Sqrt(squareSum / samples.Length);
        if (peak < 0.001f || rms < 0.0001)
            throw new DiagnosticRefusalException("GENERATED_AUDIO_SILENT");
        return new AudioSummary(sampleRate, samples.Length, Math.Round(samples.Length / (double)sampleRate, 3),
            MathF.Round(peak, 5), Math.Round(rms, 5), clippedSampleCount);
    }

    private static byte[] BuildWave(float[] samples, int sampleRate)
    {
        if (sampleRate is < 8000 or > 96000 || samples.Length is 0 || samples.Length > sampleRate * 60L
            || samples.Length > (int.MaxValue - 44) / 2)
            throw new DiagnosticRefusalException("WAV_SIZE_INVALID");
        var dataBytes = checked(samples.Length * sizeof(short));
        var wave = new byte[checked(44 + dataBytes)];
        "RIFF"u8.CopyTo(wave.AsSpan(0, 4));
        BinaryPrimitives.WriteInt32LittleEndian(wave.AsSpan(4, 4), checked(36 + dataBytes));
        "WAVEfmt "u8.CopyTo(wave.AsSpan(8, 8));
        BinaryPrimitives.WriteInt32LittleEndian(wave.AsSpan(16, 4), 16);
        BinaryPrimitives.WriteInt16LittleEndian(wave.AsSpan(20, 2), 1);
        BinaryPrimitives.WriteInt16LittleEndian(wave.AsSpan(22, 2), 1);
        BinaryPrimitives.WriteInt32LittleEndian(wave.AsSpan(24, 4), sampleRate);
        BinaryPrimitives.WriteInt32LittleEndian(wave.AsSpan(28, 4), checked(sampleRate * sizeof(short)));
        BinaryPrimitives.WriteInt16LittleEndian(wave.AsSpan(32, 2), sizeof(short));
        BinaryPrimitives.WriteInt16LittleEndian(wave.AsSpan(34, 2), 16);
        "data"u8.CopyTo(wave.AsSpan(36, 4));
        BinaryPrimitives.WriteInt32LittleEndian(wave.AsSpan(40, 4), dataBytes);
        try
        {
            var pcmSamples = wave.AsSpan(44);
            for (var index = 0; index < samples.Length; index++)
            {
                var sample = samples[index];
                if (!float.IsFinite(sample)) throw new DiagnosticRefusalException("GENERATED_AUDIO_NOT_FINITE");
                var clamped = Math.Clamp(sample, -1f, 1f);
                var pcm = (short)Math.Round(clamped * (clamped < 0 ? 32768.0 : 32767.0));
                BinaryPrimitives.WriteInt16LittleEndian(pcmSamples.Slice(index * sizeof(short), sizeof(short)), pcm);
            }
            return wave;
        }
        catch
        {
            CryptographicOperations.ZeroMemory(wave);
            throw;
        }
    }

    private static void RefuseWhileObservationIsLive(string workspace)
    {
        var stabilityRoot = Path.Combine(workspace, ".tools", "stability");
        if (!Directory.Exists(stabilityRoot)) return;
        if (HasReparsePointInPath(stabilityRoot))
            throw new DiagnosticRefusalException("OBSERVATION_STATE_PATH_INVALID");
        foreach (var controlPath in Directory.EnumerateFiles(stabilityRoot, "*.control.json", SearchOption.TopDirectoryOnly))
        {
            if (HasReparsePointInPath(controlPath))
                throw new DiagnosticRefusalException("OBSERVATION_CONTROL_INVALID");
            var info = new FileInfo(controlPath);
            if (info.Length is <= 0 or > 64 * 1024)
                throw new DiagnosticRefusalException("OBSERVATION_CONTROL_INVALID");
            using var document = JsonDocument.Parse(File.ReadAllBytes(controlPath), new JsonDocumentOptions { MaxDepth = 16 });
            if (IsLiveObserver(document.RootElement, GetProcessStartUtc))
                throw new DiagnosticRefusalException("LONG_RUN_OBSERVER_ACTIVE");
        }
    }

    private static bool IsLiveObserver(JsonElement control, Func<int, DateTimeOffset?> processStartLookup)
    {
        if (!control.TryGetProperty("state", out var state) || state.ValueKind != JsonValueKind.String)
            throw new DiagnosticRefusalException("OBSERVATION_CONTROL_INVALID");
        var stateText = state.GetString();
        if (stateText != "running")
        {
            if (stateText is "completed" or "interrupted" or "monitor_interrupted" or "stopped")
                return false;
            throw new DiagnosticRefusalException("OBSERVATION_CONTROL_INVALID");
        }
        if (!control.TryGetProperty("monitorProcessId", out var pidElement)
            || !pidElement.TryGetInt32(out var pid) || pid <= 0
            || !control.TryGetProperty("monitorStartedUtc", out var startElement)
            || !DateTimeOffset.TryParse(startElement.GetString(), CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var expectedStart))
            throw new DiagnosticRefusalException("OBSERVATION_CONTROL_INVALID");

        var actualStart = processStartLookup(pid);
        return actualStart.HasValue && (actualStart.Value - expectedStart).Duration() <= MaximumObserverStartTimeSkew;
    }

    private static DateTimeOffset? GetProcessStartUtc(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return process.StartTime.ToUniversalTime();
        }
        catch (ArgumentException) { return null; }
        catch (InvalidOperationException) { return null; }
        catch (System.ComponentModel.Win32Exception)
        {
            throw new DiagnosticRefusalException("OBSERVER_IDENTITY_UNVERIFIABLE");
        }
    }

    private static void RefuseForCompetingModelProcesses()
    {
        if (HasProcessNamed("XiaoK.Host") || HasProcessNamed("llama-server") || HasProcessNamed("llama-bench"))
            throw new DiagnosticRefusalException("COMPETING_MODEL_PROCESS_ACTIVE");
    }

    private static bool HasProcessNamed(string name)
    {
        var processes = Process.GetProcessesByName(name);
        var found = processes.Length != 0;
        foreach (var process in processes)
            process.Dispose();
        return found;
    }

    private static void EnsureMemoryFloor(AvailableMemorySampler sampler)
    {
        if (sampler.SamplingFailed || sampler.MinimumAvailableMiB < MinimumRuntimeAvailableMemoryMiB)
            throw new DiagnosticRefusalException("RAM_RUNTIME_FLOOR_BREACHED");
    }

    private static int GetAvailableMemoryMiB()
    {
        var status = new MemoryStatusEx { Length = (uint)Marshal.SizeOf<MemoryStatusEx>() };
        if (!GlobalMemoryStatusEx(ref status))
            throw new DiagnosticRefusalException("SYSTEM_MEMORY_UNAVAILABLE");
        return checked((int)(status.AvailablePhysicalBytes / (1024 * 1024)));
    }

    private static int[] GetWorkerProcessIds(string pythonPath)
    {
        var expectedPath = Path.GetFullPath(pythonPath);
        var results = new List<int>();
        foreach (var process in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(pythonPath)))
        {
            using (process)
            {
                try
                {
                    var actualPath = process.MainModule?.FileName;
                    if (actualPath is not null && string.Equals(Path.GetFullPath(actualPath), expectedPath,
                            StringComparison.OrdinalIgnoreCase))
                        results.Add(process.Id);
                }
                catch (System.ComponentModel.Win32Exception)
                {
                    throw new DiagnosticRefusalException("ASR_WORKER_IDENTITY_UNVERIFIABLE");
                }
                catch (InvalidOperationException) { }
            }
        }
        return results.ToArray();
    }

    private static string Normalize(string text) => string.Concat(text.Where(character =>
        !char.IsWhiteSpace(character) && !char.IsPunctuation(character)).Select(char.ToLowerInvariant));

    private static double PercentileNearestRank(IReadOnlyList<double> values, double percentile)
    {
        if (values.Count == 0) return 0;
        var sorted = values.Order().ToArray();
        var index = Math.Clamp((int)Math.Ceiling(percentile * sorted.Length) - 1, 0, sorted.Length - 1);
        return Math.Round(sorted[index], 2);
    }

    private static string GetString(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? "" : "";

    private static string FindWorkspaceRoot(string startingDirectory)
    {
        DirectoryInfo? current = new DirectoryInfo(Path.GetFullPath(startingDirectory));
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "model-lock", "models.lock.json")))
                return current.FullName;
            current = current.Parent;
        }
        return Path.GetFullPath(startingDirectory);
    }

    private static bool IsWithinRoot(string candidate, string root)
    {
        var fullCandidate = Path.GetFullPath(candidate);
        var fullRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        return string.Equals(fullCandidate, fullRoot, StringComparison.OrdinalIgnoreCase)
            || fullCandidate.StartsWith(fullRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    private static bool HasReparsePointInPath(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var current = fullPath;
        while (!string.IsNullOrEmpty(current))
        {
            if ((File.Exists(current) || Directory.Exists(current))
                && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                return true;
            var parent = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(current));
            if (string.IsNullOrEmpty(parent) || string.Equals(parent, current, StringComparison.OrdinalIgnoreCase))
                break;
            current = parent;
        }
        return false;
    }

    private static async Task WriteNewReportAsync<T>(string reportPath, T report)
    {
        var reportDirectory = Path.GetDirectoryName(reportPath)
            ?? throw new DiagnosticRefusalException("REPORT_PATH_INVALID");
        if (!Directory.Exists(reportDirectory) || HasReparsePointInPath(reportDirectory))
            throw new DiagnosticRefusalException("REPORT_PATH_INVALID");
        await using var output = new FileStream(reportPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        await JsonSerializer.SerializeAsync(output, report,
                new JsonSerializerOptions { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase })
            .ConfigureAwait(false);
        await output.FlushAsync().ConfigureAwait(false);
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed record CandidateProfile(string LocalDirectory, IReadOnlyList<LockedFile> Files,
        string Revision, bool RequiredForP0);
    private sealed record LockedFile(string Name, long SizeBytes, string Sha256);
    private sealed record AudioSummary(int SampleRate, int SampleCount, double DurationSeconds, float Peak, double Rms,
        int ClippedSampleCount);

    private sealed class DiagnosticRefusalException(string code) : Exception
    {
        public string Code { get; } = code;
    }

    private sealed class AvailableMemorySampler : IAsyncDisposable
    {
        private readonly Timer _timer;
        private int _minimumAvailableMiB;
        private int _samplingFailed;

        public AvailableMemorySampler(int initialAvailableMiB)
        {
            _minimumAvailableMiB = initialAvailableMiB;
            _timer = new Timer(Sample, null, TimeSpan.Zero, TimeSpan.FromMilliseconds(500));
        }

        public int MinimumAvailableMiB => Volatile.Read(ref _minimumAvailableMiB);
        public bool SamplingFailed => Volatile.Read(ref _samplingFailed) != 0;

        public ValueTask DisposeAsync() => _timer.DisposeAsync();

        private void Sample(object? _)
        {
            try
            {
                var current = GetAvailableMemoryMiB();
                var observed = Volatile.Read(ref _minimumAvailableMiB);
                while (current < observed)
                {
                    var prior = Interlocked.CompareExchange(ref _minimumAvailableMiB, current, observed);
                    if (prior == observed) break;
                    observed = prior;
                }
            }
            catch { Volatile.Write(ref _samplingFailed, 1); }
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct MemoryStatusEx
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhysicalBytes;
        public ulong AvailablePhysicalBytes;
        public ulong TotalPageFileBytes;
        public ulong AvailablePageFileBytes;
        public ulong TotalVirtualBytes;
        public ulong AvailableVirtualBytes;
        public ulong AvailableExtendedVirtualBytes;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx buffer);
}
