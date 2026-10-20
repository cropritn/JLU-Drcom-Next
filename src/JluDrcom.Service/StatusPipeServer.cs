using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using JluDrcom.Core.Configuration;
using JluDrcom.Core.Protocol;
using JluDrcom.Core.Status;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace JluDrcom.Service;

/// <summary>
/// Local status/reconnect endpoint. It exposes no credentials; any local user
/// may query status and request a reconnect.
/// </summary>
public sealed class StatusPipeServer : BackgroundService
{
    private readonly ServiceStatusStore _status;
    private readonly ReloadSignal _reload;
    private readonly SessionControl _control;
    private readonly ConfigStore _configStore;
    private readonly ILogger<StatusPipeServer> _logger;

    public StatusPipeServer(
        ServiceStatusStore status,
        ReloadSignal reload,
        SessionControl control,
        ConfigStore configStore,
        ILogger<StatusPipeServer> logger)
    {
        _status = status;
        _reload = reload;
        _control = control;
        _configStore = configStore;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var pipe = CreatePipe();
                await pipe.WaitForConnectionAsync(stoppingToken).ConfigureAwait(false);
                await HandleAsync(pipe, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (IOException exception)
            {
                _logger.LogDebug(exception, "Status pipe client disconnected.");
                await Task.Delay(200, stoppingToken).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                _logger.LogWarning(exception, "Status pipe server error.");
                await Task.Delay(500, stoppingToken).ConfigureAwait(false);
            }
        }
    }

    private async Task HandleAsync(NamedPipeServerStream pipe, CancellationToken cancellationToken)
    {
        using var reader = new StreamReader(
            pipe,
            Encoding.UTF8,
            detectEncodingFromByteOrderMarks: false,
            bufferSize: 1024,
            leaveOpen: true);
        await using var writer = new StreamWriter(
            pipe,
            new UTF8Encoding(false),
            bufferSize: 1024,
            leaveOpen: true)
        {
            AutoFlush = true,
        };

        var line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(line))
        {
            return;
        }

        StatusResponse response;
        try
        {
            var request = StatusJson.Deserialize<StatusRequest>(line);
            var command = request?.Command?.Trim().ToLowerInvariant();

            switch (command)
            {
                case "status":
                case "ping":
                    response = StatusResponse.Success(_status.Snapshot(_configStore));
                    break;

                case "reconnect":
                    _control.Reconnect();
                    _reload.Request();
                    response = StatusResponse.Success(_status.Snapshot(_configStore));
                    break;

                case "disconnect":
                    _control.Disconnect();
                    _reload.Request();
                    response = StatusResponse.Success(_status.Snapshot(_configStore));
                    break;

                default:
                    response = StatusResponse.Failure($"未知命令：{request?.Command}");
                    break;
            }
        }
        catch (Exception exception)
        {
            _logger.LogDebug(exception, "Invalid status pipe request.");
            response = StatusResponse.Failure(exception.Message);
        }

        await writer.WriteLineAsync(StatusJson.Serialize(response)).ConfigureAwait(false);
    }

    private static NamedPipeServerStream CreatePipe()
    {
        var security = new PipeSecurity();

        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
            PipeAccessRights.FullControl,
            AccessControlType.Allow));

        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
            PipeAccessRights.FullControl,
            AccessControlType.Allow));

        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null),
            PipeAccessRights.ReadWrite,
            AccessControlType.Allow));

        return NamedPipeServerStreamAcl.Create(
            ProtocolConstants.ResolveStatusPipeName(),
            PipeDirection.InOut,
            maxNumberOfServerInstances: 8,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous,
            inBufferSize: 4096,
            outBufferSize: 4096,
            pipeSecurity: security);
    }
}
