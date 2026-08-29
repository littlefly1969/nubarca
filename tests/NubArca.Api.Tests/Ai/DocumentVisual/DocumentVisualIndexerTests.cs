using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NubArca.Api.Ai;
using NubArca.Api.Ai.Backends;
using NubArca.Api.Ai.DocumentVisual;
using NubArca.Api.Ai.Documents;
using NubArca.Api.Ai.Resolution;
using NubArca.Api.Domain;
using NubArca.Api.Domain.Ai;
using NubArca.Api.Storage;
using Xunit;

namespace NubArca.Api.Tests.Ai.DocumentVisual;

// THE PUBLICATION RULE, exercised against real bytes.
//
// The indexer renders and embeds page by page — one image in memory at a time —
// and that is precisely the shape in which "index what worked" is one `continue`
// away. So these tests make individual pages fail and assert that the DOCUMENT
// does not appear at all: no `Completed` index, no queryable units, and nothing
// anywhere reporting a document that reads as whole and is not.
//
// Real PDFs through the real PDFium, real storage on a real temp directory,
// with only the embedding model stubbed.
public sealed class DocumentVisualIndexerTests : IDisposable
{
    private readonly DocumentVisualHarness _harness = new();
    private readonly string _storageRoot;
    private readonly LocalFileSystemBlobStorage _storage;
    private readonly AiProfile _extraction;

