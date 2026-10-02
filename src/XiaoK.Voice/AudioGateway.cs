namespace XiaoK.Voice;

public enum VoiceAvailability { NotConfigured, Ready, Listening, Stopped, DeviceUnavailable, PermissionDenied }

/// <summary>Microphone capture and CPU wake-word/VAD are not active; model worker integration is separate.</summary>
public sealed class AudioGateway
{
    private int _capturing;
    public VoiceAvailability Availability => Volatile.Read(ref _capturing) == 1 ? VoiceAvailability.Listening : VoiceAvailability.NotConfigured;
    public void StopImmediately() => Interlocked.Exchange(ref _capturing, 0);
    public bool IsCapturing => Volatile.Read(ref _capturing) == 1;
}
