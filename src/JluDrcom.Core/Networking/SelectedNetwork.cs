using System.Net;

namespace JluDrcom.Core.Networking;

/// <summary>The local address/MAC and resolved server address for one session.</summary>
public sealed record SelectedNetwork(
    IPAddress LocalAddress,
    IPAddress RemoteAddress,
    byte[] MacAddress,
    string AdapterName,
    string AdapterId,
    string AdapterDescription);
