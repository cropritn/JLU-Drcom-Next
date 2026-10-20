namespace JluDrcom.Core.Protocol;

/// <summary>Everything needed to build one login request.</summary>
public sealed record DrcomLoginRequest(
    string Username,
    string Password,
    ReadOnlyMemory<byte> Challenge,
    ReadOnlyMemory<byte> AssignedIp,
    ReadOnlyMemory<byte> MacAddress,
    string Hostname,
    LoginPacketMode PacketMode);
