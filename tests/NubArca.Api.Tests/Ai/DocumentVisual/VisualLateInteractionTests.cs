using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NubArca.Api.Ai;
using NubArca.Api.Ai.DocumentVisual;
using NubArca.Api.Domain.Ai;
using Xunit;

namespace NubArca.Api.Tests.Ai.DocumentVisual;

// THE LATE-INTERACTION SEAM, tested as a seam.
//
// No model is promoted in this release, so what these tests protect is the
// CONTRACT: a provider that returns something the profile did not declare fails
// the profile, an absent or failing provider leaves the dense order alone, and
// the reranker cannot reach a page the owner-prefiltered dense pass did not
// already surface.
//
// Nothing here asserts that late interaction improves a result. Whether a
// candidate model is worth enabling is a MEASUREMENT — see the evaluation lane —
// and a test that assumed the answer would have to be rewritten for every
// checkpoint.
public sealed class VisualLateInteractionTests : IDisposable
{
    private const int LateDimension = 8;

    private readonly DocumentVisualHarness _harness = new();
    private AiProfile _lateProfile = null!;

    public VisualLateInteractionTests()
    {
        _harness.SeedProfile();
        SeedLateProfile();
    }

    public void Dispose() => _harness.Dispose();

    private void SeedLateProfile()
    {
        var model = new AiModel
        {
            Id = Guid.NewGuid(),
            Key = "colvision-candidate-v1",
            Provider = AiProviders.Onnx,
            Capability = AiCapabilities.DocumentVisualEmbedding,
            Modality = AiModalities.Multimodal,
            Dimension = LateDimension,
            DistanceMetric = AiDistanceMetrics.Cosine,
            Enabled = true,
            CreatedAt = DateTime.UtcNow,
        };
        _lateProfile = new AiProfile
        {
            Id = Guid.NewGuid(),
            Key = "document-visual-late-candidate-v1",
            AiModelId = model.Id,
            Capability = AiCapabilities.DocumentVisualEmbedding,
            Modality = AiModalities.Multimodal,
            Dimension = LateDimension,
            DistanceMetric = AiDistanceMetrics.Cosine,
            Enabled = true,
            CreatedAt = DateTime.UtcNow,
        };
        _harness.Db.AiModels.Add(model);
        _harness.Db.AiProfiles.Add(_lateProfile);
        _harness.Db.SaveChanges();
    }

    private DocumentVisualOptions Options(bool enabled = true, string? key = null) => new()
    {
        Enabled = true,
        LateInteractionEnabled = enabled,
        LateProfileKey = key ?? _lateProfile.Key,
        MaxLateInteractionDimension = 64,
        MaxVectorsPerVisualUnit = 16,
    };

    private VisualLateInteractionReranker Reranker(
        DocumentVisualOptions options, IVisualLateInteractionProvider? provider)
        => new(
            _harness.Db,
            new AiProfileRegistry(_harness.Db, TimeProvider.System),
            _harness.Serializer,
            Microsoft.Extensions.Options.Options.Create(options),
            NullLogger<VisualLateInteractionReranker>.Instance,
            provider);

    // ---- degradation ---------------------------------------------------------

    [Fact]
    public async Task With_No_Provider_Registered_The_Dense_Order_Stands()
    {
        var candidates = await SeedTwoCandidatesAsync();

        Assert.Null(await Reranker(Options(), provider: null)
            .RerankAsync(_harness.OwnerA, "a question", candidates));
    }

    [Fact]
    public async Task With_The_Feature_Off_The_Dense_Order_Stands()
    {
        var candidates = await SeedTwoCandidatesAsync();

        Assert.Null(await Reranker(Options(enabled: false), new StubProvider())
            .RerankAsync(_harness.OwnerA, "a question", candidates));
    }

    [Fact]
    public async Task With_No_Promoted_Profile_Key_The_Dense_Order_Stands()
    {
        // The shipped state of this release.
        var candidates = await SeedTwoCandidatesAsync();

        Assert.Null(await Reranker(Options(key: ""), new StubProvider())
            .RerankAsync(_harness.OwnerA, "a question", candidates));
    }

    [Fact]
    public async Task A_Failing_Worker_Leaves_The_Dense_Order_Intact()
    {
        // An unreachable worker is never an error for the question and never a
        // verdict about a document. Null means "the dense order stands", which
        // is deliberately different from an empty list — that would delete every
        // candidate the dense pass found.
        var candidates = await SeedTwoCandidatesAsync();

        var result = await Reranker(Options(), new ThrowingProvider())
            .RerankAsync(_harness.OwnerA, "a question", candidates);

        Assert.Null(result);
    }

