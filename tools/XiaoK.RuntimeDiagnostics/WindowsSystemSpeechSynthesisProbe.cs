using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using XiaoK.Host;

internal static class WindowsSystemSpeechSynthesisProbe
{
    private const string SyntheticText = "你好，小K。";

    public static async Task<int> RunAsync()
    {
        if (!OperatingSystem.IsWindows())
        {
            Console.Error.WriteLine("Windows 系统语音诊断仅支持 Windows；没有生成音频。");
            return 2;
        }

        var clock = Stopwatch.StartNew();
        WindowsInstalledChineseSpeech? speech = null;
        byte[]? audio = null;
        try
        {
            speech = WindowsInstalledChineseSpeech.TryCreate(out var status);
            if (speech is null)
            {
                Console.WriteLine(JsonSerializer.Serialize(new
                {
                    diagnostic = "windows-installed-chinese-speech-in-memory",
                    available = false,
                    status,
                    audioSaved = false,
                    audioPlayed = false,
                    modelWeightsLoaded = false
                }));
                return 1;
            }

            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            audio = await speech.SynthesizeWavAsync(SyntheticText, deadline.Token);
            clock.Stop();
            var validWave = WindowsInstalledChineseSpeech.IsValidWave(audio);
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                diagnostic = "windows-installed-chinese-speech-in-memory",
                available = true,
                voiceName = speech.VoiceName,
                validBoundedWave = validWave,
                wavBytes = audio.Length,
                synthesisElapsedMs = Math.Round(clock.Elapsed.TotalMilliseconds, 1),
                audioSaved = false,
                audioPlayed = false,
                modelWeightsLoaded = false,
                transcriptSaved = false
            }));
            return validWave ? 0 : 1;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException
                                           and not StackOverflowException
                                           and not AccessViolationException)
        {
            clock.Stop();
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                diagnostic = "windows-installed-chinese-speech-in-memory",
                available = speech is not null,
                errorType = exception.GetType().Name,
                synthesisElapsedMs = Math.Round(clock.Elapsed.TotalMilliseconds, 1),
                audioSaved = false,
                audioPlayed = false,
                modelWeightsLoaded = false,
                transcriptSaved = false
            }));
            return 1;
        }
        finally
        {
            if (audio is not null)
                CryptographicOperations.ZeroMemory(audio);
        }
    }
}
