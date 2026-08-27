using NubArca.Api.Domain;
using NubArca.Api.MediaLibrary;

namespace NubArca.Api.Ai.Documents;

/// WHICH of a person's files may become private knowledge — asked once, in one
/// place, by both ingestion and retrieval.
///
/// The two callers matter more than the rule. Ingestion asks it to decide what
/// to extract; RETRIEVAL asks it again, live, on every question. A derived row
/// is a cache of a decision made at some point in the past, and the past is
/// exactly the wrong time to have decided: a file moved into the Private Vault
/// this morning, or deleted last week, still has its `DocumentText`, its chunks
/// and its embeddings until something cleans them up. Cleanup is housekeeping.
/// It is not the security boundary, and a boundary that depends on a sweeper
/// having run is a boundary that fails whenever the sweeper is behind.
///
/// So retrieval JOINS to this predicate rather than trusting what it stored,
/// and the answer to "can this still be read" is recomputed from the live
/// FileItem every single time.
///
/// The Vault half is structural rather than written here: `FileItem` carries a
/// global EF query filter of `PrivateVaultId == null`, so every query that does
/// not say `IgnoreQueryFilters()` cannot see vaulted content at all. Nothing in
/// this bounded context says `IgnoreQueryFilters()`. The explicit predicate
/// below is stated anyway — in the one place a future refactor would remove the
/// filter, a test that reads this file should fail.
public static class OwnerDocumentEligibility
{
    /// Every condition, as one composable predicate.
    ///
    /// OWNERSHIP is what confers authority, and it is deliberately the FileItem's
    /// owner rather than anything about the blob. Two people can hold the same
    /// bytes — deduplication is a storage fact — and it does not follow that
    /// either of them may read the other's document. See the shared-blob tests.
    ///
    /// SHARE VISIBILITY confers nothing. A file shared with this user, visible
    /// through a public link, on a Party surface or cast to a TV is not owned by
    /// them, so it never satisfies the owner clause in the first place. That is
    /// why there is no "and not shared" term here: being able to SEE something
    /// was never the test.
    public static IQueryable<FileItem> Eligible(IQueryable<FileItem> files, Guid ownerUserId)
        => files
            .Where(f => f.OwnerUserId == ownerUserId)
            // Deleted content answers no questions. `DeletedAt` is the soft
            // delete, and a soft-deleted file is gone as far as knowledge is
            // concerned even while its bytes are still refcounted.
            .Where(f => f.DeletedAt == null)
            // Belt to the query filter's braces. If the global
            // `PrivateVaultId == null` filter were ever removed, this line is
            // what keeps vaulted documents out of a person's own corpus.
            .Where(f => f.PrivateVaultId == null)
            // The owner moved this out of their library. NubArca is told not to
            // process it for AI, and "answer questions from it" is processing.
            .Where(f => f.MediaLibraryState == MediaLibraryState.Active);

    /// The same rule as a predicate expression, for `Any`/`EXISTS` subqueries
    /// that cannot take an IQueryable transform.
    public static System.Linq.Expressions.Expression<Func<FileItem, bool>> IsEligibleFor(Guid ownerUserId)
        => f => f.OwnerUserId == ownerUserId
                && f.DeletedAt == null
                && f.PrivateVaultId == null
                && f.MediaLibraryState == MediaLibraryState.Active;

    /// Eligible AND of a type NubArca reads as native text.
    ///
    /// Split from the rule above on purpose: "may this person's knowledge
    /// include this file" and "can we read it" are different questions, and only
    /// the first is a privacy decision. A future PDF extractor widens the second
    /// and must not be able to widen the first by accident.
    public static IQueryable<FileItem> Extractable(IQueryable<FileItem> files, Guid ownerUserId)
        => Eligible(files, ownerUserId)
            .Where(f => SupportedContentTypes.Contains(f.MimeType));

    /// The allowlist as a materialized array so EF can translate `Contains` into
    /// an `IN (…)`. Mirrors NativeTextExtractor.IsSupportedContentType, which
    /// stays the authority — a stored MIME type carrying a `; charset=` suffix
    /// simply does not match here and is filtered out by the extractor instead.
    private static readonly string[] SupportedContentTypes =
    {
        "text/plain",
        "text/markdown",
        "text/x-markdown",
        "text/csv",
        "text/tab-separated-values",
        "application/json",
        "application/xml",
        "text/xml",
        "application/yaml",
        "text/yaml",
    };

    /// Exposed so a test can assert this list and the extractor's agree. Two
    /// lists that must match are two lists that will not, unless something
    /// compares them.
    public static IReadOnlyList<string> DeclaredContentTypes => SupportedContentTypes;

    /// Deliberately unused here, and referenced so the relationship is
    /// discoverable: media surfaces narrow through MediaLibraryScopePolicy, and
    /// this predicate uses the same Active meaning rather than a second opinion
    /// about what "in the library" means.
    public static readonly int ActiveDbValue = MediaLibraryScopePolicy.ActiveDbValue;
}
