using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using NubArca.Api.Ai;
using NubArca.Api.Ai.Backends;
using NubArca.Api.Ai.Faces;
using NubArca.Api.Ai.Resolution;
using NubArca.Api.Data;
using NubArca.Api.Domain;
using NubArca.Api.Domain.Ai;
using NubArca.Api.Files;
using NubArca.Api.Security;
using NubArca.Api.Storage;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace NubArca.Api.Party;

public sealed class PartyFaceSearchService : IPartyFaceSearchService
{
    // Conservative in-memory selfie ceiling; overridable via config.
    private const long DefaultMaxUploadBytes = 15L * 1024 * 1024;
    private const int DefaultMaxImageDimension = 12000;
    private const int DefaultSessionTtlMinutes = 20;
    private const int DefaultMaxResults = 200;

    // Face-crop indicator thumbnail: square edge + padding around the detected
    // bbox (mirrors FacePreviewService geometry) + JPEG quality.
    private const int FaceCropEdge = 256;
    private const double FaceCropPaddingPerSide = 0.30;
    private const int FaceCropJpegQuality = 82;
    public const string FaceCropMimeType = "image/jpeg";

    private readonly AppDbContext _db;
    private readonly IAiBackendResolver _resolver;
    private readonly IAiProfileRegistry _profiles;
    private readonly IFaceSettingsProvider _faceSettings;
    private readonly IAiVectorSerializer _serializer;
    private readonly IBlobService _blobs;
    private readonly TimeProvider _clock;

    private readonly bool _enabled;
    private readonly long _maxBytes;
    private readonly int _maxDimension;
    private readonly int _ttlMinutes;
    private readonly int _maxResults;
    private readonly string? _configuredFaceProfileKey;

    public PartyFaceSearchService(
        AppDbContext db,
        IAiBackendResolver resolver,
        IAiProfileRegistry profiles,
        IFaceSettingsProvider faceSettings,
        IAiVectorSerializer serializer,
        IBlobService blobs,
        TimeProvider clock,
        IConfiguration config)
    {
        _db = db;
        _resolver = resolver;
        _profiles = profiles;
        _faceSettings = faceSettings;
        _serializer = serializer;
        _blobs = blobs;
        _clock = clock;

        _enabled = config.GetValue<bool?>("Party:FaceSearch:Enabled") ?? true;
        var bytes = config.GetValue<long?>("Party:FaceSearch:MaxUploadBytes");
        _maxBytes = bytes is > 0 ? bytes.Value : DefaultMaxUploadBytes;
        var dim = config.GetValue<int?>("Party:FaceSearch:MaxImageDimension");
        _maxDimension = dim is > 0 ? dim.Value : DefaultMaxImageDimension;
        var ttl = config.GetValue<int?>("Party:FaceSearch:SessionTtlMinutes");
        _ttlMinutes = ttl is > 0 ? ttl.Value : DefaultSessionTtlMinutes;
        var max = config.GetValue<int?>("Party:FaceSearch:MaxResults");
        _maxResults = max is > 0 ? max.Value : DefaultMaxResults;

        // Optional configured active face package (mirrors the backfill/CLI).
        var key = config["Ai:FaceProfileKey"];
        _configuredFaceProfileKey = string.IsNullOrWhiteSpace(key) ? null : key;
    }

