using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NubArca.Api.Data;
using NubArca.Api.Domain.Ai;

namespace NubArca.Api.Ai.Faces.Video;

// VFACE-02: the owner-private surface over canonical video face tracks.
//
// Two responsibilities, both strictly owner-scoped:
//   1. DECISIONS — assign a track to one of the caller's people, ignore it, or
//      clear the decision. Explicit user intent is authoritative and is never
//      overwritten by anything automated (nothing automated writes here at all).
//   2. READS — the review queue of undecided tracks, the person's video results
//      with their temporal intervals, and co-presence between two people.
//
// Every entry point resolves visibility through VideoFaceTrackVisibility BEFORE
// touching a track, so a foreign, deleted or vault-only track is indistinguishable
// from a missing one (generic 404 upstream). Canonical track evidence is never
// modified: this service only ever writes VideoFaceTrackPersonDecision rows.
//
// DTOs carry logical FileItem ids, names, millisecond intervals and person names
// only — never a track id outside the authenticated review surface, and never a
// BlobObjectId, embedding, profile id, storage key or path.
//
// VFACE-02C — TWO DELIBERATE NON-DEPENDENCIES:
//   * VideoFaceAnalysisOptions is NOT injected. Every answer here is a function
//     of persisted evidence alone, so retuning sampling can never change what a
//     historical query returns.
//   * Ai:VideoFaceAnalysis:Enabled is NOT consulted. That flag governs
//     GENERATION (post-segmentation scheduling and backfill execution); it is not
//     a kill switch for People data. Turning it off stops new analysis and leaves
//     every already-persisted track, decision, video result and co-presence
//     answer fully readable and still decidable.
public sealed class VideoFaceTrackPeopleService
{
    public const int DefaultPageSize = 50;
    public const int MaxPageSize = 100;

    // Further intervals returned per video besides the best one. Mirrors the
    // VSEM-03 bound so the player handoff behaves identically for both evidence
    // kinds.
    public const int MaxAdditionalMatches = 4;

    private readonly AppDbContext _db;
    private readonly TimeProvider _clock;
    private readonly ILogger<VideoFaceTrackPeopleService> _logger;

    public VideoFaceTrackPeopleService(
        AppDbContext db,
        TimeProvider clock,
        ILogger<VideoFaceTrackPeopleService> logger)
    {
        _db = db;
        _clock = clock;
        _logger = logger;
    }

    // ---- decisions ---------------------------------------------------------

    // Assign a visible track to one of the caller's own people. Replaces any
    // previous decision on that track, including an ignore. Returns false as a
    // generic 404 for an invisible track or a person that is not the caller's.
    public async Task<bool> AssignAsync(
        Guid ownerUserId, Guid trackId, Guid personId, CancellationToken cancellationToken = default)
    {
        var started = Stopwatch.GetTimestamp();
        if (!await IsVisibleAsync(ownerUserId, trackId, cancellationToken))
        {
            return false;
        }

        // The person must belong to the caller and still be active. The composite
        // foreign key would refuse a cross-owner id anyway; this check turns that
        // into a clean 404 instead of a database error.
        var personExists = await _db.People.AsNoTracking().AnyAsync(
            p => p.Id == personId && p.OwnerUserId == ownerUserId && !p.IsArchived, cancellationToken);
        if (!personExists)
        {
            return false;
        }

        var now = _clock.GetUtcNow().UtcDateTime;
        var decision = await LoadDecisionAsync(ownerUserId, trackId, cancellationToken);
        if (decision is null)
        {
            _db.VideoFaceTrackPersonDecisions.Add(new VideoFaceTrackPersonDecision
            {
                Id = Guid.NewGuid(),
                OwnerUserId = ownerUserId,
                VideoFaceTrackId = trackId,
                PersonId = personId,
                Decision = VideoFaceTrackDecisions.Assigned,
                Source = VideoFaceTrackDecisionSources.User,
                CreatedAt = now,
                ConfirmedAt = now,
            });
        }
        else
        {
            // Re-confirming the SAME person is idempotent: the original
            // confirmation timestamp is preserved as audit.
            var samePerson = decision.Decision == VideoFaceTrackDecisions.Assigned
                && decision.PersonId == personId;
            decision.PersonId = personId;
            decision.Decision = VideoFaceTrackDecisions.Assigned;
            decision.Source = VideoFaceTrackDecisionSources.User;
            decision.UpdatedAt = now;
            decision.ConfirmedAt = samePerson ? decision.ConfirmedAt ?? now : now;
        }

        await _db.SaveChangesAsync(cancellationToken);
        Log("assign", ownerUserId, VideoFaceTrackDecisions.Assigned, started);
        return true;
    }

