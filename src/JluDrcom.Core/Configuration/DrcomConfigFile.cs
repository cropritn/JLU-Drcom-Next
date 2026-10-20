using JluDrcom.Core.Protocol;

namespace JluDrcom.Core.Configuration;

/// <summary>On-disk configuration. The password is always DPAPI-protected.</summary>
public sealed class DrcomConfigFile
{
    public string Username { get; set; } = string.Empty;

    public string PasswordProtected { get; set; } = string.Empty;

    public string AuthServer { get; set; } = ProtocolConstants.DefaultAuthServer;

    public int AuthPort { get; set; } = ProtocolConstants.DefaultAuthPort;

    /// <summary>Optional override, e.g. "B0-25-AA-5E-0B-2B". Null means derive from the route.</summary>
    public string? Mac { get; set; }

    /// <summary>Optional adapter name, e.g. "以太网 2". Null means derive from the route.</summary>
    public string? InterfaceName { get; set; }

    public LoginPacketMode PacketMode { get; set; } = LoginPacketMode.Complete;

    public int HeartbeatSeconds { get; set; } = 20;

    public int TimeoutSeconds { get; set; } = 5;

    public string LogLevel { get; set; } = "Information";

    /// <summary>
    /// Optional wired NIC settings. The service ignores this section; it is
    /// read/applied by the elevated Setup-Gui process.
    /// </summary>
    public WiredOptions? Wired { get; set; }

    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>IP/gateway/DNS values for a wired NIC, applied only from the GUI.</summary>
public sealed class WiredOptions
{
    public string? InterfaceName { get; set; }

    public string? Mac { get; set; }

    public string? IpAddress { get; set; }

    public string? SubnetMask { get; set; }

    public string? Gateway { get; set; }

    public string? Dns1 { get; set; }

    public string? Dns2 { get; set; }

    /// <summary>True when the adapter is currently configured by DHCP.</summary>
    public bool? DhcpEnabled { get; set; }
}

/// <summary>Validated configuration with the clear-text password only in memory.</summary>
public sealed record DrcomSettings(DrcomConfigFile File, string Password);
