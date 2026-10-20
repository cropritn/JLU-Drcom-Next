namespace JluDrcom.SetupGui.Wired;

public sealed record WiredSettings(
    string IpAddress,
    string SubnetMask,
    string Gateway,
    string Dns1,
    string Dns2);

public sealed record WiredApplyResult(bool Success, string Message);
