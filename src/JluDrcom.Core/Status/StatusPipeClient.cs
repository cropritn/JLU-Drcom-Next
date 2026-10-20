using System.IO.Pipes;
using System.Text;
using JluDrcom.Core.Protocol;

namespace JluDrcom.Core.Status;

/// <summary>Reads status / asks the service to reconnect over a local named pipe.</summary>
public sealed class StatusPipeClient
{
    private readonly string _pipeName;
    private readonly TimeSpan _connectTimeout;

    public StatusPipeClient(
        string? pipeName = null,
        TimeSpan? connectTimeout = null)
    {
        _pipeName = pipeName ?? ProtocolConstants.ResolveStatusPipeName();
        _connectTimeout = connectTimeout ?? TimeSpan.FromMilliseconds(500);
    }

    public async Task<StatusSnapshot?> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        var response = await SendAsync("status", cancellationToken).ConfigureAwait(false);
        return response is { Ok: true, Status: not null } ? response.Status : null;
    }

    public async Task<bool> ReconnectAsync(CancellationToken cancellationToken = default)
    {
        var response = await SendAsync("reconnect", cancellationToken).ConfigureAwait(false);
        return response is { Ok: true };
    }

    public async Task<bool> DisconnectAsync(CancellationToken cancellationToken = default)
    {
        var response = await SendAsync("disconnect", cancellationToken).ConfigureAwait(false);
        return response is { Ok: true };
    }

    private async Task<StatusResponse?> SendAsync(string command, CancellationToken cancellationToken)
    {
        try
        {
            await using var pipe = new NamedPipeClientStream(
                ".",
                _pipeName,
                PipeDirection.InOut,
                PipeOptions.Asynchronous);

            using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            connectCts.CancelAfter(_connectTimeout);
            await pipe.ConnectAsync(connectCts.Token).ConfigureAwait(false);

            await using var writer = new StreamWriter(
                pipe,
                new UTF8Encoding(false),
                bufferSize: 1024,
                leaveOpen: true)
            {
                AutoFlush = true,
            };
            using var reader = new StreamReader(
                pipe,
                Encoding.UTF8,
                detectEncodingFromByteOrderMarks: false,
                bufferSize: 1024,
                leaveOpen: true);

            await writer.WriteLineAsync(StatusJson.Serialize(new StatusRequest(command))).ConfigureAwait(false);
            var line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            return string.IsNullOrWhiteSpace(line) ? null : StatusJson.Deserialize<StatusResponse>(line);
        }
        catch (Exception exception) when (exception is TimeoutException
                                          or OperationCanceledException
                                          or IOException
                                          or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
