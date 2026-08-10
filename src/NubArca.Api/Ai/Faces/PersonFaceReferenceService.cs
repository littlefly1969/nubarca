using Microsoft.EntityFrameworkCore;
using NubArca.Api.Data;
using NubArca.Api.Domain;
using NubArca.Api.Domain.Ai;

namespace NubArca.Api.Ai.Faces;

// Owner-private lifecycle of a Person's persistent reference faces (max 6 per
// person/profile). This is the ONLY place that reads a person's historical
// embeddings, and it does so on exactly two occasions:
//
//   * BOOTSTRAP  — the first similar-face request for a person/profile that has
//                  no reference set yet;
//   * REPLENISH  — a request that found one of its references no longer eligible
//                  (unassigned, moved to another person, ignored, vaulted,
//                  deleted) and has a free slot to refill.
//
// An ordinary request with a healthy set costs ZERO historical scans, zero photo
// reads and zero inference: the persisted references are read back and reused.
// New assignments are folded in INCREMENTALLY at write time (MaintainAfterAssign),
// so a growing person never re-derives its set from every historical face.
//
// Nothing here exposes a vector, a blob id, a SHA, a storage key or a path; the
// vectors it returns stay inside the search path.
public sealed class PersonFaceReferenceService
{
    public const int MaxPersonReferenceFaces = PersonReferenceSelector.MaxPersonReferenceFaces;

    // Upper bound on the confirmed faces one bootstrap/replenish considers.
    // Ordered by quality descending, so the faces a reference would ever be drawn
    // from are the ones retained; keeps peak memory flat for a person with
    // thousands of confirmed faces.
    private const int CandidateScanCap = 1000;

    private readonly AppDbContext _db;
    private readonly IAiVectorSerializer _serializer;
    private readonly IFaceSettingsProvider _settings;
    private readonly TimeProvider _clock;

    public PersonFaceReferenceService(
        AppDbContext db, IAiVectorSerializer serializer, IFaceSettingsProvider settings, TimeProvider clock)
    {
        _db = db;
        _serializer = serializer;
        _settings = settings;
        _clock = clock;
    }

    // One reference face and the vector a search queries with. Owner-private and
    // never surfaced through a DTO.
    public readonly record struct PersonReferenceVector(Guid FaceDetectionId, float[] Vector);

    // The person's current reference vectors for this profile, bootstrapping or
    // replenishing them if needed. Empty = the person has no usable confirmed
    // embedding in this profile yet.
    public async Task<IReadOnlyList<PersonReferenceVector>> EnsureAsync(
        Guid ownerUserId, Guid personId, Guid profileId, double coverageThreshold,
        CancellationToken cancellationToken = default)
    {
        var rows = await _db.PersonFaceReferences
            .Where(r => r.OwnerUserId == ownerUserId && r.PersonId == personId && r.ProfileId == profileId)
            .OrderBy(r => r.Ordinal)
            .ToListAsync(cancellationToken);

        var kept = new List<PersonFaceReference>(rows.Count);
        var keptCandidates = new List<PersonReferenceSelector.ReferenceCandidate>(rows.Count);
        var invalidated = false;

        if (rows.Count > 0)
        {
            var usable = await LoadEligibleAsync(
                ownerUserId, personId, profileId, rows.Select(r => r.FaceDetectionId).ToList(), cancellationToken);
            foreach (var row in rows)
            {
                if (usable.TryGetValue(row.FaceDetectionId, out var candidate))
                {
                    kept.Add(row);
                    keptCandidates.Add(candidate);
                }
                else
                {
                    // No longer confirmed for this person / no longer surfaceable /
                    // embedding gone: the reference cannot keep steering the search.
                    _db.PersonFaceReferences.Remove(row);
                    invalidated = true;
                }
            }

            if (invalidated)
            {
                // Freed slots are committed BEFORE new ones are inserted, so a
                // refilled ordinal can never collide with the row it replaces.
                if (!await TrySaveAsync(cancellationToken))
                {
                    return await ReadPersistedAsync(ownerUserId, personId, profileId, cancellationToken);
                }
            }
        }

        var needsScan = rows.Count == 0 || (invalidated && kept.Count < MaxPersonReferenceFaces);
        if (!needsScan)
        {
            return keptCandidates.Select(c => new PersonReferenceVector(c.FaceDetectionId, c.Vector)).ToList();
        }

        var candidates = await LoadCandidatesAsync(ownerUserId, personId, profileId, cancellationToken);
        var selected = PersonReferenceSelector.Select(candidates, coverageThreshold, keptCandidates);
        if (!await PersistAdditionsAsync(ownerUserId, personId, profileId, kept, selected, cancellationToken))
        {
            // A concurrent request bootstrapped the same person first — two
            // similar-face requests overlap on any double click or slider drag.
            // Its set is just as valid, so read it rather than failing the search.
            return await ReadPersistedAsync(ownerUserId, personId, profileId, cancellationToken);
        }

        var byId = candidates.ToDictionary(c => c.FaceDetectionId, c => c.Vector);
        foreach (var c in keptCandidates)
        {
            byId[c.FaceDetectionId] = c.Vector;
        }
        return selected
            .Where(byId.ContainsKey)
            .Select(id => new PersonReferenceVector(id, byId[id]))
            .ToList();
    }

