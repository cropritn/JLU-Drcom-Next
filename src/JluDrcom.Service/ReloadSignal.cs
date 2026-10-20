using System.Threading.Channels;

namespace JluDrcom.Service;

/// <summary>Coalescing "restart the session" signal for config/network changes.</summary>
public sealed class ReloadSignal
{
    private readonly Channel<byte> _channel = Channel.CreateBounded<byte>(
        new BoundedChannelOptions(1)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false,
        });

    public void Request() => _channel.Writer.TryWrite(0);

    public async Task<bool> WaitAsync(CancellationToken cancellationToken)
    {
        // Read (not just WaitToRead) so the signal is consumed; otherwise the
        // next loop would see the same item and reload forever.
        await _channel.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }
}
