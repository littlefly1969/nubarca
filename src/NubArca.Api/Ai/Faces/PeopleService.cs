using System.Globalization;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using NubArca.Api.Data;
using NubArca.Api.Domain;
using NubArca.Api.Domain.Ai;

namespace NubArca.Api.Ai.Faces;

// Owner-private People/Face experience (People v0). ALL reads/writes are scoped to
// one owner; there is no cross-owner identity. Every face surfaced must have an
// active, NON-VAULT FileItem owned by the caller (the FileItems predicate carries
// the global Private-Vault filter), so vaulted / vault-only faces never appear and
// no vault existence/counts leak. DTOs expose only logical file ids + names +
// normalized boxes + rounded scores — never raw vectors, BlobObjectId, SHA,
// StorageKey, paths, or model internals.
public sealed class PeopleService
{
    public const int DefaultPageSize = 50;
    public const int MaxPageSize = 100;
    public const int SimilarFetchCap = 200;

    private readonly AppDbContext _db;
    private readonly IAiProfileRegistry _registry;
    private readonly IOptions<AiOptions> _options;
    private readonly IFaceSettingsProvider _settings;
    private readonly IAiVectorSerializer _serializer;
    private readonly FaceVectorIndexService _vectors;
    private readonly TimeProvider _clock;

    public PeopleService(
        AppDbContext db,
        IAiProfileRegistry registry,
        IOptions<AiOptions> options,
        IFaceSettingsProvider settings,
        IAiVectorSerializer serializer,
        FaceVectorIndexService vectors,
        TimeProvider clock)
    {
        _db = db;
        _registry = registry;
        _options = options;
        _settings = settings;
        _serializer = serializer;
        _vectors = vectors;
        _clock = clock;
    }

    // ---- people list / CRUD ----------------------------------------------

    public async Task<IReadOnlyList<PersonDto>> ListPeopleAsync(Guid ownerUserId, CancellationToken cancellationToken = default)
    {
        var people = await _db.People.AsNoTracking()
            .Where(p => p.OwnerUserId == ownerUserId && !p.IsArchived)
            .OrderBy(p => p.DisplayName).ThenBy(p => p.CreatedAt)
            .ToListAsync(cancellationToken);
        if (people.Count == 0)
        {
            return Array.Empty<PersonDto>();
        }

        var ids = people.Select(p => p.Id).ToList();
        var counts = await SurfaceableFaceCountsByPersonAsync(ownerUserId, ids, cancellationToken);
        var reps = await RepresentativeRefByPersonAsync(ownerUserId, ids, cancellationToken);

        return people
            .Select(p => new PersonDto(
                p.Id, p.DisplayName,
                counts.GetValueOrDefault(p.Id, 0),
                reps.GetValueOrDefault(p.Id)))
            .ToList();
    }

    public async Task<PersonDto?> GetPersonAsync(Guid ownerUserId, Guid personId, CancellationToken cancellationToken = default)
    {
        var person = await _db.People.AsNoTracking()
            .FirstOrDefaultAsync(p => p.Id == personId && p.OwnerUserId == ownerUserId && !p.IsArchived, cancellationToken);
        if (person is null)
        {
            return null;
        }

        var counts = await SurfaceableFaceCountsByPersonAsync(ownerUserId, new List<Guid> { personId }, cancellationToken);
        var reps = await RepresentativeRefByPersonAsync(ownerUserId, new List<Guid> { personId }, cancellationToken);
        return new PersonDto(person.Id, person.DisplayName, counts.GetValueOrDefault(personId, 0), reps.GetValueOrDefault(personId));
    }

    public async Task<PersonDto> CreatePersonAsync(Guid ownerUserId, string? displayName, CancellationToken cancellationToken = default)
    {
        var name = await ResolveNameAsync(ownerUserId, displayName, personIdToIgnore: null, cancellationToken);
        var now = _clock.GetUtcNow().UtcDateTime;
        var person = new Person { Id = Guid.NewGuid(), OwnerUserId = ownerUserId, DisplayName = name, CreatedAt = now };
        _db.People.Add(person);
        await _db.SaveChangesAsync(cancellationToken);
        return new PersonDto(person.Id, person.DisplayName, 0, null);
    }

    public async Task<PersonDto?> RenamePersonAsync(Guid ownerUserId, Guid personId, string? displayName, CancellationToken cancellationToken = default)
    {
        var person = await _db.People.FirstOrDefaultAsync(
            p => p.Id == personId && p.OwnerUserId == ownerUserId && !p.IsArchived, cancellationToken);
        if (person is null)
        {
            return null;
        }

        person.DisplayName = await ResolveNameAsync(ownerUserId, displayName, personId, cancellationToken);
        person.UpdatedAt = _clock.GetUtcNow().UtcDateTime;
        await _db.SaveChangesAsync(cancellationToken);
        return await GetPersonAsync(ownerUserId, personId, cancellationToken);
    }

    // Archive (soft): never a hard delete of derived data. Unlinks the person's
    // clusters (FK SetNull) and drops the person's confirmed assignments so its
    // faces return to the suggestion pool on the next cluster run.
    public async Task<bool> ArchivePersonAsync(Guid ownerUserId, Guid personId, CancellationToken cancellationToken = default)
    {
        var person = await _db.People.FirstOrDefaultAsync(
            p => p.Id == personId && p.OwnerUserId == ownerUserId && !p.IsArchived, cancellationToken);
        if (person is null)
        {
            return false;
        }

        var assignments = await _db.PersonFaceAssignments
            .Where(a => a.OwnerUserId == ownerUserId && a.PersonId == personId)
            .ToListAsync(cancellationToken);
        _db.PersonFaceAssignments.RemoveRange(assignments);

        var clusters = await _db.FaceClusters
            .Where(c => c.OwnerUserId == ownerUserId && c.PersonId == personId)
            .ToListAsync(cancellationToken);
        foreach (var c in clusters)
        {
            c.PersonId = null;
            c.Status = FaceClusterStatuses.Suggested;
            c.UpdatedAt = _clock.GetUtcNow().UtcDateTime;
        }

        person.IsArchived = true;
        person.UpdatedAt = _clock.GetUtcNow().UtcDateTime;
        await _db.SaveChangesAsync(cancellationToken);
        return true;
    }

    // ---- suggested groups / review ---------------------------------------

    public Task<IReadOnlyList<SuggestedGroupDto>> ListGroupsAsync(
        Guid ownerUserId, bool review, CancellationToken cancellationToken = default)
        => ListGroupsByStatusAsync(
            ownerUserId, review ? FaceClusterStatuses.NeedsReview : FaceClusterStatuses.Suggested, cancellationToken);

