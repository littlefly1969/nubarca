using Microsoft.AspNetCore.Mvc;
using NubArca.Api.Access;
using NubArca.Api.Audit;
using NubArca.Api.Domain;
using NubArca.Api.Http;
using NubArca.Api.Party;

namespace NubArca.Api.Endpoints;

/// <summary>
/// Who arrived — two surfaces with two authorities, writing one fact.
///
/// <para>The HOST's routes live under <c>/api/parties/{partyId}</c>, require
/// <c>party.access</c> and are owner-scoped in every query: the whole list, the
/// counts, every guest and every other arrival, recorded and corrected while the
/// party is live and after it.</para>
///
/// <para>The GUEST's live under <c>/api/party-invitations/{token}</c> and reach
/// exactly the group that token opens: "Sono qui" for one of its own people, and
/// taking its own "Sono qui" back, only while the party is live. They answer
/// with the invitation view — the group's own people and their own arrivals —
/// and never with the host's list, its counts, another group or anybody recorded
/// at the door. There is deliberately nothing under <c>/api/party/{token}</c>:
/// the party's QR is a browser at the party, not an arrival.</para>
///
/// <para>Audit lines name the party, the guest or recorded person by id, and the
/// source of a check-in — never a name. Only a request that changed something
/// is an event: a repeated check-in, a second undo or a replayed add records
/// nothing.</para>
/// </summary>
public static class PartyAttendanceEndpoints
{
    /// <summary>One "add", minted by the host's browser and reused for its retries.</summary>
    public sealed record OtherGuestCreateRequest(string? Name, Guid ClientRequestId);

    public sealed record OtherGuestUpdateRequest(string? Name, int Version);

