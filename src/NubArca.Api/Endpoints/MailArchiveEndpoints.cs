using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using NubArca.Api.Audit;
using NubArca.Api.Http;
using NubArca.Api.MailArchive;

namespace NubArca.Api.Endpoints;

// MailArchive — owner-private consultation of imported Outlook PST archives
// (docs/mailarchive/contract.md §5). Every route is authenticated and scoped
// to the caller; a foreign or missing id is a plain 404.
//
// There is deliberately NO route that changes a message: messages can be
// listed, read, searched and deleted, never renamed, moved or edited. Their
// content is not a FileItem, so no generic file route can reach it either.
public static class MailArchiveEndpoints
{
    public static IEndpointRouteBuilder MapMailArchiveEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/mail").RequireAuthorization();

        group.MapGet("/status", (
            [FromServices] MailArchiveService service,
            [FromServices] IOptions<MailArchiveOptions> options) =>
            Results.Ok(service.Status(options.Value)))
            .WithName("GetMailArchiveStatus");

        group.MapGet("/sources", async (
            HttpContext http,
            [FromServices] MailArchiveService service,
            CancellationToken cancellationToken) =>
        {
            NoStore(http);
            var items = await service.ListSourcesAsync(Owner(http), cancellationToken);
            return Results.Ok(new { items });
        }).WithName("ListMailSources");

        group.MapPost("/archives", async (
            HttpContext http,
            [FromBody] MailStartImportRequest? body,
            [FromServices] MailArchiveService service,
            [FromServices] IAuditLogger audit,
            CancellationToken cancellationToken) =>
        {
            if (body is null || body.FileItemId == Guid.Empty)
            {
                return Results.BadRequest(new { error = "file_required" });
            }
            var owner = Owner(http);
            var result = await service.StartFromFileAsync(owner, body.FileItemId, cancellationToken);
            return await StartResultAsync(http, result, audit, owner, cancellationToken,
                r => Results.Json(new MailStartImportResponse(r.Archive!, r.Run!), statusCode: StatusCodes.Status202Accepted));
        }).WithName("StartMailImport");

        group.MapGet("/archives", async (
            HttpContext http,
            [FromServices] MailArchiveService service,
            CancellationToken cancellationToken) =>
        {
            NoStore(http);
            var items = await service.ListArchivesAsync(Owner(http), cancellationToken);
            return Results.Ok(new { items });
        }).WithName("ListMailArchives");

        group.MapGet("/archives/{id:guid}", async (
            Guid id,
            HttpContext http,
            [FromServices] MailArchiveService service,
            CancellationToken cancellationToken) =>
        {
            NoStore(http);
            var archive = await service.GetArchiveAsync(Owner(http), id, cancellationToken);
            return archive is null ? Results.NotFound() : Results.Ok(archive);
        }).WithName("GetMailArchive");

        group.MapPost("/archives/{id:guid}/import", async (
            Guid id,
            HttpContext http,
            [FromServices] MailArchiveService service,
            [FromServices] IAuditLogger audit,
            CancellationToken cancellationToken) =>
        {
            var owner = Owner(http);
            var result = await service.ReimportAsync(owner, id, cancellationToken);
            return await StartResultAsync(http, result, audit, owner, cancellationToken,
                r => Results.Json(r.Run!, statusCode: StatusCodes.Status202Accepted));
        }).WithName("ReimportMailArchive");

        group.MapGet("/archives/{id:guid}/folders", async (
            Guid id,
            HttpContext http,
            [FromServices] MailArchiveService service,
            CancellationToken cancellationToken) =>
        {
            NoStore(http);
            var items = await service.ListFoldersAsync(Owner(http), id, cancellationToken);
            return items is null ? Results.NotFound() : Results.Ok(new { items });
        }).WithName("ListMailFolders");

        group.MapGet("/runs/{id:guid}", async (
            Guid id,
            HttpContext http,
            [FromServices] MailArchiveService service,
            CancellationToken cancellationToken) =>
        {
            NoStore(http);
            var run = await service.GetRunAsync(Owner(http), id, cancellationToken);
            return run is null ? Results.NotFound() : Results.Ok(run);
        }).WithName("GetMailImportRun");

        group.MapGet("/runs/{id:guid}/failures", async (
            Guid id,
            string? cursor,
            HttpContext http,
            [FromServices] MailArchiveService service,
            CancellationToken cancellationToken) =>
        {
            NoStore(http);
            var page = await service.ListFailuresAsync(Owner(http), id, cursor, cancellationToken);
            return page is null ? Results.NotFound() : Results.Ok(page);
        }).WithName("ListMailImportFailures");