    // Dismiss a visible track. Replaces any existing assignment — ignore wins,
    // exactly as IgnoreFaceAsync does for static faces. The canonical track is
    // never deleted.
    public async Task<bool> IgnoreAsync(
        Guid ownerUserId, Guid trackId, CancellationToken cancellationToken = default)
    {
        var started = Stopwatch.GetTimestamp();
        if (!await IsVisibleAsync(ownerUserId, trackId, cancellationToken))
        {
            return false;
        }

        var now = _clock.GetUtcNow().UtcDateTime;
        var decision = await LoadDecisionAsync(ownerUserId, trackId, cancellationToken);
        if (decision is null)
        {
            _db.VideoFaceTrackPersonDecisions.Add(new VideoFaceTrackPersonDecision
            {
                Id = Guid.NewGuid(),
                OwnerUserId = ownerUserId,
                VideoFaceTrackId = trackId,
                PersonId = null,
                Decision = VideoFaceTrackDecisions.Ignored,
                Source = VideoFaceTrackDecisionSources.User,
                CreatedAt = now,
            });
        }
        else
        {
            decision.PersonId = null;
            decision.Decision = VideoFaceTrackDecisions.Ignored;
            decision.Source = VideoFaceTrackDecisionSources.User;
            decision.UpdatedAt = now;
            decision.ConfirmedAt = null;
        }

        await _db.SaveChangesAsync(cancellationToken);
        Log("ignore", ownerUserId, VideoFaceTrackDecisions.Ignored, started);
        return true;
    }

    // Return a track to UNDECIDED. Nothing is reassigned automatically — the
    // track simply re-enters the review queue.
    public async Task<bool> ClearAsync(
        Guid ownerUserId, Guid trackId, CancellationToken cancellationToken = default)
    {
        var started = Stopwatch.GetTimestamp();
        if (!await IsVisibleAsync(ownerUserId, trackId, cancellationToken))
        {
            return false;
        }

        var decision = await LoadDecisionAsync(ownerUserId, trackId, cancellationToken);
        if (decision is null)
        {
            // Already undecided: idempotent success, not a 404.
            return true;
        }

        _db.VideoFaceTrackPersonDecisions.Remove(decision);
        await _db.SaveChangesAsync(cancellationToken);
        Log("clear", ownerUserId, "undecided", started);
        return true;
    }

    // ---- review queue ------------------------------------------------------

    // Visible tracks this owner has said nothing about, best evidence first
    // (longest, then highest quality), so the queue starts with the tracks most
    // worth naming. Keyset-free simple paging by offset is deliberate: the queue
    // shrinks as the owner works through it.
    public async Task<VideoFaceTrackReviewPage> ListUndecidedAsync(
        Guid ownerUserId, int? limit, CancellationToken cancellationToken = default)
    {
        var take = Math.Clamp(limit ?? DefaultPageSize, 1, MaxPageSize);

        var rows = await (
            from track in VideoFaceTrackVisibility.VisibleTracks(_db, ownerUserId)
            where !_db.VideoFaceTrackPersonDecisions.Any(
                d => d.OwnerUserId == ownerUserId && d.VideoFaceTrackId == track.Id)
            orderby (track.EndMilliseconds - track.StartMilliseconds) descending,
                track.QualityScore descending, track.Id
            select new
            {
                track.Id,
                track.StartMilliseconds,
                track.EndMilliseconds,
                track.RepresentativeTimestampMilliseconds,
                track.DetectionCount,
                track.QualityScore,
            })
            .Take(take + 1)
            .ToListAsync(cancellationToken);

        var hasMore = rows.Count > take;
        var window = rows.Take(take).ToList();
        var files = await ResolveRepresentativeFilesAsync(
            ownerUserId, window.Select(r => r.Id).ToList(), cancellationToken);

        var items = window
            .Where(r => files.ContainsKey(r.Id))
            .Select(r => new VideoFaceTrackReviewDto(
                r.Id,
                files[r.Id].FileItemId,
                files[r.Id].Name,
                r.StartMilliseconds,
                r.EndMilliseconds,
                r.RepresentativeTimestampMilliseconds,
                r.DetectionCount,
                Math.Round(r.QualityScore, 4)))
            .ToList();

        return new VideoFaceTrackReviewPage(items, hasMore);
    }

