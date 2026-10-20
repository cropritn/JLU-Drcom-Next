using System.Diagnostics;
using System.Security.Principal;
using System.Windows;

namespace JluDrcom.SetupGui;

public partial class App : System.Windows.Application
{
    private Mutex? _mutex;
    private TrayController? _tray;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var settingsMode = e.Args.Any(argument =>
            argument.Equals("--settings", StringComparison.OrdinalIgnoreCase));

        if (settingsMode)
        {
            ShowSettings();
            return;
        }

        _mutex = new Mutex(initiallyOwned: true, @"Local\JluDrcom.SetupGui", out var createdNew);
        if (!createdNew)
        {
            // Another instance owns the tray; still honor a second double-click
            // by opening the settings window.
            OpenSettingsProcess();
            Shutdown();
            return;
        }

        _tray = new TrayController();
        _tray.Initialize();

        // A manual launch should show the configuration instead of only
        // dropping an icon into the notification area.
        _tray.OpenSettings();
    }

    private void ShowSettings()
    {
        if (!IsElevated())
        {
            System.Windows.MessageBox.Show(
                "设置需要管理员权限。请从托盘菜单打开设置。",
                "JLU Drcom",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            Shutdown();
            return;
        }

        var window = new SettingsWindow();
        MainWindow = window;
        ShutdownMode = ShutdownMode.OnMainWindowClose;
        window.Show();
    }

    private static void OpenSettingsProcess()
    {
        var processPath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(processPath))
        {
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = processPath,
                Arguments = "--settings",
                UseShellExecute = true,
            });
        }
        catch
        {
            // The tray still works if the secondary settings process cannot start.
        }
    }

    private static bool IsElevated()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _tray?.Dispose();
        _mutex?.Dispose();
        base.OnExit(e);
    }
}
