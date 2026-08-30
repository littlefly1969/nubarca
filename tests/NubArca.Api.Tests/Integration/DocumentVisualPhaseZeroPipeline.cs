using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NubArca.Api.Ai;
using NubArca.Api.Ai.Backends;
using NubArca.Api.Ai.DocumentVisual;
using NubArca.Api.Ai.Onnx;
using NubArca.Api.Ai.Resolution;
using NubArca.Api.Ai.TextEmbeddings;
using NubArca.Api.Domain.Ai;
using NubArca.Api.Rag;
using NubArca.Api.Rag.Domains;
using NubArca.Api.Rag.ProductHelp;
using NubArca.Api.Rag.Retrieval;
using NubArca.Api.Rag.Storage;
using NubArca.Api.Tests.Ai.DocumentVisual;

namespace NubArca.Api.Tests.Integration;

/// The REAL pipeline, assembled over a Phase-0 fixture.
///
/// Every component is the production one: `RagRetriever` for the text passes,
/// `OwnerDocumentVisualRetriever` for the dense visual pass with the real
/// SigLIP2 towers, `VisualLateInteractionReranker` for MaxSim, and
/// `OwnerDocumentRetrievalPipeline` to fuse them. Only the LATE-INTERACTION
/// PROVIDER is substituted, and only because its model runs in Python — the
/// vectors it serves were produced by the real candidate on the real rendered
/// pages, and they travel through NubArca's own interface, storage and scorer.
internal static class DocumentVisualPhaseZeroPipeline
{
    internal static OwnerDocumentRetrievalPipeline Build(
        DocumentVisualHarness harness,
        IOptions<AiOptions> ai,
        IOptions<DocumentVisualOptions> visual,
        string modelDir,
        IVisualLateInteractionProvider? lateProvider = null)
    {
        var ragOptions = Options.Create(new RagOptions());
        var semantic = new RagSemanticProfileResolver(RagDomainRegistry.Instance, ragOptions);
        var embeddings = new TextEmbeddingResolver(
            harness.Db,
            new ITextEmbeddingProvider[] { new DeterministicTextEmbeddingProvider() },
            semantic);
        var serializer = new AiVectorSerializer();
        var corpus = new OwnerDocumentCorpusSource(harness.Db);

        var text = new RagRetriever(
            RagDomainRegistry.Instance,
            new RagDatabaseServices(
                new DatabaseRagCorpusSource(harness.Db),
                new RagVectorRetriever(
                    embeddings,
                    new RagVectorIndexService(harness.Db, serializer, TimeProvider.System),
                    ragOptions),
                embeddings,
                new RagVectorIndexService(harness.Db, serializer, TimeProvider.System),
                corpus,
                new OwnerDocumentVectorRetriever(harness.Db, corpus, embeddings, serializer)),
            new BundledProductHelpCorpusSource(ProductHelpCorpus.Empty),
            new RagLexicalIndexCache(),
            ragOptions,
            semantic,
            NullLogger<RagRetriever>.Instance);

        // THE REAL PAIRED TOWERS. The query side is what turns a typed question
        // into a point in the same space the rendered pages were embedded into,
        // and substituting it would measure a different model than the one the
        // pages went through.
        var factory = new OnnxInferenceSessionFactory(
            ai, NullLogger<OnnxInferenceSessionFactory>.Instance);
        var images = new OnnxImageEmbedder(
            ai, new OnnxImagePreprocessor(), NullLogger<OnnxImageEmbedder>.Instance, factory);
        var queries = new OnnxTextEmbedder(ai, factory);

        var backends = new AiBackendResolver(
            ai,
            new AiProfileRegistry(harness.Db, TimeProvider.System),
            new IAiBackend[] { images, queries });

        var visualRetriever = new OwnerDocumentVisualRetriever(
            harness.Db,
            new DocumentVisualProfileResolver(
                backends, new AiProfileRegistry(harness.Db, TimeProvider.System), visual),
            harness.Renderers,
            new DocumentVisualVectorIndexService(harness.Db, serializer),
            serializer,
            visual,
            new VisualLateInteractionReranker(
                harness.Db,
                new AiProfileRegistry(harness.Db, TimeProvider.System),
                serializer,
                visual,
                NullLogger<VisualLateInteractionReranker>.Instance,
                lateProvider),
            NullLogger<OwnerDocumentVisualRetriever>.Instance);

        return new OwnerDocumentRetrievalPipeline(text, visualRetriever);
    }
}

