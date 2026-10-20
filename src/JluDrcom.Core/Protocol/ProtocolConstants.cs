namespace JluDrcom.Core.Protocol;

/// <summary>
/// Constants for the JLU Dr.COM variant. The byte layout was verified against the
/// 2018 service's own login packet (MD5A/MD5B/MD5C/ROR all matched) and the
/// login/heartbeat code path that currently authenticates on JLU.
/// </summary>
public static class ProtocolConstants
{
    public const string DefaultAuthServer = "10.100.61.3";
    public const int DefaultAuthPort = 61440;

    public const int MaxUsernameLength = 36;
    public const int MaxHostnameLength = 32;
    public const int MaxPasswordLength = 16;

    public const int StartRequestLength = 20;
    public const int LoginHeaderLength = 4;
    public const int Md5Length = 16;
    public const int MacLength = 6;
    public const int IpLength = 4;
    public const int AuthInformationLength = 16;

    public const int LoginPrefixLength = 97;
    public const int LoginFixedLengthWithoutPassword = 316;

    public const string ServiceName = "JluDrcom";
    public const string DisplayName = "JLU Drcom Client";
    public const string StatusPipeName = "JluDrcom.Status";
    public const string ProductName = "JluDrcom";

    /// <summary>
    /// Allows a development/test process to use a separate pipe instead of the
    /// installed service's pipe. Production always uses the constant.
    /// </summary>
    public static string ResolveStatusPipeName()
    {
        var overrideName = Environment.GetEnvironmentVariable("JLUDRCOM_PIPE");
        return string.IsNullOrWhiteSpace(overrideName) ? StatusPipeName : overrideName.Trim();
    }

    public const string LegacyRegistryPath =
        @"SYSTEM\CurrentControlSet\Hardware Profiles\Current\Software\JLUDrcomService";

    /// <summary>
    /// Fixed OS/client fingerprint block used by the existing JLU client. It is
    /// protocol data, not a secret. Changing it may make the server reject login.
    /// </summary>
    public static ReadOnlyMemory<byte> OsFingerprint { get; } = Convert.FromHexString(
        "0A0A0A0A00000000080808080000000000000000940000000600000002000000F0230000020000004472434F4D00CF076A0000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000031633231306339393538356664323261643033643335633935363931316165656331656234343962000000000000000000000000000000000000000000000000");
}
