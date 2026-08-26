using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NubArca.Api.Ai.TextEmbeddings;
using NubArca.Api.Rag.Domains;
using NubArca.Api.Rag.Indexing;
using NubArca.Api.Rag.ProductHelp;
using NubArca.Api.Rag.Retrieval;
using NubArca.Api.Rag.Sources;
using NubArca.Api.Rag.Storage;

namespace NubArca.Api.Rag;

/// Registers the RAG substrate.
///
/// Split in two on purpose. `AddRagSubstrate` is what every host calls and needs
/// no database: the domain registry, the bundled Product Help corpus, the index
/// cache and the retriever, so an installation with no connection string still
/// answers Help from the corpus in its image. `AddRagDatabase` adds the indexed
/// corpus, the vector table and the indexer, and is called only where an
/// AppDbContext exists.
///
/// Nothing here is enabled by default. Semantic retrieval is off, no profile is
/// configured, no model is downloaded, and the repository domain has no index
/// until an operator runs `rag index`.
public static class RagServiceRegistration
{
    public static IServiceCollection AddRagSubstrate(this IServiceCollection services)
    {
        // The policy table. A singleton over an immutable value: the same object
        // a test, the CLI and the web host read.
        services.AddSingleton<IRagDomainRegistry>(RagDomainRegistry.Instance);

        // One built index per domain, rebuilt when the corpus signature changes.
        services.AddSingleton<RagLexicalIndexCache>();

        // The corpus that ships INSIDE the image. Loaded once at startup: it
        // cannot change under a running process, and re-reading it per request
        // would add a filesystem dependency to every Help question.
        services.AddSingleton(sp =>
        {
            var resolver = sp.GetRequiredService<Assistant.AssistantModelResolver>();
            var log = sp.GetRequiredService<ILoggerFactory>().CreateLogger("product-help");
            return ProductHelpCorpusLoader.Load(
                resolver.HelpBounds.CorpusPath, ProductHelpCorpusLoader.RunningRevision, log);
        });
        services.AddSingleton<BundledProductHelpCorpusSource>();

        // An EXPLICIT factory, because `RagDatabaseServices` is optional and the
        // built-in container has no notion of an optional constructor
        // parameter. The obvious shortcut — registering
        // `AddScoped(sp => sp.GetService<RagDatabaseServices>())` as a default —
        // is a trap: it makes the type resolve to ITSELF whenever this method
        // runs after AddRagDatabase, and the container recurses forever. The web
        // host and the CLI both register in that order, so it hung both and no
        // test host did, because a test registers its own graph last.
        services.AddScoped<IRagRetriever>(sp => new RagRetriever(
            sp.GetRequiredService<IRagDomainRegistry>(),
            sp.GetService<RagDatabaseServices>(),
            sp.GetRequiredService<BundledProductHelpCorpusSource>(),
            sp.GetRequiredService<RagLexicalIndexCache>(),
            sp.GetRequiredService<IOptions<RagOptions>>(),
            sp.GetRequiredService<ILogger<RagRetriever>>()));

        return services;
    }

    /// The database half. Call from any host with an AppDbContext — web host,
    /// CLI/worker host, test fixture — so retrieval behaves identically
    /// everywhere.
    public static IServiceCollection AddRagDatabase(this IServiceCollection services)
    {
        services.AddScoped<DatabaseRagCorpusSource>();
        services.AddScoped<RagVectorIndexService>();
        services.AddScoped<TextEmbeddingResolver>();
        services.AddScoped<RagVectorRetriever>();
        services.AddScoped<RagDatabaseServices>();

        // Local text embedding. Both providers are registered; which one runs is
        // decided by the configured PROFILE's model provider, never by
        // registration order — see TextEmbeddingResolver.
        services.AddSingleton<ITextEmbeddingProvider, DeterministicTextEmbeddingProvider>();
        services.AddSingleton<OnnxTextEmbeddingProvider>();
        services.AddSingleton<ITextEmbeddingProvider>(
            sp => sp.GetRequiredService<OnnxTextEmbeddingProvider>());

        // One provider per domain. A provider serves exactly one domain, so
        // there is no argument that makes the repository provider write into
        // `product-help`.
        services.AddSingleton<IRepositorySnapshotReader, GitRepositorySnapshotReader>();
        services.AddScoped<IRagSourceProvider, RepositorySnapshotSourceProvider>();
        services.AddScoped<IRagSourceProvider, ProductHelpSourceProvider>();

        services.AddScoped<IRagIndexer, RagIndexer>();

        return services;
    }
}
