using SherpaOnnx;

namespace XiaoK.Voice;

/// <summary>
/// CPU-only streaming keyword detector. The caller owns microphone capture and must serialize calls.
/// Audio frames are copied only for the native call and cleared immediately afterward.
/// </summary>
public sealed class SherpaKeywordSpotter : IDisposable
{
    public const int SampleRate = 16_000;
    public const int MaximumSamplesPerCall = SampleRate;
    private const long MaximumKeywordFileBytes = 64 * 1024;

    private readonly KeywordSpotter _spotter;
    private readonly OnlineStream _stream;
    private bool _disposed;
    private bool _finished;

    private SherpaKeywordSpotter(KeywordSpotter spotter)
    {
        _spotter = spotter;
        _stream = spotter.CreateStream();
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

        var config = new KeywordSpotterConfig
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
            KeywordsFile = keywords,
            KeywordsBuf = string.Empty,
            KeywordsBufSize = 0
        };

        var spotter = new KeywordSpotter(config);
        try { return new SherpaKeywordSpotter(spotter); }
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
