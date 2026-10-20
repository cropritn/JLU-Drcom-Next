using System.Security.Cryptography;

namespace JluDrcom.Core.Protocol;

/// <summary>
/// Pure protocol crypto. The formulas match the JLU client that is currently
/// authenticating; the packet builder and tests exercise each one independently.
/// </summary>
public static class DrcomCrypto
{
    public static byte[] Md5A(ReadOnlySpan<byte> challenge, ReadOnlySpan<byte> password)
    {
        if (challenge.Length != ProtocolConstants.IpLength)
        {
            throw new ArgumentException("Challenge must be 4 bytes.", nameof(challenge));
        }

        if (password.IsEmpty)
        {
            throw new ArgumentException("Password must not be empty.", nameof(password));
        }

        Span<byte> input = stackalloc byte[2 + 4 + password.Length];
        input[0] = (byte)DrcomCode.LoginRequest;
        input[1] = 0x01;
        challenge.CopyTo(input[2..6]);
        password.CopyTo(input[6..]);
        return MD5.HashData(input);
    }

    public static byte[] Md5B(ReadOnlySpan<byte> challenge, ReadOnlySpan<byte> password)
    {
        if (challenge.Length != ProtocolConstants.IpLength)
        {
            throw new ArgumentException("Challenge must be 4 bytes.", nameof(challenge));
        }

        Span<byte> input = stackalloc byte[1 + password.Length + 4 + 4];
        input[0] = 0x01;
        password.CopyTo(input[1..]);
        challenge.CopyTo(input[(1 + password.Length)..]);
        // The final four bytes stay zero; this is part of the protocol.
        return MD5.HashData(input);
    }

    /// <summary>
    /// MD5C is MD5(first 97 bytes of the login packet + 14 00 07 0B), truncated to 8 bytes.
    /// </summary>
    public static byte[] Md5C(ReadOnlySpan<byte> loginPrefix)
    {
        if (loginPrefix.Length != ProtocolConstants.LoginPrefixLength)
        {
            throw new ArgumentException(
                $"Login prefix must be {ProtocolConstants.LoginPrefixLength} bytes, got {loginPrefix.Length}.",
                nameof(loginPrefix));
        }

        Span<byte> input = stackalloc byte[loginPrefix.Length + 4];
        loginPrefix.CopyTo(input);
        input[^4] = 0x14;
        input[^3] = 0x00;
        input[^2] = 0x07;
        input[^1] = 0x0B;

        Span<byte> hash = stackalloc byte[16];
        MD5.HashData(input, hash);
        return hash[..8].ToArray();
    }

    /// <summary>ROL(x ^ md5a[i], 3), where x is one password byte.</summary>
    public static byte[] Ror(ReadOnlySpan<byte> md5a, ReadOnlySpan<byte> password)
    {
        if (md5a.Length < password.Length)
        {
            throw new ArgumentException("MD5A is shorter than the password.", nameof(md5a));
        }

        if (password.Length > ProtocolConstants.MaxPasswordLength)
        {
            throw new ArgumentException(
                $"Password is longer than {ProtocolConstants.MaxPasswordLength} bytes.",
                nameof(password));
        }

        var result = new byte[password.Length];
        for (var i = 0; i < password.Length; i++)
        {
            var x = md5a[i] ^ password[i];
            result[i] = (byte)(((x << 3) & 0xFF) | (x >> 5));
        }

        return result;
    }

    /// <summary>
    /// XOR checksum over 4-byte big-endian words. Unlike the old implementation,
    /// a trailing partial word is zero-padded instead of reading past the array.
    /// </summary>
    public static byte[] CheckSum(ReadOnlySpan<byte> packet)
    {
        var paddedLength = (packet.Length + 3) & ~3;
        ulong sum = 1234;

        for (var i = 0; i < paddedLength; i += 4)
        {
            ulong check = 0;
            for (var j = 0; j < 4; j++)
            {
                var index = i + j;
                var value = index < packet.Length ? packet[index] : (byte)0;
                check = (check << 8) | value;
            }

            sum ^= check;
        }

        var value32 = (uint)((1968UL * sum) & 0xFFFFFFFFUL);
        return BitConverter.GetBytes(value32);
    }

    /// <summary>
    /// The correct 32-bit CRC: XOR the 16-bit words, multiply by 711, and emit
    /// the low 32 bits in big-endian order.
    /// </summary>
    public static byte[] Crc(ReadOnlySpan<byte> packet)
    {
        var (lo, hi) = XorWords(packet);
        var sum = (short)(lo | (hi << 8));
        var b = sum * 711;
        return
        [
            (byte)(b >> 24),
            (byte)(b >> 16),
            (byte)(b >> 8),
            (byte)b,
        ];
    }

    /// <summary>
    /// Byte-for-byte compatibility with the 2018 implementation. It took the
    /// high four bytes of a sign-extended <see cref="long"/>, so the result is
    /// 00 00 00 00 or FF FF FF FF. Kept only for <c>LoginPacketMode.Legacy</c>.
    /// </summary>
    public static byte[] CrcLegacy(ReadOnlySpan<byte> packet)
    {
        var (lo, hi) = XorWords(packet);
        var sum = (short)(lo | (hi << 8));
        var b = sum * 711L;
        var bytes = BitConverter.GetBytes(b);
        return [bytes[7], bytes[6], bytes[5], bytes[4]];
    }

    private static (byte Lo, byte Hi) XorWords(ReadOnlySpan<byte> packet)
    {
        byte lo = 0;
        byte hi = 0;

        for (var i = 0; i + 1 < packet.Length; i += 2)
        {
            lo ^= packet[i + 1];
            hi ^= packet[i];
        }

        return (lo, hi);
    }
}
