using System.Text;

namespace JluDrcom.Core.Protocol;

/// <summary>Builds the JLU Dr.COM packets without any I/O or global state.</summary>
public static class DrcomPacketBuilder
{
    public static byte[] BuildStartRequest(int retry, IRandomSource? random = null)
    {
        random ??= CryptoRandomSource.Instance;

        var packet = new byte[ProtocolConstants.StartRequestLength];
        packet[0] = (byte)DrcomCode.StartRequest;
        packet[1] = unchecked((byte)(0x02 + retry));
        random.Fill(packet.AsSpan(2, 2));
        packet[4] = 0x6A;
        packet[5] = 0x00;
        return packet;
    }

    public static byte[] BuildLoginRequest(DrcomLoginRequest request, IRandomSource? random = null)
    {
        random ??= CryptoRandomSource.Instance;

        var username = ToAscii(request.Username, nameof(request.Username), ProtocolConstants.MaxUsernameLength, allowEmpty: false);
        var password = ToAscii(request.Password, nameof(request.Password), ProtocolConstants.MaxPasswordLength, allowEmpty: false);
        var hostname = ToAscii(request.Hostname, nameof(request.Hostname), ProtocolConstants.MaxHostnameLength, allowEmpty: true);
        var challenge = request.Challenge.Span;
        var assignedIp = request.AssignedIp.Span;
        var mac = request.MacAddress.Span;

        if (challenge.Length != 4)
        {
            throw new ArgumentException("Challenge must be 4 bytes.", nameof(request));
        }

        if (assignedIp.Length != 4)
        {
            throw new ArgumentException("Assigned IP must be 4 bytes.", nameof(request));
        }

        if (mac.Length != 6)
        {
            throw new ArgumentException("MAC address must be 6 bytes.", nameof(request));
        }

        if (password.Length > ProtocolConstants.MaxPasswordLength)
        {
            throw new ArgumentException(
                $"Password must be at most {ProtocolConstants.MaxPasswordLength} bytes.",
                nameof(request));
        }

        var md5a = DrcomCrypto.Md5A(challenge, password);
        var md5b = DrcomCrypto.Md5B(challenge, password);
        var macXor = new byte[6];
        for (var i = 0; i < 6; i++)
        {
            macXor[i] = (byte)(mac[i] ^ md5a[i]);
        }

        var packet = new List<byte>(360);

        // 4-byte header: 03 01 00 (username length + 20).
        packet.Add((byte)DrcomCode.LoginRequest);
        packet.Add(0x01);
        packet.Add(0x00);
        packet.Add((byte)(username.Length + 20));

        packet.AddRange(md5a);
        packet.AddRange(PadTo(username, 36));
        packet.Add(0x20);
        packet.Add(0x04);
        packet.AddRange(macXor);
        packet.AddRange(md5b);
        packet.Add(0x01);
        packet.AddRange(assignedIp);
        packet.AddRange(new byte[12]);

        if (packet.Count != ProtocolConstants.LoginPrefixLength)
        {
            throw new InvalidOperationException(
                $"Login prefix length mismatch: {packet.Count}, expected {ProtocolConstants.LoginPrefixLength}.");
        }

        packet.AddRange(DrcomCrypto.Md5C(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(packet)));

        // IPDog: 01 + 4 zero bytes.
        packet.Add(0x01);
        packet.AddRange(new byte[4]);

        packet.AddRange(PadTo(hostname, 32));
        packet.AddRange(ProtocolConstants.OsFingerprint.Span);

        // ClientVerInfoAndInternetMode.
        packet.Add(0x6A);
        packet.Add(0x00);
        packet.Add(0x00);

        packet.Add((byte)password.Length);
        packet.AddRange(DrcomCrypto.Ror(md5a, password));

        // AuthExtDataOption.
        packet.Add(0x02);
        packet.Add(0x0C);

        if (request.PacketMode == LoginPacketMode.Complete)
        {
            var checksum = DrcomCrypto.CheckSum(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(packet));
            packet.AddRange(checksum);

            // AutoLogout(?), broadcast mode(?).
            packet.Add(0x00);
            packet.Add(0x00);

            packet.AddRange(mac);

            Span<byte> tail = stackalloc byte[2];
            random.Fill(tail);
            packet.Add(tail[0]);
            packet.Add(tail[1]);
        }

        return packet.ToArray();
    }

