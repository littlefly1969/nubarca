using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using NubArca.Api.Data;
using NubArca.Api.Domain;
using NubArca.Api.Domain.Ai;

namespace NubArca.Api.Ai.Faces.Video;

// VFACE-02: owner-scoped identity SUGGESTIONS for a canonical video face track.
//
// A suggestion is never an identity. This service reads evidence and returns a
// ranked, bounded list of the OWNER'S OWN people; nothing it produces is
// persisted, and an explicit user decision always wins. That is the whole
// contract — "model output = suggestion, user action = confirmed assignment".
//
// CANDIDATE POOL. Only the caller's own confirmed evidence:
//   * faces the owner confirmed on a person (PersonFaceAssignment → FaceEmbedding)
//   * video tracks the owner already assigned to a person (VFACE-02 decisions)
// Another owner's confirmed identity is never consulted, even when the two
// libraries share the very same deduplicated blob. Individually ignored faces
// and ignored tracks contribute nothing.
//
// PROFILE COMPATIBILITY. Face embeddings only compare inside ONE model space, so
// every side is filtered to the track's own embedding profile. A track produced
// under a retired profile simply has no candidates rather than silently being
// scored against another space.
//
// NO SECOND INDEX. The pool is one owner's confirmed faces, which is small and
// bounded further by MaximumEvidencePerPerson, so the comparison is an exact
// in-process cosine over the canonical byte[] vectors — the same representation
// and the same measure the People path already uses. No pgvector table is added,
// and pgvector's absence never disables suggestions.
public sealed class VideoFaceTrackIdentitySuggestionService
{
    // Confirmed vectors considered per person. Caps the work for a heavily
    // populated person while keeping the score stable (the best match dominates).
    public const int MaximumEvidencePerPerson = 32;

    public const int DefaultMaximumCandidates = 5;
    public const int MaximumCandidates = 20;

    private readonly AppDbContext _db;
    private readonly IAiVectorSerializer _serializer;
    private readonly IFaceSettingsProvider _settings;
    private readonly IOptions<AiOptions> _options;
    private readonly IAiProfileRegistry _registry;

    public VideoFaceTrackIdentitySuggestionService(
        AppDbContext db,
        IAiVectorSerializer serializer,
        IFaceSettingsProvider settings,
        IOptions<AiOptions> options,
        IAiProfileRegistry registry)
    {
        _db = db;
        _serializer = serializer;
        _settings = settings;
        _options = options;
        _registry = registry;
    }

