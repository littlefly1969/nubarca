namespace NubArca.Api.ShareLinks;

// Status buckets for the global GET /api/share-links listing. "All" is the
// default. Note that an exhausted-but-not-expired-not-revoked link is only
// "active"-excluded — it surfaces under All, never under a specific bucket —
// matching the four filters the slice asks for.
public enum ShareLinkStatusFilter
{
    All,
    Active,
    Expired,
    Revoked,
}

// Parser shared between the endpoint and tests. Returns false on unknown
// values so the endpoint can map them to 400 (mirrors ImageSort).
public static class ShareLinkStatus
{
    public const ShareLinkStatusFilter Default = ShareLinkStatusFilter.All;

    public static bool TryParse(string? raw, out ShareLinkStatusFilter status)
    {
        switch (raw?.Trim().ToLowerInvariant())
        {
            case null: case "": case "all": status = ShareLinkStatusFilter.All; return true;
            case "active": status = ShareLinkStatusFilter.Active; return true;
            case "expired": status = ShareLinkStatusFilter.Expired; return true;
            case "revoked": status = ShareLinkStatusFilter.Revoked; return true;
            default: status = Default; return false;
        }
    }
}
