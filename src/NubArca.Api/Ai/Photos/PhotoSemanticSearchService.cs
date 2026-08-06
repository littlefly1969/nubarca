using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using NubArca.Api.Ai.Backends;
using NubArca.Api.Ai.Resolution;
using NubArca.Api.Data;
using NubArca.Api.Domain;
using NubArca.Api.MediaLibrary;

namespace NubArca.Api.Ai.Photos;

// Owner-private text-to-image retrieval. Text is embedded on demand with the
// text tower of the active PHOTO profile; it is never stored and never mixed
// with an image query. Results contain owner-visible FileItem ids only.
public sealed class PhotoSemanticSearchService
{
    public const int DefaultPageSize = 50;
    public const int MaxPageSize = 100;
    public const int MaxResults = 500;
    public const int MaxQueryLength = 256;
    private const int MaxExactCandidates = 50_000;

    private readonly AppDbContext _db;
    private readonly PhotoEmbeddingProfileService _profiles;
    private readonly IAiBackendResolver _backends;
    private readonly PhotoVectorIndexService _vectors;
    private readonly IAiVectorSerializer _serializer;
    private readonly IMediaLibraryService _mediaLibrary;

    public PhotoSemanticSearchService(
        AppDbContext db,
        PhotoEmbeddingProfileService profiles,
        IAiBackendResolver backends,
        PhotoVectorIndexService vectors,
        IAiVectorSerializer serializer,
        IMediaLibraryService mediaLibrary)
    {
        _db = db;
        _profiles = profiles;
        _backends = backends;
        _vectors = vectors;
        _serializer = serializer;
        _mediaLibrary = mediaLibrary;
    }

    public async Task<SemanticPhotosPage> SearchAsync(
        Guid ownerUserId,
        string query,
        int limit,
        string? cursor,
        CancellationToken cancellationToken = default)
    {
        var normalizedQuery = query.Trim();
        if (normalizedQuery.Length == 0 || normalizedQuery.Length > MaxQueryLength)
        {
            throw new ArgumentOutOfRangeException(nameof(query));
        }
        var cursorOffset = DecodeCursor(cursor, normalizedQuery);
        if (!string.IsNullOrWhiteSpace(cursor) && cursorOffset < 0)
        {
            throw new SemanticSearchCursorException();
        }

        var profileResolution = await _profiles.ResolveActiveProfileAsync(null, cancellationToken);
        if (!profileResolution.Usable || profileResolution.Profile is null
            || profileResolution.Profile.Dimension != PhotoVectorIndexService.SupportedDimension)
        {
            return SemanticPhotosPage.Unavailable(
                profileResolution.UnavailableReason ?? AiUnavailableReasons.ProfileDimensionInvalid);
        }

        var profile = profileResolution.Profile;
        var backendResolution = await _backends.ResolveForProfileKeyAsync<ITextEmbedder>(
            profile.Key, cancellationToken);
        if (!backendResolution.Resolution.IsAvailable || backendResolution.Backend is null)
        {
            return SemanticPhotosPage.Unavailable(backendResolution.Resolution.UnavailableReason);
        }

        var embedding = await backendResolution.Backend.EmbedTextAsync(
            normalizedQuery, profile, cancellationToken);
        if (embedding.Dimension != profile.Dimension
            || embedding.Vector.Length != PhotoVectorIndexService.SupportedDimension)
        {
            return SemanticPhotosPage.Unavailable(AiUnavailableReasons.ProfileDimensionInvalid);
        }

        IReadOnlyList<SemanticPhotoHit> ordered;
        if (await _vectors.CountIndexedAsync(profile.Id, cancellationToken) > 0)
        {
            var neighbours = await _vectors.SearchByVectorAsync(
                profile.Id, embedding.Vector, ownerUserId, MaxResults, cancellationToken);
            if (neighbours is not null)
            {
                ordered = neighbours
                    .Select(n => new SemanticPhotoHit(n.FileItemId, n.Score))
                    .OrderByDescending(x => x.Score)
                    .ThenBy(x => x.FileItemId)
                    .ToList();
                return Paginate(ordered, normalizedQuery, cursorOffset, limit);
            }
        }

        // Provider-independent exact fallback over canonical vectors. FileItem's
        // global vault filter plus media-library eligibility preserve the same
        // membership boundary as the normal gallery.
        var files = _mediaLibrary.ApplyMediaLibraryVisibility(
            _db.FileItems.AsNoTracking().Where(f =>
                f.OwnerUserId == ownerUserId
                && f.DeletedAt == null
                // Slice 3: excluded files never compete in semantic retrieval.
                && f.MediaLibraryState == MediaLibraryState.Active
                && (_db.BlobMetadata.Any(m => m.BlobObjectId == f.BlobObjectId
                        && m.DetectedContentType != null
                        && m.DetectedContentType.StartsWith("image/"))
                    || (!_db.BlobMetadata.Any(m => m.BlobObjectId == f.BlobObjectId)
                        && f.MimeType.StartsWith("image/")))),
            MediaKind.Photo);
        files = SemanticPhotoCandidatePolicy.Apply(files, _db);

        var candidates = await (
            from f in files
            join e in _db.BlobEmbeddings.AsNoTracking() on f.BlobObjectId equals e.BlobObjectId
            where e.ProfileId == profile.Id
            orderby f.Id
            select new { f.Id, e.EmbeddingBytes })
            .Take(MaxExactCandidates)
            .ToListAsync(cancellationToken);

        var scored = new List<SemanticPhotoHit>(candidates.Count);
        foreach (var candidate in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var vector = _serializer.Deserialize(candidate.EmbeddingBytes);
                if (vector.Length == embedding.Vector.Length)
                {
                    scored.Add(new SemanticPhotoHit(
                        candidate.Id, Math.Round(Cosine(embedding.Vector, vector), 6)));
                }
            }
            catch
            {
                // A corrupt canonical row is skipped without leaking internals.
            }
        }

