using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NubArca.Api.Rag;
using NubArca.Api.Rag.Domains;
using NubArca.Api.Rag.ProductHelp;
using NubArca.Api.Rag.Retrieval;

namespace NubArca.Api.Tests.Rag;

// Builds the REAL generic retriever over an in-memory corpus.
//
// Deliberately the production RagRetriever rather than a test double of it:
// these tests protect the evidence gate, the ranking profiles, the fusion and
// the excerpting, and every one of those would keep passing against a
// simplified stand-in while the shipped path regressed.
//
// `RagDatabaseServices` is null, which is the honest shape of a host with no
// connection string: the bundled corpus, lexical retrieval, no vectors. The
// semantic path has its own tests.
internal static class RagTestHarness
{
    internal static RagRetriever ForProductHelp(ProductHelpCorpus corpus, RagOptions? options = null)
        => Build(new BundledProductHelpCorpusSource(corpus), options);

    /// The production retriever, wired the way the container wires it — the
    /// REAL semantic resolver included, so a test cannot accidentally prove that
    /// a domain is semantic when the shipped configuration says it is not.
    internal static RagRetriever Build(
        BundledProductHelpCorpusSource bundled,
        RagOptions? options = null,
        RagDatabaseServices? database = null)
    {
        var resolved = Options.Create(options ?? new RagOptions());
        return new RagRetriever(
            RagDomainRegistry.Instance,
            database,
            bundled,
            new RagLexicalIndexCache(),
            resolved,
            SemanticResolver(resolved.Value),
            NullLogger<RagRetriever>.Instance);
    }

    internal static IRagSemanticProfileResolver SemanticResolver(RagOptions options)
        => new RagSemanticProfileResolver(RagDomainRegistry.Instance, Options.Create(options));

    /// The Product Help corpus built from the sources this release actually
    /// ships, from the repository the tests are running in.
    internal static ProductHelpCorpus ShippedProductHelp(string revision = "test-revision")
        => ProductHelpCorpusBuilder.Build(RepositoryRoot(), revision);

    /// Walks up from the test binary to the repository root. The Product Help
    /// golden tests read the real documents, so they need the real tree.
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
