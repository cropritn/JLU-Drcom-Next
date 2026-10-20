using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using JluDrcom.Core.Protocol;

namespace JluDrcom.Core.Configuration;

/// <summary>Loads/saves <c>%ProgramData%\JluDrcom\config.json</c> atomically.</summary>
public sealed class ConfigStore
{
    private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();

    public ConfigStore(string? configPath = null)
    {
        ConfigPath = Path.GetFullPath(configPath ?? AppPaths.DefaultConfigPath);
    }

    public string ConfigPath { get; }

    public string DirectoryPath => Path.GetDirectoryName(ConfigPath)
        ?? throw new InvalidOperationException("Config path has no directory.");

    public bool Exists => File.Exists(ConfigPath);

    public DrcomConfigFile LoadFile()
    {
        if (!File.Exists(ConfigPath))
        {
            throw new DrcomConfigurationException($"配置文件不存在：{ConfigPath}");
        }

        try
        {
            var json = File.ReadAllText(ConfigPath, Encoding.UTF8);
            var file = JsonSerializer.Deserialize<DrcomConfigFile>(json, JsonOptions);
            if (file is null)
            {
                throw new DrcomConfigurationException("配置文件为空或格式错误。");
            }

            return file;
        }
        catch (JsonException exception)
        {
            throw new DrcomConfigurationException($"配置文件 JSON 无效：{exception.Message}", exception);
        }
        catch (IOException exception)
        {
            throw new DrcomConfigurationException($"读取配置失败：{exception.Message}", exception);
        }
        catch (UnauthorizedAccessException exception)
        {
            throw new DrcomConfigurationException($"没有权限读取配置：{exception.Message}", exception);
        }
    }

    public DrcomSettings Load()
    {
        var file = LoadFile();
        Validate(file, requirePassword: true);
        var password = SecretProtector.Unprotect(file.PasswordProtected);
        ValidatePassword(password);
        return new DrcomSettings(file, password);
    }

    /// <summary>
    /// Saves the file. Pass <paramref name="newPassword"/> to replace the secret;
    /// pass null/empty to keep the existing <see cref="DrcomConfigFile.PasswordProtected"/>.
    /// </summary>
    public void Save(DrcomConfigFile file, string? newPassword = null)
    {
        ArgumentNullException.ThrowIfNull(file);
        Normalize(file);

        if (!string.IsNullOrEmpty(newPassword))
        {
            ValidatePassword(newPassword);
            file.PasswordProtected = SecretProtector.Protect(newPassword);
        }
        else if (string.IsNullOrEmpty(file.PasswordProtected))
        {
            // The caller may have loaded only the public fields; preserve the secret.
            if (File.Exists(ConfigPath))
            {
                var existing = LoadFile();
                file.PasswordProtected = existing.PasswordProtected;
            }
        }

        Validate(file, requirePassword: true);
        file.UpdatedAt = DateTimeOffset.UtcNow;

        try
        {
            Directory.CreateDirectory(DirectoryPath);
            var json = JsonSerializer.Serialize(file, JsonOptions);
            var tempPath = ConfigPath + ".tmp";
            File.WriteAllText(tempPath, json, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            File.Move(tempPath, ConfigPath, overwrite: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new DrcomConfigurationException($"保存配置失败：{exception.Message}", exception);
        }
    }

    public DrcomConfigFile CreateDefault(string? username = null)
    {
        return new DrcomConfigFile
        {
            Username = username ?? string.Empty,
            AuthServer = ProtocolConstants.DefaultAuthServer,
            AuthPort = ProtocolConstants.DefaultAuthPort,
            HeartbeatSeconds = 20,
            TimeoutSeconds = 5,
            PacketMode = LoginPacketMode.Complete,
            LogLevel = "Information",
        };
    }

    public static void Validate(DrcomConfigFile file, bool requirePassword)
    {
        if (string.IsNullOrWhiteSpace(file.Username))
        {
            throw new DrcomConfigurationException("用户名不能为空。");
        }

        if (!IsAscii(file.Username))
        {
            throw new DrcomConfigurationException("用户名只能包含 ASCII 字符。");
        }

        if (Encoding.ASCII.GetByteCount(file.Username) > ProtocolConstants.MaxUsernameLength)
        {
            throw new DrcomConfigurationException(
                $"用户名最多 {ProtocolConstants.MaxUsernameLength} 个字符。");
        }

        if (requirePassword && string.IsNullOrWhiteSpace(file.PasswordProtected))
        {
            throw new DrcomConfigurationException("尚未设置密码。");
        }

        if (string.IsNullOrWhiteSpace(file.AuthServer))
        {
            throw new DrcomConfigurationException("认证服务器不能为空。");
        }

        if (!IPAddress.TryParse(file.AuthServer, out _) && Uri.CheckHostName(file.AuthServer) == UriHostNameType.Unknown)
        {
            throw new DrcomConfigurationException("认证服务器不是有效的 IP 或主机名。");
        }

        if (file.AuthPort is < 1 or > 65535)
        {
            throw new DrcomConfigurationException("认证端口必须在 1..65535 之间。");
        }

        if (file.HeartbeatSeconds is < 5 or > 300)
        {
            throw new DrcomConfigurationException("心跳间隔必须在 5..300 秒之间。");
        }

        if (file.TimeoutSeconds is < 1 or > 30)
        {
            throw new DrcomConfigurationException("超时必须在 1..30 秒之间。");
        }

        if (!Enum.IsDefined(file.PacketMode))
        {
            throw new DrcomConfigurationException("登录包模式无效。");
        }

        if (!string.IsNullOrWhiteSpace(file.Mac) && !TryParseMac(file.Mac, out _))
        {
            throw new DrcomConfigurationException("MAC 地址格式应为 AA-BB-CC-DD-EE-FF。");
        }
    }

    private static void ValidatePassword(string password)
    {
        if (string.IsNullOrEmpty(password))
        {
            throw new DrcomConfigurationException("密码不能为空。");
        }

        if (password.Any(character => character > 0x7F))
        {
            throw new DrcomConfigurationException("密码只能包含 ASCII 字符。");
        }

        if (Encoding.ASCII.GetByteCount(password) > ProtocolConstants.MaxPasswordLength)
        {
            throw new DrcomConfigurationException(
                $"密码最多 {ProtocolConstants.MaxPasswordLength} 个字符（当前协议字段限制）。");
        }
    }

    public static bool TryParseMac(string text, out byte[] mac)
    {
        mac = Array.Empty<byte>();
        var normalized = text.Trim().Replace(':', '-');

        if (normalized.Length != 17)
        {
            return false;
        }

        var parts = normalized.Split('-');
        if (parts.Length != 6)
        {
            return false;
        }

        var bytes = new byte[6];
        for (var i = 0; i < 6; i++)
        {
            if (!byte.TryParse(parts[i], System.Globalization.NumberStyles.HexNumber, null, out bytes[i]))
            {
                return false;
            }
        }

        if (bytes.All(value => value == 0))
        {
            return false;
        }

        mac = bytes;
        return true;
    }

    private static void Normalize(DrcomConfigFile file)
    {
        file.Username = file.Username.Trim();
        file.AuthServer = file.AuthServer.Trim();
        file.Mac = string.IsNullOrWhiteSpace(file.Mac) ? null : file.Mac.Trim().Replace(':', '-').ToUpperInvariant();
        file.InterfaceName = string.IsNullOrWhiteSpace(file.InterfaceName) ? null : file.InterfaceName.Trim();
    }

    private static bool IsAscii(string value) => value.All(character => character <= 0x7F);

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true,
            WriteIndented = true,
        };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        return options;
    }
}
