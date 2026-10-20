using System.Net;
using System.Text;

namespace JluDrcom.Core.Protocol;

public sealed record DrcomStartResponse(byte[] Challenge, IPAddress AssignedIp);

public sealed record DrcomLoginResponse(byte[] AuthInformation, string? ServerMessage);

public sealed record DrcomKeepAliveResponse(byte Code, byte[] Tail);

/// <summary>Defensive packet parsing: every offset is length-checked.</summary>
public static class DrcomPacketParser
{
    public static DrcomStartResponse ParseStartResponse(ReadOnlySpan<byte> packet)
    {
        EnsureLength(packet, 24, "start response");

        if (packet[0] != (byte)DrcomCode.StartResponse)
        {
            throw new DrcomProtocolException(
                packet[0],
                $"Unexpected start response code 0x{packet[0]:X2}.");
        }

        var challenge = packet.Slice(4, 4).ToArray();
        var assignedIp = new IPAddress(packet.Slice(20, 4));
        return new DrcomStartResponse(challenge, assignedIp);
    }

    public static DrcomLoginResponse ParseLoginResponse(ReadOnlySpan<byte> packet)
    {
        EnsureLength(packet, 4, "login response");
        var code = packet[0];

        if (code == (byte)DrcomCode.LoginFailure || code == (byte)DrcomCode.Message)
        {
            throw new DrcomAuthException(code, DecodeMessage(packet));
        }

        if (code != (byte)DrcomCode.LoginSuccess)
        {
            throw new DrcomProtocolException(code, $"Unexpected login response code 0x{code:X2}.");
        }

        EnsureLength(packet, 39, "login success response");
        var authInformation = packet.Slice(23, ProtocolConstants.AuthInformationLength).ToArray();
        return new DrcomLoginResponse(authInformation, DecodeMessage(packet));
    }

    public static DrcomKeepAliveResponse ParseKeepAliveResponse(ReadOnlySpan<byte> packet)
    {
        EnsureLength(packet, 20, "keep-alive response");

        // The 2018 client did not validate the response code; it copied the tail
        // unconditionally. Keep the same tolerance: any >=20-byte response means
        // the server is reachable, and only a 0x07 response updates the tail.
        return new DrcomKeepAliveResponse(packet[0], packet.Slice(16, 4).ToArray());
    }

    public static string? DecodeMessage(ReadOnlySpan<byte> packet)
    {
        if (packet.Length <= 4)
        {
            return null;
        }

        var text = Encoding.ASCII
            .GetString(packet[4..])
            .TrimEnd('\0', '\r', '\n', ' ')
            .Trim();

        return string.IsNullOrWhiteSpace(text) ? null : text;
    }

    private static void EnsureLength(ReadOnlySpan<byte> packet, int minimum, string what)
    {
        if (packet.Length < minimum)
        {
            throw new DrcomProtocolException(
                $"Truncated {what}: got {packet.Length} bytes, need at least {minimum}.");
        }
    }
}
