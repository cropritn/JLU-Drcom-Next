using System.Net;
using System.Net.Sockets;

namespace JluDrcom.Core.Networking;

/// <summary>
/// One socket per session. Every exchange has a hard timeout and is cancellable;
/// the old implementation's unbounded <c>UdpClient.Receive</c> is gone.
/// </summary>
public sealed class UdpTransport : IUdpTransport
{
    private readonly Socket _socket;
    private bool _disposed;

    public UdpTransport(IPAddress localAddress)
    {
        _socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        _socket.Bind(new IPEndPoint(localAddress, 0));
        _socket.ReceiveBufferSize = 64 * 1024;
    }

    public async Task<UdpExchangeResult> ExchangeAsync(
        ReadOnlyMemory<byte> request,
        IPEndPoint remote,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(remote);

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeout);

        try
        {
            await _socket.SendToAsync(request, SocketFlags.None, remote, timeoutCts.Token)
                .ConfigureAwait(false);

            var buffer = new byte[4096];
            var result = await _socket.ReceiveFromAsync(
                    buffer.AsMemory(),
                    SocketFlags.None,
                    remote,
                    timeoutCts.Token)
                .ConfigureAwait(false);

            return UdpExchangeResult.Ok(buffer[..result.ReceivedBytes]);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return UdpExchangeResult.Timeout();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (SocketException exception)
        {
            return UdpExchangeResult.Failure(exception.Message);
        }
        catch (ObjectDisposedException)
        {
            throw;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _socket.Dispose();
    }
}
