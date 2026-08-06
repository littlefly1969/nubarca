using System.Linq.Expressions;
using NubArca.Api.Domain;

namespace NubArca.Api.MediaLibrary;

// Slice 3 (media organization): the per-file media-library scope, and the ONE
// place that turns it into a query predicate. Every media surface (galleries,
// album content, search, similarity, semantic, people, TV, Party, AI candidate
// selection) narrows through this instead of hand-writing
// `f.MediaLibraryState == …` in dozens of places.
//
//   Active   — the normal media library (the default for every surface).
//   Excluded — the "Esclusi" tab: files the owner moved out of the library.
//   All       — no scope constraint. Used ONLY where a surface legitimately
//               needs both (e.g. the bulk endpoint resolving ownership); never
//               a default.
//
// This is deliberately NOT a global EF query filter: Excluded files must stay
// visible in the file browser, downloads, quota, import and cleanup. Those
// paths simply never call this policy.
public enum MediaLibraryScope
{
    Active,
    Excluded,
    All,
}

public static class MediaLibraryScopePolicy
{
    // Raw DB value of MediaLibraryState.Active, for the pgvector raw-SQL paths
    // that add the predicate by hand (same convention as PrivateVaultId there).
    public const int ActiveDbValue = (int)MediaLibraryState.Active;

    // Composable predicate for "in the active media library". Reused inside
    // blob-keyed EXISTS/Any candidate checks (face/embedding backfills) where a
    // full IQueryable transform does not fit.
    public static readonly Expression<Func<FileItem, bool>> IsActive =
        f => f.MediaLibraryState == MediaLibraryState.Active;

    public static IQueryable<FileItem> ApplyScope(
        IQueryable<FileItem> query, MediaLibraryScope scope) => scope switch
    {
        MediaLibraryScope.Active => query.Where(f => f.MediaLibraryState == MediaLibraryState.Active),
        MediaLibraryScope.Excluded => query.Where(f => f.MediaLibraryState == MediaLibraryState.Excluded),
        _ => query,
    };
}
