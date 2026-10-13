using System.Buffers.Binary;
using XiaoK.Voice;

namespace XiaoK.Host;

/// <summary>Strict framing and response allowlist for the local KWS worker pipe.</summary>
internal static class SherpaKwsWorkerProtocol
{
    public const int FrameHeaderLength = sizeof(int);
    public const int MaximumSamplesPerFrame = SherpaKeywordSpotter.MaximumSamplesPerCall;

    public static void WriteSampleCount(Span<byte> header, int sampleCount)
    {
        if (header.Length != FrameHeaderLength)
            throw new ArgumentException("KWS frame header must be exactly four bytes.", nameof(header));
        if (sampleCount is < 1 or > MaximumSamplesPerFrame)
            throw new ArgumentOutOfRangeException(nameof(sampleCount));
        BinaryPrimitives.WriteInt32LittleEndian(header, sampleCount);
    }

    public static bool TryReadSampleCount(ReadOnlySpan<byte> header, out int sampleCount)
    {
        sampleCount = 0;
        if (header.Length != FrameHeaderLength)
            return false;

        var value = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (value is < 1 or > MaximumSamplesPerFrame)
            return false;

        sampleCount = value;
        return true;
    }

    public static bool IsAllowedKeywordLabel(string label)
        => label is "XIAOK_HI" or "XIAOK_HELLO" or "XIAOK_HI_EN";
}
