using Microsoft.AspNetCore.Mvc;
using NubArca.Api.Audit;
using NubArca.Api.Files;
using NubArca.Api.Folders;
using NubArca.Api.Http;

namespace NubArca.Api.Endpoints;

// Extracted verbatim from Program.cs (modular-monolith cleanup, not a service
// split — same process, same DI container, same middleware pipeline). Route
// paths, HTTP methods, endpoint names, authorization metadata, status codes,
// DTOs, and audit behavior are unchanged from the original inline mappings.
//
// Folder listing (unified directory-children pagination shared by root and
// nested folders), Trash (list/permanent-delete/empty), and the folder
// lifecycle (create, rename, move, delete-preview, soft-delete incl.
// recursive, restore). File-scoped media delivery and file lifecycle live in
// the sibling Endpoints/FileEndpoints.cs. Every endpoint is owner-scoped; a
// foreign or missing/soft-deleted folder returns a generic 404 (no-leak).
// Never exposes BlobObjectId, SHA/content hash, StorageKey, or physical
// paths.
public static class FolderTrashEndpoints
{
    public static IEndpointRouteBuilder MapFolderTrashEndpoints(this IEndpointRouteBuilder app)
    {
        // Files UI v2 directory listing. Folders are returned in full on the first
        // page (no cursor); files are seek-paginated. `sort` ∈ {name,created,size,type},
        // `direction` ∈ {asc,desc}, `limit` is clamped, `cursor` resumes a prior page
        // and is bound to the (sort, direction, folder) it was issued under. Unknown
        // sort/direction or a stale/foreign cursor → 400. No-leak DTOs are unchanged.
        async Task<IResult> ListDirectoryChildrenAsync(
            Guid? parentFolderId,
            HttpContext httpContext,
            IFolderService folders,
            IFileItemService files,
            string? sortRaw,
            string? directionRaw,
            int? limitRaw,
            string? cursorRaw,
            CancellationToken cancellationToken)
        {
            var ownerUserId = httpContext.GetCurrentUserId()!.Value;

            if (parentFolderId is Guid pid)
            {
                var parent = await folders.GetByIdAsync(pid, ownerUserId, cancellationToken);
                if (parent is null)
                {
                    return Results.NotFound();
                }
            }

            if (!DirectorySort.TryParseField(sortRaw, out var sort))
            {
                return Results.BadRequest(new { error = "Invalid sort field." });
            }
            if (!DirectorySort.TryParseDirection(directionRaw, out var direction))
            {
                return Results.BadRequest(new { error = "Invalid sort direction." });
            }

            var limit = Math.Clamp(
                limitRaw ?? DirectoryListingDefaults.DefaultLimit,
                1,
                DirectoryListingDefaults.MaxLimit);

            DirectoryCursor? cursor = null;
            if (!string.IsNullOrWhiteSpace(cursorRaw))
            {
                if (!DirectoryCursor.TryParse(cursorRaw, out var parsed)
                    || !parsed.MatchesSort(sort, direction)
                    || !parsed.MatchesScope(parentFolderId))
                {
                    return Results.BadRequest(new { error = "Invalid or stale cursor." });
                }
                cursor = parsed;
            }

            // Folders are delivered once, on the first page. Later file pages carry an
            // empty folder list because the client already has the full set.
            IReadOnlyList<FolderSummary> folderList = cursor is null
                ? await folders.ListChildFoldersAsync(ownerUserId, parentFolderId, sort, direction, cancellationToken)
                : Array.Empty<FolderSummary>();

            var page = await files.ListChildFilesPageAsync(
                ownerUserId, parentFolderId, sort, direction, limit, cursor, cancellationToken);

            return Results.Ok(new FolderChildrenResponse(
                parentFolderId, folderList, page.Files, page.NextCursor, page.HasMore));
        }

        app.MapGet("/api/folders/children", (
            HttpContext httpContext,
            [FromServices] IFolderService folders,
            [FromServices] IFileItemService files,
            [FromQuery] string? sort,
            [FromQuery] string? direction,
            [FromQuery] int? limit,
            [FromQuery] string? cursor,
            CancellationToken cancellationToken)
            => ListDirectoryChildrenAsync(
                null, httpContext, folders, files, sort, direction, limit, cursor, cancellationToken))
            .WithName("ListRootChildren").RequireAuthorization();

        app.MapGet("/api/folders/{id:guid}/children", (
            Guid id,
            HttpContext httpContext,
            [FromServices] IFolderService folders,
            [FromServices] IFileItemService files,
            [FromQuery] string? sort,
            [FromQuery] string? direction,
            [FromQuery] int? limit,
            [FromQuery] string? cursor,
            CancellationToken cancellationToken)
            => ListDirectoryChildrenAsync(
                id, httpContext, folders, files, sort, direction, limit, cursor, cancellationToken))
            .WithName("ListFolderChildren").RequireAuthorization();

        app.MapGet("/api/trash", async (
            HttpContext httpContext,
            [FromServices] IFolderService folders,
            [FromServices] IFileItemService files,
            CancellationToken cancellationToken) =>
        {
            var ownerUserId = httpContext.GetCurrentUserId()!.Value;
            var folderList = await folders.ListTrashAsync(ownerUserId, null, cancellationToken);
            var fileList = await files.ListTrashAsync(ownerUserId, null, cancellationToken);
            return Results.Ok(new TrashResponse(folderList, fileList));
        }).WithName("ListTrash").RequireAuthorization();

        app.MapGet("/api/trash/folders/{id:guid}/children", async (
            Guid id,
            HttpContext httpContext,
            [FromServices] IFolderService folders,
            [FromServices] IFileItemService files,
            CancellationToken cancellationToken) =>
        {
            var ownerUserId = httpContext.GetCurrentUserId()!.Value;

            // Owner-scoped existence check that accepts the parent in either state
            // (active or soft-deleted) — a user looking at their trash needs to be
            // able to drill into a deleted folder to see its deleted children. Missing
            // and foreign-owner both collapse to 404 (no-leak).
            var parentExists = await folders.GetByIdAsync(id, ownerUserId, cancellationToken) is not null
                || (await folders.ListTrashAsync(ownerUserId, null, cancellationToken)).Any(f => f.Id == id);
            if (!parentExists)
            {
                return Results.NotFound();
            }

            var folderList = await folders.ListTrashAsync(ownerUserId, id, cancellationToken);
            var fileList = await files.ListTrashAsync(ownerUserId, id, cancellationToken);
            return Results.Ok(new TrashResponse(folderList, fileList));
        }).WithName("ListTrashFolderChildren").RequireAuthorization();

        app.MapDelete("/api/trash/files/{id:guid}", async (
            Guid id,
            HttpContext httpContext,
            [FromServices] IFileItemService files,
            [FromServices] IAuditLogger audit,
            CancellationToken cancellationToken) =>
        {
            var ownerUserId = httpContext.GetCurrentUserId()!.Value;
            var ip = httpContext.Connection.RemoteIpAddress?.ToString();

            try
            {
                var deleted = await files.PermanentDeleteAsync(ownerUserId, id, cancellationToken);
                if (!deleted)
                {
                    return Results.NotFound();
                }

                await audit.LogAsync(
                    userId: ownerUserId,
                    action: AuditActions.FilePermanentDelete,
                    entityType: AuditEntityTypes.File,
                    entityId: id,
                    ipAddress: ip,
                    metadata: null,
                    cancellationToken: cancellationToken);

                return Results.NoContent();
            }
            catch (ResourceNotInTrashException)
            {
                return Results.Conflict();
            }
        }).WithName("PermanentDeleteFile").RequireAuthorization();

        app.MapDelete("/api/trash/folders/{id:guid}", async (
            Guid id,
            HttpContext httpContext,
            [FromServices] IFolderService folders,
            [FromServices] IAuditLogger audit,
            CancellationToken cancellationToken) =>
        {
            var ownerUserId = httpContext.GetCurrentUserId()!.Value;
            var ip = httpContext.Connection.RemoteIpAddress?.ToString();

            try
            {
                var deleted = await folders.PermanentDeleteAsync(ownerUserId, id, cancellationToken);
                if (!deleted)
                {
                    return Results.NotFound();
                }

                await audit.LogAsync(
                    userId: ownerUserId,
                    action: AuditActions.FolderPermanentDelete,
                    entityType: AuditEntityTypes.Folder,
                    entityId: id,
                    ipAddress: ip,
                    metadata: null,
                    cancellationToken: cancellationToken);

                return Results.NoContent();
            }
            catch (ResourceNotInTrashException)
            {
                return Results.Conflict();
            }
            catch (FolderNotEmptyException)
            {
                return Results.Conflict();
            }
        }).WithName("PermanentDeleteFolder").RequireAuthorization();

        app.MapDelete("/api/trash", async (
            HttpContext httpContext,
            [FromServices] IFileItemService files,
            [FromServices] IFolderService folders,
            [FromServices] IAuditLogger audit,
            CancellationToken cancellationToken) =>
        {
            var ownerUserId = httpContext.GetCurrentUserId()!.Value;
            var ip = httpContext.Connection.RemoteIpAddress?.ToString();

            var deletedFiles = 0;
            var deletedFolders = 0;
            var conflicts = 0;
            var errors = 0;
            var failures = new List<EmptyTrashFailure>();

            // Phase 1 — files. Each file is independent; one stuck row never aborts
            // the run. The service's PermanentDeleteAsync mirrors the FileItemSweeper
            // transaction (drops share links + thumbnails, releases thumbnail blob
            // refcounts; physical blob reclamation stays with BlobJanitor).
            foreach (var file in await files.ListTrashAsync(ownerUserId, null, cancellationToken))
            {
                try
                {
                    var ok = await files.PermanentDeleteAsync(ownerUserId, file.Id, cancellationToken);
                    if (ok)
                    {
                        deletedFiles++;
                    }
                    // ok == false only when the row vanished between fetch and delete
                    // (concurrent restore / sweeper purge). Silently move on.
                }
                catch (ResourceNotInTrashException)
                {
                    // Lost a race to a concurrent restore.
                    conflicts++;
                    failures.Add(new EmptyTrashFailure(file.Id, "file", "not_in_trash"));
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch
                {
                    errors++;
                    failures.Add(new EmptyTrashFailure(file.Id, "file", "unexpected_error"));
                }
            }

            // Phase 2 — folders, multi-pass. Deleting child folders first lets parent
            // folders become empty within the same bulk operation. The loop
            // terminates because each pass either deletes at least one row (progress)
            // or zero (every survivor is genuinely blocked → stop).
            var pending = (await folders.ListTrashAsync(ownerUserId, null, cancellationToken))
                .Select(f => f.Id)
                .ToHashSet();

            while (pending.Count > 0)
            {
                var stillBlocked = new HashSet<Guid>();
                var deletedThisPass = 0;

                foreach (var folderId in pending)
                {
                    try
                    {
                        var ok = await folders.PermanentDeleteAsync(ownerUserId, folderId, cancellationToken);
                        if (ok)
                        {
                            deletedFolders++;
                            deletedThisPass++;
                        }
                    }
                    catch (FolderNotEmptyException)
                    {
                        // May become empty on a later pass once a child is purged.
                        stillBlocked.Add(folderId);
                    }
                    catch (ResourceNotInTrashException)
                    {
                        // Concurrent restore between fetch and delete.
                        conflicts++;
                        failures.Add(new EmptyTrashFailure(folderId, "folder", "not_in_trash"));
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch
                    {
                        errors++;
                        failures.Add(new EmptyTrashFailure(folderId, "folder", "unexpected_error"));
                    }
                }

                pending = stillBlocked;
                if (deletedThisPass == 0)
                {
                    break;
                }
            }

            // Folders that survived every pass are blocked by something we can't
            // touch from here (active children, or a deeper soft-deleted folder that
            // hit an unexpected error). Report them as "not_empty" conflicts.
            foreach (var stuck in pending)
            {
                conflicts++;
                failures.Add(new EmptyTrashFailure(stuck, "folder", "not_empty"));
            }

            await audit.LogAsync(
                userId: ownerUserId,
                action: AuditActions.TrashEmpty,
                entityType: AuditEntityTypes.Trash,
                entityId: null,
                ipAddress: ip,
                metadata: new
                {
                    deletedFiles,
                    deletedFolders,
                    conflicts,
                    errors,
                },
                cancellationToken: cancellationToken);

            return Results.Ok(new EmptyTrashResult(
                deletedFiles, deletedFolders, conflicts, errors, failures));
        }).WithName("EmptyTrash").RequireAuthorization();

        app.MapPost("/api/folders", async (
            HttpContext httpContext,
            CreateFolderRequest? body,
            [FromServices] IFolderService folders,
            [FromServices] IAuditLogger audit,
            CancellationToken cancellationToken) =>
            await CreateFolderAsync(httpContext, parentFolderId: null, body, folders, audit, cancellationToken))
            .WithName("CreateRootFolder").RequireAuthorization();

        app.MapPost("/api/folders/{id:guid}/folders", async (
            Guid id,
            HttpContext httpContext,
            CreateFolderRequest? body,
            [FromServices] IFolderService folders,
            [FromServices] IAuditLogger audit,
            CancellationToken cancellationToken) =>
            await CreateFolderAsync(httpContext, parentFolderId: id, body, folders, audit, cancellationToken))
            .WithName("CreateChildFolder").RequireAuthorization();

        app.MapPatch("/api/folders/{id:guid}/rename", async (
            Guid id,
            RenameRequest? body,
            HttpContext httpContext,
            [FromServices] IFolderService folders,
            [FromServices] IAuditLogger audit,
            CancellationToken cancellationToken) =>
        {
            var ownerUserId = httpContext.GetCurrentUserId()!.Value;
            var ip = httpContext.Connection.RemoteIpAddress?.ToString();

            if (body is null || string.IsNullOrWhiteSpace(body.Name))
            {
                return Results.BadRequest(new { error = "Missing 'name'." });
            }

            try
            {
                var renamed = await folders.RenameAsync(ownerUserId, id, body.Name, cancellationToken);
                if (renamed is null)
                {
                    return Results.NotFound();
                }

                await audit.LogAsync(
                    userId: ownerUserId,
                    action: AuditActions.FolderRename,
                    entityType: AuditEntityTypes.Folder,
                    entityId: id,
                    ipAddress: ip,
                    metadata: new { name = renamed.Name },
                    cancellationToken: cancellationToken);

                return Results.Ok(new FolderSummary(renamed.Id, renamed.Name, renamed.CreatedAt));
            }
            catch (DuplicateFolderNameException)
            {
                return Results.Conflict();
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        }).WithName("RenameFolder").RequireAuthorization();

        app.MapPatch("/api/folders/{id:guid}/move", async (
            Guid id,
            MoveRequest? body,
            HttpContext httpContext,
            [FromServices] IFolderService folders,
            [FromServices] IAuditLogger audit,
            CancellationToken cancellationToken) =>
        {
            var ownerUserId = httpContext.GetCurrentUserId()!.Value;
            var ip = httpContext.Connection.RemoteIpAddress?.ToString();

            if (body is null)
            {
                return Results.BadRequest(new { error = "Missing body." });
            }

            try
            {
                var moved = await folders.MoveAsync(ownerUserId, id, body.ParentFolderId, cancellationToken);
                if (moved is null)
                {
                    return Results.NotFound();
                }

                await audit.LogAsync(
                    userId: ownerUserId,
                    action: AuditActions.FolderMove,
                    entityType: AuditEntityTypes.Folder,
                    entityId: id,
                    ipAddress: ip,
                    metadata: new { parentFolderId = body.ParentFolderId },
                    cancellationToken: cancellationToken);

                return Results.Ok(new FolderSummary(moved.Id, moved.Name, moved.CreatedAt));
            }
            catch (FolderNotFoundException)
            {
                return Results.NotFound();
            }
            catch (DuplicateFolderNameException)
            {
                return Results.Conflict();
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        }).WithName("MoveFolder").RequireAuthorization();

        // GET /api/folders/{id}/delete-preview — safe file/folder counts for the
        // confirmation UI. No physical paths, SHA, BlobId, or storage internals.
        app.MapGet("/api/folders/{id:guid}/delete-preview", async (
            Guid id,
            HttpContext httpContext,
            [FromServices] IFolderService folders,
            CancellationToken cancellationToken) =>
        {
            var ownerUserId = httpContext.GetCurrentUserId()!.Value;
            var preview = await folders.GetDeletePreviewAsync(ownerUserId, id, cancellationToken);
            return preview is null ? Results.NotFound() : Results.Ok(preview);
        }).WithName("FolderDeletePreview").RequireAuthorization();

        // DELETE /api/folders/{id}[?recursive=true]
        // Without recursive: existing behaviour — 409 if not empty.
        // With recursive=true: soft-deletes the folder and all its descendant files
        // and sub-folders. Returns a safe summary (deletedFileCount, deletedFolderCount).
        app.MapDelete("/api/folders/{id:guid}", async (
            Guid id,
            [FromQuery] bool? recursive,
            HttpContext httpContext,
            [FromServices] IFolderService folders,
            [FromServices] IAuditLogger audit,
            CancellationToken cancellationToken) =>
        {
            var ownerUserId = httpContext.GetCurrentUserId()!.Value;
            var ip = httpContext.Connection.RemoteIpAddress?.ToString();

            if (recursive == true)
            {
                var result = await folders.SoftDeleteRecursiveAsync(ownerUserId, id, cancellationToken);
                if (result is null)
                {
                    return Results.NotFound();
                }

                await audit.LogAsync(
                    userId: ownerUserId,
                    action: AuditActions.FolderDeleteRecursive,
                    entityType: AuditEntityTypes.Folder,
                    entityId: id,
                    ipAddress: ip,
                    metadata: new { result.DeletedFileCount, result.DeletedFolderCount },
                    cancellationToken: cancellationToken);

                return Results.Ok(result);
            }

            try
            {
                var deleted = await folders.SoftDeleteAsync(ownerUserId, id, cancellationToken);
                if (!deleted)
                {
                    return Results.NotFound();
                }

                await audit.LogAsync(
                    userId: ownerUserId,
                    action: AuditActions.FolderDelete,
                    entityType: AuditEntityTypes.Folder,
                    entityId: id,
                    ipAddress: ip,
                    metadata: null,
                    cancellationToken: cancellationToken);

                return Results.NoContent();
            }
            catch (FolderNotEmptyException)
            {
                return Results.Conflict();
            }
        }).WithName("DeleteFolder").RequireAuthorization();

        app.MapPost("/api/folders/{id:guid}/restore", async (
            Guid id,
            HttpContext httpContext,
            [FromServices] IFolderService folders,
            [FromServices] IAuditLogger audit,
            CancellationToken cancellationToken) =>
        {
            var ownerUserId = httpContext.GetCurrentUserId()!.Value;
            var ip = httpContext.Connection.RemoteIpAddress?.ToString();

            try
            {
                var restored = await folders.RestoreAsync(ownerUserId, id, cancellationToken);
                if (restored is null)
                {
                    return Results.NotFound();
                }

                await audit.LogAsync(
                    userId: ownerUserId,
                    action: AuditActions.FolderRestore,
                    entityType: AuditEntityTypes.Folder,
                    entityId: id,
                    ipAddress: ip,
                    metadata: new { name = restored.Name, parentFolderId = restored.ParentFolderId },
                    cancellationToken: cancellationToken);

                return Results.Ok(new FolderSummary(restored.Id, restored.Name, restored.CreatedAt));
            }
            catch (DuplicateFolderNameException)
            {
                return Results.Conflict();
            }
            catch (RestoreParentDeletedException)
            {
                return Results.Conflict();
            }
        }).WithName("RestoreFolder").RequireAuthorization();


        return app;
    }

    // Shared by CreateRootFolder and CreateChildFolder (root vs. folder-nested
    // only differ in `parentFolderId`).
    private static async Task<IResult> CreateFolderAsync(
        HttpContext httpContext,
        Guid? parentFolderId,
        CreateFolderRequest? body,
        IFolderService folders,
        IAuditLogger audit,
        CancellationToken cancellationToken)
    {
        var ownerUserId = httpContext.GetCurrentUserId()!.Value;
        var ip = httpContext.Connection.RemoteIpAddress?.ToString();

        if (body is null || string.IsNullOrWhiteSpace(body.Name))
        {
            return Results.BadRequest(new { error = "Missing 'name'." });
        }

        try
        {
            var created = await folders.CreateAsync(ownerUserId, parentFolderId, body.Name, cancellationToken);
            await audit.LogAsync(
                userId: ownerUserId,
                action: AuditActions.FolderCreate,
                entityType: AuditEntityTypes.Folder,
                entityId: created.Id,
                ipAddress: ip,
                metadata: new { name = created.Name, parentFolderId },
                cancellationToken: cancellationToken);

            var summary = new FolderSummary(created.Id, created.Name, created.CreatedAt);
            return Results.Created($"/api/folders/{created.Id}/children", summary);
        }
        catch (DuplicateFolderNameException)
        {
            return Results.Conflict();
        }
        catch (FolderNotFoundException)
        {
            return Results.NotFound();
        }
        catch (ArgumentException ex)
        {
            return Results.BadRequest(new { error = ex.Message });
        }
    }
}
