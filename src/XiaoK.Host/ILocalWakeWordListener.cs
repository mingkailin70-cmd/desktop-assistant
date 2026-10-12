namespace XiaoK.Host;

internal interface ILocalWakeWordListener : IAsyncDisposable
{
    string Status { get; }
    bool IsListening { get; }
    event Action? WakeWordDetected;
    event Action<string>? StatusChanged;
    Task<string> SetEnabledAsync(bool enabled, CancellationToken cancellationToken = default);
    Task PauseForDictationAsync(CancellationToken cancellationToken = default);
    Task ResumeAfterDictationAsync(CancellationToken cancellationToken = default);
}
