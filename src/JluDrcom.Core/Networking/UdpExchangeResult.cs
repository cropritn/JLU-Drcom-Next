namespace JluDrcom.Core.Networking;

public readonly record struct UdpExchangeResult(
    bool Success,
    bool TimedOut,
    byte[] Data,
    string? Error)
{
    public static UdpExchangeResult Ok(byte[] data) => new(true, false, data, null);

    public static UdpExchangeResult Timeout() =>
        new(false, true, Array.Empty<byte>(), "UDP 接收超时");

    public static UdpExchangeResult Failure(string error) =>
        new(false, false, Array.Empty<byte>(), error);
}
