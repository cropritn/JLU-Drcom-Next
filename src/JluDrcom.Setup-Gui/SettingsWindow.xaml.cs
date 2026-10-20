using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net.NetworkInformation;
using System.Security.Principal;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using JluDrcom.Core.Configuration;
using JluDrcom.Core.Protocol;
using JluDrcom.Core.Session;
using JluDrcom.Core.Status;
using JluDrcom.SetupGui.Wired;

namespace JluDrcom.SetupGui;

public partial class SettingsWindow : Window
{
    private static readonly System.Windows.Media.Brush OnlineBrush = CreateBrush(4, 120, 87);
    private static readonly System.Windows.Media.Brush ErrorBrush = CreateBrush(185, 28, 28);
    private static readonly System.Windows.Media.Brush MutedBrush = CreateBrush(107, 114, 128);

    private readonly ConfigStore _store = new();
    private readonly StatusPipeClient _statusClient = new();
    private readonly WiredNetworkService _wiredService = new();
    private readonly DispatcherTimer _timer;
    private DrcomConfigFile _file = new();
    private string? _logDirectory;
    private IReadOnlyList<WiredAdapter> _wiredAdapters = Array.Empty<WiredAdapter>();

    public SettingsWindow()
    {
        InitializeComponent();
        ApplyWindowIcon();

        if (!IsElevated())
        {
            System.Windows.MessageBox.Show(
                "设置需要管理员权限。请从托盘菜单打开设置。",
                "JLU Drcom",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            Loaded += (_, _) => Close();
            _timer = new DispatcherTimer();
            return;
        }

        LoadConfig();

        _timer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(2),
        };
        _timer.Tick += async (_, _) => await RefreshStatusAsync();
        _timer.Start();

        Closed += (_, _) => _timer.Stop();
        _ = RefreshStatusAsync();
    }

    private void LoadConfig()
    {
        var password = string.Empty;

        try
        {
            var settings = _store.Load();
            _file = settings.File;
            password = settings.Password;
        }
        catch (DrcomConfigurationException)
        {
            try
            {
                _file = _store.LoadFile();
            }
            catch (DrcomConfigurationException)
            {
                _file = _store.CreateDefault();
            }
        }

        txt_username.Text = _file.Username;
        txt_server.Text = _file.AuthServer;
        txt_port.Text = _file.AuthPort.ToString(CultureInfo.InvariantCulture);
        txt_heartbeat.Text = _file.HeartbeatSeconds.ToString(CultureInfo.InvariantCulture);
        txt_timeout.Text = _file.TimeoutSeconds.ToString(CultureInfo.InvariantCulture);
        txt_mac.Text = _file.Mac ?? string.Empty;
        SetPassword(password);
        txt_password_hint.Text = string.IsNullOrEmpty(_file.PasswordProtected)
            ? "尚未设置密码。"
            : "已读取保存的密码；留空保存则保留原密码。";

        PopulateInterfaces(_file.InterfaceName);
        SelectMode(_file.PacketMode);
        LoadWiredOptions();
    }

    private void SetPassword(string password)
    {
        txt_password.Password = password;
        txt_password_plain.Text = password;
        ShowPlainPassword(false);
    }

    private void ShowPlainPassword(bool plain)
    {
        txt_password_plain.Visibility = plain ? Visibility.Visible : Visibility.Collapsed;
        txt_password.Visibility = plain ? Visibility.Collapsed : Visibility.Visible;
        btn_toggle_password.Content = plain ? "\uE7B2" : "\uE7B3";
    }

    private void TogglePassword_Click(object sender, RoutedEventArgs e)
    {
        var showPlain = txt_password.Visibility == Visibility.Visible;
        if (showPlain)
        {
            txt_password_plain.Text = txt_password.Password;
        }
        else
        {
            txt_password.Password = txt_password_plain.Text;
        }

        ShowPlainPassword(showPlain);
    }

    private void LoadWiredOptions()
    {
        try
        {
            _wiredAdapters = _wiredService.GetWiredAdapters();
            cmb_wired_adapter.DisplayMemberPath = nameof(WiredAdapter.DisplayName);
            cmb_wired_adapter.ItemsSource = _wiredAdapters;

            var preferred = _wiredService.FindPreferred(
                _wiredAdapters,
                _file.Wired?.InterfaceName ?? _file.InterfaceName,
                _file.Wired?.Mac ?? _file.Mac);

            if (preferred is not null)
            {
                cmb_wired_adapter.SelectedItem = preferred;
                FillWiredFields(preferred);
            }
            else
            {
                txt_wired_status.Text = "没有检测到有线网卡。";
            }
        }
        catch (Exception exception)
        {
            txt_wired_status.Text = $"读取有线网卡失败：{exception.Message}";
        }
    }