    public async Task<PartyFaceSearchOutcome> SearchAsync(
        Guid ownerUserId,
        Guid albumId,
        Guid? partyAlbumLinkId,
        byte[] selfieBytes,
        string? declaredContentType,
        CancellationToken cancellationToken = default)
    {
        // Feature kill-switch (admin can disable party face search independently of
        // the AI substrate). Treated exactly like an unavailable capability.
        if (!_enabled)
        {
            return PartyFaceSearchOutcome.State(PartyFaceSearchStatuses.Unavailable);
        }

        // Opportunistic physical cleanup of this album's expired sessions (and
        // their face crops) so short-lived party rows never accumulate.
        await CleanupExpiredQuietlyAsync(ownerUserId, albumId, cancellationToken);

        // 1) Validate the selfie in memory (never stored). Cheap client-type gate
        //    first, then an authoritative header decode for real dimensions.
        if (selfieBytes.Length == 0 || selfieBytes.Length > _maxBytes
            || !SafeContentType.IsTrustedImage(declaredContentType))
        {
            return PartyFaceSearchOutcome.State(PartyFaceSearchStatuses.InvalidImage);
        }

        try
        {
            var info = Image.Identify(selfieBytes);
            if (info is null || info.Width <= 0 || info.Height <= 0
                || info.Width > _maxDimension || info.Height > _maxDimension)
            {
                return PartyFaceSearchOutcome.State(PartyFaceSearchStatuses.InvalidImage);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return PartyFaceSearchOutcome.State(PartyFaceSearchStatuses.InvalidImage);
        }

        // 2) Resolve the face package (detector + embedder). One AiProfile
        //    encapsulates both, so they share a ProfileId. Any unavailability (AI
        //    disabled, no default profile, model files missing) is a safe
        //    "unavailable" state — NEVER a content failure.
        var detector = await FaceProfileResolver.ResolveDetectorAsync(
            _resolver, null, _configuredFaceProfileKey, cancellationToken);
        var embedder = await FaceProfileResolver.ResolveEmbedderAsync(
            _resolver, null, _configuredFaceProfileKey, cancellationToken);
        if (!detector.IsAvailable || !embedder.IsAvailable
            || detector.Resolution.ProfileKey is null
            || !string.Equals(detector.Resolution.ProfileKey, embedder.Resolution.ProfileKey, StringComparison.Ordinal))
        {
            return PartyFaceSearchOutcome.State(PartyFaceSearchStatuses.Unavailable);
        }

        var profile = await _profiles.GetProfileByKeyAsync(detector.Resolution.ProfileKey!, cancellationToken);
        if (profile is null)
        {
            return PartyFaceSearchOutcome.State(PartyFaceSearchStatuses.Unavailable);
        }

        // 3) Detect faces in the selfie; pick the largest (most prominent) one.
        //    MVP behaviour: multiple faces → the largest bbox is used.
        var settings = await _faceSettings.GetAsync(cancellationToken);
        AiFaceDetectionResult detection;
        try
        {
            detection = await detector.Backend!.DetectFacesAsync(selfieBytes, profile, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            // A whole-image decode/inference failure is an environment/processing
            // state to the guest, not a content verdict on the album.
            return PartyFaceSearchOutcome.State(PartyFaceSearchStatuses.Unavailable);
        }

        var faces = detection.Faces;
        if (faces.Count == 0)
        {
            return await RecordEmptyAsync(
                ownerUserId, albumId, partyAlbumLinkId, PartyFaceSearchStatuses.NoFace, cancellationToken);
        }
        if (faces.Count > settings.MaxFacesPerImage)
        {
            faces = faces.OrderByDescending(f => f.Width * f.Height).Take(settings.MaxFacesPerImage).ToList();
        }

        var largest = faces.OrderByDescending(f => f.Width * f.Height).First();

        // 4) Embed the largest face. Prefer the aligned (real ONNX) path when the
        //    backend supports it; else the plain per-face path (mirrors the
        //    embedding backfill exactly so query + candidate spaces match).
        float[]? queryVector;
        try
        {
            queryVector = await EmbedQueryAsync(embedder.Backend!, profile, selfieBytes, largest, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return PartyFaceSearchOutcome.State(PartyFaceSearchStatuses.Unavailable);
        }
        if (queryVector is null || queryVector.Length == 0)
        {
            return PartyFaceSearchOutcome.State(PartyFaceSearchStatuses.Unavailable);
        }
        var query = _serializer.Normalize(queryVector);

        // 5) Load candidate face embeddings — ONLY visible image members of THIS
        //    owner's THIS album, joined to their blob-level face embeddings for the
        //    active profile. Owner + Private-Vault + moderation are all enforced in
        //    the candidate query, so no cross-owner / vaulted / hidden face leaks.
        var candidates = await VisibleImageMembersQuery(ownerUserId, albumId)
            .Join(_db.FaceDetections.AsNoTracking().Where(d => d.ProfileId == profile.Id),
                m => m.BlobObjectId,
                d => d.BlobObjectId,
                (m, d) => new { m.FileItemId, d.Id })
            .Join(_db.FaceEmbeddings.AsNoTracking()
                    .Where(e => e.ProfileId == profile.Id && e.EmbeddingStatus == AiArtifactStatuses.Completed),
                x => x.Id,
                e => e.FaceDetectionId,
                (x, e) => new { x.FileItemId, e.EmbeddingBytes })
            .ToListAsync(cancellationToken);

        var threshold = settings.ClampSearchThreshold(settings.SearchDefaultSimilarityThreshold);

        // Best cosine per FileItem (a file matches if ANY of its faces is similar
        // enough). Only the ordering survives — the score itself is never exposed.
        var bestByFile = new Dictionary<Guid, double>();
        foreach (var c in candidates)
        {
            if (c.EmbeddingBytes.Length == 0)
            {
                continue;
            }

            float[] candidate;
            try
            {
                candidate = _serializer.Deserialize(c.EmbeddingBytes);
            }
            catch
            {
                continue;
            }
            if (candidate.Length != query.Length)
            {
                continue;
            }

            var score = Cosine(query, candidate);
            if (score < threshold)
            {
                continue;
            }
            if (!bestByFile.TryGetValue(c.FileItemId, out var prev) || score > prev)
            {
                bestByFile[c.FileItemId] = score;
            }
        }

        var ranked = bestByFile
            .OrderByDescending(kv => kv.Value)
            .ThenBy(kv => kv.Key)
            .Take(_maxResults)
            .Select(kv => kv.Key)
            .ToList();

        // 6) Persist the short-lived session + ranked matches (file ids only). No
        //    selfie, no query vector, no score. When there ARE matches, also store
        //    the small detected-face crop (never the full selfie) so an activated
        //    TV filter can show the indicator thumbnail; a crop failure is
        //    tolerated (the search works, the indicator just has no image).
        var now = _clock.GetUtcNow().UtcDateTime;
        Guid? faceCropBlobId = ranked.Count > 0
            ? await TryStoreFaceCropAsync(selfieBytes, largest, cancellationToken)
            : null;
        var session = new PartyFaceSearchSession
        {
            Id = Guid.NewGuid(),
            OwnerUserId = ownerUserId,
            AlbumId = albumId,
            PartyAlbumLinkId = partyAlbumLinkId,
            Status = PartyFaceSearchStatuses.Ready,
            ResultCount = ranked.Count,
            CreatedAt = now,
            ExpiresAt = now.AddMinutes(_ttlMinutes),
            FaceCropBlobObjectId = faceCropBlobId,
        };
        _db.PartyFaceSearchSessions.Add(session);
        for (var i = 0; i < ranked.Count; i++)
        {
            _db.PartyFaceSearchResults.Add(new PartyFaceSearchResult
            {
                Id = Guid.NewGuid(),
                PartyFaceSearchSessionId = session.Id,
                FileItemId = ranked[i],
                Rank = i,
                CreatedAt = now,
            });
        }
        try
        {
            await _db.SaveChangesAsync(cancellationToken);
        }
        catch
        {
            if (faceCropBlobId is not null)
            {
                await TryReleaseQuietlyAsync(faceCropBlobId.Value);
            }
            throw;
        }

        return new PartyFaceSearchOutcome(
            PartyFaceSearchStatuses.Ready, session.Id, ranked.Count, ranked);
    }

    public async Task<PartyFaceSearchView?> GetAsync(
        Guid ownerUserId, Guid albumId, Guid searchId, CancellationToken cancellationToken = default)
    {
        var now = _clock.GetUtcNow().UtcDateTime;
        var session = await _db.PartyFaceSearchSessions
            .AsNoTracking()
            .FirstOrDefaultAsync(
                s => s.Id == searchId && s.OwnerUserId == ownerUserId && s.AlbumId == albumId
                    && s.Status == PartyFaceSearchStatuses.Ready && s.ExpiresAt > now,
                cancellationToken);
        if (session is null)
        {
            return null;
        }

        return new PartyFaceSearchView(session.Id, await LiveMatchesAsync(session.Id, ownerUserId, albumId, cancellationToken));
    }

    public async Task<PartyFaceSearchActivationResult> ActivateForTvAsync(
        Guid ownerUserId, Guid albumId, Guid searchId, CancellationToken cancellationToken = default)
    {
        var now = _clock.GetUtcNow().UtcDateTime;
        var session = await _db.PartyFaceSearchSessions
            .FirstOrDefaultAsync(
                s => s.Id == searchId && s.OwnerUserId == ownerUserId && s.AlbumId == albumId
                    && s.Status == PartyFaceSearchStatuses.Ready && s.ExpiresAt > now,
                cancellationToken);
        if (session is null)
        {
            return new PartyFaceSearchActivationResult(PartyFaceSearchActivationStatus.NotFound);
        }

        // An empty result must never be sent to the TV — re-derived live, so a
        // search whose matches were all hidden since cannot be activated either.
        var matches = await LiveMatchesAsync(session.Id, ownerUserId, albumId, cancellationToken);
        if (matches.Count == 0)
        {
            return new PartyFaceSearchActivationResult(PartyFaceSearchActivationStatus.NoMatches);
        }

        // Stale-activation guard (server-side ordering, never client timestamps):
        // a search OLDER than the currently active one cannot replace it. The
        // check uses server-assigned CreatedAt; re-activating the current search
        // itself stays allowed (idempotent refresh).
        var active = await _db.PartyFaceSearchSessions.AsNoTracking()
            .Where(s => s.OwnerUserId == ownerUserId && s.AlbumId == albumId
                && s.Status == PartyFaceSearchStatuses.Ready && s.ExpiresAt > now
                && s.TvActivationVersion != null && s.Id != session.Id)
            .OrderByDescending(s => s.TvActivationVersion)
            .Select(s => new { s.CreatedAt })
            .FirstOrDefaultAsync(cancellationToken);
        if (active is not null && active.CreatedAt > session.CreatedAt)
        {
            return new PartyFaceSearchActivationResult(PartyFaceSearchActivationStatus.StaleSearch);
        }

        // Server-assigned monotonic per-album activation version: the newest
        // accepted activation always wins the "active" lookup.
        var maxVersion = await _db.PartyFaceSearchSessions.AsNoTracking()
            .Where(s => s.OwnerUserId == ownerUserId && s.AlbumId == albumId
                && s.TvActivationVersion != null)
            .MaxAsync(s => s.TvActivationVersion, cancellationToken) ?? 0L;

        session.TvActivationVersion = maxVersion + 1;
        session.TvActivatedAt = now;
        // Keep an activated filter alive a full TTL from activation so it does
        // not vanish from the TV moments after the guest sent it.
        var extended = now.AddMinutes(_ttlMinutes);
        if (session.ExpiresAt < extended)
        {
            session.ExpiresAt = extended;
        }
        await _db.SaveChangesAsync(cancellationToken);

        return new PartyFaceSearchActivationResult(
            PartyFaceSearchActivationStatus.Activated, session.TvActivationVersion);
    }

    public async Task DeleteAsync(
        Guid ownerUserId, Guid albumId, Guid searchId, CancellationToken cancellationToken = default)
    {
        // Row-scoped by id + owner + album: deleting an older search can never
        // touch a newer one, and a repeated delete is a no-op. ExecuteDelete
        // makes a concurrent phone+TV cancellation safe: exactly ONE caller sees
        // the row deleted and releases the crop reference (rank rows cascade).
        var session = await _db.PartyFaceSearchSessions.AsNoTracking()
            .Where(s => s.Id == searchId && s.OwnerUserId == ownerUserId && s.AlbumId == albumId)
            .Select(s => new { s.FaceCropBlobObjectId })
            .FirstOrDefaultAsync(cancellationToken);
        if (session is null)
        {
            return;
        }

        var deleted = await _db.PartyFaceSearchSessions
            .Where(s => s.Id == searchId && s.OwnerUserId == ownerUserId && s.AlbumId == albumId)
            .ExecuteDeleteAsync(cancellationToken);
        if (deleted == 1 && session.FaceCropBlobObjectId is not null)
        {
            await TryReleaseQuietlyAsync(session.FaceCropBlobObjectId.Value);
        }
    }

    public async Task<PartyFaceSearchActiveView?> GetActiveAsync(
        Guid ownerUserId, Guid albumId, CancellationToken cancellationToken = default)
    {
        if (!await IsOwnerTvAlbumAsync(ownerUserId, albumId, cancellationToken))
        {
            return null;
        }

        // The TV poll doubles as the album's expiry sweeper, so the last search
        // of a party never pins its crop blob forever.
        await CleanupExpiredQuietlyAsync(ownerUserId, albumId, cancellationToken);

        var now = _clock.GetUtcNow().UtcDateTime;
        // Only EXPLICITLY activated searches ever reach the TV; the highest
        // server-assigned activation version is the album's active filter.
        var session = await _db.PartyFaceSearchSessions
            .AsNoTracking()
            .Where(s => s.OwnerUserId == ownerUserId && s.AlbumId == albumId
                && s.Status == PartyFaceSearchStatuses.Ready && s.ExpiresAt > now
                && s.TvActivationVersion != null)
            .OrderByDescending(s => s.TvActivationVersion)
            .FirstOrDefaultAsync(cancellationToken);
        if (session is null)
        {
            return null;
        }

        var matches = await LiveMatchesAsync(session.Id, ownerUserId, albumId, cancellationToken);
        // An active search with no currently-visible match is not worth showing on
        // the TV (e.g. every match was hidden since) — treat as no active search.
        return matches.Count == 0
            ? null
            : new PartyFaceSearchActiveView(
                session.Id,
                session.TvActivationVersion!.Value,
                session.TvActivatedAt ?? session.CreatedAt,
                session.FaceCropBlobObjectId is not null,
                matches);
    }

    public async Task<bool> ClearActiveAsync(
        Guid ownerUserId, Guid albumId, CancellationToken cancellationToken = default)
    {
        if (!await IsOwnerTvAlbumAsync(ownerUserId, albumId, cancellationToken))
        {
            return false;
        }

        // Deactivate only — the searches themselves stay usable on the guests'
        // phones until they expire or are deleted explicitly.
        await _db.PartyFaceSearchSessions
            .Where(s => s.OwnerUserId == ownerUserId && s.AlbumId == albumId
                && s.TvActivationVersion != null)
            .ExecuteUpdateAsync(u => u
                .SetProperty(s => s.TvActivationVersion, (long?)null)
                .SetProperty(s => s.TvActivatedAt, (DateTime?)null), cancellationToken);
        return true;
    }

    public async Task<bool> DeleteForTvAsync(
        Guid ownerUserId, Guid albumId, Guid searchId, CancellationToken cancellationToken = default)
    {
        if (!await IsOwnerTvAlbumAsync(ownerUserId, albumId, cancellationToken))
        {
            return false;
        }

        await DeleteAsync(ownerUserId, albumId, searchId, cancellationToken);
        return true;
    }

    public async Task<ThumbnailContent?> OpenFaceCropAsync(
        Guid ownerUserId, Guid albumId, Guid searchId, CancellationToken cancellationToken = default)
    {
        if (!await IsOwnerTvAlbumAsync(ownerUserId, albumId, cancellationToken))
        {
            return null;
        }

        var now = _clock.GetUtcNow().UtcDateTime;
        // Served only while the search is still an ACTIVATED, live TV filter —
        // never for local-only or already-cleared searches.
        var session = await _db.PartyFaceSearchSessions.AsNoTracking()
            .FirstOrDefaultAsync(
                s => s.Id == searchId && s.OwnerUserId == ownerUserId && s.AlbumId == albumId
                    && s.Status == PartyFaceSearchStatuses.Ready && s.ExpiresAt > now
                    && s.TvActivationVersion != null && s.FaceCropBlobObjectId != null,
                cancellationToken);
        if (session is null)
        {
            return null;
        }

        var blob = await _db.BlobObjects.AsNoTracking()
            .FirstOrDefaultAsync(b => b.Id == session.FaceCropBlobObjectId!.Value, cancellationToken);
        if (blob is null)
        {
            return null;
        }

        // The crop is NOT regenerable (the selfie was discarded) — missing bytes
        // are a plain 404, never a re-render.
        var stream = await _blobs.OpenDerivedContentAsync(session.FaceCropBlobObjectId!.Value, cancellationToken);
        return stream is null
            ? null
            : new ThumbnailContent(stream, FaceCropMimeType, FaceCropEdge, FaceCropEdge, blob.SizeBytes);
    }

    // Intersect a stored search's ranked results with the album's CURRENTLY-visible
    // image members, preserving rank order. Re-derives visibility on every read, so
    // items hidden/removed/pending since the search drop out automatically.
    private async Task<IReadOnlyList<Guid>> LiveMatchesAsync(
        Guid sessionId, Guid ownerUserId, Guid albumId, CancellationToken cancellationToken)
    {
        var ranked = await _db.PartyFaceSearchResults
            .AsNoTracking()
            .Where(r => r.PartyFaceSearchSessionId == sessionId)
            .OrderBy(r => r.Rank)
            .Select(r => r.FileItemId)
            .ToListAsync(cancellationToken);
        if (ranked.Count == 0)
        {
            return Array.Empty<Guid>();
        }

        var visible = await VisibleImageMembersQuery(ownerUserId, albumId)
            .Where(m => ranked.Contains(m.FileItemId))
            .Select(m => m.FileItemId)
            .ToListAsync(cancellationToken);
        var visibleSet = visible.ToHashSet();

        return ranked.Where(visibleSet.Contains).ToList();
    }

    // Currently-visible IMAGE members of the owner's album: owner-owned, active,
    // non-vault (FileItems carries the Private-Vault global filter), and NOT a
    // pending/hidden/rejected guest upload. Mirrors PartyMediaService.
    private IQueryable<VisibleMember> VisibleImageMembersQuery(Guid ownerUserId, Guid albumId) =>
        _db.AlbumItems
            .AsNoTracking()
            .Where(ai => ai.AlbumId == albumId)
            .Join(_db.FileItems.AsNoTracking(),
                ai => ai.FileItemId,
                f => f.Id,
                (ai, f) => new { f.Id, f.BlobObjectId, f.OwnerUserId, f.DeletedAt, f.MediaLibraryState })
            // Slice 3: excluded files are not searchable on the Party face-search
            // surface (the AlbumItem persists but the file is out of the library).
            .Where(x => x.OwnerUserId == ownerUserId
                && x.DeletedAt == null
                && x.MediaLibraryState == Domain.MediaLibraryState.Active)
            .Where(x => !_db.PartyUploadItems.Any(pu =>
                pu.FileItemId == x.Id && pu.Status != PartyUploadStatuses.Approved))
            .Join(_db.BlobMetadata.AsNoTracking(),
                x => x.BlobObjectId,
                m => m.BlobObjectId,
                (x, m) => new { x.Id, x.BlobObjectId, m.MediaCategory })
            .Where(x => x.MediaCategory == MediaCategories.Image)
            .Select(x => new VisibleMember { FileItemId = x.Id, BlobObjectId = x.BlobObjectId });

    private async Task<bool> IsOwnerTvAlbumAsync(Guid ownerUserId, Guid albumId, CancellationToken cancellationToken) =>
        await _db.Albums.AsNoTracking()
            .AnyAsync(a => a.Id == albumId && a.OwnerUserId == ownerUserId && a.ShowOnTv, cancellationToken);

    private async Task<PartyFaceSearchOutcome> RecordEmptyAsync(
        Guid ownerUserId, Guid albumId, Guid? partyAlbumLinkId, string status, CancellationToken cancellationToken)
    {
        // A no-face selfie is recorded (safe status, zero results) so the public
        // GET can re-fetch it, but it never becomes a TV active slideshow (only
        // "ready" sessions with visible matches do).
        var now = _clock.GetUtcNow().UtcDateTime;
        var session = new PartyFaceSearchSession
        {
            Id = Guid.NewGuid(),
            OwnerUserId = ownerUserId,
            AlbumId = albumId,
            PartyAlbumLinkId = partyAlbumLinkId,
            Status = status,
            ResultCount = 0,
            CreatedAt = now,
            ExpiresAt = now.AddMinutes(_ttlMinutes),
        };
        _db.PartyFaceSearchSessions.Add(session);
        await _db.SaveChangesAsync(cancellationToken);
        return new PartyFaceSearchOutcome(status, null, 0, Array.Empty<Guid>());
    }

    // Render + store the small square crop of the DETECTED query face (same
    // EXIF-orient + crop geometry as FacePreviewService, so the box detection
    // produced is the box that gets cropped). Any failure returns null — the
    // search proceeds without an indicator thumbnail.
    private async Task<Guid?> TryStoreFaceCropAsync(
        byte[] selfieBytes, DetectedFace face, CancellationToken cancellationToken)
    {
        try
        {
            using var image = Image.Load<Rgb24>(selfieBytes);
            image.Mutate(c => c.AutoOrient());

            var crop = FacePreviewService.ComputeCropRect(
                image.Width, image.Height,
                face.X, face.Y, face.Width, face.Height,
                FaceCropPaddingPerSide);
            var edge = Math.Min(FaceCropEdge, crop.Width); // no upscaling

            image.Mutate(c => c
                .Crop(crop)
                .Resize(new ResizeOptions
                {
                    Size = new Size(edge, edge),
                    Mode = ResizeMode.Stretch, // square→square, no distortion
                    Sampler = KnownResamplers.Lanczos3,
                }));

            using var ms = new MemoryStream();
            image.SaveAsJpeg(ms, new JpegEncoder { Quality = FaceCropJpegQuality });
            ms.Position = 0;
            var blob = await _blobs.StoreDerivedAsync(ms, cancellationToken);
            return blob.Id;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return null;
        }
    }

    // Physically remove this album's EXPIRED sessions (rank rows cascade) and
    // release their face-crop blob references. Best-effort — a failure here must
    // never break the guest's search.
    private async Task CleanupExpiredQuietlyAsync(
        Guid ownerUserId, Guid albumId, CancellationToken cancellationToken)
    {
        try
        {
            var now = _clock.GetUtcNow().UtcDateTime;
            var expired = await _db.PartyFaceSearchSessions.AsNoTracking()
                .Where(s => s.OwnerUserId == ownerUserId && s.AlbumId == albumId && s.ExpiresAt <= now)
                .Select(s => new { s.Id, s.FaceCropBlobObjectId })
                .ToListAsync(cancellationToken);
            foreach (var row in expired)
            {
                // Per-row ExecuteDelete: a concurrent cleanup/delete makes exactly
                // one caller see deleted == 1 and release the crop reference.
                var deleted = await _db.PartyFaceSearchSessions
                    .Where(s => s.Id == row.Id && s.ExpiresAt <= now)
                    .ExecuteDeleteAsync(cancellationToken);
                if (deleted == 1 && row.FaceCropBlobObjectId is not null)
                {
                    await TryReleaseQuietlyAsync(row.FaceCropBlobObjectId.Value);
                }
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            // Rows stay for the next opportunity; the reference audit keeps the
            // accounting honest either way.
        }
    }

    private async Task TryReleaseQuietlyAsync(Guid blobId)
    {
        try
        {
            await _blobs.ReleaseAsync(blobId, CancellationToken.None);
        }
        catch
        {
            // Best-effort; a stray derived blob is reclaimed via repair + janitor.
        }
    }

    private static async Task<float[]?> EmbedQueryAsync(
        IFaceEmbedder embedder, AiProfile profile, byte[] imageBytes, DetectedFace face,
        CancellationToken cancellationToken)
    {
        if (embedder is IAlignedFaceEmbedder aligned)
        {
            var landmarks = face.Landmarks ?? Array.Empty<FaceLandmark>();
            var attempts = await aligned.EmbedAlignedFacesAsync(
                imageBytes,
                new[] { (IReadOnlyList<FaceLandmark>)landmarks },
                profile,
                cancellationToken);
            var first = attempts.Count > 0 ? attempts[0] : null;
            return first is { Outcome: FaceEmbedOutcome.Ok, Embedding: { } e } ? e.Vector : null;
        }

        var result = await embedder.EmbedFaceAsync(imageBytes, profile, cancellationToken);
        return result.Vector;
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
        if (na <= double.Epsilon || nb <= double.Epsilon)
        {
            return 0;
        }
        return dot / (Math.Sqrt(na) * Math.Sqrt(nb));
    }

    private sealed class VisibleMember
    {
        public Guid FileItemId { get; set; }
        public Guid BlobObjectId { get; set; }
    }
}
