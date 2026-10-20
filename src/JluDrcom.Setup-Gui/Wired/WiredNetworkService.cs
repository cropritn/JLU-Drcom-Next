using System.Management;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace JluDrcom.SetupGui.Wired;

/// <summary>
/// Reads and applies IPv4 settings for wired Ethernet adapters only.
/// All methods require an elevated process (the Setup-Gui manifest requests it).
/// </summary>
public sealed class WiredNetworkService
{
    public IReadOnlyList<WiredAdapter> GetWiredAdapters()
    {
        var adapters = new List<WiredAdapter>();

        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces()
                     .Where(nic => nic.NetworkInterfaceType == NetworkInterfaceType.Ethernet)
                     .OrderByDescending(nic => nic.OperationalStatus == OperationalStatus.Up)
                     .ThenBy(nic => nic.Name, StringComparer.CurrentCulture))
        {
            adapters.Add(ReadAdapter(nic));
        }

        return adapters;
    }

    public WiredAdapter? FindPreferred(IEnumerable<WiredAdapter> adapters, string? interfaceName, string? mac)
    {
        var list = adapters.ToList();

        if (!string.IsNullOrWhiteSpace(mac))
        {
            var match = list.FirstOrDefault(adapter =>
                string.Equals(NormalizeMac(adapter.MacAddress), NormalizeMac(mac), StringComparison.OrdinalIgnoreCase));
            if (match is not null)
            {
                return match;
            }
        }

        if (!string.IsNullOrWhiteSpace(interfaceName))
        {
            var match = list.FirstOrDefault(adapter =>
                string.Equals(adapter.Name, interfaceName, StringComparison.OrdinalIgnoreCase));
            if (match is not null)
            {
                return match;
            }
        }

        return list.FirstOrDefault(adapter => adapter.IsUp && !string.IsNullOrWhiteSpace(adapter.IpAddress))
               ?? list.FirstOrDefault(adapter => adapter.IsUp)
               ?? list.FirstOrDefault();
    }

    public WiredApplyResult Apply(WiredAdapter adapter, WiredSettings settings)
    {
        if (!Validate(settings, out var error))
        {
            return new WiredApplyResult(false, error);
        }

        try
        {
            using var searcher = new ManagementObjectSearcher(
                $"SELECT * FROM Win32_NetworkAdapterConfiguration WHERE MACAddress = '{ToWmiMac(adapter.MacAddress)}'");
            using var results = searcher.Get();
            using var configuration = results.Cast<ManagementObject>().FirstOrDefault();

            if (configuration is null)
            {
                return new WiredApplyResult(false, $"找不到网卡 {adapter.DisplayName} 的 WMI 配置。");
            }

            var staticResult = InvokeEnableStatic(configuration, settings);
            if (!staticResult.Success)
            {
                return staticResult;
            }

            if (!string.IsNullOrWhiteSpace(settings.Gateway))
            {
                var gatewayResult = InvokeSetGateways(configuration, settings.Gateway);
                if (!gatewayResult.Success)
                {
                    return gatewayResult;
                }
            }

            var dns = new[] { settings.Dns1, settings.Dns2 }
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Select(value => value.Trim())
                .ToArray();

            if (dns.Length > 0)
            {
                var dnsResult = InvokeSetDns(configuration, dns);
                if (!dnsResult.Success)
                {
                    return dnsResult;
                }
            }

            return new WiredApplyResult(true, "有线设置已应用。");
        }
        catch (ManagementException exception)
        {
            return new WiredApplyResult(false, $"WMI 错误：{exception.Message}");
        }
        catch (Exception exception)
        {
            return new WiredApplyResult(false, $"应用失败：{exception.Message}");
        }
    }

    public WiredApplyResult EnableDhcp(WiredAdapter adapter)
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(
                $"SELECT * FROM Win32_NetworkAdapterConfiguration WHERE MACAddress = '{ToWmiMac(adapter.MacAddress)}'");
            using var results = searcher.Get();
            using var configuration = results.Cast<ManagementObject>().FirstOrDefault();

            if (configuration is null)
            {
                return new WiredApplyResult(false, $"找不到网卡 {adapter.DisplayName} 的 WMI 配置。");
            }

            using var enableDhcp = configuration.GetMethodParameters("EnableDHCP");
            using var enableResult = configuration.InvokeMethod("EnableDHCP", enableDhcp, null);
            var dhcpCode = Convert.ToUInt32(enableResult?["ReturnValue"] ?? 1U);
            if (dhcpCode is not (0 or 1))
            {
                return new WiredApplyResult(false, $"切换 DHCP 失败（代码 {dhcpCode}）。");
            }

            using var dnsParameters = configuration.GetMethodParameters("SetDNSServerSearchOrder");
            dnsParameters["DNSServerSearchOrder"] = null;
            configuration.InvokeMethod("SetDNSServerSearchOrder", dnsParameters, null);

            return new WiredApplyResult(true, "已切换为自动获取（DHCP）。");
        }
        catch (Exception exception)
        {
            return new WiredApplyResult(false, $"切换 DHCP 失败：{exception.Message}");
        }
    }

    private static WiredAdapter ReadAdapter(NetworkInterface nic)
    {
        var properties = nic.GetIPProperties();
        var ipv4 = properties.UnicastAddresses
            .FirstOrDefault(address => address.Address.AddressFamily == AddressFamily.InterNetwork);
        var gateway = properties.GatewayAddresses
            .Select(item => item.Address)
            .FirstOrDefault(address => address.AddressFamily == AddressFamily.InterNetwork);
        var dns = properties.DnsAddresses
            .Where(address => address.AddressFamily == AddressFamily.InterNetwork)
            .Select(address => address.ToString())
            .ToList();

        var mac = FormatMac(nic.GetPhysicalAddress().GetAddressBytes());
        var ipAddress = ipv4?.Address.ToString() ?? string.Empty;
        var subnetMask = ipv4 is null ? string.Empty : PrefixToSubnetMask(ipv4.PrefixLength);
        var gatewayText = gateway?.ToString() ?? string.Empty;
        var dns1 = dns.ElementAtOrDefault(0) ?? string.Empty;
        var dns2 = dns.ElementAtOrDefault(1) ?? string.Empty;
        var dhcpEnabled = false;

        try
        {
            using var searcher = new ManagementObjectSearcher(
                $"SELECT * FROM Win32_NetworkAdapterConfiguration WHERE MACAddress = '{ToWmiMac(mac)}'");
            using var results = searcher.Get();
            using var configuration = results.Cast<ManagementObject>().FirstOrDefault();
            if (configuration is not null)
            {
                dhcpEnabled = configuration["DHCPEnabled"] as bool? ?? false;

                if (configuration["IPAddress"] is string[] wmiAddresses)
                {
                    var wmiIpv4 = wmiAddresses.FirstOrDefault(value => value.Contains('.'));
                    if (!string.IsNullOrWhiteSpace(wmiIpv4))
                    {
                        ipAddress = wmiIpv4;
                    }
                }

                if (configuration["IPSubnet"] is string[] wmiMasks)
                {
                    var wmiMask = wmiMasks.FirstOrDefault(value => value.Contains('.'));
                    if (!string.IsNullOrWhiteSpace(wmiMask))
                    {
                        subnetMask = wmiMask;
                    }
                }

                if (configuration["DefaultIPGateway"] is string[] wmiGateways)
                {
                    var wmiGateway = wmiGateways.FirstOrDefault(value => value.Contains('.'));
                    if (!string.IsNullOrWhiteSpace(wmiGateway))
                    {
                        gatewayText = wmiGateway;
                    }
                }

                if (configuration["DNSServerSearchOrder"] is string[] wmiDns)
                {
                    var ipv4Dns = wmiDns.Where(value => value.Contains('.')).ToList();
                    dns1 = ipv4Dns.ElementAtOrDefault(0) ?? dns1;
                    dns2 = ipv4Dns.ElementAtOrDefault(1) ?? dns2;
                }
            }
        }
        catch (ManagementException)
        {
            // Fall back to the NetworkInterface values.
        }

        var interfaceIndex = 0;
        try
        {
            interfaceIndex = properties.GetIPv4Properties()?.Index ?? 0;
        }
        catch (NetworkInformationException)
        {
            // Adapter has no IPv4 properties yet (for example, cable unplugged).
        }

        return new WiredAdapter(
            nic.Name,
            nic.Description,
            mac,
            interfaceIndex,
            nic.OperationalStatus == OperationalStatus.Up,
            dhcpEnabled,
            ipAddress,
            subnetMask,
            gatewayText,
            dns1,
            dns2);
    }

    private static WiredApplyResult InvokeEnableStatic(ManagementObject configuration, WiredSettings settings)
    {
        using var parameters = configuration.GetMethodParameters("EnableStatic");
        parameters["IPAddress"] = new[] { settings.IpAddress.Trim() };
        parameters["SubnetMask"] = new[] { settings.SubnetMask.Trim() };

        using var result = configuration.InvokeMethod("EnableStatic", parameters, null);
        var code = Convert.ToUInt32(result?["ReturnValue"] ?? 1U);
        return code is 0 or 1
            ? new WiredApplyResult(true, "IP/掩码已应用。")
            : new WiredApplyResult(false, $"设置 IP/掩码失败（代码 {code}）。");
    }

    private static WiredApplyResult InvokeSetGateways(ManagementObject configuration, string gateway)
    {
        using var parameters = configuration.GetMethodParameters("SetGateways");
        parameters["DefaultIPGateway"] = new[] { gateway.Trim() };
        parameters["GatewayCostMetric"] = new ushort[] { 1 };

        using var result = configuration.InvokeMethod("SetGateways", parameters, null);
        var code = Convert.ToUInt32(result?["ReturnValue"] ?? 1U);
        return code is 0 or 1
            ? new WiredApplyResult(true, "网关已应用。")
            : new WiredApplyResult(false, $"设置网关失败（代码 {code}）。");
    }

    private static WiredApplyResult InvokeSetDns(ManagementObject configuration, string[] dns)
    {
        using var parameters = configuration.GetMethodParameters("SetDNSServerSearchOrder");
        parameters["DNSServerSearchOrder"] = dns;

        using var result = configuration.InvokeMethod("SetDNSServerSearchOrder", parameters, null);
        var code = Convert.ToUInt32(result?["ReturnValue"] ?? 1U);
        return code is 0 or 1
            ? new WiredApplyResult(true, "DNS 已应用。")
            : new WiredApplyResult(false, $"设置 DNS 失败（代码 {code}）。");
    }

    public static bool Validate(WiredSettings settings, out string error)
    {
        if (!TryParseIpv4(settings.IpAddress, out var ip))
        {
            error = "IP 地址无效。";
            return false;
        }

        if (!TryParseIpv4(settings.SubnetMask, out var mask) || !IsContiguousMask(mask))
        {
            error = "子网掩码无效（示例：255.255.255.0）。";
            return false;
        }

        if (!string.IsNullOrWhiteSpace(settings.Gateway))
        {
            if (!TryParseIpv4(settings.Gateway, out var gateway))
            {
                error = "网关地址无效。";
                return false;
            }

            if (!SameSubnet(ip, gateway, mask))
            {
                error = "网关与 IP 不在同一子网。";
                return false;
            }
        }

        if (!string.IsNullOrWhiteSpace(settings.Dns1) && !TryParseIpv4(settings.Dns1, out _))
        {
            error = "首选 DNS 无效。";
            return false;
        }

        if (!string.IsNullOrWhiteSpace(settings.Dns2) && !TryParseIpv4(settings.Dns2, out _))
        {
            error = "备用 DNS 无效。";
            return false;
        }

        error = string.Empty;
        return true;
    }

    public static string PrefixToSubnetMask(int prefixLength)
    {
        prefixLength = Math.Clamp(prefixLength, 0, 32);
        var mask = prefixLength == 0 ? 0U : uint.MaxValue << (32 - prefixLength);
        var bytes = BitConverter.GetBytes(mask);
        if (BitConverter.IsLittleEndian)
        {
            Array.Reverse(bytes);
        }

        return new IPAddress(bytes).ToString();
    }

    public static bool TryParseIpv4(string? text, out IPAddress address)
    {
        if (string.IsNullOrWhiteSpace(text)
            || !IPAddress.TryParse(text.Trim(), out var parsed)
            || parsed.AddressFamily != AddressFamily.InterNetwork)
        {
            address = IPAddress.None;
            return false;
        }

        address = parsed;
        return true;
    }

    private static bool IsContiguousMask(IPAddress mask)
    {
        var bytes = mask.GetAddressBytes();
        var value = 0U;
        foreach (var part in bytes)
        {
            value = (value << 8) | part;
        }

        var inverted = ~value;
        return (inverted & (inverted + 1)) == 0;
    }

    private static bool SameSubnet(IPAddress left, IPAddress right, IPAddress mask)
    {
        var leftBytes = left.GetAddressBytes();
        var rightBytes = right.GetAddressBytes();
        var maskBytes = mask.GetAddressBytes();

        for (var i = 0; i < 4; i++)
        {
            if ((leftBytes[i] & maskBytes[i]) != (rightBytes[i] & maskBytes[i]))
            {
                return false;
            }
        }

        return true;
    }

    private static string FormatMac(byte[] bytes) =>
        bytes.Length == 6 ? string.Join('-', bytes.Select(value => value.ToString("X2"))) : string.Empty;

    private static string NormalizeMac(string mac) => mac.Replace(':', '-').Trim().ToUpperInvariant();

    private static string ToWmiMac(string mac) => NormalizeMac(mac).Replace('-', ':');
}
