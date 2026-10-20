using System.Security.Cryptography;
using System.Text;
using JluDrcom.Core.Protocol;

namespace JluDrcom.Core.Configuration;

/// <summary>
/// DPAPI (machine scope) for the campus password. Machine scope is required
/// because the service and the elevated settings app run under different
/// accounts. The config directory ACL is the second line of defence.
/// </summary>
public static class SecretProtector
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("JluDrcom/v1");

    public static string Protect(string plaintext)
    {
        if (string.IsNullOrEmpty(plaintext))
        {
            throw new ArgumentException("Password must not be empty.", nameof(plaintext));
        }

        var bytes = Encoding.UTF8.GetBytes(plaintext);
        try
        {
            var protectedBytes = ProtectedData.Protect(bytes, Entropy, DataProtectionScope.LocalMachine);
            return Convert.ToBase64String(protectedBytes);
        }
        catch (CryptographicException exception)
        {
            throw new DrcomConfigurationException("DPAPI 加密失败，无法保存密码。", exception);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    public static string Unprotect(string protectedBase64)
    {
        if (string.IsNullOrWhiteSpace(protectedBase64))
        {
            throw new DrcomConfigurationException("配置中没有密码。");
        }

        byte[] protectedBytes;
        try
        {
            protectedBytes = Convert.FromBase64String(protectedBase64);
        }
        catch (FormatException exception)
        {
            throw new DrcomConfigurationException("密码字段不是有效的 Base64。", exception);
        }

        try
        {
            var bytes = ProtectedData.Unprotect(protectedBytes, Entropy, DataProtectionScope.LocalMachine);
            try
            {
                return Encoding.UTF8.GetString(bytes);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(bytes);
            }
        }
        catch (CryptographicException exception)
        {
            throw new DrcomConfigurationException(
                "无法解密密码。配置可能来自另一台机器，或 DPAPI 数据已损坏。",
                exception);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(protectedBytes);
        }
    }
}