    public static byte[] BuildAlive(
        ReadOnlySpan<byte> md5a,
        ReadOnlySpan<byte> authInformation,
        IRandomSource? random = null)
    {
        random ??= CryptoRandomSource.Instance;

        if (md5a.Length != ProtocolConstants.Md5Length)
        {
            throw new ArgumentException("MD5A must be 16 bytes.", nameof(md5a));
        }

        if (authInformation.Length != ProtocolConstants.AuthInformationLength)
        {
            throw new ArgumentException("Auth information must be 16 bytes.", nameof(authInformation));
        }

        var packet = new byte[38];
        packet[0] = (byte)DrcomCode.AliveClient;
        md5a.CopyTo(packet.AsSpan(1, 16));
        // Bytes 17..19 stay zero.
        authInformation.CopyTo(packet.AsSpan(20, 16));
        random.Fill(packet.AsSpan(36, 2));
        return packet;
    }

    public static byte[] BuildKeepAlive(
        byte type,
        bool fake,
        byte count,
        ReadOnlySpan<byte> tail,
        ReadOnlySpan<byte> assignedIp,
        bool legacyCrc,
        IRandomSource? random = null)
    {
        random ??= CryptoRandomSource.Instance;

        if (type is not (1 or 3))
        {
            throw new ArgumentOutOfRangeException(nameof(type), "Keep-alive type must be 1 or 3.");
        }

        if (tail.Length != 4)
        {
            throw new ArgumentException("Tail must be 4 bytes.", nameof(tail));
        }

        if (assignedIp.Length != 4)
        {
            throw new ArgumentException("Assigned IP must be 4 bytes.", nameof(assignedIp));
        }

        var packet = new List<byte>(40);
        packet.Add((byte)DrcomCode.KeepAlive);
        packet.Add(count);
        packet.Add(0x28);
        packet.Add(0x00);
        packet.Add(0x0B);
        packet.Add(type);
        packet.Add((byte)(fake ? 0x0F : 0xDC));
        packet.Add((byte)(fake ? 0x27 : 0x02));

        Span<byte> randomBytes = stackalloc byte[2];
        random.Fill(randomBytes);
        packet.Add(randomBytes[0]);
        packet.Add(randomBytes[1]);
        packet.AddRange(new byte[6]);
        packet.AddRange(tail.ToArray());
        packet.AddRange(new byte[4]);

        if (type == 3)
        {
            var crcInput = new byte[packet.Count + 4];
            packet.CopyTo(crcInput);
            assignedIp.CopyTo(crcInput.AsSpan(packet.Count));
            packet.AddRange(legacyCrc ? DrcomCrypto.CrcLegacy(crcInput) : DrcomCrypto.Crc(crcInput));
            packet.AddRange(assignedIp.ToArray());
            packet.AddRange(new byte[8]);
        }
        else
        {
            packet.AddRange(new byte[16]);
        }

        return packet.ToArray();
    }

    private static byte[] ToAscii(string value, string parameterName, int maxLength, bool allowEmpty)
    {
        value ??= string.Empty;

        if (value.Any(character => character > 0x7F))
        {
            throw new ArgumentException("Only ASCII characters are supported.", parameterName);
        }

        var bytes = Encoding.ASCII.GetBytes(value);

        if (!allowEmpty && bytes.Length == 0)
        {
            throw new ArgumentException("Value must not be empty.", parameterName);
        }

        if (bytes.Length > maxLength)
        {
            throw new ArgumentException(
                $"Value is {bytes.Length} bytes; the protocol allows at most {maxLength}.",
                parameterName);
        }

        return bytes;
    }

    private static byte[] PadTo(ReadOnlySpan<byte> value, int length)
    {
        var field = new byte[length];
        value.CopyTo(field);
        return field;
    }
}
