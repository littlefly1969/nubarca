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
/// SEVEN CONDITIONS, and it is easy to write six. Beyond the live-file rule the
/// text side already has, the visual side adds four of its own:
///
///  - the index must be `Completed`. A partially rendered document has rows for
///    the pages that worked, and they are not a reading of that document;
///  - the index's blob must be the file's CURRENT blob. This is what makes
///    replacing a document's content invalidate its visual index instantly,
///    with no sweeper: the join simply stops matching;
///  - the render profile and the embedding profile must both be the ACTIVE
///    ones. Pixels drawn by a superseded engine and vectors from a superseded
///    checkpoint are both plausible-looking answers in the wrong coordinate
///    system, and neither announces itself;
///  - and the file must still have a CURRENT, COMPLETED `DocumentText` for
///    these same bytes.
///
/// THAT LAST ONE IS THE ONE THIS BOUNDARY IS MOST LIKELY TO BE WRITTEN WITHOUT,
/// because it looks redundant and is not.
///
/// A visual index is an OPTIONAL derivative of a document whose authority is its
/// text. When the text side stops being current — an extractor upgrade
/// superseded it, a re-extraction failed, the reading was withdrawn — that file
/// has no authoritative interpretation any more, and a visual index describing
/// its pixels must stop introducing it. Leaving the check to the scoped text
/// pass that runs afterwards is not the same thing: it repairs the EVIDENCE
/// while the file has already been introduced as a candidate, the visual hit has
/// already displaced somebody else's document from a bounded candidate list, and
/// `documents visual-status` has already counted it as retrievable. The
/// derivative must not outlive the authority it derives from, and the place to
/// say so is here, once, where every reader passes.
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
        IQueryable<DocumentText> documents,
        IQueryable<FileItem> files,
        Guid ownerUserId,
        Guid embeddingProfileId,
        IReadOnlyCollection<string> activeRenderProfileKeys)
        => from unit in units
           join index in indexes on unit.DocumentVisualIndexId equals index.Id
           join file in Answerable(files, documents, ownerUserId)
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
        IQueryable<DocumentText> documents,
        IQueryable<FileItem> files,
        Guid ownerUserId,
        Guid embeddingProfileId,
        IReadOnlyCollection<string> activeRenderProfileKeys)
        => (from index in indexes
            join file in Answerable(files, documents, ownerUserId)
                on index.FileItemId equals file.Id
            where index.OwnerUserId == ownerUserId
                  && index.Status == AiArtifactStatuses.Completed
                  && index.SourceBlobObjectId == file.BlobObjectId
                  && index.EmbeddingProfileId == embeddingProfileId
                  && activeRenderProfileKeys.Contains(index.RenderProfileKey)
            select file.Id).Distinct();

    /// AN ELIGIBLE FILE THAT STILL HAS AN AUTHORITATIVE READING.
    ///
    /// `OwnerDocumentEligibility.Eligible` answers "may this person's knowledge
    /// include this file". This adds the question the visual side must also ask:
    /// does the file still have a CURRENT, COMPLETED extraction of these exact
    /// bytes — because a visual index is a derivative of a document whose
    /// authority is its text, and it must not outlive that authority.
    ///
    /// Expressed as one filtered queryable that both readers join to, rather
    /// than as a predicate each of them spells. Two spellings of one boundary
    /// are two spellings that drift, and the drift is invisible: the file-level
    /// projection feeds candidate expansion and the unit-level one feeds
    /// ranking, so a rule present in only one of them produces a candidate list
    /// and a hit list that disagree about what is retrievable.
    ///
    /// An EXISTS rather than a join, so a unit cannot be multiplied by however
    /// many `DocumentText` rows matched. The filtered unique index makes at most
    /// one current row per file today; this does not depend on that staying
    /// true.
    ///
    /// All five parts are required and each removes a different way of being
    /// wrong:
    ///
    ///  - OWNER on the extraction, matching the asker. A derived row's owner
    ///    column is a denormalized copy, so it is checked against the value the
    ///    live-file join already established rather than trusted;
    ///  - the extraction must belong to THIS live file, not to another file of
    ///    the same owner;
    ///  - `IsCurrent`, because a superseded reading is not authority — that is
    ///    the entire purpose of the flag;
    ///  - `Completed`, because a failed or skipped re-extraction leaves a
    ///    current row that answers nothing;
    ///  - and its source blob must be the file's CURRENT blob, so an extraction
    ///    of the previous content cannot vouch for pixels of the new one.
    public static IQueryable<FileItem> Answerable(
        IQueryable<FileItem> files,
        IQueryable<DocumentText> documents,
        Guid ownerUserId)
        => OwnerDocumentEligibility.Eligible(files, ownerUserId)
            .Where(f => documents.Any(d => d.FileItemId == f.Id
                                           && d.OwnerUserId == ownerUserId
                                           && d.IsCurrent
                                           && d.Status == AiArtifactStatuses.Completed
                                           && d.SourceBlobObjectId == f.BlobObjectId));
}

/// One eligible visual unit with the rows that made it eligible.
public sealed class EligibleVisualUnit
{
    public DocumentVisualUnit Unit { get; init; } = null!;
    public DocumentVisualIndex Index { get; init; } = null!;
    public FileItem File { get; init; } = null!;
}
