using System.Buffers.Binary;
using System.Diagnostics;
using System.Text.Json;
using XiaoK.Voice;

try
{
    if (args.Length == 2 && args[0] == "--check-xiaok-keywords")
    {
        var candidateDirectory = Path.GetFullPath(args[1]);
        using var candidate = SherpaKeywordSpotter.CreateWithKeywords(candidateDirectory,
            SherpaKeywordSpotter.XiaoKWakeWordKeywords);
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            ok = true,
            microphoneOpened = false,
            device = "cpu",
            threads = 1,
            configuredLabels = new[] { "XIAOK_HI", "XIAOK_HELLO", "XIAOK_HI_EN" }
        }));
        return 0;
    }

    var options = ParseArguments(args);
    var modelDirectory = Path.GetFullPath(options.ModelDirectory);
    var wavePath = Path.GetFullPath(options.WavePath);
    var keywordsPath = Path.GetFullPath(options.KeywordsPath ?? Path.Combine(modelDirectory, "test_wavs", "keywords.txt"));

    RequireFile(Path.GetDirectoryName(wavePath)!, Path.GetFileName(wavePath));

    var (sampleRate, samples) = ReadMonoPcm16Wave(wavePath);
    var loadTimer = Stopwatch.StartNew();
    using var detector = SherpaKeywordSpotter.Create(modelDirectory, keywordsPath);
    loadTimer.Stop();

    var decodeTimer = Stopwatch.StartNew();
    var hits = new HashSet<string>(StringComparer.Ordinal);
    for (var offset = 0; offset < samples.Length; offset += SherpaKeywordSpotter.MaximumSamplesPerCall)
    {
        var count = Math.Min(SherpaKeywordSpotter.MaximumSamplesPerCall, samples.Length - offset);
        foreach (var keyword in detector.AcceptWaveform(samples.AsSpan(offset, count), sampleRate))
            hits.Add(keyword);
    }
    var tailPadding = new float[sampleRate / 2];
    try
    {
        foreach (var keyword in detector.AcceptWaveform(tailPadding, sampleRate)) hits.Add(keyword);
        foreach (var keyword in detector.Finish()) hits.Add(keyword);
    }
    finally { Array.Clear(tailPadding); }
    decodeTimer.Stop();

    Console.WriteLine(JsonSerializer.Serialize(new
    {
        ok = true,
        device = "cpu",
        threads = 1,
        sample = Path.GetFileName(wavePath),
        sampleRate,
        durationSeconds = Math.Round((double)samples.Length / sampleRate, 3),
        modelLoadMilliseconds = loadTimer.ElapsedMilliseconds,
        decodeMilliseconds = decodeTimer.ElapsedMilliseconds,
        streamingChunks = (samples.Length + SherpaKeywordSpotter.MaximumSamplesPerCall - 1)
            / SherpaKeywordSpotter.MaximumSamplesPerCall,
        detected = hits.Count > 0,
        keywords = hits.Order(StringComparer.Ordinal).ToArray()
    }));
    return 0;
}
catch (Exception error) when (error is ArgumentException or IOException or InvalidDataException
                               or InvalidOperationException or UnauthorizedAccessException
                               or DllNotFoundException or BadImageFormatException or EntryPointNotFoundException)
{
    Console.Error.WriteLine(JsonSerializer.Serialize(new { ok = false, error = error.Message }));
    return 2;
}

static KwsOptions ParseArguments(string[] arguments)
{
    string? modelDirectory = null;
    string? wavePath = null;
    string? keywordsPath = null;
    for (var index = 0; index < arguments.Length; index++)
    {
        var option = arguments[index];
        if (index + 1 >= arguments.Length)
            throw new ArgumentException($"参数缺少值：{option}");
        var value = arguments[++index];
        switch (option)
        {
            case "--model-dir": modelDirectory = value; break;
            case "--wav": wavePath = value; break;
            case "--keywords": keywordsPath = value; break;
            default: throw new ArgumentException($"不支持的参数：{option}");
        }
    }

    if (string.IsNullOrWhiteSpace(modelDirectory) || string.IsNullOrWhiteSpace(wavePath))
        throw new ArgumentException("用法：XiaoK.KwsDiagnostics --model-dir <模型目录> --wav <单声道16kHz PCM16 WAV> [--keywords <关键词文件>]");

    return new KwsOptions(modelDirectory, wavePath, keywordsPath);
}

static string RequireFile(string directory, string name)
{
    var path = Path.GetFullPath(Path.Combine(directory, name));
    if (!File.Exists(path)) throw new FileNotFoundException($"缺少本地 KWS 文件：{name}");
    return path;
}

static (int SampleRate, float[] Samples) ReadMonoPcm16Wave(string path)
{
    var bytes = File.ReadAllBytes(path);
    if (bytes.Length is < 44 or > 64 * 1024 * 1024
        || !bytes.AsSpan(0, 4).SequenceEqual("RIFF"u8)
        || !bytes.AsSpan(8, 4).SequenceEqual("WAVE"u8))
        throw new InvalidDataException("输入必须是小于64 MiB的标准 RIFF/WAVE 文件。");

    ushort format = 0;
    ushort channels = 0;
    ushort bitsPerSample = 0;
    var sampleRate = 0;
    ReadOnlySpan<byte> sampleBytes = default;
    var offset = 12;
    while (offset <= bytes.Length - 8)
    {
        var chunkSizeValue = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset + 4, 4));
        if (chunkSizeValue > int.MaxValue) throw new InvalidDataException("WAV 区块大小无效。");
        var chunkSize = (int)chunkSizeValue;
        var contentOffset = offset + 8;
        if (chunkSize > bytes.Length - contentOffset) throw new InvalidDataException("WAV 区块超出文件范围。");

        if (bytes.AsSpan(offset, 4).SequenceEqual("fmt "u8))
        {
            if (chunkSize < 16) throw new InvalidDataException("WAV fmt 区块过短。");
            var formatBytes = bytes.AsSpan(contentOffset, chunkSize);
            format = BinaryPrimitives.ReadUInt16LittleEndian(formatBytes[..2]);
            channels = BinaryPrimitives.ReadUInt16LittleEndian(formatBytes.Slice(2, 2));
            sampleRate = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(formatBytes.Slice(4, 4)));
            bitsPerSample = BinaryPrimitives.ReadUInt16LittleEndian(formatBytes.Slice(14, 2));
        }
        else if (bytes.AsSpan(offset, 4).SequenceEqual("data"u8))
        {
            sampleBytes = bytes.AsSpan(contentOffset, chunkSize);
        }

        offset = checked(contentOffset + chunkSize + (chunkSize & 1));
    }

    if (format != 1 || channels != 1 || sampleRate != 16000 || bitsPerSample != 16
        || sampleBytes.IsEmpty || (sampleBytes.Length & 1) != 0)
        throw new InvalidDataException("WAV 必须是单声道、16 kHz、16-bit PCM；本工具不重采样或保存音频。");

    var samples = new float[sampleBytes.Length / sizeof(short)];
    for (var index = 0; index < samples.Length; index++)
    {
        var sample = BinaryPrimitives.ReadInt16LittleEndian(sampleBytes.Slice(index * sizeof(short), sizeof(short)));
        samples[index] = sample / 32768.0f;
    }

    return (sampleRate, samples);
}

internal sealed record KwsOptions(string ModelDirectory, string WavePath, string? KeywordsPath);
