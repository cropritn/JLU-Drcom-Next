using System.Net.NetworkInformation;
using JluDrcom.Core.Configuration;
using JluDrcom.Core.Networking;
using JluDrcom.Core.Protocol;
using JluDrcom.Core.Session;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace JluDrcom.Service;

/// <summary>
/// Owns the session lifetime. It reloads on config changes and network changes,
/// and never blocks the SCM start path.
/// </summary>
public sealed class DrcomWorker : BackgroundService
{
    private readonly ConfigStore _configStore;
    private readonly INetworkSelector _networkSelector;
    private readonly IUdpTransportFactory _transportFactory;
    private readonly ServiceStatusStore _status;
    private readonly ReloadSignal _reload;
    private readonly SessionControl _control;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<DrcomWorker> _logger;

    private FileSystemWatcher? _watcher;
    private Timer? _debounce;

    public DrcomWorker(
        ConfigStore configStore,
        INetworkSelector networkSelector,
        IUdpTransportFactory transportFactory,
        ServiceStatusStore status,
        ReloadSignal reload,
        SessionControl control,
        ILoggerFactory loggerFactory,
        ILogger<DrcomWorker> logger)
    {
        _configStore = configStore;
        _networkSelector = networkSelector;
        _transportFactory = transportFactory;
        _status = status;
        _reload = reload;
        _control = control;
        _loggerFactory = loggerFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _watcher = TryCreateWatcher();
        NetworkChange.NetworkAddressChanged += OnNetworkAddressChanged;
        NetworkChange.NetworkAvailabilityChanged += OnNetworkAvailabilityChanged;

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                if (_control.IsPaused)
                {
                    _status.UpdateSession(new DrcomStatus
                    {
                        State = DrcomSessionState.Disconnected,
                        LastError = null,
                    });
                    await WaitForReloadAsync(stoppingToken).ConfigureAwait(false);
                    continue;
                }

                DrcomSettings settings;
                try
                {
                    settings = _configStore.Load();
                    _status.SetConfigError(null);
                }
                catch (DrcomConfigurationException exception)
                {
                    _status.SetConfigError(exception.Message);
                    _status.UpdateSession(new DrcomStatus
                    {
                        State = DrcomSessionState.ConfigurationError,
                        LastError = exception.Message,
                    });
                    _logger.LogWarning("Configuration error: {Message}", exception.Message);
                    await WaitForReloadAsync(stoppingToken).ConfigureAwait(false);
                    continue;
                }

                var options = DrcomSessionOptions.FromSettings(settings);
                using var sessionCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                using var session = new DrcomSession(
                    options,
                    _networkSelector,
                    _transportFactory,
                    TimeProvider.System,
                    logger: _loggerFactory.CreateLogger<DrcomSession>(),
                    progress: new Progress<DrcomStatus>(_status.UpdateSession));

                var runTask = session.RunAsync(sessionCts.Token);
                using var waitCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                var reloadTask = _reload.WaitAsync(waitCts.Token);

                var completed = await Task.WhenAny(runTask, reloadTask).ConfigureAwait(false);
                if (completed == reloadTask)
                {
                    waitCts.Cancel();
                    sessionCts.Cancel();
                    try
                    {
                        await runTask.ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        // Expected when the config/network changed.
                    }
                    catch (Exception exception)
                    {
                        _logger.LogDebug(exception, "Session stopped after reload.");
                    }

                    await Task.Delay(250, stoppingToken).ConfigureAwait(false);
                    continue;
                }

                waitCts.Cancel();
                try
                {
                    await reloadTask.ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    // We cancelled the reload wait because the session ended.
                }

                try
                {
                    await runTask.ConfigureAwait(false);
                }
                catch (DrcomAuthException exception)
                {
                    _logger.LogWarning("Authentication failed: {Message}", exception.Message);
                    _status.UpdateSession(new DrcomStatus
                    {
                        State = DrcomSessionState.AuthFailed,
                        LastError = exception.Message,
                    });
                    await WaitForReloadAsync(stoppingToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception exception)
                {
                    _logger.LogError(exception, "Session terminated unexpectedly.");
                    await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken).ConfigureAwait(false);
                }
            }
        }
        finally
        {
            NetworkChange.NetworkAddressChanged -= OnNetworkAddressChanged;
            NetworkChange.NetworkAvailabilityChanged -= OnNetworkAvailabilityChanged;
            _watcher?.Dispose();
            _debounce?.Dispose();
        }
    }

    private async Task WaitForReloadAsync(CancellationToken stoppingToken)
    {
        try
        {
            await _reload.WaitAsync(stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Normal shutdown.
        }
    }

    private FileSystemWatcher? TryCreateWatcher()
    {
        try
        {
            Directory.CreateDirectory(_configStore.DirectoryPath);
            var watcher = new FileSystemWatcher(_configStore.DirectoryPath, "*")
            {
                NotifyFilter = NotifyFilters.LastWrite
                               | NotifyFilters.FileName
                               | NotifyFilters.CreationTime
                               | NotifyFilters.Size,
                EnableRaisingEvents = true,
            };
            watcher.Changed += OnConfigChanged;
            watcher.Created += OnConfigChanged;
            watcher.Deleted += OnConfigChanged;
            watcher.Renamed += OnConfigRenamed;
            return watcher;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(exception, "Cannot watch config directory; config reload will be unavailable.");
            return null;
        }
    }

    private void OnConfigChanged(object sender, FileSystemEventArgs e)
    {
        _logger.LogInformation("Config file changed ({ChangeType}); scheduling reload.", e.ChangeType);
        ScheduleReload();
    }

    private void OnConfigRenamed(object sender, RenamedEventArgs e)
    {
        _logger.LogInformation("Config file renamed ({OldName} -> {Name}); scheduling reload.", e.OldName, e.Name);
        ScheduleReload();
    }

    private void ScheduleReload()
    {
        _debounce?.Dispose();
        _debounce = new Timer(
            _ =>
            {
                _logger.LogInformation("Requesting session reload.");
                _reload.Request();
            },
            state: null,
            dueTime: 400,
            period: Timeout.Infinite);
    }

    private void OnNetworkAddressChanged(object? sender, EventArgs e)
    {
        _logger.LogInformation("Network address changed; restarting session.");
        _reload.Request();
    }

    private void OnNetworkAvailabilityChanged(object? sender, NetworkAvailabilityEventArgs e)
    {
        _logger.LogInformation("Network availability changed: {Available}", e.IsAvailable);
        _reload.Request();
    }

    public override void Dispose()
    {
        _watcher?.Dispose();
        _debounce?.Dispose();
        base.Dispose();
    }
}
