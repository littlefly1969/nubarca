using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using NubArca.Api.Assistant;
using NubArca.Api.Data;
using NubArca.Api.Domain;
using NubArca.Api.Domain.Ai;
using NubArca.Api.Rag;
using NubArca.Api.Rag.ProductHelp;
using NubArca.Api.Tests.Assistant;
using NubArca.Api.Tests.Endpoints;
using Xunit;

namespace NubArca.Api.Tests.Help;

// The privacy boundary itself.
//
// The premise of Help is that the model learns about the PRODUCT and never about
// the LIBRARY. That claim is worth exactly as much as the bytes on the wire, so
// these tests seed unmistakable private data, ask an ordinary Help question
// through the real endpoint, and read the COMPLETE outbound HTTP body the fake
// endpoint received.
//
// A test that only inspected a DTO would pass while a later change serialized
// something extra. A test that only asserted "no sentinels" would also pass if
// the body were empty — so it asserts that approved PUBLIC text IS present,
// which is what makes the absence meaningful.
public sealed class HelpPrivacyTests : IDisposable
{
    private const string Key = "SUPER_SECRET_EXTERNAL_HELP_KEY_XYZ";

    private const string FileSentinel = "PRIVATE_FILE_SENTINEL_X91";
    private const string PersonSentinel = "PRIVATE_PERSON_SENTINEL_Q72";
    private const string AlbumSentinel = "PRIVATE_ALBUM_SENTINEL_M44";
    private const string GpsSentinel = "PRIVATE_GPS_SENTINEL_K18";
    private static readonly string[] AllSentinels =
        { FileSentinel, PersonSentinel, AlbumSentinel, GpsSentinel };

    private const string PublicMarker = "NubArca is a self-hosted personal media library";

    private readonly SqliteWebApplicationFactory _factory;
    private readonly string _corpusPath;

    public HelpPrivacyTests()
    {
        _corpusPath = WriteCorpus();

        Handler = new CapturingProviderHandler(request => _respond(request));
        _factory = new SqliteWebApplicationFactory(ExternalConfiguration(_corpusPath, Key));
        // The REAL OpenAiCompatibleTextModel stays in the pipeline — only its
        // transport is replaced — because the bytes it produces are the thing
        // under test. Swapping the whole IAssistantTextModel for a stub would
        // test the stub.
        _factory.ConfigureExtraServices = services =>
            services.AddHttpClient<IAssistantTextModel, OpenAiCompatibleTextModel>()
                .ConfigurePrimaryHttpMessageHandler(() => Handler);
        _factory.EnsureDatabaseCreated();
    }

    private CapturingProviderHandler Handler { get; }

    private Func<HttpRequestMessage, HttpResponseMessage> _respond =
        _ => CapturingProviderHandler.Answer("An album is a named collection.");

    public void Dispose()
    {
        _factory.Dispose();
        try { File.Delete(_corpusPath); } catch (IOException) { }
    }

    /// One External profile, configured the way an operator would.
    internal static Dictionary<string, string?> ExternalConfiguration(string corpusPath, string key)
        => new()
        {
            ["Assistant:Enabled"] = "true",
            ["Assistant:HelpModel"] = "help-default",
            ["Assistant:Models:help-default:Protocol"] = "OpenAiCompatible",
            ["Assistant:Models:help-default:Trust"] = "External",
            ["Assistant:Models:help-default:BaseUrl"] = "https://provider.example",
            ["Assistant:Models:help-default:ApiKey"] = key,
            ["Assistant:Models:help-default:Model"] = "test-model-1",
            ["Assistant:Models:help-default:Label"] = "Test Provider",
            ["Assistant:Help:CorpusPath"] = corpusPath,
        };

