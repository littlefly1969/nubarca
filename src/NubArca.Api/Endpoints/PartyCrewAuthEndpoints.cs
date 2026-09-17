using Microsoft.AspNetCore.Mvc;
using NubArca.Api.Party;

namespace NubArca.Api.Endpoints;

/// <summary>
/// Becoming a Party Crew device: the link, the code, and the two-device limit.
///
/// <para>Every route here is anonymous, because the person using them has no
/// account and never will. What stands in for authentication is the pair of
/// factors the service enforces — the personal link says WHICH collaborator,
/// the emailed code says it is really them — and until both are satisfied
/// nothing on these routes returns anything about the party beyond its name and
/// the role somebody was invited as.</para>
///
/// <para><b>The token arrives in a body, never in the URL.</b> The invite link
/// carries it in the fragment, which browsers do not send to a server, do not
/// write to an access log and do not put in a Referer. The page reads it,
/// removes it from the address bar, and posts it here. A token in a query
/// string would be in the server log of every hop on the way.</para>
///
/// <para><b>Errors say as little as possible.</b> Unknown link, expired link,
/// revoked collaborator, deleted party and wrong party all answer the same
/// thing. Anything more is an oracle for guessing, and none of the distinctions
/// help the person who is legitimately stuck — for whom the answer is always
/// "ask the host for a new link".</para>
/// </summary>
public static class PartyCrewAuthEndpoints
{
    /// <summary>Validating a link. Guessing one is 256 bits of work; this stops the guessing being cheap.</summary>
    public const string InviteRateLimitPolicy = "party-crew-invite";

    /// <summary>Sending a code. Bounded per address, because it spends the operator's mail.</summary>
    public const string CodeRateLimitPolicy = "party-crew-code";

    /// <summary>Typing a code, and freeing a device slot.</summary>
    public const string VerifyRateLimitPolicy = "party-crew-verify";

    public sealed record InviteRequest(string? Token);

    public sealed record VerifyRequest(string? Code);

    public static IEndpointRouteBuilder MapPartyCrewAuthEndpoints(this IEndpointRouteBuilder app)
    {
        // ── 1. The link ─────────────────────────────────────────────────────

        app.MapPost("/api/party-crew/auth/invite", async (
            HttpContext http,
            [FromServices] IPartyCrewAuthService auth,
            [FromBody] InviteRequest? body,
            CancellationToken ct) =>
        {
            NoStore(http);
            var result = await auth.StartAsync(
                body?.Token ?? string.Empty, UserAgent(http), Ip(http), ct);
            if (result.Error is { } error) return Problem(error);

            PartyCrewSession.IssueChallenge(http, result.Value!.RawChallengeToken);
            return Results.Ok(result.Value.View);
        }).WithName("StartPartyCrewPairing").RequireRateLimiting(InviteRateLimitPolicy);

        // ── 2. The code ─────────────────────────────────────────────────────

        // What the browser holding a challenge is in the middle of. Read-only,
        // party-safe, and the reason the code screen survives a refresh.
        app.MapGet("/api/party-crew/auth/challenge", async (
            HttpContext http,
            [FromServices] IPartyCrewAuthService auth,
            CancellationToken ct) =>
        {
            NoStore(http);
            var challenge = PartyCrewSession.Challenge(http);
            if (challenge is null) return Problem(PartyCrewAuthError.Unavailable);
            var result = await auth.ChallengeAsync(challenge, ct);
            return result.Error is { } error ? Problem(error) : Results.Ok(result.Value);
        }).WithName("GetPartyCrewChallenge");

        app.MapPost("/api/party-crew/auth/resend", async (
            HttpContext http,
            [FromServices] IPartyCrewAuthService auth,
            CancellationToken ct) =>
        {
            NoStore(http);
            var challenge = PartyCrewSession.Challenge(http);
            if (challenge is null) return Problem(PartyCrewAuthError.Unavailable);
            var error = await auth.ResendAsync(challenge, ct);
            return error is null ? Results.NoContent() : Problem(error.Value);
        }).WithName("ResendPartyCrewCode").RequireRateLimiting(CodeRateLimitPolicy);

        app.MapPost("/api/party-crew/auth/verify", async (
            HttpContext http,
            [FromServices] IPartyCrewAuthService auth,
            [FromBody] VerifyRequest? body,
            CancellationToken ct) =>
        {
            NoStore(http);
            var challenge = PartyCrewSession.Challenge(http);
            if (challenge is null) return Problem(PartyCrewAuthError.Unavailable);

            var result = await auth.VerifyAsync(
                challenge, body?.Code ?? string.Empty, UserAgent(http),
                PartyCrewSession.Device(http), Ip(http), ct);
            return Settle(http, result);
        }).WithName("VerifyPartyCrewCode").RequireRateLimiting(VerifyRateLimitPolicy);

        // ── 3. The limit ────────────────────────────────────────────────────
        //
        // Reachable only with a VERIFIED challenge, and only about the
        // collaborator that challenge belongs to. The person has already proved
        // they hold the mailbox; being shown their own two devices and allowed
        // to drop one is the whole point of not failing the pairing outright.

        app.MapGet("/api/party-crew/auth/devices", async (
            HttpContext http,
            [FromServices] IPartyCrewAuthService auth,
            CancellationToken ct) =>
        {
            NoStore(http);
            var challenge = PartyCrewSession.Challenge(http);
            if (challenge is null) return Problem(PartyCrewAuthError.Unavailable);
            var result = await auth.ChallengeDevicesAsync(challenge, ct);
            return result.Error is { } error ? Problem(error) : Results.Ok(result.Value);
        }).WithName("ListPartyCrewPairingDevices");

        app.MapDelete("/api/party-crew/auth/devices/{grantId:guid}", async (
            Guid grantId,
            HttpContext http,
            [FromServices] IPartyCrewAuthService auth,
            CancellationToken ct) =>
        {
            NoStore(http);
            var challenge = PartyCrewSession.Challenge(http);
            if (challenge is null) return Problem(PartyCrewAuthError.Unavailable);
            var error = await auth.RevokeDuringChallengeAsync(challenge, grantId, Ip(http), ct);
            return error is null ? Results.NoContent() : Problem(error.Value);
        }).WithName("RevokePartyCrewPairingDevice").RequireRateLimiting(VerifyRateLimitPolicy);

        // Finishing after a slot was freed. No second code: the challenge is
        // already verified, and asking again would charge the person for the
        // product's inability to count to two.
        app.MapPost("/api/party-crew/auth/complete", async (
            HttpContext http,
            [FromServices] IPartyCrewAuthService auth,
            CancellationToken ct) =>
        {
            NoStore(http);
            var challenge = PartyCrewSession.Challenge(http);
            if (challenge is null) return Problem(PartyCrewAuthError.Unavailable);
            var result = await auth.CompleteAsync(
                challenge, UserAgent(http), PartyCrewSession.Device(http), Ip(http), ct);
            return Settle(http, result);
        }).WithName("CompletePartyCrewPairing").RequireRateLimiting(VerifyRateLimitPolicy);

        return app;
    }