/// The candidate's QUERY tower, replayed.
///
/// The model itself runs in the disposable Python environment — NubArca ships no
/// production worker for an unpromoted candidate, deliberately — so the query
/// multi-vectors it produced for each golden question are recorded and served
/// back here through the real `IVisualLateInteractionProvider` interface. What
/// is being measured on this side is NubArca's reranking: the storage decode,
/// the MaxSim, the ordering, the bounds. The model's own cost is measured where
/// the model runs.
internal sealed class PrecomputedLateInteractionProvider : IVisualLateInteractionProvider
{
    private readonly IReadOnlyDictionary<string, IReadOnlyList<float[]>> _queries;
    private readonly int _dimension;

    internal PrecomputedLateInteractionProvider(
        IReadOnlyDictionary<string, IReadOnlyList<float[]>> queries, int dimension)
    {
        _queries = queries;
        _dimension = dimension;
    }

    public string Provider => "colvision-precomputed";

    public VisualProviderReadiness CheckReadiness(AiProfile profile)
        => VisualProviderReadiness.Available;

    public Task<MultiVectorEmbeddingResult> EmbedImageAsync(
        AiProfile profile, ReadOnlyMemory<byte> imageBytes, CancellationToken ct = default)
        => throw new NotSupportedException(
            "Phase-0 page vectors are produced where the model runs, not here.");

    public Task<MultiVectorEmbeddingResult> EmbedQueryAsync(
        AiProfile profile, string query, CancellationToken ct = default)
    {
        if (!_queries.TryGetValue(query.Trim(), out var vectors))
        {
            // A question the candidate was never asked. Refused rather than
            // answered with something plausible: a benchmark that silently
            // invents a query vector is measuring the invention.
            throw new VisualLateInteractionException(DocumentVisualReasons.ModelUnavailable);
        }

        return Task.FromResult(
            new MultiVectorEmbeddingResult(vectors, _dimension, profile.Key));
    }
}

/// The dense visual retriever alone, for reporting what a corpus discriminates.
internal static class DocumentVisualPhaseZeroRetriever
{
    internal static IOwnerDocumentVisualRetriever Build(
        DocumentVisualHarness harness,
        IOptions<AiOptions> ai,
        IOptions<DocumentVisualOptions> visual,
        string modelDir)
    {
        var serializer = new AiVectorSerializer();
        var factory = new OnnxInferenceSessionFactory(
            ai, NullLogger<OnnxInferenceSessionFactory>.Instance);
        var images = new OnnxImageEmbedder(
            ai, new OnnxImagePreprocessor(), NullLogger<OnnxImageEmbedder>.Instance, factory);
        var queries = new OnnxTextEmbedder(ai, factory);

        var backends = new AiBackendResolver(
            ai,
            new AiProfileRegistry(harness.Db, TimeProvider.System),
            new IAiBackend[] { images, queries });

        return new OwnerDocumentVisualRetriever(
            harness.Db,
            new DocumentVisualProfileResolver(
                backends, new AiProfileRegistry(harness.Db, TimeProvider.System), visual),
            harness.Renderers,
            new DocumentVisualVectorIndexService(harness.Db, serializer),
            serializer,
            visual,
            new VisualLateInteractionReranker(
                harness.Db,
                new AiProfileRegistry(harness.Db, TimeProvider.System),
                serializer,
                visual,
                NullLogger<VisualLateInteractionReranker>.Instance),
            NullLogger<OwnerDocumentVisualRetriever>.Instance);
    }
}
