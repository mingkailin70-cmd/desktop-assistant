using System.Buffers.Binary;
using System.IO;
using System.Security.Cryptography;
using Windows.Foundation;
using Windows.Media.SpeechSynthesis;
using Windows.Storage.Streams;

namespace XiaoK.Host;

/// <summary>Uses an already installed Windows zh-CN voice; it never downloads weights or writes audio to disk.</summary>
internal sealed class WindowsInstalledChineseSpeech
{
    public const int MaximumTextLength = 1000;
    public const int MaximumAudioBytes = 12 * 1024 * 1024;

    private readonly string _voiceId;

    private WindowsInstalledChineseSpeech(string voiceId, string voiceName)
    {
        _voiceId = voiceId;
        VoiceName = voiceName;
        Status = $"Windows 系统中文语音候选：{voiceName}；仅在点击播报或模型资源不满足时按需合成，断网与听感仍待验收。";
    }

    public string VoiceName { get; }
    public string Status { get; }

    public static WindowsInstalledChineseSpeech? TryCreate(out string status)
    {
        try
        {
            var voice = SpeechSynthesizer.AllVoices
                .Where(candidate => IsSupportedChineseVoiceLanguage(candidate.Language))
                .OrderBy(candidate => candidate.Id, StringComparer.Ordinal)
                .FirstOrDefault();
            if (voice is null)
            {
                status = "语音：没有发现已安装的简体中文 Windows 语音；语音播报不可用，麦克风未采集。";
                return null;
            }

            var available = new WindowsInstalledChineseSpeech(voice.Id, voice.DisplayName);
            status = available.Status;
            return available;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException
                                   and not AccessViolationException)
        {
            status = "语音：无法枚举 Windows 系统中文语音；备用播报不可用，麦克风未采集。";
            return null;
        }
    }

    internal static bool IsSupportedChineseVoiceLanguage(string? language) =>
        string.Equals(language, "zh-CN", StringComparison.OrdinalIgnoreCase)
        || string.Equals(language, "zh-Hans-CN", StringComparison.OrdinalIgnoreCase);

    internal static bool IsValidWave(ReadOnlySpan<byte> audio) =>
        audio.Length is >= 44 and <= MaximumAudioBytes
        && audio[..4].SequenceEqual("RIFF"u8)
        && BinaryPrimitives.ReadUInt32LittleEndian(audio[4..8]) == audio.Length - 8
        && audio[8..12].SequenceEqual("WAVE"u8);

    public async Task<byte[]> SynthesizeWavAsync(string text, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length > MaximumTextLength)
            throw new ArgumentOutOfRangeException(nameof(text), "播报文本长度必须为1到1000个字符。");

        cancellationToken.ThrowIfCancellationRequested();
        var voice = SpeechSynthesizer.AllVoices.FirstOrDefault(candidate =>
            candidate.Id.Equals(_voiceId, StringComparison.Ordinal)
            && IsSupportedChineseVoiceLanguage(candidate.Language));
        if (voice is null)
            throw new InvalidOperationException("已选 Windows 中文语音不再可用；没有生成或播放音频。");

        using var synthesizer = new SpeechSynthesizer { Voice = voice };
        var synthesis = synthesizer.SynthesizeTextToStreamAsync(text);
        using var speech = await AwaitWithCancellationAsync(synthesis, cancellationToken);
        if (speech.Size is < 44 or > MaximumAudioBytes)
            throw new InvalidDataException("Windows 系统语音输出超过12 MiB或不是有效 WAV；没有播放音频。");

        using var reader = new DataReader(speech.GetInputStreamAt(0));
        var load = reader.LoadAsync(checked((uint)speech.Size));
        var loaded = await AwaitWithCancellationAsync(load, cancellationToken);
        if (loaded is < 44 or > MaximumAudioBytes || loaded != speech.Size)
            throw new InvalidDataException("Windows 系统语音没有返回完整的有界 WAV；没有播放音频。");

        var wav = new byte[checked((int)loaded)];
        try
        {
            reader.ReadBytes(wav);
            if (!IsValidWave(wav))
                throw new InvalidDataException("Windows 系统语音输出不是有效 WAV；没有播放音频。");

            cancellationToken.ThrowIfCancellationRequested();
            return wav;
        }
        catch
        {
            CryptographicOperations.ZeroMemory(wav);
            throw;
        }
    }

    private static async Task<T> AwaitWithCancellationAsync<T>(IAsyncOperation<T> operation,
        CancellationToken cancellationToken)
    {
        using var registration = cancellationToken.Register(static state => TryCancel((IAsyncInfo)state!),
            operation);
        try
        {
            return await operation;
        }
        catch (Exception) when (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(cancellationToken);
        }
    }

    private static void TryCancel(IAsyncInfo operation)
    {
        try { operation.Cancel(); }
        catch (Exception) { }
    }
}
