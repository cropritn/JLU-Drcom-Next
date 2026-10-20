namespace JluDrcom.Service;

/// <summary>Runtime facts that the status pipe exposes for diagnostics.</summary>
public sealed class ServiceRuntimeInfo
{
    public string LogDirectory { get; init; } = string.Empty;
}
