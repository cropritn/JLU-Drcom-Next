using System.Net;
using System.Net.Sockets;
using System.Text;
using JluDrcom.Core.Networking;
using JluDrcom.Core.Protocol;
using Microsoft.Extensions.Logging;

namespace JluDrcom.Core.Session;

/// <summary>
/// The protocol state machine: challenge -> login -> heartbeat, with bounded
/// retries and exponential backoff. It owns no global state and is testable
/// through <see cref="INetworkSelector"/> and <see cref="IUdpTransportFactory"/>.
/// </summary>
public sealed class DrcomSession : IDisposable
{
    private readonly DrcomSessionOptions _options;
    private readonly INetworkSelector _networkSelector;
    private readonly IUdpTransportFactory _transportFactory;
    private readonly TimeProvider _timeProvider;
    private readonly IRandomSource _random;
    private readonly ILogger<DrcomSession>? _logger;
    private readonly IProgress<DrcomStatus>? _progress;
    private readonly object _statusGate = new();

    private DrcomStatus _status = new();
    private IUdpTransport? _transport;
    private bool _disposed;

    public DrcomSession(
        DrcomSessionOptions options,
        INetworkSelector networkSelector,
        IUdpTransportFactory transportFactory,
        TimeProvider? timeProvider = null,
        IRandomSource? random = null,
        ILogger<DrcomSession>? logger = null,
        IProgress<DrcomStatus>? progress = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _networkSelector = networkSelector ?? throw new ArgumentNullException(nameof(networkSelector));
        _transportFactory = transportFactory ?? throw new ArgumentNullException(nameof(transportFactory));
        _timeProvider = timeProvider ?? TimeProvider.System;
        _random = random ?? CryptoRandomSource.Instance;
        _logger = logger;
        _progress = progress;
    }