    /// A tiny approved corpus, with the same structured metadata the real
    /// builder produces — the evidence gate reads those fields, so a fixture
    /// without them would be exercising a different retriever.
    internal static string WriteCorpus()
    {
        var path = Path.Combine(Path.GetTempPath(), $"help-corpus-{Guid.NewGuid():N}.json");
        // No revision: the test host has no NUBARCA_GIT_SHA, so the gate cannot
        // compare and accepts it. The revision behaviour itself is asserted in
        // ProductHelpCorpusBoundaryTests, where the gate lives.
        var corpus = new ProductHelpCorpus(
            RagDomainKey.ProductHelp.Value, string.Empty, new[]
            {
                new ProductHelpDocument(
                    "docs/help/albums.md#1", "docs/help/albums.md", "Albums", "What an album is",
                    $"{PublicMarker}. An album is a named collection you make yourself. "
                    + "Adding a photo to an album never moves or copies the file.",
                    Feature: "albums",
                    Intent: ProductHelpVocabulary.Intent.HowTo,
                    Audience: ProductHelpVocabulary.Audience.User,
                    Language: ProductHelpVocabulary.Language.English,
                    SourceKind: ProductHelpVocabulary.SourceKind.UserGuide,
                    Aliases: new[] { "album", "albums", "raccolta", "collection" },
                    Priority: 100),
                new ProductHelpDocument(
                    "README.md#1", "README.md", "NubArca", string.Empty,
                    $"{PublicMarker} you run yourself.",
                    Feature: "nubarca",
                    Intent: ProductHelpVocabulary.Intent.Explanation,
                    Audience: ProductHelpVocabulary.Audience.User,
                    Language: ProductHelpVocabulary.Language.English,
                    SourceKind: ProductHelpVocabulary.SourceKind.FeatureCatalog,
                    Aliases: new[] { "nubarca", "overview" },
                    Priority: 70),
            });
        File.WriteAllText(path, JsonSerializer.Serialize(corpus));
        return path;
    }

    /// Real private library content, carrying sentinels in every field an
    /// over-helpful implementation might be tempted to attach.
    internal static async Task SeedPrivateLibraryAsync(
        SqliteWebApplicationFactory factory, Guid ownerId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var blobId = Guid.NewGuid();
        db.BlobObjects.Add(new BlobObject
        {
            Id = blobId, Sha256 = $"sha-{blobId:N}", SizeBytes = 1,
            StorageKey = $"sk/{blobId:N}", ReferenceCount = 1, CreatedAt = DateTime.UtcNow,
        });
        var fileId = Guid.NewGuid();
        db.FileItems.Add(new FileItem
        {
            Id = fileId, OwnerUserId = ownerId, BlobObjectId = blobId,
            Name = $"{FileSentinel}.jpg", MimeType = "image/jpeg", SizeBytes = 1,
            CreatedAt = DateTime.UtcNow, EffectiveDateTaken = DateTime.UtcNow,
        });
        db.People.Add(new Person
        {
            Id = Guid.NewGuid(), OwnerUserId = ownerId,
            DisplayName = PersonSentinel, CreatedAt = DateTime.UtcNow,
        });
        db.Albums.Add(new Album
        {
            Id = Guid.NewGuid(), OwnerUserId = ownerId,
            Name = AlbumSentinel, CreatedAt = DateTime.UtcNow,
        });
        db.BlobMetadata.Add(new BlobMetadata
        {
            Id = Guid.NewGuid(), BlobObjectId = blobId, SizeBytes = 1,
            RawMetadataJson = $"{{\"gps\":\"{GpsSentinel}\"}}",
        });
        await db.SaveChangesAsync();
    }

    /// A logged-in client; the model transport is the capturing fake.
    private async Task<(HttpClient Client, Guid OwnerId, CapturingProviderHandler Handler)> AuthenticatedAsync(
        Func<HttpRequestMessage, HttpResponseMessage>? respond = null,
        string email = "owner@example.com")
    {
        if (respond is not null) _respond = respond;
        var (ownerId, client) = await _factory.CreateAuthenticatedClientAsync(email);
        return (client, ownerId, Handler);
    }

    internal static HttpContent Question(string text) => JsonContent.Create(new
    {
        message = text,
        history = new[]
        {
            new { fromUser = true, text = "hello" },
            new { fromUser = false, text = "Hello — ask me about NubArca." },
        },
    });

    // ---- the privacy sentinel test ----------------------------------------

