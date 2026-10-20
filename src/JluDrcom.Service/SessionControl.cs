namespace JluDrcom.Service;

/// <summary>
/// User intent for the current service process. A manual disconnect pauses the
/// session; it is not persisted, so a service restart goes back online.
/// </summary>
public sealed class SessionControl
{
    private int _paused;

    public bool IsPaused => Volatile.Read(ref _paused) == 1;

    public void Disconnect() => Interlocked.Exchange(ref _paused, 1);

    public void Reconnect() => Interlocked.Exchange(ref _paused, 0);
}
