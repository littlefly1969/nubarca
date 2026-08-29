using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NubArca.Api.Ai;
using NubArca.Api.Ai.Documents;
using NubArca.Api.Ai.TextEmbeddings;
using NubArca.Api.Data;
using NubArca.Api.Domain;
using NubArca.Api.Domain.Ai;
using NubArca.Api.Rag;
using NubArca.Api.Rag.Domains;
using NubArca.Api.Storage;
using Xunit;
using W = DocumentFormat.OpenXml.Wordprocessing;

namespace NubArca.Api.Tests.Ai.Documents;

// The completeness invariant where it actually matters: the DATABASE.
//
// The provider-level tests prove each parser refuses. These prove the refusal
// survives the whole pipeline — that nothing downstream quietly turns it back
// into a published document. What must never exist after a refused extraction
// is a `DocumentText` row reading Completed, the chunks of a partial reading,
// or an embedding of text that is only part of somebody's file.
//
// And the reachability half: a valid Office package larger than the native-text
// ceiling but inside its own must be indexed. Before the probe budget, the
// pre-read gate used the 4 MiB text limit for everything, so the 64 MiB Office
// ceiling could not be exercised by any document at all.
public sealed class OwnerDocumentCompletenessIntegrationTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly AppDbContext _db;
    private readonly string _storageRoot;
    private readonly LocalFileSystemBlobStorage _storage;
    private readonly Guid _owner = Guid.NewGuid();

    public OwnerDocumentCompletenessIntegrationTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(_connection).Options);
        _db.Database.EnsureCreated();
        _db.SeedBuiltInRoles();

        _storageRoot = Path.Combine(
            Path.GetTempPath(), "nubarca-complete-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_storageRoot);
        _storage = new LocalFileSystemBlobStorage(_storageRoot, 128 * 1024 * 1024);

        _db.Users.Add(new User
        {
            Id = _owner,
            Email = $"owner-{Guid.NewGuid():N}@example.invalid",
            DisplayName = "Owner",
            CreatedAt = DateTime.UtcNow,
        });
        _db.SaveChanges();
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
        try { Directory.Delete(_storageRoot, recursive: true); } catch (IOException) { }
    }

    // ---- refusal leaves nothing behind --------------------------------------

    [Fact]
    public async Task A_Document_Past_A_Bound_Leaves_No_Completed_Row_No_Chunks_No_Embeddings()
    {
        // Ten paragraphs of a hundred characters against a 999-character budget.
        SeedEmbeddingProfile();
        await AddOfficeFileAsync("contratto.docx", Docx(paragraphs: 10, size: 100));

        var outcome = await Indexer(
            new DocumentExtractionOptions { MaxCharacters = 999 },
            semantic: true).IndexOwnerAsync(_owner, embed: true);

        Assert.Equal(1, outcome.Skipped);
        Assert.Equal(1, outcome.SkipReasons[DocumentExtractionReasons.DocumentTooComplex]);

        // The verdict is recorded — these bytes earn the same answer next pass
        // rather than being re-read forever — but it is NOT a reading.
        var row = Assert.Single(await _db.DocumentTexts.ToListAsync());
        Assert.Equal(AiArtifactStatuses.Skipped, row.Status);
        Assert.Equal(DocumentExtractionReasons.DocumentTooComplex, row.ErrorCode);
        Assert.NotEqual(AiArtifactStatuses.Completed, row.Status);
        Assert.True(string.IsNullOrEmpty(row.Text), "a refused document stores no text");

        // Nothing partial survived anywhere downstream.
        Assert.Empty(await _db.DocumentChunks.ToListAsync());
        Assert.Empty(await _db.DocumentChunkEmbeddings.ToListAsync());
    }

    [Fact]
    public async Task A_Document_Past_The_Chunk_Bound_Is_Refused_Before_It_Is_Published()
    {
        // The chunk ceiling used to be discovered AFTER the row was written
        // Completed and promoted to current, so the document was published and
        // then chunked partially. Nothing is published now until chunking is
        // known to fit.
        SeedEmbeddingProfile();
        await AddOfficeFileAsync("lungo.docx", Docx(paragraphs: 40, size: 50));

        var outcome = await Indexer(
            new DocumentExtractionOptions { MaxChunks = 5, MaxCharacters = 1_000_000 },
            semantic: true).IndexOwnerAsync(_owner, embed: true);

        Assert.Equal(1, outcome.Skipped);
        Assert.Equal(1, outcome.SkipReasons[DocumentExtractionReasons.DocumentTooComplex]);

        Assert.DoesNotContain(
            await _db.DocumentTexts.ToListAsync(),
            d => d.Status == AiArtifactStatuses.Completed);
        Assert.Empty(await _db.DocumentChunks.ToListAsync());
        Assert.Empty(await _db.DocumentChunkEmbeddings.ToListAsync());
    }

    [Fact]
    public async Task The_Control_A_Normal_Document_Still_Indexes_Completely()
    {
        // The bounds must refuse the pathological document and nothing else. A
        // rule that refuses everything would pass every test above.
        SeedEmbeddingProfile();
        await AddOfficeFileAsync("normale.docx", Docx(paragraphs: 10, size: 100));

        var outcome = await Indexer(new DocumentExtractionOptions(), semantic: true)
            .IndexOwnerAsync(_owner, embed: true);

        Assert.Equal(0, outcome.Skipped);
        var row = Assert.Single(await _db.DocumentTexts.ToListAsync());
        Assert.Equal(AiArtifactStatuses.Completed, row.Status);
        Assert.True(row.IsCurrent);
        // 10 blocks of 100, joined by the canonicalizer's blank line: the
        // separators are part of the stored document, so they are counted.
        Assert.Equal(1_000 + (9 * 2), row.CharCount);

        var chunks = await _db.DocumentChunks.ToListAsync();
        Assert.NotEmpty(chunks);

        // The control that gives the refusal tests their meaning: under the SAME
        // configuration a good document does reach the embedder, so "no
        // embeddings" above is the bound working rather than embeddings being
        // switched off.
        Assert.True(outcome.EmbeddingsCreated > 0, outcome.EmbeddingReason);
        Assert.NotEmpty(await _db.DocumentChunkEmbeddings.ToListAsync());
    }

    // ---- the rich source budget is reachable --------------------------------

    [Fact]
    public async Task An_Office_Package_Over_The_Native_Ceiling_Is_Indexed()
    {
        // THE BUG THIS CLOSES. The pre-read gate compared the recorded size to
        // the native-text ceiling before the format was known, so this document
        // — a perfectly ordinary Word file carrying photographs — was refused
        // `too-large` without ever being opened, and the 64 MiB Office ceiling
        // an operator can configure was unreachable by construction.
        var options = new DocumentExtractionOptions();
        var bytes = DocxWithIncompressibleMedia(
            paragraphs: 4, size: 100, mediaBytes: 5 * 1024 * 1024);

        Assert.True(
            bytes.Length > options.EffectiveMaxSourceBytes,
            "the fixture must exceed the native-text ceiling to be the case under test");
        Assert.True(
            bytes.Length < options.EffectiveMaxOfficeSourceBytes,
            "and stay inside its own");

        await AddOfficeFileAsync("relazione.docx", bytes);

        var outcome = await Indexer(options, semantic: false).IndexOwnerAsync(_owner);

        Assert.Equal(0, outcome.Skipped);
        var row = Assert.Single(await _db.DocumentTexts.ToListAsync());
        Assert.Equal(AiArtifactStatuses.Completed, row.Status);
        Assert.Equal(DocumentTextSources.Word, row.Source);
        Assert.Equal(400 + (3 * 2), row.CharCount);
    }

    [Fact]
    public async Task An_Office_Package_Over_Its_Own_Ceiling_Is_Still_Refused()
    {
        // The budget is a budget, not an amnesty: past the Office ceiling the
        // document is refused exactly as before.
        var options = new DocumentExtractionOptions { MaxOfficeSourceBytes = 1024 * 1024 };
        var bytes = DocxWithIncompressibleMedia(
            paragraphs: 4, size: 100, mediaBytes: 5 * 1024 * 1024);

        await AddOfficeFileAsync("enorme.docx", bytes);

        var outcome = await Indexer(options, semantic: false).IndexOwnerAsync(_owner);

        Assert.Equal(1, outcome.Skipped);
        Assert.Equal(1, outcome.SkipReasons[DocumentExtractionReasons.TooLarge]);
        Assert.DoesNotContain(
            await _db.DocumentTexts.ToListAsync(),
            d => d.Status == AiArtifactStatuses.Completed);
    }

    [Fact]
    public async Task A_Blob_Larger_Than_Its_Recorded_Size_Is_Refused_Not_Truncated()
    {
        // `FileItem.SizeBytes` is recorded at upload and is the pre-read gate.
        // If it under-reports, the bounded read stops at the budget and the
        // buffer holds the document's FIRST N bytes — which must never be
        // parsed, because a cleanly-extracted prefix is the most convincing
        // possible partial document.
        var bytes = DocxWithIncompressibleMedia(
            paragraphs: 4, size: 100, mediaBytes: 2 * 1024 * 1024);
        var file = await AddOfficeFileAsync("bugiardo.docx", bytes);

        file.SizeBytes = 1_000;
        await _db.SaveChangesAsync();

        var options = new DocumentExtractionOptions { MaxOfficeSourceBytes = 4_000 };
        var outcome = await Indexer(options, semantic: false).IndexOwnerAsync(_owner);

        Assert.Equal(1, outcome.Skipped);
        Assert.Equal(1, outcome.SkipReasons[DocumentExtractionReasons.TooLarge]);
        Assert.DoesNotContain(
            await _db.DocumentTexts.ToListAsync(),
            d => d.Status == AiArtifactStatuses.Completed);
    }

    // ---- extractor-profile upgrade ------------------------------------------

    [Fact]
    public async Task A_Newer_Extraction_Profile_Re_Reads_The_Same_Blob_And_Promotes()
    {
        // THE PRODUCTION PATH, not a hand-promoted fixture. The earlier tests
        // created and promoted the second profile themselves, so nothing
        // noticed that the indexer's early exit never checked whether the
        // current row belonged to the profile now selected for its format: same
        // blob and a current chunk format were enough to skip the file, and the
        // upgrade could not happen at all in production.
        await AddOfficeFileAsync("contratto.docx", Docx(paragraphs: 3, size: 40));

        var first = await Indexer(new DocumentExtractionOptions(), semantic: false)
            .IndexOwnerAsync(_owner);
        Assert.Equal(1, first.Extracted);

        var before = Assert.Single(await _db.DocumentTexts.AsNoTracking().ToListAsync());
        Assert.True(before.IsCurrent);

        // The same bytes, read by a parser that records under a NEW profile
        // key — which is what a better Word reader shipping looks like.
        var upgraded = await Indexer(
            new DocumentExtractionOptions(), semantic: false,
            word: new RelabelledWordProvider("doc-openxml-word-v2"))
            .IndexOwnerAsync(_owner);

        Assert.Equal(1, upgraded.Extracted);

        var rows = await _db.DocumentTexts.AsNoTracking().ToListAsync();
        Assert.Equal(2, rows.Count);

        // Exactly one current row, and it is the NEW one. The old reading is
        // kept as provenance and demoted in the same save.
        var current = Assert.Single(rows, r => r.IsCurrent);
        Assert.NotEqual(before.Id, current.Id);
        Assert.Equal(AiArtifactStatuses.Completed, current.Status);
        Assert.Equal(before.SourceBlobObjectId, current.SourceBlobObjectId);

        var newProfileId = await _db.AiProfiles
            .Where(p => p.Key == "doc-openxml-word-v2").Select(p => p.Id).SingleAsync();
        Assert.Equal(newProfileId, current.ProfileId);
    }

    [Fact]
    public async Task The_Same_Active_Profile_On_The_Same_Blob_Still_Does_Nothing()
    {
        // The other half of the rule. Making the upgrade reachable must not cost
        // idempotence: an unchanged file read by the same active profile is
        // still free, which is what keeps a rename or a move from re-parsing
        // every rich document in a library.
        await AddOfficeFileAsync("contratto.docx", Docx(paragraphs: 3, size: 40));

        Assert.Equal(1, (await Indexer(new DocumentExtractionOptions(), semantic: false)
            .IndexOwnerAsync(_owner)).Extracted);

        var second = await Indexer(new DocumentExtractionOptions(), semantic: false)
            .IndexOwnerAsync(_owner);

        Assert.Equal(0, second.Extracted);
        Assert.Equal(1, second.Unchanged);
        Assert.Single(await _db.DocumentTexts.ToListAsync());
    }

    [Fact]
    public async Task A_Failing_Upgrade_Leaves_The_Working_Reading_Authoritative()
    {
        // A newly added parser that cannot read a format must not withdraw a
        // working document from its owner's corpus. The upgrade is attempted,
        // it refuses, and the valid same-blob reading keeps its authority — the
        // alternative is an upgrade that looks exactly like data loss.
        await AddOfficeFileAsync("contratto.docx", Docx(paragraphs: 3, size: 40));

        await Indexer(new DocumentExtractionOptions(), semantic: false).IndexOwnerAsync(_owner);
        var before = Assert.Single(await _db.DocumentTexts.AsNoTracking().ToListAsync());

        var outcome = await Indexer(
            new DocumentExtractionOptions(), semantic: false,
            word: new RefusingWordProvider("doc-openxml-word-v2"))
            .IndexOwnerAsync(_owner);

        Assert.Equal(0, outcome.Extracted);

        var rows = await _db.DocumentTexts.AsNoTracking().ToListAsync();
        var current = Assert.Single(rows, r => r.IsCurrent);
        Assert.Equal(before.Id, current.Id);
        Assert.Equal(AiArtifactStatuses.Completed, current.Status);
        Assert.Equal(before.TextHash, current.TextHash);
    }

    // ---- harness ------------------------------------------------------------

    /// A Word provider that reads normally but records under another profile
    /// key — what a shipped improvement to the Word reader looks like.
    private sealed class RelabelledWordProvider : IDocumentExtractionProvider
    {
        private readonly WordDocumentExtractionProvider _inner = new();

        public RelabelledWordProvider(string profileKey) => ProfileKey = profileKey;

        public DocumentFormatKind Format => DocumentFormatKind.WordOpenXml;

        public string ProfileKey { get; }

        public Task<DocumentExtractionOutcome> ExtractAsync(
            DocumentExtractionRequest request, CancellationToken cancellationToken = default)
            => _inner.ExtractAsync(request, cancellationToken);
    }

    /// A newer Word provider that cannot read the document.
    private sealed class RefusingWordProvider : IDocumentExtractionProvider
    {
        public RefusingWordProvider(string profileKey) => ProfileKey = profileKey;

        public DocumentFormatKind Format => DocumentFormatKind.WordOpenXml;

        public string ProfileKey { get; }

        public Task<DocumentExtractionOutcome> ExtractAsync(
            DocumentExtractionRequest request, CancellationToken cancellationToken = default)
            => Task.FromResult(DocumentExtractionOutcome.Rejected(
                DocumentExtractionReasons.OfficePackageInvalid));
    }

    private const string EmbeddingProfileKey = "rag-text-deterministic-v1";

    /// The owner-documents domain must be enabled EXPLICITLY — a global
    /// semantic switch deliberately does not embed somebody's private library —
    /// so a test that wants embeddings has to ask for them the way the product
    /// does.
    private OwnerDocumentIndexer Indexer(
        DocumentExtractionOptions extraction, bool semantic,
        IDocumentExtractionProvider? word = null)
    {
        var rag = Options.Create(new RagOptions
        {
            SemanticEnabled = semantic,
            TextEmbeddingProfileKey = EmbeddingProfileKey,
            Domains = semantic
                ? new Dictionary<string, RagDomainSemanticOptions>(StringComparer.OrdinalIgnoreCase)
                {
                    [RagDomains.UserDocuments] = new()
                    {
                        SemanticEnabled = true,
                        TextEmbeddingProfileKey = EmbeddingProfileKey,
                    },
                }
                : new Dictionary<string, RagDomainSemanticOptions>(StringComparer.OrdinalIgnoreCase),
        });

        return new OwnerDocumentIndexer(
            _db,
            _storage,
            new TextEmbeddingResolver(
                _db,
                new ITextEmbeddingProvider[] { new DeterministicTextEmbeddingProvider() },
                new RagSemanticProfileResolver(RagDomainRegistry.Instance, rag)),
            new AiVectorSerializer(),
            new DocumentExtractionProviders(
                new[]
                {
                    (IDocumentExtractionProvider)new NativeTextExtractionProvider(),
                    word ?? new WordDocumentExtractionProvider(),
                    new SpreadsheetExtractionProvider(),
                    new PresentationExtractionProvider(),
                }),
            Options.Create(extraction),
            TimeProvider.System,
            NullLogger<OwnerDocumentIndexer>.Instance);
    }

    private void SeedEmbeddingProfile()
    {
        var model = new AiModel
        {
            Id = Guid.NewGuid(),
            Key = EmbeddingProfileKey + "-model",
            Provider = AiProviders.Deterministic,
            Capability = AiCapabilities.TextEmbedding,
            Modality = AiModalities.Text,
            Dimension = DeterministicTextEmbeddingProvider.Dimension,
            DistanceMetric = AiDistanceMetrics.Cosine,
            Enabled = true,
            CreatedAt = DateTime.UtcNow,
        };
        _db.AiModels.Add(model);
        _db.AiProfiles.Add(new AiProfile
        {
            Id = Guid.NewGuid(),
            Key = EmbeddingProfileKey,
            AiModelId = model.Id,
            Capability = AiCapabilities.TextEmbedding,
            Modality = AiModalities.Text,
            Dimension = DeterministicTextEmbeddingProvider.Dimension,
            DistanceMetric = AiDistanceMetrics.Cosine,
            Enabled = true,
            CreatedAt = DateTime.UtcNow,
        });
        _db.SaveChanges();
    }

    private async Task<FileItem> AddOfficeFileAsync(string name, byte[] bytes)
    {
        using var stream = new MemoryStream(bytes);
        var written = await _storage.WriteAsync(stream);

        var blob = new BlobObject
        {
            Id = Guid.NewGuid(),
            Sha256 = written.Sha256,
            StorageKey = written.StorageKey,
            SizeBytes = written.SizeBytes,
            CreatedAt = DateTime.UtcNow,
        };
        _db.BlobObjects.Add(blob);

        var file = new FileItem
        {
            Id = Guid.NewGuid(),
            OwnerUserId = _owner,
            BlobObjectId = blob.Id,
            Name = name,
            MimeType = DocumentFormatProbe.WordMimeType,
            SizeBytes = written.SizeBytes,
            CreatedAt = DateTime.UtcNow,
            EffectiveDateTaken = DateTime.UtcNow,
            EffectiveDateTakenSource = "uploaded",
        };
        _db.FileItems.Add(file);
        await _db.SaveChangesAsync();
        return file;
    }

    private static byte[] Docx(int paragraphs, int size)
        => DocxWithIncompressibleMedia(paragraphs, size, mediaBytes: 0);

    /// A DOCX whose text is small and whose PACKAGE is large.
    ///
    /// The media is cryptographically random precisely so it does not compress:
    /// a package padded with zeroes would zip down to nothing and could never
    /// exceed a source ceiling, which is the thing under test. This is also what
    /// a real document looks like — a report with photographs in it is mostly
    /// photographs by weight and contributes none of them to extraction.
    private static byte[] DocxWithIncompressibleMedia(int paragraphs, int size, int mediaBytes)
    {
        using var buffer = new MemoryStream();
        using (var document = WordprocessingDocument.Create(
                   buffer, WordprocessingDocumentType.Document, autoSave: true))
        {
            var main = document.AddMainDocumentPart();
            main.Document = new Document();
            var body = main.Document.AppendChild(new Body());

            for (var i = 0; i < paragraphs; i++)
            {
                var paragraph = new Paragraph();
                paragraph.AppendChild(new W.Run(
                    new W.Text(new string('a', size)) { Space = SpaceProcessingModeValues.Preserve }));
                body.AppendChild(paragraph);
            }

            if (mediaBytes > 0)
            {
                var payload = new byte[mediaBytes];
                System.Security.Cryptography.RandomNumberGenerator.Fill(payload);
                var image = main.AddImagePart(ImagePartType.Png);
                using var media = new MemoryStream(payload);
                image.FeedData(media);
            }
        }

        return buffer.ToArray();
    }
}
