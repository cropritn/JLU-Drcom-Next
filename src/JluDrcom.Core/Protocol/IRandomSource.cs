using System.Security.Cryptography;

namespace JluDrcom.Core.Protocol;

/// <summary>Injectable random source so packet builders are deterministic in tests.</summary>
public interface IRandomSource
{
    void Fill(Span<byte> buffer);
}

public sealed class CryptoRandomSource : IRandomSource
{
    public static CryptoRandomSource Instance { get; } = new();

    public void Fill(Span<byte> buffer) => RandomNumberGenerator.Fill(buffer);
}