    // Fold newly assigned faces into an EXISTING reference set without touching
    // history. Also drops references that the assignment invalidated: a face moved
    // to another person stops being that person's reference.
    //
    // When the target person has no reference set yet, this does nothing on
    // purpose — the next similar-face request bootstraps it lazily, so an import
    // or a bulk cluster confirmation never turns into background face work.
    public async Task MaintainAfterAssignAsync(
        Guid ownerUserId, Guid personId, IReadOnlyList<Guid> faceIds,
        CancellationToken cancellationToken = default)
    {
        if (faceIds.Count == 0)
        {
            return;
        }

        var distinct = faceIds.Distinct().ToList();

        // These faces now belong to `personId`; any reference they held elsewhere
        // is stale. (The target person's own rows are left alone.)
        var moved = await _db.PersonFaceReferences
            .Where(r => r.OwnerUserId == ownerUserId && r.PersonId != personId && distinct.Contains(r.FaceDetectionId))
            .ToListAsync(cancellationToken);
        if (moved.Count > 0)
        {
            _db.PersonFaceReferences.RemoveRange(moved);
            if (!await TrySaveAsync(cancellationToken))
            {
                return; // another writer got there first; Ensure repairs the rest
            }
        }

        var existing = await _db.PersonFaceReferences
            .Where(r => r.OwnerUserId == ownerUserId && r.PersonId == personId)
            .OrderBy(r => r.Ordinal)
            .ToListAsync(cancellationToken);
        if (existing.Count == 0)
        {
            return; // nothing bootstrapped yet → leave it to the lazy path
        }

        // Only now is a threshold needed: an assignment to a person nobody has
        // searched yet costs one indexed lookup and nothing else.
        var settings = await _settings.GetAsync(cancellationToken);
        var coverageThreshold = settings.ClampSearchThreshold(settings.SearchDefaultSimilarityThreshold);

        foreach (var group in existing.GroupBy(r => r.ProfileId))
        {
            var rows = group.ToList();
            if (rows.Count >= MaxPersonReferenceFaces)
            {
                continue; // full: a new face does not displace a persisted reference
            }

            var known = rows.Select(r => r.FaceDetectionId).ToHashSet();
            var newIds = distinct.Where(id => !known.Contains(id)).ToList();
            if (newIds.Count == 0)
            {
                continue;
            }

            var seedById = await LoadEligibleAsync(ownerUserId, personId, group.Key, rows.Select(r => r.FaceDetectionId).ToList(), cancellationToken);
            var seed = rows
                .Where(r => seedById.ContainsKey(r.FaceDetectionId))
                .Select(r => seedById[r.FaceDetectionId])
                .ToList();
            if (seed.Count == 0)
            {
                continue; // every persisted reference is stale → let Ensure repair it
            }

            var candidates = (await LoadEligibleAsync(ownerUserId, personId, group.Key, newIds, cancellationToken))
                .Values.OrderBy(c => c.FaceDetectionId).ToList();
            if (candidates.Count == 0)
            {
                continue;
            }

            var selected = PersonReferenceSelector.Select(candidates, coverageThreshold, seed);
            // Best effort: a lost race here just means the next search's Ensure
            // does the folding instead. Maintenance must never fail an assignment.
            await PersistAdditionsAsync(ownerUserId, personId, group.Key, rows, selected, cancellationToken);
        }
    }

