namespace JluDrcom.Core.Protocol;

/// <summary>A malformed, truncated, or unexpected Dr.COM packet.</summary>
public sealed class DrcomProtocolException : Exception
{
    public DrcomProtocolException(string message) : base(message)
    {
    }

    public DrcomProtocolException(string message, Exception innerException) : base(message, innerException)
    {
    }

    public DrcomProtocolException(byte code, string message) : base(message)
    {
        Code = code;
    }

    public byte? Code { get; }
}

/// <summary>The server rejected the credentials or the account state.</summary>
public sealed class DrcomAuthException : Exception
{
    public DrcomAuthException(byte code, string? serverMessage)
        : base(string.IsNullOrWhiteSpace(serverMessage)
            ? $"Authentication failed (code 0x{code:X2})."
            : $"Authentication failed (code 0x{code:X2}): {serverMessage}")
    {
        Code = code;
        ServerMessage = serverMessage;
    }

    public byte Code { get; }

    public string? ServerMessage { get; }
}

/// <summary>A transient network failure (timeout, socket error, short response).</summary>
public sealed class DrcomConnectionException : Exception
{
    public DrcomConnectionException(string message) : base(message)
    {
    }

    public DrcomConnectionException(string message, Exception innerException) : base(message, innerException)
    {
    }
}

/// <summary>Configuration is missing or invalid.</summary>
public sealed class DrcomConfigurationException : Exception
{
    public DrcomConfigurationException(string message) : base(message)
    {
    }

    public DrcomConfigurationException(string message, Exception innerException) : base(message, innerException)
    {
    }
}
