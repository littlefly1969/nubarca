namespace NubArca.Api.Files;

// Page of gallery items. `Count` is the size of `Items` in this response, not
// the total number of matching images — there is no total-count query so we
// don't pay for an unbounded COUNT(*) on every page.
//
// `NextCursor` + `HasMore` (slice 60) are populated in both offset and cursor
// modes: clients can switch to cursor pagination without changing the
// response shape. In offset mode the `Offset` field is the current page's
// 0-based offset; in cursor mode it stays 0.
public sealed record ImageListResponse(
    IReadOnlyList<ImageItem> Items,
    int Limit,
    int Offset,
    int Count,
    string? NextCursor,
    bool HasMore)
{
    // Slice 100 (web NL search): present only on a physical-filter-first,
    // semantic-ranked page (?semanticQuery=…). Added as OPTIONAL init-only
    // properties so every existing positional construction is unchanged and
    // non-semantic responses serialise these as their defaults. `SemanticStatus`
    // is "ok" | "unavailable" | "indexing" — a generic, non-technical signal.
    public bool SemanticActive { get; init; }
    public int SemanticTopK { get; init; }
    public string? SemanticStatus { get; init; }

    // Server-authoritative total number of items matching the current filter set
    // (independent of paging; duplicate-collapse aware). The gallery engine
    // already computes this (ImagePage.TotalCount / GallerySemanticPage.TotalCount);
    // it is surfaced here so the web workspace shows a real result count instead
    // of the loaded-page count. Null on the legacy offset path, which has no
    // bounded total. Adding it as an OPTIONAL init-only property keeps every
    // existing positional construction unchanged.
    public int? Total { get; init; }
}