    /// <summary>
    /// One place decides what a pairing attempt does to the browser's cookies.
    ///
    /// <para>A device token is set as a cookie and returned nowhere else. The
    /// challenge cookie is cleared the moment it has been spent, so a finished
    /// pairing leaves nothing behind that could start another.</para>
    /// </summary>
    private static IResult Settle(HttpContext http, PartyCrewAuthResult<PartyCrewPairing> result)
    {
        if (result.Error is { } error) return Problem(error);

        var pairing = result.Value!;
        if (pairing.RawDeviceToken is not null)
        {
            PartyCrewSession.IssueDevice(http, pairing.RawDeviceToken);
            PartyCrewSession.ClearChallenge(http);
        }
        return Results.Ok(pairing.Result);
    }

    /// <summary>
    /// The wire form of a refusal: a stable code the UI can speak about, and
    /// nothing else. No counts, no timings, no which-part-was-wrong.
    /// </summary>
    private static IResult Problem(PartyCrewAuthError error) => error switch
    {
        PartyCrewAuthError.InvalidCode => Results.BadRequest(new { error = "invalid_code" }),
        PartyCrewAuthError.TooManyAttempts => Results.StatusCode(StatusCodes.Status429TooManyRequests),
        PartyCrewAuthError.ResendTooSoon => Results.StatusCode(StatusCodes.Status429TooManyRequests),
        PartyCrewAuthError.MailUnavailable => Results.BadRequest(new { error = "mail_unavailable" }),
        PartyCrewAuthError.DeviceLimitReached => Results.BadRequest(new { error = "device_limit" }),
        _ => Results.NotFound(),
    };

    private static string? Ip(HttpContext http) => http.Connection.RemoteIpAddress?.ToString();

    private static string? UserAgent(HttpContext http) => http.Request.Headers.UserAgent.ToString();

    // Everything here is about a credential. None of it belongs in a cache.
    private static void NoStore(HttpContext http) =>
        http.Response.Headers.CacheControl = "no-store";
}
