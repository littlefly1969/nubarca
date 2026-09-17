using NubArca.Api.Party;

namespace NubArca.Api.Endpoints;

/// <summary>
/// The two cookies Party Crew uses, and the only place either is written.
///
/// <para>They are deliberately different cookies with different paths,
/// different lifetimes and different meanings, and neither is ever read as the
/// other:</para>
///
/// <list type="bullet">
///   <item><b>The challenge</b> lives for ten minutes on
///   <c>/api/party-crew/auth</c>. It is what binds a one-time code to the
///   BROWSER that asked for it, and it authorizes nothing: every route that
///   accepts it is a step of the pairing itself.</item>
///   <item><b>The device</b> lives for a year on <c>/api/party-crew</c>. It is
///   the credential, and it is the only thing a crew request carries.</item>
/// </list>
///
/// <para><b>Both are <c>SameSite=Strict</c>.</b> The guest cookie is Lax
/// because a guest arrives by following a link from WhatsApp and must be the
/// same guest when they land. Party Crew has no such flow: a collaborator opens
/// the invite, pairs, and works inside the app. Strict costs nothing here and
/// removes cross-site navigation as a vector entirely — on top of the
/// same-origin check every unsafe <c>/api</c> request already passes.</para>
///
/// <para><b>Neither value is ever returned in a body.</b> The raw token exists
/// in one HTTP response header and in the browser's cookie jar, and nowhere
/// else — not in JSON, not in a URL, not in localStorage, not in a log.</para>
/// </summary>
internal static class PartyCrewSession
{
    internal const string ChallengeCookieName = "NubArca.PartyCrewChallenge";
    internal const string DeviceCookieName = "NubArca.PartyCrew";

    internal const string ChallengePath = "/api/party-crew/auth";
    internal const string DevicePath = "/api/party-crew";

    internal static string? Challenge(HttpContext context) =>
        Clean(context.Request.Cookies[ChallengeCookieName]);

    internal static string? Device(HttpContext context) =>
        Clean(context.Request.Cookies[DeviceCookieName]);

    internal static void IssueChallenge(HttpContext context, string rawToken) =>
        context.Response.Cookies.Append(
            ChallengeCookieName, rawToken, Options(context, ChallengePath, PartyCrewLimits.ChallengeLifetime));

    internal static void ClearChallenge(HttpContext context) =>
        context.Response.Cookies.Delete(ChallengeCookieName, Options(context, ChallengePath, null));

    internal static void IssueDevice(HttpContext context, string rawToken) =>
        context.Response.Cookies.Append(
            DeviceCookieName, rawToken, Options(context, DevicePath, PartyCrewLimits.DeviceLifetime));

    internal static void ClearDevice(HttpContext context) =>
        context.Response.Cookies.Delete(DeviceCookieName, Options(context, DevicePath, null));

    private static string? Clean(string? value) =>
        PartyCrewTokens.LooksLikeToken(value) ? value : null;

    private static CookieOptions Options(HttpContext context, string path, TimeSpan? lifetime) => new()
    {
        HttpOnly = true,
        // Secure over HTTPS. Not unconditional, for the same reason the guest
        // cookie is not: a party is regularly run over plain http on a venue
        // LAN, and a cookie the browser silently drops there would make Party
        // Crew simply not work — which is not more secure, only broken. In
        // production the public origin is HTTPS and the flag is set.
        Secure = context.Request.IsHttps,
        // See the class remarks: a collaborator never arrives cross-site.
        SameSite = SameSiteMode.Strict,
        Path = path,
        Expires = lifetime is null ? null : DateTimeOffset.UtcNow.Add(lifetime.Value),
        IsEssential = true,
    };
}
