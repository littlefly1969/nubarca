using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NubArca.Api.Ai.Documents;
using NubArca.Api.Assistant;
using NubArca.Api.Cli;
using NubArca.Api.Data;
using NubArca.Api.Domain;
using NubArca.Api.Domain.Ai;
using NubArca.Api.Rag;
using NubArca.Api.Tests.Assistant;
using NubArca.Api.Tests.Endpoints;
using Xunit;

namespace NubArca.Api.Tests.Ai.Documents;

// THE SEARCH ISOLATION AND PROVENANCE PROOF.
//
// `POST /api/documents/search` is the first private-document surface that puts
// passages and an identifier on the wire, so it gets the same treatment as the
// chat boundary: sentinels in documents the asker is NOT entitled to — another
// owner's, a vaulted one, a deleted one, one excluded from the library — with
// their derived rows deliberately left in place, and assertions over the
// COMPLETE response body rather than a parsed subset of it.
//
// The one identifier allowed out is the caller's own FileItemId (a reviewed
// exception, docs/rag-platform.md "Search returns the owner's own file id").
// Every other id in the database is asserted absent.
public sealed class OwnerDocumentSearchTests : IAsyncLifetime
{
    private const string OwnerASentinel = "OWNER_A_SEARCH_SENTINEL";
    private const string OwnerBSentinel = "OWNER_B_SEARCH_SENTINEL";
    private const string VaultSentinel = "VAULT_SEARCH_SENTINEL";
    private const string DeletedSentinel = "DELETED_SEARCH_SENTINEL";
    private const string ExcludedSentinel = "EXCLUDED_SEARCH_SENTINEL";
    private const string SharedBody =
        "SHARED_SEARCH_BODY. Ricetta della torta di mele: farina, zucchero, uova e mele "
        + "renette tagliate a fette sottili, cottura in forno per quaranta minuti.";

    private const string Question = "ogni quanto pulire il filtro della caldaia";

    private SqliteWebApplicationFactory _factory = null!;
    private CapturingProviderHandler _handler = null!;
    private HttpClient _clientA = null!;
    private HttpClient _clientB = null!;
    private Guid _ownerA;
    private Guid _ownerB;

    // Files by role, so assertions can name exactly what must and must not appear.
    private Guid _manualA;
    private Guid _pdfA;
    private Guid _slidesA;
    private Guid _sharedA;
    private Guid _sharedB;
    private Guid _notesB;
    private Guid _vaultA;
    private Guid _deletedA;
    private Guid _excludedA;

    private readonly List<string> _forbiddenStrings = new();

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync()
    {
        _factory?.Dispose();
        return Task.CompletedTask;
    }

    private async Task BuildAsync()
    {
        _handler = new CapturingProviderHandler(
            _ => CapturingProviderHandler.Answer("Il filtro va pulito ogni sei mesi (pagina 3)."));

        // A local private model so the chat-citation test can run; search
        // itself never calls it, which the tests assert.
        _factory = new SqliteWebApplicationFactory(new Dictionary<string, string?>
        {
            ["Assistant:Enabled"] = "true",
            ["Assistant:PrivateKnowledgeModel"] = "private",
            ["Assistant:Models:private:Protocol"] = "OpenAiCompatible",
            ["Assistant:Models:private:Trust"] = "LocalTrusted",
            ["Assistant:Models:private:BaseUrl"] = "http://model.internal:11434",
            ["Assistant:Models:private:ApiKey"] = "",
            ["Assistant:Models:private:Model"] = "local-model-1",
            ["Assistant:Models:private:Label"] = "Local Model",
        });
        _factory.ConfigureExtraServices = services =>
            services.AddHttpClient<IAssistantTextModel, OpenAiCompatibleTextModel>()
                .ConfigurePrimaryHttpMessageHandler(() => _handler);
        _factory.EnsureDatabaseCreated();

        (_ownerA, _clientA) = await _factory.CreateAuthenticatedClientAsync("a@example.invalid");
        (_ownerB, _clientB) = await _factory.CreateAuthenticatedClientAsync("b@example.invalid");
        await SeedAsync();
    }

    // ---- what the owner gets ------------------------------------------------

