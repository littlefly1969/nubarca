using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using NubArca.Api.MediaLibrary;

namespace NubArca.Api.Files;

// Slice 61: compact gallery filter set. All fields are nullable; null means
// "no constraint on this dimension". Owner-scoping is enforced by the outer
// query and is not represented here.
//
// Fingerprint() returns a short hex hash of the canonical filter shape so a
// cursor can be bound to the (q + filters + folder) set it was issued under;
// any change → different fingerprint → request 400s instead of returning
// silently-stale boundary rows. Two requests with equivalent inputs produce
// the same fingerprint; the canonical form normalises by:
//   * lower-casing q, owner-supplied,
//   * round-tripping dates through ISO-8601 UTC ("o"),
//   * stable ordering across all fields.
public sealed record ImageFilters
{
    public string? Query { get; init; }
    public bool? Favorite { get; init; }
    public int? MinRating { get; init; }
    public bool? HasGps { get; init; }
    public DateTime? DateTakenFrom { get; init; }
    public DateTime? DateTakenTo { get; init; }
    public Guid? FolderId { get; init; }

    // Slice 75: when true, duplicate FileItems (same owner + same blob) are
    // collapsed so only the canonical representative appears, annotated with
    // OccurrenceCount. The representative is the oldest active FileItem
    // pointing to that blob for this owner (CreatedAt asc, Id asc tiebreaker).
    // Included in the cursor fingerprint so a cursor issued with collapse=true
    // is rejected when replayed with collapse=false and vice-versa.
    public bool CollapseDuplicates { get; init; } = false;

    // Slice 3 (media organization): which media-library scope this gallery view
    // is. Active is the normal gallery (default); Excluded is the "Esclusi" tab.
    // Part of the fingerprint so an Active cursor can never replay in the
    // Excluded tab (and vice-versa). Never surfaced to the file browser.
    public MediaLibraryScope Scope { get; init; } = MediaLibraryScope.Active;

    // Video-metadata filters (video gallery only; null = no constraint). Applied
    // by BuildGalleryQuery against the blob's ffprobe-derived BlobMetadata. On
    // the image gallery these are always null. Part of the cursor fingerprint.
    public double? DurationMinSeconds { get; init; }
    public double? DurationMaxSeconds { get; init; }
    public int? MinWidth { get; init; }
    public int? MinHeight { get; init; }
    public string? VideoCodec { get; init; }
    public bool? HasAudio { get; init; }

    // Gallery-as-operational-surface filters. All owner-scoped; person ids are
    // safe owner-private identifiers (a foreign person id simply matches nothing
    // because PersonFaceAssignment is owner-filtered — no existence leak).
    //
    // AlbumId constrains membership to the given album (already owner-validated
    // by the endpoint). IncludePersonIds/ExcludePersonIds are the People filter
    // (see IncludePeopleMode). SimilarToFileId is only carried for the cursor
    // fingerprint — the actual restrict-set is resolved server-side into
    // RestrictToFileIds (kept out of the fingerprint so it can be re-resolved).
    public Guid? AlbumId { get; init; }

    // Album MEMBERSHIP (distinct from AlbumId, which means "in THIS album"):
    // Assigned = in at least one album, Unassigned = in none, Any = no
    // constraint. Owner-safe because the FileItems it filters are already
    // owner-scoped — an AlbumItem row can only reference this owner's files.
    // Shared verbatim by the photo and the video gallery.
    public AlbumMembershipFilter AlbumMembership { get; init; } = AlbumMembershipFilter.Any;
    public IReadOnlyList<Guid>? IncludePersonIds { get; init; }
    public IReadOnlyList<Guid>? ExcludePersonIds { get; init; }
    public PeopleFilterMode IncludePeopleMode { get; init; } = PeopleFilterMode.All;
    public Guid? SimilarToFileId { get; init; }
    // Resolved candidate set (e.g. from photo-similarity). Applied as a
    // membership restriction; NOT part of the fingerprint. An empty (non-null)
    // list means "restrict to nothing" → no matches.
    public IReadOnlyList<Guid>? RestrictToFileIds { get; init; }

    // Slice 100 (TV natural-language search): optional visual semantic residual.
    // When set, the gallery becomes physical-filter-first + semantic-ranked: the
    // physical filters above build the owner-scoped candidate set, then the
    // active multimodal text tower ranks ONLY inside that set and reduces it to
    // SemanticTopK. When null the gallery behaves exactly as before (no ranking,
    // normal filtered total). SemanticQuery is a VISUAL description, never SQL/
    // ids; it is part of the fingerprint so any change invalidates the cursor.
    // SemanticTopK is the server-clamped reduction size (see
    // AiNaturalGallerySearchOptions). It is fingerprinted so a Top-K change
    // invalidates the cursor.
    public string? SemanticQuery { get; init; }
    public int? SemanticTopK { get; init; }

    public bool HasSemanticQuery => !string.IsNullOrWhiteSpace(SemanticQuery);

    private bool HasIncludePeople => IncludePersonIds is { Count: > 0 };
    private bool HasExcludePeople => ExcludePersonIds is { Count: > 0 };

