using SherpaOnnx;
using System.Text.RegularExpressions;

namespace XiaoK.Voice;

/// <summary>
/// CPU-only streaming keyword detector. The caller owns microphone capture and must serialize calls.
/// Audio frames are copied only for the native call and cleared immediately afterward.
/// </summary>
public sealed class SherpaKeywordSpotter : IDisposable
{
    public const int SampleRate = 16_000;
    public const int MaximumSamplesPerCall = SampleRate;
    public const string XiaoKWakeWordKeywords = "h āi x iǎo k ēi @XIAOK_HI\nn ǐ h ǎo x iǎo k ēi @XIAOK_HELLO\nHH AY1 S Y AW1 K EY1 @XIAOK_HI_EN";
    private const long MaximumKeywordFileBytes = 64 * 1024;
    private static readonly Regex KeywordLabelPattern = new("^@[A-Z0-9_]{1,48}$", RegexOptions.CultureInvariant);

    private readonly KeywordSpotter _spotter;
    private readonly OnlineStream _stream;
    private bool _disposed;
    private bool _finished;

    private SherpaKeywordSpotter(KeywordSpotter spotter, string? keywordBuffer = null)
    {
        _spotter = spotter;
        _stream = keywordBuffer is null ? spotter.CreateStream() : spotter.CreateStream(keywordBuffer);
    }

    public static SherpaKeywordSpotter Create(string modelDirectory, string keywordsFile)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(keywordsFile);

        var root = Path.GetFullPath(modelDirectory);
        if (!Directory.Exists(root)) throw new DirectoryNotFoundException("KWS 模型目录不存在。");
        var keywords = Path.GetFullPath(keywordsFile);
        var rootedPrefix = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        if (!keywords.StartsWith(rootedPrefix, StringComparison.OrdinalIgnoreCase))
            throw new UnauthorizedAccessException("KWS 关键词文件必须位于锁定模型目录内。");
        var keywordInfo = new FileInfo(keywords);
        if (!keywordInfo.Exists || keywordInfo.Length is <= 0 or > MaximumKeywordFileBytes)
            throw new InvalidDataException("KWS 关键词文件不存在、为空或超过64 KiB。");

        var config = CreateConfig(root, keywords, string.Empty);

