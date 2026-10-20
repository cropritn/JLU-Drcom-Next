namespace JluDrcom.Core.Protocol;

/// <summary>Dr.COM packet type codes observed on the JLU network.</summary>
public enum DrcomCode : byte
{
    StartRequest = 0x01,
    StartResponse = 0x02,
    LoginRequest = 0x03,
    LoginSuccess = 0x04,
    LoginFailure = 0x05,
    LogoutRequest = 0x06,
    KeepAlive = 0x07,
    Message = 0x4D,
    AliveServer = 0xFE,
    AliveClient = 0xFF,
}
