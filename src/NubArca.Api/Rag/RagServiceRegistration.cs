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

        // Which domain embeds with which model. A singleton like the registry it
        // reads: the answer is a compiled policy plus configuration, and neither
        // depends on a request.
        services.AddSingleton<IRagSemanticProfileResolver, RagSemanticProfileResolver>();

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
            sp.GetRequiredService<IRagSemanticProfileResolver>(),
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

        // The OWNER-PRIVATE half. Separate types rather than owner parameters on
        // the system ones: `user-documents` reads different tables, ranks a
        // different way and cannot be queried without an owner, and a shared
        // implementation with an optional owner argument is exactly the shape
        // where "forgot to pass it" becomes "answered from everybody's".
        services.AddScoped<Retrieval.OwnerDocumentCorpusSource>();
        services.AddScoped<Retrieval.OwnerDocumentVectorRetriever>();
        // The parsers, and the registry that resolves one by format. Registered
        // as the interface so a host can add a family without this file
        // learning about it, and singletons because a parser holds no
        // per-request state — it is handed bytes and returns blocks.
        services.AddSingleton<Ai.Documents.IDocumentExtractionProvider,
            Ai.Documents.NativeTextExtractionProvider>();
        services.AddSingleton<Ai.Documents.IDocumentExtractionProvider,
            Ai.Documents.WordDocumentExtractionProvider>();
        services.AddSingleton<Ai.Documents.IDocumentExtractionProvider,
            Ai.Documents.SpreadsheetExtractionProvider>();
        services.AddSingleton<Ai.Documents.IDocumentExtractionProvider,
            Ai.Documents.PresentationExtractionProvider>();
        services.AddSingleton<Ai.Documents.IDocumentExtractionProvider,
            Ai.Documents.PdfExtractionProvider>();
        services.AddSingleton<Ai.Documents.DocumentExtractionProviders>();

        // The OCR seam and the renderer. Both singletons: the renderer serialises
        // access to a native library that is not documented as thread-safe, and
        // the OCR provider holds the installation-wide concurrency gate.
        services.AddSingleton<Ai.Documents.IDocumentOcrProvider,
            Ai.Documents.TesseractOcrProvider>();
        services.AddSingleton<Ai.Documents.PdfPageRenderer>();

        services.AddScoped<Ai.Documents.OwnerDocumentIndexer>();

        // ---- visual document retrieval -----------------------------------
        //
        // Registered unconditionally alongside the text pipeline, and INERT
        // unless `Ai:DocumentVisual:Enabled` is set: the resolver reports the
        // capability unavailable, the indexer no-ops and the retriever answers
        // "unavailable" so the Assistant falls back to the text pass it always
        // used. Registration is not enablement.
        //
        // The renderers are singletons because a renderer holds no per-request
        // state and because the PDF one serialises access to a native library
        // that is not documented as thread-safe — a per-request instance would
        // be several gates over one PDFium.
        services.AddSingleton<Ai.DocumentVisual.PdfVisualRenderer>();
        services.AddSingleton<Ai.DocumentVisual.IDocumentVisualRenderer>(
            sp => sp.GetRequiredService<Ai.DocumentVisual.PdfVisualRenderer>());
        services.AddSingleton<Ai.DocumentVisual.IDocumentVisualRenderer,
            Ai.DocumentVisual.TextCanvasVisualRenderer>();
        // The Office renderer is registered even when its worker is not
        // deployed. `ActiveRenderProfileKeys` must be a stable statement about
        // what this BUILD renders — a worker restarting is an environment blip
        // and must not make an owner's already-indexed DOCX pages vanish from
        // search and come back. Readiness, checked per document, is what
        // actually gates rendering.
        services.AddSingleton<Ai.DocumentVisual.IDocumentVisualRenderer,
            Ai.DocumentVisual.OfficeVisualRenderer>();
        services.AddSingleton<Ai.DocumentVisual.DocumentVisualRenderers>();

        services.AddScoped<Ai.DocumentVisual.DocumentVisualProfileResolver>();
        services.AddScoped<Ai.DocumentVisual.DocumentVisualVectorIndexService>();
        // An EXPLICIT factory, because the late-interaction provider is
        // genuinely optional and the built-in container has no notion of an
        // optional constructor parameter — it validates the whole graph at
        // startup, so a plain `AddScoped<T>()` would stop every installation
        // that has not promoted a late model from booting. This release ships
        // with no provider registered, which is the shipped state.
        services.AddScoped(sp => new Ai.DocumentVisual.VisualLateInteractionReranker(
            sp.GetRequiredService<Data.AppDbContext>(),
            sp.GetRequiredService<Ai.IAiProfileRegistry>(),
            sp.GetRequiredService<Ai.IAiVectorSerializer>(),
            sp.GetRequiredService<IOptions<Ai.DocumentVisual.DocumentVisualOptions>>(),
            sp.GetRequiredService<ILogger<Ai.DocumentVisual.VisualLateInteractionReranker>>(),
            sp.GetService<Ai.DocumentVisual.IVisualLateInteractionProvider>()));
        services.AddScoped<Ai.DocumentVisual.OwnerDocumentVisualIndexer>();
        services.AddScoped<Ai.DocumentVisual.IOwnerDocumentVisualRetriever,
            Ai.DocumentVisual.OwnerDocumentVisualRetriever>();

        // The retrieval half of "answer from my documents", as ONE object: the
        // global text pass, the visual pass, the scoped text pass it implies,
        // and the rank fusion of the two. Shared by the Assistant, the
        // evaluation harness and the operator CLI — a benchmark that
        // re-implements the pipeline it benchmarks measures the
        // re-implementation.
        services.AddScoped(sp => new Ai.DocumentVisual.OwnerDocumentRetrievalPipeline(
            sp.GetRequiredService<IRagRetriever>(),
            sp.GetService<Ai.DocumentVisual.IOwnerDocumentVisualRetriever>()));
        services.AddScoped<Ai.DocumentVisual.DocumentVisualEvaluator>();

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
