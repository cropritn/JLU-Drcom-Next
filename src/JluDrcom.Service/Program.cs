using JluDrcom.Core.Configuration;
using JluDrcom.Core.Networking;
using JluDrcom.Core.Protocol;
using JluDrcom.Service;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Serilog;
using Serilog.Events;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddWindowsService(options =>
{
    options.ServiceName = ProtocolConstants.ServiceName;
});

builder.Services.Configure<HostOptions>(options =>
{
    options.ShutdownTimeout = TimeSpan.FromSeconds(10);
});

var configPath = GetOption(args, "--config");
builder.Services.AddSingleton(new ConfigStore(configPath));
builder.Services.AddSingleton<INetworkSelector, NetworkSelector>();
builder.Services.AddSingleton<IUdpTransportFactory, UdpTransportFactory>();
builder.Services.AddSingleton<ServiceStatusStore>();
builder.Services.AddSingleton<ReloadSignal>();
builder.Services.AddSingleton<SessionControl>();
builder.Services.AddHostedService<DrcomWorker>();
builder.Services.AddHostedService<StatusPipeServer>();

var logDirectory = ResolveLogDirectory();
builder.Services.AddSingleton(new ServiceRuntimeInfo { LogDirectory = logDirectory });

var logger = new LoggerConfiguration()
    .MinimumLevel.Information()
    .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
    .Enrich.FromLogContext()
    .WriteTo.Console()
    .WriteTo.File(
        Path.Combine(logDirectory, "jlu-drcom-.log"),
        rollingInterval: RollingInterval.Day,
        retainedFileCountLimit: 14,
        fileSizeLimitBytes: 10 * 1024 * 1024,
        rollOnFileSizeLimit: true,
        shared: false)
    .CreateLogger();

builder.Logging.ClearProviders();
builder.Logging.AddSerilog(logger, dispose: true);

var host = builder.Build();
host.Run();

static string? GetOption(string[] args, string name)
{
    for (var i = 0; i < args.Length - 1; i++)
    {
        if (args[i].Equals(name, StringComparison.OrdinalIgnoreCase))
        {
            return args[i + 1];
        }
    }

    return null;
}

static string ResolveLogDirectory()
{
    // Deployment grants LocalService modify on the ProgramData log directory.
    // Probe it anyway: an ACL/redirect problem must not leave the service
    // without logs. The fallbacks are per-service-account locations.
    var candidates = new[]
    {
        AppPaths.LogsDirectory,
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "JluDrcom",
            "logs"),
        Path.Combine(Path.GetTempPath(), "JluDrcom", "logs"),
    };

    foreach (var candidate in candidates)
    {
        if (TryPrepareLogDirectory(candidate))
        {
            return candidate;
        }
    }

    return Path.GetTempPath();
}

static bool TryPrepareLogDirectory(string directory)
{
    try
    {
        Directory.CreateDirectory(directory);
        var probe = Path.Combine(directory, ".write-probe");
        File.WriteAllText(probe, "ok");

        try
        {
            File.Delete(probe);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Writing works; a leftover probe file is harmless.
        }

        return true;
    }
    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
    {
        return false;
    }
}
