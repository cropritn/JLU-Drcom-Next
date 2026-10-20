using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.ServiceProcess;
using System.Windows;
using System.Windows.Threading;
using JluDrcom.Core.Configuration;
using JluDrcom.Core.Protocol;
using JluDrcom.Core.Session;
using JluDrcom.Core.Status;
using Forms = System.Windows.Forms;

namespace JluDrcom.SetupGui;

/// <summary>Tray icon, context menu, and 2-second status polling.</summary>
public sealed class TrayController : IDisposable
{
    private readonly StatusPipeClient _client = new();
    private readonly DispatcherTimer _timer;
    private readonly Icon _onlineIcon;
    private readonly Icon _offlineIcon;
    private readonly Icon _errorIcon;

    private Forms.NotifyIcon? _icon;
    private Forms.ToolStripMenuItem? _statusItem;
    private Forms.ToolStripMenuItem? _ipItem;
    private Forms.ToolStripMenuItem? _adapterItem;
    private Forms.ToolStripMenuItem? _errorItem;
    private bool _refreshing;
    private bool _disposed;
    private string? _logDirectory;

    public TrayController()
    {
        Forms.Application.EnableVisualStyles();

        _onlineIcon = LoadIcon("online.ico");
        _offlineIcon = LoadIcon("offline.ico");
        _errorIcon = LoadIcon("error.ico");

        _timer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(2),
        };
        _timer.Tick += async (_, _) => await RefreshAsync();
    }

    public void Initialize()
    {
        _icon = new Forms.NotifyIcon
        {
            Visible = true,
            Icon = _offlineIcon,
            Text = "JLU Drcom",
        };

        var menu = new Forms.ContextMenuStrip();

        _statusItem = new Forms.ToolStripMenuItem("正在读取状态...")
        {
            Enabled = false,
        };
        _ipItem = new Forms.ToolStripMenuItem("IP：-") { Enabled = false };
        _adapterItem = new Forms.ToolStripMenuItem("网卡：-") { Enabled = false };
        _errorItem = new Forms.ToolStripMenuItem("错误：-")
        {
            Enabled = false,
            Visible = false,
        };

        var reconnectItem = new Forms.ToolStripMenuItem("重新连接");
        reconnectItem.Click += async (_, _) => await ReconnectAsync();

        var disconnectItem = new Forms.ToolStripMenuItem("断开连接");
        disconnectItem.Click += async (_, _) => await DisconnectAsync();

        var settingsItem = new Forms.ToolStripMenuItem("设置...");
        settingsItem.Click += (_, _) => OpenSettings();

        var logsItem = new Forms.ToolStripMenuItem("打开日志目录");
        logsItem.Click += (_, _) => OpenLogs();

        var exitItem = new Forms.ToolStripMenuItem("退出");
        exitItem.Click += (_, _) => Exit();

        menu.Items.Add(_statusItem);
        menu.Items.Add(_ipItem);
        menu.Items.Add(_adapterItem);
        menu.Items.Add(_errorItem);
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add(reconnectItem);
        menu.Items.Add(disconnectItem);
        menu.Items.Add(settingsItem);
        menu.Items.Add(logsItem);
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add(exitItem);

        _icon.ContextMenuStrip = menu;
        _icon.DoubleClick += (_, _) => OpenSettings();

        _timer.Start();
        _ = RefreshAsync();
    }

    private async Task RefreshAsync()
    {
        if (_refreshing || _disposed || _icon is null)
        {
            return;
        }

        _refreshing = true;
        try
        {
            var snapshot = await _client.GetStatusAsync();
            if (snapshot is null)
            {
                var state = QueryService();
                var running = state.Equals("Running", StringComparison.OrdinalIgnoreCase);
                Update(
                    running ? "服务运行中（等待状态）" : $"服务未运行（{state}）",
                    "-",
                    "-",
                    null,
                    _offlineIcon);
                return;
            }

            _logDirectory = snapshot.LogDirectory;
            var session = snapshot.Session;
            var stateText = Describe(session.State);
            var ip = session.AssignedIp ?? session.LocalIp ?? "-";
            var adapter = session.AdapterName ?? "-";
            var error = session.LastError ?? snapshot.ConfigError;
            var icon = session.State switch
            {
                DrcomSessionState.Online => _onlineIcon,
                DrcomSessionState.AuthFailed or DrcomSessionState.ConfigurationError => _errorIcon,
                _ => _offlineIcon,
            };

            Update(stateText, ip, adapter, error, icon);
        }
        catch (Exception)
        {
            Update("状态读取失败", "-", "-", null, _errorIcon);
        }
        finally
        {
            _refreshing = false;
        }
    }

    private void Update(string state, string ip, string adapter, string? error, Icon icon)
    {
        if (_icon is null)
        {
            return;
        }

        _icon.Icon = icon;
        _icon.Text = Truncate($"JLU Drcom — {state}", 63);

        if (_statusItem is not null)
        {
            _statusItem.Text = $"状态：{state}";
        }

        if (_ipItem is not null)
        {
            _ipItem.Text = $"IP：{ip}";
        }

        if (_adapterItem is not null)
        {
            _adapterItem.Text = $"网卡：{adapter}";
        }

        if (_errorItem is not null)
        {
            _errorItem.Text = $"错误：{error}";
            _errorItem.Visible = !string.IsNullOrWhiteSpace(error);
        }
    }

    private async Task ReconnectAsync()
    {
        var ok = await _client.ReconnectAsync();
        ShowBalloon(ok ? "已请求重新连接。" : "服务没有响应，可能未安装或未启动。");
    }

    private async Task DisconnectAsync()
    {
        var ok = await _client.DisconnectAsync();
        ShowBalloon(ok ? "已断开当前会话。服务仍在运行，可随时重新连接。" : "服务没有响应，可能未安装或未启动。");
    }

    public void OpenSettings()
    {
        try
        {
            var processPath = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(processPath))
            {
                ShowBalloon("无法定位程序路径。");
                return;
            }

            Process.Start(new ProcessStartInfo
            {
                FileName = processPath,
                Arguments = "--settings",
                UseShellExecute = true,
            });
        }
        catch (Win32Exception)
        {
            // User cancelled UAC.
        }
        catch (Exception exception)
        {
            ShowBalloon($"打开设置失败：{exception.Message}");
        }
    }

    private void OpenLogs()
    {
        try
        {
            var path = !string.IsNullOrWhiteSpace(_logDirectory) && Directory.Exists(_logDirectory)
                ? _logDirectory
                : Directory.Exists(AppPaths.LogsDirectory)
                    ? AppPaths.LogsDirectory
                    : AppPaths.ProgramDataRoot;
            Directory.CreateDirectory(path);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"")
            {
                UseShellExecute = true,
            });
        }
        catch (Exception exception)
        {
            ShowBalloon($"打开日志目录失败：{exception.Message}");
        }
    }

    private void ShowBalloon(string message)
    {
        _icon?.ShowBalloonTip(3000, "JLU Drcom", message, Forms.ToolTipIcon.Info);
    }

    private void Exit()
    {
        _timer.Stop();
        if (_icon is not null)
        {
            _icon.Visible = false;
        }

        System.Windows.Application.Current.Shutdown();
    }

    private static string QueryService()
    {
        try
        {
            using var service = new ServiceController(ProtocolConstants.ServiceName);
            return service.Status.ToString();
        }
        catch (InvalidOperationException)
        {
            return "NotInstalled";
        }
        catch (Exception exception) when (exception is Win32Exception or UnauthorizedAccessException)
        {
            return "Unknown";
        }
    }

    private static string Describe(DrcomSessionState state) => state switch
    {
        DrcomSessionState.Online => "在线",
        DrcomSessionState.SelectingNetwork => "选择网卡",
        DrcomSessionState.Challenging => "认证握手",
        DrcomSessionState.Authenticating => "登录中",
        DrcomSessionState.Reconnecting => "重连中",
        DrcomSessionState.AuthFailed => "认证失败",
        DrcomSessionState.ConfigurationError => "配置错误",
        DrcomSessionState.Disconnected => "已断开",
        _ => "已停止",
    };

    private static Icon LoadIcon(string name)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Assets", name);
        return File.Exists(path) ? new Icon(path) : (Icon)SystemIcons.Application.Clone();
    }

    private static string Truncate(string value, int maximum) =>
        value.Length <= maximum ? value : value[..maximum];

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _timer.Stop();

        if (_icon is not null)
        {
            _icon.Visible = false;
            _icon.Dispose();
            _icon = null;
        }

        _onlineIcon.Dispose();
        _offlineIcon.Dispose();
        _errorIcon.Dispose();
    }
}
