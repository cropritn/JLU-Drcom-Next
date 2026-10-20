using System.Text.Json.Serialization;

namespace JluDrcom.Core.Session;

/// <summary>Serializable status snapshot shared by the service, pipe, and tray.</summary>
public sealed record DrcomStatus
{
    public DrcomSessionState State { get; init; } = DrcomSessionState.Stopped;

    public string? LocalIp { get; init; }

    public string? AdapterName { get; init; }

    public string? MacAddress { get; init; }

    public string? AssignedIp { get; init; }

    public DateTimeOffset? OnlineSince { get; init; }

    public string? LastError { get; init; }

    public int Attempt { get; init; }

    public long Heartbeats { get; init; }

    public DateTimeOffset UpdatedAt { get; init; } = DateTimeOffset.UtcNow;

    [JsonIgnore]
    public bool IsOnline => State == DrcomSessionState.Online;

    [JsonIgnore]
    public TimeSpan? OnlineDuration => OnlineSince is null ? null : UpdatedAt - OnlineSince;
}
