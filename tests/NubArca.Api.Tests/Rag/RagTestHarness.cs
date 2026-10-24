using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NubArca.Api.Rag;
using NubArca.Api.Rag.Domains;
using NubArca.Api.Rag.Retrieval;

namespace NubArca.Api.Tests.Rag;

// Builds the REAL generic retriever.
//
// Deliberately the production RagRetriever rather than a test double of it:
// these tests protect the evidence gate, the ranking profile, the fusion and
// the excerpting, and every one of those would keep passing against a
// simplified stand-in while the shipped path regressed.
//
// `RagDatabaseServices` null is the honest shape of a host with no connection
// string: the owner's corpus cannot be read, so every query fails closed.
internal static class RagTestHarness
{
    /// The production retriever, wired the way the container wires it — the
    /// REAL semantic resolver included, so a test cannot accidentally prove that
    /// a domain is semantic when the shipped configuration says it is not.
    internal static RagRetriever Build(
        RagOptions? options = null,
        RagDatabaseServices? database = null)
    {
        var resolved = Options.Create(options ?? new RagOptions());
        return new RagRetriever(
            RagDomainRegistry.Instance,
            database,
            resolved,
            SemanticResolver(resolved.Value),
            NullLogger<RagRetriever>.Instance);
    }

    internal static IRagSemanticProfileResolver SemanticResolver(RagOptions options)
        => new RagSemanticProfileResolver(RagDomainRegistry.Instance, Options.Create(options));

    /// Walks up from the test binary to the repository root, for tests that
    /// read the real tree.
    internal static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "NubArca.sln")))
        {
            directory = directory.Parent;
        }
        return directory?.FullName
               ?? throw new InvalidOperationException("Repository root not found from the test binary.");
    }
}
