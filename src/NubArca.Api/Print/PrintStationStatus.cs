using NubArca.Api.Domain.Print;

namespace NubArca.Api.Print;

public static class PrintStationStatus
{
    public const string Online = "online";
    public const string Degraded = "degraded";
    public const string Offline = "offline";
    public const string Revoked = "revoked";

    public static string Calculate(
        DateTime? lastSeenAt, DateTime now, bool revoked, bool enabled,
        IEnumerable<string> deviceStates, int onlineSeconds, int offlineSeconds)
    {
        if (revoked || !enabled) return Revoked;
        if (lastSeenAt is null || now - lastSeenAt.Value > TimeSpan.FromSeconds(offlineSeconds))
            return Offline;
        if (now - lastSeenAt.Value > TimeSpan.FromSeconds(onlineSeconds))
            return Degraded;
        var states = deviceStates.ToArray();
        return states.Length > 0 && states.Any(s => s is PrintDeviceStates.Ready or PrintDeviceStates.Busy)
            && states.All(s => s is not PrintDeviceStates.Error and not PrintDeviceStates.Offline)
            ? Online
            : Degraded;
    }
}
