using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NubArca.Api.Ai;
using NubArca.Api.Ai.Backends;
using NubArca.Api.Ai.DocumentVisual;
using NubArca.Api.Ai.Documents;
using NubArca.Api.Ai.Resolution;
using NubArca.Api.Ai.TextEmbeddings;
using NubArca.Api.Data;
using NubArca.Api.Domain;
using NubArca.Api.Domain.Ai;
using NubArca.Api.Rag;
using NubArca.Api.Rag.Domains;
using NubArca.Api.Rag.ProductHelp;
using NubArca.Api.Rag.Retrieval;
using NubArca.Api.Rag.Storage;
using Xunit;
using Xunit.Abstractions;

namespace NubArca.Api.Tests.Ai.DocumentVisual;

// DOES THE VISUAL SIGNAL EARN ITS COST — measured, and reported per category.
//
// The isolation tests prove nothing of anybody else's comes back, which a corpus
// returning nothing satisfies perfectly. This is the other half: a synthetic
// library of one person's documents, thirteen questions of the shapes section 66
// of the specification names, and the same pipeline the Assistant runs, three
// times — text only, dense-visual-expanded, and (when a profile is promoted)
// late-interaction-expanded.
//
// WHAT THIS MEASURES AND WHAT IT DOES NOT. The embedding providers here are
// DETERMINISTIC — they hash their input into a vector — so this is not a
// statement about SigLIP2's semantics. The page vectors are seeded by the
// fixture, which means the harness controls exactly which document "looks like"
// which question, and what is measured is the PLUMBING: that a visually-found
// document reaches the top, that a text-only strength is not lost to it, and
// that the recovered/regressed accounting is honest. Model quality is a
// different question, asked in `DocumentVisualRealOnnxTests` and by
// `documents visual-evaluate` against a real library.
//
// The floors are deliberately LOOSE. They are a regression tripwire, not a
// target: thirteen questions is a small set, and tuning weights until a small
// set scores well moves the number and not the product.
public sealed class DocumentVisualGoldenEvaluationTests : IDisposable
{
    private readonly ITestOutputHelper _output;
    private readonly DocumentVisualHarness _harness = new();
    private AiProfile _extraction = null!;
    private AiProfile _visualProfile = null!;

    public DocumentVisualGoldenEvaluationTests(ITestOutputHelper output)
    {
        _output = output;
        Seed();
    }

    public void Dispose() => _harness.Dispose();

    // ---- the golden set -----------------------------------------------------

    /// Declared in `DocumentVisualGoldenCorpus`, shared with the Phase-0
    /// real-model lane. Two copies of a benchmark are two benchmarks.
    private static IReadOnlyList<DocumentVisualGoldenCase> Cases
        => DocumentVisualGoldenCorpus.Cases;

    // ---- measurement ----------------------------------------------------------

    [Fact]
    public async Task Text_Plus_Visual_Is_At_Least_As_Good_As_Text_Alone()
    {
        var comparison = await new DocumentVisualEvaluator(Pipeline())
            .CompareAsync(_harness.OwnerA, Cases, maxEvidence: 3);

        Report("text-only", comparison.Baseline);
        Report("visual-expanded", comparison.Candidate);
        _output.WriteLine($"recovered={comparison.Recovered.Count} regressed={comparison.Regressed.Count}");
        foreach (var query in comparison.Recovered) _output.WriteLine($"  + {query}");
        foreach (var query in comparison.Regressed) _output.WriteLine($"  - {query}");

        // THE CLAIM OF THIS SLICE, as a tripwire. Fusion must not LOSE what the
        // text path already found: a drop here means the visual half is
        // displacing correct results rather than adding to them.
        Assert.True(
            comparison.Candidate.RecallAtFive >= comparison.Baseline.RecallAtFive - 0.001,
            $"visual-expanded Recall@5 {comparison.Candidate.RecallAtFive:F3} fell below "
            + $"text-only {comparison.Baseline.RecallAtFive:F3}");

        Assert.True(
            comparison.Candidate.MeanReciprocalRank
                >= comparison.Baseline.MeanReciprocalRank - 0.001,
            $"visual-expanded MRR {comparison.Candidate.MeanReciprocalRank:F3} fell below "
            + $"text-only {comparison.Baseline.MeanReciprocalRank:F3}");

        // AND IT MUST ACTUALLY DO SOMETHING. A visual pass that changes nothing
        // is a visual pass nobody should pay to run.
        Assert.True(
            comparison.Recovered.Count > 0,
            "the visual pass recovered no query at all; it is not contributing");
    }

    [Fact]
    public async Task An_Exact_Identifier_Is_Not_Displaced_By_The_Visual_Pass()
    {
        // The regression this slice is most likely to cause. Vectors are worse
        // at exact identifiers than BM25 is, and a visual pass that promoted a
        // similar-LOOKING page over the file that literally contains the string
        // would be a downgrade dressed as a feature.
        var identifier = Cases.Single(c => c.Query == "NUBARCA_STORAGE_ROOT");
        var evaluator = new DocumentVisualEvaluator(Pipeline());

        var withVisual = await evaluator.EvaluateAsync(
            _harness.OwnerA, new[] { identifier }, useVisual: true, maxEvidence: 3);

        var outcome = Assert.Single(withVisual.Outcomes);
        Assert.Equal(1, outcome.FirstExpectedRank);
    }

