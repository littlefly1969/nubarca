using NubArca.Api.Data;

namespace NubArca.Api.Ai.Documents.Sources;

/// One owner-private document source as the engine sees it in a candidate page.
///
/// `RevisionHash` is the idempotence key: an unchanged value means nothing is
/// re-derived. `DisplayName` is the owner's own name for the source; it is
/// never logged and never printed by an operator command.
public sealed record DocumentSourceCandidate(Guid SourceId, string RevisionHash, string DisplayName);

/// What loading one source produced: raw BYTES for the extractors to identify
/// and parse, already normalized SECTIONS, or a sanitized reason there is
/// neither.
public sealed record DocumentSourceLoad
{
    public DocumentSourceLoadKind Kind { get; private init; }

    // ---- bytes path (the `file` kind) -------------------------------------
    public byte[]? Bytes { get; private init; }
    public string? FileName { get; private init; }
    public string? MimeType { get; private init; }

    // ---- sections path (a kind that already knows its structure) ------------
    public IReadOnlyList<ExtractedDocumentBlock>? Sections { get; private init; }

    /// The `DocumentText.Source` value and the extraction profile key a
    /// sections kind records its readings under.
    public string? SectionsSource { get; private init; }
    public string? SectionsProfileKey { get; private init; }

    // ---- refusal ------------------------------------------------------------
    public string? Reason { get; private init; }

    /// A content verdict about this revision (true), or a statement about this
    /// installation right now (false).
    public bool IsPermanent { get; private init; }

    /// Whether a permanent verdict is written to `document_texts`. A source
    /// refused BEFORE it was read (a recorded size past the bound) is reported
    /// but not recorded, exactly as before sources existed.
    public bool RecordVerdict { get; private init; }

    public static DocumentSourceLoad FromBytes(byte[] bytes, string fileName, string mimeType)
        => new() { Kind = DocumentSourceLoadKind.Bytes, Bytes = bytes, FileName = fileName, MimeType = mimeType };

    public static DocumentSourceLoad FromSections(
        IReadOnlyList<ExtractedDocumentBlock> sections, string source, string profileKey)
        => new()
        {
            Kind = DocumentSourceLoadKind.Sections,
            Sections = sections,
            SectionsSource = source,
            SectionsProfileKey = profileKey,
        };

    /// The source is no longer eligible, or no longer at the revision the
    /// candidate named. Nothing is recorded; a later pass sees the truth.
    public static DocumentSourceLoad NotEligible()
        => new() { Kind = DocumentSourceLoadKind.NotEligible };

    public static DocumentSourceLoad Refused(string reason, bool permanent, bool recordVerdict)
        => new()
        {
            Kind = DocumentSourceLoadKind.Refused,
            Reason = reason,
            IsPermanent = permanent,
            RecordVerdict = permanent && recordVerdict,
        };
}

public enum DocumentSourceLoadKind
{
    Bytes,
    Sections,
    NotEligible,
    Refused,
}

/// One live, eligible source of one kind, as the engine joins it: the id the
/// derived rows are keyed by, the LIVE owner (read off the kind's own row, not
/// a derived copy) and the name a citation shows. Nothing else — a citation
/// needs a name, and a kind's other columns stay inside the kind.
public sealed class EligibleSourceRow
{
    public string SourceKind { get; init; } = string.Empty;
    public Guid SourceId { get; init; }
    public Guid OwnerUserId { get; init; }
    public string DisplayName { get; init; } = string.Empty;
}

/// THE BOUNDARY for one kind (see docs/document-intelligence/source-contract.md).
///
/// Split from IOwnerDocumentSource only so retrieval can ask the question
/// without the kind's loading collaborators (storage, options): every
/// registered source IS one of these, and the engine's eligible set is the
/// union of exactly the registered ones.
public interface IOwnerDocumentSourceEligibility
{
    /// The discriminator stored in `SourceKind` (see DocumentSourceKinds).
    string Kind { get; }