    public static IEndpointRouteBuilder MapPartyAttendanceEndpoints(this IEndpointRouteBuilder app)
    {
        // --- OWNER --------------------------------------------------------------

        app.MapGet("/api/parties/{partyId:guid}/attendance", async (
            Guid partyId,
            HttpContext httpContext,
            [FromServices] IPartyAttendanceService attendance,
            CancellationToken cancellationToken) =>
        {
            SetNoStore(httpContext);
            var result = await attendance.GetAsync(httpContext.GetCurrentUserId()!.Value, partyId, cancellationToken);
            return result is null ? Results.NotFound() : Results.Ok(result);
        }).WithName("GetPartyAttendance").RequirePermission(Permissions.PartyAccess);

        // PUT: "this person arrived". Stating it twice is stating it once.
        app.MapPut("/api/parties/{partyId:guid}/attendance/guests/{guestId:guid}", async (
            Guid partyId,
            Guid guestId,
            HttpContext httpContext,
            [FromServices] IPartyAttendanceService attendance,
            [FromServices] IAuditLogger audit,
            CancellationToken cancellationToken) =>
        {
            SetNoStore(httpContext);
            var ownerUserId = httpContext.GetCurrentUserId()!.Value;
            var result = await attendance.CheckInGuestAsync(ownerUserId, partyId, guestId, cancellationToken);
            if (result.Changed)
            {
                await AuditAsync(httpContext, audit, ownerUserId, AuditActions.PartyAttendanceCheckIn, partyId,
                    new { guestId, source = PartyAttendanceSources.Owner }, cancellationToken);
            }
            return ToResult(result, PreferHeader.WantsMinimal(httpContext), guestId: guestId);
        }).WithName("CheckInPartyGuest").RequirePermission(Permissions.PartyAccess);

        // DELETE: "that arrival was recorded by mistake" — never "they left".
        app.MapDelete("/api/parties/{partyId:guid}/attendance/guests/{guestId:guid}", async (
            Guid partyId,
            Guid guestId,
            HttpContext httpContext,
            [FromServices] IPartyAttendanceService attendance,
            [FromServices] IAuditLogger audit,
            CancellationToken cancellationToken) =>
        {
            SetNoStore(httpContext);
            var ownerUserId = httpContext.GetCurrentUserId()!.Value;
            var result = await attendance.UndoGuestCheckInAsync(ownerUserId, partyId, guestId, cancellationToken);
            if (result.Changed)
            {
                await AuditAsync(httpContext, audit, ownerUserId, AuditActions.PartyAttendanceUndo, partyId,
                    new { guestId, source = PartyAttendanceSources.Owner }, cancellationToken);
            }
            return ToResult(result, PreferHeader.WantsMinimal(httpContext), guestId: guestId);
        }).WithName("UndoPartyGuestCheckIn").RequirePermission(Permissions.PartyAccess);

        app.MapPost("/api/parties/{partyId:guid}/attendance/other-guests", async (
            Guid partyId,
            HttpContext httpContext,
            [FromServices] IPartyAttendanceService attendance,
            [FromServices] IAuditLogger audit,
            [FromBody] OtherGuestCreateRequest? body,
            CancellationToken cancellationToken) =>
        {
            SetNoStore(httpContext);
            if (body is null) return Results.BadRequest(new { error = "invalid_request" });
            var ownerUserId = httpContext.GetCurrentUserId()!.Value;
            var result = await attendance.CreateOtherGuestAsync(
                ownerUserId, partyId, body.Name, body.ClientRequestId, cancellationToken);
            if (result.Changed)
            {
                await AuditAsync(httpContext, audit, ownerUserId, AuditActions.PartyAttendanceOtherCreate, partyId,
                    new { attendanceGuestId = result.AttendanceGuestId }, cancellationToken);
            }
            return ToResult(result, PreferHeader.WantsMinimal(httpContext), otherGuestId: result.AttendanceGuestId);
        }).WithName("CreatePartyAttendanceGuest").RequirePermission(Permissions.PartyAccess);

        app.MapPut("/api/parties/{partyId:guid}/attendance/other-guests/{attendanceGuestId:guid}", async (
            Guid partyId,
            Guid attendanceGuestId,
            HttpContext httpContext,
            [FromServices] IPartyAttendanceService attendance,
            [FromServices] IAuditLogger audit,
            [FromBody] OtherGuestUpdateRequest? body,
            CancellationToken cancellationToken) =>
        {
            SetNoStore(httpContext);
            if (body is null) return Results.BadRequest(new { error = "invalid_request" });
            var ownerUserId = httpContext.GetCurrentUserId()!.Value;
            var result = await attendance.UpdateOtherGuestAsync(
                ownerUserId, partyId, attendanceGuestId, body.Name, body.Version, cancellationToken);
            if (result.Changed)
            {
                await AuditAsync(httpContext, audit, ownerUserId, AuditActions.PartyAttendanceOtherUpdate, partyId,
                    new { attendanceGuestId }, cancellationToken);
            }
            return ToResult(result, PreferHeader.WantsMinimal(httpContext), otherGuestId: attendanceGuestId);
        }).WithName("UpdatePartyAttendanceGuest").RequirePermission(Permissions.PartyAccess);

        app.MapDelete("/api/parties/{partyId:guid}/attendance/other-guests/{attendanceGuestId:guid}", async (
            Guid partyId,
            Guid attendanceGuestId,
            HttpContext httpContext,
            [FromServices] IPartyAttendanceService attendance,
            [FromServices] IAuditLogger audit,
            CancellationToken cancellationToken) =>
        {
            SetNoStore(httpContext);
            var ownerUserId = httpContext.GetCurrentUserId()!.Value;
            var result = await attendance.DeleteOtherGuestAsync(ownerUserId, partyId, attendanceGuestId, cancellationToken);
            if (result.Changed)
            {
                await AuditAsync(httpContext, audit, ownerUserId, AuditActions.PartyAttendanceOtherDelete, partyId,
                    new { attendanceGuestId }, cancellationToken);
            }
            return ToResult(result, PreferHeader.WantsMinimal(httpContext));
        }).WithName("DeletePartyAttendanceGuest").RequirePermission(Permissions.PartyAccess);

        // --- GUEST (the personal invitation token) ------------------------------
        //
        // "Sono qui" and its undo. The token resolves exactly as the invitation's
        // own routes resolve it — unknown, rotated, removed, Draft, closed and
        // permission-revoked are one generic 404 — and the guest id must belong
        // to that token's group. It mints no participant, reads none and sets no
        // cookie: an invitation holder is not a browser at the party.

        app.MapPut("/api/party-invitations/{token}/attendance/guests/{guestId:guid}", async (
            string token,
            Guid guestId,
            HttpContext httpContext,
            [FromServices] IPartyRsvpService rsvp,
            [FromServices] IPartyAttendanceService attendance,
            [FromServices] IAuditLogger audit,
            CancellationToken cancellationToken) =>
        {
            SetNoStore(httpContext);
            var access = await rsvp.ResolveAsync(token, cancellationToken);
            if (access is null) return Results.NotFound();
            var result = await attendance.CheckInFromInvitationAsync(access, guestId, cancellationToken);
            if (result.Changed)
            {
                await AuditAsync(httpContext, audit, null, AuditActions.PartyAttendanceCheckIn, access.PartyId,
                    new { guestId, source = PartyAttendanceSources.Invitation }, cancellationToken);
            }
            return await ToInvitationResultAsync(result, access, token, rsvp, cancellationToken);
        }).WithName("SelfCheckInPartyGuest").RequireRateLimiting(PartyInvitationEndpoints.RsvpRateLimitPolicy);

        app.MapDelete("/api/party-invitations/{token}/attendance/guests/{guestId:guid}", async (
            string token,
            Guid guestId,
            HttpContext httpContext,
            [FromServices] IPartyRsvpService rsvp,
            [FromServices] IPartyAttendanceService attendance,
            [FromServices] IAuditLogger audit,
            CancellationToken cancellationToken) =>
        {
            SetNoStore(httpContext);
            var access = await rsvp.ResolveAsync(token, cancellationToken);
            if (access is null) return Results.NotFound();
            var result = await attendance.UndoFromInvitationAsync(access, guestId, cancellationToken);
            if (result.Changed)
            {
                await AuditAsync(httpContext, audit, null, AuditActions.PartyAttendanceUndo, access.PartyId,
                    new { guestId, source = PartyAttendanceSources.Invitation }, cancellationToken);
            }
            return await ToInvitationResultAsync(result, access, token, rsvp, cancellationToken);
        }).WithName("UndoSelfCheckInPartyGuest").RequireRateLimiting(PartyInvitationEndpoints.RsvpRateLimitPolicy);

        return app;
    }

