namespace JluDrcom.Core.Session;

public enum DrcomSessionState
{
    Stopped = 0,
    SelectingNetwork = 1,
    Challenging = 2,
    Authenticating = 3,
    Online = 4,
    Reconnecting = 5,
    AuthFailed = 6,
    ConfigurationError = 7,
    Disconnected = 8,
}