    public bool IsEmpty =>
        string.IsNullOrEmpty(Query)
        && Scope == MediaLibraryScope.Active
        && Favorite is null
        && MinRating is null
        && HasGps is null
        && DateTakenFrom is null
        && DateTakenTo is null
        && FolderId is null
        && !CollapseDuplicates
        && AlbumId is null
        && AlbumMembership == AlbumMembershipFilter.Any
        && !HasIncludePeople
        && !HasExcludePeople
        && SimilarToFileId is null
        && !HasSemanticQuery
        && DurationMinSeconds is null
        && DurationMaxSeconds is null
        && MinWidth is null
        && MinHeight is null
        && string.IsNullOrEmpty(VideoCodec)
        && HasAudio is null;

    // The physical-only projection of these filters (semantic residual removed).
    // Used to build the candidate set that semantic ranking runs INSIDE, so the
    // physical filters are always applied BEFORE ranking.
    public ImageFilters WithoutSemantic() =>
        HasSemanticQuery || SemanticTopK is not null
            ? this with { SemanticQuery = null, SemanticTopK = null }
            : this;

    public string? Fingerprint()
    {
        if (IsEmpty) return null;

        var sb = new StringBuilder();
        sb.Append("q="); sb.Append(Query?.Trim().ToLowerInvariant() ?? "");
        sb.Append("|scope="); sb.Append(Scope switch
        {
            MediaLibraryScope.Excluded => "excluded",
            MediaLibraryScope.All => "all",
            _ => "active",
        });
        sb.Append("|fav="); sb.Append(Favorite?.ToString().ToLowerInvariant() ?? "");
        sb.Append("|r="); sb.Append(MinRating?.ToString(CultureInfo.InvariantCulture) ?? "");
        sb.Append("|gps="); sb.Append(HasGps?.ToString().ToLowerInvariant() ?? "");
        sb.Append("|from="); sb.Append(DateTakenFrom?.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture) ?? "");
        sb.Append("|to="); sb.Append(DateTakenTo?.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture) ?? "");
        sb.Append("|fld="); sb.Append(FolderId?.ToString("D") ?? "");
        sb.Append("|dup="); sb.Append(CollapseDuplicates ? "1" : "0");
        sb.Append("|alb="); sb.Append(AlbumId?.ToString("D") ?? "");
        sb.Append("|albm="); sb.Append(AlbumMembership switch
        {
            AlbumMembershipFilter.Assigned => "assigned",
            AlbumMembershipFilter.Unassigned => "unassigned",
            _ => "any",
        });
        sb.Append("|ip="); sb.Append(CanonicalIds(IncludePersonIds));
        sb.Append("|xp="); sb.Append(CanonicalIds(ExcludePersonIds));
        sb.Append("|pm="); sb.Append(IncludePeopleMode == PeopleFilterMode.Any ? "any" : "all");
        sb.Append("|sim="); sb.Append(SimilarToFileId?.ToString("D") ?? "");
        // Semantic residual + Top-K are part of the query identity: changing the
        // semantic description or the reduction size must invalidate the cursor.
        // (The active embedding-profile key + ordering version are additionally
        // folded in by the semantic query service, which resolves the profile.)
        sb.Append("|sq="); sb.Append(SemanticQuery?.Trim().ToLowerInvariant() ?? "");
        sb.Append("|sk="); sb.Append(SemanticTopK?.ToString(CultureInfo.InvariantCulture) ?? "");
        // Video-metadata filters.
        sb.Append("|dmin="); sb.Append(DurationMinSeconds?.ToString(CultureInfo.InvariantCulture) ?? "");
        sb.Append("|dmax="); sb.Append(DurationMaxSeconds?.ToString(CultureInfo.InvariantCulture) ?? "");
        sb.Append("|mw="); sb.Append(MinWidth?.ToString(CultureInfo.InvariantCulture) ?? "");
        sb.Append("|mh="); sb.Append(MinHeight?.ToString(CultureInfo.InvariantCulture) ?? "");
        sb.Append("|vc="); sb.Append(VideoCodec?.Trim().ToLowerInvariant() ?? "");
        sb.Append("|ha="); sb.Append(HasAudio?.ToString().ToLowerInvariant() ?? "");

        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString()));
        // 64-bit truncated hex is plenty for an opaque mismatch check — the
        // value is never used for cryptography, only string equality.
        return Convert.ToHexString(bytes, 0, 8).ToLowerInvariant();
    }

    // Stable, order-independent rendering of a person-id set for the fingerprint
    // (the UI order must not change the fingerprint).
    private static string CanonicalIds(IReadOnlyList<Guid>? ids)
    {
        if (ids is null || ids.Count == 0) return "";
        return string.Join(",", ids.Select(g => g.ToString("D")).Distinct().OrderBy(s => s, StringComparer.Ordinal));
    }
}

// Album-membership filter, shared by the photo and video galleries.
//   Any        → no constraint.
//   Assigned   → at least one AlbumItem row exists for the FileItem.
//   Unassigned → no AlbumItem row exists for the FileItem.
// NOT the same as ImageFilters.AlbumId, which restricts to ONE named album:
// `albumId=<guid>` + `albumMembership=unassigned` is contradictory and the
// endpoint rejects it with 400 rather than returning a silently empty page.
// Wire values are the lower-case member names; an absent parameter is Any.
public enum AlbumMembershipFilter
{
    Any = 0,
    Assigned = 1,
    Unassigned = 2,
}

// People filter include semantics. All: the item must contain ALL selected
// people. Any: at least one. Exclude always means "contains none of".
public enum PeopleFilterMode
{
    All = 0,
    Any = 1,
}
