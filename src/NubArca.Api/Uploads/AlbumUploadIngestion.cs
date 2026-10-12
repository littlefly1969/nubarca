using System.Security.Cryptography;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.EntityFrameworkCore;
using NubArca.Api.Albums;
using NubArca.Api.Albums.Sharing;
using NubArca.Api.Audit;
using NubArca.Api.Data;
using NubArca.Api.Domain;

namespace NubArca.Api.Uploads;

/// <summary>The album an upload is for, as the uploader may reach it.</summary>
public sealed record AlbumUploadDestination(Guid AlbumId, Guid AlbumOwnerUserId, string AlbumName, bool IsOwner);

public enum AlbumUploadAccess
{
    Allowed,
    // Missing, or not shared with the caller: indistinguishable on purpose.
    NotFound,
    // A viewer: may see the album, may not add to it.
    NotPermitted,
}

/// <summary>Only photographs and videos belong in an album.</summary>
public sealed class AlbumUploadNotMediaException : Exception
{
    public AlbumUploadNotMediaException()
        : base("Only photos and videos can be added to an album.")
    {
    }
}

/// <summary>What an album upload answers: the file, and how it reached the album.</summary>
public sealed record AlbumUploadResult(
    Guid Id,
    string Name,
    string MimeType,
    long SizeBytes,
    DateTime CreatedAt,
    int? Width,
    int? Height,
    Guid AlbumId,
    // The uploader already had these bytes: that file went into the album and
    // nothing new was stored.
    bool LinkedExisting,
    // The file was already in the album before this upload.
    bool AlreadyInAlbum);

public interface IAlbumUploadIngestion
{
    Task<(AlbumUploadAccess Access, AlbumUploadDestination? Destination)> ResolveAsync(
        Guid albumId, Guid actorUserId, CancellationToken cancellationToken);

    /// <summary>
    /// Stores <paramref name="content"/> for the album, or finds the file the
    /// uploader already owns with the same bytes. The album link is made
    /// separately by <see cref="AddToAlbumAsync"/>, so a replayed upload links too.
    /// </summary>
    Task<IngestOutcome> IngestAsync(
        AlbumUploadDestination destination,
        Guid actorUserId,
        string fileName,
        string? contentType,
        Guid? uploadOperationClaimToken,
        string? ipAddress,
        Stream content,
        CancellationToken cancellationToken);

    /// <summary>Puts the file in the album (idempotent) and shapes the answer.</summary>
    Task<IResult> AddToAlbumAsync(
        AlbumUploadDestination destination,
        Guid actorUserId,
        UploadAnswer answer,
        string? ipAddress,
        CancellationToken cancellationToken);
}

// UPLOADING INTO AN ALBUM. An album holds files; it is not a place files live.
// So an upload "into" an album is two steps: the bytes become the uploader's
// own file in their folder "Album/<album name>" (renamed on a clash, never
// refused), then that file is added to the album — by the owner's path for
// the owner, by the contribution path for a contributor, so every rule those
// paths enforce still holds. When the uploader already owns the same bytes,
// that file is added instead and nothing is stored twice.
public sealed class AlbumUploadIngestion : IAlbumUploadIngestion
{
    // The folder every album upload lands under, in the uploader's own space.
    public const string AlbumsFolderName = "Album";

    private static readonly FileExtensionContentTypeProvider ContentTypes = CreateContentTypes();

    private readonly AppDbContext _db;
    private readonly IAlbumAccessResolver _access;
    private readonly IAlbumService _albums;
    private readonly IAlbumSharingService _sharing;
    private readonly IOwnerUploadIngestion _ingestion;
    private readonly IUploadIdempotencyService _operations;
    private readonly IAuditLogger _audit;

    public AlbumUploadIngestion(
        AppDbContext db,
        IAlbumAccessResolver access,
        IAlbumService albums,
        IAlbumSharingService sharing,
        IOwnerUploadIngestion ingestion,
        IUploadIdempotencyService operations,
        IAuditLogger audit)
    {
        _db = db;
        _access = access;
        _albums = albums;
        _sharing = sharing;
        _ingestion = ingestion;
        _operations = operations;
        _audit = audit;
    }

