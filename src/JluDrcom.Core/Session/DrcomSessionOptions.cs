using JluDrcom.Core.Configuration;
using JluDrcom.Core.Protocol;

namespace JluDrcom.Core.Session;

public sealed record DrcomSessionOptions(
    string Username,
    string Password,
    string AuthServer,
    int AuthPort,
    string? InterfaceName,
    string? Mac,
    LoginPacketMode PacketMode,
    TimeSpan HeartbeatInterval,
    TimeSpan ExchangeTimeout,
    TimeSpan InitialBackoff,
    int MaxChallengeAttempts = 10,
    int HeartbeatFailureThreshold = 3)
{
    public static DrcomSessionOptions FromSettings(DrcomSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return new DrcomSessionOptions(
            settings.File.Username,
            settings.Password,
            settings.File.AuthServer,
            settings.File.AuthPort,
            settings.File.InterfaceName,
            settings.File.Mac,
            settings.File.PacketMode,
            TimeSpan.FromSeconds(settings.File.HeartbeatSeconds),
            TimeSpan.FromSeconds(settings.File.TimeoutSeconds),
            TimeSpan.FromSeconds(1));
    }
}
