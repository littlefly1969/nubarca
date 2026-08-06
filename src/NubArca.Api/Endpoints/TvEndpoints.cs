using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using NubArca.Api.Aesthetics;
using NubArca.Api.Albums;
using NubArca.Api.Audit;
using NubArca.Api.Domain;
using NubArca.Api.Files;
using NubArca.Api.Http;
using NubArca.Api.Metadata;
using NubArca.Api.Plates;
using NubArca.Api.Security;
using NubArca.Api.Tv;

namespace NubArca.Api.Endpoints;

// Extracted verbatim from Program.cs (modular-monolith cleanup, not a service
// split — same process, same DI container, same middleware pipeline). Route
// paths, HTTP methods, endpoint names, authorization/anonymous metadata,
// rate limits, status codes, DTOs, and audit behavior are unchanged from the
// original inline mappings.
//
// TV feature surface: paired-TV pairing/session, owner-side TV device
// management, TV Personal Area (PIN management + TV-session-authenticated
// personal gallery/videos/media/aesthetics), and TV Party-album browsing +
// media delivery. All of this was one single contiguous block in Program.cs
// (immediately after the auth endpoints), so it consolidates into one
// MapTvEndpoints() call with no route-ordering concerns.
public static class TvEndpoints
{
    // Mirrors the top-level rate-limit-policy constants still defined in
    // Program.cs for the (untouched) rate limiter policy registration —
    // duplicated here only as the literal policy names, not new policies.
    private const string TvPairingStartRateLimitPolicy = "tv-pairing-start";
    private const string TvPersonalUnlockRateLimitPolicy = "tv-personal-unlock";
    private const string TvPersonalInterpretRateLimitPolicy = "tv-personal-interpret";