    // Drop every reference held by these faces (any person, this owner). Used when
    // a face is unassigned, ignored, or otherwise stops being confirmed evidence.
    public async Task DropForFacesAsync(
        Guid ownerUserId, IReadOnlyList<Guid> faceIds, CancellationToken cancellationToken = default)
    {
        if (faceIds.Count == 0)
        {
            return;
        }

        var rows = await _db.PersonFaceReferences
            .Where(r => r.OwnerUserId == ownerUserId && faceIds.Contains(r.FaceDetectionId))
            .ToListAsync(cancellationToken);
        if (rows.Count > 0)
        {
            _db.PersonFaceReferences.RemoveRange(rows);
        }
    }

    // Drop a person's whole reference set (archive). Queued on the caller's unit of
    // work so it commits with the archive itself.
    public async Task DropForPersonAsync(
        Guid ownerUserId, Guid personId, CancellationToken cancellationToken = default)
    {
        var rows = await _db.PersonFaceReferences
            .Where(r => r.OwnerUserId == ownerUserId && r.PersonId == personId)
            .ToListAsync(cancellationToken);
        if (rows.Count > 0)
        {
            _db.PersonFaceReferences.RemoveRange(rows);
        }
    }

    // ---- internals -------------------------------------------------------

    // The persisted set, re-read after losing a race. Only faces that are still
    // eligible are returned, so a winner's stale row cannot steer this search.
    private async Task<IReadOnlyList<PersonReferenceVector>> ReadPersistedAsync(
        Guid ownerUserId, Guid personId, Guid profileId, CancellationToken cancellationToken)
    {
        var rows = await _db.PersonFaceReferences.AsNoTracking()
            .Where(r => r.OwnerUserId == ownerUserId && r.PersonId == personId && r.ProfileId == profileId)
            .OrderBy(r => r.Ordinal)
            .ToListAsync(cancellationToken);
        if (rows.Count == 0)
        {
            return Array.Empty<PersonReferenceVector>();
        }

        var usable = await LoadEligibleAsync(
            ownerUserId, personId, profileId, rows.Select(r => r.FaceDetectionId).ToList(), cancellationToken);
        return rows
            .Where(r => usable.ContainsKey(r.FaceDetectionId))
            .Select(r => new PersonReferenceVector(r.FaceDetectionId, usable[r.FaceDetectionId].Vector))
            .ToList();
    }