    private async Task<IReadOnlyList<SuggestedGroupDto>> ListGroupsByStatusAsync(
        Guid ownerUserId, string status, CancellationToken cancellationToken = default)
    {
        var clusters = await _db.FaceClusters.AsNoTracking()
            .Where(c => c.OwnerUserId == ownerUserId && c.Status == status)
            .OrderByDescending(c => c.MemberCount)
            .Take(MaxPageSize)
            .ToListAsync(cancellationToken);
        if (clusters.Count == 0)
        {
            return Array.Empty<SuggestedGroupDto>();
        }

        // Representative face refs, resolved through the owner's visible library.
        var repFaceIds = clusters.Where(c => c.RepresentativeFaceDetectionId.HasValue)
            .Select(c => c.RepresentativeFaceDetectionId!.Value).Distinct().ToList();
        var refs = await ResolveFaceRefsAsync(ownerUserId, repFaceIds, cancellationToken);

        // Surfaceable member counts per cluster (excludes vaulted/hidden faces).
        var clusterIds = clusters.Select(c => c.Id).ToList();
        var memberCounts = await SurfaceableMemberCountsByClusterAsync(ownerUserId, clusterIds, cancellationToken);

        var result = new List<SuggestedGroupDto>(clusters.Count);
        foreach (var c in clusters)
        {
            var count = memberCounts.GetValueOrDefault(c.Id, 0);
            if (count == 0)
            {
                continue; // every member is hidden (e.g. all vaulted) → don't surface
            }
            FaceRefDto? rep = c.RepresentativeFaceDetectionId.HasValue
                ? refs.GetValueOrDefault(c.RepresentativeFaceDetectionId.Value)
                : null;
            rep ??= await FirstSurfaceableMemberRefAsync(ownerUserId, c.Id, cancellationToken);
            result.Add(new SuggestedGroupDto(c.Id, rep, count, c.ConfidenceAggregate, c.Status));
        }

        return result;
    }

    // Name a suggested group → create/reuse a Person, confirm the cluster, and
    // create confirmed assignments for its members. Owner-scoped.
    public async Task<PersonDto?> AssignGroupAsync(
        Guid ownerUserId, Guid groupId, string? personName, Guid? existingPersonId, CancellationToken cancellationToken = default)
    {
        var cluster = await _db.FaceClusters.FirstOrDefaultAsync(
            c => c.Id == groupId && c.OwnerUserId == ownerUserId, cancellationToken);
        if (cluster is null)
        {
            return null;
        }

        Person person;
        if (existingPersonId is Guid pid)
        {
            var existing = await _db.People.FirstOrDefaultAsync(
                p => p.Id == pid && p.OwnerUserId == ownerUserId && !p.IsArchived, cancellationToken);
            if (existing is null)
            {
                return null;
            }
            person = existing;
        }
        else
        {
            var now0 = _clock.GetUtcNow().UtcDateTime;
            person = new Person
            {
                Id = Guid.NewGuid(),
                OwnerUserId = ownerUserId,
                DisplayName = await ResolveNameAsync(ownerUserId, personName, null, cancellationToken),
                CreatedAt = now0,
            };
            _db.People.Add(person);
        }

        var memberFaceIds = await _db.FaceClusterMembers.AsNoTracking()
            .Where(m => m.FaceClusterId == groupId)
            .Select(m => m.FaceDetectionId)
            .ToListAsync(cancellationToken);

        await AssignFacesAsync(ownerUserId, person.Id, memberFaceIds, PersonFaceAssignmentSources.ClusterConfirmed, cancellationToken);

        var now = _clock.GetUtcNow().UtcDateTime;
        cluster.PersonId = person.Id;
        cluster.Status = FaceClusterStatuses.Confirmed;
        cluster.UpdatedAt = now;
        person.UpdatedAt = now;
        await _db.SaveChangesAsync(cancellationToken);

        return await GetPersonAsync(ownerUserId, person.Id, cancellationToken);
    }

    // Ignore a whole suggested/review group as a BULK PER-FACE action (not a
    // group-entity ignore): every surfaceable member face is individually ignored —
    // exactly as if the owner had ignored each face one by one — so each lands in the
    // "Ignorati" tab and can be restored individually. Any existing person assignment
    // on those faces is removed (ignore wins, mirroring IgnoreFaceAsync). Because the
    // group's surfaceable count then excludes ignored faces, the group drops out of
    // the suggested queue immediately, and the ignored faces stay out of future
    // clustering until restored. Returns the number of faces newly ignored; null =
    // cross-owner / missing group (generic 404). Owner-scoped; no internals.
    public async Task<int?> IgnoreGroupAsync(
        Guid ownerUserId, Guid groupId, CancellationToken cancellationToken = default)
    {
        var cluster = await _db.FaceClusters.AsNoTracking().FirstOrDefaultAsync(
            c => c.Id == groupId && c.OwnerUserId == ownerUserId, cancellationToken);
        if (cluster is null)
        {
            return null;
        }

        var memberFaceIds = await _db.FaceClusterMembers.AsNoTracking()
            .Where(m => m.FaceClusterId == groupId)
            .Select(m => m.FaceDetectionId)
            .ToListAsync(cancellationToken);

        // Only surfaceable members (owner-visible, non-vault) — the same faces the
        // owner actually sees in the group; vaulted/hidden faces are never surfaced.
        var refs = await ResolveFaceRefsAsync(ownerUserId, memberFaceIds, cancellationToken);
        var surfaceable = memberFaceIds.Where(id => refs.ContainsKey(id)).ToList();
        if (surfaceable.Count == 0)
        {
            return 0;
        }

        // Ignore wins over assignment (as in IgnoreFaceAsync).
        var assignments = await _db.PersonFaceAssignments
            .Where(a => a.OwnerUserId == ownerUserId && surfaceable.Contains(a.FaceDetectionId))
            .ToListAsync(cancellationToken);
        _db.PersonFaceAssignments.RemoveRange(assignments);

        var alreadyIgnored = (await _db.IgnoredFaces.AsNoTracking()
            .Where(i => i.OwnerUserId == ownerUserId && surfaceable.Contains(i.FaceDetectionId))
            .Select(i => i.FaceDetectionId)
            .ToListAsync(cancellationToken)).ToHashSet();

        var now = _clock.GetUtcNow().UtcDateTime;
        var added = 0;
        foreach (var faceId in surfaceable)
        {
            if (alreadyIgnored.Contains(faceId))
            {
                continue;
            }
            _db.IgnoredFaces.Add(new IgnoredFace
            {
                Id = Guid.NewGuid(),
                OwnerUserId = ownerUserId,
                FaceDetectionId = faceId,
                CreatedAt = now,
            });
            added++;
        }

        await _db.SaveChangesAsync(cancellationToken);
        return added;
    }

