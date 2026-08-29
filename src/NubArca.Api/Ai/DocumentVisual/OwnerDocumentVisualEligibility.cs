using NubArca.Api.Ai.Documents;
using NubArca.Api.Domain;
using NubArca.Api.Domain.Ai;

namespace NubArca.Api.Ai.DocumentVisual;

/// WHICH visual units may answer a question — asked live, in one place, by
/// every reader of the visual tables.
///
/// This is `OwnerDocumentEligibility.EligibleChunks` for the other derivative,
/// and it exists for the identical reason: a `DocumentVisualIndex` records a
/// rendering that happened at some point in the past, and between then and now
/// the file may have been deleted, vaulted, excluded from the library, or had
/// its bytes replaced. Cleanup of orphaned visual rows is housekeeping; a
/// boundary that only holds once a sweeper has run is a boundary that fails for
/// as long as the sweeper is behind.
///
/// SIX CONDITIONS, and it is easy to write five. Beyond the live-file rule the
/// text side already has, the visual side adds three of its own:
///
///  - the index must be `Completed`. A partially rendered document has rows for
///    the pages that worked, and they are not a reading of that document;
///  - the index's blob must be the file's CURRENT blob. This is what makes
///    replacing a document's content invalidate its visual index instantly,
///    with no sweeper: the join simply stops matching;
///  - the render profile and the embedding profile must both be the ACTIVE
///    ones. Pixels drawn by a superseded engine and vectors from a superseded
///    checkpoint are both plausible-looking answers in the wrong coordinate
///    system, and neither announces itself.
///
/// The Vault half is structural, as it is for text: `FileItem` carries a global
/// query filter of `PrivateVaultId == null` and nothing in this bounded context
/// says `IgnoreQueryFilters()`. The explicit predicate inside
/// `OwnerDocumentEligibility.Eligible` is stated anyway, so that a refactor
/// removing the filter fails a test rather than opening the Vault.
public static class OwnerDocumentVisualEligibility
{
    /// Every condition, as one join, for every caller that reads visual rows.
    ///
    /// The live `File` is carried out rather than discarded because the callers
    /// need what only it can honestly answer — the display name, and the OWNER,
    /// which is verified live truth here and not the copy on the derived row.
    public static IQueryable<EligibleVisualUnit> EligibleUnits(
        IQueryable<DocumentVisualUnit> units,
        IQueryable<DocumentVisualIndex> indexes,
        IQueryable<FileItem> files,
        Guid ownerUserId,
        Guid embeddingProfileId,
        IReadOnlyCollection<string> activeRenderProfileKeys)
        => from unit in units
           join index in indexes on unit.DocumentVisualIndexId equals index.Id
           join file in OwnerDocumentEligibility.Eligible(files, ownerUserId)
               on index.FileItemId equals file.Id
           where index.OwnerUserId == ownerUserId
                 // COMPLETE, or nothing. Section 44: if unit N failed, units
                 // 1..N-1 must not become search results.
                 && index.Status == AiArtifactStatuses.Completed
                 // THE FILE'S CURRENT BYTES. A document whose content was
                 // replaced has an index describing pixels that are no longer
                 // in it; this clause is what makes that index unreachable on
                 // the very next question rather than after a cleanup pass.
                 && index.SourceBlobObjectId == file.BlobObjectId
                 && index.EmbeddingProfileId == embeddingProfileId
                 && activeRenderProfileKeys.Contains(index.RenderProfileKey)
           select new EligibleVisualUnit { Unit = unit, Index = index, File = file };

    /// The same rule expressed over FILES, for the candidate-expansion step
    /// that only needs to know which documents a visual hit may introduce.
    public static IQueryable<Guid> EligibleFileIds(
        IQueryable<DocumentVisualIndex> indexes,
        IQueryable<FileItem> files,
        Guid ownerUserId,
        Guid embeddingProfileId,
        IReadOnlyCollection<string> activeRenderProfileKeys)
        => (from index in indexes
            join file in OwnerDocumentEligibility.Eligible(files, ownerUserId)
                on index.FileItemId equals file.Id
            where index.OwnerUserId == ownerUserId
                  && index.Status == AiArtifactStatuses.Completed
                  && index.SourceBlobObjectId == file.BlobObjectId
                  && index.EmbeddingProfileId == embeddingProfileId
                  && activeRenderProfileKeys.Contains(index.RenderProfileKey)
            select file.Id).Distinct();
}

/// One eligible visual unit with the rows that made it eligible.
public sealed class EligibleVisualUnit
{
    public DocumentVisualUnit Unit { get; init; } = null!;
    public DocumentVisualIndex Index { get; init; } = null!;
    public FileItem File { get; init; } = null!;
}