    [Fact]
    public async Task Search_Finds_The_Owners_Document_With_Its_Own_Source_Id()
    {
        await BuildAsync();

        var (json, _) = await SearchAsync(_clientA, Question);

        Assert.Equal("ok", json.GetProperty("status").GetString());
        var first = json.GetProperty("documents")[0];
        Assert.Equal("file", first.GetProperty("sourceKind").GetString());
        Assert.Equal(_manualA, first.GetProperty("sourceId").GetGuid());
        Assert.Equal("boiler-manual.md", first.GetProperty("name").GetString());
        Assert.Equal(1, first.GetProperty("rank").GetInt32());

        var passage = first.GetProperty("passages")[0];
        Assert.Contains("sei mesi", passage.GetProperty("snippet").GetString());
        Assert.Equal("Manutenzione › Pulizia del filtro", passage.GetProperty("section").GetString());
        Assert.True(passage.GetProperty("snippet").GetString()!.Length
                    <= OwnerDocumentSearchService.MaxSnippetCharacters);
    }

    [Fact]
    public async Task Semantic_Off_Searches_Lexically_And_Says_So()
    {
        await BuildAsync();

        var (json, _) = await SearchAsync(_clientA, Question);

        Assert.Equal(RagRetrievalModes.Lexical, json.GetProperty("mode").GetString());
        Assert.False(json.GetProperty("semantic").GetBoolean());
        Assert.False(json.GetProperty("visualUsed").GetBoolean());
        Assert.Equal(JsonValueKind.Null, json.GetProperty("fallbackReason").ValueKind);
    }

    [Fact]
    public async Task Search_Never_Calls_A_Model()
    {
        await BuildAsync();

        await SearchAsync(_clientA, Question);

        Assert.Equal(0, _handler.Calls);
    }

    [Fact]
    public async Task Pdf_Page_And_Slide_Locators_Reach_The_Result()
    {
        await BuildAsync();

        var (pdf, _) = await SearchAsync(_clientA, "penale per il recesso anticipato dal contratto");
        var pdfDoc = Document(pdf, _pdfA);
        var pdfPassage = pdfDoc.GetProperty("passages")[0];
        Assert.Equal(3, pdfPassage.GetProperty("page").GetInt32());
        Assert.Equal("page", pdfPassage.GetProperty("locator").GetProperty("kind").GetString());
        Assert.Equal(3, pdfPassage.GetProperty("locator").GetProperty("index").GetInt32());

        var (slides, _) = await SearchAsync(_clientA, "roadmap del lancio trimestrale prodotto");
        var slidePassage = Document(slides, _slidesA).GetProperty("passages")[0];
        // A slide is not a page: Page stays null, the locator says slide 7.
        Assert.Equal(JsonValueKind.Null, slidePassage.GetProperty("page").ValueKind);
        Assert.Equal("slide", slidePassage.GetProperty("locator").GetProperty("kind").GetString());
        Assert.Equal(7, slidePassage.GetProperty("locator").GetProperty("index").GetInt32());
        Assert.Equal("Piano di lancio", slidePassage.GetProperty("locator").GetProperty("label").GetString());
    }

    [Fact]
    public async Task No_Evidence_And_Not_Indexed_Are_Different_Answers()
    {
        await BuildAsync();

        var (none, _) = await SearchAsync(_clientA, "capitale del Madagascar");
        Assert.Equal("no_evidence", none.GetProperty("status").GetString());
        Assert.Equal(0, none.GetProperty("documents").GetArrayLength());

        var (_, empty) = await _factory.CreateAuthenticatedClientAsync("c@example.invalid");
        var (notIndexed, _) = await SearchAsync(empty, Question);
        Assert.Equal("not_indexed", notIndexed.GetProperty("status").GetString());
    }

    // ---- isolation ----------------------------------------------------------

    [Fact]
    public async Task Nothing_The_Owner_Is_Not_Entitled_To_Appears()
    {
        await BuildAsync();

        // Every one of these documents is a match for its own query; owner B's
        // is a deliberately BETTER match for the boiler question.
        foreach (var query in new[]
                 {
                     Question, "documento riservato cassaforte filtro",
                     "appunti cancellati filtro", "appunti esclusi libreria filtro",
                 })
        {
            var (_, body) = await SearchAsync(_clientA, query);
            foreach (var sentinel in new[] { OwnerBSentinel, VaultSentinel, DeletedSentinel, ExcludedSentinel })
            {
                Assert.DoesNotContain(sentinel, body, StringComparison.Ordinal);
            }
            foreach (var id in new[] { _notesB, _sharedB, _vaultA, _deletedA, _excludedA })
            {
                Assert.DoesNotContain(id.ToString(), body, StringComparison.OrdinalIgnoreCase);
            }
        }
    }