    // Associate an entire cluster with a person: assign all ELIGIBLE members
    // (owner-visible, non-vault, not ignored) that are unassigned. By default faces
    // already on ANOTHER person are SKIPPED (never silently stolen); pass
    // moveAssigned=true to reassign those too. dryRun computes the same summary
    // without persisting (for the confirm dialog's counts). On a real run the
    // cluster is Confirmed → it leaves the suggested queue and its faces are
    // excluded from future clustering. Null = generic 404 (cross-owner / missing
    // person or cluster). One-person-per-face preserved. No internals.
    public async Task<ClusterAssignSummaryDto?> AssignClusterToPersonAsync(
        Guid ownerUserId, Guid personId, Guid clusterId, bool moveAssigned, bool dryRun,
        CancellationToken cancellationToken = default)
    {
        var person = await _db.People.FirstOrDefaultAsync(
            p => p.Id == personId && p.OwnerUserId == ownerUserId && !p.IsArchived, cancellationToken);
        if (person is null)
        {
            return null;
        }
        var cluster = await _db.FaceClusters.FirstOrDefaultAsync(
            c => c.Id == clusterId && c.OwnerUserId == ownerUserId, cancellationToken);
        if (cluster is null)
        {
            return null;
        }

        var memberFaceIds = await _db.FaceClusterMembers.AsNoTracking()
            .Where(m => m.FaceClusterId == clusterId)
            .Select(m => m.FaceDetectionId)
            .ToListAsync(cancellationToken);

        // Surfaceable = owner-visible, non-vault (ineligible = vaulted / hidden / missing).
        var refs = await ResolveFaceRefsAsync(ownerUserId, memberFaceIds, cancellationToken);
        var surfaceable = memberFaceIds.Where(id => refs.ContainsKey(id)).ToList();
        var skippedIneligible = memberFaceIds.Count - surfaceable.Count;

        // Individually-ignored members are skipped (dismiss wins).
        var ignoredSet = (await _db.IgnoredFaces.AsNoTracking()
            .Where(i => i.OwnerUserId == ownerUserId && surfaceable.Contains(i.FaceDetectionId))
            .Select(i => i.FaceDetectionId)
            .ToListAsync(cancellationToken)).ToHashSet();
        var remaining = surfaceable.Where(id => !ignoredSet.Contains(id)).ToList();
        var skippedIgnored = surfaceable.Count - remaining.Count;

        var existing = await _db.PersonFaceAssignments.AsNoTracking()
            .Where(a => a.OwnerUserId == ownerUserId && remaining.Contains(a.FaceDetectionId))
            .Select(a => new { a.FaceDetectionId, a.PersonId })
            .ToListAsync(cancellationToken);
        var byFace = existing.ToDictionary(x => x.FaceDetectionId, x => x.PersonId);

        var free = remaining.Where(id => !byFace.ContainsKey(id)).ToList();
        var onOther = remaining.Where(id => byFace.TryGetValue(id, out var pid) && pid != personId).ToList();

        var assignedCount = free.Count + (moveAssigned ? onOther.Count : 0);
        var skippedAlreadyAssigned = moveAssigned ? 0 : onOther.Count;

        if (dryRun)
        {
            return new ClusterAssignSummaryDto(
                assignedCount, skippedAlreadyAssigned, skippedIgnored, skippedIneligible, cluster.Status);
        }

        var toAssign = new List<Guid>(free);
        if (moveAssigned)
        {
            toAssign.AddRange(onOther);
        }
        await AssignFacesAsync(ownerUserId, personId, toAssign, PersonFaceAssignmentSources.ClusterConfirmed, cancellationToken);

        var now = _clock.GetUtcNow().UtcDateTime;
        cluster.PersonId = personId;
        cluster.Status = FaceClusterStatuses.Confirmed;
        cluster.UpdatedAt = now;
        person.UpdatedAt = now;
        await _db.SaveChangesAsync(cancellationToken);

        return new ClusterAssignSummaryDto(
            assignedCount, skippedAlreadyAssigned, skippedIgnored, skippedIneligible, cluster.Status);
    }

    // All surfaceable (owner-visible, non-vault) faces of a suggested/review group,
    // so the whole group can be reviewed face-by-face in the context viewer (not
    // just the representative photo). Representative first, then the rest.
    // Null = generic 404 (cross-owner / missing group).
    public async Task<IReadOnlyList<FaceRefDto>?> GetGroupFacesAsync(
        Guid ownerUserId, Guid groupId, CancellationToken cancellationToken = default)
    {
        var cluster = await _db.FaceClusters.AsNoTracking()
            .Where(c => c.Id == groupId && c.OwnerUserId == ownerUserId)
            .Select(c => new { c.RepresentativeFaceDetectionId })
            .FirstOrDefaultAsync(cancellationToken);
        if (cluster is null)
        {
            return null;
        }

        var memberFaceIds = await _db.FaceClusterMembers.AsNoTracking()
            .Where(m => m.FaceClusterId == groupId)
            .OrderBy(m => m.FaceDetectionId)
            .Select(m => m.FaceDetectionId)
            .ToListAsync(cancellationToken);

        var refs = await ResolveFaceRefsAsync(ownerUserId, memberFaceIds, cancellationToken);

        // Deterministic order, representative first (if surfaceable).
        var ordered = memberFaceIds.Where(id => refs.ContainsKey(id)).ToList();
        var repId = cluster.RepresentativeFaceDetectionId;
        if (repId.HasValue && ordered.Contains(repId.Value))
        {
            ordered.Remove(repId.Value);
            ordered.Insert(0, repId.Value);
        }
        return ordered.Select(id => refs[id]).ToList();
    }

    // ---- person faces / photos -------------------------------------------

    public async Task<bool> AddFaceToPersonAsync(
        Guid ownerUserId, Guid personId, Guid faceDetectionId, CancellationToken cancellationToken = default)
    {
        var person = await _db.People.AnyAsync(
            p => p.Id == personId && p.OwnerUserId == ownerUserId && !p.IsArchived, cancellationToken);
        if (!person)
        {
            return false;
        }

        // The face must be surfaceable to this owner (visible, non-vault).
        var refs = await ResolveFaceRefsAsync(ownerUserId, new List<Guid> { faceDetectionId }, cancellationToken);
        if (!refs.ContainsKey(faceDetectionId))
        {
            return false;
        }

        await AssignFacesAsync(ownerUserId, personId, new List<Guid> { faceDetectionId }, PersonFaceAssignmentSources.ManualAdd, cancellationToken);
        await _db.SaveChangesAsync(cancellationToken);
        return true;
    }

    public async Task<bool> RemoveFaceFromPersonAsync(
        Guid ownerUserId, Guid personId, Guid faceDetectionId, CancellationToken cancellationToken = default)
    {
        var assignment = await _db.PersonFaceAssignments.FirstOrDefaultAsync(
            a => a.OwnerUserId == ownerUserId && a.PersonId == personId && a.FaceDetectionId == faceDetectionId, cancellationToken);
        if (assignment is null)
        {
            return false;
        }

        _db.PersonFaceAssignments.Remove(assignment);
        await _db.SaveChangesAsync(cancellationToken);
        return true;
    }

