namespace NubArca.Api.Rag.Sources;

/// One eligible source, with everything the indexer needs and nothing it does
/// not.
///
/// `Text` is the source's content, streamed one file at a time rather than
/// materialized as a list: the repository domain is thousands of files, and a
/// provider that returns `IReadOnlyList<…>` has already read all of them.
public sealed record RagSourceDescriptor(
    string SourceKey,
    string Path,
    string Title,
    string SourceKind,
    string Revision,
    string ContentHash,
    string Language,
    string CodeLanguage,
    string Text,
    int Priority,
    IReadOnlyDictionary<string, string>? DomainMetadata = null);

/// Where a provider should read from.
///
/// `Revision` is explicit rather than discovered at index time by every
/// provider independently, so a run that indexes two domains stamps both with
/// the same snapshot.
public sealed record RagSourceRequest(string RootPath, string Revision);

/// A source of knowledge for ONE domain.
///
/// The provider decides what is eligible; the indexer decides what to do with
/// it. Keeping those apart is what lets the repository's safety rules — tracked
/// only, no `.git`, no build output, no secrets, no binaries — be a small,
/// separately testable thing rather than conditions scattered through an
/// indexing loop.
public interface IRagSourceProvider
{
    /// The domain this provider populates. A provider serves one domain: a
    /// provider that could be asked for any domain would be one argument away
    /// from writing repository sources into `product-help`.
    string Domain { get; }

    IAsyncEnumerable<RagSourceDescriptor> EnumerateAsync(
        RagSourceRequest request, CancellationToken cancellationToken = default);
}
