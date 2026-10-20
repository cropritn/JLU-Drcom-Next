using System.Reflection;
using JluDrcom.Core.Configuration;
using JluDrcom.Core.Protocol;
using JluDrcom.Core.Session;
using JluDrcom.Core.Status;

namespace JluDrcom.Service;

/// <summary>Thread-safe holder for the latest session status and config error.</summary>
public sealed class ServiceStatusStore
{
    private readonly ServiceRuntimeInfo _runtime;
    private readonly object _gate = new();
    private DrcomStatus _session = new();
    private string? _configError;

    public ServiceStatusStore(ServiceRuntimeInfo runtime)
    {
        _runtime = runtime;
    }

    public void UpdateSession(DrcomStatus status)
    {
        lock (_gate)
        {
            _session = status;
        }
    }

    public void SetConfigError(string? error)
    {
        lock (_gate)
        {
            _configError = error;
        }
    }

    public StatusSnapshot Snapshot(ConfigStore configStore)
    {
        DrcomConfigFile? file = null;
        string? loadError = null;
        try
        {
            file = configStore.LoadFile();
        }
        catch (DrcomConfigurationException exception)
        {
            loadError = exception.Message;
        }

        lock (_gate)
        {
            return new StatusSnapshot
            {
                ServiceInstalled = true,
                ServiceRunning = true,
                ServiceVersion = Assembly.GetEntryAssembly()?.GetName().Version?.ToString(3) ?? "unknown",
                ConfigPath = configStore.ConfigPath,
                LogDirectory = _runtime.LogDirectory,
                UsernameMasked = Mask(file?.Username),
                PacketMode = file?.PacketMode.ToString() ?? "unknown",
                ConfigError = _configError ?? loadError,
                Session = _session,
                SampledAt = DateTimeOffset.UtcNow,
            };
        }
    }

    private static string Mask(string? username)
    {
        if (string.IsNullOrWhiteSpace(username))
        {
            return "未配置";
        }

        if (username.Length <= 2)
        {
            return "**";
        }

        if (username.Length <= 4)
        {
            return username[..1] + "***";
        }

        return username[..2] + "***" + username[^2..];
    }
}
