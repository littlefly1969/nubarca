using Microsoft.EntityFrameworkCore;
using NubArca.Api.Data;
using NubArca.Api.Domain;

namespace NubArca.Api.Metadata;

public sealed class MetadataService : IMetadataService
{
    private const int MaxTitleLength = 255;
    private const int MaxDescriptionLength = 2000;
    private const int MaxLocationLength = 512;

    private readonly AppDbContext _db;
    private readonly TimeProvider _clock;

    public MetadataService(AppDbContext db, TimeProvider clock)
    {
        _db = db;
        _clock = clock;
    }

    public async Task<FileMetadataResponse?> GetFileMetadataAsync(
        Guid ownerUserId,
        Guid fileItemId,
        CancellationToken cancellationToken = default)
    {
        var file = await _db.FileItems
            .AsNoTracking()
            .FirstOrDefaultAsync(
                f => f.Id == fileItemId
                    && f.OwnerUserId == ownerUserId
                    && f.DeletedAt == null,
                cancellationToken);
        if (file is null)
        {
            return null;
        }

        var blobMeta = await _db.BlobMetadata
            .AsNoTracking()
            .FirstOrDefaultAsync(m => m.BlobObjectId == file.BlobObjectId, cancellationToken);

        var userMeta = await _db.FileItemUserMetadata
            .AsNoTracking()
            .FirstOrDefaultAsync(m => m.FileItemId == file.Id, cancellationToken);

        return BuildResponse(file, blobMeta, userMeta);
    }

    public async Task<FileMetadataResponse?> UpdateUserMetadataAsync(
        Guid ownerUserId,
        Guid fileItemId,
        UpdateFileMetadataRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        // Owner-scoped existence check: only the owner of an active file may
        // edit its metadata. Missing / foreign / soft-deleted all collapse to
        // null → 404 (no info-leak oracle).
        var file = await _db.FileItems
            .AsNoTracking()
            .FirstOrDefaultAsync(
                f => f.Id == fileItemId
                    && f.OwnerUserId == ownerUserId
                    && f.DeletedAt == null,
                cancellationToken);
        if (file is null)
        {
            return null;
        }

        var title = NormalizeText(request.Title, MaxTitleLength, nameof(request.Title));
        var description = NormalizeText(request.Description, MaxDescriptionLength, nameof(request.Description));
        var location = NormalizeText(request.LocationOverride, MaxLocationLength, nameof(request.LocationOverride));
        var tagsJson = MetadataTags.NormalizeToJson(request.Tags);

        if (request.Rating is int rating && (rating < 0 || rating > 5))
        {
            throw new ArgumentException("Rating must be between 0 and 5.", nameof(request.Rating));
        }

        var now = _clock.GetUtcNow().UtcDateTime;

        var userMeta = await _db.FileItemUserMetadata
            .FirstOrDefaultAsync(m => m.FileItemId == file.Id, cancellationToken);

        if (userMeta is null)
        {
            userMeta = new FileItemUserMetadata
            {
                Id = Guid.NewGuid(),
                FileItemId = file.Id,
                CreatedAt = now,
            };
            _db.FileItemUserMetadata.Add(userMeta);
        }

        // Full replace of the editable fields (omitted = cleared).
        userMeta.Title = title;
        userMeta.Description = description;
        userMeta.TagsJson = tagsJson;
        userMeta.Rating = request.Rating;
        userMeta.IsFavorite = request.Favorite ?? false;
        userMeta.DateTakenOverride = request.DateTakenOverride;
        userMeta.LocationOverride = location;
        userMeta.UpdatedAt = now;

        await _db.SaveChangesAsync(cancellationToken);

        var blobMeta = await _db.BlobMetadata
            .AsNoTracking()
            .FirstOrDefaultAsync(m => m.BlobObjectId == file.BlobObjectId, cancellationToken);

        // Keep the denormalized gallery sort column in sync: a DateTakenOverride
        // set/clear changes the effective capture date for this one file.
        var (effDate, effSource) = EffectiveDateTakenSources.Compute(
            userMeta.DateTakenOverride, blobMeta?.DateTaken, file.CreatedAt);
        await _db.FileItems
            .Where(f => f.Id == file.Id)
            .ExecuteUpdateAsync(
                s => s
                    .SetProperty(f => f.EffectiveDateTaken, _ => effDate)
                    .SetProperty(f => f.EffectiveDateTakenSource, _ => effSource),
                cancellationToken);

        return BuildResponse(file, blobMeta, userMeta);
    }

    public async Task<bool?> SetFavoriteAsync(
        Guid ownerUserId,
        Guid fileItemId,
        bool favorite,
        CancellationToken cancellationToken = default)
    {
        // Same owner-scoped existence rule as the full update: missing /
        // foreign / soft-deleted collapse to null → 404 (no info-leak oracle).
        var file = await _db.FileItems
            .AsNoTracking()
            .FirstOrDefaultAsync(
                f => f.Id == fileItemId
                    && f.OwnerUserId == ownerUserId
                    && f.DeletedAt == null,
                cancellationToken);
        if (file is null)
        {
            return null;
        }

        var now = _clock.GetUtcNow().UtcDateTime;
        var userMeta = await _db.FileItemUserMetadata
            .FirstOrDefaultAsync(m => m.FileItemId == file.Id, cancellationToken);

        if (userMeta is null)
        {
            // No row + unfavorite is already the effective state — do not
            // materialize a row just to store the default.
            if (!favorite)
            {
                return false;
            }
            userMeta = new FileItemUserMetadata
            {
                Id = Guid.NewGuid(),
                FileItemId = file.Id,
                CreatedAt = now,
            };
            _db.FileItemUserMetadata.Add(userMeta);
        }

        if (userMeta.IsFavorite != favorite)
        {
            userMeta.IsFavorite = favorite;
            userMeta.UpdatedAt = now;
            await _db.SaveChangesAsync(cancellationToken);
        }

        return favorite;
    }