    private void WiredAdapter_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (cmb_wired_adapter.SelectedItem is WiredAdapter adapter)
        {
            FillWiredFields(adapter);
        }
    }

    private void FillWiredFields(WiredAdapter adapter)
    {
        txt_wired_ip.Text = adapter.IpAddress;
        txt_wired_mask.Text = adapter.SubnetMask;
        txt_wired_gateway.Text = adapter.Gateway;
        txt_wired_dns1.Text = adapter.Dns1;
        txt_wired_dns2.Text = adapter.Dns2;
        txt_wired_status.Text = adapter.DhcpEnabled
            ? $"{adapter.DisplayName}：当前为自动获取（DHCP）。"
            : $"{adapter.DisplayName}：当前为静态配置。";
    }

    private WiredAdapter? SelectedWiredAdapter => cmb_wired_adapter.SelectedItem as WiredAdapter;

    private void ReadWired_Click(object sender, RoutedEventArgs e)
    {
        LoadWiredOptions();
    }

    private async void ApplyWired_Click(object sender, RoutedEventArgs e)
    {
        var adapter = SelectedWiredAdapter;
        if (adapter is null)
        {
            txt_wired_status.Text = "请先选择有线网卡。";
            return;
        }

        var settings = new WiredSettings(
            txt_wired_ip.Text.Trim(),
            txt_wired_mask.Text.Trim(),
            txt_wired_gateway.Text.Trim(),
            txt_wired_dns1.Text.Trim(),
            txt_wired_dns2.Text.Trim());

        txt_wired_status.Text = "正在应用有线设置...";
        var result = await Task.Run(() => _wiredService.Apply(adapter, settings));
        txt_wired_status.Text = result.Message;
        if (result.Success)
        {
            await Task.Delay(500);
            LoadWiredOptions();
        }
    }

    private async void EnableDhcp_Click(object sender, RoutedEventArgs e)
    {
        var adapter = SelectedWiredAdapter;
        if (adapter is null)
        {
            txt_wired_status.Text = "请先选择有线网卡。";
            return;
        }

        txt_wired_status.Text = "正在恢复自动获取...";
        var result = await Task.Run(() => _wiredService.EnableDhcp(adapter));
        txt_wired_status.Text = result.Message;
        if (result.Success)
        {
            await Task.Delay(500);
            LoadWiredOptions();
        }
    }

    private void PopulateInterfaces(string? selected)
    {
        cmb_interface.Items.Clear();
        var automatic = new ComboBoxItem
        {
            Content = "自动（按路由选择）",
            Tag = null,
        };
        cmb_interface.Items.Add(automatic);

        ComboBoxItem? match = null;
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces()
                     .Where(nic => nic.OperationalStatus == OperationalStatus.Up)
                     .Where(nic => nic.GetIPProperties().UnicastAddresses
                         .Any(address => address.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork))
                     .OrderBy(nic => nic.Name))
        {
            var item = new ComboBoxItem
            {
                Content = $"{nic.Name} — {nic.Description}",
                Tag = nic.Name,
            };
            cmb_interface.Items.Add(item);
            if (string.Equals(nic.Name, selected, StringComparison.OrdinalIgnoreCase))
            {
                match = item;
            }
        }

        cmb_interface.SelectedItem = match ?? automatic;
    }

    private void SelectMode(LoginPacketMode mode)
    {
        var wanted = mode == LoginPacketMode.Complete ? "complete" : "legacy";
        foreach (var item in cmb_mode.Items.OfType<ComboBoxItem>())
        {
            if (string.Equals(item.Tag as string, wanted, StringComparison.OrdinalIgnoreCase))
            {
                cmb_mode.SelectedItem = item;
                return;
            }
        }

        cmb_mode.SelectedIndex = 0;
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (!int.TryParse(txt_port.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var port))
            {
                throw new DrcomConfigurationException("端口必须是整数。");
            }

            if (!int.TryParse(txt_heartbeat.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var heartbeat))
            {
                throw new DrcomConfigurationException("心跳间隔必须是整数。");
            }

            if (!int.TryParse(txt_timeout.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var timeout))
            {
                throw new DrcomConfigurationException("超时必须是整数。");
            }

            _file.Username = txt_username.Text.Trim();
            _file.AuthServer = txt_server.Text.Trim();
            _file.AuthPort = port;
            _file.HeartbeatSeconds = heartbeat;
            _file.TimeoutSeconds = timeout;
            _file.Mac = string.IsNullOrWhiteSpace(txt_mac.Text) ? null : txt_mac.Text.Trim();
            _file.InterfaceName = (cmb_interface.SelectedItem as ComboBoxItem)?.Tag as string;
            _file.PacketMode = string.Equals(
                (cmb_mode.SelectedItem as ComboBoxItem)?.Tag as string,
                "complete",
                StringComparison.OrdinalIgnoreCase)
                ? LoginPacketMode.Complete
                : LoginPacketMode.Legacy;

            var password = txt_password.Visibility == Visibility.Visible
                ? txt_password.Password
                : txt_password_plain.Text;

            if (string.IsNullOrEmpty(password) && string.IsNullOrEmpty(_file.PasswordProtected))
            {
                throw new DrcomConfigurationException("首次使用必须设置密码。");
            }

            var wiredAdapter = SelectedWiredAdapter;
            _file.Wired = wiredAdapter is null && string.IsNullOrWhiteSpace(txt_wired_ip.Text)
                ? null
                : new WiredOptions
                {
                    InterfaceName = wiredAdapter?.Name,
                    Mac = wiredAdapter?.MacAddress,
                    IpAddress = txt_wired_ip.Text.Trim(),
                    SubnetMask = txt_wired_mask.Text.Trim(),
                    Gateway = txt_wired_gateway.Text.Trim(),
                    Dns1 = txt_wired_dns1.Text.Trim(),
                    Dns2 = txt_wired_dns2.Text.Trim(),
                    DhcpEnabled = wiredAdapter?.DhcpEnabled,
                };

            _store.Save(_file, string.IsNullOrEmpty(password) ? null : password);
            txt_password_hint.Text = "已保存；密码留空则保留原密码。";

            ShowStatus("已保存。服务会自动重新加载配置；有线设置需点击“应用有线设置”才会写入 Windows。", isError: false);
        }
        catch (Exception exception) when (exception is DrcomConfigurationException
                                          or IOException
                                          or UnauthorizedAccessException)
        {
            ShowStatus(exception.Message, isError: true);
        }
    }

    private async void Reconnect_Click(object sender, RoutedEventArgs e)
    {
        var ok = await _statusClient.ReconnectAsync();
        ShowStatus(ok ? "已请求重新连接。" : "服务没有响应，可能未安装或未启动。", isError: !ok);
    }

    private async void Disconnect_Click(object sender, RoutedEventArgs e)
    {
        var ok = await _statusClient.DisconnectAsync();
        ShowStatus(ok ? "已断开当前会话；服务仍在运行，可随时重新连接。" : "服务没有响应，可能未安装或未启动。", isError: !ok);
        await RefreshStatusAsync();
    }

    private void OpenLogs_Click(object sender, RoutedEventArgs e)
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
            ShowStatus($"打开日志目录失败：{exception.Message}", isError: true);
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => Close();

    private async Task RefreshStatusAsync()
    {
        try
        {
            var snapshot = await _statusClient.GetStatusAsync();
            if (snapshot is null)
            {
                txt_service_state.Text = "服务未运行 / 未安装";
                txt_service_state.Foreground = MutedBrush;
                txt_session_ip.Text = "-";
                txt_adapter.Text = "-";
                txt_last_error.Text = "-";
                txt_last_error.Foreground = MutedBrush;
                return;
            }

            _logDirectory = snapshot.LogDirectory;
            var session = snapshot.Session;
            txt_service_state.Text = Describe(session.State);
            txt_service_state.Foreground = session.State switch
            {
                DrcomSessionState.Online => OnlineBrush,
                DrcomSessionState.AuthFailed or DrcomSessionState.ConfigurationError => ErrorBrush,
                _ => MutedBrush,
            };
            txt_session_ip.Text = session.AssignedIp ?? session.LocalIp ?? "-";
            txt_adapter.Text = string.IsNullOrWhiteSpace(session.AdapterName)
                ? "-"
                : $"{session.AdapterName}  {session.MacAddress}";
            var error = session.LastError ?? snapshot.ConfigError;
            txt_last_error.Text = string.IsNullOrWhiteSpace(error) ? "-" : error;
            txt_last_error.Foreground = string.IsNullOrWhiteSpace(error) ? MutedBrush : ErrorBrush;
        }
        catch (Exception exception)
        {
            txt_service_state.Text = $"状态读取失败：{exception.Message}";
        }
    }

    private void ShowStatus(string message, bool isError)
    {
        txt_save_status.Text = message;
        txt_save_status.Foreground = new SolidColorBrush(
            isError
                ? System.Windows.Media.Color.FromRgb(185, 28, 28)
                : System.Windows.Media.Color.FromRgb(4, 120, 87));
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

    private void ApplyWindowIcon()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Assets", "online.ico");
        if (!File.Exists(path))
        {
            return;
        }

        try
        {
            var decoder = new IconBitmapDecoder(
                new Uri(path, UriKind.Absolute),
                BitmapCreateOptions.None,
                BitmapCacheOption.OnLoad);
            Icon = decoder.Frames.OrderByDescending(frame => frame.PixelWidth).First();
        }
        catch (Exception)
        {
            // The title bar falls back to the executable icon.
        }
    }

    private static System.Windows.Media.Brush CreateBrush(byte red, byte green, byte blue)
    {
        var brush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(red, green, blue));
        brush.Freeze();
        return brush;
    }

    private static bool IsElevated()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }
}