    // Assign ONE face to a person — either an existing person (personId) or a new
    // one (newPersonName). Honors one-person-per-face: if the face was on another
    // person it is MOVED here. Assigning also clears any per-face "ignored" mark.
    // Returns the target person (with refreshed counts) or null = generic 404
    // (face not surfaceable, or personId not found / archived / cross-owner).
    public async Task<PersonDto?> AssignFaceAsync(
        Guid ownerUserId, Guid faceDetectionId, Guid? personId, string? newPersonName,
        CancellationToken cancellationToken = default)
    {
        // The face must be surfaceable to this owner (visible, non-vault).
        var refs = await ResolveFaceRefsAsync(ownerUserId, new List<Guid> { faceDetectionId }, cancellationToken);
        if (!refs.ContainsKey(faceDetectionId))
        {
            return null;
        }

        Person person;
        if (personId is Guid pid)
        {
            var existing = await _db.People.FirstOrDefaultAsync(
                p => p.Id == pid && p.OwnerUserId == ownerUserId && !p.IsArchived, cancellationToken);
            if (existing is null)
            {
                return null;
            }
            person = existing;
        }
        else
        {
            person = new Person
            {
                Id = Guid.NewGuid(),
                OwnerUserId = ownerUserId,
                DisplayName = await ResolveNameAsync(ownerUserId, newPersonName, null, cancellationToken),
                CreatedAt = _clock.GetUtcNow().UtcDateTime,
            };
            _db.People.Add(person);
        }

        await RemoveIgnoreRowsAsync(ownerUserId, new List<Guid> { faceDetectionId }, cancellationToken);
        await AssignFacesAsync(ownerUserId, person.Id, new List<Guid> { faceDetectionId }, PersonFaceAssignmentSources.ManualAdd, cancellationToken);
        person.UpdatedAt = _clock.GetUtcNow().UtcDateTime;
        await _db.SaveChangesAsync(cancellationToken);

        return await GetPersonAsync(ownerUserId, person.Id, cancellationToken);
    }

    // Remove a face's person assignment regardless of which person it was on
    // (owner-scoped). The face becomes unassigned and eligible for clustering again.
    public async Task<bool> RemoveFaceAssignmentAsync(
        Guid ownerUserId, Guid faceDetectionId, CancellationToken cancellationToken = default)
    {
        var assignment = await _db.PersonFaceAssignments.FirstOrDefaultAsync(
            a => a.OwnerUserId == ownerUserId && a.FaceDetectionId == faceDetectionId, cancellationToken);
        if (assignment is null)
        {
            return false;
        }
        _db.PersonFaceAssignments.Remove(assignment);
        await _db.SaveChangesAsync(cancellationToken);
        return true;
    }

    // Dismiss a single face (mis-detection / stranger): it stops appearing in
    // suggestions and the unassigned view, and is excluded from clustering. Also
    // drops any existing person assignment. Reversible via UnignoreFaceAsync (the
    // prior person assignment is NOT restored). Null-safe: false = generic 404.
    public async Task<bool> IgnoreFaceAsync(
        Guid ownerUserId, Guid faceDetectionId, CancellationToken cancellationToken = default)
    {
        var refs = await ResolveFaceRefsAsync(ownerUserId, new List<Guid> { faceDetectionId }, cancellationToken);
        if (!refs.ContainsKey(faceDetectionId))
        {
            return false;
        }

        var assignments = await _db.PersonFaceAssignments
            .Where(a => a.OwnerUserId == ownerUserId && a.FaceDetectionId == faceDetectionId)
            .ToListAsync(cancellationToken);
        _db.PersonFaceAssignments.RemoveRange(assignments);

        var already = await _db.IgnoredFaces.AnyAsync(
            i => i.OwnerUserId == ownerUserId && i.FaceDetectionId == faceDetectionId, cancellationToken);
        if (!already)
        {
            _db.IgnoredFaces.Add(new IgnoredFace
            {
                Id = Guid.NewGuid(),
                OwnerUserId = ownerUserId,
                FaceDetectionId = faceDetectionId,
                CreatedAt = _clock.GetUtcNow().UtcDateTime,
            });
        }
        await _db.SaveChangesAsync(cancellationToken);
        return true;
    }

    // Un-dismiss a face → it becomes an unassigned candidate again (and eligible
    // for clustering). false = it was not ignored.
    public async Task<bool> UnignoreFaceAsync(
        Guid ownerUserId, Guid faceDetectionId, CancellationToken cancellationToken = default)
    {
        var rows = await _db.IgnoredFaces
            .Where(i => i.OwnerUserId == ownerUserId && i.FaceDetectionId == faceDetectionId)
            .ToListAsync(cancellationToken);
        if (rows.Count == 0)
        {
            return false;
        }
        _db.IgnoredFaces.RemoveRange(rows);
        await _db.SaveChangesAsync(cancellationToken);
        return true;
    }

    // Owner-visible, non-vault faces the owner has individually ignored — so a
    // mistaken ignore can be restored (un-ignore). Keyset-paged by ignore time
    // (most recent first). Sanitized (no vectors/blob-id/SHA/path).
    public async Task<IgnoredFacesPage> GetIgnoredFacesAsync(
        Guid ownerUserId, int limit, string? cursor, CancellationToken cancellationToken = default)
    {
        var pageSize = Math.Clamp(limit, 1, MaxPageSize);
        var after = DecodeCursor2(cursor);

        var q =
            from ig in _db.IgnoredFaces.AsNoTracking()
            where ig.OwnerUserId == ownerUserId
            join d in _db.FaceDetections.AsNoTracking() on ig.FaceDetectionId equals d.Id
            where _db.FileItems.Any(f => f.BlobObjectId == d.BlobObjectId && f.OwnerUserId == ownerUserId && f.DeletedAt == null && f.MediaLibraryState == MediaLibraryState.Active)
            select new { ig.CreatedAt, Face = d };

        if (after is not null && long.TryParse(after.Value.SortRaw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var ticks))
        {
            var t = new DateTime(ticks, DateTimeKind.Utc);
            q = q.Where(r => r.CreatedAt < t || (r.CreatedAt == t && r.Face.Id.CompareTo(after.Value.FaceId) > 0));
        }

        var rows = await q
            .OrderByDescending(r => r.CreatedAt).ThenBy(r => r.Face.Id)
            .Take(pageSize + 1)
            .Select(r => new
            {
                r.CreatedAt,
                r.Face.Id,
                r.Face.BoundingBoxX,
                r.Face.BoundingBoxY,
                r.Face.BoundingBoxWidth,
                r.Face.BoundingBoxHeight,
                File = _db.FileItems
                    .Where(f => f.BlobObjectId == r.Face.BlobObjectId && f.OwnerUserId == ownerUserId && f.DeletedAt == null && f.MediaLibraryState == MediaLibraryState.Active)
                    .OrderBy(f => f.Id)
                    .Select(f => new { f.Id, f.Name })
                    .FirstOrDefault(),
            })
            .ToListAsync(cancellationToken);

        var hasMore = rows.Count > pageSize;
        var window = rows.Take(pageSize).ToList();
        var items = window
            .Where(r => r.File is not null)
            .Select(r => new IgnoredFaceDto(
                r.Id, r.File!.Id, r.File.Name,
                new FaceBoxDto(
                    Math.Round(r.BoundingBoxX, 6), Math.Round(r.BoundingBoxY, 6),
                    Math.Round(r.BoundingBoxWidth, 6), Math.Round(r.BoundingBoxHeight, 6))))
            .ToList();

        string? nextCursor = hasMore && window.Count > 0
            ? EncodeCursor2(window[^1].CreatedAt.Ticks.ToString(CultureInfo.InvariantCulture), window[^1].Id)
            : null;

        return new IgnoredFacesPage(items, nextCursor);
    }

