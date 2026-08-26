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
        => new(
            RagDomainRegistry.Instance,
            database: null,
            new BundledProductHelpCorpusSource(corpus),
            new RagLexicalIndexCache(),
            Options.Create(options ?? new RagOptions()),
            NullLogger<RagRetriever>.Instance);

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