    public static IEndpointRouteBuilder MapTvEndpoints(this IEndpointRouteBuilder app)
    {
        // TV pairing is deliberately separate from cookie authentication. The pairing
        // secret proves possession of the QR/start response; only the normal user cookie
        // can approve. The resulting cookie is path-scoped to /api/tv and is resolved
        // manually by these endpoints, so it cannot authenticate any owner API.
        app.MapPost("/api/tv/pairing/start", async (
            HttpContext httpContext,
            [FromServices] ITvPairingService tv,
            CancellationToken cancellationToken) =>
        {
            SetNoStore(httpContext);
            var origin = $"{httpContext.Request.Scheme}://{httpContext.Request.Host}";
            return Results.Ok(await tv.StartAsync(origin, cancellationToken));
        }).WithName("StartTvPairing").RequireRateLimiting(TvPairingStartRateLimitPolicy);

        app.MapGet("/api/tv/pairing/{publicCode}/status", async (
            string publicCode,
            HttpContext httpContext,
            [FromServices] ITvPairingService tv,
            CancellationToken cancellationToken) =>
        {
            SetNoStore(httpContext);
            var secret = httpContext.Request.Headers[TvPairingService.PairingSecretHeader].ToString();
            var result = await tv.PollAsync(
                publicCode, secret, httpContext.Request.Headers.UserAgent.ToString(), cancellationToken);
            if (result is null)
            {
                return Results.NotFound();
            }

            if (result.NewSessionToken is not null && result.SessionExpiresAt is DateTime expiresAt)
            {
                AppendTvSessionCookie(httpContext, result.NewSessionToken, expiresAt);
            }
            return Results.Ok(result.Response);
        }).WithName("GetTvPairingStatus");

        // Atomic approval: an owner WITHOUT a Personal Area PIN must create it in this
        // same call — the PIN row and the approval commit together server-side, so an
        // abandoned/failed PIN step leaves the pairing pending (no Party, no Personal
        // Area, no partial association). An owner WITH a PIN approves normally; PIN
        // fields are ignored (never replaced from the pairing flow).
        app.MapPost("/api/tv/pairing/{publicCode}/approve", async (
            string publicCode,
            [FromBody] TvPairingApprovalRequest? body,
            HttpContext httpContext,
            [FromServices] ITvPairingService tv,
            [FromServices] IAuditLogger audit,
            CancellationToken cancellationToken) =>
        {
            SetNoStore(httpContext);
            var ownerUserId = httpContext.GetCurrentUserId()!.Value;
            var result = await tv.ApproveAsync(
                publicCode, body?.PairingSecret, ownerUserId,
                body?.PersonalPin, body?.PersonalPinConfirmation, cancellationToken);
            switch (result.Status)
            {
                case TvPairingApproveStatus.NotFound:
                    return Results.NotFound();
                case TvPairingApproveStatus.PinRequired:
                    return Results.BadRequest(new { error = "pin_required" });
                case TvPairingApproveStatus.InvalidPin:
                    return Results.BadRequest(new { error = "invalid_pin" });
                case TvPairingApproveStatus.PinMismatch:
                    return Results.BadRequest(new { error = "pin_mismatch" });
            }

            await audit.LogAsync(ownerUserId, AuditActions.TvPairingApprove, AuditEntityTypes.TvPairing,
                result.PairingId, httpContext.Connection.RemoteIpAddress?.ToString(),
                new { pinCreated = result.PinCreated }, cancellationToken);
            if (result.PinCreated)
            {
                await audit.LogAsync(ownerUserId, AuditActions.TvPersonalPinCreate, AuditEntityTypes.User,
                    ownerUserId, httpContext.Connection.RemoteIpAddress?.ToString(), null, cancellationToken);
            }
            return Results.Ok(result.Response);
        }).WithName("ApproveTvPairing").RequireAuthorization();

        app.MapGet("/api/tv/session", async (
            HttpContext httpContext,
            [FromServices] ITvPairingService tv,
            CancellationToken cancellationToken) =>
        {
            SetNoStore(httpContext);
            var result = await tv.GetSessionAsync(
                httpContext.Request.Cookies[TvPairingService.CookieName], heartbeat: false, cancellationToken);
            return result is null ? Results.Unauthorized() : Results.Ok(result);
        }).WithName("GetTvSession");

        app.MapPost("/api/tv/session/heartbeat", async (
            HttpContext httpContext,
            [FromServices] ITvPairingService tv,
            CancellationToken cancellationToken) =>
        {
            SetNoStore(httpContext);
            var result = await tv.GetSessionAsync(
                httpContext.Request.Cookies[TvPairingService.CookieName], heartbeat: true, cancellationToken);
            return result is null ? Results.Unauthorized() : Results.Ok(result);
        }).WithName("HeartbeatTvSession");

        app.MapDelete("/api/tv/session", async (
            HttpContext httpContext,
            [FromServices] ITvPairingService tv,
            CancellationToken cancellationToken) =>
        {
            SetNoStore(httpContext);
            var token = httpContext.Request.Cookies[TvPairingService.CookieName];
            var revoked = await tv.RevokeSessionAsync(token, cancellationToken);
            DeleteTvSessionCookie(httpContext);
            return revoked ? Results.NoContent() : Results.Unauthorized();
        }).WithName("RevokeTvSession");

        // --- Owner-side TV device management ---
        // These are OWNER endpoints (normal auth cookie), deliberately NOT under the
        // limited /api/tv path (so the path-scoped TV cookie is never even sent here).
        // An owner lists and revokes only their own paired TV sessions. Revoke takes
        // effect immediately: the limited session's ResolveOwnerUserIdAsync/GetSession
        // re-check RevokedAt on every call, so all /api/tv endpoints then fail.
        app.MapGet("/api/tv-devices", async (
            HttpContext httpContext,
            [FromServices] ITvPairingService tv,
            CancellationToken cancellationToken) =>
        {
            var ownerUserId = httpContext.GetCurrentUserId()!.Value;
            return Results.Ok(await tv.ListOwnerSessionsAsync(ownerUserId, cancellationToken));
        }).WithName("ListTvDevices").RequireAuthorization();

        app.MapDelete("/api/tv-devices/{sessionId:guid}", async (
            Guid sessionId,
            HttpContext httpContext,
            [FromServices] ITvPairingService tv,
            [FromServices] IAuditLogger audit,
            CancellationToken cancellationToken) =>
        {
            var ownerUserId = httpContext.GetCurrentUserId()!.Value;
            var ip = httpContext.Connection.RemoteIpAddress?.ToString();

            var revoked = await tv.RevokeOwnerSessionAsync(ownerUserId, sessionId, cancellationToken);
            if (!revoked)
            {
                return Results.NotFound();
            }

            await audit.LogAsync(ownerUserId, AuditActions.TvSessionRevoke, AuditEntityTypes.TvSession,
                sessionId, ip, null, cancellationToken);
            return Results.NoContent();
        }).WithName("RevokeTvDevice").RequireAuthorization();

        // --- TV Personal Area: owner-side PIN management ---
        // OWNER endpoints (normal auth cookie), deliberately NOT under /api/tv (the
        // path-scoped TV cookie is never sent here, and the TV session can never call
        // them). The PIN is created exactly once from the authenticated pairing/approval
        // flow; it is never chosen on the TV and never returned by any endpoint.

        app.MapGet("/api/tv-personal/pin", async (
            HttpContext httpContext,
            [FromServices] ITvPersonalAreaService personal,
            CancellationToken cancellationToken) =>
        {
            SetNoStore(httpContext);
            var ownerUserId = httpContext.GetCurrentUserId()!.Value;
            return Results.Ok(await personal.GetPinStatusAsync(ownerUserId, cancellationToken));
        }).WithName("GetTvPersonalPinStatus").RequireAuthorization();

        // Owner-authenticated set/change/reset. No old PIN is required (the owner
        // session IS the authorization) and no "would the old PIN have matched" signal
        // exists. Changing the PIN atomically bumps the generation, revokes every
        // outstanding unlock grant of this owner (all TVs must re-enter the new PIN),
        // and clears the owner's TV-session cooldown state. The TV pairing itself is
        // NOT revoked — Party keeps working.
        app.MapPost("/api/tv-personal/pin", async (
            [FromBody] TvPersonalPinSetRequest? body,
            HttpContext httpContext,
            [FromServices] ITvPersonalAreaService personal,
            [FromServices] IAuditLogger audit,
            CancellationToken cancellationToken) =>
        {
            SetNoStore(httpContext);
            var ownerUserId = httpContext.GetCurrentUserId()!.Value;
            var result = await personal.SetPinAsync(
                ownerUserId, body?.Pin, body?.ConfirmPin, cancellationToken);
            switch (result.Outcome)
            {
                case TvPersonalPinSetOutcome.InvalidPin:
                    return Results.BadRequest(new { error = "invalid_pin" });
                case TvPersonalPinSetOutcome.PinMismatch:
                    return Results.BadRequest(new { error = "pin_mismatch" });
            }

            await audit.LogAsync(
                ownerUserId,
                result.Outcome == TvPersonalPinSetOutcome.Created
                    ? AuditActions.TvPersonalPinCreate
                    : AuditActions.TvPersonalPinChange,
                AuditEntityTypes.User, ownerUserId,
                httpContext.Connection.RemoteIpAddress?.ToString(),
                new { grantsRevoked = result.GrantsRevoked }, cancellationToken);
            return Results.Ok(new TvPersonalPinStatusDto(true, result.UpdatedAt));
        }).WithName("SetTvPersonalPin")
            .RequireAuthorization()
            .RequireRateLimiting(TvPersonalUnlockRateLimitPolicy);

        // --- TV Personal Area: TV-session endpoints ---
        // The paired TV session authenticates the DEVICE; the 6-digit PIN is a second
        // local authorization step. Personal access requires BOTH: the limited TV
        // session cookie AND a server-side unlock grant presented in the
        // X-Tv-Personal-Unlock header (opaque; only its hash is stored, bound to this
        // session + owner + PIN generation). Wrong PIN, malformed PIN, and "no PIN
        // configured" collapse into ONE generic 403 so a TV session cannot probe
        // PIN/account state; the progressive per-session cooldown answers 429 with
        // Retry-After. Everything is no-store: no personal payload may be cached.

        app.MapPost("/api/tv/personal/unlock", async (
            [FromBody] TvPersonalUnlockRequest? body,
            HttpContext httpContext,
            [FromServices] ITvPersonalAreaService personal,
            [FromServices] IAuditLogger audit,
            CancellationToken cancellationToken) =>
        {
            SetNoStore(httpContext);
            var ip = httpContext.Connection.RemoteIpAddress?.ToString();
            var outcome = await personal.UnlockAsync(
                httpContext.Request.Cookies[TvPairingService.CookieName], body?.Pin, cancellationToken);
            switch (outcome.Status)
            {
                case TvPersonalUnlockStatus.SessionInvalid:
                    return Results.Unauthorized();
                case TvPersonalUnlockStatus.Throttled:
                    httpContext.Response.Headers.RetryAfter =
                        outcome.RetryAfterSeconds!.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
                    return Results.StatusCode(StatusCodes.Status429TooManyRequests);
                case TvPersonalUnlockStatus.Invalid:
                    await audit.LogAsync(outcome.OwnerUserId, AuditActions.TvPersonalUnlockFailure,
                        AuditEntityTypes.TvSession, outcome.TvSessionId, ip, null, cancellationToken);
                    return Results.Json(new { error = "invalid" }, statusCode: StatusCodes.Status403Forbidden);
            }

            await audit.LogAsync(outcome.OwnerUserId, AuditActions.TvPersonalUnlock,
                AuditEntityTypes.TvSession, outcome.TvSessionId, ip, null, cancellationToken);
            return Results.Ok(outcome.Grant);
        }).WithName("UnlockTvPersonal").RequireRateLimiting(TvPersonalUnlockRateLimitPolicy);

        app.MapPost("/api/tv/personal/lock", async (
            HttpContext httpContext,
            [FromServices] ITvPersonalAreaService personal,
            [FromServices] IAuditLogger audit,
            CancellationToken cancellationToken) =>
        {
            SetNoStore(httpContext);
            var lockedSessionId = await personal.LockAsync(
                httpContext.Request.Cookies[TvPairingService.CookieName], cancellationToken);
            if (lockedSessionId is null)
            {
                return Results.Unauthorized();
            }

            await audit.LogAsync(null, AuditActions.TvPersonalLock, AuditEntityTypes.TvSession,
                lockedSessionId, httpContext.Connection.RemoteIpAddress?.ToString(), null, cancellationToken);
            return Results.NoContent();
        }).WithName("LockTvPersonal");

        app.MapGet("/api/tv/personal/status", async (
            HttpContext httpContext,
            [FromServices] ITvPersonalAreaService personal,
            CancellationToken cancellationToken) =>
        {
            SetNoStore(httpContext);
            var status = await personal.GetStatusAsync(
                httpContext.Request.Cookies[TvPairingService.CookieName],
                httpContext.Request.Headers[TvPersonalAreaService.UnlockHeader].ToString(),
                cancellationToken);
            return status is null ? Results.Unauthorized() : Results.Ok(status);
        }).WithName("GetTvPersonalStatus");

        app.MapGet("/api/tv/personal/home", async (
            HttpContext httpContext,
            [FromServices] ITvPersonalAreaService personal,
            CancellationToken cancellationToken) =>
        {
            SetNoStore(httpContext);
            var access = await personal.ResolveAccessAsync(
                httpContext.Request.Cookies[TvPairingService.CookieName],
                httpContext.Request.Headers[TvPersonalAreaService.UnlockHeader].ToString(),
                cancellationToken);
            if (access.Status == TvPersonalAccessStatus.SessionInvalid)
            {
                return Results.Unauthorized();
            }
            if (access.Status == TvPersonalAccessStatus.GrantStalePinChanged)
            {
                // The owner changed the PIN: the client drops to mode selection and
                // shows the "PIN was changed" notice (pairing stays valid). No hash,
                // generation, or grant details are revealed.
                return Results.Json(new { error = "pin_changed" }, statusCode: StatusCodes.Status403Forbidden);
            }
            if (access.Status == TvPersonalAccessStatus.GrantInvalid)
            {
                return Results.Json(new { error = "locked" }, statusCode: StatusCodes.Status403Forbidden);
            }

            var home = await personal.GetHomeAsync(access.OwnerUserId!.Value, cancellationToken);
            return home is null ? Results.Unauthorized() : Results.Ok(home);
        }).WithName("GetTvPersonalHome");

        // --- TV Personal Gallery (grant-gated projection of the owner image gallery) ---
        // EVERY endpoint below re-resolves the limited TV session cookie AND the
        // Personal Area unlock grant on each call (ResolveTvPersonalAccessAsync):
        // 401 = session invalid; 403 pin_changed = stale PIN generation; 403 locked =
        // no/invalid grant. Query semantics are the shared web-gallery ones
        // (GalleryQueryParser + ListImagesPageAsync); DTOs and media bytes are TV-safe
        // projections — derived artifacts only, never originals, never storage/AI
        // internals. JSON responses are no-store; derived bytes use the same private
        // browser-cache policy as every other authorized derivative endpoint.

        app.MapGet("/api/tv/personal/gallery", async (
            [FromQuery] int? limit,
            [FromQuery] string? cursor,
            [FromQuery] string? q,
            [FromQuery] string? sort,
            [FromQuery] string? direction,
            [FromQuery] bool? favorite,
            [FromQuery] int? minRating,
            [FromQuery] bool? hasGps,
            [FromQuery] DateTime? dateTakenFrom,
            [FromQuery] DateTime? dateTakenTo,
            [FromQuery] bool? collapseDuplicates,
            [FromQuery] string? includePeople,
            [FromQuery] string? excludePeople,
            [FromQuery] string? includePeopleMode,
            [FromQuery] string? semanticQuery,
            [FromQuery] int? semanticTopK,
            HttpContext httpContext,
            [FromServices] ITvPersonalAreaService personal,
            [FromServices] ITvPersonalGalleryService gallery,
            CancellationToken cancellationToken) =>
        {
            SetNoStore(httpContext);
            var (failure, ownerUserId) = await ResolveTvPersonalAccessAsync(httpContext, personal, cancellationToken);
            if (failure is not null)
            {
                return failure;
            }

            var result = await gallery.ListGalleryAsync(
                ownerUserId,
                new TvPersonalGalleryQuery(
                    limit, cursor, q, sort, direction, favorite, minRating, hasGps,
                    dateTakenFrom, dateTakenTo, collapseDuplicates,
                    includePeople, excludePeople, includePeopleMode,
                    semanticQuery, semanticTopK),
                cancellationToken);
            return result.Error is not null
                ? Results.BadRequest(new { error = result.Error })
                : Results.Ok(result.Page);
        }).WithName("ListTvPersonalGallery");

        // Owner-private video library for the native TV app. This is deliberately a
        // separate surface from the image gallery, matching the web /api/videos model:
        // newest-first, cursor-paged, server-detected videos only. Every page and every
        // byte below revalidates the limited TV session + in-memory Personal grant.
        app.MapGet("/api/tv/personal/videos", async (
            [FromQuery] int? limit,
            [FromQuery] string? cursor,
            HttpContext httpContext,
            [FromServices] ITvPersonalAreaService personal,
            [FromServices] ITvPersonalGalleryService gallery,
            CancellationToken cancellationToken) =>
        {
            SetNoStore(httpContext);
            var (failure, ownerUserId) = await ResolveTvPersonalAccessAsync(httpContext, personal, cancellationToken);
            if (failure is not null) return failure;

            var result = await gallery.ListVideosAsync(
                ownerUserId, new TvPersonalVideoQuery(limit, cursor), cancellationToken);
            return result.Error is not null
                ? Results.BadRequest(new { error = result.Error })
                : Results.Ok(result.Page);
        }).WithName("ListTvPersonalVideos");

        // Slice 100: LOCAL natural-language command interpretation. Turns a typed IT/EN
        // request into a validated PROPOSED draft (never applied here). No cloud, no
        // command text in URL/logs/audit; POST body only, no-store, grant-gated, and a
        // dedicated bounded rate-limit policy. The model proposes; the user applies.
        app.MapPost("/api/tv/personal/gallery/interpret-command", async (
            [FromBody] NubArca.Api.Ai.NaturalGallery.InterpretCommandRequest request,
            HttpContext httpContext,
            [FromServices] ITvPersonalAreaService personal,
            [FromServices] NubArca.Api.Ai.NaturalGallery.NaturalGalleryCommandService interpreter,
            [FromServices] IAuditLogger audit,
            CancellationToken cancellationToken) =>
        {
            SetNoStore(httpContext);
            var (failure, ownerUserId) = await ResolveTvPersonalAccessAsync(httpContext, personal, cancellationToken);
            if (failure is not null)
            {
                return failure;
            }

            var outcome = await interpreter.InterpretAsync(ownerUserId, request ?? new(), cancellationToken);
            var ip = httpContext.Connection.RemoteIpAddress?.ToString();

            // Audit SAFE FACTS ONLY: outcome kind, whether clarification is needed, and
            // the interpreter key. NEVER the command text, names, dates or semantic text.
            var auditFact = outcome.Kind switch
            {
                NubArca.Api.Ai.NaturalGallery.InterpretOutcomeKind.Ok =>
                    outcome.Response!.RequiresClarification ? "clarification" : "ok",
                _ => outcome.Kind.ToString().ToLowerInvariant(),
            };
            await audit.LogAsync(ownerUserId, AuditActions.TvPersonalInterpretCommand,
                AuditEntityTypes.TvSession, null, ip,
                new { outcome = auditFact, interpreter = outcome.InterpreterKey }, cancellationToken);

            return outcome.Kind switch
            {
                NubArca.Api.Ai.NaturalGallery.InterpretOutcomeKind.Ok => Results.Ok(outcome.Response),
                NubArca.Api.Ai.NaturalGallery.InterpretOutcomeKind.Unsupported =>
                    Results.Json(new { error = "unsupported_command" }, statusCode: StatusCodes.Status422UnprocessableEntity),
                NubArca.Api.Ai.NaturalGallery.InterpretOutcomeKind.ModelBusy =>
                    Results.Json(new { error = "model_busy" }, statusCode: StatusCodes.Status429TooManyRequests),
                NubArca.Api.Ai.NaturalGallery.InterpretOutcomeKind.Timeout =>
                    Results.Json(new { error = "model_timeout" }, statusCode: StatusCodes.Status504GatewayTimeout),
                NubArca.Api.Ai.NaturalGallery.InterpretOutcomeKind.ModelUnavailable =>
                    Results.Json(new { error = "model_unavailable" }, statusCode: StatusCodes.Status503ServiceUnavailable),
                _ => // Malformed
                    Results.Json(new { error = "interpretation_failed" }, statusCode: StatusCodes.Status422UnprocessableEntity),
            };
        }).WithName("InterpretTvPersonalGalleryCommand")
          .RequireRateLimiting(TvPersonalInterpretRateLimitPolicy);

        app.MapGet("/api/tv/personal/gallery/people", async (
            HttpContext httpContext,
            [FromServices] ITvPersonalAreaService personal,
            [FromServices] ITvPersonalGalleryService gallery,
            CancellationToken cancellationToken) =>
        {
            SetNoStore(httpContext);
            var (failure, ownerUserId) = await ResolveTvPersonalAccessAsync(httpContext, personal, cancellationToken);
            if (failure is not null)
            {
                return failure;
            }

            return Results.Ok(await gallery.ListPeopleAsync(ownerUserId, cancellationToken));
        }).WithName("ListTvPersonalGalleryPeople");

        app.MapGet("/api/tv/personal/gallery/albums", async (
            HttpContext httpContext,
            [FromServices] ITvPersonalAreaService personal,
            [FromServices] ITvPersonalGalleryService gallery,
            CancellationToken cancellationToken) =>
        {
            SetNoStore(httpContext);
            var (failure, ownerUserId) = await ResolveTvPersonalAccessAsync(httpContext, personal, cancellationToken);
            if (failure is not null)
            {
                return failure;
            }

            return Results.Ok(await gallery.ListAlbumsAsync(ownerUserId, cancellationToken));
        }).WithName("ListTvPersonalGalleryAlbums");

        // Bulk "add selection to album" — the ONLY bulk action the web gallery has.
        // Same idempotent semantics as the owner bulk endpoint (already-member and
        // foreign/missing ids count as skipped, never errors); the album must be the
        // owner's (404 otherwise). Audits ids/counts only.
        app.MapPost("/api/tv/personal/gallery/albums/{albumId:guid}/items", async (
            Guid albumId,
            [FromBody] TvPersonalAlbumAddRequest? body,
            HttpContext httpContext,
            [FromServices] ITvPersonalAreaService personal,
            [FromServices] IAlbumService albums,
            [FromServices] IAuditLogger audit,
            CancellationToken cancellationToken) =>
        {
            // Mirrors the owner bulk endpoint's request cap.
            const int MaxTvPersonalBulkAlbumItems = 1000;

            SetNoStore(httpContext);
            var (failure, ownerUserId) = await ResolveTvPersonalAccessAsync(httpContext, personal, cancellationToken);
            if (failure is not null)
            {
                return failure;
            }

            if (body?.FileItemIds is null || body.FileItemIds.Count == 0)
            {
                return Results.BadRequest(new { error = "Missing 'fileItemIds'." });
            }
            if (body.FileItemIds.Count > MaxTvPersonalBulkAlbumItems)
            {
                return Results.BadRequest(new { error = $"At most {MaxTvPersonalBulkAlbumItems} items per request." });
            }

            var result = await albums.AddItemsAsync(albumId, ownerUserId, body.FileItemIds, cancellationToken);
            if (result is null)
            {
                return Results.NotFound();
            }

            await audit.LogAsync(ownerUserId, AuditActions.TvPersonalAlbumBulkAdd, AuditEntityTypes.Album,
                albumId, httpContext.Connection.RemoteIpAddress?.ToString(),
                new { result.Requested, result.Succeeded, result.Skipped }, cancellationToken);
            return Results.Ok(result);
        }).WithName("TvPersonalAlbumBulkAdd");

        // Add an existing Personal Gallery selection to the owner's Beauty Lab. This
        // is the grant-gated TV projection of /api/aesthetics-lab/items/from-gallery:
        // the shared service acquires blob references without copying bytes and never
        // starts analysis. Partial failures retain their source FileItem ids so a TV
        // client can keep only failed items selected.
        app.MapPost("/api/tv/personal/gallery/add-to-beauty-lab", async (
            [FromBody] TvPersonalGalleryBulkRequest? body,
            HttpContext httpContext,
            [FromServices] ITvPersonalAreaService personal,
            [FromServices] IAestheticLabService lab,
            [FromServices] IOptions<AestheticsOptions> options,
            [FromServices] IAuditLogger audit,
            CancellationToken cancellationToken) =>
        {
            SetNoStore(httpContext);
            var (failure, ownerUserId) = await ResolveTvPersonalAccessAsync(httpContext, personal, cancellationToken);
            if (failure is not null)
            {
                return failure;
            }

            var ids = (body?.FileItemIds ?? Array.Empty<Guid>()).Distinct().ToList();
            if (ids.Count == 0)
            {
                return Results.BadRequest(new { error = "no_items" });
            }
            var cap = Math.Max(1, options.Value.MaximumBatchItems);
            if (ids.Count > cap)
            {
                return Results.BadRequest(new { error = "batch_limit_exceeded", maximum = cap });
            }

            var succeeded = new List<Guid>();
            var failures = new List<TvPersonalGalleryBulkFailureDto>();
            foreach (var fileItemId in ids)
            {
                try
                {
                    await lab.AddFromGalleryAsync(ownerUserId, fileItemId, cancellationToken);
                    succeeded.Add(fileItemId);
                }
                catch (AestheticLabValidationException)
                {
                    failures.Add(new(fileItemId, "not_available"));
                }
            }

            await audit.LogAsync(ownerUserId, AuditActions.AestheticLabAdd,
                AuditEntityTypes.AestheticLabItem, null, httpContext.Connection.RemoteIpAddress?.ToString(),
                new { source = "tv_gallery", succeeded = succeeded.Count, skipped = failures.Count },
                cancellationToken);
            return Results.Ok(new TvPersonalGalleryBulkResultDto(
                ids.Count, succeeded.Count, failures.Count, succeeded, failures));
        }).WithName("TvPersonalGalleryAddToBeautyLab");

        // Same projection for the owner-private Plates container. AddFromGalleryAsync
        // is idempotent, reference-counted, byte-copy-free, and does not start plate
        // analysis.
        app.MapPost("/api/tv/personal/gallery/add-to-plates", async (
            [FromBody] TvPersonalGalleryBulkRequest? body,
            HttpContext httpContext,
            [FromServices] ITvPersonalAreaService personal,
            [FromServices] IPlateImageService plates,
            [FromServices] IAuditLogger audit,
            CancellationToken cancellationToken) =>
        {
            const int MaxTvPersonalPlateItems = 500;

            SetNoStore(httpContext);
            var (failure, ownerUserId) = await ResolveTvPersonalAccessAsync(httpContext, personal, cancellationToken);
            if (failure is not null)
            {
                return failure;
            }

            var ids = (body?.FileItemIds ?? Array.Empty<Guid>()).Distinct().ToList();
            if (ids.Count == 0)
            {
                return Results.BadRequest(new { error = "no_items" });
            }
            if (ids.Count > MaxTvPersonalPlateItems)
            {
                return Results.BadRequest(new { error = "batch_limit_exceeded", maximum = MaxTvPersonalPlateItems });
            }

            var succeeded = new List<Guid>();
            var failures = new List<TvPersonalGalleryBulkFailureDto>();
            foreach (var fileItemId in ids)
            {
                try
                {
                    await plates.AddFromGalleryAsync(ownerUserId, fileItemId, cancellationToken);
                    succeeded.Add(fileItemId);
                }
                catch (PlateImageValidationException)
                {
                    failures.Add(new(fileItemId, "not_available"));
                }
            }

            await audit.LogAsync(ownerUserId, AuditActions.PlateAddFromGallery,
                AuditEntityTypes.Plate, null, httpContext.Connection.RemoteIpAddress?.ToString(),
                new { source = "tv_gallery", succeeded = succeeded.Count, skipped = failures.Count },
                cancellationToken);
            return Results.Ok(new TvPersonalGalleryBulkResultDto(
                ids.Count, succeeded.Count, failures.Count, succeeded, failures));
        }).WithName("TvPersonalGalleryAddToPlates");

        // Soft-delete only. The shared FileItem service owns tombstones, reference
        // counts, and Trash semantics; the TV handler never reproduces delete logic.
        app.MapPost("/api/tv/personal/gallery/trash", async (
            [FromBody] TvPersonalGalleryBulkRequest? body,
            HttpContext httpContext,
            [FromServices] ITvPersonalAreaService personal,
            [FromServices] IFileItemService files,
            [FromServices] IAuditLogger audit,
            CancellationToken cancellationToken) =>
        {
            const int MaxTvPersonalTrashItems = 500;

            SetNoStore(httpContext);
            var (failure, ownerUserId) = await ResolveTvPersonalAccessAsync(httpContext, personal, cancellationToken);
            if (failure is not null)
            {
                return failure;
            }

            var ids = (body?.FileItemIds ?? Array.Empty<Guid>()).Distinct().ToList();
            if (ids.Count == 0)
            {
                return Results.BadRequest(new { error = "no_items" });
            }
            if (ids.Count > MaxTvPersonalTrashItems)
            {
                return Results.BadRequest(new { error = "batch_limit_exceeded", maximum = MaxTvPersonalTrashItems });
            }

            var succeeded = new List<Guid>();
            var failures = new List<TvPersonalGalleryBulkFailureDto>();
            foreach (var fileItemId in ids)
            {
                var deleted = await files.SoftDeleteAsync(
                    ownerUserId, fileItemId, cancellationToken, FileDeleteReason.UserBulkDelete);
                if (deleted)
                {
                    succeeded.Add(fileItemId);
                }
                else
                {
                    failures.Add(new(fileItemId, "not_available"));
                }
            }

            await audit.LogAsync(ownerUserId, AuditActions.FileDelete, AuditEntityTypes.File,
                null, httpContext.Connection.RemoteIpAddress?.ToString(),
                new { source = "tv_gallery", succeeded = succeeded.Count, skipped = failures.Count },
                cancellationToken);
            return Results.Ok(new TvPersonalGalleryBulkResultDto(
                ids.Count, succeeded.Count, failures.Count, succeeded, failures));
        }).WithName("TvPersonalGalleryTrash");

        // Curated per-item metadata for the TV viewer (owner-private flow: GPS presence
        // only, never coordinates; strictly gallery-scoped — non-gallery ids are 404).
        app.MapGet("/api/tv/personal/media/{fileId:guid}/info", async (
            Guid fileId,
            HttpContext httpContext,
            [FromServices] ITvPersonalAreaService personal,
            [FromServices] ITvPersonalGalleryService gallery,
            CancellationToken cancellationToken) =>
        {
            SetNoStore(httpContext);
            var (failure, ownerUserId) = await ResolveTvPersonalAccessAsync(httpContext, personal, cancellationToken);
            if (failure is not null)
            {
                return failure;
            }

            var info = await gallery.GetMediaInfoAsync(ownerUserId, fileId, cancellationToken);
            return info is null ? Results.NotFound() : Results.Ok(info);
        }).WithName("GetTvPersonalMediaInfo");

        // Favorite toggle — the same owner-level mutation the web gallery performs
        // (FileItemUserMetadata.IsFavorite), exposed narrowly (nothing else on the
        // user-metadata document can be touched from the TV). Idempotent PUT.
        app.MapPut("/api/tv/personal/media/{fileId:guid}/favorite", async (
            Guid fileId,
            [FromBody] TvPersonalFavoriteRequest? body,
            HttpContext httpContext,
            [FromServices] ITvPersonalAreaService personal,
            [FromServices] IFileItemService files,
            [FromServices] IMetadataService metadata,
            [FromServices] IAuditLogger audit,
            CancellationToken cancellationToken) =>
        {
            SetNoStore(httpContext);
            var (failure, ownerUserId) = await ResolveTvPersonalAccessAsync(httpContext, personal, cancellationToken);
            if (failure is not null)
            {
                return failure;
            }

            if (body is null)
            {
                return Results.BadRequest(new { error = "Missing body." });
            }

            // Strictly gallery-scoped, like every TV personal media endpoint.
            if (!await files.IsGalleryImageAsync(ownerUserId, fileId, cancellationToken))
            {
                return Results.NotFound();
            }

            var favorite = await metadata.SetFavoriteAsync(ownerUserId, fileId, body.Favorite, cancellationToken);
            if (favorite is null)
            {
                return Results.NotFound();
            }

            await audit.LogAsync(ownerUserId, AuditActions.TvPersonalFavoriteSet, AuditEntityTypes.File,
                fileId, httpContext.Connection.RemoteIpAddress?.ToString(),
                new { favorite = favorite.Value }, cancellationToken);
            return Results.Ok(new TvPersonalFavoriteDto(fileId, favorite.Value));
        }).WithName("SetTvPersonalMediaFavorite");

        // TV personal media bytes: DERIVED artifacts only (small grid thumbnail /
        // medium viewer preview) — never original full-resolution bytes. Session
        // cookie AND unlock grant are re-checked on every request; the file must be
        // currently gallery-eligible (the exact listing rule), so revoking, deleting,
        // vaulting, or hiding a file cuts its bytes off immediately.
        app.MapGet("/api/tv/personal/media/{fileId:guid}/thumbnail", async (
            Guid fileId,
            HttpContext httpContext,
            [FromServices] ITvPersonalAreaService personal,
            [FromServices] IFileItemService files,
            [FromServices] IFileThumbnailService thumbnails,
            CancellationToken cancellationToken) =>
        {
            var (failure, ownerUserId) = await ResolveTvPersonalAccessAsync(httpContext, personal, cancellationToken);
            if (failure is not null)
            {
                SetNoStore(httpContext);
                return failure;
            }

            if (!await files.IsGalleryImageAsync(ownerUserId, fileId, cancellationToken))
            {
                SetNoStore(httpContext);
                return Results.NotFound();
            }

            var content = await thumbnails.EnsureAsync(
                fileId, ownerUserId, ThumbnailSizes.Small, cancellationToken);
            if (content is null)
            {
                SetNoStore(httpContext);
                return Results.NotFound();
            }

            SetPrivateDerivativeCache(httpContext);
            return Results.File(content.Content, content.MimeType);
        }).WithName("GetTvPersonalMediaThumbnail");

        app.MapGet("/api/tv/personal/media/{fileId:guid}/preview", async (
            Guid fileId,
            HttpContext httpContext,
            [FromServices] ITvPersonalAreaService personal,
            [FromServices] IFileItemService files,
            [FromServices] IFileThumbnailService thumbnails,
            CancellationToken cancellationToken) =>
        {
            var (failure, ownerUserId) = await ResolveTvPersonalAccessAsync(httpContext, personal, cancellationToken);
            if (failure is not null)
            {
                SetNoStore(httpContext);
                return failure;
            }

            if (!await files.IsGalleryImageAsync(ownerUserId, fileId, cancellationToken))
            {
                SetNoStore(httpContext);
                return Results.NotFound();
            }

            var content = await thumbnails.EnsureAsync(
                fileId, ownerUserId, ThumbnailSizes.Medium, cancellationToken);
            if (content is null)
            {
                SetNoStore(httpContext);
                return Results.NotFound();
            }

            SetPrivateDerivativeCache(httpContext);
            return Results.File(content.Content, content.MimeType);
        }).WithName("GetTvPersonalMediaPreview");

        app.MapGet("/api/tv/personal/media/{fileId:guid}/poster", async (
            Guid fileId,
            HttpContext httpContext,
            [FromServices] ITvPersonalAreaService personal,
            [FromServices] IFileItemService files,
            [FromServices] IFileThumbnailService thumbnails,
            CancellationToken cancellationToken) =>
        {
            var (failure, ownerUserId) = await ResolveTvPersonalAccessAsync(httpContext, personal, cancellationToken);
            if (failure is not null) { SetNoStore(httpContext); return failure; }
            if (!await files.IsGalleryVideoAsync(ownerUserId, fileId, cancellationToken))
            { SetNoStore(httpContext); return Results.NotFound(); }

            var content = await thumbnails.EnsureAsync(
                fileId, ownerUserId, ThumbnailSizes.Poster, cancellationToken);
            if (content is null) { SetNoStore(httpContext); return Results.NotFound(); }
            SetPrivateDerivativeCache(httpContext);
            return Results.File(content.Content, content.MimeType);
        }).WithName("GetTvPersonalVideoPoster");

        app.MapGet("/api/tv/personal/media/{fileId:guid}/video-preview-strip", async (
            Guid fileId,
            HttpContext httpContext,
            [FromServices] ITvPersonalAreaService personal,
            [FromServices] IFileItemService files,
            [FromServices] IFileThumbnailService thumbnails,
            CancellationToken cancellationToken) =>
        {
            var (failure, ownerUserId) = await ResolveTvPersonalAccessAsync(httpContext, personal, cancellationToken);
            if (failure is not null) { SetNoStore(httpContext); return failure; }
            if (!await files.IsGalleryVideoAsync(ownerUserId, fileId, cancellationToken))
            { SetNoStore(httpContext); return Results.NotFound(); }

            var content = await thumbnails.EnsureAsync(
                fileId, ownerUserId, ThumbnailSizes.VideoPreviewStrip, cancellationToken);
            if (content is null) { SetNoStore(httpContext); return Results.NotFound(); }
            SetPrivateDerivativeCache(httpContext);
            return Results.File(content.Content, content.MimeType);
        }).WithName("GetTvPersonalVideoPreviewStrip");

        app.MapGet("/api/tv/personal/media/{fileId:guid}/video", async (
            Guid fileId,
            HttpContext httpContext,
            [FromServices] ITvPersonalAreaService personal,
            [FromServices] IFileItemService files,
            [FromServices] NubArca.Api.Data.AppDbContext db,
            [FromServices] VideoHlsServingService hlsServing,
            CancellationToken cancellationToken) =>
        {
            var (failure, ownerUserId) = await ResolveTvPersonalAccessAsync(httpContext, personal, cancellationToken);
            if (failure is not null) return failure;
            if (!await files.IsGalleryVideoAsync(ownerUserId, fileId, cancellationToken))
                return Results.NotFound();

            if (hlsServing.Enabled)
            {
                var master = await hlsServing.GetMasterAsync(fileId, ownerUserId, cancellationToken);
                return master.Status switch
                {
                    VideoHlsMasterStatus.Ready => Results.Text(
                        master.MasterPlaylist!, VideoHlsServingService.MasterContentType),
                    VideoHlsMasterStatus.Preparing =>
                        VideoHlsServingService.Preparing(httpContext.Response),
                    _ => Results.NotFound(),
                };
            }

            var fileBlob = await db.FileItems.AsNoTracking()
                .Where(f => f.Id == fileId && f.OwnerUserId == ownerUserId && f.DeletedAt == null)
                .Select(f => new { f.BlobObjectId, f.Name })
                .FirstOrDefaultAsync(cancellationToken);
            if (fileBlob is null) return Results.NotFound();
            var detectedType = await db.BlobMetadata.AsNoTracking()
                .Where(m => m.BlobObjectId == fileBlob.BlobObjectId)
                .Select(m => m.DetectedContentType)
                .FirstOrDefaultAsync(cancellationToken);
            if (!SafeContentType.IsTrustedVideo(detectedType)) return Results.NotFound();

            var content = await files.OpenContentAsync(fileId, ownerUserId, cancellationToken);
            return content is null
                ? Results.NotFound()
                : Results.File(content.Content, SafeContentType.ForServingVideo(detectedType),
                    fileBlob.Name, enableRangeProcessing: true);
        }).WithName("StreamTvPersonalVideo");

        app.MapGet("/api/tv/personal/media/{fileId:guid}/video/{rendition}/{file}", async (
            Guid fileId,
            string rendition,
            string file,
            HttpContext httpContext,
            [FromServices] ITvPersonalAreaService personal,
            [FromServices] IFileItemService files,
            [FromServices] VideoHlsServingService hlsServing,
            CancellationToken cancellationToken) =>
        {
            if (!hlsServing.Enabled) return Results.NotFound();
            var (failure, ownerUserId) = await ResolveTvPersonalAccessAsync(httpContext, personal, cancellationToken);
            if (failure is not null) return failure;
            if (!await files.IsGalleryVideoAsync(ownerUserId, fileId, cancellationToken))
                return Results.NotFound();

            var content = await hlsServing.OpenLadderFileAsync(
                fileId, ownerUserId, $"{rendition}/{file}", cancellationToken);
            if (content is null) return Results.NotFound();
            SetPrivateDerivativeCache(httpContext);
            return Results.File(content.Content, content.ContentType);
        }).WithName("StreamTvPersonalVideoHlsFile");

        // --- TV "Beauty Lab" (Laboratorio bellezza) — grant-gated projection of the
        // owner-private Aesthetics Lab. Every endpoint re-resolves the limited TV
        // session cookie AND the Personal Area unlock grant on each call (the SAME
        // ResolveTvPersonalAccessAsync used by TV Personal Gallery: 401 session invalid;
        // 403 pin_changed; 403 locked), then delegates to the EXACT SAME owner-private
        // application services the web lab uses (IAestheticLabService /
        // IAestheticAnalysisService) — no duplicated orchestration, so TV and web
        // behaviour are identical by construction. DTOs are the curated, no-leak lab
        // DTOs (never a blob id, SHA, storage path, container key, or raw model output);
        // media is DERIVED-only (small thumbnail / medium preview), never originals.
        // JSON is no-store; derived bytes use the private browser-cache policy.

        app.MapGet("/api/tv/personal/aesthetics/items", async (
            [FromQuery] int? limit,
            [FromQuery] string? cursor,
            HttpContext httpContext,
            [FromServices] ITvPersonalAreaService personal,
            [FromServices] IAestheticLabService lab,
            CancellationToken cancellationToken) =>
        {
            SetNoStore(httpContext);
            var (failure, ownerUserId) = await ResolveTvPersonalAccessAsync(httpContext, personal, cancellationToken);
            if (failure is not null)
            {
                return failure;
            }
            var page = await lab.ListAsync(ownerUserId, cursor, limit ?? 50, cancellationToken);
            return Results.Ok(page);
        }).WithName("ListTvBeautyLabItems");

        app.MapGet("/api/tv/personal/aesthetics/items/{id:guid}", async (
            Guid id,
            HttpContext httpContext,
            [FromServices] ITvPersonalAreaService personal,
            [FromServices] IAestheticLabService lab,
            CancellationToken cancellationToken) =>
        {
            SetNoStore(httpContext);
            var (failure, ownerUserId) = await ResolveTvPersonalAccessAsync(httpContext, personal, cancellationToken);
            if (failure is not null)
            {
                return failure;
            }
            var detail = await lab.GetDetailAsync(ownerUserId, id, cancellationToken);
            return detail is null ? Results.NotFound() : Results.Ok(detail);
        }).WithName("GetTvBeautyLabItem");

        app.MapGet("/api/tv/personal/aesthetics/items/{id:guid}/thumbnail", async (
            Guid id,
            string? size,
            HttpContext httpContext,
            [FromServices] ITvPersonalAreaService personal,
            [FromServices] IAestheticLabService lab,
            CancellationToken cancellationToken) =>
        {
            var (failure, ownerUserId) = await ResolveTvPersonalAccessAsync(httpContext, personal, cancellationToken);
            if (failure is not null)
            {
                SetNoStore(httpContext);
                return failure;
            }
            var content = await lab.RenderDerivativeAsync(
                ownerUserId, id, string.IsNullOrWhiteSpace(size) ? ThumbnailSizes.Small : size, cancellationToken);
            if (content is null)
            {
                SetNoStore(httpContext);
                return Results.NotFound();
            }
            SetPrivateDerivativeCache(httpContext);
            return Results.File(content.Content, content.ContentType);
        }).WithName("GetTvBeautyLabItemThumbnail");

        app.MapGet("/api/tv/personal/aesthetics/items/{id:guid}/preview", async (
            Guid id,
            HttpContext httpContext,
            [FromServices] ITvPersonalAreaService personal,
            [FromServices] IAestheticLabService lab,
            CancellationToken cancellationToken) =>
        {
            var (failure, ownerUserId) = await ResolveTvPersonalAccessAsync(httpContext, personal, cancellationToken);
            if (failure is not null)
            {
                SetNoStore(httpContext);
                return failure;
            }
            var content = await lab.RenderDerivativeAsync(ownerUserId, id, ThumbnailSizes.Medium, cancellationToken);
            if (content is null)
            {
                SetNoStore(httpContext);
                return Results.NotFound();
            }
            SetPrivateDerivativeCache(httpContext);
            return Results.File(content.Content, content.ContentType);
        }).WithName("GetTvBeautyLabItemPreview");

        app.MapPost("/api/tv/personal/aesthetics/analyses", async (
            [FromBody] AestheticAnalyzeRequest body,
            HttpContext httpContext,
            [FromServices] ITvPersonalAreaService personal,
            [FromServices] IAestheticAnalysisService analysis,
            [FromServices] IOptions<AestheticsOptions> options,
            [FromServices] IAuditLogger audit,
            CancellationToken cancellationToken) =>
        {
            SetNoStore(httpContext);
            var (failure, ownerUserId) = await ResolveTvPersonalAccessAsync(httpContext, personal, cancellationToken);
            if (failure is not null)
            {
                return failure;
            }
            var ip = httpContext.Connection.RemoteIpAddress?.ToString();
            var ids = (body?.ItemIds ?? new List<Guid>()).Distinct().ToList();
            if (ids.Count == 0)
            {
                return Results.BadRequest(new { error = "no_items" });
            }
            var opts = options.Value;
            if (ids.Count > opts.MaximumBatchItems)
            {
                return Results.BadRequest(new { error = "batch_limit_exceeded", maximum = opts.MaximumBatchItems });
            }
            if (!opts.Enabled)
            {
                // Controlled unavailable — identical to the web lab: 200 with every item
                // skipped and NO job created.
                return Results.Ok(new AestheticAnalysisBatchResultDto(
                    Array.Empty<AestheticAnalysisEnqueuedDto>(),
                    ids.Select(i => new AestheticAnalysisSkippedDto(i, AestheticErrorCodes.FeatureDisabled)).ToList()));
            }
            var result = await analysis.RequestAnalysisAsync(ownerUserId, ids, body?.Capabilities, cancellationToken);
            await audit.LogAsync(
                ownerUserId, AuditActions.AestheticAnalyzeRequest, AuditEntityTypes.AestheticRun, null, ip,
                new { enqueued = result.Enqueued.Count, skipped = result.Skipped.Count }, cancellationToken);
            return Results.Accepted("/api/tv/personal/aesthetics/items", result);
        }).WithName("RequestTvBeautyLabAnalysis");

        app.MapPost("/api/tv/personal/aesthetics/runs/{id:guid}/cancel", async (
            Guid id,
            HttpContext httpContext,
            [FromServices] ITvPersonalAreaService personal,
            [FromServices] IAestheticAnalysisService analysis,
            [FromServices] IAuditLogger audit,
            CancellationToken cancellationToken) =>
        {
            SetNoStore(httpContext);
            var (failure, ownerUserId) = await ResolveTvPersonalAccessAsync(httpContext, personal, cancellationToken);
            if (failure is not null)
            {
                return failure;
            }
            var ok = await analysis.CancelRunAsync(ownerUserId, id, cancellationToken);
            if (!ok)
            {
                return Results.NotFound();
            }
            await audit.LogAsync(ownerUserId, AuditActions.AestheticAnalyzeCancel, AuditEntityTypes.AestheticRun,
                id, httpContext.Connection.RemoteIpAddress?.ToString(), null, cancellationToken);
            return Results.NoContent();
        }).WithName("CancelTvBeautyLabRun");

        app.MapPost("/api/tv/personal/aesthetics/runs/{id:guid}/retry", async (
            Guid id,
            HttpContext httpContext,
            [FromServices] ITvPersonalAreaService personal,
            [FromServices] IAestheticAnalysisService analysis,
            [FromServices] IAuditLogger audit,
            CancellationToken cancellationToken) =>
        {
            SetNoStore(httpContext);
            var (failure, ownerUserId) = await ResolveTvPersonalAccessAsync(httpContext, personal, cancellationToken);
            if (failure is not null)
            {
                return failure;
            }
            var run = await analysis.RetryRunAsync(ownerUserId, id, cancellationToken);
            if (run is null)
            {
                return Results.NotFound();
            }
            await audit.LogAsync(ownerUserId, AuditActions.AestheticAnalyzeRetry, AuditEntityTypes.AestheticRun,
                run.Id, httpContext.Connection.RemoteIpAddress?.ToString(), null, cancellationToken);
            return Results.Accepted("/api/tv/personal/aesthetics/items", run);
        }).WithName("RetryTvBeautyLabRun");

        app.MapDelete("/api/tv/personal/aesthetics/items/{id:guid}", async (
            Guid id,
            HttpContext httpContext,
            [FromServices] ITvPersonalAreaService personal,
            [FromServices] IAestheticLabService lab,
            [FromServices] IAuditLogger audit,
            CancellationToken cancellationToken) =>
        {
            SetNoStore(httpContext);
            var (failure, ownerUserId) = await ResolveTvPersonalAccessAsync(httpContext, personal, cancellationToken);
            if (failure is not null)
            {
                return failure;
            }
            var removed = await lab.RemoveAsync(ownerUserId, id, cancellationToken);
            if (!removed)
            {
                return Results.NotFound();
            }
            await audit.LogAsync(ownerUserId, AuditActions.AestheticLabRemove, AuditEntityTypes.AestheticLabItem,
                id, httpContext.Connection.RemoteIpAddress?.ToString(), null, cancellationToken);
            return Results.NoContent();
        }).WithName("RemoveTvBeautyLabItem");

        // QR upload-session lifecycle (grant-gated). Create returns the one-time QR URL
        // (raw token in a path segment); status is pollable while the QR screen is open;
        // revoke is the explicit teardown when the screen closes. Audits SAFE counts /
        // lifecycle only — never the token.
        app.MapPost("/api/tv/personal/aesthetics/upload-sessions", async (
            HttpContext httpContext,
            [FromServices] ITvPersonalAreaService personal,
            [FromServices] IAestheticUploadSessionService sessions,
            [FromServices] IAuditLogger audit,
            CancellationToken cancellationToken) =>
        {
            SetNoStore(httpContext);
            var (failure, ownerUserId) = await ResolveTvPersonalAccessAsync(httpContext, personal, cancellationToken);
            if (failure is not null)
            {
                return failure;
            }
            var created = await sessions.CreateAsync(ownerUserId, cancellationToken);
            await audit.LogAsync(ownerUserId, AuditActions.AestheticUploadSessionCreate, AuditEntityTypes.AestheticLabItem,
                created.Id, httpContext.Connection.RemoteIpAddress?.ToString(),
                new { expiresAt = created.ExpiresAt, created.MaxFiles }, cancellationToken);
            return Results.Ok(created);
        }).WithName("CreateTvBeautyLabUploadSession");

        app.MapGet("/api/tv/personal/aesthetics/upload-sessions/{id:guid}", async (
            Guid id,
            HttpContext httpContext,
            [FromServices] ITvPersonalAreaService personal,
            [FromServices] IAestheticUploadSessionService sessions,
            CancellationToken cancellationToken) =>
        {
            SetNoStore(httpContext);
            var (failure, ownerUserId) = await ResolveTvPersonalAccessAsync(httpContext, personal, cancellationToken);
            if (failure is not null)
            {
                return failure;
            }
            var status = await sessions.GetStatusAsync(ownerUserId, id, cancellationToken);
            return status is null ? Results.NotFound() : Results.Ok(status);
        }).WithName("GetTvBeautyLabUploadSession");

        app.MapPost("/api/tv/personal/aesthetics/upload-sessions/{id:guid}/revoke", async (
            Guid id,
            HttpContext httpContext,
            [FromServices] ITvPersonalAreaService personal,
            [FromServices] IAestheticUploadSessionService sessions,
            [FromServices] IAuditLogger audit,
            CancellationToken cancellationToken) =>
        {
            SetNoStore(httpContext);
            var (failure, ownerUserId) = await ResolveTvPersonalAccessAsync(httpContext, personal, cancellationToken);
            if (failure is not null)
            {
                return failure;
            }
            var ok = await sessions.RevokeAsync(ownerUserId, id, cancellationToken);
            if (!ok)
            {
                return Results.NotFound();
            }
            await audit.LogAsync(ownerUserId, AuditActions.AestheticUploadSessionRevoke, AuditEntityTypes.AestheticLabItem,
                id, httpContext.Connection.RemoteIpAddress?.ToString(), null, cancellationToken);
            return Results.NoContent();
        }).WithName("RevokeTvBeautyLabUploadSession");

        // --- TV media browsing (ShowOnTv allowlist) ---
        // These endpoints authorize ONLY via the limited TV session cookie (resolved
        // manually to an owner id), never the normal user auth scheme. They surface a
        // paired TV owner's own albums that the owner has explicitly enabled for TV,
        // and re-check ShowOnTv on every call so disabling an album removes it live.
        // DTOs and media bytes are owner-private and carry no storage/blob/AI internals.

        app.MapGet("/api/tv/albums", async (
            HttpContext httpContext,
            [FromServices] ITvPairingService tv,
            [FromServices] ITvMediaService media,
            CancellationToken cancellationToken) =>
        {
            SetNoStore(httpContext);
            var ownerUserId = await tv.ResolveOwnerUserIdAsync(
                httpContext.Request.Cookies[TvPairingService.CookieName], cancellationToken);
            if (ownerUserId is null)
            {
                return Results.Unauthorized();
            }

            return Results.Ok(await media.ListAlbumsAsync(ownerUserId.Value, cancellationToken));
        }).WithName("ListTvAlbums");

        app.MapGet("/api/tv/albums/{albumId:guid}/items", async (
            Guid albumId,
            HttpContext httpContext,
            [FromServices] ITvPairingService tv,
            [FromServices] ITvMediaService media,
            CancellationToken cancellationToken) =>
        {
            SetNoStore(httpContext);
            var ownerUserId = await tv.ResolveOwnerUserIdAsync(
                httpContext.Request.Cookies[TvPairingService.CookieName], cancellationToken);
            if (ownerUserId is null)
            {
                return Results.Unauthorized();
            }

            // Null → the album is missing, foreign, or no longer enabled for TV. All
            // collapse to a generic 404 (no existence leak, live revocation).
            var items = await media.ListItemsAsync(ownerUserId.Value, albumId, cancellationToken);
            return items is null ? Results.NotFound() : Results.Ok(items);
        }).WithName("ListTvAlbumItems");

        // TV active face filter: a guest's face search reaches the TV ONLY after an
        // explicit "show these photos on TV" activation on the public party page (the
        // backend bridges the activation — the party client never calls /api/tv). The TV
        // keeps polling this endpoint; when Active it filters the grid/slideshow to the
        // matching subset (same /api/tv media URLs). TV-session scoped → owner; the album
        // must be one of the owner's ShowOnTv albums. Visibility is re-derived every poll,
        // so a hidden/removed match drops out and an expired/cleared/deleted search
        // returns Active=false (the TV returns to the full album). ActivationVersion is
        // the server-side activation order; FaceThumbnailUrl serves only the small
        // detected-face crop. No names, no scores, no face/person identity data.
        app.MapGet("/api/tv/albums/{albumId:guid}/face-search/active", async (
            Guid albumId,
            HttpContext httpContext,
            [FromServices] ITvPairingService tv,
            [FromServices] ITvMediaService media,
            [FromServices] NubArca.Api.Party.IPartyFaceSearchService faceSearch,
            CancellationToken cancellationToken) =>
        {
            SetNoStore(httpContext);
            var ownerUserId = await tv.ResolveOwnerUserIdAsync(
                httpContext.Request.Cookies[TvPairingService.CookieName], cancellationToken);
            if (ownerUserId is null)
            {
                return Results.Unauthorized();
            }

            var inactive = new NubArca.Api.Tv.TvFaceSearchActiveDto(
                false, null, null, null, null, Array.Empty<NubArca.Api.Tv.TvAlbumItemDto>());

            var view = await faceSearch.GetActiveAsync(ownerUserId.Value, albumId, cancellationToken);
            if (view is null)
            {
                return Results.Ok(inactive);
            }

            // Reuse the album's live TV item list (same URL/name building + ShowOnTv +
            // moderation re-check), then keep only the matching ids in rank order.
            var album = await media.ListItemsAsync(ownerUserId.Value, albumId, cancellationToken);
            if (album is null)
            {
                return Results.Ok(inactive);
            }

            var byId = album.Items.ToDictionary(i => i.Id);
            var items = view.FileItemIds
                .Where(byId.ContainsKey)
                .Select(id => byId[id])
                .ToList();
            return items.Count == 0
                ? Results.Ok(inactive)
                : Results.Ok(new NubArca.Api.Tv.TvFaceSearchActiveDto(
                    true,
                    view.SearchId,
                    view.ActivationVersion,
                    view.ActivatedAt,
                    view.HasFaceCrop
                        ? $"/api/tv/albums/{albumId}/face-search/{view.SearchId}/face-thumbnail"
                        : null,
                    items));
        }).WithName("GetTvActiveFaceSearch");

        // TV exits face-filter mode (BACK on the remote). With ?searchId= the TV deletes
        // THAT search (session + rank rows + stored face crop) — row-scoped, so a stale
        // request for an older search never removes a newer active filter, and a
        // concurrent phone-side cancellation completes safely. Without searchId it only
        // DEACTIVATES whatever is active (legacy/fallback; guests' local searches are
        // kept). Idempotent; missing/foreign/not-ShowOnTv album → generic 404.
        app.MapDelete("/api/tv/albums/{albumId:guid}/face-search/active", async (
            Guid albumId,
            Guid? searchId,
            HttpContext httpContext,
            [FromServices] ITvPairingService tv,
            [FromServices] NubArca.Api.Party.IPartyFaceSearchService faceSearch,
            [FromServices] IAuditLogger audit,
            CancellationToken cancellationToken) =>
        {
            SetNoStore(httpContext);
            var ownerUserId = await tv.ResolveOwnerUserIdAsync(
                httpContext.Request.Cookies[TvPairingService.CookieName], cancellationToken);
            if (ownerUserId is null)
            {
                return Results.Unauthorized();
            }

            var ok = searchId is null
                ? await faceSearch.ClearActiveAsync(ownerUserId.Value, albumId, cancellationToken)
                : await faceSearch.DeleteForTvAsync(ownerUserId.Value, albumId, searchId.Value, cancellationToken);
            if (!ok)
            {
                return Results.NotFound();
            }

            await audit.LogAsync(
                userId: ownerUserId,
                action: AuditActions.PartyFaceSearchDelete,
                entityType: AuditEntityTypes.PartyAlbum,
                entityId: albumId,
                ipAddress: httpContext.Connection.RemoteIpAddress?.ToString(),
                metadata: new { source = "tv", deleted = searchId is not null },
                cancellationToken: cancellationToken);
            return Results.NoContent();
        }).WithName("ClearTvActiveFaceSearch");

        // TV face-filter indicator thumbnail: the small crop of the DETECTED query face
        // stored at search time (never the full selfie, never an original). Served only
        // while that search is still an ACTIVATED live filter of the owner's ShowOnTv
        // album; anything else → generic 404. no-store: the crop disappears with the
        // search.
        app.MapGet("/api/tv/albums/{albumId:guid}/face-search/{searchId:guid}/face-thumbnail", async (
            Guid albumId,
            Guid searchId,
            HttpContext httpContext,
            [FromServices] ITvPairingService tv,
            [FromServices] NubArca.Api.Party.IPartyFaceSearchService faceSearch,
            CancellationToken cancellationToken) =>
        {
            SetNoStore(httpContext);
            var ownerUserId = await tv.ResolveOwnerUserIdAsync(
                httpContext.Request.Cookies[TvPairingService.CookieName], cancellationToken);
            if (ownerUserId is null)
            {
                return Results.Unauthorized();
            }

            var content = await faceSearch.OpenFaceCropAsync(
                ownerUserId.Value, albumId, searchId, cancellationToken);
            return content is null
                ? Results.NotFound()
                : Results.File(content.Content, content.MimeType);
        }).WithName("GetTvFaceSearchThumbnail");

        // TV media bytes. Each endpoint resolves the TV session → owner, verifies the
        // file is currently allowlisted (member of one of the owner's ShowOnTv albums,
        // owner-owned, active, non-vault) and only then serves a DERIVED artifact
        // (small thumbnail / medium preview / video poster) or the range-streamed
        // video. Original full-resolution image bytes are never served here.
        app.MapGet("/api/tv/media/{fileId:guid}/thumbnail", async (
            Guid fileId,
            HttpContext httpContext,
            [FromServices] ITvPairingService tv,
            [FromServices] ITvMediaService media,
            [FromServices] IFileThumbnailService thumbnails,
            CancellationToken cancellationToken) =>
        {
            var ownerUserId = await tv.ResolveOwnerUserIdAsync(
                httpContext.Request.Cookies[TvPairingService.CookieName], cancellationToken);
            if (ownerUserId is null)
            {
                return Results.Unauthorized();
            }
            if (!await media.IsMediaVisibleAsync(ownerUserId.Value, fileId, cancellationToken))
            {
                return Results.NotFound();
            }

            var content = await thumbnails.EnsureAsync(
                fileId, ownerUserId.Value, ThumbnailSizes.Small, cancellationToken);
            if (content is null)
            {
                return Results.NotFound();
            }

            SetPrivateDerivativeCache(httpContext);
            return Results.File(content.Content, content.MimeType);
        }).WithName("GetTvMediaThumbnail");

        app.MapGet("/api/tv/media/{fileId:guid}/preview", async (
            Guid fileId,
            HttpContext httpContext,
            [FromServices] ITvPairingService tv,
            [FromServices] ITvMediaService media,
            [FromServices] IFileThumbnailService thumbnails,
            CancellationToken cancellationToken) =>
        {
            var ownerUserId = await tv.ResolveOwnerUserIdAsync(
                httpContext.Request.Cookies[TvPairingService.CookieName], cancellationToken);
            if (ownerUserId is null)
            {
                return Results.Unauthorized();
            }
            if (!await media.IsMediaVisibleAsync(ownerUserId.Value, fileId, cancellationToken))
            {
                return Results.NotFound();
            }

            var content = await thumbnails.EnsureAsync(
                fileId, ownerUserId.Value, ThumbnailSizes.Medium, cancellationToken);
            if (content is null)
            {
                return Results.NotFound();
            }

            SetPrivateDerivativeCache(httpContext);
            return Results.File(content.Content, content.MimeType);
        }).WithName("GetTvMediaPreview");

        app.MapGet("/api/tv/media/{fileId:guid}/poster", async (
            Guid fileId,
            HttpContext httpContext,
            [FromServices] ITvPairingService tv,
            [FromServices] ITvMediaService media,
            [FromServices] IFileThumbnailService thumbnails,
            [FromServices] NubArca.Api.Data.AppDbContext db,
            CancellationToken cancellationToken) =>
        {
            var ownerUserId = await tv.ResolveOwnerUserIdAsync(
                httpContext.Request.Cookies[TvPairingService.CookieName], cancellationToken);
            if (ownerUserId is null)
            {
                return Results.Unauthorized();
            }
            if (!await media.IsMediaVisibleAsync(ownerUserId.Value, fileId, cancellationToken))
            {
                return Results.NotFound();
            }

            // Server-detected video gate, mirroring /api/files/{id}/poster.
            var ok = await db.FileItems
                .AsNoTracking()
                .Where(f => f.Id == fileId && f.OwnerUserId == ownerUserId.Value && f.DeletedAt == null)
                .Join(
                    db.BlobMetadata.AsNoTracking(),
                    f => f.BlobObjectId,
                    m => m.BlobObjectId,
                    (f, m) => new
                    {
                        m.MediaCategory, m.DetectedContentType, m.VideoExtractionStatus, m.VideoCodec,
                    })
                .FirstOrDefaultAsync(cancellationToken);
            // Serves an ffmpeg-PRODUCED JPEG, so a legacy container confirmed by ffprobe
            // qualifies (SafeContentType.IsServerConfirmedVideo).
            if (ok is null
                || ok.MediaCategory != MediaCategories.Video
                || !SafeContentType.IsServerConfirmedVideo(
                    ok.DetectedContentType, ok.VideoExtractionStatus, ok.VideoCodec))
            {
                return Results.NotFound();
            }

            var content = await thumbnails.EnsureAsync(
                fileId, ownerUserId.Value, ThumbnailSizes.Poster, cancellationToken);
            if (content is null)
            {
                return Results.NotFound();
            }

            SetPrivateDerivativeCache(httpContext);
            return Results.File(content.Content, content.MimeType);
        }).WithName("GetTvMediaPoster");

        app.MapGet("/api/tv/media/{fileId:guid}/video-preview-strip", async (
            Guid fileId,
            HttpContext httpContext,
            [FromServices] ITvPairingService tv,
            [FromServices] ITvMediaService media,
            [FromServices] IFileThumbnailService thumbnails,
            [FromServices] NubArca.Api.Data.AppDbContext db,
            CancellationToken cancellationToken) =>
        {
            var ownerUserId = await tv.ResolveOwnerUserIdAsync(
                httpContext.Request.Cookies[TvPairingService.CookieName], cancellationToken);
            if (ownerUserId is null) return Results.Unauthorized();
            if (!await media.IsMediaVisibleAsync(ownerUserId.Value, fileId, cancellationToken))
                return Results.NotFound();

            var ok = await db.FileItems.AsNoTracking()
                .Where(f => f.Id == fileId && f.OwnerUserId == ownerUserId.Value && f.DeletedAt == null)
                .Join(db.BlobMetadata.AsNoTracking(), f => f.BlobObjectId, m => m.BlobObjectId,
                    (f, m) => new
                    {
                        m.MediaCategory, m.DetectedContentType, m.VideoExtractionStatus, m.VideoCodec,
                    })
                .FirstOrDefaultAsync(cancellationToken);
            // ffmpeg-PRODUCED strip → server-confirmed video is enough.
            if (ok is null || ok.MediaCategory != MediaCategories.Video
                || !SafeContentType.IsServerConfirmedVideo(
                    ok.DetectedContentType, ok.VideoExtractionStatus, ok.VideoCodec))
                return Results.NotFound();

            var content = await thumbnails.EnsureAsync(
                fileId, ownerUserId.Value, ThumbnailSizes.VideoPreviewStrip, cancellationToken);
            if (content is null) return Results.NotFound();

            SetPrivateDerivativeCache(httpContext);
            return Results.File(content.Content, content.MimeType);
        }).WithName("GetTvMediaVideoPreviewStrip");

        app.MapGet("/api/tv/media/{fileId:guid}/video", async (
            Guid fileId,
            HttpContext httpContext,
            [FromServices] ITvPairingService tv,
            [FromServices] ITvMediaService media,
            [FromServices] IFileItemService files,
            [FromServices] NubArca.Api.Data.AppDbContext db,
            [FromServices] VideoHlsServingService hlsServing,
            CancellationToken cancellationToken) =>
        {
            var ownerUserId = await tv.ResolveOwnerUserIdAsync(
                httpContext.Request.Cookies[TvPairingService.CookieName], cancellationToken);
            if (ownerUserId is null)
            {
                return Results.Unauthorized();
            }
            if (!await media.IsMediaVisibleAsync(ownerUserId.Value, fileId, cancellationToken))
            {
                return Results.NotFound();
            }

            // Video-hls slice 2: same adaptive contract as /api/files/{id}/video when
            // the provider is enabled (master playlist | 202-preparing | 404).
            if (hlsServing.Enabled)
            {
                var master = await hlsServing.GetMasterAsync(
                    fileId, ownerUserId.Value, cancellationToken);
                return master.Status switch
                {
                    VideoHlsMasterStatus.Ready => Results.Text(
                        master.MasterPlaylist!, VideoHlsServingService.MasterContentType),
                    VideoHlsMasterStatus.Preparing =>
                        VideoHlsServingService.Preparing(httpContext.Response),
                    _ => Results.NotFound(),
                };
            }

            var fileBlob = await db.FileItems
                .AsNoTracking()
                .Where(f => f.Id == fileId && f.OwnerUserId == ownerUserId.Value && f.DeletedAt == null)
                .Select(f => new { f.BlobObjectId, f.Name })
                .FirstOrDefaultAsync(cancellationToken);
            if (fileBlob is null)
            {
                return Results.NotFound();
            }

            var blobMeta = await db.BlobMetadata
                .AsNoTracking()
                .Where(m => m.BlobObjectId == fileBlob.BlobObjectId)
                .Select(m => new { m.MediaCategory, m.DetectedContentType })
                .FirstOrDefaultAsync(cancellationToken);
            if (blobMeta is null
                || blobMeta.MediaCategory != MediaCategories.Video
                || !SafeContentType.IsTrustedVideo(blobMeta.DetectedContentType))
            {
                return Results.NotFound();
            }

            var content = await files.OpenContentAsync(fileId, ownerUserId.Value, cancellationToken);
            if (content is null)
            {
                return Results.NotFound();
            }

            var safeType = SafeContentType.ForServingVideo(blobMeta.DetectedContentType);
            return Results.File(
                content.Content, safeType, fileBlob.Name, enableRangeProcessing: true);
        }).WithName("StreamTvMediaVideo");

        // Video-hls slice 2: TV ladder child files — same contract and gating as the
        // web child route, under the TV pairing-cookie authorization model.
        app.MapGet("/api/tv/media/{fileId:guid}/video/{rendition}/{file}", async (
            Guid fileId,
            string rendition,
            string file,
            HttpContext httpContext,
            [FromServices] ITvPairingService tv,
            [FromServices] ITvMediaService media,
            [FromServices] VideoHlsServingService hlsServing,
            CancellationToken cancellationToken) =>
        {
            if (!hlsServing.Enabled)
            {
                return Results.NotFound();
            }
            var ownerUserId = await tv.ResolveOwnerUserIdAsync(
                httpContext.Request.Cookies[TvPairingService.CookieName], cancellationToken);
            if (ownerUserId is null)
            {
                return Results.Unauthorized();
            }
            if (!await media.IsMediaVisibleAsync(ownerUserId.Value, fileId, cancellationToken))
            {
                return Results.NotFound();
            }
            var content = await hlsServing.OpenLadderFileAsync(
                fileId, ownerUserId.Value, $"{rendition}/{file}", cancellationToken);
            if (content is null)
            {
                return Results.NotFound();
            }
            SetPrivateDerivativeCache(httpContext);
            return Results.File(content.Content, content.ContentType);
        }).WithName("StreamTvMediaVideoHlsFile");

        return app;
    }