    // ---- person media ------------------------------------------------------

    // Videos in which this person has at least one CONFIRMED track, with the
    // temporal evidence the player needs. Undecided and ignored tracks never
    // appear. Null = the person is not the caller's (generic 404 upstream).
    public async Task<IReadOnlyList<PersonVideoDto>?> GetPersonVideosAsync(
        Guid ownerUserId, Guid personId, CancellationToken cancellationToken = default)
    {
        var started = Stopwatch.GetTimestamp();
        var personExists = await _db.People.AsNoTracking().AnyAsync(
            p => p.Id == personId && p.OwnerUserId == ownerUserId && !p.IsArchived, cancellationToken);
        if (!personExists)
        {
            return null;
        }

        var tracks = await LoadConfirmedTracksAsync(
            ownerUserId, new[] { personId }, cancellationToken);

        var results = await BuildVideoResultsAsync(ownerUserId, tracks, cancellationToken);

        _logger.LogInformation(
            "video-people: operation={Operation} owner={OwnerUserId} results={ResultCount} "
            + "tracks={TrackCount} elapsed-ms={ElapsedMs}",
            "video.people.person-videos", ownerUserId, results.Count, tracks.Count,
            (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        return results;
    }

    // ---- co-presence -------------------------------------------------------

    // Videos where BOTH people have a confirmed track and those tracks actually
    // OVERLAP IN TIME within the same canonical analysis. Two people who merely
    // appear somewhere in the same long video are not co-present.
    //
    // Intervals are STRICT HALF-OPEN [Start, End): they overlap iff
    //   A.Start < B.End && B.Start < A.End
    // so adjacent intervals ([0,1000) and [1000,2000)) are NOT co-present while a
    // one-millisecond genuine overlap is.
    //
    // VFACE-02C: no tolerance derived from runtime sampling configuration. An
    // earlier version widened each interval by one FrameIntervalMilliseconds,
    // which made a HISTORICAL query answer change whenever an operator retuned
    // sampling — even though the persisted tracks were byte-identical. Co-presence
    // is a question about stored evidence, so it must depend only on stored
    // evidence. That is why this service no longer reads VideoFaceAnalysisOptions
    // at all: the dependency is now structurally impossible, not merely unused.
    //
    // Null = either person is not the caller's; the same person twice is
    // rejected rather than trivially "co-present with themselves".
    public async Task<IReadOnlyList<PersonVideoDto>?> GetCoPresentVideosAsync(
        Guid ownerUserId, Guid personId, Guid otherPersonId,
        CancellationToken cancellationToken = default)
    {
        var started = Stopwatch.GetTimestamp();
        if (personId == otherPersonId)
        {
            return null;
        }

        var owned = await _db.People.AsNoTracking()
            .Where(p => p.OwnerUserId == ownerUserId && !p.IsArchived
                && (p.Id == personId || p.Id == otherPersonId))
            .Select(p => p.Id)
            .ToListAsync(cancellationToken);
        if (owned.Count != 2)
        {
            return null;
        }

        var confirmed = await LoadConfirmedTracksAsync(
            ownerUserId, new[] { personId, otherPersonId }, cancellationToken);

        var overlapping = new List<ConfirmedTrackRow>();

        // Co-presence is defined WITHIN one canonical analysis. Grouping by
        // AnalysisId is what enforces that: a VideoFaceAnalysisStatus row is
        // unique per (manifest, analysis version, detection profile, embedding
        // profile), so two tracks can only be compared when they describe the
        // same blob, the same manifest, the same version AND the same profile
        // pair. Cross-owner comparison is already impossible — the confirmed set
        // was loaded owner-scoped.
        foreach (var group in confirmed.GroupBy(t => t.AnalysisId))
        {
            var mine = group.Where(t => t.PersonId == personId).ToList();
            var theirs = group.Where(t => t.PersonId == otherPersonId).ToList();
            foreach (var a in mine)
            {
                if (theirs.Any(b => Overlaps(a, b)))
                {
                    overlapping.Add(a);
                }
            }
        }

        var results = await BuildVideoResultsAsync(ownerUserId, overlapping, cancellationToken);

        _logger.LogInformation(
            "video-people: operation={Operation} owner={OwnerUserId} results={ResultCount} "
            + "elapsed-ms={ElapsedMs}",
            "video.people.co-presence", ownerUserId, results.Count,
            (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        return results;
    }

    // STRICT half-open overlap of [Start, End) intervals. Configuration-free and
    // therefore stable for the whole life of the persisted evidence.
    internal static bool Overlaps(ConfirmedTrackRow a, ConfirmedTrackRow b)
        => a.StartMilliseconds < b.EndMilliseconds
            && b.StartMilliseconds < a.EndMilliseconds;

    // ---- internals ---------------------------------------------------------

    private Task<bool> IsVisibleAsync(Guid ownerUserId, Guid trackId, CancellationToken cancellationToken)
        => VideoFaceTrackVisibility.VisibleTracks(_db, ownerUserId)
            .AnyAsync(t => t.Id == trackId, cancellationToken);

    private Task<VideoFaceTrackPersonDecision?> LoadDecisionAsync(
        Guid ownerUserId, Guid trackId, CancellationToken cancellationToken)
        => _db.VideoFaceTrackPersonDecisions.FirstOrDefaultAsync(
            d => d.OwnerUserId == ownerUserId && d.VideoFaceTrackId == trackId, cancellationToken);

    // Confirmed (assigned) tracks of the given people that this owner can still
    // see, with the temporal interval each one contributes.
    //
    // Deliberately TWO simple round trips instead of one deep composition: the
    // decision table is the cheap, highly selective starting point, and resolving
    // its tracks by id keeps both statements flat enough to translate on every
    // provider. Visibility is still applied in the database, never in memory.
    private async Task<List<ConfirmedTrackRow>> LoadConfirmedTracksAsync(
        Guid ownerUserId, IReadOnlyList<Guid> personIds, CancellationToken cancellationToken)
    {
        var decisions = await _db.VideoFaceTrackPersonDecisions.AsNoTracking()
            .Where(d => d.OwnerUserId == ownerUserId
                && d.Decision == VideoFaceTrackDecisions.Assigned
                && d.PersonId != null
                && personIds.Contains(d.PersonId!.Value))
            .Select(d => new { d.VideoFaceTrackId, d.PersonId })
            .ToListAsync(cancellationToken);
        if (decisions.Count == 0)
        {
            return new List<ConfirmedTrackRow>();
        }

        var trackIds = decisions.Select(d => d.VideoFaceTrackId).Distinct().ToList();
        var visible = await (
            from track in _db.VideoFaceTracks.AsNoTracking()
            where trackIds.Contains(track.Id)
            join analysis in _db.VideoFaceAnalysisStatuses.AsNoTracking()
                on track.VideoFaceAnalysisStatusId equals analysis.Id
            join index in _db.VideoSemanticIndexes.AsNoTracking()
                on analysis.VideoSemanticIndexId equals index.Id
            where _db.FileItems.Any(file =>
                file.BlobObjectId == index.BlobObjectId
                && file.OwnerUserId == ownerUserId
                && file.DeletedAt == null
                && file.MediaLibraryState == Domain.MediaLibraryState.Active)
            select new
            {
                TrackId = track.Id,
                AnalysisId = analysis.Id,
                track.StartMilliseconds,
                track.EndMilliseconds,
                track.RepresentativeTimestampMilliseconds,
            })
            .ToListAsync(cancellationToken);

        var byTrack = visible.ToDictionary(v => v.TrackId);
        return decisions
            .Where(d => byTrack.ContainsKey(d.VideoFaceTrackId))
            .Select(d =>
            {
                var v = byTrack[d.VideoFaceTrackId];
                return new ConfirmedTrackRow(
                    d.PersonId, v.TrackId, v.AnalysisId,
                    v.StartMilliseconds, v.EndMilliseconds, v.RepresentativeTimestampMilliseconds);
            })
            .ToList();
    }

    // Turns confirmed track intervals into owner-visible media results. One
    // result per logical FileItem, exactly as the gallery presents duplicates —
    // two visible files on the same blob both appear, and both reuse the SAME
    // canonical intervals rather than duplicating evidence.
    private async Task<IReadOnlyList<PersonVideoDto>> BuildVideoResultsAsync(
        Guid ownerUserId, IReadOnlyList<ConfirmedTrackRow> intervals,
        CancellationToken cancellationToken)
    {
        if (intervals.Count == 0)
        {
            return Array.Empty<PersonVideoDto>();
        }

        var trackIds = intervals.Select(i => i.TrackId).Distinct().ToList();
        var files = await VideoFaceTrackVisibility
            .VisibleFilesForTracks(_db, ownerUserId, trackIds)
            .Select(f => new { f.Id, f.Name, f.BlobObjectId })
            .ToListAsync(cancellationToken);
        if (files.Count == 0)
        {
            return Array.Empty<PersonVideoDto>();
        }

        // Which blob does each track belong to? Needed to attach intervals to the
        // right files.
        var blobByTrack = await (
            from track in _db.VideoFaceTracks.AsNoTracking()
            where trackIds.Contains(track.Id)
            join analysis in _db.VideoFaceAnalysisStatuses.AsNoTracking()
                on track.VideoFaceAnalysisStatusId equals analysis.Id
            join index in _db.VideoSemanticIndexes.AsNoTracking()
                on analysis.VideoSemanticIndexId equals index.Id
            select new { TrackId = track.Id, index.BlobObjectId })
            .ToDictionaryAsync(r => r.TrackId, r => r.BlobObjectId, cancellationToken);

        var intervalsByBlob = new Dictionary<Guid, List<ConfirmedTrackRow>>();
        foreach (var interval in intervals)
        {
            if (!blobByTrack.TryGetValue(interval.TrackId, out var blobId))
            {
                continue;
            }

            if (!intervalsByBlob.TryGetValue(blobId, out var list))
            {
                list = new List<ConfirmedTrackRow>();
                intervalsByBlob[blobId] = list;
            }

            list.Add(interval);
        }

        var results = new List<PersonVideoDto>(files.Count);
        foreach (var file in files)
        {
            if (!intervalsByBlob.TryGetValue(file.BlobObjectId, out var forFile) || forFile.Count == 0)
            {
                continue;
            }

            // Best = longest evidence, deterministic on ties. The rest are
            // capped and ordered chronologically so the UI reads as a timeline.
            var ordered = forFile
                .DistinctBy(i => i.TrackId)
                .OrderByDescending(i => i.EndMilliseconds - i.StartMilliseconds)
                .ThenBy(i => i.StartMilliseconds)
                .ThenBy(i => i.TrackId)
                .ToList();

            var best = ordered[0];
            var additional = ordered.Skip(1)
                .OrderBy(i => i.StartMilliseconds)
                .ThenBy(i => i.TrackId)
                .Take(MaxAdditionalMatches)
                .Select(Match)
                .ToList();

            results.Add(new PersonVideoDto(file.Id, file.Name, Match(best), additional));
        }

        return results
            .OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(r => r.FileItemId)
            .ToList();
    }

    private static PersonVideoMatchDto Match(ConfirmedTrackRow interval)
        => new(
            PersonVideoMatchDto.Person,
            interval.StartMilliseconds,
            interval.EndMilliseconds,
            interval.RepresentativeMilliseconds);

    // One owner-visible representative FileItem per track (deterministic: the
    // lowest file id), for the review queue. Tracks whose only references are
    // vaulted or deleted resolve to nothing and never surface.
    private async Task<Dictionary<Guid, (Guid FileItemId, string Name)>> ResolveRepresentativeFilesAsync(
        Guid ownerUserId, IReadOnlyList<Guid> trackIds, CancellationToken cancellationToken)
    {
        var result = new Dictionary<Guid, (Guid, string)>();
        if (trackIds.Count == 0)
        {
            return result;
        }

        var rows = await (
            from track in _db.VideoFaceTracks.AsNoTracking()
            where trackIds.Contains(track.Id)
            join analysis in _db.VideoFaceAnalysisStatuses.AsNoTracking()
                on track.VideoFaceAnalysisStatusId equals analysis.Id
            join index in _db.VideoSemanticIndexes.AsNoTracking()
                on analysis.VideoSemanticIndexId equals index.Id
            join file in _db.FileItems.AsNoTracking()
                on index.BlobObjectId equals file.BlobObjectId
            where file.OwnerUserId == ownerUserId
                && file.DeletedAt == null
                && file.MediaLibraryState == Domain.MediaLibraryState.Active
            orderby file.Id
            select new { TrackId = track.Id, FileItemId = file.Id, file.Name })
            .ToListAsync(cancellationToken);

        foreach (var row in rows)
        {
            if (!result.ContainsKey(row.TrackId))
            {
                result[row.TrackId] = (row.FileItemId, row.Name);
            }
        }

        return result;
    }

    // Operation, owner id (the identifier every other owner-scoped People log
    // already uses), decision kind and duration. NEVER a person name, a track or
    // blob id, a filename, a path, a storage key or an embedding.
    private void Log(string operation, Guid ownerUserId, string decision, long startedTimestamp)
        => _logger.LogInformation(
            "video-people: operation={Operation} owner={OwnerUserId} decision={Decision} "
            + "elapsed-ms={ElapsedMs}",
            "video.people." + operation, ownerUserId, decision,
            (long)Stopwatch.GetElapsedTime(startedTimestamp).TotalMilliseconds);

}

// A confirmed track's temporal contribution to one person. `AnalysisId` scopes
// co-presence to one canonical analysis of one video. Flat and base-class-free so
// the projection translates to SQL.
public sealed record ConfirmedTrackRow(
    Guid? PersonId,
    Guid TrackId,
    Guid AnalysisId,
    long StartMilliseconds,
    long EndMilliseconds,
    long RepresentativeMilliseconds);

// ---- sanitized owner-private DTOs ---------------------------------------

// One temporal match inside one video result. Shaped like VSEM-03's
// SemanticBestMatch so the frontend player handoff is identical; the evidence
// kind is what differs.
public sealed record PersonVideoMatchDto(
    string EvidenceType,
    long StartMilliseconds,
    long EndMilliseconds,
    long RepresentativeMilliseconds)
{
    public const string Person = "person";
}

// One owner-visible video in which the person is confirmed. Carries the logical
// file id (which the existing player and preview endpoints already accept) plus
// bounded temporal evidence — never a track id, blob id, profile id or path.
public sealed record PersonVideoDto(
    Guid FileItemId,
    string Name,
    PersonVideoMatchDto BestMatch,
    IReadOnlyList<PersonVideoMatchDto> AdditionalMatches);

// One undecided track awaiting review. This IS the authenticated review surface,
// so it is the only DTO that carries a TrackId — the client needs it to post a
// decision back. Nothing else internal is exposed.
public sealed record VideoFaceTrackReviewDto(
    Guid TrackId,
    Guid FileItemId,
    string Name,
    long StartMilliseconds,
    long EndMilliseconds,
    long RepresentativeMilliseconds,
    int DetectionCount,
    double QualityScore);

public sealed record VideoFaceTrackReviewPage(
    IReadOnlyList<VideoFaceTrackReviewDto> Items, bool HasMore);
