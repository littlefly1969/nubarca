using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NubArca.Api.Ai;
using NubArca.Api.Ai.Backends;
using NubArca.Api.Ai.Resolution;
using NubArca.Api.Ai.DocumentVisual;
using NubArca.Api.Ai.Documents;
using NubArca.Api.Data;
using NubArca.Api.Domain;
using NubArca.Api.Domain.Ai;

namespace NubArca.Api.Tests.Ai.DocumentVisual;

/// A SQLite database holding one installation's worth of visual derivatives,
/// and the smallest set of services that can query them.
///
/// Vectors are seeded DIRECTLY rather than produced by a model, and that is
/// deliberate: these tests are about the eligibility join, the completeness
/// gate and the aggregation rule, none of which should depend on what a
/// checkpoint happens to think two pages look like. The real model has its own
/// lane (`DocumentVisualRealOnnxTests`), where semantic quality is the question.
///
/// Every fixture below leaves the DERIVED ROWS IN PLACE for files that should
/// not be reachable — vaulted, deleted, another owner's, a superseded blob.
/// That is the whole point: cleanup is housekeeping, and a boundary that only
/// holds once a sweeper has run is not a boundary.
internal sealed class DocumentVisualHarness : IDisposable
{
    public const int Dimension = DocumentVisualProfiles.DenseDimension;

    private readonly SqliteConnection _connection;