    [Fact]
    public async Task An_Unanswerable_Question_Stays_Unanswerable_With_Visual_Retrieval_On()
    {
        // A visually similar page is not permission to improvise. The corpus has
        // documents that LOOK like anything; none of them answers this.
        var unanswerable = Cases.Single(c => !c.Answerable);

        var result = await Pipeline().RetrieveAsync(
            _harness.OwnerA, unanswerable.Query, 3, 8_000, useVisual: true);

        Assert.NotEqual(RagRetrievalOutcome.Strong, result.Outcome);
        Assert.Empty(result.Evidence);
    }

    [Fact]
    public async Task Another_Owner_Scores_Nothing_On_This_Set()
    {
        // The same thirteen questions asked by somebody who owns none of these
        // documents. Every metric must be zero — which is also a check that the
        // measurement itself is owner-scoped rather than reading a shared
        // corpus.
        var report = await new DocumentVisualEvaluator(Pipeline())
            .EvaluateAsync(_harness.OwnerB, Cases, useVisual: true, maxEvidence: 3);

        Assert.Equal(0.0, report.RecallAtFive);
        Assert.Equal(0.0, report.MeanReciprocalRank);
        Assert.Equal(0, report.TopThreePassed);
    }

    [Fact]
    public async Task Every_Expected_Document_Actually_Exists()
    {
        // A rename must not silently invalidate half the set: a case expecting a
        // document nobody has scores zero forever and looks like a regression.
        var names = await _harness.Db.FileItems
            .Where(f => f.OwnerUserId == _harness.OwnerA)
            .Select(f => f.Name)
            .ToListAsync();

        foreach (var expected in Cases.SelectMany(c => c.ExpectedDocuments))
        {
            Assert.Contains(expected, names);
        }
    }

    [Fact]
    public void The_Golden_Set_Covers_Every_Declared_Category()
    {
        // Thirteen cases, and at least one of each shape the specification
        // names. A set that quietly lost its scanned-PDF case would still score
        // well and would stop measuring the thing it exists for.
        Assert.Equal(13, Cases.Count);
        Assert.Single(Cases.Where(c => !c.Answerable));
        Assert.True(Cases.Count(c => c.Visual) >= 7);
        Assert.Contains(Cases, c => c.Note == "exact identifier; lexical must win");
        Assert.Contains(Cases, c => c.Note == "Italian paraphrase");
        Assert.Contains(Cases, c => c.Note == "English paraphrase");
    }

    private void Report(string label, DocumentVisualModeReport report)
    {
        _output.WriteLine(
            $"user-documents {label} [{report.Mode}]: Recall@5 {report.RecallAtFive:F3} "
            + $"MRR {report.MeanReciprocalRank:F3} top-3 {report.TopThreePassed}/{report.Queries} "
            + $"visual-nDCG@5 {report.VisualNdcgAtFive:F3} "
            + $"p50 {report.MedianLatencyMs}ms p95 {report.P95LatencyMs}ms");

        foreach (var outcome in report.Outcomes)
        {
            _output.WriteLine(
                $"  rank={outcome.FirstExpectedRank?.ToString() ?? "-"} "
                + $"\"{outcome.Case.Query}\" → [{string.Join(", ", outcome.TopDocuments.Take(3))}]");
        }
    }

    // ---- the pipeline under measurement ---------------------------------------

    private OwnerDocumentRetrievalPipeline Pipeline()
    {
        var ragOptions = Options.Create(new RagOptions());
        var semantic = new RagSemanticProfileResolver(RagDomainRegistry.Instance, ragOptions);
        var embeddings = new TextEmbeddingResolver(
            _harness.Db,
            new ITextEmbeddingProvider[] { new DeterministicTextEmbeddingProvider() },
            semantic);
        var serializer = new AiVectorSerializer();
        var corpus = new OwnerDocumentCorpusSource(_harness.Db);

        var retriever = new RagRetriever(
            RagDomainRegistry.Instance,
            new RagDatabaseServices(
                new DatabaseRagCorpusSource(_harness.Db),
                new RagVectorRetriever(
                    embeddings,
                    new RagVectorIndexService(_harness.Db, serializer, TimeProvider.System),
                    ragOptions),
                embeddings,
                new RagVectorIndexService(_harness.Db, serializer, TimeProvider.System),
                corpus,
                new OwnerDocumentVectorRetriever(_harness.Db, corpus, embeddings, serializer)),
            new BundledProductHelpCorpusSource(ProductHelpCorpus.Empty),
            new RagLexicalIndexCache(),
            ragOptions,
            semantic,
            NullLogger<RagRetriever>.Instance);

        var visualOptions = Options.Create(new DocumentVisualOptions { Enabled = true });
        var backends = new AiBackendResolver(
            Options.Create(new AiOptions { Enabled = true, Provider = AiProviders.Deterministic }),
            new AiProfileRegistry(_harness.Db, TimeProvider.System),
            new IAiBackend[] { new DeterministicAiBackend() });

        var visual = new OwnerDocumentVisualRetriever(
            _harness.Db,
            new DocumentVisualProfileResolver(
                backends, new AiProfileRegistry(_harness.Db, TimeProvider.System), visualOptions),
            _harness.Renderers,
            new DocumentVisualVectorIndexService(_harness.Db, serializer),
            serializer,
            visualOptions,
            new VisualLateInteractionReranker(
                _harness.Db,
                new AiProfileRegistry(_harness.Db, TimeProvider.System),
                serializer,
                visualOptions,
                NullLogger<VisualLateInteractionReranker>.Instance),
            NullLogger<OwnerDocumentVisualRetriever>.Instance);

        return new OwnerDocumentRetrievalPipeline(retriever, visual);
    }