    [Fact]
    public async Task The_Response_Carries_No_Internal_Identifier()
    {
        await BuildAsync();

        var (_, body) = await SearchAsync(_clientA, Question);

        // Chunk ids, DocumentText ids, blob ids, hashes, storage keys, owners.
        foreach (var forbidden in _forbiddenStrings)
        {
            Assert.DoesNotContain(forbidden, body, StringComparison.OrdinalIgnoreCase);
        }
        Assert.DoesNotContain(_ownerA.ToString(), body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("objects/", body, StringComparison.Ordinal);
        Assert.DoesNotContain("score", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Identical_Bytes_Held_By_Two_Owners_Answer_Each_With_Their_Own_File()
    {
        await BuildAsync();

        var (a, aBody) = await SearchAsync(_clientA, "ricetta torta di mele renette forno");
        var (b, bBody) = await SearchAsync(_clientB, "ricetta torta di mele renette forno");

        Assert.Equal(_sharedA, a.GetProperty("documents")[0].GetProperty("sourceId").GetGuid());
        Assert.Equal(_sharedB, b.GetProperty("documents")[0].GetProperty("sourceId").GetGuid());
        Assert.DoesNotContain(_sharedB.ToString(), aBody, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(_sharedA.ToString(), bBody, StringComparison.OrdinalIgnoreCase);
        // And B's own sentinel document never shows up for A, nor A's for B.
        Assert.DoesNotContain(OwnerASentinel, bBody, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Forged_Owner_Domain_And_Source_Fields_Change_Nothing()
    {
        await BuildAsync();

        var response = await _clientA.PostAsync(
            "/api/documents/search",
            new StringContent(
                $$"""
                {
                  "query": "{{Question}}",
                  "ownerUserId": "{{_ownerB}}",
                  "owner": "{{_ownerB}}",
                  "domain": "nubarca-repository",
                  "sourceKinds": ["email"],
                  "allowedSourceIds": ["{{_notesB}}"],
                  "fileItemIds": ["{{_notesB}}", "{{_vaultA}}"],
                  "fileId": "{{_notesB}}"
                }
                """,
                Encoding.UTF8, "application/json"));
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadAsStringAsync();
        var json = JsonDocument.Parse(body).RootElement;

        // Answered as owner A, over A's whole library: the forged narrowing to
        // B's file is not a field, so it neither reached B nor emptied A's result.
        Assert.Equal(_manualA, json.GetProperty("documents")[0].GetProperty("sourceId").GetGuid());
        Assert.DoesNotContain(OwnerBSentinel, body, StringComparison.Ordinal);
        Assert.DoesNotContain(_notesB.ToString(), body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void The_Request_Dto_Has_Only_Query_And_Limit()
    {
        var properties = typeof(NubArca.Api.Endpoints.OwnerDocumentSearchEndpoints.DocumentSearchRequestDto)
            .GetProperties().Select(p => p.Name).OrderBy(n => n).ToArray();

        Assert.Equal(new[] { "Limit", "Query" }, properties);
    }

    [Fact]
    public async Task Server_Side_Narrowing_Can_Only_Remove()
    {
        await BuildAsync();
        using var scope = _factory.Services.CreateScope();
        var search = scope.ServiceProvider.GetRequiredService<OwnerDocumentSearchService>();

        // Another owner's file, a vaulted and a deleted one: none is in A's
        // eligible set, so narrowing to them yields nothing — never their text.
        var forged = await search.SearchAsync(_ownerA, new OwnerDocumentSearchRequest(
            Question, AllowedSourceIds: new[] { _notesB, _vaultA, _deletedA }));
        Assert.Empty(forged.Documents);

        var own = await search.SearchAsync(_ownerA, new OwnerDocumentSearchRequest(
            Question, AllowedSourceIds: new[] { _manualA }));
        Assert.Equal(_manualA, Assert.Single(own.Documents).SourceId);

        var otherKind = await search.SearchAsync(_ownerA, new OwnerDocumentSearchRequest(
            Question, SourceKinds: new[] { "email" }));
        Assert.Empty(otherKind.Documents);
    }

    [Fact]
    public async Task Moving_Into_The_Vault_Removes_The_Result_Immediately()
    {
        await BuildAsync();
        var (before, _) = await SearchAsync(_clientA, Question);
        Assert.Equal(_manualA, before.GetProperty("documents")[0].GetProperty("sourceId").GetGuid());

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var file = await db.FileItems.SingleAsync(f => f.Id == _manualA);
            file.PrivateVaultId = await db.PrivateVaults
                .Where(v => v.OwnerUserId == _ownerA).Select(v => v.Id).FirstAsync();
            await db.SaveChangesAsync();
        }

        var (_, after) = await SearchAsync(_clientA, Question);
        Assert.DoesNotContain(_manualA.ToString(), after, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(OwnerASentinel, after, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_Unauthenticated_Caller_Gets_Nothing()
    {
        await BuildAsync();
        using var anonymous = _factory.CreateClient();

        var response = await anonymous.PostAsJsonAsync("/api/documents/search", new { query = Question });

        Assert.True(
            response.StatusCode is System.Net.HttpStatusCode.Unauthorized
                or System.Net.HttpStatusCode.Forbidden
                or System.Net.HttpStatusCode.Redirect,
            $"anonymous callers must not reach private documents (got {response.StatusCode})");
    }

    [Fact]
    public async Task An_Empty_Query_Is_A_Bad_Request()
    {
        await BuildAsync();

        var response = await _clientA.PostAsJsonAsync("/api/documents/search", new { query = "   " });

        Assert.Equal(System.Net.HttpStatusCode.BadRequest, response.StatusCode);
    }

    // ---- status and chat citations -----------------------------------------

    [Fact]
    public async Task Status_Reports_Visual_Indexing_Counts_And_Availability()
    {
        await BuildAsync();

        var response = await _clientA.GetAsync("/api/assistant/documents/status");
        var body = await response.Content.ReadAsStringAsync();
        var json = JsonDocument.Parse(body).RootElement;

        // Visual is not enabled in this host, and says why.
        Assert.False(json.GetProperty("visualEnabled").GetBoolean());
        Assert.False(string.IsNullOrEmpty(json.GetProperty("visualReason").GetString()));

        var indexing = json.GetProperty("indexing");
        // A's eligible library: manual, three fillers, pdf, slides, shared.
        Assert.Equal(7, indexing.GetProperty("candidateFiles").GetInt32());
        Assert.Equal(7, indexing.GetProperty("indexedFiles").GetInt32());
        Assert.Equal(0, indexing.GetProperty("notIndexedFiles").GetInt32());
        Assert.Equal(JsonValueKind.Null, indexing.GetProperty("embeddedChunks").ValueKind);

        var file = json.GetProperty("availability").GetProperty("kinds")[0];
        Assert.Equal("file", file.GetProperty("kind").GetString());
        Assert.True(file.GetProperty("lexical").GetProperty("available").GetBoolean());
        Assert.False(file.GetProperty("semantic").GetProperty("available").GetBoolean());
        Assert.Equal("disabled", file.GetProperty("semantic").GetProperty("reason").GetString());

        // Counts only: no file name and no id in a status.
        Assert.DoesNotContain("boiler-manual", body, StringComparison.Ordinal);
        Assert.DoesNotContain(_manualA.ToString(), body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Chat_Citations_Carry_The_Pdf_Page_And_No_Id()
    {
        await BuildAsync();

        var response = await _clientA.PostAsJsonAsync(
            "/api/assistant/documents/chat",
            new { message = "Qual è la penale per il recesso anticipato dal contratto?" });
        var body = await response.Content.ReadAsStringAsync();
        var json = JsonDocument.Parse(body).RootElement;

        Assert.True(json.GetProperty("ok").GetBoolean());
        var source = json.GetProperty("sources").EnumerateArray()
            .Single(s => s.GetProperty("document").GetString() == "contratto.pdf");
        Assert.Equal(3, source.GetProperty("page").GetInt32());
        Assert.Equal("page", source.GetProperty("locator").GetProperty("kind").GetString());

        // The prompt names the page, so the model can cite it.
        Assert.Contains("page 3", _handler.Body!, StringComparison.Ordinal);

        // Still no identifier of any kind in a chat citation.
        Assert.DoesNotContain(_pdfA.ToString(), body, StringComparison.OrdinalIgnoreCase);
        foreach (var forbidden in _forbiddenStrings)
        {
            Assert.DoesNotContain(forbidden, body, StringComparison.OrdinalIgnoreCase);
        }
    }

    // ---- the CLI ------------------------------------------------------------

    [Fact]
    public void Cli_Prints_Ranks_And_Locators_But_No_Name_Text_Or_Id()
    {
        var fileId = Guid.NewGuid();
        var result = new OwnerDocumentSearchResult(
            "ok", null, "lexical", "unavailable", false,
            new[]
            {
                new OwnerDocumentSearchHit("file", fileId, "SECRET_NAME.pdf", 1, new[]
                {
                    new OwnerDocumentSearchPassage(
                        1, "SECRET_SNIPPET text", "SECRET_HEADING", 3, "slide", 7, "SECRET_LABEL",
                        4, 1, null),
                }),
            });

        var output = new StringWriter();
        DocumentsQueryCliCommand.Print(output, result, new[] { "SECRET_NAME.pdf" }, 12);
        var text = output.ToString();

        Assert.Contains("mode=lexical", text, StringComparison.Ordinal);
        Assert.Contains("page=3", text, StringComparison.Ordinal);
        Assert.Contains("locator=slide:7", text, StringComparison.Ordinal);
        Assert.Contains("expected=true", text, StringComparison.Ordinal);
        Assert.Contains("expected_first_rank=1", text, StringComparison.Ordinal);
        Assert.DoesNotContain("SECRET", text, StringComparison.Ordinal);
        Assert.DoesNotContain(fileId.ToString(), text, StringComparison.OrdinalIgnoreCase);
    }

    // ---- fixture ------------------------------------------------------------

    private async Task<(JsonElement Json, string Body)> SearchAsync(HttpClient client, string query)
    {
        var response = await client.PostAsJsonAsync("/api/documents/search", new { query });
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadAsStringAsync();
        return (JsonDocument.Parse(body).RootElement, body);
    }

    private static JsonElement Document(JsonElement json, Guid sourceId)
        => json.GetProperty("documents").EnumerateArray()
            .Single(d => d.GetProperty("sourceId").GetGuid() == sourceId);

    private async Task SeedAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

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
        db.AiModels.Add(model);
        db.AiProfiles.Add(profile);
        var vault = new PrivateVault { Id = Guid.NewGuid(), OwnerUserId = _ownerA, CreatedAt = DateTime.UtcNow };
        db.PrivateVaults.Add(vault);
        await db.SaveChangesAsync();

        _manualA = Indexed(db, profile.Id, _ownerA, "boiler-manual.md",
            $"{OwnerASentinel}. Questo manuale della caldaia descrive la pulizia periodica del filtro. "
            + "Il filtro dell'acqua va pulito ogni sei mesi: chiudere il rubinetto di ingresso, "
            + "svitare il corpo del filtro e sciacquare la cartuccia sotto acqua corrente.",
            heading: "Manutenzione › Pulizia del filtro").File;

        // A realistic library — BM25 needs term rarity across documents.
        Indexed(db, profile.Id, _ownerA, "travel-notes.md",
            "Appunti di viaggio: il treno per Lisbona parte alle sette del mattino, l'albergo si "
            + "trova vicino alla stazione centrale e la colazione è inclusa.");
        Indexed(db, profile.Id, _ownerA, "project-notes.md",
            "Note di progetto: la riunione settimanale si tiene il martedì, le decisioni vengono "
            + "registrate nel verbale e le attività assegnate ai responsabili.");
        Indexed(db, profile.Id, _ownerA, "config-example.md",
            "Esempio di configurazione: la porta di ascolto è 8080, il livello di log è impostato "
            + "su info e la cartella dei dati viene montata come volume.");

        _pdfA = Indexed(db, profile.Id, _ownerA, "contratto.pdf",
            "Articolo 12. In caso di recesso anticipato dal contratto è dovuta una penale pari a "
            + "tre mensilità del canone, da versare entro trenta giorni.",
            heading: "Recesso", mime: "application/pdf",
            page: 3, locatorKind: DocumentLocatorKinds.Page, locatorIndex: 3).File;

        _slidesA = Indexed(db, profile.Id, _ownerA, "lancio.pptx",
            "Roadmap del lancio trimestrale del prodotto: beta a marzo, disponibilità generale a "
            + "giugno, campagna stampa coordinata con i partner.",
            heading: "Piano di lancio",
            mime: "application/vnd.openxmlformats-officedocument.presentationml.presentation",
            locatorKind: DocumentLocatorKinds.Slide, locatorIndex: 7, locatorLabel: "Piano di lancio").File;

        var sharedSha = (Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N"));
        var sharedBlob = AddBlob(db, sharedSha);
        _sharedA = Indexed(db, profile.Id, _ownerA, "ricetta.md", SharedBody, blob: sharedBlob).File;
        _sharedB = Indexed(db, profile.Id, _ownerB, "ricetta.md", SharedBody, blob: sharedBlob).File;

        _vaultA = Indexed(db, profile.Id, _ownerA, "vault-secret.md",
            $"{VaultSentinel}. Documento riservato in cassaforte: il filtro riservato va pulito ogni sei mesi.",
            vaultId: vault.Id).File;
        _deletedA = Indexed(db, profile.Id, _ownerA, "deleted-notes.md",
            $"{DeletedSentinel}. Appunti cancellati: il filtro cancellato va pulito ogni sei mesi.",
            deleted: true).File;
        _excludedA = Indexed(db, profile.Id, _ownerA, "excluded-notes.md",
            $"{ExcludedSentinel}. Appunti esclusi dalla libreria: il filtro escluso va pulito ogni sei mesi.",
            excluded: true).File;
        _notesB = Indexed(db, profile.Id, _ownerB, "private-notes.md",
            $"{OwnerBSentinel}. Pulizia del filtro della caldaia: il filtro va pulito ogni sei mesi, "
            + "filtro filtro caldaia pulizia manutenzione sei mesi.").File;

        await db.SaveChangesAsync();

        _forbiddenStrings.AddRange(await db.BlobObjects.Select(b => b.StorageKey).ToListAsync());
        _forbiddenStrings.AddRange(await db.BlobObjects.Select(b => b.Sha256).ToListAsync());
        var derivedIds = new List<Guid>();
        derivedIds.AddRange(await db.BlobObjects.Select(b => b.Id).ToListAsync());
        derivedIds.AddRange(await db.DocumentTexts.Select(d => d.Id).ToListAsync());
        derivedIds.AddRange(await db.DocumentChunks.Select(c => c.Id).ToListAsync());
        foreach (var id in derivedIds)
        {
            _forbiddenStrings.Add(id.ToString());
            _forbiddenStrings.Add(id.ToString("N"));
        }
    }

    private static BlobObject AddBlob(AppDbContext db, string sha)
    {
        var blob = new BlobObject
        {
            Id = Guid.NewGuid(),
            Sha256 = sha,
            StorageKey = $"objects/{sha[..2]}/{sha[2..4]}/{sha}",
            SizeBytes = 512,
            ReferenceCount = 1,
            CreatedAt = DateTime.UtcNow,
        };
        db.BlobObjects.Add(blob);
        return blob;
    }

    private static (Guid File, Guid Chunk) Indexed(
        AppDbContext db, Guid profileId, Guid owner, string name, string body,
        Guid? vaultId = null, bool deleted = false, bool excluded = false,
        string heading = "Contenuto", string mime = "text/markdown", BlobObject? blob = null,
        int? page = null, string? locatorKind = null, int? locatorIndex = null, string? locatorLabel = null)
    {
        blob ??= AddBlob(db, Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N"));

        var file = new FileItem
        {
            Id = Guid.NewGuid(),
            OwnerUserId = owner,
            BlobObjectId = blob.Id,
            Name = name,
            MimeType = mime,
            SizeBytes = body.Length,
            PrivateVaultId = vaultId,
            DeletedAt = deleted ? DateTime.UtcNow : null,
            MediaLibraryState = excluded ? MediaLibraryState.Excluded : MediaLibraryState.Active,
            CreatedAt = DateTime.UtcNow,
            EffectiveDateTaken = DateTime.UtcNow,
            EffectiveDateTakenSource = "uploaded",
        };
        db.FileItems.Add(file);

        var document = new DocumentText
        {
            Id = Guid.NewGuid(),
            FileItemId = file.Id,
            OwnerUserId = owner,
            ProfileId = profileId,
            SourceBlobObjectId = blob.Id,
            Source = DocumentTextSources.Native,
            Status = AiArtifactStatuses.Completed,
            IsCurrent = true,
            TextHash = new string('a', 64),
            Text = body,
            CharCount = body.Length,
            ChunkFormatVersion = OwnerDocumentChunkFormat.Current,
            CreatedAt = DateTime.UtcNow,
        };
        db.DocumentTexts.Add(document);

        var chunk = new DocumentChunk
        {
            Id = Guid.NewGuid(),
            DocumentTextId = document.Id,
            OwnerUserId = owner,
            ProfileId = profileId,
            Ordinal = 1,
            Heading = heading,
            Text = body,
            TextHash = new string('b', 64),
            Page = page,
            LocatorKind = locatorKind,
            LocatorIndex = locatorIndex,
            LocatorLabel = locatorLabel,
            CreatedAt = DateTime.UtcNow,
        };
        db.DocumentChunks.Add(chunk);
        return (file.Id, chunk.Id);
    }
}
