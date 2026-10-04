using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using XiaoK.Inference;
using XiaoK.Voice;

internal static class VoiceHostRoundTrip
{
    private const string SyntheticPhrase = "你好，小K，请打开本地项目。";
    private const string AsrRevision = "5eb144179a02acc5e5ba31e748d22b0cf3e303b0";
    private const string TtsRevision = "85e237c12c027371202489a0ec509ded67b5e4b5";

    public static async Task<int> RunAsync(string workspaceArgument, string environmentArgument,
        string reportArgument)
    {
        if (!OperatingSystem.IsWindows())
        {
            Console.Error.WriteLine("Host 语音诊断只支持 Windows。");
            return 2;
        }

        var workspace = Path.TrimEndingDirectorySeparator(Path.GetFullPath(workspaceArgument));
        var environment = Path.GetFullPath(environmentArgument);
        var reportPath = Path.GetFullPath(reportArgument);
        var reportRelative = Path.GetRelativePath(workspace, reportPath);
        if (Path.IsPathRooted(reportRelative) || reportRelative != ".."
            && !reportRelative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
        {
            Console.Error.WriteLine("报告必须保存到仓库目录之外，避免把本机评测数据加入 Git。");
            return 2;
        }
        if (File.Exists(reportPath))
        {
            Console.Error.WriteLine("报告路径已存在；为避免覆盖历史记录，本次未运行。");
            return 2;
        }

        var totalClock = Stopwatch.StartNew();
        var ttsClockMs = 0d;
        var asrClockMs = 0d;
        var audioBytes = 0;
        var normalizedMatch = false;
        var languageReported = false;
        var errorType = (string?)null;
        var modelBrokerWasUsed = false;
        var serviceCreated = false;
        var wav = Array.Empty<byte>();
        VoiceInferenceService? voice = null;
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(5));

        try
        {
            var broker = new ModelBroker();
            voice = VoiceInferenceService.TryCreateForWorkspace(workspace, environment,
                Path.Combine(workspace, "src", "XiaoK.Voice", "voice_worker.py"), broker, out _);
            serviceCreated = voice is not null;
            if (voice is null)
                throw new InvalidOperationException("VOICE_SERVICE_UNAVAILABLE");

            var ttsClock = Stopwatch.StartNew();
            wav = await voice.SynthesizeChineseWavAsync(SyntheticPhrase, deadline.Token);
            ttsClock.Stop();
            ttsClockMs = ttsClock.Elapsed.TotalMilliseconds;
            audioBytes = wav.Length;

            var asrClock = Stopwatch.StartNew();
            var recognition = await voice.TranscribeWavAsync(wav, forceChinese: true, deadline.Token);
            asrClock.Stop();
            asrClockMs = asrClock.Elapsed.TotalMilliseconds;
            languageReported = !string.IsNullOrWhiteSpace(recognition.Language);
            normalizedMatch = Normalize(recognition.Text).Equals(Normalize(SyntheticPhrase), StringComparison.Ordinal);
            modelBrokerWasUsed = broker.LastUseUtc is not null;
        }
        catch (Exception exception)
        {
            // Store only the exception type; exception messages can contain local paths or worker output.
            errorType = exception.GetType().Name;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(wav);
            if (voice is not null)
            {
                try { await voice.DisposeAsync(); }
                catch (Exception exception) { errorType ??= exception.GetType().Name; }
            }
        }

        totalClock.Stop();
        var passed = errorType is null && serviceCreated && modelBrokerWasUsed && audioBytes > 44
            && normalizedMatch && languageReported;
        var report = new
        {
            schemaVersion = 1,
            startedAtUtc = DateTimeOffset.UtcNow - totalClock.Elapsed,
            completedAtUtc = DateTimeOffset.UtcNow,
            path = "VoiceInferenceService -> ModelBroker -> locked local CPU TTS -> in-memory WAV -> locked local CPU ASR",
            modelRevisions = new { asr = AsrRevision, tts = TtsRevision },
            sampleCount = 1,
            syntheticTextAndAudioSaved = false,
            transcriptSaved = false,
            wavBytesInMemory = audioBytes,
            wavZeroedAfterUse = wav.Length == 0 || wav.All(value => value == 0),
            modelBrokerObservedUse = modelBrokerWasUsed,
            ttsElapsedMs = ttsClockMs,
            asrElapsedMs = asrClockMs,
            transcriptNormalizedMatch = normalizedMatch,
            languageFieldPresent = languageReported,
            totalElapsedMs = totalClock.Elapsed.TotalMilliseconds,
            errorType,
            diagnosticPassed = passed
        };

        Directory.CreateDirectory(Path.GetDirectoryName(reportPath)!);
        await File.WriteAllTextAsync(reportPath,
            JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }), Encoding.UTF8);
        Console.WriteLine($"Host 语音 CPU 合成往返：{(passed ? "通过" : "未通过")}；"
            + $"TTS {ttsClockMs:F0} ms，ASR {asrClockMs:F0} ms，文本匹配 {normalizedMatch}。"
            + "没有保存音频或转写内容。");
        Console.WriteLine($"匿名报告：{reportPath}");
        return passed ? 0 : 1;
    }

    private static string Normalize(string text)
    {
        var normalized = text.Normalize(NormalizationForm.FormKC);
        return string.Concat(normalized.Where(char.IsLetterOrDigit));
    }
}