    private async Task RemoveIgnoreRowsAsync(
        Guid ownerUserId, IReadOnlyList<Guid> faceIds, CancellationToken cancellationToken)
    {
        var rows = await _db.IgnoredFaces
            .Where(i => i.OwnerUserId == ownerUserId && faceIds.Contains(i.FaceDetectionId))
            .ToListAsync(cancellationToken);
        if (rows.Count > 0)
        {
            _db.IgnoredFaces.RemoveRange(rows);
        }
    }

    // Owner-visible, non-vault faces (active profile) not assigned to any person and
    // not individually ignored — the pool the owner works through to build people.
    // Keyset-paged (sort: recent|quality|detection). hasEmbedding: true = only faces
    // with a completed embedding, false = only faces without, null = all. Sanitized.
    public async Task<UnassignedFacesPage> GetUnassignedFacesAsync(
        Guid ownerUserId, int limit, string? cursor, bool? hasEmbedding, string? sort,
        CancellationToken cancellationToken = default)
    {
        var pageSize = Math.Clamp(limit, 1, MaxPageSize);
        var profile = await ResolveActiveProfileAsync(cancellationToken);
        if (profile is null)
        {
            return new UnassignedFacesPage(Array.Empty<UnassignedFaceDto>(), null, false);
        }
        var profileId = profile.Id;
        var sortKey = (sort ?? "recent").Trim().ToLowerInvariant();

        var q =
            from d in _db.FaceDetections.AsNoTracking()
            where d.ProfileId == profileId
                && !_db.PersonFaceAssignments.Any(a => a.OwnerUserId == ownerUserId && a.FaceDetectionId == d.Id)
                && !_db.IgnoredFaces.Any(g => g.OwnerUserId == ownerUserId && g.FaceDetectionId == d.Id)
                && _db.FileItems.Any(f => f.BlobObjectId == d.BlobObjectId && f.OwnerUserId == ownerUserId && f.DeletedAt == null && f.MediaLibraryState == MediaLibraryState.Active)
            select d;

        var withEmbedding = _db.FaceEmbeddings.Where(e => e.ProfileId == profileId && e.EmbeddingStatus == AiArtifactStatuses.Completed);
        if (hasEmbedding == true)
        {
            q = q.Where(d => withEmbedding.Any(e => e.FaceDetectionId == d.Id));
        }
        else if (hasEmbedding == false)
        {
            q = q.Where(d => !withEmbedding.Any(e => e.FaceDetectionId == d.Id));
        }

        var after = DecodeCursor2(cursor);

        // Apply sort + keyset cursor. Score sorts coalesce quality→detection→0.
        IQueryable<FaceDetection> ordered;
        if (sortKey == "quality" || sortKey == "detection")
        {
            if (after is not null && double.TryParse(after.Value.SortRaw, NumberStyles.Float, CultureInfo.InvariantCulture, out var s))
            {
                q = sortKey == "quality"
                    ? q.Where(d => ((d.FaceQualityScore ?? d.DetectionScore ?? 0) < s)
                        || ((d.FaceQualityScore ?? d.DetectionScore ?? 0) == s && d.Id.CompareTo(after.Value.FaceId) > 0))
                    : q.Where(d => ((d.DetectionScore ?? 0) < s)
                        || ((d.DetectionScore ?? 0) == s && d.Id.CompareTo(after.Value.FaceId) > 0));
            }
            ordered = sortKey == "quality"
                ? q.OrderByDescending(d => d.FaceQualityScore ?? d.DetectionScore ?? 0).ThenBy(d => d.Id)
                : q.OrderByDescending(d => d.DetectionScore ?? 0).ThenBy(d => d.Id);
        }
        else // recent (default)
        {
            if (after is not null && long.TryParse(after.Value.SortRaw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var ticks))
            {
                var t = new DateTime(ticks, DateTimeKind.Utc);
                q = q.Where(d => d.CreatedAt < t || (d.CreatedAt == t && d.Id.CompareTo(after.Value.FaceId) > 0));
            }
            ordered = q.OrderByDescending(d => d.CreatedAt).ThenBy(d => d.Id);
        }

        var rows = await ordered
            .Take(pageSize + 1)
            .Select(d => new
            {
                d.Id,
                d.BoundingBoxX,
                d.BoundingBoxY,
                d.BoundingBoxWidth,
                d.BoundingBoxHeight,
                d.DetectionScore,
                d.FaceQualityScore,
                d.CreatedAt,
                HasEmbedding = withEmbedding.Any(e => e.FaceDetectionId == d.Id),
                File = _db.FileItems
                    .Where(f => f.BlobObjectId == d.BlobObjectId && f.OwnerUserId == ownerUserId && f.DeletedAt == null && f.MediaLibraryState == MediaLibraryState.Active)
                    .OrderBy(f => f.Id)
                    .Select(f => new { f.Id, f.Name })
                    .FirstOrDefault(),
            })
            .ToListAsync(cancellationToken);

        var hasMore = rows.Count > pageSize;
        var window = rows.Take(pageSize).ToList();

        var items = window
            .Where(r => r.File is not null)
            .Select(r => new UnassignedFaceDto(
                r.Id, r.File!.Id, r.File.Name,
                new FaceBoxDto(
                    Math.Round(r.BoundingBoxX, 6), Math.Round(r.BoundingBoxY, 6),
                    Math.Round(r.BoundingBoxWidth, 6), Math.Round(r.BoundingBoxHeight, 6)),
                r.HasEmbedding,
                r.DetectionScore.HasValue ? Math.Round(r.DetectionScore.Value, 3) : (double?)null))
            .ToList();

        string? nextCursor = null;
        if (hasMore && window.Count > 0)
        {
            var last = window[^1];
            var sortRaw = sortKey switch
            {
                "quality" => (last.FaceQualityScore ?? last.DetectionScore ?? 0).ToString("R", CultureInfo.InvariantCulture),
                "detection" => (last.DetectionScore ?? 0).ToString("R", CultureInfo.InvariantCulture),
                _ => last.CreatedAt.Ticks.ToString(CultureInfo.InvariantCulture),
            };
            nextCursor = EncodeCursor2(sortRaw, last.Id);
        }

        return new UnassignedFacesPage(items, nextCursor, true);
    }