    [Fact]
    public async Task With_No_Stored_Multi_Vectors_The_Dense_Order_Stands()
    {
        // A promoted profile whose backfill has not run yet.
        var a = _harness.SeedFile(_harness.OwnerA, "a.pdf");
        _harness.SeedVisualIndex(a, new[] { DocumentVisualHarness.Vector(1) });
        var unit = await _harness.Db.DocumentVisualUnits.SingleAsync();

        var candidates = new List<DocumentVisualCandidate>
        {
            new(unit.Id, a.Id, 0.9),
        };

        Assert.Null(await Reranker(Options(), new StubProvider())
            .RerankAsync(_harness.OwnerA, "a question", candidates));
    }

    // ---- reranking -----------------------------------------------------------

    [Fact]
    public async Task A_Designed_Fixture_Is_Promoted_By_MaxSim()
    {
        // Dense puts the WRONG page first; the multi-vectors say otherwise. The
        // fixture is designed so the two disagree, which is the only condition
        // under which reranking is observable at all.
        var (weakUnit, strongUnit, weakFile, strongFile) = await SeedDisagreeingCandidatesAsync();

        var candidates = new List<DocumentVisualCandidate>
        {
            new(weakUnit, weakFile, 0.95),
            new(strongUnit, strongFile, 0.40),
        };

        var reranked = await Reranker(Options(), new StubProvider())
            .RerankAsync(_harness.OwnerA, "a question", candidates);

        Assert.NotNull(reranked);
        Assert.Equal(strongUnit, reranked![0].VisualUnitId);
        Assert.Equal(weakUnit, reranked[1].VisualUnitId);
    }

    [Fact]
    public async Task A_Candidate_With_No_Multi_Vector_Keeps_Its_Place_Behind_The_Reranked_Ones()
    {
        // A backfill in progress must not make half an owner's corpus vanish
        // from search.
        var (weakUnit, strongUnit, weakFile, strongFile) = await SeedDisagreeingCandidatesAsync();

        var unembedded = _harness.SeedFile(_harness.OwnerA, "unembedded.pdf");
        _harness.SeedVisualIndex(unembedded, new[] { DocumentVisualHarness.Vector(5) });
        var unembeddedUnit = await _harness.Db.DocumentVisualUnits
            .Where(u => !_harness.Db.DocumentVisualEmbeddings
                .Any(e => e.DocumentVisualUnitId == u.Id
                          && e.Layout == DocumentVisualEmbeddingLayouts.LateInteraction))
            .Select(u => u.Id)
            .Where(id => id != weakUnit && id != strongUnit)
            .FirstAsync();

        var candidates = new List<DocumentVisualCandidate>
        {
            new(weakUnit, weakFile, 0.95),
            new(unembeddedUnit, unembedded.Id, 0.80),
            new(strongUnit, strongFile, 0.40),
        };

        var reranked = await Reranker(Options(), new StubProvider())
            .RerankAsync(_harness.OwnerA, "a question", candidates);

        Assert.NotNull(reranked);
        Assert.Equal(3, reranked!.Count);
        Assert.Equal(unembeddedUnit, reranked[^1].VisualUnitId);
    }

    [Fact]
    public async Task The_Reranker_Cannot_Introduce_A_Unit_The_Dense_Pass_Did_Not_Surface()
    {
        // THE SECURITY ARGUMENT for not needing a multi-vector ANN engine: this
        // stage reorders a list the owner-prefiltered query already produced.
        // Owner B's page has a stored multi-vector and cannot appear.
        var (_, strongUnit, _, strongFile) = await SeedDisagreeingCandidatesAsync();

        var theirs = _harness.SeedFile(_harness.OwnerB, "theirs.pdf");
        _harness.SeedVisualIndex(theirs, new[] { DocumentVisualHarness.Vector(9) });
        var theirUnit = await _harness.Db.DocumentVisualUnits
            .Join(_harness.Db.DocumentVisualIndexes, u => u.DocumentVisualIndexId, i => i.Id,
                (u, i) => new { u.Id, i.FileItemId })
            .Where(x => x.FileItemId == theirs.Id)
            .Select(x => x.Id)
            .SingleAsync();
        AddMultiVector(theirUnit, Strong());

        var candidates = new List<DocumentVisualCandidate> { new(strongUnit, strongFile, 0.4) };

        var reranked = await Reranker(Options(), new StubProvider())
            .RerankAsync(_harness.OwnerA, "a question", candidates);

        Assert.NotNull(reranked);
        Assert.DoesNotContain(reranked!, c => c.VisualUnitId == theirUnit);
    }