    // Shared authorization step for every TV Personal Gallery endpoint: resolves
    // the limited TV session cookie + the X-Tv-Personal-Unlock grant and maps the
    // failure modes to the canonical responses (401 session invalid; 403
    // pin_changed for a stale PIN generation; 403 locked for any other grant
    // failure — one generic bucket, mirroring /api/tv/personal/home). On success
    // returns (null, ownerUserId).
    private static async Task<(IResult? Failure, Guid OwnerUserId)> ResolveTvPersonalAccessAsync(
        HttpContext httpContext,
        NubArca.Api.Tv.ITvPersonalAreaService personal,
        CancellationToken cancellationToken)
    {
        var access = await personal.ResolveAccessAsync(
            httpContext.Request.Cookies[NubArca.Api.Tv.TvPairingService.CookieName],
            httpContext.Request.Headers[NubArca.Api.Tv.TvPersonalAreaService.UnlockHeader].ToString(),
            cancellationToken);
        return access.Status switch
        {
            NubArca.Api.Tv.TvPersonalAccessStatus.SessionInvalid
                => (Results.Unauthorized(), Guid.Empty),
            NubArca.Api.Tv.TvPersonalAccessStatus.GrantStalePinChanged
                => (Results.Json(new { error = "pin_changed" }, statusCode: StatusCodes.Status403Forbidden), Guid.Empty),
            NubArca.Api.Tv.TvPersonalAccessStatus.GrantInvalid
                => (Results.Json(new { error = "locked" }, statusCode: StatusCodes.Status403Forbidden), Guid.Empty),
            _ => (null, access.OwnerUserId!.Value),
        };
    }