    public async Task<IReadOnlyList<PersonPhotoDto>?> GetPersonPhotosAsync(
        Guid ownerUserId, Guid personId, CancellationToken cancellationToken = default)
    {
        var exists = await _db.People.AnyAsync(
            p => p.Id == personId && p.OwnerUserId == ownerUserId && !p.IsArchived, cancellationToken);
        if (!exists)
        {
            return null;
        }

        var faceIds = await _db.PersonFaceAssignments.AsNoTracking()
            .Where(a => a.OwnerUserId == ownerUserId && a.PersonId == personId)
            .Select(a => a.FaceDetectionId)
            .ToListAsync(cancellationToken);

        var refs = await ResolveFaceRefsAsync(ownerUserId, faceIds, cancellationToken);

        // One photo entry per surfaceable file, carrying the person's face(s).
        return refs.Values
            .GroupBy(r => r.FileItemId)
            .Select(g => new PersonPhotoDto(
                g.Key, g.First().Name,
                g.Select(r => new PersonPhotoFace(r.FaceId, r.Box)).ToList()))
            .OrderBy(p => p.Name)
            .ToList();
    }

    // ---- similar faces (owner-private, threshold, pgvector) --------------

    public async Task<SimilarFacesPage?> FindSimilarFacesAsync(
        Guid ownerUserId, Guid personId, double? minSimilarity, int limit, string? cursor,
        CancellationToken cancellationToken = default)
    {
        var person = await _db.People.AnyAsync(
            p => p.Id == personId && p.OwnerUserId == ownerUserId && !p.IsArchived, cancellationToken);
        if (!person)
        {
            return null;
        }

        var settings = await _settings.GetAsync(cancellationToken);
        var threshold = settings.ClampSearchThreshold(minSimilarity ?? settings.SearchDefaultSimilarityThreshold);
        var pageSize = Math.Clamp(limit, 1, MaxPageSize);

        var profile = await ResolveActiveProfileAsync(cancellationToken);
        if (profile is null)
        {
            return new SimilarFacesPage(false, threshold, Array.Empty<SimilarFaceDto>(), null, false, "no-active-profile");
        }

        // Query vector = the person's representative assigned face embedding.
        var assignedFaceIds = await _db.PersonFaceAssignments.AsNoTracking()
            .Where(a => a.OwnerUserId == ownerUserId && a.PersonId == personId)
            .Select(a => a.FaceDetectionId)
            .ToListAsync(cancellationToken);
        if (assignedFaceIds.Count == 0)
        {
            return new SimilarFacesPage(true, threshold, Array.Empty<SimilarFaceDto>(), null, false, null);
        }

        var source = await _db.FaceEmbeddings.AsNoTracking()
            .Where(e => e.ProfileId == profile.Id
                && e.EmbeddingStatus == AiArtifactStatuses.Completed
                && assignedFaceIds.Contains(e.FaceDetectionId))
            .Select(e => new { e.FaceDetectionId, e.EmbeddingBytes })
            .FirstOrDefaultAsync(cancellationToken);
        if (source is null)
        {
            return new SimilarFacesPage(true, threshold, Array.Empty<SimilarFaceDto>(), null, false, "not-indexed");
        }

        float[] vector;
        try { vector = _serializer.Deserialize(source.EmbeddingBytes); }
        catch { return new SimilarFacesPage(true, threshold, Array.Empty<SimilarFaceDto>(), null, false, "not-indexed"); }

        var neighbors = await _vectors.SearchAsync(
            profile.Id, vector, ownerUserId, source.FaceDetectionId, threshold, SimilarFetchCap, cancellationToken);
        if (neighbors is null)
        {
            // pgvector unavailable → face search not available in this deployment.
            return new SimilarFacesPage(false, threshold, Array.Empty<SimilarFaceDto>(), null, false, "vector-backend-unavailable");
        }

        // Exclude faces already assigned to THIS person; dedupe by face keeping the
        // best score; order (score desc, faceId asc); apply keyset cursor + limit.
        var assignedSet = assignedFaceIds.ToHashSet();
        var ordered = neighbors
            .Where(n => !assignedSet.Contains(n.FaceDetectionId))
            .GroupBy(n => n.FaceDetectionId)
            .Select(g => g.OrderByDescending(x => x.Score).First())
            .OrderByDescending(n => n.Score).ThenBy(n => n.FaceDetectionId)
            .ToList();

        var after = DecodeCursor(cursor);
        if (after is not null)
        {
            ordered = ordered
                .Where(n => n.Score < after.Value.Score
                    || (n.Score == after.Value.Score && n.FaceDetectionId.CompareTo(after.Value.FaceId) > 0))
                .ToList();
        }

        var pageRows = ordered.Take(pageSize + 1).ToList();
        var hasMore = pageRows.Count > pageSize;
        var window = pageRows.Take(pageSize).ToList();

        // Resolve boxes/file refs for the window (owner-visible only).
        var refs = await ResolveFaceRefsAsync(ownerUserId, window.Select(w => w.FaceDetectionId).ToList(), cancellationToken);
        var items = window
            .Where(w => refs.ContainsKey(w.FaceDetectionId))
            .Select(w =>
            {
                var r = refs[w.FaceDetectionId];
                return new SimilarFaceDto(w.FaceDetectionId, r.FileItemId, r.Name, r.Box, Math.Round(w.Score, 4));
            })
            .ToList();

        string? nextCursor = hasMore && window.Count > 0
            ? EncodeCursor(window[^1].Score, window[^1].FaceDetectionId)
            : null;

        return new SimilarFacesPage(true, threshold, items, nextCursor, hasMore, null);
    }

    // ---- face context (for the full-photo viewer) ------------------------

    // Owner-private context for one face: the containing photo (a visible,
    // non-vault FileItem), the selected face box, every other face box on the same
    // photo, and the person the face is assigned to (if any). Null = 404 (missing /
    // cross-owner / vaulted). No storage internals.
    public async Task<FaceContextDto?> GetFaceContextAsync(
        Guid ownerUserId, Guid faceId, CancellationToken cancellationToken = default)
    {
        var face = await _db.FaceDetections.AsNoTracking()
            .Where(d => d.Id == faceId)
            .Select(d => new { d.BlobObjectId, d.ProfileId })
            .FirstOrDefaultAsync(cancellationToken);
        if (face is null)
        {
            return null;
        }

        // A visible, non-vault file for this owner (deterministic representative).
        var file = await _db.FileItems.AsNoTracking()
            .Where(f => f.BlobObjectId == face.BlobObjectId && f.OwnerUserId == ownerUserId && f.DeletedAt == null && f.MediaLibraryState == MediaLibraryState.Active)
            .OrderBy(f => f.Id)
            .Select(f => new { f.Id, f.Name })
            .FirstOrDefaultAsync(cancellationToken);
        if (file is null)
        {
            return null;
        }

        // All faces on the same blob for the same profile (owner sees the photo →
        // sees its faces). Blob-level; owner boundary already enforced above.
        var faces = await _db.FaceDetections.AsNoTracking()
            .Where(d => d.BlobObjectId == face.BlobObjectId && d.ProfileId == face.ProfileId)
            .OrderBy(d => d.FaceIndex)
            .Select(d => new FaceBoxRef(
                d.Id,
                new FaceBoxDto(
                    Math.Round(d.BoundingBoxX, 6), Math.Round(d.BoundingBoxY, 6),
                    Math.Round(d.BoundingBoxWidth, 6), Math.Round(d.BoundingBoxHeight, 6))))
            .ToListAsync(cancellationToken);

        var selected = faces.FirstOrDefault(x => x.FaceId == faceId);
        if (selected is null)
        {
            return null;
        }

        // Person assignment for the selected face (owner-scoped), if any.
        var person = await (
            from a in _db.PersonFaceAssignments.AsNoTracking()
            where a.OwnerUserId == ownerUserId && a.FaceDetectionId == faceId
            join p in _db.People.AsNoTracking() on a.PersonId equals p.Id
            where !p.IsArchived
            select new { p.Id, p.DisplayName }).FirstOrDefaultAsync(cancellationToken);

        return new FaceContextDto(
            file.Id, file.Name, faceId, selected.Box, faces,
            person?.Id, person?.DisplayName);
    }