    public DrcomStatus CurrentStatus
    {
        get
        {
            lock (_statusGate)
            {
                return _status;
            }
        }
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var attempt = 0;

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    await RunSessionAsync(cancellationToken).ConfigureAwait(false);
                    attempt = 0;
                    throw new DrcomConnectionException("会话意外结束。");
                }
                catch (DrcomAuthException exception)
                {
                    Update(status => status with
                    {
                        State = DrcomSessionState.AuthFailed,
                        LastError = exception.Message,
                        OnlineSince = null,
                    });
                    throw;
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception exception) when (exception is DrcomConnectionException
                                                  or DrcomProtocolException
                                                  or SocketException
                                                  or IOException)
                {
                    attempt++;
                    Update(status => status with
                    {
                        State = DrcomSessionState.Reconnecting,
                        LastError = exception.Message,
                        Attempt = attempt,
                        OnlineSince = null,
                    });
                    _logger?.LogWarning(
                        exception,
                        "Connection attempt {Attempt} failed: {Message}",
                        attempt,
                        exception.Message);
                    await DelayBackoffAsync(attempt, cancellationToken).ConfigureAwait(false);
                }
            }
        }
        finally
        {
            DisposeTransport();
            Update(status => status.State == DrcomSessionState.AuthFailed
                ? status
                : status with
                {
                    State = DrcomSessionState.Stopped,
                    OnlineSince = null,
                });
        }
    }

    private async Task RunSessionAsync(CancellationToken cancellationToken)
    {
        Update(status => status with
        {
            State = DrcomSessionState.SelectingNetwork,
            LastError = null,
            Attempt = 0,
        });

        var network = await _networkSelector.SelectAsync(
                _options.AuthServer,
                _options.AuthPort,
                _options.InterfaceName,
                _options.Mac,
                cancellationToken)
            .ConfigureAwait(false);

        _transport = _transportFactory.Create(network.LocalAddress);
        var remote = new IPEndPoint(network.RemoteAddress, _options.AuthPort);

        Update(status => status with
        {
            LocalIp = network.LocalAddress.ToString(),
            AdapterName = network.AdapterName,
            MacAddress = FormatMac(network.MacAddress),
            State = DrcomSessionState.Challenging,
        });

        _logger?.LogInformation(
            "Using adapter {Adapter} ({LocalIp}, {Mac}) -> {Remote}",
            network.AdapterName,
            network.LocalAddress,
            FormatMac(network.MacAddress),
            remote);

        var challenge = await ChallengeAsync(remote, cancellationToken).ConfigureAwait(false);
        var assignedIp = challenge.AssignedIp.Equals(IPAddress.Any)
            ? network.LocalAddress
            : challenge.AssignedIp;
        var assignedIpBytes = assignedIp.GetAddressBytes();

        Update(status => status with
        {
            State = DrcomSessionState.Authenticating,
            AssignedIp = assignedIp.ToString(),
        });

        var hostname = SanitizeHostname(Environment.MachineName);
        var loginRequest = new DrcomLoginRequest(
            _options.Username,
            _options.Password,
            challenge.Challenge,
            assignedIpBytes,
            network.MacAddress,
            hostname,
            _options.PacketMode);

        var loginPacket = DrcomPacketBuilder.BuildLoginRequest(loginRequest, _random);
        var loginData = await ExchangeAsync(loginPacket, remote, cancellationToken).ConfigureAwait(false);
        var login = DrcomPacketParser.ParseLoginResponse(loginData);

        // MD5A is always bytes 4..19 of the login packet, regardless of trailer mode.
        var md5a = loginPacket.AsSpan(4, ProtocolConstants.Md5Length).ToArray();

        _logger?.LogInformation(
            "Login succeeded; packet mode {Mode}, login packet {Length} bytes",
            _options.PacketMode,
            loginPacket.Length);

        Update(status => status with
        {
            State = DrcomSessionState.Online,
            AssignedIp = assignedIp.ToString(),
            OnlineSince = _timeProvider.GetUtcNow(),
            LastError = null,
            Heartbeats = 0,
        });

        await HeartbeatLoopAsync(remote, md5a, login.AuthInformation, assignedIpBytes, cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<DrcomStartResponse> ChallengeAsync(
        IPEndPoint remote,
        CancellationToken cancellationToken)
    {
        string? lastError = null;

        for (var attempt = 0; attempt < _options.MaxChallengeAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var packet = DrcomPacketBuilder.BuildStartRequest(attempt, _random);
            try
            {
                var data = await ExchangeAsync(packet, remote, cancellationToken).ConfigureAwait(false);
                return DrcomPacketParser.ParseStartResponse(data);
            }
            catch (DrcomConnectionException exception)
            {
                lastError = exception.Message;
            }
            catch (DrcomProtocolException exception)
            {
                lastError = exception.Message;
            }

            await Delay(ChallengeRetryDelay, cancellationToken).ConfigureAwait(false);
        }

        throw new DrcomConnectionException(
            $"Challenge failed after {_options.MaxChallengeAttempts} attempts: {lastError ?? "no response"}");
    }

    private async Task HeartbeatLoopAsync(
        IPEndPoint remote,
        byte[] md5a,
        byte[] authInformation,
        byte[] assignedIp,
        CancellationToken cancellationToken)
    {
        var count = (byte)0;
        var failures = 0;
        var heartbeats = 0L;
        var tail = new byte[4];

        while (!cancellationToken.IsCancellationRequested)
        {
            await Delay(_options.HeartbeatInterval, cancellationToken).ConfigureAwait(false);

            try
            {
                var alive = DrcomPacketBuilder.BuildAlive(md5a, authInformation, _random);
                await ExchangeAsync(alive, remote, cancellationToken).ConfigureAwait(false);

                if (count % 10 == 0)
                {
                    var fake = DrcomPacketBuilder.BuildKeepAlive(
                        1,
                        fake: true,
                        count,
                        tail,
                        assignedIp,
                        legacyCrc: _options.PacketMode == LoginPacketMode.Legacy,
                        _random);
                    await ExchangeAsync(fake, remote, cancellationToken).ConfigureAwait(false);
                }

                var keepAlive1 = DrcomPacketBuilder.BuildKeepAlive(
                    1,
                    fake: false,
                    count,
                    tail,
                    assignedIp,
                    legacyCrc: _options.PacketMode == LoginPacketMode.Legacy,
                    _random);
                var response1 = await ExchangeAsync(keepAlive1, remote, cancellationToken)
                    .ConfigureAwait(false);
                var parsed1 = DrcomPacketParser.ParseKeepAliveResponse(response1);
                if (parsed1.Code == (byte)DrcomCode.KeepAlive)
                {
                    tail = parsed1.Tail;
                }
                else
                {
                    _logger?.LogDebug(
                        "Keep-alive response code 0x{Code:X2}; tail not updated.",
                        parsed1.Code);
                }

                var keepAlive3 = DrcomPacketBuilder.BuildKeepAlive(
                    3,
                    fake: false,
                    count,
                    tail,
                    assignedIp,
                    legacyCrc: _options.PacketMode == LoginPacketMode.Legacy,
                    _random);
                var response3 = await ExchangeAsync(keepAlive3, remote, cancellationToken)
                    .ConfigureAwait(false);
                _ = DrcomPacketParser.ParseKeepAliveResponse(response3);

                failures = 0;
                count++;
                heartbeats++;
                Update(status => status with
                {
                    Heartbeats = heartbeats,
                    LastError = null,
                });
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception) when (exception is DrcomConnectionException
                                              or DrcomProtocolException
                                              or SocketException)
            {
                failures++;
                _logger?.LogWarning(
                    exception,
                    "Heartbeat failure {Failures}/{Threshold}",
                    failures,
                    _options.HeartbeatFailureThreshold);

                Update(status => status with { LastError = exception.Message });

                if (failures >= _options.HeartbeatFailureThreshold)
                {
                    throw new DrcomConnectionException(
                        $"心跳连续失败 {failures} 次，准备重新登录。",
                        exception);
                }
            }
        }
    }

    private async Task<byte[]> ExchangeAsync(
        byte[] packet,
        IPEndPoint remote,
        CancellationToken cancellationToken)
    {
        if (_transport is null)
        {
            throw new InvalidOperationException("UDP transport is not initialized.");
        }

        var result = await _transport.ExchangeAsync(
                packet,
                remote,
                _options.ExchangeTimeout,
                cancellationToken)
            .ConfigureAwait(false);

        if (result.Success)
        {
            return result.Data;
        }

        if (result.TimedOut)
        {
            throw new DrcomConnectionException($"等待 {remote} 响应超时（{_options.ExchangeTimeout.TotalSeconds:0.#}s）。");
        }

        throw new DrcomConnectionException(result.Error ?? "UDP 交换失败。");
    }

    private TimeSpan ChallengeRetryDelay => TimeSpan.FromMilliseconds(
        Math.Min(1000, Math.Max(100, _options.InitialBackoff.TotalMilliseconds / 2)));

    private async Task DelayBackoffAsync(int attempt, CancellationToken cancellationToken)
    {
        var exponent = Math.Min(attempt, 5);
        var seconds = Math.Min(30, _options.InitialBackoff.TotalSeconds * Math.Pow(2, exponent));
        var jitter = 0.75 + (Random.Shared.NextDouble() * 0.5);
        await Delay(TimeSpan.FromSeconds(seconds * jitter), cancellationToken).ConfigureAwait(false);
    }

    private Task Delay(TimeSpan delay, CancellationToken cancellationToken) =>
        Task.Delay(delay, _timeProvider, cancellationToken);

    private void Update(Func<DrcomStatus, DrcomStatus> update)
    {
        DrcomStatus snapshot;
        lock (_statusGate)
        {
            _status = update(_status) with { UpdatedAt = _timeProvider.GetUtcNow() };
            snapshot = _status;
        }

        _progress?.Report(snapshot);
    }

    private void DisposeTransport()
    {
        _transport?.Dispose();
        _transport = null;
    }

    private static string SanitizeHostname(string hostname)
    {
        var builder = new StringBuilder(ProtocolConstants.MaxHostnameLength);
        foreach (var character in hostname)
        {
            if (builder.Length >= ProtocolConstants.MaxHostnameLength)
            {
                break;
            }

            if (character <= 0x7F && !char.IsControl(character))
            {
                builder.Append(character);
            }
        }

        return builder.Length == 0 ? "JLU-PC" : builder.ToString();
    }

    private static string FormatMac(ReadOnlySpan<byte> mac) =>
        string.Join('-', mac.ToArray().Select(value => value.ToString("X2")));

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        DisposeTransport();
    }
}
