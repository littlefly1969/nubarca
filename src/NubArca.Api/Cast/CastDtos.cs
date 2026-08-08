namespace NubArca.Api.Cast;

// What the sender browser receives when a grant is minted.
//
// Every URL is ORIGIN-RELATIVE. The frontend joins them onto the secure origin
// it is itself being served from, so nothing here depends on a Host header a
// caller controls — a spoofed one cannot make NubArca hand a television an
// address pointing somewhere else. The token rides in the query string rather
// than the path: it is a secret, and a path is the part of a URL that everything
// from a proxy log to an error page treats as safe to print.
//
// Never carries: StorageKey, physical path, blob id, sha256, the file name, or
// anything about the owner.
public sealed record CastGrantResponse(
    Guid GrantId,
    DateTime ExpiresAt,
    string ContentPath,
    string PosterPath,
    string ContentType,
    string StreamType,
    string Mode)
{
    // Phase 1 casts complete VOD files only; there is no live contract here.
    public const string BufferedStreamType = "BUFFERED";

    public static CastGrantResponse From(CastGrantSecret grant)
    {
        var basePath = CastGrantService.MediaBasePath(grant.GrantId);
        var query = "?token=" + Uri.EscapeDataString(grant.Token);
        return new CastGrantResponse(
            grant.GrantId,
            grant.ExpiresAt,
            $"{basePath}/video{query}",
            $"{basePath}/poster{query}",
            grant.ContentType,
            BufferedStreamType,
            grant.Mode);
    }
}
