using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NubArca.Api.Ai;
using NubArca.Api.Ai.DocumentVisual;
using NubArca.Api.Ai.Documents;
using NubArca.Api.Ai.Onnx;
using NubArca.Api.Domain.Ai;
using NubArca.Api.Tests.Ai.DocumentVisual;
using Xunit;
using Xunit.Abstractions;

namespace NubArca.Api.Tests.Integration;

// PHASE 0 — is a late-interaction model worth its cost, on NubArca's own corpus?
//
// The specification permits a candidate to be evaluated and NOT promoted. It
// does not permit skipping the measurement, so this is the lane that makes it.
//
// It runs in three steps, because one of them has to happen in Python:
//
//   1. `Export_Golden_Pages_For_Candidate_Embedding` renders the shared golden
//      corpus with NubArca's OWN renderer and writes the PNGs and the questions
//      to `NUBARCA_PHASE0_DIR`. The candidate must see exactly the pixels
//      production would produce, not an approximation built elsewhere.
//   2. `scripts/measure-colvision-candidate.py`, in a disposable environment,
//      embeds those exact files with the candidate and writes multi-vectors plus
//      its own resource measurements.
//   3. `Dense_Baseline_And_Late_Interaction_Over_The_Golden_Set` loads them back,
//      stores them as `late-interaction` rows, and runs NubArca's REAL pipeline
//      — the real dense pass with real SigLIP2, the real MaxSim reranker, the
//      real evidence gate — in both modes over the same cases.
//
// Step 3 is what makes this a measurement of NubArca rather than of a notebook.
// A benchmark that reimplements the pipeline measures the reimplementation.
[Collection(RealModelCollection.Name)]
[Trait("Category", "External")]
public sealed class DocumentVisualPhaseZeroTests : IDisposable
{
    private readonly ITestOutputHelper _output;
    private readonly DocumentVisualHarness _harness = new();

    public DocumentVisualPhaseZeroTests(ITestOutputHelper output)
    {
        _output = output;
    }

    public void Dispose() => _harness.Dispose();

    private static string? WorkDir
    {
        get
        {
            var dir = Environment.GetEnvironmentVariable("NUBARCA_PHASE0_DIR");
            return string.IsNullOrWhiteSpace(dir) ? null : dir;
        }
    }

    private static string? ModelDir
    {
        get
        {
            var dir = Environment.GetEnvironmentVariable("Ai__Onnx__ModelDir");
            if (string.IsNullOrWhiteSpace(dir)) return null;
            var model = Path.Combine(dir, OnnxImageModels.SiglipSo400mKey);
            return File.Exists(Path.Combine(model, OnnxImageModels.DefaultModelFile))
                   && File.Exists(Path.Combine(model, OnnxImageModels.DefaultTextModelFile))
                   && File.Exists(Path.Combine(model, OnnxImageModels.DefaultTokenizerFile))
                ? dir
                : null;
        }
    }

    // ---- step 1: the pixels a candidate must see -----------------------------