    public DocumentVisualHarness()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        Db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(_connection).Options);
        Db.Database.EnsureCreated();
        Db.SeedBuiltInRoles();

        // The two owners exist as real rows: every derived table here has a
        // foreign key to `users`, and a fixture that skipped them would be
        // testing a schema the product does not have.
        Db.Users.Add(new User
        {
            Id = OwnerA,
            Email = $"owner-a-{OwnerA:N}@example.invalid",
            DisplayName = "Owner A",
            CreatedAt = DateTime.UtcNow,
        });
        Db.Users.Add(new User
        {
            Id = OwnerB,
            Email = $"owner-b-{OwnerB:N}@example.invalid",
            DisplayName = "Owner B",
            CreatedAt = DateTime.UtcNow,
        });
        Db.SaveChanges();

        Serializer = new AiVectorSerializer();
        Renderers = new DocumentVisualRenderers(new IDocumentVisualRenderer[]
        {
            new PdfVisualRenderer(
                Options.Create(new DocumentVisualOptions()), NullLogger<PdfVisualRenderer>.Instance),
            new TextCanvasVisualRenderer(Options.Create(new DocumentVisualOptions())),
        });
    }

    public AppDbContext Db { get; }
    public IAiVectorSerializer Serializer { get; }
    public DocumentVisualRenderers Renderers { get; }
    public AiProfile Profile { get; private set; } = null!;

    public Guid OwnerA { get; } = Guid.NewGuid();
    public Guid OwnerB { get; } = Guid.NewGuid();

    public void Dispose()
    {
        Db.Dispose();
        _connection.Dispose();
    }

    // ---- seeding -------------------------------------------------------------

    public AiProfile SeedProfile(string key = DocumentVisualProfiles.DenseSiglip2So400m)
    {
        var model = new AiModel
        {
            Id = Guid.NewGuid(),
            Key = "siglip2-so400m-patch14-384",
            Provider = AiProviders.Onnx,
            Capability = AiCapabilities.ImageEmbedding,
            Modality = AiModalities.Multimodal,
            Dimension = Dimension,
            DistanceMetric = AiDistanceMetrics.Cosine,
            Enabled = true,
            CreatedAt = DateTime.UtcNow,
        };
        var profile = new AiProfile
        {
            Id = Guid.NewGuid(),
            Key = key,
            AiModelId = model.Id,
            Capability = AiCapabilities.DocumentVisualEmbedding,
            Modality = AiModalities.Multimodal,
            Dimension = Dimension,
            DistanceMetric = AiDistanceMetrics.Cosine,
            Enabled = true,
            CreatedAt = DateTime.UtcNow,
        };
        Db.AiModels.Add(model);
        Db.AiProfiles.Add(profile);
        Db.SaveChanges();
        Profile = profile;
        return profile;
    }

    public AiProfile SeedExtractionProfile()
    {
        var model = new AiModel
        {
            Id = Guid.NewGuid(),
            Key = DocumentTextSources.NativeModelKey,
            Provider = AiProviders.None,
            Capability = AiCapabilities.DocumentExtraction,
            Modality = AiModalities.Document,
            Enabled = true,
            CreatedAt = DateTime.UtcNow,
        };
        var profile = new AiProfile
        {
            Id = Guid.NewGuid(),
            Key = DocumentTextSources.NativeProfileKey,
            AiModelId = model.Id,
            Capability = AiCapabilities.DocumentExtraction,
            Modality = AiModalities.Document,
            Enabled = true,
            CreatedAt = DateTime.UtcNow,
        };
        Db.AiModels.Add(model);
        Db.AiProfiles.Add(profile);
        Db.SaveChanges();
        return profile;
    }

    public PrivateVault SeedVault(Guid owner)
    {
        var vault = new PrivateVault
        {
            Id = Guid.NewGuid(),
            OwnerUserId = owner,
            CreatedAt = DateTime.UtcNow,
        };
        Db.PrivateVaults.Add(vault);
        Db.SaveChanges();
        return vault;
    }

    public FileItem SeedFile(
        Guid owner, string name, Guid? vaultId = null, bool deleted = false,
        MediaLibraryState state = MediaLibraryState.Active)
    {
        var sha = Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N");
        var blob = new BlobObject
        {
            Id = Guid.NewGuid(),
            Sha256 = sha,
            StorageKey = $"objects/{sha[..2]}/{sha[2..4]}/{sha}",
            SizeBytes = 1024,
            ReferenceCount = 1,
            CreatedAt = DateTime.UtcNow,
        };
        Db.BlobObjects.Add(blob);

        var file = new FileItem
        {
            Id = Guid.NewGuid(),
            OwnerUserId = owner,
            BlobObjectId = blob.Id,
            Name = name,
            MimeType = "application/pdf",
            SizeBytes = 1024,
            PrivateVaultId = vaultId,
            DeletedAt = deleted ? DateTime.UtcNow : null,
            MediaLibraryState = state,
            CreatedAt = DateTime.UtcNow,
            EffectiveDateTaken = DateTime.UtcNow,
            EffectiveDateTakenSource = "uploaded",
        };
        Db.FileItems.Add(file);
        Db.SaveChanges();
        return file;
    }

    /// A visual index and its units. `status` and `blobOverride` exist so a test
    /// can create precisely the stale, partial or superseded row it wants to
    /// prove is unreachable.
    public DocumentVisualIndex SeedVisualIndex(
        FileItem file,
        float[][] unitVectors,
        string status = AiArtifactStatuses.Completed,
        string? renderProfileKey = null,
        Guid? blobOverride = null,
        Guid? profileOverride = null)
    {
        var index = new DocumentVisualIndex
        {
            Id = Guid.NewGuid(),
            FileItemId = file.Id,
            OwnerUserId = file.OwnerUserId,
            SourceBlobObjectId = blobOverride ?? file.BlobObjectId,
            RenderProfileKey = renderProfileKey ?? DocumentVisualRenderProfiles.PdfiumPage,
            EmbeddingProfileId = profileOverride ?? Profile.Id,
            Status = status,
            UnitCount = status == AiArtifactStatuses.Completed ? unitVectors.Length : 0,
            CreatedAt = DateTime.UtcNow,
            CompletedAt = status == AiArtifactStatuses.Completed ? DateTime.UtcNow : null,
        };
        Db.DocumentVisualIndexes.Add(index);

        for (var i = 0; i < unitVectors.Length; i++)
        {
            var unit = new DocumentVisualUnit
            {
                Id = Guid.NewGuid(),
                DocumentVisualIndexId = index.Id,
                Ordinal = i,
                RenderKind = DocumentVisualRenderKinds.PdfPage,
                SourceLocatorKind = DocumentLocatorKinds.Page,
                SourcePage = i + 1,
                Width = 1_240,
                Height = 1_754,
                PixelHash = Guid.NewGuid().ToString("N"),
                CreatedAt = DateTime.UtcNow,
            };
            Db.DocumentVisualUnits.Add(unit);
            Db.DocumentVisualEmbeddings.Add(new DocumentVisualEmbedding
            {
                Id = Guid.NewGuid(),
                DocumentVisualUnitId = unit.Id,
                ProfileId = profileOverride ?? Profile.Id,
                Layout = DocumentVisualEmbeddingLayouts.Dense,
                Dimension = Dimension,
                VectorCount = 1,
                EmbeddingBytes = Serializer.Serialize(unitVectors[i], Dimension),
                CreatedAt = DateTime.UtcNow,
            });
        }

        Db.SaveChanges();
        return index;
    }

    // ---- services -------------------------------------------------------------

    /// The real retriever, wired to the real profile resolver and the real
    /// backend resolver, with only the two SigLIP2 towers replaced.
    ///
    /// Faking the model and nothing else is the point: the eligibility join, the
    /// profile/capability/dimension checks, the corpus ceiling, the aggregation
    /// rule and the fallback are all the production code. What a checkpoint
    /// thinks two pages look like is a different question, answered in the real
    /// model lane.
    public OwnerDocumentVisualRetriever BuildRetriever(
        float[] queryVector, DocumentVisualOptions? options = null)
    {
        var visual = options ?? new DocumentVisualOptions { Enabled = true };
        var accessor = Microsoft.Extensions.Options.Options.Create(visual);

        var backends = new AiBackendResolver(
            Microsoft.Extensions.Options.Options.Create(new AiOptions
            {
                Enabled = true,
                Provider = AiProviders.Onnx,
            }),
            new AiProfileRegistry(Db, TimeProvider.System),
            new IAiBackend[] { new StubTower(queryVector) });

        var resolver = new DocumentVisualProfileResolver(
            backends, new AiProfileRegistry(Db, TimeProvider.System), accessor);

        return new OwnerDocumentVisualRetriever(
            Db,
            resolver,
            Renderers,
            new DocumentVisualVectorIndexService(Db, Serializer),
            Serializer,
            accessor,
            new VisualLateInteractionReranker(
                Db,
                new AiProfileRegistry(Db, TimeProvider.System),
                Serializer,
                accessor,
                NullLogger<VisualLateInteractionReranker>.Instance),
            NullLogger<OwnerDocumentVisualRetriever>.Instance);
    }

    /// Both SigLIP2 towers, stubbed. The IMAGE side is never exercised by the
    /// retriever — vectors are seeded directly — so it exists only to satisfy
    /// the resolver's "both towers or neither" rule, which is itself a
    /// production behaviour worth exercising.
    private sealed class StubTower : IImageEmbedder, ITextEmbedder
    {
        private readonly float[] _query;

        public StubTower(float[] query) => _query = query;

        public string Provider => AiProviders.Onnx;

        public bool Supports(string capability) =>
            capability is AiCapabilities.ImageEmbedding or AiCapabilities.DocumentVisualEmbedding;

        public AiBackendReadiness CheckReadiness(AiProfile profile) => AiBackendReadiness.Ready;

        public Task<AiEmbeddingResult> EmbedImageAsync(
            ReadOnlyMemory<byte> imageBytes, AiProfile profile, CancellationToken ct = default)
            => Task.FromResult(new AiEmbeddingResult(_query, Dimension, AiDistanceMetrics.Cosine));

        public Task<AiEmbeddingResult> EmbedTextAsync(
            string text, AiProfile profile, CancellationToken ct = default)
            => Task.FromResult(new AiEmbeddingResult(_query, Dimension, AiDistanceMetrics.Cosine));
    }

    /// A unit vector pointing mostly along `axis`, with a controllable amount of
    /// alignment. Two vectors built from the same axis are close; different axes
    /// are near-orthogonal.
    public static float[] Vector(int axis, float strength = 1f)
    {
        var vector = new float[Dimension];
        vector[axis % Dimension] = strength;
        vector[(axis + 1) % Dimension] = 1f - strength;
        return Normalize(vector);
    }

    private static float[] Normalize(float[] vector)
    {
        double sum = 0;
        foreach (var value in vector) sum += (double)value * value;
        var norm = Math.Sqrt(sum);
        if (norm <= 0) return vector;
        for (var i = 0; i < vector.Length; i++) vector[i] = (float)(vector[i] / norm);
        return vector;
    }
}