    // Save, treating a lost race as an outcome rather than an error. The reference
    // set is DERIVED: when a concurrent request already wrote (or already removed)
    // these rows there is nothing to repair, so the pending changes are discarded
    // and the caller re-reads. Failing here would turn overlapping reads of the
    // People page into a 500 on a cache fill.
    private async Task<bool> TrySaveAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _db.SaveChangesAsync(cancellationToken);
            return true;
        }
        catch (DbUpdateException)
        {
            foreach (var entry in _db.ChangeTracker.Entries<PersonFaceReference>().ToList())
            {
                entry.State = EntityState.Detached;
            }
            return false;
        }
    }

    private async Task<bool> PersistAdditionsAsync(
        Guid ownerUserId, Guid personId, Guid profileId, IReadOnlyList<PersonFaceReference> kept,
        IReadOnlyList<Guid> selected, CancellationToken cancellationToken)
    {
        var existingIds = kept.Select(r => r.FaceDetectionId).ToHashSet();
        var additions = selected.Where(id => !existingIds.Contains(id)).ToList();
        if (additions.Count == 0)
        {
            return true;
        }

        var usedOrdinals = kept.Select(r => r.Ordinal).ToHashSet();
        var now = _clock.GetUtcNow().UtcDateTime;
        foreach (var faceId in additions)
        {
            var ordinal = -1;
            for (var i = 0; i < MaxPersonReferenceFaces; i++)
            {
                if (usedOrdinals.Add(i))
                {
                    ordinal = i;
                    break;
                }
            }
            if (ordinal < 0)
            {
                break; // set is full — the cap is enforced here as well as in SQL
            }

            _db.PersonFaceReferences.Add(new PersonFaceReference
            {
                Id = Guid.NewGuid(),
                OwnerUserId = ownerUserId,
                PersonId = personId,
                ProfileId = profileId,
                FaceDetectionId = faceId,
                Ordinal = ordinal,
                CreatedAt = now,
            });
        }

        return await TrySaveAsync(cancellationToken);
    }

    // The person's confirmed faces that could serve as a reference, best quality
    // first and capped. Eligible = still assigned to THIS person, surfaceable to
    // the owner (visible, non-vault), with a completed embedding in this profile.
    private async Task<List<PersonReferenceSelector.ReferenceCandidate>> LoadCandidatesAsync(
        Guid ownerUserId, Guid personId, Guid profileId, CancellationToken cancellationToken)
    {
        var rows = await (
            from a in _db.PersonFaceAssignments.AsNoTracking()
            where a.OwnerUserId == ownerUserId && a.PersonId == personId
            join d in _db.FaceDetections.AsNoTracking() on a.FaceDetectionId equals d.Id
            join e in _db.FaceEmbeddings.AsNoTracking() on d.Id equals e.FaceDetectionId
            where e.ProfileId == profileId && e.EmbeddingStatus == AiArtifactStatuses.Completed
                && _db.FileItems.Any(f => f.BlobObjectId == d.BlobObjectId
                    && f.OwnerUserId == ownerUserId && f.DeletedAt == null
                    && f.MediaLibraryState == MediaLibraryState.Active)
            orderby (d.FaceQualityScore ?? d.DetectionScore ?? 0d) descending, d.Id
            select new EligibleRow(d.Id, d.FaceQualityScore ?? d.DetectionScore ?? 0d, e.EmbeddingBytes))
            .Take(CandidateScanCap)
            .ToListAsync(cancellationToken);

        return Materialize(rows);
    }

    private async Task<Dictionary<Guid, PersonReferenceSelector.ReferenceCandidate>> LoadEligibleAsync(
        Guid ownerUserId, Guid personId, Guid profileId, IReadOnlyList<Guid> faceIds, CancellationToken cancellationToken)
    {
        if (faceIds.Count == 0)
        {
            return new Dictionary<Guid, PersonReferenceSelector.ReferenceCandidate>();
        }

        var rows = await (
            from a in _db.PersonFaceAssignments.AsNoTracking()
            where a.OwnerUserId == ownerUserId && a.PersonId == personId && faceIds.Contains(a.FaceDetectionId)
            join d in _db.FaceDetections.AsNoTracking() on a.FaceDetectionId equals d.Id
            join e in _db.FaceEmbeddings.AsNoTracking() on d.Id equals e.FaceDetectionId
            where e.ProfileId == profileId && e.EmbeddingStatus == AiArtifactStatuses.Completed
                && _db.FileItems.Any(f => f.BlobObjectId == d.BlobObjectId
                    && f.OwnerUserId == ownerUserId && f.DeletedAt == null
                    && f.MediaLibraryState == MediaLibraryState.Active)
            select new EligibleRow(d.Id, d.FaceQualityScore ?? d.DetectionScore ?? 0d, e.EmbeddingBytes))
            .ToListAsync(cancellationToken);

        return Materialize(rows).ToDictionary(c => c.FaceDetectionId);
    }

    private List<PersonReferenceSelector.ReferenceCandidate> Materialize(IReadOnlyList<EligibleRow> rows)
    {
        var result = new List<PersonReferenceSelector.ReferenceCandidate>(rows.Count);
        foreach (var row in rows)
        {
            float[] vector;
            try
            {
                vector = _serializer.Deserialize(row.EmbeddingBytes);
            }
            catch
            {
                continue; // undeserializable → not a usable reference
            }
            if (vector.Length == 0)
            {
                continue;
            }
            result.Add(new PersonReferenceSelector.ReferenceCandidate(row.FaceId, vector, row.Quality));
        }
        return result;
    }

    private sealed record EligibleRow(Guid FaceId, double Quality, byte[] EmbeddingBytes);
}