    // ---- the synthetic corpus ---------------------------------------------------

    private void Seed()
    {
        _extraction = _harness.SeedExtractionProfile();
        _visualProfile = SeedDeterministicVisualProfile();

        // Every document from the shared corpus, with the page vectors SEEDED
        // from `LooksLike`: the fixture embeds each such question with the same
        // deterministic function the retriever will use, so page and query land
        // at the same point. That makes "this document looks like that question"
        // a controlled fact here rather than a hope about a checkpoint — and it
        // is exactly the part the Phase-0 lane replaces with a real model.
        foreach (var document in DocumentVisualGoldenCorpus.Documents)
        {
            AddFor(
                document.OwnedByB ? _harness.OwnerB : _harness.OwnerA,
                document.Name, document.Heading, document.Body, document.LooksLike);
        }

        _harness.Db.SaveChanges();
    }

    private AiProfile SeedDeterministicVisualProfile()
    {
        var model = new AiModel
        {
            Id = Guid.NewGuid(),
            Key = "document-visual-deterministic",
            Provider = AiProviders.Deterministic,
            Capability = AiCapabilities.DocumentVisualEmbedding,
            Modality = AiModalities.Multimodal,
            Dimension = DocumentVisualProfiles.DenseDimension,
            DistanceMetric = AiDistanceMetrics.Cosine,
            Enabled = true,
            CreatedAt = DateTime.UtcNow,
        };
        var profile = new AiProfile
        {
            Id = Guid.NewGuid(),
            Key = DocumentVisualProfiles.DenseSiglip2So400m,
            AiModelId = model.Id,
            Capability = AiCapabilities.DocumentVisualEmbedding,
            Modality = AiModalities.Multimodal,
            Dimension = DocumentVisualProfiles.DenseDimension,
            DistanceMetric = AiDistanceMetrics.Cosine,
            Enabled = true,
            CreatedAt = DateTime.UtcNow,
        };
        _harness.Db.AiModels.Add(model);
        _harness.Db.AiProfiles.Add(profile);
        _harness.Db.SaveChanges();
        return profile;
    }

    private void AddFor(
        Guid owner, string name, string heading, string body, IReadOnlyList<string>? looksLike)
    {
        var file = _harness.SeedFile(owner, name);

        var document = new DocumentText
        {
            Id = Guid.NewGuid(),
            FileItemId = file.Id,
            OwnerUserId = owner,
            ProfileId = _extraction.Id,
            SourceBlobObjectId = file.BlobObjectId,
            Source = DocumentTextSources.Native,
            Status = AiArtifactStatuses.Completed,
            IsCurrent = true,
            ChunkFormatVersion = OwnerDocumentChunkFormat.Current,
            Text = body,
            CharCount = body.Length,
            CreatedAt = DateTime.UtcNow,
        };
        _harness.Db.DocumentTexts.Add(document);
        _harness.Db.DocumentChunks.Add(new DocumentChunk
        {
            Id = Guid.NewGuid(),
            DocumentTextId = document.Id,
            OwnerUserId = owner,
            ProfileId = _extraction.Id,
            Ordinal = 0,
            Heading = heading,
            Text = body,
            CreatedAt = DateTime.UtcNow,
        });
        _harness.Db.SaveChanges();

        if (looksLike is null || looksLike.Count == 0) return;

        // ONE PAGE PER QUESTION IT RESEMBLES, embedded with the same
        // deterministic function the retriever's text tower uses — so the page
        // and the query land at the same point, exactly as a real multimodal
        // model would put a table page near "a table of costs".
        var vectors = looksLike
            .Select(question => new DeterministicAiBackend()
                .EmbedTextAsync(question, _visualProfile).GetAwaiter().GetResult().Vector)
            .ToArray();

        _harness.SeedVisualIndex(
            file, vectors, renderProfileKey: DocumentVisualRenderProfiles.TextCanvas,
            profileOverride: _visualProfile.Id);
    }
}