    // Combines the two metadata sources into the effective display view. When
    // the blob-derived row is missing (a file uploaded before this model
    // existed) the blob facts fall back to what the FileItem itself carries.
    private static FileMetadataResponse BuildResponse(
        FileItem file,
        BlobMetadata? blobMeta,
        FileItemUserMetadata? userMeta)
    {
        var width = blobMeta?.Width ?? file.Width;
        var height = blobMeta?.Height ?? file.Height;
        long? pixelCount = blobMeta?.PixelCount
            ?? (width is int w && height is int h ? (long)w * h : null);

        var blob = new BlobDerivedMetadata(
            MediaCategory: blobMeta?.MediaCategory ?? MediaCategories.FromMimeType(file.MimeType),
            DetectedContentType: blobMeta?.DetectedContentType,
            DetectedFormat: blobMeta?.DetectedFormat,
            Width: width,
            Height: height,
            PixelCount: pixelCount,
            ThumbnailStatus: blobMeta?.ThumbnailStatus ?? MetadataStatuses.Unknown,
            ExtractionStatus: blobMeta?.ExtractionStatus ?? MetadataStatuses.Pending,
            Embedded: BuildEmbedded(blobMeta),
            Video: BuildVideo(blobMeta));

        var user = new UserMetadataView(
            Title: userMeta?.Title,
            Description: userMeta?.Description,
            Tags: MetadataTags.Deserialize(userMeta?.TagsJson),
            Rating: userMeta?.Rating,
            Favorite: userMeta?.IsFavorite ?? false,
            DateTakenOverride: userMeta?.DateTakenOverride,
            LocationOverride: userMeta?.LocationOverride);

        return new FileMetadataResponse(
            file.Id,
            file.Name,
            file.MimeType,
            file.SizeBytes,
            file.CreatedAt,
            file.UpdatedAt,
            blob,
            user,
            BuildEffective(file, blobMeta, userMeta));
    }

    // Effective metadata layering (slice 56):
    //   DateTaken: user override → embedded DateTaken → upload time.
    //   DisplayName: user title → file name.
    //   Location: user override only (embedded GPS stays internal).
    private static EffectiveMetadata BuildEffective(
        FileItem file,
        BlobMetadata? blobMeta,
        FileItemUserMetadata? userMeta)
    {
        var (dateTaken, source) = EffectiveDateTakenSources.Compute(
            userMeta?.DateTakenOverride, blobMeta?.DateTaken, file.CreatedAt);

        return new EffectiveMetadata(
            DisplayName: !string.IsNullOrWhiteSpace(userMeta?.Title) ? userMeta!.Title! : file.Name,
            DateTaken: dateTaken,
            DateTakenSource: source,
            Location: userMeta?.LocationOverride);
    }

    // Curated, safe projection of embedded image metadata for the Owner
    // audience. Present only once extraction has completed. Fields named in
    // MetadataExposurePolicy.SensitiveEmbeddedNeedles (GPS coordinates,
    // serial numbers, software, lens make, raw document, date offset) are
    // intentionally NOT included — only a HasGps boolean signals GPS
    // presence. See MetadataExposurePolicy for the canonical rule.
    private static EmbeddedImageMetadata? BuildEmbedded(BlobMetadata? blobMeta)
    {
        if (blobMeta is null || blobMeta.ExtractionStatus != MetadataStatuses.Completed)
        {
            return null;
        }

        return new EmbeddedImageMetadata(
            DateTaken: blobMeta.DateTaken,
            DateTakenSource: blobMeta.DateTakenSource,
            Orientation: blobMeta.Orientation,
            CameraMake: blobMeta.CameraMake,
            CameraModel: blobMeta.CameraModel,
            LensModel: blobMeta.LensModel,
            Iso: blobMeta.IsoSpeed,
            Aperture: blobMeta.FNumber,
            ExposureTime: blobMeta.ExposureTime,
            FocalLength: blobMeta.FocalLength,
            ColorSpace: blobMeta.ColorSpace,
            HasGps: blobMeta.GpsLatitude is not null && blobMeta.GpsLongitude is not null);
    }

    // Curated projection of probed video metadata for the Owner audience.
    // Present only once probing completed for a video blob. All fields are
    // non-sensitive; the container creation time is deliberately excluded here
    // (it is surfaced through the shared DateTaken / effective capture date).
    private static VideoMetadata? BuildVideo(BlobMetadata? blobMeta)
    {
        if (blobMeta is null
            || blobMeta.MediaCategory != MediaCategories.Video
            || blobMeta.VideoExtractionStatus != MetadataStatuses.Completed)
        {
            return null;
        }

        return new VideoMetadata(
            DurationSeconds: blobMeta.DurationSeconds,
            VideoCodec: blobMeta.VideoCodec,
            AudioCodec: blobMeta.AudioCodec,
            FrameRate: blobMeta.FrameRate,
            VideoBitrate: blobMeta.VideoBitrate,
            HasAudio: blobMeta.HasAudio,
            AudioChannels: blobMeta.AudioChannels,
            AudioSampleRate: blobMeta.AudioSampleRate,
            Rotation: blobMeta.Rotation);
    }

    private static string? NormalizeText(string? value, int maxLength, string paramName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }
        var trimmed = value.Trim();
        if (trimmed.Length > maxLength)
        {
            throw new ArgumentException(
                $"{paramName} must be {maxLength} characters or fewer.", paramName);
        }
        return trimmed;
    }
}