    public async Task<(AlbumUploadAccess Access, AlbumUploadDestination? Destination)> ResolveAsync(
        Guid albumId, Guid actorUserId, CancellationToken cancellationToken)
    {
        var grant = await _access.ResolveAsync(albumId, actorUserId, cancellationToken);
        if (grant is null)
        {
            return (AlbumUploadAccess.NotFound, null);
        }
        if (!grant.IsOwner && !AlbumRoles.CanContribute(grant.Role))
        {
            return (AlbumUploadAccess.NotPermitted, null);
        }

        var name = await _db.Albums.AsNoTracking()
            .Where(a => a.Id == albumId)
            .Select(a => a.Name)
            .FirstOrDefaultAsync(cancellationToken);
        return name is null
            ? (AlbumUploadAccess.NotFound, null)
            : (AlbumUploadAccess.Allowed, new AlbumUploadDestination(albumId, grant.AlbumOwnerUserId, name, grant.IsOwner));
    }

    public async Task<IngestOutcome> IngestAsync(
        AlbumUploadDestination destination,
        Guid actorUserId,
        string fileName,
        string? contentType,
        Guid? uploadOperationClaimToken,
        string? ipAddress,
        Stream content,
        CancellationToken cancellationToken)
    {
        var mediaType = MediaTypeOf(fileName, contentType)
            ?? throw new AlbumUploadNotMediaException();

        if (content.CanSeek)
        {
            var start = content.Position;
            var sha256 = Convert.ToHexStringLower(await SHA256.HashDataAsync(content, cancellationToken));
            content.Position = start;

            var existing = await FindOwnedCopyAsync(actorUserId, destination.AlbumId, sha256, cancellationToken);
            if (existing is not null)
            {
                if (uploadOperationClaimToken is Guid claimToken)
                {
                    await _operations.CompleteWithExistingAsync(claimToken, existing.Id, cancellationToken);
                }
                return IngestOutcome.Existing(existing);
            }
        }

        var created = await _ingestion.IngestAsync(
            new OwnerUploadRequest(
                actorUserId,
                ParentFolderId: null,
                FileName: fileName,
                RelativePath: $"{AlbumsFolderName}/{FolderNameFor(destination.AlbumName)}/{Path.GetFileName(fileName)}",
                ContentType: mediaType,
                Conflict: UploadNameConflictPolicy.Rename,
                UploadOperationClaimToken: uploadOperationClaimToken,
                IpAddress: ipAddress),
            content,
            cancellationToken);
        return IngestOutcome.Success(created);
    }

    public async Task<IResult> AddToAlbumAsync(
        AlbumUploadDestination destination,
        Guid actorUserId,
        UploadAnswer answer,
        string? ipAddress,
        CancellationToken cancellationToken)
    {
        var fileId = answer.File.Id;
        var alreadyInAlbum = await _db.AlbumItems.AsNoTracking()
            .AnyAsync(ai => ai.AlbumId == destination.AlbumId && ai.FileItemId == fileId, cancellationToken);

        if (!alreadyInAlbum)
        {
            if (destination.IsOwner)
            {
                if (!await _albums.AddItemAsync(destination.AlbumId, actorUserId, fileId, cancellationToken))
                {
                    return Results.NotFound();
                }
            }
            else
            {
                switch (await _sharing.ContributeAsync(actorUserId, destination.AlbumId, fileId, cancellationToken))
                {
                    case AlbumContributionResult.Ok:
                        await _audit.LogAsync(actorUserId, AuditActions.AlbumContributionAdd,
                            AuditEntityTypes.AlbumContribution, fileId, ipAddress,
                            new
                            {
                                albumId = destination.AlbumId,
                                albumOwnerUserId = destination.AlbumOwnerUserId,
                                sourceOwnerUserId = actorUserId,
                            },
                            cancellationToken);
                        break;
                    case AlbumContributionResult.AlreadyPresent:
                        alreadyInAlbum = true;
                        break;
                    case AlbumContributionResult.RoleNotPermitted:
                        return Results.Forbid();
                    case AlbumContributionResult.FileNotContributable:
                        // Stored, but the server did not recognise it as a
                        // photo or video: it stays in the uploader's folder.
                        return NotMedia();
                    default:
                        return Results.NotFound();
                }
            }
        }

        var file = answer.File;
        var result = new AlbumUploadResult(
            file.Id, file.Name, file.MimeType, file.SizeBytes, file.CreatedAt, file.Width, file.Height,
            destination.AlbumId, answer.LinkedExisting, alreadyInAlbum);
        return answer.Replayed || answer.LinkedExisting
            ? Results.Ok(result)
            : Results.Created($"/api/files/{file.Id}/content", result);
    }