    // ---- internals -------------------------------------------------------

    private async Task<AiProfile?> ResolveActiveProfileAsync(CancellationToken cancellationToken)
    {
        var key = _options.Value.FaceProfileKey;
        return !string.IsNullOrWhiteSpace(key)
            ? await _registry.GetProfileByKeyAsync(key!, cancellationToken)
            : await _registry.GetDefaultProfileAsync(AiCapabilities.FaceEmbedding, cancellationToken);
    }

    private async Task<string> ResolveNameAsync(
        Guid ownerUserId, string? requested, Guid? personIdToIgnore, CancellationToken cancellationToken)
    {
        var baseName = string.IsNullOrWhiteSpace(requested) ? "Senza nome" : requested!.Trim();
        if (baseName.Length > 200)
        {
            baseName = baseName[..200];
        }

        var siblings = await _db.People.AsNoTracking()
            .Where(p => p.OwnerUserId == ownerUserId && !p.IsArchived && (personIdToIgnore == null || p.Id != personIdToIgnore))
            .Select(p => p.DisplayName)
            .ToListAsync(cancellationToken);
        var taken = siblings.Where(s => s != null).Select(s => s!).ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (!taken.Contains(baseName))
        {
            return baseName;
        }
        for (var i = 2; i < 1000; i++)
        {
            var candidate = $"{baseName} ({i})";
            if (!taken.Contains(candidate))
            {
                return candidate;
            }
        }
        return $"{baseName} ({Guid.NewGuid():N})"[..Math.Min(200, baseName.Length + 34)];
    }

    // Create/replace assignments, honoring the one-person-per-face rule (reassigns
    // a face to the new person if it was on another).
    private async Task AssignFacesAsync(
        Guid ownerUserId, Guid personId, IReadOnlyList<Guid> faceIds, string source, CancellationToken cancellationToken)
    {
        if (faceIds.Count == 0)
        {
            return;
        }

        var existing = await _db.PersonFaceAssignments
            .Where(a => a.OwnerUserId == ownerUserId && faceIds.Contains(a.FaceDetectionId))
            .ToListAsync(cancellationToken);
        var byFace = existing.ToDictionary(a => a.FaceDetectionId);
        var now = _clock.GetUtcNow().UtcDateTime;

        foreach (var faceId in faceIds.Distinct())
        {
            if (byFace.TryGetValue(faceId, out var a))
            {
                if (a.PersonId != personId)
                {
                    a.PersonId = personId;
                    a.Source = source;
                    a.UpdatedAt = now;
                }
            }
            else
            {
                _db.PersonFaceAssignments.Add(new PersonFaceAssignment
                {
                    Id = Guid.NewGuid(),
                    OwnerUserId = ownerUserId,
                    PersonId = personId,
                    FaceDetectionId = faceId,
                    Source = source,
                    CreatedAt = now,
                });
            }
        }
    }

    // Resolve blob-level faces to an owner-visible, non-vault (file id + name +
    // normalized box). Faces with no visible file (e.g. only vaulted references)
    // are omitted → they never surface in People UI.
    private async Task<Dictionary<Guid, FaceRefDto>> ResolveFaceRefsAsync(
        Guid ownerUserId, IReadOnlyList<Guid> faceIds, CancellationToken cancellationToken)
    {
        var result = new Dictionary<Guid, FaceRefDto>();
        if (faceIds.Count == 0)
        {
            return result;
        }

        var rows = await (
            from d in _db.FaceDetections.AsNoTracking()
            where faceIds.Contains(d.Id)
            join f in _db.FileItems.AsNoTracking() on d.BlobObjectId equals f.BlobObjectId
            where f.OwnerUserId == ownerUserId && f.DeletedAt == null && f.MediaLibraryState == MediaLibraryState.Active
            orderby f.Id
            select new
            {
                FaceId = d.Id,
                FileItemId = f.Id,
                f.Name,
                d.BoundingBoxX,
                d.BoundingBoxY,
                d.BoundingBoxWidth,
                d.BoundingBoxHeight,
            }).ToListAsync(cancellationToken);

        foreach (var r in rows)
        {
            if (result.ContainsKey(r.FaceId))
            {
                continue; // first (min file id) wins — deterministic representative file
            }
            result[r.FaceId] = new FaceRefDto(
                r.FaceId, r.FileItemId, r.Name,
                new FaceBoxDto(
                    Math.Round(r.BoundingBoxX, 6), Math.Round(r.BoundingBoxY, 6),
                    Math.Round(r.BoundingBoxWidth, 6), Math.Round(r.BoundingBoxHeight, 6)));
        }

        return result;
    }

    private async Task<FaceRefDto?> FirstSurfaceableMemberRefAsync(
        Guid ownerUserId, Guid clusterId, CancellationToken cancellationToken)
    {
        var memberFaceIds = await _db.FaceClusterMembers.AsNoTracking()
            .Where(m => m.FaceClusterId == clusterId)
            .OrderBy(m => m.FaceDetectionId)
            .Select(m => m.FaceDetectionId)
            .Take(50)
            .ToListAsync(cancellationToken);
        var refs = await ResolveFaceRefsAsync(ownerUserId, memberFaceIds, cancellationToken);
        return memberFaceIds.Select(id => refs.GetValueOrDefault(id)).FirstOrDefault(r => r is not null);
    }

    private async Task<Dictionary<Guid, int>> SurfaceableFaceCountsByPersonAsync(
        Guid ownerUserId, IReadOnlyList<Guid> personIds, CancellationToken cancellationToken)
    {
        var rows = await (
            from a in _db.PersonFaceAssignments.AsNoTracking()
            where a.OwnerUserId == ownerUserId && personIds.Contains(a.PersonId)
            join d in _db.FaceDetections.AsNoTracking() on a.FaceDetectionId equals d.Id
            where _db.FileItems.Any(f => f.BlobObjectId == d.BlobObjectId && f.OwnerUserId == ownerUserId && f.DeletedAt == null && f.MediaLibraryState == MediaLibraryState.Active)
            group a by a.PersonId into g
            select new { PersonId = g.Key, Count = g.Count() }).ToListAsync(cancellationToken);
        return rows.ToDictionary(r => r.PersonId, r => r.Count);
    }