    [Fact]
    public async Task The_Candidate_Pool_Is_Bounded()
    {
        var candidates = new List<DocumentVisualCandidate>();
        for (var i = 0; i < 12; i++)
        {
            var file = _harness.SeedFile(_harness.OwnerA, $"doc-{i}.pdf");
            _harness.SeedVisualIndex(file, new[] { DocumentVisualHarness.Vector(i) });
        }

        var units = await _harness.Db.DocumentVisualUnits
            .Join(_harness.Db.DocumentVisualIndexes, u => u.DocumentVisualIndexId, i => i.Id,
                (u, i) => new { u.Id, i.FileItemId })
            .ToListAsync();
        foreach (var unit in units)
        {
            AddMultiVector(unit.Id, Strong());
            candidates.Add(new DocumentVisualCandidate(unit.Id, unit.FileItemId, 0.5));
        }

        var options = Options();
        options.MaxMultiVectorCandidateUnits = 4;

        var reranked = await Reranker(options, new StubProvider())
            .RerankAsync(_harness.OwnerA, "a question", candidates);

        Assert.NotNull(reranked);
        Assert.Equal(4, reranked!.Count);
    }

    // ---- the output contract -------------------------------------------------

    [Theory]
    [InlineData(0)]   // no vectors
    [InlineData(999)] // past MaxVectorsPerVisualUnit
    public void A_Provider_Output_Outside_The_Declared_Layout_Fails_The_Profile(int vectorCount)
    {
        // NEVER TRUNCATED TO FIT. A MaxSim over a truncated page is a confident
        // score for a document that does not exist.
        var vectors = Enumerable.Range(0, vectorCount)
            .Select(_ => new float[LateDimension]).ToArray();

        Assert.False(VisualLateInteractionReranker.Validate(
            new MultiVectorEmbeddingResult(vectors, LateDimension, "k"), LateDimension, Options()));
    }

    [Fact]
    public void A_Provider_Output_Of_The_Wrong_Dimension_Fails_The_Profile()
    {
        Assert.False(VisualLateInteractionReranker.Validate(
            new MultiVectorEmbeddingResult(new[] { new float[4] }, 4, "k"), LateDimension, Options()));
    }

    [Fact]
    public void A_Provider_Output_With_A_NonFinite_Component_Fails_The_Profile()
    {
        var vector = new float[LateDimension];
        vector[3] = float.NaN;

        Assert.False(VisualLateInteractionReranker.Validate(
            new MultiVectorEmbeddingResult(new[] { vector }, LateDimension, "k"),
            LateDimension, Options()));
    }

    [Fact]
    public void A_Provider_Output_Past_The_Byte_Ceiling_Fails_The_Profile()
    {
        var options = Options();
        options.MaxMultiVectorBytesPerUnit = 1_024;
        options.MaxVectorsPerVisualUnit = 4_096;

        var vectors = Enumerable.Range(0, 64).Select(_ => new float[LateDimension]).ToArray();

        Assert.False(VisualLateInteractionReranker.Validate(
            new MultiVectorEmbeddingResult(vectors, LateDimension, "k"), LateDimension, options));
    }

    [Fact]
    public void A_Well_Formed_Output_Is_Accepted()
    {
        // The positive control: a validator that refuses everything is not a
        // validator, it is an outage.
        var vectors = new[] { new float[LateDimension], new float[LateDimension] };

        Assert.True(VisualLateInteractionReranker.Validate(
            new MultiVectorEmbeddingResult(vectors, LateDimension, "k"), LateDimension, Options()));
    }

    // ---- fixture -------------------------------------------------------------

    /// The query's own sequence, and the two page sequences the stub scores it
    /// against. `Strong` aligns with the query; `Weak` is near-orthogonal.
    private static float[][] QueryVectors() => new[] { Axis(0), Axis(1) };
    private static float[][] Strong() => new[] { Axis(0), Axis(1) };
    private static float[][] Weak() => new[] { Axis(4), Axis(5) };

