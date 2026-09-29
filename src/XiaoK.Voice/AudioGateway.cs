namespace XiaoK.Voice;

public enum VoiceAvailability { NotConfigured, Ready, Listening, Stopped, DeviceUnavailable, PermissionDenied }

/// <summary>Voice boundary only; CPU wake-word and ASR runtime are not bundled pending P0 comparison.</summary>
public sealed class AudioGateway
{
    private int _capturing;
    public VoiceAvailability Availability => Volatile.Read(ref _capturing) == 1 ? VoiceAvailability.Listening : VoiceAvailability.NotConfigured;
    public void StopImmediately() => Interlocked.Exchange(ref _capturing, 0);
    public bool IsCapturing => Volatile.Read(ref _capturing) == 1;
}