    private static void AppendTvSessionCookie(HttpContext context, string token, DateTime expiresAt)
    {
        context.Response.Cookies.Append(TvPairingService.CookieName, token, new CookieOptions
        {
            HttpOnly = true,
            Secure = true,
            SameSite = SameSiteMode.Strict,
            Path = "/api/tv",
            Expires = new DateTimeOffset(expiresAt, TimeSpan.Zero),
            IsEssential = true,
        });
    }

    private static void DeleteTvSessionCookie(HttpContext context)
    {
        context.Response.Cookies.Delete(TvPairingService.CookieName, new CookieOptions
        {
            HttpOnly = true,
            Secure = true,
            SameSite = SameSiteMode.Strict,
            Path = "/api/tv",
        });
    }

    // Duplicated from Program.cs's local `SetNoStore` / `SetPrivateDerivativeCache`
    // helpers (used by dozens of other still-inline endpoints there, so they stay
    // put) — same logic.
    private static void SetNoStore(HttpContext context)
    {
        context.Response.Headers.CacheControl = "no-store";
        context.Response.Headers.Pragma = "no-cache";
    }

    private static void SetPrivateDerivativeCache(HttpContext httpContext)
    {
        httpContext.Response.Headers.CacheControl = "private, max-age=86400";
    }
}
