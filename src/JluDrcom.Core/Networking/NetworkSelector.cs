using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using JluDrcom.Core.Configuration;
using JluDrcom.Core.Protocol;

namespace JluDrcom.Core.Networking;

/// <summary>
/// Selects the NIC by asking the kernel which local address it would use to
/// reach the auth server. This is critical on machines with VPN/TAP adapters:
/// "first Ethernet adapter" is not the same thing as "the adapter with a route".
/// </summary>
public sealed class NetworkSelector : INetworkSelector
{
    public async Task<SelectedNetwork> SelectAsync(
        string authServer,
        int authPort,
        string? preferredInterface,
        string? preferredMac,
        CancellationToken cancellationToken)
    {
        var remoteAddress = await ResolveAsync(authServer, cancellationToken).ConfigureAwait(false);
        var preferred = string.IsNullOrWhiteSpace(preferredInterface) ? null : preferredInterface.Trim();

        NetworkInterface? nic;
        IPAddress localAddress;

        if (preferred is not null)
        {
            nic = FindByName(preferred);
            if (nic is null)
            {
                throw new DrcomConnectionException($"找不到网卡：{preferred}");
            }

            localAddress = GetIpv4Address(nic)
                ?? throw new DrcomConnectionException($"网卡 {nic.Name} 没有 IPv4 地址。");
        }
        else
        {
            localAddress = await GetRouteLocalAddressAsync(remoteAddress, authPort, cancellationToken)
                .ConfigureAwait(false);
            nic = FindByAddress(localAddress);
        }

        byte[] mac;
        if (!string.IsNullOrWhiteSpace(preferredMac))
        {
            if (!ConfigStore.TryParseMac(preferredMac, out mac))
            {
                throw new DrcomConnectionException("配置中的 MAC 地址无效。");
            }
        }
        else
        {
            if (nic is null)
            {
                throw new DrcomConnectionException(
                    $"无法根据本地地址 {localAddress} 找到网卡。请手动指定网卡或 MAC。");
            }

            mac = nic.GetPhysicalAddress().GetAddressBytes();
        }

        ValidateMac(mac);

        return new SelectedNetwork(
            localAddress,
            remoteAddress,
            mac,
            nic?.Name ?? "(手动 MAC)",
            nic?.Id ?? string.Empty,
            nic?.Description ?? string.Empty);
    }

    private static async Task<IPAddress> ResolveAsync(string host, CancellationToken cancellationToken)
    {
        if (IPAddress.TryParse(host, out var parsed))
        {
            if (parsed.AddressFamily != AddressFamily.InterNetwork)
            {
                throw new DrcomConnectionException("认证服务器必须是 IPv4 地址。");
            }

            return parsed;
        }

        IPAddress[] addresses;
        try
        {
            addresses = await Dns.GetHostAddressesAsync(host, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is SocketException or ArgumentException)
        {
            throw new DrcomConnectionException($"无法解析认证服务器 {host}：{exception.Message}", exception);
        }

        return addresses.FirstOrDefault(address => address.AddressFamily == AddressFamily.InterNetwork)
            ?? throw new DrcomConnectionException($"认证服务器 {host} 没有 IPv4 地址。");
    }

    private static async Task<IPAddress> GetRouteLocalAddressAsync(
        IPAddress remoteAddress,
        int remotePort,
        CancellationToken cancellationToken)
    {
        try
        {
            using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            socket.Connect(new IPEndPoint(remoteAddress, remotePort));
            return ((IPEndPoint)socket.LocalEndPoint!).Address;
        }
        catch (SocketException exception)
        {
            throw new DrcomConnectionException(
                $"没有到认证服务器 {remoteAddress}:{remotePort} 的路由：{exception.Message}",
                exception);
        }
        finally
        {
            cancellationToken.ThrowIfCancellationRequested();
        }
    }

    private static NetworkInterface? FindByAddress(IPAddress address)
    {
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up)
            {
                continue;
            }

            if (nic.GetIPProperties().UnicastAddresses.Any(unicast => unicast.Address.Equals(address)))
            {
                return nic;
            }
        }

        return null;
    }

    private static NetworkInterface? FindByName(string name)
    {
        var candidates = NetworkInterface.GetAllNetworkInterfaces()
            .Where(nic => nic.OperationalStatus == OperationalStatus.Up)
            .Where(nic =>
                string.Equals(nic.Name, name, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(nic.Id, name, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(nic.Description, name, StringComparison.OrdinalIgnoreCase))
            .ToList();

        return candidates.FirstOrDefault(nic => GetIpv4Address(nic) is not null)
            ?? candidates.FirstOrDefault();
    }

    private static IPAddress? GetIpv4Address(NetworkInterface nic)
    {
        return nic.GetIPProperties().UnicastAddresses
            .Select(unicast => unicast.Address)
            .FirstOrDefault(address => address.AddressFamily == AddressFamily.InterNetwork);
    }

    private static void ValidateMac(byte[] mac)
    {
        if (mac.Length != 6)
        {
            throw new DrcomConnectionException($"MAC 地址长度应为 6，实际为 {mac.Length}。");
        }

        if (mac.All(value => value == 0))
        {
            throw new DrcomConnectionException("网卡 MAC 地址为空。");
        }

        if ((mac[0] & 1) != 0)
        {
            throw new DrcomConnectionException("MAC 地址是组播地址，不能用于登录。");
        }
    }
}
