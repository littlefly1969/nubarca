using NubArca.Api.Party;

namespace NubArca.Api.Endpoints;

/// <summary>
/// The one place a Party endpoint turns a browser into a guest.
///
/// <para>Every public Party surface goes through here, and none of them
/// implements its own version. That is the whole point: the previous cookie was
/// scoped to <c>/api/party/{capabilityToken}</c>, so the same browser reading
/// the album, uploading a photo and printing a keepsake arrived as three
/// different guests with three different allowances — not because anyone
/// decided that, but because a cookie path said so.</para>
///
/// <para><b>Capability and identity are different things.</b> The token in the
/// route says what this browser may do; it is on a QR code and identifies
/// nobody. The cookie below says who, anonymously, is doing it. Neither is ever
/// used as the other.</para>
///
/// <para><b>Establishing a session may mint; a privileged action may not.</b>
/// Opening a surface, contributing, joining a game — those are a guest arriving,
/// and arriving creates a guest. Casting a vote or spending a finite budget is
/// not arriving, and must never manufacture the identity that authorises it.
/// <see cref="ResolveAsync"/> is that second kind, and it creates nothing.</para>
/// </summary>
internal static class PartyGuestSession
{
    /// <summary>
    /// The browser's Party identity, for the WHOLE public party surface.
    ///
    /// One name and one path, so every capability sees the same cookie. The
    /// value is a random token the server issued and stores nowhere; the row it
    /// resolves to is keyed by a per-link derivation of it, so two parties on
    /// the same browser never share a counter.
    /// </summary>
    internal const string BrowserCookieName = "NubArca.PartyBrowser";

    private const string CookiePath = "/api/party";

    /// <summary>
    /// The pre-migration cookie: one per capability token path, holding a raw
    /// participant token rather than a browser identity. Read only so a party
    /// that is running right now carries its guests across the change; never
    /// written.
    /// </summary>
    internal const string LegacyCookieName = "NubArca.PartyGuest";

    /// <summary>
    /// The guest making this request, created if this browser has not been seen
    /// at this party before. Issues the browser cookie when there is none, and
    /// hands the participant service any legacy cookie the browser still holds
    /// so the old session is carried over rather than replaced.
    /// </summary>
    internal static async Task<Guid?> ResolveOrCreateAsync(
        HttpContext context,
        IPartyParticipantService participants,
        Guid? partyAlbumLinkId,
        CancellationToken cancellationToken)
    {
        if (partyAlbumLinkId is not Guid linkId) return null;

        var browserToken = context.Request.Cookies[BrowserCookieName];
        var issued = false;
        if (!Looks(browserToken))
        {
            browserToken = context.RequestServices
                .GetRequiredService<IPartyGuestIdentity>().NewBrowserToken();
            issued = true;
        }

        var resolution = await participants.ResolveOrCreateAsync(
            linkId, browserToken!, context.Request.Cookies[LegacyCookieName], cancellationToken);

        if (issued) Issue(context, browserToken!);
        return resolution.ParticipantId;
    }

    /// <summary>
    /// The guest making this request, or null. Creates nothing — no participant,
    /// no cookie — so a caller with no identity is told no rather than handed
    /// one.
    /// </summary>
    internal static async Task<Guid?> ResolveAsync(
        HttpContext context,
        IPartyParticipantService participants,
        Guid? partyAlbumLinkId,
        CancellationToken cancellationToken)
    {
        if (partyAlbumLinkId is not Guid linkId) return null;
        return await participants.ResolveAsync(
            linkId, context.Request.Cookies[BrowserCookieName], cancellationToken);
    }

    private static bool Looks(string? token) =>
        token is { Length: 43 } && token.All(c =>
            c is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9' or '-' or '_');

    private static void Issue(HttpContext context, string browserToken) =>
        context.Response.Cookies.Append(BrowserCookieName, browserToken, new CookieOptions
        {
            HttpOnly = true,
            // Secure only over HTTPS: a party is often demoed over plain http on
            // a LAN, and an unconditional Secure flag would silently drop the
            // cookie there — handing every capability a fresh guest again.
            Secure = context.Request.IsHttps,
            SameSite = SameSiteMode.Lax,
            // The whole public party surface, not one capability's path. This
            // single line is what makes one browser one guest.
            Path = CookiePath,
            Expires = DateTimeOffset.UtcNow.AddDays(30),
            IsEssential = true,
        });
}
