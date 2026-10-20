using System.Net;

namespace JluDrcom.Core.Networking;

public interface IUdpTransport : IDisposable
{
    Task<UdpExchangeResult> ExchangeAsync(
        ReadOnlyMemory<byte> request,
        IPEndPoint remote,
        TimeSpan timeout,
        CancellationToken cancellationToken);
}

public interface IUdpTransportFactory
{
    IUdpTransport Create(IPAddress localAddress);
}

public sealed class UdpTransportFactory : IUdpTransportFactory
{
    public IUdpTransport Create(IPAddress localAddress) => new UdpTransport(localAddress);
}
