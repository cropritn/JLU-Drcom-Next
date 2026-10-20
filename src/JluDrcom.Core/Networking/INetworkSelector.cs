namespace JluDrcom.Core.Networking;

public interface INetworkSelector
{
    Task<SelectedNetwork> SelectAsync(
        string authServer,
        int authPort,
        string? preferredInterface,
        string? preferredMac,
        CancellationToken cancellationToken);
}