    public DocumentVisualIndexerTests()
    {
        _harness.SeedProfile();
        _extraction = _harness.SeedExtractionProfile();
        _storageRoot = Path.Combine(
            Path.GetTempPath(), "nubarca-visual-indexer-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_storageRoot);
        _storage = new LocalFileSystemBlobStorage(_storageRoot, 256 * 1024 * 1024);
    }

    public void Dispose()
    {
        _harness.Dispose();
        try { Directory.Delete(_storageRoot, recursive: true); } catch (IOException) { }
    }

    // ---- the happy path ------------------------------------------------------

    [Fact]
    public async Task A_Four_Page_Pdf_Publishes_Four_Units_And_A_Completed_Index()
    {
        await SeedDocumentAsync("report.pdf", PdfFixtures.Pages(4));

        var outcome = await Indexer().IndexOwnerAsync(_harness.OwnerA);

        Assert.Equal(1, outcome.Indexed);
        Assert.Equal(4, outcome.UnitsEmbedded);
        Assert.Equal(0, outcome.Skipped);

        var index = await _harness.Db.DocumentVisualIndexes.SingleAsync();
        Assert.Equal(AiArtifactStatuses.Completed, index.Status);
        Assert.Equal(4, index.UnitCount);
        Assert.NotNull(index.CompletedAt);
        Assert.Equal(DocumentVisualRenderProfiles.PdfiumPage, index.RenderProfileKey);

        var units = await _harness.Db.DocumentVisualUnits.OrderBy(u => u.Ordinal).ToListAsync();
        Assert.Equal(new[] { 0, 1, 2, 3 }, units.Select(u => u.Ordinal).ToArray());
        Assert.Equal(new int?[] { 1, 2, 3, 4 }, units.Select(u => u.SourcePage).ToArray());
        // Every unit carries a pixel hash and no image.
        Assert.All(units, u => Assert.Equal(64, u.PixelHash.Length));

        Assert.Equal(4, await _harness.Db.DocumentVisualEmbeddings.CountAsync());
    }

    [Fact]
    public async Task A_Rendered_Page_Is_Never_Written_To_Disk()
    {
        // RENDER, EMBED, DISCARD. The blob store holds the SOURCE documents and
        // nothing else — a rendered page landing there would be a second copy of
        // somebody's paperwork with its own deletion and share-boundary problems.
        await SeedDocumentAsync("report.pdf", PdfFixtures.Pages(3));
        var before = Directory.GetFiles(_storageRoot, "*", SearchOption.AllDirectories).Length;

        await Indexer().IndexOwnerAsync(_harness.OwnerA);

        var after = Directory.GetFiles(_storageRoot, "*", SearchOption.AllDirectories);
        Assert.Equal(before, after.Length);
        Assert.DoesNotContain(after, f => f.EndsWith(".png", StringComparison.OrdinalIgnoreCase));
    }

    // ---- completeness --------------------------------------------------------

    [Fact]
    public async Task An_Embedding_Failure_On_Page_Three_Publishes_Nothing()
    {
        // The exact scenario section 58 of the specification names, with the
        // failure moved to the embedder so the renderer is not the thing under
        // test. Pages 1 and 2 embedded successfully and are DISCARDED.
        await SeedDocumentAsync("contract.pdf", PdfFixtures.Pages(5));

        var outcome = await Indexer(failOnCall: 3).IndexOwnerAsync(_harness.OwnerA);

        Assert.Equal(0, outcome.Indexed);
        Assert.Equal(1, outcome.Skipped);
        Assert.Empty(await _harness.Db.DocumentVisualIndexes.ToListAsync());
        Assert.Empty(await _harness.Db.DocumentVisualUnits.ToListAsync());
        Assert.Empty(await _harness.Db.DocumentVisualEmbeddings.ToListAsync());
    }

    [Fact]
    public async Task A_Model_Failure_Records_No_Permanent_Verdict()
    {
        // Provider unavailable is an ENVIRONMENT state, never a content failure.
        // A row saying "this document cannot be rendered" would outlive the
        // outage that caused it.
        await SeedDocumentAsync("contract.pdf", PdfFixtures.Pages(2));

        var outcome = await Indexer(failOnCall: 1).IndexOwnerAsync(_harness.OwnerA);

        Assert.Equal(1, outcome.Skipped);
        Assert.Contains(DocumentVisualReasons.ModelUnavailable, outcome.SkipReasons.Keys);
        Assert.Empty(await _harness.Db.DocumentVisualIndexes.ToListAsync());

        // And the next pass, with the model back, succeeds.
        var recovered = await Indexer().IndexOwnerAsync(_harness.OwnerA);
        Assert.Equal(1, recovered.Indexed);
    }

    [Fact]
    public async Task A_Document_Past_The_Unit_Bound_Is_Refused_And_Recorded()
    {
        await SeedDocumentAsync("huge.pdf", PdfFixtures.Pages(6));

        var outcome = await Indexer(new DocumentVisualOptions
        {
            Enabled = true,
            MaxVisualUnitsPerDocument = 4,
        }).IndexOwnerAsync(_harness.OwnerA);

        Assert.Equal(0, outcome.Indexed);
        Assert.Contains(DocumentVisualReasons.DocumentTooComplex, outcome.SkipReasons.Keys);

        // A PERMANENT verdict about the content, recorded so the next pass does
        // not spend the same CPU reaching the same refusal — and with no units.
        var index = await _harness.Db.DocumentVisualIndexes.SingleAsync();
        Assert.Equal(AiArtifactStatuses.Skipped, index.Status);
        Assert.Equal(DocumentVisualReasons.DocumentTooComplex, index.ErrorCode);
        Assert.Equal(0, index.UnitCount);
        Assert.Empty(await _harness.Db.DocumentVisualUnits.ToListAsync());
    }

    [Fact]
    public async Task A_Completed_Index_With_No_Units_Cannot_Be_Written()
    {
        // The database's own statement of the rule. An index marked done with
        // nothing in it is exactly the artefact a partial-publication bug
        // leaves, so it is a write that cannot commit rather than a convention.
        var file = _harness.SeedFile(_harness.OwnerA, "empty.pdf");
        _harness.Db.DocumentVisualIndexes.Add(new DocumentVisualIndex
        {
            Id = Guid.NewGuid(),
            FileItemId = file.Id,
            OwnerUserId = _harness.OwnerA,
            SourceBlobObjectId = file.BlobObjectId,
            RenderProfileKey = DocumentVisualRenderProfiles.PdfiumPage,
            EmbeddingProfileId = _harness.Profile.Id,
            Status = AiArtifactStatuses.Completed,
            UnitCount = 0,
            CreatedAt = DateTime.UtcNow,
        });

        await Assert.ThrowsAnyAsync<DbUpdateException>(() => _harness.Db.SaveChangesAsync());
        _harness.Db.ChangeTracker.Clear();
    }

    // ---- idempotence ---------------------------------------------------------

    [Fact]
    public async Task The_Same_Bytes_Under_The_Same_Profiles_Are_Not_Re_Rendered()
    {
        await SeedDocumentAsync("report.pdf", PdfFixtures.Pages(3));

        var first = await Indexer().IndexOwnerAsync(_harness.OwnerA);
        Assert.Equal(1, first.Indexed);

        var second = await Indexer().IndexOwnerAsync(_harness.OwnerA);
        Assert.Equal(0, second.Indexed);
        Assert.Equal(1, second.Unchanged);
        Assert.Equal(0, second.UnitsEmbedded);
    }

    [Fact]
    public async Task A_Rename_Costs_Nothing()
    {
        // Move and rename are DB-only operations that leave the
        // content-addressed blob alone, so the pixels and the vectors are still
        // correct. Re-deriving them would be hours of local inference bought by
        // renaming a folder.
        var file = await SeedDocumentAsync("report.pdf", PdfFixtures.Pages(3));
        await Indexer().IndexOwnerAsync(_harness.OwnerA);

        var tracked = await _harness.Db.FileItems.SingleAsync(f => f.Id == file.Id);
        tracked.Name = "renamed-report.pdf";
        await _harness.Db.SaveChangesAsync();

        var again = await Indexer().IndexOwnerAsync(_harness.OwnerA);
        Assert.Equal(1, again.Unchanged);
        Assert.Equal(0, again.UnitsEmbedded);
    }

    [Fact]
    public async Task A_New_Visual_Profile_Re_Embeds_Under_Its_Own_Identity()
    {
        await SeedDocumentAsync("report.pdf", PdfFixtures.Pages(2));
        await Indexer().IndexOwnerAsync(_harness.OwnerA);

        var original = _harness.Profile;
        var upgraded = _harness.SeedProfile("document-visual-siglip3-v1");

        var outcome = await Indexer(profileKey: upgraded.Key).IndexOwnerAsync(_harness.OwnerA);

        Assert.Equal(1, outcome.Indexed);
        // BOTH indexes exist, under their own profile identities. The old one is
        // not deleted and not reinterpreted; it is simply not the active one.
        Assert.Equal(2, await _harness.Db.DocumentVisualIndexes.CountAsync());
        Assert.Equal(1, await _harness.Db.DocumentVisualIndexes
            .CountAsync(i => i.EmbeddingProfileId == original.Id));
        Assert.Equal(1, await _harness.Db.DocumentVisualIndexes
            .CountAsync(i => i.EmbeddingProfileId == upgraded.Id));
    }

    [Fact]
    public async Task Replacing_A_Documents_Bytes_Produces_A_New_Index()
    {
        var file = await SeedDocumentAsync("report.pdf", PdfFixtures.Pages(2));
        await Indexer().IndexOwnerAsync(_harness.OwnerA);
        var originalBlob = file.BlobObjectId;

        await ReplaceBytesAsync(file, PdfFixtures.Pages(3, "Revised"));

        var outcome = await Indexer().IndexOwnerAsync(_harness.OwnerA);

        Assert.Equal(1, outcome.Indexed);
        var indexes = await _harness.Db.DocumentVisualIndexes.ToListAsync();
        Assert.Equal(2, indexes.Count);
        // The old index still names the old blob, which is what makes it
        // unreachable rather than wrong.
        Assert.Contains(indexes, i => i.SourceBlobObjectId == originalBlob);
        Assert.Contains(indexes, i => i.SourceBlobObjectId == file.BlobObjectId && i.UnitCount == 3);
    }

    [Fact]
    public async Task A_Document_With_No_Current_Completed_Extraction_Is_Not_Rendered()
    {
        // The visual index is a SECOND derivative of a document NubArca has
        // already decided it can read. Without a current, completed extraction
        // there is nothing for the candidate expansion to scope text retrieval
        // to, so rendering it would be pixels for a reading that is not
        // authority.
        var file = await SeedDocumentAsync("report.pdf", PdfFixtures.Pages(2));
        var document = await _harness.Db.DocumentTexts.SingleAsync(d => d.FileItemId == file.Id);
        document.IsCurrent = false;
        await _harness.Db.SaveChangesAsync();

        var outcome = await Indexer().IndexOwnerAsync(_harness.OwnerA);

        Assert.Equal(0, outcome.FilesSeen);
        Assert.Empty(await _harness.Db.DocumentVisualIndexes.ToListAsync());
    }

    [Fact]
    public async Task A_Vaulted_Document_Is_Never_Read_From_Storage()
    {
        var vault = _harness.SeedVault(_harness.OwnerA);
        var file = await SeedDocumentAsync("secret.pdf", PdfFixtures.Pages(2));
        var tracked = await _harness.Db.FileItems.SingleAsync(f => f.Id == file.Id);
        tracked.PrivateVaultId = vault.Id;
        await _harness.Db.SaveChangesAsync();

        var refusing = new RefusingBlobStorage();
        var outcome = await Indexer(storage: refusing).IndexOwnerAsync(_harness.OwnerA);

        // ORDER IS THE SECURITY PROPERTY: the storage layer was never asked.
        Assert.Equal(0, outcome.FilesSeen);
        Assert.Equal(0, refusing.Opens);
    }

    [Fact]
    public async Task The_Visual_Pass_Never_Touches_Text_Extraction_State()
    {
        await SeedDocumentAsync("report.pdf", PdfFixtures.Pages(2));
        var before = await _harness.Db.DocumentTexts
            .Select(d => new { d.Id, d.IsCurrent, d.Status, d.SourceBlobObjectId }).ToListAsync();

        await Indexer(failOnCall: 1).IndexOwnerAsync(_harness.OwnerA);

        var after = await _harness.Db.DocumentTexts
            .Select(d => new { d.Id, d.IsCurrent, d.Status, d.SourceBlobObjectId }).ToListAsync();
        Assert.Equal(before, after);
    }

    [Fact]
    public async Task An_Unavailable_Visual_Profile_Indexes_Nothing_And_Reports_Why()
    {
        await SeedDocumentAsync("report.pdf", PdfFixtures.Pages(2));

        var outcome = await Indexer(new DocumentVisualOptions { Enabled = false })
            .IndexOwnerAsync(_harness.OwnerA);

        Assert.Equal(DocumentVisualReasons.Disabled, outcome.Reason);
        Assert.Equal(0, outcome.FilesSeen);
        Assert.Empty(await _harness.Db.DocumentVisualIndexes.ToListAsync());
    }

    // ---- fixture -------------------------------------------------------------

    private OwnerDocumentVisualIndexer Indexer(
        DocumentVisualOptions? options = null,
        int? failOnCall = null,
        string? profileKey = null,
        IBlobStorage? storage = null)
    {
        var visual = options ?? new DocumentVisualOptions { Enabled = true };
        if (profileKey is not null) visual.DenseProfileKey = profileKey;
        var accessor = Options.Create(visual);

        var backends = new AiBackendResolver(
            Options.Create(new AiOptions { Enabled = true, Provider = AiProviders.Onnx }),
            new AiProfileRegistry(_harness.Db, TimeProvider.System),
            new IAiBackend[] { new CountingTower(failOnCall) });

        return new OwnerDocumentVisualIndexer(
            _harness.Db,
            storage ?? _storage,
            _harness.Renderers,
            new DocumentVisualProfileResolver(
                backends, new AiProfileRegistry(_harness.Db, TimeProvider.System), accessor),
            _harness.Serializer,
            accessor,
            Options.Create(new DocumentExtractionOptions()),
            TimeProvider.System,
            NullLogger<OwnerDocumentVisualIndexer>.Instance);
    }

    private async Task<FileItem> SeedDocumentAsync(string name, byte[] bytes)
    {
        var written = await _storage.WriteAsync(new MemoryStream(bytes));
        var blob = new BlobObject
        {
            Id = Guid.NewGuid(),
            Sha256 = written.Sha256,
            StorageKey = written.StorageKey,
            SizeBytes = bytes.Length,
            ReferenceCount = 1,
            CreatedAt = DateTime.UtcNow,
        };
        _harness.Db.BlobObjects.Add(blob);

        var file = new FileItem
        {
            Id = Guid.NewGuid(),
            OwnerUserId = _harness.OwnerA,
            BlobObjectId = blob.Id,
            Name = name,
            MimeType = "application/pdf",
            SizeBytes = bytes.Length,
            MediaLibraryState = MediaLibraryState.Active,
            CreatedAt = DateTime.UtcNow,
            EffectiveDateTaken = DateTime.UtcNow,
            EffectiveDateTakenSource = "uploaded",
        };
        _harness.Db.FileItems.Add(file);

        _harness.Db.DocumentTexts.Add(new DocumentText
        {
            Id = Guid.NewGuid(),
            FileItemId = file.Id,
            OwnerUserId = _harness.OwnerA,
            ProfileId = _extraction.Id,
            SourceBlobObjectId = blob.Id,
            Source = DocumentTextSources.Pdf,
            Status = AiArtifactStatuses.Completed,
            IsCurrent = true,
            Text = "extracted text",
            CharCount = 14,
            CreatedAt = DateTime.UtcNow,
        });

        await _harness.Db.SaveChangesAsync();
        return file;
    }

    private async Task ReplaceBytesAsync(FileItem file, byte[] bytes)
    {
        var written = await _storage.WriteAsync(new MemoryStream(bytes));
        var blob = new BlobObject
        {
            Id = Guid.NewGuid(),
            Sha256 = written.Sha256,
            StorageKey = written.StorageKey,
            SizeBytes = bytes.Length,
            ReferenceCount = 1,
            CreatedAt = DateTime.UtcNow,
        };
        _harness.Db.BlobObjects.Add(blob);

        var tracked = await _harness.Db.FileItems.SingleAsync(f => f.Id == file.Id);
        tracked.BlobObjectId = blob.Id;
        tracked.SizeBytes = bytes.Length;
        file.BlobObjectId = blob.Id;

        var document = await _harness.Db.DocumentTexts.SingleAsync(d => d.FileItemId == file.Id);
        document.SourceBlobObjectId = blob.Id;

        await _harness.Db.SaveChangesAsync();
    }

    /// A model that works until the nth call and then fails, so a failure can be
    /// placed on a specific page.
    private sealed class CountingTower : IImageEmbedder, ITextEmbedder
    {
        private readonly int? _failOnCall;
        private int _calls;

        public CountingTower(int? failOnCall) => _failOnCall = failOnCall;

        public string Provider => AiProviders.Onnx;

        public bool Supports(string capability) =>
            capability is AiCapabilities.ImageEmbedding or AiCapabilities.DocumentVisualEmbedding;

        public AiBackendReadiness CheckReadiness(AiProfile profile) => AiBackendReadiness.Ready;

        public Task<AiEmbeddingResult> EmbedImageAsync(
            ReadOnlyMemory<byte> imageBytes, AiProfile profile, CancellationToken ct = default)
        {
            _calls++;
            if (_failOnCall == _calls)
            {
                throw new InvalidOperationException("the model is unavailable");
            }

            return Task.FromResult(new AiEmbeddingResult(
                DocumentVisualHarness.Vector(_calls), DocumentVisualHarness.Dimension,
                AiDistanceMetrics.Cosine));
        }

        public Task<AiEmbeddingResult> EmbedTextAsync(
            string text, AiProfile profile, CancellationToken ct = default)
            => Task.FromResult(new AiEmbeddingResult(
                DocumentVisualHarness.Vector(0), DocumentVisualHarness.Dimension,
                AiDistanceMetrics.Cosine));
    }

    /// Storage that fails loudly if anything asks it to open a blob. The proof
    /// that ineligible documents are not read AND THEN discarded.
    private sealed class RefusingBlobStorage : IBlobStorage
    {
        public int Opens { get; private set; }

        public Task<Stream> OpenReadAsync(string storageKey, CancellationToken ct = default)
        {
            Opens++;
            throw new InvalidOperationException("an ineligible document must never be read");
        }

        public Task<BlobWriteResult> WriteAsync(Stream content, CancellationToken ct = default)
            => throw new NotSupportedException();
        public Task<bool> ExistsAsync(string storageKey, CancellationToken ct = default)
            => Task.FromResult(false);
        public Task DeleteAsync(string storageKey, CancellationToken ct = default)
            => Task.CompletedTask;
        public IAsyncEnumerable<string> EnumerateStorageKeysAsync(CancellationToken ct = default)
            => throw new NotSupportedException();
    }
}