    private async Task<Dictionary<Guid, int>> SurfaceableMemberCountsByClusterAsync(
        Guid ownerUserId, IReadOnlyList<Guid> clusterIds, CancellationToken cancellationToken)
    {
        var rows = await (
            from m in _db.FaceClusterMembers.AsNoTracking()
            where clusterIds.Contains(m.FaceClusterId)
            join d in _db.FaceDetections.AsNoTracking() on m.FaceDetectionId equals d.Id
            where _db.FileItems.Any(f => f.BlobObjectId == d.BlobObjectId && f.OwnerUserId == ownerUserId && f.DeletedAt == null && f.MediaLibraryState == MediaLibraryState.Active)
            // Individually-ignored members don't count — so a group whose faces have
            // all been ignored (incl. via bulk group-ignore) drops out of the queue.
            where !_db.IgnoredFaces.Any(i => i.OwnerUserId == ownerUserId && i.FaceDetectionId == m.FaceDetectionId)
            group m by m.FaceClusterId into g
            select new { ClusterId = g.Key, Count = g.Count() }).ToListAsync(cancellationToken);
        return rows.ToDictionary(r => r.ClusterId, r => r.Count);
    }

    private async Task<Dictionary<Guid, FaceRefDto>> RepresentativeRefByPersonAsync(
        Guid ownerUserId, IReadOnlyList<Guid> personIds, CancellationToken cancellationToken)
    {
        // One surfaceable face per person (deterministic: lowest face id).
        var pairs = await (
            from a in _db.PersonFaceAssignments.AsNoTracking()
            where a.OwnerUserId == ownerUserId && personIds.Contains(a.PersonId)
            join d in _db.FaceDetections.AsNoTracking() on a.FaceDetectionId equals d.Id
            where _db.FileItems.Any(f => f.BlobObjectId == d.BlobObjectId && f.OwnerUserId == ownerUserId && f.DeletedAt == null && f.MediaLibraryState == MediaLibraryState.Active)
            select new { a.PersonId, a.FaceDetectionId }).ToListAsync(cancellationToken);

        var repFacePerPerson = pairs
            .GroupBy(p => p.PersonId)
            .ToDictionary(g => g.Key, g => g.Min(x => x.FaceDetectionId));

        var refs = await ResolveFaceRefsAsync(ownerUserId, repFacePerPerson.Values.Distinct().ToList(), cancellationToken);
        var result = new Dictionary<Guid, FaceRefDto>();
        foreach (var (personId, faceId) in repFacePerPerson)
        {
            if (refs.TryGetValue(faceId, out var r))
            {
                result[personId] = r;
            }
        }
        return result;
    }

    private static string EncodeCursor(double score, Guid faceId)
    {
        var raw = $"{score.ToString("R", CultureInfo.InvariantCulture)}|{faceId:N}";
        return Convert.ToBase64String(Encoding.UTF8.GetBytes(raw));
    }

    private static (double Score, Guid FaceId)? DecodeCursor(string? cursor)
    {
        if (string.IsNullOrWhiteSpace(cursor))
        {
            return null;
        }
        try
        {
            var raw = Encoding.UTF8.GetString(Convert.FromBase64String(cursor));
            var parts = raw.Split('|', 2);
            if (parts.Length == 2
                && double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var score)
                && Guid.TryParse(parts[1], out var faceId))
            {
                return (score, faceId);
            }
        }
        catch
        {
            // fall through
        }
        return null;
    }

    // Opaque keyset cursor for the unassigned-faces view: base64("<sortRaw>|<faceId>").
    // sortRaw is the sort field's value (ticks for recent, R-format double for score
    // sorts); it carries no storage internals.
    private static string EncodeCursor2(string sortRaw, Guid faceId)
        => Convert.ToBase64String(Encoding.UTF8.GetBytes($"{sortRaw}|{faceId:N}"));

    private static (string SortRaw, Guid FaceId)? DecodeCursor2(string? cursor)
    {
        if (string.IsNullOrWhiteSpace(cursor))
        {
            return null;
        }
        try
        {
            var raw = Encoding.UTF8.GetString(Convert.FromBase64String(cursor));
            var parts = raw.Split('|', 2);
            if (parts.Length == 2 && Guid.TryParse(parts[1], out var faceId))
            {
                return (parts[0], faceId);
            }
        }
        catch
        {
            // fall through
        }
        return null;
    }
}

// ---- sanitized owner-private DTOs ---------------------------------------

public sealed record FaceBoxDto(double X, double Y, double Width, double Height);

public sealed record FaceRefDto(Guid FaceId, Guid FileItemId, string Name, FaceBoxDto Box);

public sealed record PersonDto(Guid PersonId, string? Name, int FaceCount, FaceRefDto? Representative);

public sealed record SuggestedGroupDto(
    Guid GroupId, FaceRefDto? Representative, int FaceCount, double? Confidence, string Status);

public sealed record PersonPhotoFace(Guid FaceId, FaceBoxDto Box);

public sealed record PersonPhotoDto(Guid FileItemId, string Name, IReadOnlyList<PersonPhotoFace> Faces);

public sealed record SimilarFaceDto(Guid FaceId, Guid FileItemId, string Name, FaceBoxDto Box, double Score);

public sealed record UnassignedFaceDto(
    Guid FaceId, Guid FileItemId, string Name, FaceBoxDto Box, bool HasEmbedding, double? DetectionScore);

public sealed record UnassignedFacesPage(
    IReadOnlyList<UnassignedFaceDto> Items, string? NextCursor, bool ProfileAvailable);

public sealed record IgnoredFaceDto(Guid FaceId, Guid FileItemId, string Name, FaceBoxDto Box);

public sealed record IgnoredFacesPage(IReadOnlyList<IgnoredFaceDto> Items, string? NextCursor);

public sealed record ClusterAssignSummaryDto(
    int AssignedCount,
    int SkippedAlreadyAssignedCount,
    int SkippedIgnoredCount,
    int SkippedIneligibleCount,
    string ClusterStatus);

public sealed record SimilarFacesPage(
    bool ProfileAvailable,
    double Threshold,
    IReadOnlyList<SimilarFaceDto> Items,
    string? NextCursor,
    bool HasMore,
    string? UnavailableReason);

public sealed record FaceBoxRef(Guid FaceId, FaceBoxDto Box);

// Sanitized full-photo context for the face viewer. FileItemId lets the client
// load the existing owner-safe medium preview (/api/files/{id}/preview); it never
// carries a blob id, SHA, path, or vector.
public sealed record FaceContextDto(
    Guid FileItemId,
    string FileName,
    Guid SelectedFaceId,
    FaceBoxDto SelectedBox,
    IReadOnlyList<FaceBoxRef> Faces,
    Guid? PersonId,
    string? PersonName);
