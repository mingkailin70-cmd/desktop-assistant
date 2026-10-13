using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using XiaoK.Voice;

namespace XiaoK.Host;

/// <summary>Streaming Sherpa KWS decoder. It receives bounded PCM frames and never opens a microphone.</summary>
internal static class SherpaKwsWorkerEntryPoint
{
    private const int StartupTimeoutSeconds = 30;
    private const int MaximumProtocolLineCharacters = 64;

    public static async Task<int> RunAsync(string[] args)
    {
        if (!WindowsPackageIdentity.IsPresent || args.Length != 3
            || !string.Equals(args[0], "--kws-worker", StringComparison.Ordinal)
            || !string.Equals(args[1], "--model-dir", StringComparison.Ordinal)
            || !Path.IsPathFullyQualified(args[2]))
        {
            await WriteStatusAsync("ERROR:INVALID_REQUEST").ConfigureAwait(false);
            return 2;
        }

        try
        {
            await WriteStatusAsync("READY").ConfigureAwait(false);
            using var startupTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(StartupTimeoutSeconds));
            var startCommand = await Console.In.ReadLineAsync(startupTimeout.Token).ConfigureAwait(false);
            if (!string.Equals(startCommand, "START", StringComparison.Ordinal))
                return 2;

            SherpaAudioGraphWakeWordListener.VerifyLockedModelFiles(args[2]);
            using var spotter = await Task.Run(
                () => SherpaKeywordSpotter.CreateWithKeywords(args[2], SherpaKeywordSpotter.XiaoKWakeWordKeywords),
                startupTimeout.Token).ConfigureAwait(false);
            await WriteStatusAsync("STARTED").ConfigureAwait(false);

            var input = Console.OpenStandardInput();
            var header = new byte[sizeof(int)];
            while (true)
            {
                var headerBytes = await ReadChunkAsync(input, header, CancellationToken.None).ConfigureAwait(false);
                if (headerBytes == 0) return 0;
                if (headerBytes != header.Length)
                {
                    await WriteStatusAsync("ERROR:INVALID_FRAME").ConfigureAwait(false);
                    return 3;
                }

                if (!SherpaKwsWorkerProtocol.TryReadSampleCount(header, out var count))
                {
                    await WriteStatusAsync("ERROR:INVALID_FRAME").ConfigureAwait(false);
                    return 3;
                }

                var payload = new byte[checked(count * sizeof(float))];
                try
                {
                    if (!await ReadExactlyAsync(input, payload).ConfigureAwait(false))
                    {
                        await WriteStatusAsync("ERROR:TRUNCATED_FRAME").ConfigureAwait(false);
                        return 3;
                    }

                    var samples = MemoryMarshal.Cast<byte, float>(payload.AsSpan());
                    foreach (var label in spotter.AcceptWaveform(samples))
                    {
                        if (!SherpaKwsWorkerProtocol.IsAllowedKeywordLabel(label)) continue;
                        await WriteStatusAsync($"KEYWORD:{label}").ConfigureAwait(false);
                        return 0;
                    }
                }
                finally { CryptographicOperations.ZeroMemory(payload); }
            }
        }
        catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException)
        {
            await WriteStatusAsync("ERROR:KWS_WORKER_FAILED").ConfigureAwait(false);
            return 4;
        }
    }

    private static async Task<int> ReadChunkAsync(Stream stream, Memory<byte> buffer, CancellationToken cancellationToken)
    {
        var total = 0;
        while (total < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer[total..], cancellationToken).ConfigureAwait(false);
            if (read == 0) break;
            total += read;
        }
        return total;
    }

    private static async Task<bool> ReadExactlyAsync(Stream stream, Memory<byte> buffer)
    {
        var total = 0;
        while (total < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer[total..], CancellationToken.None).ConfigureAwait(false);
            if (read == 0) return false;
            total += read;
        }
        return true;
    }

    private static async Task WriteStatusAsync(string value)
    {
        if (value.Length > MaximumProtocolLineCharacters) value = "ERROR:PROTOCOL";
        await Console.Out.WriteLineAsync(value).ConfigureAwait(false);
        await Console.Out.FlushAsync().ConfigureAwait(false);
    }
}