    [SkippableFact]
    public async Task Export_Golden_Pages_For_Candidate_Embedding()
    {
        var work = WorkDir;
        Skip.If(work is null, "NUBARCA_PHASE0_DIR is not set.");

        var options = new DocumentVisualOptions { Enabled = true };
        var canvas = new TextCanvasVisualRenderer(Options.Create(options));
        Skip.IfNot(canvas.CheckReadiness().Ready, "the bundled canvas font is not installed.");

        var pages = Path.Combine(work!, "pages");
        Directory.CreateDirectory(pages);

        // RENDERED BY THE PRODUCTION RENDERER, not by a script. What the
        // candidate is measured on has to be what an owner's document would
        // actually look like in this installation, down to the font.
        var manifest = new List<object>();
        foreach (var document in DocumentVisualGoldenCorpus.Documents)
        {
            var markdown = $"# {document.Heading}\n\n{document.Body}\n";
            var outcome = await canvas.RenderAsync(new DocumentVisualRenderRequest(
                Encoding.UTF8.GetBytes(markdown), DocumentFormatKind.NativeText, options));

            Assert.True(outcome.Ok, $"{document.Name}: {outcome.Reason}");
            var unit = Assert.Single(outcome.Artifact!.Units);

            var file = $"{document.Name}.png";
            await File.WriteAllBytesAsync(Path.Combine(pages, file), unit.Png);
            manifest.Add(new
            {
                document = document.Name,
                page = file,
                width = unit.Width,
                height = unit.Height,
                ownedByB = document.OwnedByB,
            });
        }

        await File.WriteAllTextAsync(
            Path.Combine(work!, "pages.json"),
            JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }));

        await File.WriteAllLinesAsync(
            Path.Combine(work!, "queries.txt"),
            DocumentVisualGoldenCorpus.Cases.Select(c => c.Query));

        _output.WriteLine(
            $"exported pages={manifest.Count} queries={DocumentVisualGoldenCorpus.Cases.Count} "
            + $"to {Path.GetFileName(work)}");
    }

    // ---- step 3: the real pipeline, both modes -------------------------------

    [SkippableFact]
    public async Task Dense_Baseline_And_Late_Interaction_Over_The_Golden_Set()
    {
        var work = WorkDir;
        var models = ModelDir;
        Skip.If(work is null, "NUBARCA_PHASE0_DIR is not set.");
        Skip.If(models is null, "Ai__Onnx__ModelDir is not set to an installed SigLIP2.");

        var vectorsPath = Path.Combine(work!, "late-vectors.json");
        var haveCandidate = File.Exists(vectorsPath);

        var siglip = SeedSiglipProfile();
        var extraction = _harness.SeedExtractionProfile();
        var files = SeedCorpus(extraction);

        // ---- the dense side, with the real paired towers ---------------------
        var ai = Options.Create(new AiOptions
        {
            Enabled = true,
            Provider = AiProviders.Onnx,
            MaxConcurrency = 1,
            TimeoutSeconds = 900,
            Onnx = new AiOnnxOptions { ModelDir = models },
        });
        var factory = new OnnxInferenceSessionFactory(
            ai, NullLogger<OnnxInferenceSessionFactory>.Instance);
        using var images = new OnnxImageEmbedder(
            ai, new OnnxImagePreprocessor(), NullLogger<OnnxImageEmbedder>.Instance, factory);

        var pages = Path.Combine(work!, "pages");
        var stopwatch = new Stopwatch();
        long pageEmbedMs = 0;
        var embedded = 0;

        foreach (var (name, file) in files)
        {
            var png = await File.ReadAllBytesAsync(Path.Combine(pages, $"{name}.png"));
            stopwatch.Restart();
            var vector = (await images.EmbedImageAsync(png, siglip)).Vector;
            pageEmbedMs += stopwatch.ElapsedMilliseconds;
            embedded++;
            _harness.SeedVisualIndex(
                file, new[] { vector },
                renderProfileKey: DocumentVisualRenderProfiles.TextCanvas,
                profileOverride: siglip.Id);
        }

        _output.WriteLine(
            $"siglip2 pages={embedded} total_embed_ms={pageEmbedMs} "
            + $"mean_ms={(embedded == 0 ? 0 : pageEmbedMs / embedded)}");

        // ---- the candidate's multi-vectors, if prepared ----------------------
        AiProfile? late = null;
        CandidateReport? report = null;
        if (haveCandidate)
        {
            // Case-INSENSITIVE on purpose: the report is written by a Python
            // script in camelCase, and the default matcher is case-sensitive —
            // which does not fail, it silently deserializes every field to its
            // default and produces a candidate with dimension zero.
            report = JsonSerializer.Deserialize<CandidateReport>(
                await File.ReadAllTextAsync(vectorsPath),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
            Assert.True(report.Dimension > 0, "the candidate report did not deserialize");
            Assert.NotEmpty(report.Pages);
            Assert.NotEmpty(report.Queries);
            late = SeedLateProfile(report.Dimension);
            LoadMultiVectors(report, late, files);

            _output.WriteLine(
                $"candidate model={report.Model} revision={report.Revision} "
                + $"license={report.License} params={report.Parameters:N0} "
                + $"weight_bytes={report.WeightBytes:N0}");
            _output.WriteLine(
                $"candidate dim={report.Dimension} vectors_per_page={report.MeanVectorsPerPage:F1} "
                + $"float32_bytes_per_page={report.MeanFloat32BytesPerPage:N0} "
                + $"peak_rss_mb={report.PeakRssMb:N0}");
            _output.WriteLine(
                $"candidate image_embed_ms={report.MeanImageMs:F0} "
                + $"query_embed_ms={report.MeanQueryMs:F0}");
        }

        // ---- both modes, through the real pipeline ---------------------------
        var dense = await EvaluateAsync(models!, ai, siglip, lateProfile: null);
        Report("dense-visual (SigLIP2)", dense);

        // DOES THE CORPUS CARRY A VISUAL SIGNAL AT ALL?
        //
        // A benchmark that cannot detect a difference is not evidence that there
        // is none, so before comparing two models the lane reports what the
        // dense pass actually discriminates: the candidate documents each
        // question produced. If every query returns the same set, the pages do
        // not differ enough for any visual model to separate them, and a "no
        // gain" result says more about the corpus than about the candidate.
        await ReportVisualDiscriminationAsync(models!, ai, siglip);

        if (late is null)
        {
            _output.WriteLine(
                "late-interaction: NO CANDIDATE PREPARED — run "
                + "scripts/measure-colvision-candidate.py against NUBARCA_PHASE0_DIR.");
            Skip.If(true, "no late-interaction candidate vectors in NUBARCA_PHASE0_DIR.");
            return;
        }

        var provider = new PrecomputedLateInteractionProvider(
            report!.Queries.ToDictionary(
                q => q.Query.Trim(),
                q => (IReadOnlyList<float[]>)q.Vectors.Select(v => v.ToArray()).ToList(),
                StringComparer.Ordinal),
            report.Dimension);

        var reranked = await EvaluateAsync(models!, ai, siglip, late, provider);
        Report("dense + late-interaction", reranked);

        // THE RERANKER ACTUALLY ENGAGED. Identical rankings are a perfectly
        // possible result — and they are also what a silently-skipped second
        // stage looks like. Without this the report could not tell "measured, no
        // difference" from "never ran", and only one of those is a measurement.
        Assert.Equal(DocumentVisualModes.LateInteraction, reranked.Mode);

        // ---- MaxSim cost, measured on the real stored vectors -----------------
        var maxSimMs = await MeasureMaxSimAsync(report!, late);
        _output.WriteLine($"maxsim_rerank_ms_per_query={maxSimMs:F1}");

        // ---- the promotion rule, applied ---------------------------------------
        var absolute = reranked.VisualNdcgAtFive - dense.VisualNdcgAtFive;
        var relative = dense.VisualNdcgAtFive > 0 ? absolute / dense.VisualNdcgAtFive : 0;
        _output.WriteLine(
            $"visual_ndcg5 dense={dense.VisualNdcgAtFive:F4} late={reranked.VisualNdcgAtFive:F4} "
            + $"absolute={absolute:+0.0000;-0.0000;0.0000} relative={relative:P1}");

        // The specification's gate: >= 10% relative nDCG@5 on visual-heavy cases,
        // OR >= 2 additional deliberately visual queries recovered in the top 5.
        var denseFound = dense.Outcomes
            .Where(o => o.Case.Visual && o.FirstExpectedRank is >= 1 and <= 5)
            .Select(o => o.Case.Query).ToHashSet(StringComparer.Ordinal);
        var lateFound = reranked.Outcomes
            .Where(o => o.Case.Visual && o.FirstExpectedRank is >= 1 and <= 5)
            .Select(o => o.Case.Query).ToHashSet(StringComparer.Ordinal);
        var recovered = lateFound.Except(denseFound).ToList();
        var lost = denseFound.Except(lateFound).ToList();

        foreach (var query in recovered) _output.WriteLine($"  recovered: {query}");
        foreach (var query in lost) _output.WriteLine($"  regressed: {query}");

        var clears = relative >= 0.10 || recovered.Count >= 2;
        _output.WriteLine(
            clears ? "PROMOTION GATE: cleared on quality" : "PROMOTION GATE: evaluated, not promoted");

        // NOT AN ASSERTION ABOUT THE MODEL. Either answer is a valid Phase-0
        // outcome and the specification says so; what would be invalid is
        // shipping the switch on without having looked. So the lane asserts that
        // the measurement RAN — both modes produced a report over every case —
        // and prints the decision for a human to act on.
        Assert.Equal(dense.Queries, reranked.Queries);
        Assert.True(dense.Queries > 0);
    }

    // ---- the pipeline under measurement ---------------------------------------

    private async Task<DocumentVisualModeReport> EvaluateAsync(
        string modelDir,
        IOptions<AiOptions> ai,
        AiProfile siglip,
        AiProfile? lateProfile,
        IVisualLateInteractionProvider? lateProvider = null)
    {
        var visual = Options.Create(new DocumentVisualOptions
        {
            Enabled = true,
            DenseProfileKey = siglip.Key,
            LateInteractionEnabled = lateProfile is not null,
            LateProfileKey = lateProfile?.Key,
            MaxLateInteractionDimension = 4_096,
            MaxVectorsPerVisualUnit = 8_192,
            MaxMultiVectorBytesPerUnit = 32 * 1024 * 1024,
        });

        var pipeline = DocumentVisualPhaseZeroPipeline.Build(
            _harness, ai, visual, modelDir, lateProvider);
        return await new DocumentVisualEvaluator(pipeline)
            .EvaluateAsync(
                _harness.OwnerA, DocumentVisualGoldenCorpus.Cases,
                useVisual: true, maxEvidence: 3);
    }

    /// MaxSim over the bounded candidate set, on the vectors actually stored —
    /// the cost the reranker adds per question, separated from the model's.
    private async Task<double> MeasureMaxSimAsync(CandidateReport report, AiProfile late)
    {
        var serializer = _harness.Serializer;
        var stored = await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions
            .ToListAsync(_harness.Db.DocumentVisualEmbeddings
                .Where(e => e.ProfileId == late.Id));

        var pages = stored
            .Select(e => MaxSim.Decode(serializer, e.EmbeddingBytes, e.VectorCount, e.Dimension))
            .Where(v => v is not null)
            .Select(v => v!)
            .ToList();

        var query = report.Queries[0].Vectors
            .Select(v => v.ToArray())
            .ToList();

        var stopwatch = Stopwatch.StartNew();
        const int runs = 20;
        for (var run = 0; run < runs; run++)
        {
            foreach (var page in pages) _ = MaxSim.Score(query, page, report.Dimension);
        }
        stopwatch.Stop();

        return (double)stopwatch.ElapsedMilliseconds / runs;
    }

    private async Task ReportVisualDiscriminationAsync(
        string modelDir, IOptions<AiOptions> ai, AiProfile siglip)
    {
        var visual = Options.Create(new DocumentVisualOptions
        {
            Enabled = true,
            DenseProfileKey = siglip.Key,
        });

        var retriever = DocumentVisualPhaseZeroRetriever.Build(_harness, ai, visual, modelDir);
        var names = _harness.Db.FileItems.ToDictionary(f => f.Id, f => f.Name);
        var sets = new HashSet<string>(StringComparer.Ordinal);

        foreach (var golden in DocumentVisualGoldenCorpus.Cases)
        {
            var result = await retriever.RetrieveAsync(
                new DocumentVisualQuery(_harness.OwnerA, golden.Query, 60, 8));
            var documents = result.CandidateFileIds
                .Select(id => names.TryGetValue(id, out var n) ? n : "?")
                .ToList();
            sets.Add(string.Join("|", documents));
            _output.WriteLine(
                $"  visual candidates for \"{golden.Query}\" [{result.Mode}]: "
                + $"[{string.Join(", ", documents.Take(4))}]");
        }

        _output.WriteLine(
            $"visual_candidate_sets_distinct={sets.Count}/{DocumentVisualGoldenCorpus.Cases.Count}");
    }

    private void Report(string label, DocumentVisualModeReport report)
    {
        _output.WriteLine(
            $"{label}: Recall@5 {report.RecallAtFive:F3} MRR {report.MeanReciprocalRank:F3} "
            + $"top-3 {report.TopThreePassed}/{report.Queries} "
            + $"visual-nDCG@5 {report.VisualNdcgAtFive:F3} "
            + $"p50 {report.MedianLatencyMs}ms p95 {report.P95LatencyMs}ms");
        foreach (var outcome in report.Outcomes)
        {
            _output.WriteLine(
                $"  rank={outcome.FirstExpectedRank?.ToString() ?? "-"} "
                + $"\"{outcome.Case.Query}\" → [{string.Join(", ", outcome.TopDocuments.Take(3))}]");
        }
    }

    // ---- fixture ---------------------------------------------------------------

    private AiProfile SeedSiglipProfile()
    {
        var model = new AiModel
        {
            Id = Guid.NewGuid(),
            Key = OnnxImageModels.SiglipSo400mKey,
            Provider = AiProviders.Onnx,
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
            ConfigHash = OnnxImageModels.SiglipSo400mKey,
            CreatedAt = DateTime.UtcNow,
        };
        _harness.Db.AiModels.Add(model);
        _harness.Db.AiProfiles.Add(profile);
        _harness.Db.SaveChanges();
        return profile;
    }

    private AiProfile SeedLateProfile(int dimension)
    {
        var model = new AiModel
        {
            Id = Guid.NewGuid(),
            Key = "colvision-candidate",
            Provider = AiProviders.Onnx,
            Capability = AiCapabilities.DocumentVisualEmbedding,
            Modality = AiModalities.Multimodal,
            Dimension = dimension,
            DistanceMetric = AiDistanceMetrics.Cosine,
            Enabled = true,
            CreatedAt = DateTime.UtcNow,
        };
        var profile = new AiProfile
        {
            Id = Guid.NewGuid(),
            Key = "document-visual-late-candidate-v1",
            AiModelId = model.Id,
            Capability = AiCapabilities.DocumentVisualEmbedding,
            Modality = AiModalities.Multimodal,
            Dimension = dimension,
            DistanceMetric = AiDistanceMetrics.Cosine,
            Enabled = true,
            CreatedAt = DateTime.UtcNow,
        };
        _harness.Db.AiModels.Add(model);
        _harness.Db.AiProfiles.Add(profile);
        _harness.Db.SaveChanges();
        return profile;
    }

    private Dictionary<string, NubArca.Api.Domain.FileItem> SeedCorpus(AiProfile extraction)
    {
        var files = new Dictionary<string, NubArca.Api.Domain.FileItem>(StringComparer.Ordinal);
        foreach (var document in DocumentVisualGoldenCorpus.Documents)
        {
            var owner = document.OwnedByB ? _harness.OwnerB : _harness.OwnerA;
            var file = _harness.SeedFile(owner, document.Name);
            _harness.SeedExtraction(file, extraction);

            var text = _harness.Db.DocumentTexts.Single(d => d.FileItemId == file.Id);
            text.Text = document.Body;
            text.CharCount = document.Body.Length;
            _harness.Db.DocumentChunks.Add(new DocumentChunk
            {
                Id = Guid.NewGuid(),
                DocumentTextId = text.Id,
                OwnerUserId = owner,
                ProfileId = extraction.Id,
                Ordinal = 0,
                Heading = document.Heading,
                Text = document.Body,
                CreatedAt = DateTime.UtcNow,
            });
            _harness.Db.SaveChanges();
            files[document.Name] = file;
        }
        return files;
    }

    /// The candidate's page vectors, stored exactly as a promoted profile's
    /// backfill would store them — canonical float32, one row per unit.
    private void LoadMultiVectors(
        CandidateReport report, AiProfile late,
        Dictionary<string, NubArca.Api.Domain.FileItem> files)
    {
        foreach (var page in report.Pages)
        {
            if (!files.TryGetValue(page.Document, out var file)) continue;

            var index = _harness.Db.DocumentVisualIndexes
                .First(i => i.FileItemId == file.Id);
            var unit = _harness.Db.DocumentVisualUnits
                .First(u => u.DocumentVisualIndexId == index.Id);

            var vectors = page.Vectors.Select(v => v.ToArray()).ToList();
            _harness.Db.DocumentVisualEmbeddings.Add(new DocumentVisualEmbedding
            {
                Id = Guid.NewGuid(),
                DocumentVisualUnitId = unit.Id,
                ProfileId = late.Id,
                Layout = DocumentVisualEmbeddingLayouts.LateInteraction,
                Dimension = report.Dimension,
                VectorCount = vectors.Count,
                EmbeddingBytes = MaxSim.Encode(_harness.Serializer, vectors, report.Dimension),
                CreatedAt = DateTime.UtcNow,
            });
        }
        _harness.Db.SaveChanges();
    }

    // ---- what the Python step writes ------------------------------------------

    internal sealed record CandidateReport(
        string Model,
        string Revision,
        string License,
        long Parameters,
        long WeightBytes,
        int Dimension,
        double MeanVectorsPerPage,
        double MeanFloat32BytesPerPage,
        double MeanImageMs,
        double MeanQueryMs,
        double PeakRssMb,
        IReadOnlyList<CandidatePage> Pages,
        IReadOnlyList<CandidateQuery> Queries);

    internal sealed record CandidatePage(string Document, IReadOnlyList<float[]> Vectors);

    internal sealed record CandidateQuery(string Query, IReadOnlyList<float[]> Vectors);
}
