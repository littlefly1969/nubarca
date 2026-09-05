using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Internal;
using NubArca.Api.Data;
using NubArca.Api.Domain.Print;
using NubArca.Api.Party;
using NubArca.Api.Print;

namespace NubArca.Api.Endpoints;

/// <summary>
/// The public print surface: what a guest holding a print capability may see and
/// do, and nothing more.
///
/// Everything here is scoped to one print token, re-resolved on every request.
/// The responses carry no owner id, no station or device id, no device key, no
/// storage key and no original URL — a guest learns which of their party's
/// photographs they may print, how many prints are left, and what happened to
/// the one they asked for.
/// </summary>
public static class PartyPrintEndpoints
{
    private const string PartyPublicRateLimitPolicy = "party-public";
    /// <summary>
    /// Submitting is rate limited SEPARATELY from reading. The budget bounds how
    /// much paper a party can spend; it does nothing about how fast someone can
    /// ask, and each ask costs a render.
    /// </summary>
    private const string PartyPrintSubmitRateLimitPolicy = "party-print-submit";

    public static void MapPartyPrintEndpoints(this IEndpointRouteBuilder app)
    {
        // What this guest can print right now.
        app.MapGet("/api/party/{printToken}/print", async (
            string printToken,
            HttpContext httpContext,
            [FromServices] IPartyPrintAccessResolver resolver,
            [FromServices] IPartyMediaService media,
            CancellationToken cancellationToken) =>
        {
            SetNoStore(httpContext);
            var access = await resolver.ResolveAsync(printToken, cancellationToken);
            if (access is null) return Results.NotFound();

            var items = await media.ListItemsAsync(
                access.OwnerUserId, access.PartyAlbumId, cancellationToken);
            if (items is null) return Results.NotFound();

            var enc = Uri.EscapeDataString(printToken);
            // Only photographs: a video cannot be printed, and its poster is not
            // a photograph. The guest is never offered one to choose.
            var photos = items
                .Where(i => i.Kind == PartyMediaKind.Image)
                .Select(i => new PartyPrintPhotoDto(
                    i.FileItemId,
                    $"/api/party/{enc}/print/media/{i.FileItemId}/thumbnail",
                    $"/api/party/{enc}/print/media/{i.FileItemId}/preview"))
                .ToList();

            return Results.Ok(new PartyPrintManifestDto(
                access.PartyName,
                access.FooterText,
                [
                    new PartyPrintFormatDto(
                        PartyPrintProducts.Photo, access.Photo.Enabled,
                        access.Photo.Remaining,
                        PartyPrintProducts.RequiredPhotos(PartyPrintProducts.Photo)),
                    new PartyPrintFormatDto(
                        PartyPrintProducts.Strip4, access.Strip.Enabled,
                        access.Strip.Remaining,
                        PartyPrintProducts.RequiredPhotos(PartyPrintProducts.Strip4)),
                ],
                photos));
        }).WithName("GetPartyPrintManifest").RequireRateLimiting(PartyPublicRateLimitPolicy);

        // Compose it for real.
        app.MapPost("/api/party/{printToken}/print", async (
            string printToken,
            HttpContext httpContext,
            [FromBody] PartyPrintSubmitBody? body,
            [FromServices] IPartyPrintAccessResolver resolver,
            [FromServices] IPartyPrintSubmissionService submissions,
            CancellationToken cancellationToken) =>
        {
            SetNoStore(httpContext);
            if (body is null) return Results.BadRequest(new { error = "invalid" });

            // The client mints this and reuses it for retries OF THIS
            // submission. Without one there is no protection against a double
            // tap becoming a second sheet, so it is required rather than
            // optional.
            var key = httpContext.Request.Headers["Idempotency-Key"].ToString();
            if (string.IsNullOrWhiteSpace(key))
                return Results.BadRequest(new { error = "idempotency_key_required" });

            var access = await resolver.ResolveAsync(printToken, cancellationToken);
            if (access is null) return Results.NotFound();

            var result = await submissions.SubmitAsync(
                access,
                new PartyPrintSubmitRequest(
                    body.Product ?? string.Empty,
                    body.Theme ?? "pure",
                    (body.Slots ?? []).Select(s => new PartyPrintSlotRequest(
                        s.ItemId, s.CropX, s.CropY, s.CropWidth, s.CropHeight)).ToList()),
                key,
                cancellationToken);

            if (result.Ok)
            {
                var accepted = result.Accepted!;
                return Results.Accepted(value: new PartyPrintAcceptedDto(
                    accepted.JobId, accepted.PublicSequence,
                    accepted.Product, accepted.RemainingForProduct));
            }

            // Refusals carry a code the UI can speak, never a stack or an
            // internal reason.
            return result.Refusal switch
            {
                PartyPrintRefusal.BudgetExhausted =>
                    Results.Conflict(new { error = "budget_exhausted" }),
                PartyPrintRefusal.PrinterUnavailable =>
                    Results.Json(new { error = "printer_unavailable" }, statusCode: 503),
                PartyPrintRefusal.Unavailable => Results.NotFound(),
                PartyPrintRefusal.InvalidSource =>
                    Results.BadRequest(new { error = "invalid_source" }),
                PartyPrintRefusal.RenderFailed =>
                    Results.Json(new { error = "render_failed" }, statusCode: 503),
                _ => Results.BadRequest(new { error = "invalid" }),
            };
        }).WithName("SubmitPartyPrint").RequireRateLimiting(PartyPrintSubmitRateLimitPolicy);

        // The photographs a guest chooses from, through the print token.
        // Deliberately the SAME serving path as the party landing: same derived
        // sizes, same metadata stripping, same refusal to hand out an original.
        // The print capability sees no more of the album than a viewer does.
        app.MapGet("/api/party/{printToken}/print/media/{fileId:guid}/{variant}", async (
            string printToken,
            Guid fileId,
            string variant,
            HttpContext httpContext,
            [FromServices] IPartyPrintAccessResolver resolver,
            [FromServices] IPartyMediaService media,
            [FromServices] NubArca.Api.Files.IFileThumbnailService thumbnails,
            [FromServices] NubArca.Api.Metadata.IImageMetadataStripper stripper,
            CancellationToken cancellationToken) =>
        {
            // Only the two sizes the studio needs. A print token never downloads.
            if (variant is not ("thumbnail" or "preview")) return Results.NotFound();

            var access = await resolver.ResolveAsync(printToken, cancellationToken);
            if (access is null) return Results.NotFound();

            return await PartyEndpoints.ServeResolvedPartyMediaAsync(
                access.OwnerUserId, access.PartyAlbumId, fileId, variant,
                httpContext, media, thumbnails, stripper, cancellationToken);
        }).WithName("GetPartyPrintMedia").RequireRateLimiting(PartyPublicRateLimitPolicy);

        // How is my print doing?
        app.MapGet("/api/party/{printToken}/print/{jobId:guid}", async (
            string printToken,
            Guid jobId,
            HttpContext httpContext,
            [FromServices] IPartyPrintAccessResolver resolver,
            [FromServices] AppDbContext db,
            CancellationToken cancellationToken) =>
        {
            SetNoStore(httpContext);
            var access = await resolver.ResolveAsync(printToken, cancellationToken);
            if (access is null) return Results.NotFound();

            // Scoped to the party the token belongs to: a job id from elsewhere
            // is not found, rather than answered about.
            var job = await db.PrintJobs.AsNoTracking()
                .Where(j => j.Id == jobId
                    && j.OwnerUserId == access.OwnerUserId
                    && PrintJobKinds.IsParty(j.Kind))
                .Select(j => new { j.State, j.PublicSequence, j.Kind })
                .FirstOrDefaultAsync(cancellationToken);
            if (job is null) return Results.NotFound();

            return Results.Ok(new PartyPrintStatusDto(
                jobId,
                GuestState(job.State),
                job.PublicSequence ?? 0,
                job.Kind == PrintJobKinds.PartyStrip4
                    ? PartyPrintProducts.Strip4
                    : PartyPrintProducts.Photo));
        }).WithName("GetPartyPrintStatus").RequireRateLimiting(PartyPublicRateLimitPolicy);
    }