    /// This owner's sources of this kind that may be knowledge RIGHT NOW,
    /// recomputed from the kind's live rows on every call. Composable: the
    /// engine joins it inside its own query (never materialized), so it must
    /// be translatable by EF for every supported provider, and it must END in
    /// one `new EligibleSourceRow { SourceId, OwnerUserId, DisplayName }`
    /// projection that sets all three (the engine composes over those members
    /// and unions the kinds; a member left out cannot be translated).
    /// `SourceKind` is left unset — the engine stamps the discriminator itself.
    ///
    /// Owner-prefiltered by contract, and the engine re-checks the returned
    /// `OwnerUserId` against the asking owner anyway, so a kind that forgot
    /// the owner clause still cannot widen anybody's corpus.
    IQueryable<EligibleSourceRow> Eligible(AppDbContext db, Guid ownerUserId);
}

/// ONE KIND OF OWNER-PRIVATE DOCUMENT SOURCE (see
/// docs/document-intelligence/source-contract.md).
///
/// The engine owns chunking, embeddings, state and the job; a kind owns
/// identity, liveness and how its text is obtained. Every method is
/// owner-prefiltered: a kind cannot be asked about a source without saying
/// whose, and a source of another owner is simply not there.
public interface IOwnerDocumentSource : IOwnerDocumentSourceEligibility
{
    /// A keyset page of this owner's candidates after `afterSourceId`, ordered
    /// by SourceId. Never the whole set.
    Task<IReadOnlyList<DocumentSourceCandidate>> ListCandidatesAsync(
        Guid ownerUserId, Guid? afterSourceId, int take, CancellationToken cancellationToken = default);

    /// One candidate by id, or null when it is not (or no longer) a candidate
    /// of this owner.
    Task<DocumentSourceCandidate?> GetCandidateAsync(
        Guid ownerUserId, Guid sourceId, CancellationToken cancellationToken = default);

    /// Load one source at the candidate's revision. Eligibility is re-checked
    /// here, live, before anything is read.
    Task<DocumentSourceLoad> LoadAsync(
        Guid ownerUserId, DocumentSourceCandidate candidate, CancellationToken cancellationToken = default);
}

/// The registered kinds, resolved by discriminator.
public sealed class OwnerDocumentSources
{
    private readonly IReadOnlyDictionary<string, IOwnerDocumentSource> _byKind;

    public OwnerDocumentSources(IEnumerable<IOwnerDocumentSource> sources)
    {
        _byKind = sources.ToDictionary(s => s.Kind, StringComparer.Ordinal);
    }

    public IOwnerDocumentSource? For(string? kind)
        => kind is not null && _byKind.TryGetValue(kind, out var source) ? source : null;

    public IReadOnlyCollection<IOwnerDocumentSource> All => _byKind.Values.ToArray();

    /// The registered kinds' eligibility branches — what retrieval, vector
    /// retrieval and embedding union over.
    public IReadOnlyList<IOwnerDocumentSourceEligibility> Eligibility
        => _byKind.Values.OrderBy(s => s.Kind, StringComparer.Ordinal).ToArray();
}

/// "This source is new, changed or removed." Returns once the change is
/// RECORDED (a job enqueued), never after extraction or embeddings ran — an
/// upload must not wait for either. Failures are swallowed and logged as a
/// token; a later backfill catches the source up.
///
/// Removal does not need a call to be safe: eligibility already hides a removed
/// source on the next query.
public interface IDocumentSourceChangeSink
{
    Task SourceChangedAsync(
        string kind, Guid ownerUserId, Guid sourceId, CancellationToken cancellationToken = default);

    /// Many of one owner's sources changed at once (a folder restored, an
    /// import finished). One owner-scoped pass instead of one job per source.
    Task OwnerSourcesChangedAsync(Guid ownerUserId, CancellationToken cancellationToken = default);
}