    // Ranked candidates for ONE track the caller can actually see. Returns null
    // only for a track the owner may not inspect (generic 404 upstream); an empty
    // list is a legitimate "nothing above the threshold".
    public async Task<VideoFaceTrackSuggestionsDto?> SuggestAsync(
        Guid ownerUserId, Guid trackId, int? maximumCandidates,
        CancellationToken cancellationToken = default)
    {
        var take = Math.Clamp(maximumCandidates ?? DefaultMaximumCandidates, 1, MaximumCandidates);

        // OWNER SCOPE FIRST: nothing about the track is read before the caller's
        // visibility is established.
        var track = await (
            from t in VideoFaceTrackVisibility.VisibleTracks(_db, ownerUserId)
            where t.Id == trackId
            join analysis in _db.VideoFaceAnalysisStatuses.AsNoTracking()
                on t.VideoFaceAnalysisStatusId equals analysis.Id
            select new
            {
                t.Id,
                t.EmbeddingBytes,
                t.EmbeddingDimension,
                analysis.EmbeddingProfileId,
            })
            .FirstOrDefaultAsync(cancellationToken);
        if (track is null)
        {
            return null;
        }

        var settings = await _settings.GetAsync(cancellationToken);
        var threshold = settings.CandidateSimilarityThreshold;

        float[] query;
        try
        {
            query = _serializer.Deserialize(track.EmbeddingBytes, track.EmbeddingDimension);
        }
        catch
        {
            return new VideoFaceTrackSuggestionsDto(
                threshold, Array.Empty<VideoFaceTrackSuggestionDto>(), "track-not-indexed");
        }

        if (!IsUsable(query))
        {
            return new VideoFaceTrackSuggestionsDto(
                threshold, Array.Empty<VideoFaceTrackSuggestionDto>(), "track-not-indexed");
        }

        var activeProfile = await ResolveActiveProfileAsync(cancellationToken);
        if (activeProfile is null || activeProfile.Id != track.EmbeddingProfileId)
        {
            // The track lives in a different recognition space than the one this
            // deployment currently uses. Comparing across spaces is meaningless,
            // so nothing is suggested.
            return new VideoFaceTrackSuggestionsDto(
                threshold, Array.Empty<VideoFaceTrackSuggestionDto>(), "profile-mismatch");
        }

        var evidence = await CollectEvidenceAsync(
            ownerUserId, track.EmbeddingProfileId, track.Id, cancellationToken);
        if (evidence.Count == 0)
        {
            return new VideoFaceTrackSuggestionsDto(
                threshold, Array.Empty<VideoFaceTrackSuggestionDto>(), null);
        }

        var scored = new List<VideoFaceTrackSuggestionDto>();
        foreach (var (personId, name, vectors) in evidence)
        {
            double best = double.NegativeInfinity;
            var supporting = 0;
            foreach (var vector in vectors)
            {
                var similarity = CosineSimilarity(query, vector);
                if (similarity >= threshold)
                {
                    supporting++;
                }

                if (similarity > best)
                {
                    best = similarity;
                }
            }

            if (supporting == 0 || best < threshold)
            {
                continue;
            }

            scored.Add(new VideoFaceTrackSuggestionDto(
                personId, name, Math.Round(best, 4), supporting));
        }

        // Deterministic ordering: strongest first, then a stable id tiebreak so
        // two equally-scored people never swap between calls.
        var items = scored
            .OrderByDescending(c => c.Similarity)
            .ThenByDescending(c => c.SupportingEvidenceCount)
            .ThenBy(c => c.PersonId)
            .Take(take)
            .ToList();

        return new VideoFaceTrackSuggestionsDto(threshold, items, null);
    }

    // Every person of THIS owner with confirmed evidence in the given model
    // space, together with that evidence's vectors. Archived people are excluded
    // (they are hidden from the People surface), as is the track being reviewed.
    private async Task<List<(Guid PersonId, string? Name, List<float[]> Vectors)>> CollectEvidenceAsync(
        Guid ownerUserId, Guid profileId, Guid trackId, CancellationToken cancellationToken)
    {
        // ---- confirmed static faces ----------------------------------------
        // Individually ignored faces never contribute: an owner who dismissed a
        // face must not see it come back as the reason for a suggestion.
        var faceRows = await (
            from a in _db.PersonFaceAssignments.AsNoTracking()
            where a.OwnerUserId == ownerUserId
            join p in _db.People.AsNoTracking() on a.PersonId equals p.Id
            where p.OwnerUserId == ownerUserId && !p.IsArchived
            join e in _db.FaceEmbeddings.AsNoTracking() on a.FaceDetectionId equals e.FaceDetectionId
            where e.ProfileId == profileId && e.EmbeddingStatus == AiArtifactStatuses.Completed
            where !_db.IgnoredFaces.Any(
                i => i.OwnerUserId == ownerUserId && i.FaceDetectionId == a.FaceDetectionId)
            orderby a.PersonId, a.FaceDetectionId
            select new { a.PersonId, p.DisplayName, e.EmbeddingBytes, e.Dimension })
            .ToListAsync(cancellationToken);

        // ---- confirmed video tracks ----------------------------------------
        // A track the owner already assigned is confirmed evidence exactly like a
        // confirmed face, and it lives in the same space. `ignored` decisions
        // contribute nothing by construction (they carry no PersonId).
        var trackRows = await (
            from d in _db.VideoFaceTrackPersonDecisions.AsNoTracking()
            where d.OwnerUserId == ownerUserId
                && d.Decision == VideoFaceTrackDecisions.Assigned
                && d.PersonId != null
                && d.VideoFaceTrackId != trackId
            join p in _db.People.AsNoTracking() on d.PersonId equals p.Id
            where p.OwnerUserId == ownerUserId && !p.IsArchived
            join t in _db.VideoFaceTracks.AsNoTracking() on d.VideoFaceTrackId equals t.Id
            join s in _db.VideoFaceAnalysisStatuses.AsNoTracking()
                on t.VideoFaceAnalysisStatusId equals s.Id
            where s.EmbeddingProfileId == profileId
            orderby d.PersonId, d.VideoFaceTrackId
            select new
            {
                PersonId = d.PersonId!.Value,
                p.DisplayName,
                t.EmbeddingBytes,
                Dimension = t.EmbeddingDimension,
            })
            .ToListAsync(cancellationToken);

        var byPerson = new Dictionary<Guid, (string? Name, List<float[]> Vectors)>();

        void Add(Guid personId, string? name, byte[] bytes, int dimension)
        {
            if (!byPerson.TryGetValue(personId, out var entry))
            {
                entry = (name, new List<float[]>());
                byPerson[personId] = entry;
            }

            if (entry.Vectors.Count >= MaximumEvidencePerPerson)
            {
                return;
            }

            try
            {
                var vector = _serializer.Deserialize(bytes, dimension);
                if (IsUsable(vector))
                {
                    entry.Vectors.Add(vector);
                }
            }
            catch
            {
                // A corrupt canonical row is skipped, never fatal.
            }
        }

        foreach (var row in faceRows)
        {
            Add(row.PersonId, row.DisplayName, row.EmbeddingBytes, row.Dimension);
        }

        foreach (var row in trackRows)
        {
            Add(row.PersonId, row.DisplayName, row.EmbeddingBytes, row.Dimension);
        }

        return byPerson
            .Where(kv => kv.Value.Vectors.Count > 0)
            .OrderBy(kv => kv.Key)
            .Select(kv => (kv.Key, kv.Value.Name, kv.Value.Vectors))
            .ToList();
    }

