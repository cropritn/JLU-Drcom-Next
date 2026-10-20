namespace JluDrcom.Core.Protocol;

/// <summary>
/// How much of the login trailer is emitted.
/// </summary>
public enum LoginPacketMode
{
    /// <summary>
    /// Exactly what the 2018 service sent: 316 + password length bytes, no
    /// checksum/auto-logout/MAC trailer. Kept as a compatibility fallback.
    /// </summary>
    Legacy = 0,

    /// <summary>
    /// The intended packet: a zero-padded checksum, auto-logout bytes, MAC, and
    /// two random bytes. Validated live on JLU (2026-10-20) and now the default
    /// for new configurations.
    /// </summary>
    Complete = 1,
}
