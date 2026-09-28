namespace NubArca.PrintAgent.Networking;

/// <summary>A Wi-Fi network the box can see. Never carries a password.</summary>
public sealed record WifiNetwork(string Ssid, int Signal, bool Secure);

/// <summary>A connection that can actually carry traffic: an address AND a default route.</summary>
public sealed record NetworkUplink(string Device, string Type, string? Name, string? Address);

/// <summary>What NetworkManager reports at one moment.</summary>
public sealed record NetworkSnapshot(
    bool ManagerAvailable,
    string? WifiInterface,
    NetworkUplink? Uplink,
    bool AccessPointActive,
    string? AccessPointAddress)
{
    public static readonly NetworkSnapshot Unavailable = new(false, null, null, false, null);
}

/// <summary>
/// NetworkManager, reduced to what the Print Box needs. The ONLY implementation
/// that touches the system is <see cref="NetworkManagerCli"/>; tests use a fake.
/// </summary>
public interface INetworkManager
{
    Task<NetworkSnapshot> GetSnapshotAsync(CancellationToken cancellationToken);

    /// <summary>Visible networks, strongest first, one entry per SSID.</summary>
    Task<IReadOnlyList<WifiNetwork>> ScanAsync(string wifiInterface, CancellationToken cancellationToken);

    Task<bool> StartAccessPointAsync(string wifiInterface, string ssid, string? password,
        CancellationToken cancellationToken);

    Task StopAccessPointAsync(CancellationToken cancellationToken);

    /// <summary>Creates or replaces this box's client profile for <paramref name="ssid"/> and activates it.</summary>
    Task<bool> ConnectAsync(string wifiInterface, string ssid, string? password,
        CancellationToken cancellationToken);

    /// <summary>Removes this box's client profile for <paramref name="ssid"/>, so a wrong password is not retried at every boot.</summary>
    Task ForgetAsync(string ssid, CancellationToken cancellationToken);
}