    /// <summary>
    /// The pipeline's states, reduced to what a guest can act on. Claims, leases,
    /// adapters and failure internals are the operator's business.
    /// </summary>
    private static string GuestState(string state) => state switch
    {
        PrintJobStates.Requested or PrintJobStates.Rendering => "preparing",
        PrintJobStates.Ready or PrintJobStates.Claimed => "queued",
        PrintJobStates.Submitting or PrintJobStates.Submitted => "printing",
        PrintJobStates.Completed => "completed",
        PrintJobStates.Failed or PrintJobStates.Cancelled => "failed",
        // The printer never confirmed. Said plainly, because the guest must ask
        // the staff rather than press print again.
        PrintJobStates.DeliveryUnknown => "unknown",
        _ => "preparing",
    };

    private static void SetNoStore(HttpContext httpContext) =>
        httpContext.Response.Headers.CacheControl = "no-store";
}

public sealed record PartyPrintManifestDto(
    string PartyName,
    string? FooterText,
    IReadOnlyList<PartyPrintFormatDto> Formats,
    IReadOnlyList<PartyPrintPhotoDto> Photos);

public sealed record PartyPrintFormatDto(
    string Type, bool Enabled, int Remaining, int RequiredPhotos);

/// <summary>A choosable photograph: safe derived URLs only, never an original.</summary>
public sealed record PartyPrintPhotoDto(Guid Id, string ThumbnailUrl, string PreviewUrl);

public sealed record PartyPrintSubmitBody(
    string? Product, string? Theme, List<PartyPrintSlotBody>? Slots);

public sealed record PartyPrintSlotBody(
    Guid ItemId, double CropX, double CropY, double CropWidth, double CropHeight);

public sealed record PartyPrintAcceptedDto(
    Guid JobId, long PublicSequence, string Product, int RemainingForProduct);

public sealed record PartyPrintStatusDto(
    Guid JobId, string State, long PublicSequence, string Product);