    private static Task AuditAsync(
        HttpContext httpContext, IAuditLogger audit, Guid? userId, string action, Guid partyId,
        object metadata, CancellationToken cancellationToken) =>
        audit.LogAsync(
            userId, action, AuditEntityTypes.Party, partyId,
            httpContext.Connection.RemoteIpAddress?.ToString(), metadata, cancellationToken);

    /// <summary>
    /// Under <c>Prefer: return=minimal</c> — the guest console at the door —
    /// only what changed: whether it did, the counts, and the one person the
    /// call was about as the attendance now reads them. A tap on "Segna
    /// arrivato" must not download every guest of a thousand-group party.
    /// </summary>
    internal static IResult ToResult(
        PartyAttendanceResult result, bool minimal, Guid? guestId = null, Guid? otherGuestId = null)
    {
        if (!minimal) return ToResult(result);
        var attendance = result.Attendance;
        return result.Outcome switch
        {
            PartyAttendanceOutcome.Ok => Results.Ok(new
            {
                changed = result.Changed,
                summary = attendance?.Summary,
                guest = guestId is Guid id
                    ? attendance?.Groups.SelectMany(g => g.Guests).FirstOrDefault(g => g.GuestId == id)
                    : null,
                otherGuest = otherGuestId is Guid other
                    ? attendance?.OtherGuests.FirstOrDefault(o => o.Id == other)
                    : null,
            }),
            PartyAttendanceOutcome.NotFound => Results.NotFound(),
            PartyAttendanceOutcome.InvalidRequest => Results.BadRequest(new { error = result.Error ?? "invalid_request" }),
            _ => Results.Json(
                new { error = result.Error ?? "conflict", summary = attendance?.Summary },
                statusCode: StatusCodes.Status409Conflict),
        };
    }

    internal static IResult ToResult(PartyAttendanceResult result) => result.Outcome switch
    {
        PartyAttendanceOutcome.Ok => Results.Ok(result.Attendance),
        PartyAttendanceOutcome.NotFound => Results.NotFound(),
        PartyAttendanceOutcome.InvalidRequest => Results.BadRequest(new { error = result.Error ?? "invalid_request" }),
        // Every other refusal describes a state — the party has not started, the
        // name changed meanwhile — and carries the list as it is now so the page
        // adopts it instead of overwriting it.
        _ => Results.Json(
            new { error = result.Error ?? "conflict", attendance = result.Attendance },
            statusCode: StatusCodes.Status409Conflict),
    };

    // The guest is answered with its OWN invitation as it is now — its people,
    // their answers and their own arrivals — in success and in refusal alike.
    private static async Task<IResult> ToInvitationResultAsync(
        PartyAttendanceResult result, PartyInvitationAccess access, string token, IPartyRsvpService rsvp,
        CancellationToken cancellationToken)
    {
        if (result.Outcome == PartyAttendanceOutcome.NotFound) return Results.NotFound();
        var view = await rsvp.ViewAsync(access, token, cancellationToken);
        return result.Outcome == PartyAttendanceOutcome.Ok
            ? Results.Ok(view)
            : Results.Json(
                new { error = result.Error ?? "conflict", invitation = view },
                statusCode: StatusCodes.Status409Conflict);
    }

    private static void SetNoStore(HttpContext context)
    {
        context.Response.Headers.CacheControl = "no-store";
        context.Response.Headers.Pragma = "no-cache";
    }
}
