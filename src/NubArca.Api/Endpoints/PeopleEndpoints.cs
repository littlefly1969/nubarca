using Microsoft.AspNetCore.Mvc;
using NubArca.Api.Http;

namespace NubArca.Api.Endpoints;

// Extracted verbatim from Program.cs (modular-monolith cleanup, not a service
// split — same process, same DI container, same middleware pipeline). Route
// paths, HTTP methods, endpoint names, authorization, status codes, and DTOs
// are unchanged from the original inline mappings.
//
// People / Face (People v0). Owner-private. Every face surfaced is resolved
// through the caller's active, non-vault library, so vaulted/vault-only
// faces never appear. No cross-owner access; foreign ids → 404. DTOs carry
// only logical file ids, names, normalized boxes, and rounded scores. Public
// share cannot reach these endpoints.
public static class PeopleEndpoints
{
    public static IEndpointRouteBuilder MapPeopleEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/people", async (
            HttpContext httpContext,
            [FromServices] NubArca.Api.Ai.Faces.PeopleService people,
            CancellationToken cancellationToken) =>
        {
            var ownerUserId = httpContext.GetCurrentUserId()!.Value;
            return Results.Ok(await people.ListPeopleAsync(ownerUserId, cancellationToken));
        }).WithName("ListPeople").RequireAuthorization();

        app.MapPost("/api/people", async (
            [FromBody] CreatePersonRequest? body,
            HttpContext httpContext,
            [FromServices] NubArca.Api.Ai.Faces.PeopleService people,
            CancellationToken cancellationToken) =>
        {
            var ownerUserId = httpContext.GetCurrentUserId()!.Value;
            var dto = await people.CreatePersonAsync(ownerUserId, body?.Name, cancellationToken);
            return Results.Created($"/api/people/{dto.PersonId}", dto);
        }).WithName("CreatePerson").RequireAuthorization();

        app.MapGet("/api/people/suggested-groups", async (
            [FromQuery] bool? review,
            HttpContext httpContext,
            [FromServices] NubArca.Api.Ai.Faces.PeopleService people,
            CancellationToken cancellationToken) =>
        {
            var ownerUserId = httpContext.GetCurrentUserId()!.Value;
            return Results.Ok(await people.ListGroupsAsync(ownerUserId, review ?? false, cancellationToken));
        }).WithName("ListSuggestedGroups").RequireAuthorization();

        app.MapGet("/api/people/{id:guid}", async (
            Guid id,
            HttpContext httpContext,
            [FromServices] NubArca.Api.Ai.Faces.PeopleService people,
            CancellationToken cancellationToken) =>
        {
            var ownerUserId = httpContext.GetCurrentUserId()!.Value;
            var dto = await people.GetPersonAsync(ownerUserId, id, cancellationToken);
            return dto is null ? Results.NotFound() : Results.Ok(dto);
        }).WithName("GetPerson").RequireAuthorization();

        app.MapPut("/api/people/{id:guid}", async (
            Guid id,
            [FromBody] RenamePersonRequest? body,
            HttpContext httpContext,
            [FromServices] NubArca.Api.Ai.Faces.PeopleService people,
            CancellationToken cancellationToken) =>
        {
            var ownerUserId = httpContext.GetCurrentUserId()!.Value;
            var dto = await people.RenamePersonAsync(ownerUserId, id, body?.Name, cancellationToken);
            return dto is null ? Results.NotFound() : Results.Ok(dto);
        }).WithName("RenamePerson").RequireAuthorization();

        app.MapDelete("/api/people/{id:guid}", async (
            Guid id,
            HttpContext httpContext,
            [FromServices] NubArca.Api.Ai.Faces.PeopleService people,
            CancellationToken cancellationToken) =>
        {
            var ownerUserId = httpContext.GetCurrentUserId()!.Value;
            var ok = await people.ArchivePersonAsync(ownerUserId, id, cancellationToken);
            return ok ? Results.NoContent() : Results.NotFound();
        }).WithName("ArchivePerson").RequireAuthorization();

        app.MapPost("/api/people/groups/{groupId:guid}/assign", async (
            Guid groupId,
            [FromBody] AssignGroupRequest? body,
            HttpContext httpContext,
            [FromServices] NubArca.Api.Ai.Faces.PeopleService people,
            CancellationToken cancellationToken) =>
        {
            var ownerUserId = httpContext.GetCurrentUserId()!.Value;
            var dto = await people.AssignGroupAsync(ownerUserId, groupId, body?.Name, body?.PersonId, cancellationToken);
            return dto is null ? Results.NotFound() : Results.Ok(dto);
        }).WithName("AssignGroup").RequireAuthorization();

        // Owner-private: bulk-ignore a suggested/review group. This is a per-face action
        // applied to every surfaceable member (each lands in "Ignorati", restorable
        // individually) — not a group-entity ignore. Generic 404 on cross-owner/missing.
        app.MapPost("/api/people/groups/{groupId:guid}/ignore", async (
            Guid groupId,
            HttpContext httpContext,
            [FromServices] NubArca.Api.Ai.Faces.PeopleService people,
            CancellationToken cancellationToken) =>
        {
            var ownerUserId = httpContext.GetCurrentUserId()!.Value;
            var count = await people.IgnoreGroupAsync(ownerUserId, groupId, cancellationToken);
            return count is null ? Results.NotFound() : Results.Ok(new { ignored = count.Value });
        }).WithName("IgnoreGroup").RequireAuthorization();

        app.MapPost("/api/people/{personId:guid}/faces", async (
            Guid personId,
            [FromBody] AddFaceRequest? body,
            HttpContext httpContext,
            [FromServices] NubArca.Api.Ai.Faces.PeopleService people,
            CancellationToken cancellationToken) =>
        {
            if (body is null || body.FaceId == Guid.Empty)
            {
                return Results.BadRequest(new { error = "Missing 'faceId'." });
            }
            var ownerUserId = httpContext.GetCurrentUserId()!.Value;
            var ok = await people.AddFaceToPersonAsync(ownerUserId, personId, body.FaceId, cancellationToken);
            return ok ? Results.NoContent() : Results.NotFound();
        }).WithName("AddFaceToPerson").RequireAuthorization();

        app.MapDelete("/api/people/{personId:guid}/faces/{faceId:guid}", async (
            Guid personId,
            Guid faceId,
            HttpContext httpContext,
            [FromServices] NubArca.Api.Ai.Faces.PeopleService people,
            CancellationToken cancellationToken) =>
        {
            var ownerUserId = httpContext.GetCurrentUserId()!.Value;
            var ok = await people.RemoveFaceFromPersonAsync(ownerUserId, personId, faceId, cancellationToken);
            return ok ? Results.NoContent() : Results.NotFound();
        }).WithName("RemoveFaceFromPerson").RequireAuthorization();

        app.MapGet("/api/people/{personId:guid}/photos", async (
            Guid personId,
            HttpContext httpContext,
            [FromServices] NubArca.Api.Ai.Faces.PeopleService people,
            CancellationToken cancellationToken) =>
        {
            var ownerUserId = httpContext.GetCurrentUserId()!.Value;
            var photos = await people.GetPersonPhotosAsync(ownerUserId, personId, cancellationToken);
            return photos is null ? Results.NotFound() : Results.Ok(photos);
        }).WithName("GetPersonPhotos").RequireAuthorization();

        app.MapGet("/api/people/{personId:guid}/similar-faces", async (
            Guid personId,
            [FromQuery] double? minSimilarity,
            [FromQuery] int? limit,
            [FromQuery] string? cursor,
            HttpContext httpContext,
            [FromServices] NubArca.Api.Ai.Faces.PeopleService people,
            CancellationToken cancellationToken) =>
        {
            if (minSimilarity is < 0.0 or > 1.0)
            {
                return Results.BadRequest(new { error = "'minSimilarity' must be between 0 and 1." });
            }
            var ownerUserId = httpContext.GetCurrentUserId()!.Value;
            var page = await people.FindSimilarFacesAsync(
                ownerUserId, personId, minSimilarity, limit ?? 30, cursor, cancellationToken);
            return page is null ? Results.NotFound() : Results.Ok(page);
        }).WithName("PersonSimilarFaces").RequireAuthorization();

        // Owner-private high-quality face crop (UI-only derived artifact). Lazily
        // generated from the ORIGINAL blob; cached. Cross-owner / vaulted / missing →
        // generic 404. Never the original bytes, never a blob id/path.
        app.MapGet("/api/people/faces/{faceId:guid}/preview", async (
            Guid faceId,
            [FromQuery] string? size,
            HttpContext httpContext,
            [FromServices] NubArca.Api.Ai.Faces.FacePreviewService previews,
            CancellationToken cancellationToken) =>
        {
            var ownerUserId = httpContext.GetCurrentUserId()!.Value;
            var requested = string.IsNullOrWhiteSpace(size) ? "small" : size!;
            if (!NubArca.Api.Ai.Faces.FacePreviewSizes.IsKnown(requested))
            {
                return Results.BadRequest(new { error = "Unknown face preview size." });
            }

            var content = await previews.EnsureAsync(faceId, ownerUserId, requested, cancellationToken);
            if (content is null)
            {
                return Results.NotFound();
            }

            SetPrivateDerivativeCache(httpContext);
            return Results.File(content.Content, content.MimeType);
        }).WithName("GetFacePreview").RequireAuthorization();

        // Owner-private full-photo context for the face viewer (fileItemId + face boxes +
        // person). Sanitized; 404 on cross-owner/vaulted/missing.
        app.MapGet("/api/people/faces/{faceId:guid}/context", async (
            Guid faceId,
            HttpContext httpContext,
            [FromServices] NubArca.Api.Ai.Faces.PeopleService people,
            CancellationToken cancellationToken) =>
        {
            var ownerUserId = httpContext.GetCurrentUserId()!.Value;
            var context = await people.GetFaceContextAsync(ownerUserId, faceId, cancellationToken);
            return context is null ? Results.NotFound() : Results.Ok(context);
        }).WithName("GetFaceContext").RequireAuthorization();

        // Owner-private manual assignment of a SINGLE face. Body: { personId } to assign
        // to an existing person, or { name } to create a new person and assign. Honors
        // one-person-per-face (moves the face if it was on another person). Returns the
        // target person (sanitized) or generic 404 (face not surfaceable / person missing).
        app.MapPost("/api/people/faces/{faceId:guid}/assign", async (
            Guid faceId,
            [FromBody] AssignFaceRequest? body,
            HttpContext httpContext,
            [FromServices] NubArca.Api.Ai.Faces.PeopleService people,
            CancellationToken cancellationToken) =>
        {
            if (body is null || (body.PersonId is null && string.IsNullOrWhiteSpace(body.Name)))
            {
                return Results.BadRequest(new { error = "Provide 'personId' or 'name'." });
            }
            var ownerUserId = httpContext.GetCurrentUserId()!.Value;
            var dto = await people.AssignFaceAsync(ownerUserId, faceId, body.PersonId, body.Name, cancellationToken);
            return dto is null ? Results.NotFound() : Results.Ok(dto);
        }).WithName("AssignFace").RequireAuthorization();

        // Owner-private: remove a face's person assignment (any person). The face becomes
        // unassigned and eligible for clustering again. Generic 404 if not assigned.
        app.MapDelete("/api/people/faces/{faceId:guid}/assignment", async (
            Guid faceId,
            HttpContext httpContext,
            [FromServices] NubArca.Api.Ai.Faces.PeopleService people,
            CancellationToken cancellationToken) =>
        {
            var ownerUserId = httpContext.GetCurrentUserId()!.Value;
            var ok = await people.RemoveFaceAssignmentAsync(ownerUserId, faceId, cancellationToken);
            return ok ? Results.NoContent() : Results.NotFound();
        }).WithName("RemoveFaceAssignment").RequireAuthorization();

        // Owner-private: dismiss a single face (mis-detection / stranger) so it stops
        // surfacing. Reversible. Cross-owner / vaulted / missing → generic 404.
        app.MapPost("/api/people/faces/{faceId:guid}/ignore", async (
            Guid faceId,
            HttpContext httpContext,
            [FromServices] NubArca.Api.Ai.Faces.PeopleService people,
            CancellationToken cancellationToken) =>
        {
            var ownerUserId = httpContext.GetCurrentUserId()!.Value;
            var ok = await people.IgnoreFaceAsync(ownerUserId, faceId, cancellationToken);
            return ok ? Results.NoContent() : Results.NotFound();
        }).WithName("IgnoreFace").RequireAuthorization();

        app.MapDelete("/api/people/faces/{faceId:guid}/ignore", async (
            Guid faceId,
            HttpContext httpContext,
            [FromServices] NubArca.Api.Ai.Faces.PeopleService people,
            CancellationToken cancellationToken) =>
        {
            var ownerUserId = httpContext.GetCurrentUserId()!.Value;
            var ok = await people.UnignoreFaceAsync(ownerUserId, faceId, cancellationToken);
            return ok ? Results.NoContent() : Results.NotFound();
        }).WithName("UnignoreFace").RequireAuthorization();

        // Owner-private: associate an entire cluster with a person. Assigns all eligible
        // unassigned members (skips faces on other people unless moveAssigned; excludes
        // ignored + vaulted). dryRun returns the counts without persisting (confirm dialog).
        // Generic 404 on cross-owner/missing person or cluster. Sanitized summary.
        app.MapPost("/api/people/{personId:guid}/clusters/{clusterId:guid}/assign", async (
            Guid personId,
            Guid clusterId,
            [FromBody] AssignClusterRequest? body,
            HttpContext httpContext,
            [FromServices] NubArca.Api.Ai.Faces.PeopleService people,
            CancellationToken cancellationToken) =>
        {
            var ownerUserId = httpContext.GetCurrentUserId()!.Value;
            var summary = await people.AssignClusterToPersonAsync(
                ownerUserId, personId, clusterId, body?.MoveAssigned ?? false, body?.DryRun ?? false, cancellationToken);
            return summary is null ? Results.NotFound() : Results.Ok(summary);
        }).WithName("AssignClusterToPerson").RequireAuthorization();

        // Owner-private: all surfaceable faces of a suggested/review group, so the whole
        // group can be reviewed face-by-face in the viewer. Generic 404 on cross-owner/missing.
        app.MapGet("/api/people/groups/{groupId:guid}/faces", async (
            Guid groupId,
            HttpContext httpContext,
            [FromServices] NubArca.Api.Ai.Faces.PeopleService people,
            CancellationToken cancellationToken) =>
        {
            var ownerUserId = httpContext.GetCurrentUserId()!.Value;
            var faces = await people.GetGroupFacesAsync(ownerUserId, groupId, cancellationToken);
            return faces is null ? Results.NotFound() : Results.Ok(faces);
        }).WithName("GroupFaces").RequireAuthorization();

        // Owner-private: faces the owner has individually ignored, so a mistaken ignore
        // can be restored (DELETE .../ignore). Keyset-paged. Sanitized DTOs.
        app.MapGet("/api/people/ignored-faces", async (
            [FromQuery] int? limit,
            [FromQuery] string? cursor,
            HttpContext httpContext,
            [FromServices] NubArca.Api.Ai.Faces.PeopleService people,
            CancellationToken cancellationToken) =>
        {
            var ownerUserId = httpContext.GetCurrentUserId()!.Value;
            var page = await people.GetIgnoredFacesAsync(ownerUserId, limit ?? 60, cursor, cancellationToken);
            return Results.Ok(page);
        }).WithName("IgnoredFaces").RequireAuthorization();

        // Owner-private: faces not yet assigned to any person (and not ignored). Keyset
        // paged; sort recent|quality|detection; hasEmbedding filter. Sanitized DTOs.
        app.MapGet("/api/people/unassigned-faces", async (
            [FromQuery] int? limit,
            [FromQuery] string? cursor,
            [FromQuery] bool? hasEmbedding,
            [FromQuery] string? sort,
            HttpContext httpContext,
            [FromServices] NubArca.Api.Ai.Faces.PeopleService people,
            CancellationToken cancellationToken) =>
        {
            var ownerUserId = httpContext.GetCurrentUserId()!.Value;
            var page = await people.GetUnassignedFacesAsync(ownerUserId, limit ?? 60, cursor, hasEmbedding, sort, cancellationToken);
            return Results.Ok(page);
        }).WithName("UnassignedFaces").RequireAuthorization();

        // Owner-private: drop cached crops for a face so they regenerate. Idempotent.
        app.MapPost("/api/people/faces/{faceId:guid}/preview/regenerate", async (
            Guid faceId,
            HttpContext httpContext,
            [FromServices] NubArca.Api.Ai.Faces.FacePreviewService previews,
            CancellationToken cancellationToken) =>
        {
            var ownerUserId = httpContext.GetCurrentUserId()!.Value;
            var ok = await previews.RegenerateAsync(faceId, ownerUserId, cancellationToken);
            return ok ? Results.NoContent() : Results.NotFound();
        }).WithName("RegenerateFacePreview").RequireAuthorization();

        MapVideoFaceEndpoints(app);
        return app;
    }

    // VFACE-02: owner-level identity over canonical VIDEO face tracks. Same
    // conventions as the static-face endpoints above — authenticated, owner-
    // scoped, generic 404 for anything the caller may not see (foreign, deleted
    // or vault-only), sanitized DTOs. Track ids appear ONLY on the authenticated
    // review surface, where the client needs them to post a decision back; the
    // person-media results carry logical file ids and millisecond intervals only.
    //
    // Nothing here assigns identity automatically and nothing creates a person:
    // suggestions are advisory, and only these explicit calls write a decision.
    private static void MapVideoFaceEndpoints(IEndpointRouteBuilder app)
    {
        // The review queue: visible video face tracks the owner has not decided
        // about yet, best evidence first.
        app.MapGet("/api/people/video-tracks/undecided", async (
            [FromQuery] int? limit,
            HttpContext httpContext,
            [FromServices] NubArca.Api.Ai.Faces.Video.VideoFaceTrackPeopleService tracks,
            CancellationToken cancellationToken) =>
        {
            var ownerUserId = httpContext.GetCurrentUserId()!.Value;
            return Results.Ok(await tracks.ListUndecidedAsync(ownerUserId, limit, cancellationToken));
        }).WithName("UndecidedVideoFaceTracks").RequireAuthorization();

        // Bounded, owner-scoped candidate people for one track. ADVISORY ONLY —
        // calling this never changes any decision.
        app.MapGet("/api/people/video-tracks/{trackId:guid}/suggestions", async (
            Guid trackId,
            [FromQuery] int? limit,
            HttpContext httpContext,
            [FromServices] NubArca.Api.Ai.Faces.Video.VideoFaceTrackIdentitySuggestionService suggestions,
            CancellationToken cancellationToken) =>
        {
            var ownerUserId = httpContext.GetCurrentUserId()!.Value;
            var dto = await suggestions.SuggestAsync(ownerUserId, trackId, limit, cancellationToken);
            return dto is null ? Results.NotFound() : Results.Ok(dto);
        }).WithName("VideoFaceTrackSuggestions").RequireAuthorization();

        // Confirm: this track shows one of MY people. Replaces any previous
        // decision, including an ignore.
        app.MapPost("/api/people/video-tracks/{trackId:guid}/assign", async (
            Guid trackId,
            [FromBody] AssignVideoFaceTrackRequest? body,
            HttpContext httpContext,
            [FromServices] NubArca.Api.Ai.Faces.Video.VideoFaceTrackPeopleService tracks,
            CancellationToken cancellationToken) =>
        {
            if (body is null || body.PersonId == Guid.Empty)
            {
                return Results.BadRequest(new { error = "Provide an existing 'personId'." });
            }

            var ownerUserId = httpContext.GetCurrentUserId()!.Value;
            var ok = await tracks.AssignAsync(ownerUserId, trackId, body.PersonId, cancellationToken);
            return ok ? Results.NoContent() : Results.NotFound();
        }).WithName("AssignVideoFaceTrack").RequireAuthorization();

        // Dismiss the track. The canonical evidence is never deleted.
        app.MapPost("/api/people/video-tracks/{trackId:guid}/ignore", async (
            Guid trackId,
            HttpContext httpContext,
            [FromServices] NubArca.Api.Ai.Faces.Video.VideoFaceTrackPeopleService tracks,
            CancellationToken cancellationToken) =>
        {
            var ownerUserId = httpContext.GetCurrentUserId()!.Value;
            var ok = await tracks.IgnoreAsync(ownerUserId, trackId, cancellationToken);
            return ok ? Results.NoContent() : Results.NotFound();
        }).WithName("IgnoreVideoFaceTrack").RequireAuthorization();

        // Back to undecided. Nothing is reassigned automatically.
        app.MapDelete("/api/people/video-tracks/{trackId:guid}/decision", async (
            Guid trackId,
            HttpContext httpContext,
            [FromServices] NubArca.Api.Ai.Faces.Video.VideoFaceTrackPeopleService tracks,
            CancellationToken cancellationToken) =>
        {
            var ownerUserId = httpContext.GetCurrentUserId()!.Value;
            var ok = await tracks.ClearAsync(ownerUserId, trackId, cancellationToken);
            return ok ? Results.NoContent() : Results.NotFound();
        }).WithName("ClearVideoFaceTrackDecision").RequireAuthorization();

        // The person-media surface, extended: videos where this person has a
        // CONFIRMED track, with the intervals the player opens at. Sits next to
        // the unchanged /photos endpoint rather than altering its shape.
        app.MapGet("/api/people/{personId:guid}/videos", async (
            Guid personId,
            HttpContext httpContext,
            [FromServices] NubArca.Api.Ai.Faces.Video.VideoFaceTrackPeopleService tracks,
            CancellationToken cancellationToken) =>
        {
            var ownerUserId = httpContext.GetCurrentUserId()!.Value;
            var videos = await tracks.GetPersonVideosAsync(ownerUserId, personId, cancellationToken);
            return videos is null ? Results.NotFound() : Results.Ok(videos);
        }).WithName("GetPersonVideos").RequireAuthorization();

        // Videos where BOTH people have confirmed tracks that overlap in time.
        app.MapGet("/api/people/{personId:guid}/co-present/{otherPersonId:guid}", async (
            Guid personId,
            Guid otherPersonId,
            HttpContext httpContext,
            [FromServices] NubArca.Api.Ai.Faces.Video.VideoFaceTrackPeopleService tracks,
            CancellationToken cancellationToken) =>
        {
            var ownerUserId = httpContext.GetCurrentUserId()!.Value;
            var videos = await tracks.GetCoPresentVideosAsync(
                ownerUserId, personId, otherPersonId, cancellationToken);
            return videos is null ? Results.NotFound() : Results.Ok(videos);
        }).WithName("GetPersonCoPresentVideos").RequireAuthorization();
    }

    // Duplicated from Program.cs's local `SetPrivateDerivativeCache` helper
    // (used by ~20 other still-inline endpoints there, so it stays put) —
    // same one-line logic: an owner-private derivative may be cached by the
    // browser, but never by a shared/proxy cache.
    private static void SetPrivateDerivativeCache(HttpContext httpContext)
    {
        httpContext.Response.Headers.CacheControl = "private, max-age=86400";
    }
}

// People v0 request bodies (owner-private). Moved from Program.cs's
// top-level records — these six were used exclusively by the People
// endpoints above.
public sealed record CreatePersonRequest(string? Name = null);
public sealed record RenamePersonRequest(string? Name = null);
public sealed record AssignGroupRequest(string? Name = null, Guid? PersonId = null);
public sealed record AddFaceRequest(Guid FaceId);
public sealed record AssignFaceRequest(Guid? PersonId = null, string? Name = null);
public sealed record AssignClusterRequest(bool MoveAssigned = false, bool DryRun = false);

// VFACE-02. Deliberately NO `name` field, unlike AssignFaceRequest: assigning a
// video track never creates a person. The owner names people through the
// existing People flows, and only an EXISTING person of theirs can be confirmed
// here.
public sealed record AssignVideoFaceTrackRequest(Guid PersonId);