        ordered = scored
            .OrderByDescending(x => x.Score)
            .ThenBy(x => x.FileItemId)
            .Take(MaxResults)
            .ToList();
        return Paginate(ordered, normalizedQuery, cursorOffset, limit);
    }

    private static SemanticPhotosPage Paginate(
        IReadOnlyList<SemanticPhotoHit> ordered, string query, int cursorOffset, int limit)
    {
        var pageSize = Math.Clamp(limit, 1, MaxPageSize);
        var start = cursorOffset > ordered.Count ? ordered.Count : cursorOffset;
        var items = ordered.Skip(start).Take(pageSize).ToList();
        var nextOffset = start + items.Count;
        var hasMore = nextOffset < ordered.Count;
        return new SemanticPhotosPage(
            ProfileAvailable: true,
            TextModelAvailable: true,
            items,
            NextCursor: hasMore ? EncodeCursor(nextOffset, query) : null,
            HasMore: hasMore,
            UnavailableReason: null);
    }

    private static string EncodeCursor(int offset, string query)
    {
        var fingerprint = QueryFingerprint(query);
        var raw = $"{offset.ToString(CultureInfo.InvariantCulture)}|{fingerprint}";
        return Convert.ToBase64String(Encoding.UTF8.GetBytes(raw))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    private static int DecodeCursor(string? cursor, string query)
    {
        if (string.IsNullOrWhiteSpace(cursor)) return 0;
        try
        {
            var b64 = cursor.Replace('-', '+').Replace('_', '/');
            b64 += new string('=', (4 - b64.Length % 4) % 4);
            var raw = Encoding.UTF8.GetString(Convert.FromBase64String(b64));
            var parts = raw.Split('|', 2);
            return parts.Length == 2
                && parts[1] == QueryFingerprint(query)
                && int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var offset)
                ? offset
                : -1;
        }
        catch
        {
            return -1;
        }
    }

    private static string QueryFingerprint(string query)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(query.Trim().ToLowerInvariant()));
        return Convert.ToHexString(bytes, 0, 8).ToLowerInvariant();
    }

    private static double Cosine(float[] a, float[] b)
    {
        double dot = 0, na = 0, nb = 0;
        for (var i = 0; i < a.Length; i++)
        {
            dot += (double)a[i] * b[i];
            na += (double)a[i] * a[i];
            nb += (double)b[i] * b[i];
        }
        return na <= double.Epsilon || nb <= double.Epsilon
            ? 0
            : dot / (Math.Sqrt(na) * Math.Sqrt(nb));
    }
}

public sealed class SemanticSearchCursorException : Exception
{
}

public sealed record SemanticPhotoHit(Guid FileItemId, double Score);

public sealed record SemanticPhotosPage(
    bool ProfileAvailable,
    bool TextModelAvailable,
    IReadOnlyList<SemanticPhotoHit> Items,
    string? NextCursor,
    bool HasMore,
    string? UnavailableReason)
{
    public static SemanticPhotosPage Unavailable(string? reason) => new(
        false, false, Array.Empty<SemanticPhotoHit>(), null, false, reason);
}
