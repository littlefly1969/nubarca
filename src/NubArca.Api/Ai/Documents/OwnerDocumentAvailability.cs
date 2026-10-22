using NubArca.Api.Rag;

namespace NubArca.Api.Ai.Documents;

/// Whether ONE retrieval signal can run, and the reason token when it cannot.
public sealed record OwnerDocumentSignal(bool Available, string? Reason)
{
    public static readonly OwnerDocumentSignal Ready = new(true, null);
    public static OwnerDocumentSignal Off(string reason) => new(false, reason);
}

/// Availability of one source kind's signals.
public sealed record OwnerDocumentKindAvailability(
    string Kind,
    OwnerDocumentSignal Lexical,
    OwnerDocumentSignal Semantic,
    OwnerDocumentSignal Visual);

/// "Which kinds of search will work for me right now", per domain and per
/// source kind, so a client can hide semantic search and fall back to lexical
/// without guessing (docs/document-intelligence/source-contract.md).
///
/// Reason tokens are a small closed vocabulary, mapped from the internal
/// sanitized reasons. Counts and configuration only: no name, no path, no id.
public static class OwnerDocumentAvailability
{
    public const string Disabled = "disabled";
    public const string NoProfile = "no_profile";
    public const string ModelMissing = "model_missing";
    public const string NotIndexed = "not_indexed";

    public static IReadOnlyList<OwnerDocumentKindAvailability> Build(
        PrivateDocumentStatus status, string? embeddingReason)
    {
        var indexing = status.Indexing ?? Rag.Retrieval.OwnerDocumentIndexingSummary.Empty;

        var lexical = indexing.Chunks > 0 ? OwnerDocumentSignal.Ready : OwnerDocumentSignal.Off(NotIndexed);

        var semantic = embeddingReason switch
        {
            null when indexing.EmbeddedChunks is > 0 => OwnerDocumentSignal.Ready,
            null => OwnerDocumentSignal.Off(NotIndexed),
            RagFailureReasons.EmbeddingDisabled => OwnerDocumentSignal.Off(Disabled),
            RagFailureReasons.EmbeddingProfileUnavailable => OwnerDocumentSignal.Off(NoProfile),
            RagFailureReasons.EmbeddingModelUnavailable => OwnerDocumentSignal.Off(ModelMissing),
            var other => OwnerDocumentSignal.Off(other),
        };

        var visual = status.VisualEnabled
            ? OwnerDocumentSignal.Ready
            : OwnerDocumentSignal.Off(status.VisualReason ?? Disabled);

        // Only `file` exists in this release.
        return new[]
        {
            new OwnerDocumentKindAvailability(OwnerDocumentSourceKinds.File, lexical, semantic, visual),
        };
    }
}
