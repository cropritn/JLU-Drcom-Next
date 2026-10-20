using System.Net.NetworkInformation;
using System.Security;
using JluDrcom.Core.Protocol;
using Microsoft.Win32;

namespace JluDrcom.Core.Configuration;

public sealed record LegacyConfig(string Username, string Password, byte[]? Mac);

/// <summary>
/// Reads the 2018 service's registry values. Only the elevated migration tool
/// should call this; the new service never touches the legacy key.
/// </summary>
public static class LegacyRegistryConfig
{
    public static LegacyConfig? TryRead()
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        try
        {
            using var key = Registry.CurrentConfig.OpenSubKey(ProtocolConstants.LegacyRegistryPath, writable: false);
            if (key is null)
            {
                return null;
            }

            var username = key.GetValue("username") as string ?? string.Empty;
            var password = key.GetValue("password") as string ?? string.Empty;
            var mac = key.GetValue("MAC") switch
            {
                byte[] bytes when bytes.Length == 6 => bytes,
                string text when PhysicalAddress.TryParse(text, out var address) => address.GetAddressBytes(),
                _ => null,
            };

            return new LegacyConfig(username, password, mac);
        }
        catch (Exception exception) when (exception is SecurityException or UnauthorizedAccessException or IOException)
        {
            throw new DrcomConfigurationException($"读取旧版注册表配置失败：{exception.Message}", exception);
        }
    }

    public static bool DeletePassword()
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        using var key = Registry.CurrentConfig.OpenSubKey(ProtocolConstants.LegacyRegistryPath, writable: true);
        if (key is null)
        {
            return false;
        }

        key.DeleteValue("password", throwOnMissingValue: false);
        return true;
    }

    public static bool DeleteKey()
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        try
        {
            Registry.CurrentConfig.DeleteSubKeyTree(ProtocolConstants.LegacyRegistryPath, throwOnMissingSubKey: false);
            return true;
        }
        catch (Exception exception) when (exception is SecurityException or UnauthorizedAccessException)
        {
            throw new DrcomConfigurationException($"删除旧版注册表键失败：{exception.Message}", exception);
        }
    }
}