    private async Task<AiProfile?> ResolveActiveProfileAsync(CancellationToken cancellationToken)
    {
        var key = _options.Value.FaceProfileKey;
        return !string.IsNullOrWhiteSpace(key)
            ? await _registry.GetProfileByKeyAsync(key!, cancellationToken)
            : await _registry.GetDefaultProfileAsync(AiCapabilities.FaceEmbedding, cancellationToken);
    }

    private static bool IsUsable(float[] vector)
    {
        if (vector.Length == 0)
        {
            return false;
        }

        var norm = 0d;
        foreach (var value in vector)
        {
            if (!float.IsFinite(value))
            {
                return false;
            }

            norm += (double)value * value;
        }

        return norm > 0;
    }

    // Cosine similarity of two finite, same-dimension vectors. Mismatched or
    // degenerate input scores 0 — i.e. "no evidence", which the threshold
    // rejects.
    internal static double CosineSimilarity(IReadOnlyList<float> a, IReadOnlyList<float> b)
    {
        if (a.Count == 0 || a.Count != b.Count)
        {
            return 0d;
        }

        double dot = 0, normA = 0, normB = 0;
        for (var i = 0; i < a.Count; i++)
        {
            double x = a[i];
            double y = b[i];
            dot += x * y;
            normA += x * x;
            normB += y * y;
        }

        return normA <= 0 || normB <= 0 ? 0d : dot / (Math.Sqrt(normA) * Math.Sqrt(normB));
    }
}

// ---- sanitized owner-private DTOs ---------------------------------------

// One candidate person for a track. `Similarity` is deliberately named for what
// it IS — a rounded cosine similarity in [-1, 1] — and never dressed up as a
// probability. No vector, no profile id, no track internals.
public sealed record VideoFaceTrackSuggestionDto(
    Guid PersonId, string? Name, double Similarity, int SupportingEvidenceCount);

// `UnavailableReason` is a sanitized token ("track-not-indexed" |
// "profile-mismatch") explaining an empty list that is NOT simply "nothing
// matched". Null when the query ran normally.
public sealed record VideoFaceTrackSuggestionsDto(
    double Threshold,
    IReadOnlyList<VideoFaceTrackSuggestionDto> Items,
    string? UnavailableReason);