    private static float[] Axis(int index)
    {
        var vector = new float[LateDimension];
        vector[index] = 1f;
        return vector;
    }

    private async Task<List<DocumentVisualCandidate>> SeedTwoCandidatesAsync()
    {
        var a = _harness.SeedFile(_harness.OwnerA, "a.pdf");
        var b = _harness.SeedFile(_harness.OwnerA, "b.pdf");
        _harness.SeedVisualIndex(a, new[] { DocumentVisualHarness.Vector(1) });
        _harness.SeedVisualIndex(b, new[] { DocumentVisualHarness.Vector(2) });

        var units = await _harness.Db.DocumentVisualUnits
            .Join(_harness.Db.DocumentVisualIndexes, u => u.DocumentVisualIndexId, i => i.Id,
                (u, i) => new { u.Id, i.FileItemId })
            .ToListAsync();

        return units.Select(u => new DocumentVisualCandidate(u.Id, u.FileItemId, 0.5)).ToList();
    }

    private async Task<(Guid WeakUnit, Guid StrongUnit, Guid WeakFile, Guid StrongFile)>
        SeedDisagreeingCandidatesAsync()
    {
        var weak = _harness.SeedFile(_harness.OwnerA, "dense-favourite.pdf");
        var strong = _harness.SeedFile(_harness.OwnerA, "late-favourite.pdf");
        _harness.SeedVisualIndex(weak, new[] { DocumentVisualHarness.Vector(1) });
        _harness.SeedVisualIndex(strong, new[] { DocumentVisualHarness.Vector(2) });

        var map = await _harness.Db.DocumentVisualUnits
            .Join(_harness.Db.DocumentVisualIndexes, u => u.DocumentVisualIndexId, i => i.Id,
                (u, i) => new { u.Id, i.FileItemId })
            .ToListAsync();

        var weakUnit = map.Single(x => x.FileItemId == weak.Id).Id;
        var strongUnit = map.Single(x => x.FileItemId == strong.Id).Id;

        AddMultiVector(weakUnit, Weak());
        AddMultiVector(strongUnit, Strong());

        return (weakUnit, strongUnit, weak.Id, strong.Id);
    }

    private void AddMultiVector(Guid unitId, float[][] vectors)
    {
        _harness.Db.DocumentVisualEmbeddings.Add(new DocumentVisualEmbedding
        {
            Id = Guid.NewGuid(),
            DocumentVisualUnitId = unitId,
            ProfileId = _lateProfile.Id,
            Layout = DocumentVisualEmbeddingLayouts.LateInteraction,
            Dimension = LateDimension,
            VectorCount = vectors.Length,
            EmbeddingBytes = MaxSim.Encode(_harness.Serializer, vectors, LateDimension),
            CreatedAt = DateTime.UtcNow,
        });
        _harness.Db.SaveChanges();
    }

    private sealed class StubProvider : IVisualLateInteractionProvider
    {
        public string Provider => "stub";
        public VisualProviderReadiness CheckReadiness(AiProfile profile)
            => VisualProviderReadiness.Available;
        public Task<MultiVectorEmbeddingResult> EmbedImageAsync(
            AiProfile profile, ReadOnlyMemory<byte> imageBytes, CancellationToken ct = default)
            => Task.FromResult(new MultiVectorEmbeddingResult(Strong(), LateDimension, profile.Key));
        public Task<MultiVectorEmbeddingResult> EmbedQueryAsync(
            AiProfile profile, string query, CancellationToken ct = default)
            => Task.FromResult(new MultiVectorEmbeddingResult(
                QueryVectors(), LateDimension, profile.Key));
    }

    private sealed class ThrowingProvider : IVisualLateInteractionProvider
    {
        public string Provider => "throwing";
        public VisualProviderReadiness CheckReadiness(AiProfile profile)
            => VisualProviderReadiness.Available;
        public Task<MultiVectorEmbeddingResult> EmbedImageAsync(
            AiProfile profile, ReadOnlyMemory<byte> imageBytes, CancellationToken ct = default)
            => throw new VisualLateInteractionException(DocumentVisualReasons.ModelUnavailable);
        public Task<MultiVectorEmbeddingResult> EmbedQueryAsync(
            AiProfile profile, string query, CancellationToken ct = default)
            => throw new VisualLateInteractionException(DocumentVisualReasons.ModelUnavailable);
    }
}
