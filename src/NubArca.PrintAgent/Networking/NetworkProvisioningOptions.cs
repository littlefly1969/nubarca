using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace NubArca.PrintAgent.Networking;

/// <summary>
/// The headless Print Box's own Wi-Fi setup. Linux with NetworkManager only; off
/// unless an installation turns it on.
/// </summary>
public sealed partial class NetworkProvisioningOptions
{
    public bool Enabled { get; set; }

    /// <summary>
    /// How long NetworkManager is given to find a known network — at boot, and
    /// after a working network is lost — before the setup network opens.
    /// </summary>
    public int ConnectionGraceSeconds { get; set; } = 30;

    /// <summary>The setup network is named <c>&lt;prefix&gt;-XXXX</c>, XXXX stable per box.</summary>
    public string AccessPointPrefix { get; set; } = "NubArca-Print";

    /// <summary>
    /// WPA2 password of the setup network, 8–63 characters. Empty opens it: fine
    /// for a first test, logged as a warning, wrong for an installation.
    /// </summary>
    public string AccessPointPassword { get; set; } = string.Empty;

    public int WebPort { get; set; } = 8080;

    [GeneratedRegex("^[A-Za-z0-9-]{1,26}$", RegexOptions.CultureInvariant)]
    private static partial Regex Prefix();

    public void Validate(bool isLinux)
    {
        if (!Enabled) return;
        if (!isLinux)
            throw new InvalidOperationException(
                "PrintAgent:NetworkProvisioning requires Linux with NetworkManager; disable it on this platform.");
        if (ConnectionGraceSeconds is < 5 or > 600)
            throw new InvalidOperationException(
                "PrintAgent:NetworkProvisioning:ConnectionGraceSeconds must be between 5 and 600.");
        if (!Prefix().IsMatch(AccessPointPrefix ?? string.Empty))
            throw new InvalidOperationException(
                "PrintAgent:NetworkProvisioning:AccessPointPrefix must be 1–26 letters, digits or hyphens.");
        AccessPointPassword ??= string.Empty;
        if (AccessPointPassword.Length > 0
            && (AccessPointPassword.Length is < 8 or > 63 || AccessPointPassword.Any(c => c is < ' ' or > '~')))
            throw new InvalidOperationException(
                "PrintAgent:NetworkProvisioning:AccessPointPassword must be empty or 8–63 printable ASCII characters.");
        if (WebPort is < 1024 or > 65535)
            throw new InvalidOperationException(
                "PrintAgent:NetworkProvisioning:WebPort must be between 1024 and 65535.");
    }
}

/// <summary>
/// The four characters that tell one box's setup network from another's.
///
/// Derived from a hash of the machine id, never from the id itself or anything
/// random: the same box shows the same name at every boot, and the name reveals
/// nothing about the machine.
/// </summary>
public static class BoxIdentity
{
    public const string MachineIdPath = "/etc/machine-id";

    public static string Suffix(string? machineId)
    {
        var seed = string.IsNullOrWhiteSpace(machineId) ? Environment.MachineName : machineId.Trim();
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes("nubarca-print-box:" + seed));
        return Convert.ToHexString(hash, 0, 2);
    }

    public static string ReadSuffix(string path = MachineIdPath)
    {
        try { return Suffix(File.Exists(path) ? File.ReadAllText(path) : null); }
        catch (IOException) { return Suffix(null); }
        catch (UnauthorizedAccessException) { return Suffix(null); }
    }
}