    public static IResult NotMedia() => Results.Json(
        new { error = "Only photos and videos can be added to an album.", code = "not_media" },
        statusCode: StatusCodes.Status415UnsupportedMediaType);

    // The uploader's own active photo or video with exactly these bytes. One
    // already in this album wins, so a repeated upload changes nothing.
    private async Task<FileItem?> FindOwnedCopyAsync(
        Guid actorUserId, Guid albumId, string sha256, CancellationToken cancellationToken)
    {
        var candidates = _db.FileItems.AsNoTracking()
            .Where(f => f.OwnerUserId == actorUserId
                && f.DeletedAt == null
                && f.MediaLibraryState == MediaLibraryState.Active)
            .Join(_db.BlobObjects.AsNoTracking().Where(b => b.Sha256 == sha256),
                f => f.BlobObjectId, b => b.Id, (f, b) => f)
            .Join(_db.BlobMetadata.AsNoTracking()
                    .Where(m => m.MediaCategory == MediaCategories.Image || m.MediaCategory == MediaCategories.Video),
                f => f.BlobObjectId, m => m.BlobObjectId, (f, m) => f);

        return await candidates
            .OrderByDescending(f => _db.AlbumItems.Any(ai => ai.AlbumId == albumId && ai.FileItemId == f.Id))
            .ThenBy(f => f.CreatedAt)
            .FirstOrDefaultAsync(cancellationToken);
    }

    /// <summary>
    /// The media type an album upload is stored with: the declared one when it
    /// is a photo or video, else the one its extension names (a phone sending
    /// HEIC as application/octet-stream). Null when it is neither.
    /// </summary>
    internal static string? MediaTypeOf(string fileName, string? declared)
    {
        if (IsMedia(declared))
        {
            return declared!.Trim();
        }
        return ContentTypes.TryGetContentType(fileName, out var byExtension) && IsMedia(byExtension)
            ? byExtension
            : null;
    }

    private static bool IsMedia(string? mediaType)
    {
        var category = MediaCategories.FromMimeType(mediaType);
        return category is MediaCategories.Image or MediaCategories.Video;
    }

    /// <summary>The album's name as one folder segment: no separators, no control characters, no dot-only names.</summary>
    internal static string FolderNameFor(string albumName)
    {
        var cleaned = new string(albumName
            .Select(c => c is '/' or '\\' || char.IsControl(c) ? ' ' : c)
            .ToArray());
        cleaned = string.Join(' ', cleaned.Split(' ', StringSplitOptions.RemoveEmptyEntries)).Trim().TrimEnd('.');
        if (cleaned.Length > 120)
        {
            cleaned = cleaned[..120].TrimEnd();
        }
        return cleaned.Length == 0 || cleaned.All(c => c == '.') ? AlbumsFolderName : cleaned;
    }

    private static FileExtensionContentTypeProvider CreateContentTypes()
    {
        var provider = new FileExtensionContentTypeProvider();
        // Phone formats the framework's table predates.
        provider.Mappings[".heic"] = "image/heic";
        provider.Mappings[".heif"] = "image/heif";
        provider.Mappings[".avif"] = "image/avif";
        provider.Mappings[".dng"] = "image/x-adobe-dng";
        provider.Mappings[".mkv"] = "video/x-matroska";
        provider.Mappings[".3gp"] = "video/3gpp";
        return provider;
    }
}
