using System.Buffers.Binary;
using System.IO;
using XiaoK.Voice;

namespace XiaoK.Host;

/// <summary>Strict framing and response allowlist for the local KWS worker pipe.</summary>
internal static class SherpaKwsWorkerProtocol
{
    public const int MaximumProtocolLineLength = 64;
    public const int FrameHeaderLength = sizeof(int);
    public const int MaximumSamplesPerFrame = SherpaKeywordSpotter.MaximumSamplesPerCall;

    public static async Task<string?> ReadLineAsync(TextReader reader, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(reader);

        var line = new char[MaximumProtocolLineLength];
        var nextCharacter = new char[1];
        var length = 0;
        var pendingCarriageReturn = false;
        while (true)
        {
            var read = await reader.ReadAsync(nextCharacter.AsMemory(), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                if (pendingCarriageReturn)
                {
                    if (length == line.Length)
                        throw new InvalidDataException("KWS worker协议行超过限制。");
                    line[length++] = '\r';
                }
                return length == 0 ? null : new string(line, 0, length);
            }

            var character = nextCharacter[0];
            if (character == '\n')
                return new string(line, 0, length);

            if (pendingCarriageReturn)
            {
                if (length == line.Length)
                    throw new InvalidDataException("KWS worker协议行超过限制。");
                line[length++] = '\r';
                pendingCarriageReturn = false;
            }

            if (character == '\r')
            {
                pendingCarriageReturn = true;
                continue;
            }

            if (length == line.Length)
                throw new InvalidDataException("KWS worker协议行超过限制。");
            line[length++] = character;
        }
    }

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
