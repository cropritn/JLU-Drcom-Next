namespace JluDrcom.SetupGui.Wired;

/// <summary>One wired Ethernet adapter and its current IPv4 configuration.</summary>
public sealed record WiredAdapter(
    string Name,
    string Description,
    string MacAddress,
    int InterfaceIndex,
    bool IsUp,
    bool DhcpEnabled,
    string IpAddress,
    string SubnetMask,
    string Gateway,
    string Dns1,
    string Dns2)
{
    public string DisplayName => $"{Name} — {Description}";
}
