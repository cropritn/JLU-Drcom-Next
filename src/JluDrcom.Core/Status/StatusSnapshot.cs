using JluDrcom.Core.Session;

namespace JluDrcom.Core.Status;

/// <summary>Everything the tray/settings app needs, with no secrets.</summary>
public sealed record StatusSnapshot
{
    public bool ServiceInstalled { get; init; }

    public bool ServiceRunning { get; init; }

    public string ServiceVersion { get; init; } = string.Empty;

    public string ConfigPath { get; init; } = string.Empty;

    public string LogDirectory { get; init; } = string.Empty;

    public string UsernameMasked { get; init; } = string.Empty;

    public string PacketMode { get; init; } = string.Empty;

    public string? ConfigError { get; init; }

    public DrcomStatus Session { get; init; } = new();

    public DateTimeOffset SampledAt { get; init; } = DateTimeOffset.UtcNow;
}

public sealed record StatusRequest(string Command);

public sealed record StatusResponse(bool Ok, string? Error, StatusSnapshot? Status)
{
    public static StatusResponse Success(StatusSnapshot status) => new(true, null, status);

    public static StatusResponse Failure(string error) => new(false, error, null);
}