        group.MapPost("/runs/{id:guid}/cancel", async (
            Guid id,
            HttpContext http,
            [FromServices] MailArchiveService service,
            [FromServices] IAuditLogger audit,
            CancellationToken cancellationToken) =>
        {
            var owner = Owner(http);
            var (found, active, run) = await service.CancelRunAsync(owner, id, cancellationToken);
            if (!found)
            {
                return Results.NotFound();
            }
            if (!active)
            {
                return Results.Conflict(new { error = "not_active" });
            }
            await audit.LogAsync(owner, AuditActions.MailImportCancel, AuditEntityTypes.MailArchive, run!.ArchiveId,
                Ip(http), new { runId = run.Id }, cancellationToken);
            return Results.Json(run, statusCode: StatusCodes.Status202Accepted);
        }).WithName("CancelMailImportRun");

        group.MapGet("/messages", async (
            HttpContext http,
            Guid? archiveId,
            Guid? folderId,
            string? q,
            string? from,
            string? to,
            string? subject,
            DateTime? after,
            DateTime? before,
            bool? hasAttachments,
            string? cursor,
            int? limit,
            [FromServices] MailMessageQueryService queries,
            CancellationToken cancellationToken) =>
        {
            NoStore(http);
            if (limit is < 1 or > MailMessageQueryService.MaxLimit)
            {
                return Results.BadRequest(new { error = "invalid_limit" });
            }
            var page = await queries.SearchAsync(Owner(http),
                new MailMessageQuery(archiveId, folderId, q, from, to, subject,
                    after?.ToUniversalTime(), before?.ToUniversalTime(), hasAttachments, cursor,
                    limit ?? MailMessageQueryService.DefaultLimit),
                cancellationToken);
            return page is null ? Results.BadRequest(new { error = "invalid_cursor" }) : Results.Ok(page);
        }).WithName("SearchMailMessages");

        group.MapGet("/messages/{id:guid}", async (
            Guid id,
            HttpContext http,
            [FromServices] MailMessageQueryService queries,
            CancellationToken cancellationToken) =>
        {
            NoStore(http);
            var message = await queries.GetAsync(Owner(http), id, cancellationToken);
            return message is null ? Results.NotFound() : Results.Ok(message);
        }).WithName("GetMailMessage");

        group.MapDelete("/messages/{id:guid}", async (
            Guid id,
            HttpContext http,
            [FromServices] MailMessageDeletionService deletion,
            [FromServices] IAuditLogger audit,
            CancellationToken cancellationToken) =>
        {
            var owner = Owner(http);
            var outcome = await deletion.DeleteAsync(owner, id, cancellationToken);
            if (outcome == MailDeleteOutcome.NotFound)
            {
                return Results.NotFound();
            }
            if (outcome == MailDeleteOutcome.Deleted)
            {
                await audit.LogAsync(owner, AuditActions.MailMessageDelete, AuditEntityTypes.MailMessage, id,
                    Ip(http), null, cancellationToken);
            }
            return Results.NoContent();
        }).WithName("DeleteMailMessage");

        group.MapGet("/file-origins/{fileItemId:guid}", async (
            Guid fileItemId,
            HttpContext http,
            [FromServices] MailMessageQueryService queries,
            CancellationToken cancellationToken) =>
        {
            NoStore(http);
            var items = await queries.FileOriginsAsync(Owner(http), fileItemId, cancellationToken);
            return Results.Ok(new { items });
        }).WithName("GetMailFileOrigins");

        return app;
    }

    private static async Task<IResult> StartResultAsync(
        HttpContext http, MailStartResult result, IAuditLogger audit, Guid owner, CancellationToken cancellationToken,
        Func<MailStartResult, IResult> started)
    {
        switch (result.Outcome)
        {
            case MailStartOutcome.Started:
                await audit.LogAsync(owner, AuditActions.MailImportStart, AuditEntityTypes.MailArchive,
                    result.Run!.ArchiveId, Ip(http), new { runId = result.Run.Id }, cancellationToken);
                return started(result);
            case MailStartOutcome.NotFound:
                return Results.NotFound();
            case MailStartOutcome.NotPst:
                return Results.BadRequest(new { error = "not_pst" });
            case MailStartOutcome.AlreadyRunning:
                return Results.Conflict(new { error = "import_running", runId = result.RunningRunId });
            case MailStartOutcome.SourceUnavailable:
                return Results.Conflict(new { error = "source_unavailable" });
            default:
                return Results.Json(new { error = "unavailable" }, statusCode: StatusCodes.Status503ServiceUnavailable);
        }
    }

    private static Guid Owner(HttpContext http) => http.GetCurrentUserId()!.Value;

    private static string? Ip(HttpContext http) => http.Connection.RemoteIpAddress?.ToString();

    private static void NoStore(HttpContext http) => http.Response.Headers.CacheControl = "no-store";
}