    [Fact]
    public async Task No_Private_Library_Data_Appears_In_The_Outbound_Provider_Request()
    {
        var (client, ownerId, handler) = await AuthenticatedAsync();
        await SeedPrivateLibraryAsync(_factory, ownerId);

        var response = await client.PostAsync("/api/help/ai/chat", Question("How do albums work?"));
        response.EnsureSuccessStatusCode();

        Assert.Equal(1, handler.Calls);
        var body = handler.Body!;

        // The actual bytes that left NubArca.
        foreach (var sentinel in AllSentinels)
        {
            Assert.DoesNotContain(sentinel, body, StringComparison.Ordinal);
        }

        // …and the request was NOT simply empty: approved public product text is
        // present, and so is the user's own question, which is the only user
        // content this feature ever sends.
        Assert.Contains(PublicMarker, body, StringComparison.Ordinal);
        Assert.Contains("How do albums work?", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_Sentinel_The_User_Types_Themselves_Is_Sent_Because_They_Typed_It()
    {
        // The honest half of the boundary. NubArca does not ATTACH private data;
        // it cannot stop someone from typing some. The UI says exactly this, and
        // a test that pretended otherwise would be describing a product that
        // does not exist.
        var (client, ownerId, handler) = await AuthenticatedAsync();
        await SeedPrivateLibraryAsync(_factory, ownerId);

        await client.PostAsync(
            "/api/help/ai/chat", Question($"How do albums work, and what is {FileSentinel}?"));

        Assert.Contains(FileSentinel, handler.Body!, StringComparison.Ordinal);
        // The ones they did NOT type are still absent.
        Assert.DoesNotContain(PersonSentinel, handler.Body!, StringComparison.Ordinal);
        Assert.DoesNotContain(AlbumSentinel, handler.Body!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_Request_Contract_Cannot_Carry_A_Private_Object_Reference()
    {
        // Fields a client might hope to smuggle context through — including a
        // `domain`, which would point Help at a retrieval domain it was not
        // meant to read. The DTO has no such properties, so they are ignored at
        // binding, and the assertion is that they reach neither the model nor
        // the answer.
        var (client, ownerId, handler) = await AuthenticatedAsync();
        await SeedPrivateLibraryAsync(_factory, ownerId);

        var smuggle = JsonContent.Create(new
        {
            message = "How do albums work?",
            fileId = Guid.NewGuid(),
            albumId = Guid.NewGuid(),
            personId = Guid.NewGuid(),
            currentMedia = FileSentinel,
            context = AlbumSentinel,
            domain = "private-library",
            url = "https://example.invalid/leak",
        });
        var response = await client.PostAsync("/api/help/ai/chat", smuggle);
        response.EnsureSuccessStatusCode();

        var body = handler.Body!;
        Assert.DoesNotContain(FileSentinel, body, StringComparison.Ordinal);
        Assert.DoesNotContain(AlbumSentinel, body, StringComparison.Ordinal);
        Assert.DoesNotContain("example.invalid", body, StringComparison.Ordinal);
        Assert.DoesNotContain("private-library", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_Client_Cannot_Override_The_Model_Or_Its_Trust()
    {
        // Which model answers, and what it may be given, is operator
        // configuration read server-side. A browser that asks for a different
        // one is answered by the configured External profile anyway.
        var (client, _, handler) = await AuthenticatedAsync();

        var response = await client.PostAsync("/api/help/ai/chat", JsonContent.Create(new
        {
            message = "How do albums work?",
            trust = "LocalTrusted",
            modelBoundary = "localTrusted",
            model = "attacker-model",
            baseUrl = "https://example.invalid",
        }));
        response.EnsureSuccessStatusCode();

        Assert.Equal(1, handler.Calls);
        Assert.Equal("https://provider.example/v1/chat/completions", handler.Url!.ToString());
        var body = JsonDocument.Parse(handler.Body!).RootElement;
        Assert.Equal("test-model-1", body.GetProperty("model").GetString());

        // And the status the browser is shown still says external.
        var status = await (await client.GetAsync("/api/help/ai/status")).Content.ReadAsStringAsync();
        Assert.Contains("\"modelBoundary\":\"external\"", status, StringComparison.Ordinal);
    }

    // ---- fail closed without approved product knowledge --------------------

    [Fact]
    public async Task No_Approved_Knowledge_Means_Zero_Outbound_Provider_Calls()
    {
        // A corpus that is simply not there. The OTHER way into this state — a
        // corpus built from a different revision — is proven at the loader in
        // ProductHelpCorpusBoundaryTests, where the revision logic lives. Both
        // reach the same service boundary (IsAvailable == false).
        var corpusPath = Path.Combine(Path.GetTempPath(), $"help-corpus-missing-{Guid.NewGuid():N}.json");
        var handler = new CapturingProviderHandler(_ => CapturingProviderHandler.Answer("unused"));

        using var factory = new SqliteWebApplicationFactory(ExternalConfiguration(corpusPath, Key));
        factory.ConfigureExtraServices = services =>
            services.AddHttpClient<IAssistantTextModel, OpenAiCompatibleTextModel>()
                .ConfigurePrimaryHttpMessageHandler(() => handler);
        factory.EnsureDatabaseCreated();

        var (_, client) = await factory.CreateAuthenticatedClientAsync();
        var response = await client.PostAsync("/api/help/ai/chat", Question("How do albums work?"));

        // An optional feature that cannot work, not an application error.
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains(AssistantFailureReasons.KnowledgeUnavailable, body, StringComparison.Ordinal);

        // THE ASSERTION THAT MATTERS. The provider is fully configured, so
        // nothing but the guard stops the call — and the user's question must
        // not leave NubArca to buy an answer improvised with no product
        // documentation behind it.
        Assert.Equal(0, handler.Calls);
        Assert.Null(handler.Body);

        // The status endpoint says the same thing, so the browser can decline to
        // offer a chat rather than discovering it on the first question…
        var status = await (await client.GetAsync("/api/help/ai/status")).Content.ReadAsStringAsync();
        Assert.Contains("\"knowledgeAvailable\":false", status, StringComparison.Ordinal);
        Assert.Contains("\"enabled\":true", status, StringComparison.Ordinal);
        // …and still without naming a path or any configuration value.
        foreach (var leak in new[] { corpusPath, Key })
        {
            Assert.DoesNotContain(leak, status, StringComparison.Ordinal);
            Assert.DoesNotContain(leak, body, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task No_Strong_Evidence_Means_Zero_Outbound_Provider_Calls()
    {
        // The corpus is healthy and answers nothing here. `Score > 0` used to be
        // enough, which bought a boundary crossing and an answer improvised from
        // three irrelevant paragraphs.
        var (client, ownerId, handler) = await AuthenticatedAsync();
        await SeedPrivateLibraryAsync(_factory, ownerId);

        var response = await client.PostAsync(
            "/api/help/ai/chat", Question("quanto costa un abbonamento mensile premium?"));
        response.EnsureSuccessStatusCode();

        Assert.Equal(0, handler.Calls);
        Assert.Null(handler.Body);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains(AssistantFailureReasons.NoSupportingKnowledge, body, StringComparison.Ordinal);
    }

    // ---- the API key -------------------------------------------------------

    [Fact]
    public async Task The_Api_Key_Appears_Only_In_The_Provider_Authorization_Header()
    {
        var (client, ownerId, handler) = await AuthenticatedAsync();
        await SeedPrivateLibraryAsync(_factory, ownerId);

        var chat = await client.PostAsync("/api/help/ai/chat", Question("How do albums work?"));
        var chatBody = await chat.Content.ReadAsStringAsync();
        var status = await client.GetAsync("/api/help/ai/status");
        var statusBody = await status.Content.ReadAsStringAsync();

        Assert.Equal($"Bearer {Key}", handler.Authorization);
        Assert.DoesNotContain(Key, handler.Body!, StringComparison.Ordinal);
        Assert.DoesNotContain(Key, chatBody, StringComparison.Ordinal);
        Assert.DoesNotContain(Key, statusBody, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_Provider_Error_Never_Carries_The_Key_Back_To_The_Browser()
    {
        var leaky = JsonSerializer.Serialize(new { error = new { message = $"rejected key {Key}" } });
        var (client, ownerId, _) = await AuthenticatedAsync(
            _ => CapturingProviderHandler.Json(HttpStatusCode.Unauthorized, leaky));
        await SeedPrivateLibraryAsync(_factory, ownerId);

        var response = await client.PostAsync("/api/help/ai/chat", Question("How do albums work?"));

        // Not a 5xx: an unusable provider is a state of an optional feature.
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain(Key, body, StringComparison.Ordinal);
        Assert.DoesNotContain("rejected key", body, StringComparison.Ordinal);
        Assert.Contains(AssistantFailureReasons.ProviderUnauthorized, body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Status_Exposes_Only_Safe_Product_Metadata()
    {
        var (client, _, _) = await AuthenticatedAsync();
        var body = await (await client.GetAsync("/api/help/ai/status")).Content.ReadAsStringAsync();

        Assert.Contains("Test Provider", body, StringComparison.Ordinal);
        Assert.Contains("\"modelBoundary\":\"external\"", body, StringComparison.Ordinal);
        foreach (var secret in new[] { Key, "provider.example", "test-model-1", "Authorization", "apiKey" })
        {
            Assert.DoesNotContain(secret, body, StringComparison.OrdinalIgnoreCase);
        }
    }
}