        var spotter = new KeywordSpotter(config);
        try { return new SherpaKeywordSpotter(spotter); }
        catch
        {
            spotter.Dispose();
            throw;
        }
    }

    /// <summary>Creates a detector stream for a bounded set of caller-supplied keywords.</summary>
    public static SherpaKeywordSpotter CreateWithKeywords(string modelDirectory, string keywordBuffer)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(keywordBuffer);

        var root = Path.GetFullPath(modelDirectory);
        if (!Directory.Exists(root)) throw new DirectoryNotFoundException("KWS 模型目录不存在。");
        var keywordBufferLength = System.Text.Encoding.UTF8.GetByteCount(keywordBuffer);
        if (keywordBufferLength <= 0 || keywordBufferLength > MaximumKeywordFileBytes || keywordBuffer.Contains('\0'))
            throw new InvalidDataException("KWS 关键词缓冲区无效或超过64 KiB。");
        var tokensPath = RequireFile(root, "tokens.txt");
        ValidateKeywordBuffer(tokensPath, keywordBuffer);
        // The C API requires a model-level keyword source even when a stream supplies its own
        // phrase buffer through KeywordSpotter.CreateStream(keywords).
        var config = CreateConfig(root, RequireFile(root, "test_wavs/keywords.txt"), string.Empty);
        var spotter = new KeywordSpotter(config);
        try { return new SherpaKeywordSpotter(spotter, keywordBuffer); }
        catch
        {
            spotter.Dispose();
            throw;
        }
    }

    /// <summary>Consumes one bounded mono float frame and returns only configured keyword labels.</summary>
    public IReadOnlyList<string> AcceptWaveform(ReadOnlySpan<float> samples, int sampleRate = SampleRate)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_finished) throw new InvalidOperationException("KWS 流已结束；请创建新的检测器。");
        if (sampleRate != SampleRate)
            throw new ArgumentOutOfRangeException(nameof(sampleRate), "KWS 输入必须为16 kHz；本模块不重采样。");
        if (samples.IsEmpty || samples.Length > MaximumSamplesPerCall)
            throw new ArgumentOutOfRangeException(nameof(samples), "每次KWS输入必须为1到16000个样本。");

        var ownedSamples = samples.ToArray();
        try
        {
            for (var index = 0; index < ownedSamples.Length; index++)
            {
                if (!float.IsFinite(ownedSamples[index]))
                    throw new InvalidDataException("KWS 输入包含非有限浮点样本。");
                ownedSamples[index] = Math.Clamp(ownedSamples[index], -1.0f, 1.0f);
            }

            _stream.AcceptWaveform(SampleRate, ownedSamples);
            return DecodeReady();
        }
        finally
        {
            Array.Clear(ownedSamples);
        }
    }

    /// <summary>Flushes an offline stream after its caller has appended bounded trailing silence.</summary>
    public IReadOnlyList<string> Finish()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_finished) throw new InvalidOperationException("KWS 流已结束。");
        _finished = true;
        _stream.InputFinished();
        return DecodeReady();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _stream.Dispose(); }
        finally { _spotter.Dispose(); }
    }

    private static string RequireFile(string directory, string name)
    {
        var path = Path.GetFullPath(Path.Combine(directory, name));
        if (!File.Exists(path)) throw new FileNotFoundException($"缺少锁定KWS模型文件：{name}");
        return path;
    }

    private static KeywordSpotterConfig CreateConfig(string root, string keywordsFile, string keywordsBuffer) => new()
    {
        FeatConfig = new FeatureConfig { SampleRate = SampleRate, FeatureDim = 80 },
        ModelConfig = new OnlineModelConfig
        {
            Transducer = new OnlineTransducerModelConfig
            {
                Encoder = RequireFile(root, "encoder-epoch-13-avg-2-chunk-16-left-64.int8.onnx"),
                Decoder = RequireFile(root, "decoder-epoch-13-avg-2-chunk-16-left-64.onnx"),
                Joiner = RequireFile(root, "joiner-epoch-13-avg-2-chunk-16-left-64.int8.onnx")
            },
            Tokens = RequireFile(root, "tokens.txt"),
            Provider = "cpu",
            NumThreads = 1,
            Debug = 0
        },
        MaxActivePaths = 4,
        NumTrailingBlanks = 1,
        KeywordsScore = 1.0f,
        KeywordsThreshold = 0.25f,
        KeywordsFile = keywordsFile,
        KeywordsBuf = keywordsBuffer,
        KeywordsBufSize = 0
    };

    private static void ValidateKeywordBuffer(string tokensPath, string keywordBuffer)
    {
        var tokensInfo = new FileInfo(tokensPath);
        if (!tokensInfo.Exists || tokensInfo.Length is <= 0 or > 1024 * 1024)
            throw new InvalidDataException("KWS tokens文件无效或超过1 MiB。");
        var vocabulary = File.ReadLines(tokensPath)
            .Select(line => line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault())
            .Where(token => !string.IsNullOrWhiteSpace(token))
            .ToHashSet(StringComparer.Ordinal);
        var lines = keywordBuffer.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (lines.Length is < 1 or > 16)
            throw new InvalidDataException("KWS关键词数量不在1到16条范围内。");
        foreach (var line in lines)
        {
            if (line.Length > 512 || line.Contains('\r') || line.Any(char.IsControl))
                throw new InvalidDataException("KWS关键词行包含控制字符或超过512字符。");
            var parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length is < 2 or > 64 || !KeywordLabelPattern.IsMatch(parts[^1]))
                throw new InvalidDataException("KWS关键词行必须以固定格式的@标签结尾。");
            for (var index = 0; index < parts.Length - 1; index++)
                if (!vocabulary.Contains(parts[index]))
                    throw new InvalidDataException($"KWS关键词包含tokens中不存在的词元：{parts[index]}");
        }
    }

    private IReadOnlyList<string> DecodeReady()
    {
        var detections = new List<string>();
        var iterations = 0;
        while (_spotter.IsReady(_stream))
        {
            if (++iterations > 128)
                throw new InvalidOperationException("KWS 解码步数超过单次输入上限；必须停止监听并检查输入速率。");
            _spotter.Decode(_stream);
            var keyword = _spotter.GetResult(_stream).Keyword?.Trim();
            if (string.IsNullOrWhiteSpace(keyword)) continue;
            detections.Add(keyword);
            _spotter.Reset(_stream);
        }
        return detections;
    }
}
