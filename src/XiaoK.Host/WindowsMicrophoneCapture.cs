using System.IO;
using Windows.Media.Capture;
using Windows.Media.MediaProperties;
using Windows.Storage.Streams;
using System.Security.Cryptography;

namespace XiaoK.Host;

/// <summary>Explicit foreground microphone capture into a short, bounded in-memory PCM WAV.</summary>
internal sealed class WindowsMicrophoneCapture : IAsyncDisposable
{
    public const int SampleRate = 16_000;
    public const int MaximumAudioBytes = 12 * 1024 * 1024;
    public const int MaximumDurationSeconds = 60;

    private readonly SemaphoreSlim _lifecycleGate = new(1, 1);
    private MediaCapture? _capture;
    private InMemoryRandomAccessStream? _stream;
    private CancellationTokenSource? _startCancellation;
    private CancellationTokenSource? _durationCancellation;
    private int _isStarting;
    private int _isCapturing;
    private int _disposed;

    public bool IsCapturing => Volatile.Read(ref _isCapturing) == 1;
    public bool IsActive => IsCapturing || Volatile.Read(ref _isStarting) == 1;
    public event Action<byte[]>? MaximumDurationReached;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(true);
        try
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            if (_capture is not null || IsActive) throw new InvalidOperationException("麦克风已在采集。");

            using var startCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _startCancellation = startCancellation;
            Volatile.Write(ref _isStarting, 1);
            var capture = new MediaCapture();
            var stream = new InMemoryRandomAccessStream();
            var recordingStarted = false;
            try
            {
                var settings = new MediaCaptureInitializationSettings
                {
                    StreamingCaptureMode = StreamingCaptureMode.Audio
                };
                await capture.InitializeAsync(settings);
                startCancellation.Token.ThrowIfCancellationRequested();

                var profile = MediaEncodingProfile.CreateWav(AudioEncodingQuality.Medium);
                profile.Audio = AudioEncodingProperties.CreatePcm(SampleRate, 1, 16);
                await capture.StartRecordToStreamAsync(profile, stream);
                recordingStarted = true;
                startCancellation.Token.ThrowIfCancellationRequested();

                _capture = capture;
                _stream = stream;
                capture = null!;
                stream = null!;
                Volatile.Write(ref _isCapturing, 1);
                _durationCancellation = new CancellationTokenSource();
                _ = StopAtMaximumDurationAsync(_durationCancellation.Token);
            }
            catch
            {
                if (recordingStarted)
                {
                    try { await capture.StopRecordAsync(); }
                    catch (Exception) { }
                }
                throw;
            }
            finally
            {
                capture?.Dispose();
                stream?.Dispose();
                Volatile.Write(ref _isStarting, 0);
                _startCancellation = null;
            }
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    public Task<byte[]> StopAndReadAsync() => StopCoreAsync(discard: false);

    /// <summary>Signals stop immediately; any partial recording is discarded and never sent to ASR.</summary>
    public bool StopImmediatelyAndDiscard()
    {
        var wasActive = SignalImmediateStop();
        if (wasActive) _ = StopAndDiscardQuietlyAsync();
        return wasActive;
    }

    /// <summary>Signals stop synchronously, then waits until the recorder and stream are released.</summary>
    public async Task<bool> StopImmediatelyAndDiscardAsync()
    {
        var wasActive = SignalImmediateStop();
        // Join an in-flight StartAsync/StopCoreAsync even when IsActive has already changed.
        await StopCoreAsync(discard: true).ConfigureAwait(false);
        return wasActive;
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        try { Volatile.Read(ref _startCancellation)?.Cancel(); }
        catch (ObjectDisposedException) { }
        await StopCoreAsync(discard: true).ConfigureAwait(false);
    }

    private async Task StopAndDiscardQuietlyAsync()
    {
        try { await StopCoreAsync(discard: true).ConfigureAwait(false); }
        catch (Exception) { }
    }

    private bool SignalImmediateStop()
    {
        var wasActive = IsActive;
        try { Volatile.Read(ref _startCancellation)?.Cancel(); }
        catch (ObjectDisposedException) { }
        return wasActive;
    }

    private async Task StopAtMaximumDurationAsync(CancellationToken cancellationToken)
    {
        try { await Task.Delay(TimeSpan.FromSeconds(MaximumDurationSeconds), cancellationToken).ConfigureAwait(false); }
        catch (OperationCanceledException) { return; }
        if (!IsCapturing) return;

        byte[] wav;
        try { wav = await StopCoreAsync(discard: false).ConfigureAwait(false); }
        catch (Exception) { return; }
        if (wav.Length == 0) return;
        var handler = MaximumDurationReached;
        if (handler is null)
        {
            CryptographicOperations.ZeroMemory(wav);
            return;
        }
        try { handler(wav); }
        catch (Exception) { CryptographicOperations.ZeroMemory(wav); }
    }

    private async Task<byte[]> StopCoreAsync(bool discard)
    {
        await _lifecycleGate.WaitAsync().ConfigureAwait(false);
        try
        {
            var capture = _capture;
            var stream = _stream;
            _capture = null;
            _stream = null;
            var durationCancellation = _durationCancellation;
            _durationCancellation = null;
            if (durationCancellation is not null)
            {
                try { durationCancellation.Cancel(); }
                catch (ObjectDisposedException) { }
                durationCancellation.Dispose();
            }
            if (capture is null || stream is null)
            {
                Volatile.Write(ref _isCapturing, 0);
                return [];
            }

            try
            {
                try { await capture.StopRecordAsync(); }
                catch (Exception) when (discard) { return []; }
                if (discard) return [];

                await stream.FlushAsync();
                if (stream.Size is < 44 or > MaximumAudioBytes)
                    throw new InvalidDataException("录音长度无效或超过12 MiB上限。");
                stream.Seek(0);
                using var reader = new DataReader(stream.GetInputStreamAt(0));
                var loaded = await reader.LoadAsync((uint)stream.Size);
                if (loaded is < 44 or > MaximumAudioBytes)
                    throw new InvalidDataException("没有得到有效的录音数据。");
                var wav = new byte[checked((int)loaded)];
                reader.ReadBytes(wav);
                if (!wav.AsSpan(0, 4).SequenceEqual("RIFF"u8)
                    || !wav.AsSpan(8, 4).SequenceEqual("WAVE"u8))
                {
                    CryptographicOperations.ZeroMemory(wav);
                    throw new InvalidDataException("录音设备没有输出有效 WAV 数据。");
                }
                return wav;
            }
            finally
            {
                try { capture.Dispose(); }
                finally
                {
                    try { stream.Dispose(); }
                    finally { Volatile.Write(ref _isCapturing, 0); }
                }
            }
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }
}
