using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using NubArca.Api.Data;
using NubArca.Api.Domain;
using NubArca.Api.Domain.Ai;
using NubArca.Api.Help;
using NubArca.Api.Tests.Endpoints;
using Xunit;

namespace NubArca.Api.Tests.Help;

// B, C, E, F — the privacy boundary itself.
//
// The premise of external Help is that an outside model learns about the PRODUCT
// and never about the LIBRARY. That claim is worth exactly as much as the bytes
// on the wire, so these tests seed unmistakable private data, ask an ordinary
// Help question through the real endpoint, and read the COMPLETE outbound HTTP
// body the fake provider received.
//
// A test that only inspected a DTO would pass while a later change serialized
// something extra. A test that only asserted "no sentinels" would also pass if
// the body were empty — so it asserts that approved PUBLIC text IS present,
// which is what makes the absence meaningful.
public sealed class ExternalHelpPrivacyTests : IDisposable
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

    public ExternalHelpPrivacyTests()
    {
        _corpusPath = Path.Combine(Path.GetTempPath(), $"help-corpus-{Guid.NewGuid():N}.json");
        // A corpus with no revision: the running test host has no
        // NUBARCA_GIT_SHA, so the gate cannot compare and accepts it. The
        // revision behaviour itself is asserted separately below.
        var corpus = new HelpCorpus(string.Empty, new[]
        {
            new HelpCorpusDocument("README.md", "NubArca", "README.md",
                $"{PublicMarker}. Albums group photos into collections you name yourself."),
            new HelpCorpusDocument("docs/albums.md", "Albums", "docs/albums.md",
                "An album is a named collection. Adding a photo to an album never moves the file."),
        });
        File.WriteAllText(_corpusPath, JsonSerializer.Serialize(corpus));

        Handler = new CapturingProviderHandler(
            request => _respond(request));
        _factory = new SqliteWebApplicationFactory(new Dictionary<string, string?>
        {
            ["ExternalHelp:Enabled"] = "true",
            ["ExternalHelp:BaseUrl"] = "https://provider.example",
            ["ExternalHelp:ApiKey"] = Key,
            ["ExternalHelp:Model"] = "test-model-1",
            ["ExternalHelp:ProviderLabel"] = "Test Provider",
            ["ExternalHelp:CorpusPath"] = _corpusPath,
        });
        // The REAL OpenAiCompatibleChatCompletionClient stays in the pipeline —
        // only its transport is replaced — because the bytes it produces are the
        // thing under test. Swapping the whole IExternalHelpChatClient for a stub
        // would test the stub.
        _factory.ConfigureExtraServices = services =>
            services.AddHttpClient<IExternalHelpChatClient, OpenAiCompatibleChatCompletionClient>()
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

    /// Real private library content, carrying sentinels in every field an
    /// over-helpful implementation might be tempted to attach.
    private async Task SeedPrivateLibraryAsync(Guid ownerId)
    {
        using var scope = _factory.Services.CreateScope();
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

    /// A logged-in client; the Help provider transport is the capturing fake.
    private async Task<(HttpClient Client, Guid OwnerId, CapturingProviderHandler Handler)> AuthenticatedAsync(
        Func<HttpRequestMessage, HttpResponseMessage>? respond = null,
        string email = "owner@example.com")
    {
        if (respond is not null) _respond = respond;
        var (ownerId, client) = await _factory.CreateAuthenticatedClientAsync(email);
        return (client, ownerId, Handler);
    }

    private static HttpContent Question(string text) => JsonContent.Create(new
    {
        message = text,
        history = new[]
        {
            new { fromUser = true, text = "hello" },
            new { fromUser = false, text = "Hello — ask me about NubArca." },
        },
    });

    // ---- B. the privacy sentinel test -------------------------------------

    [Fact]
    public async Task No_Private_Library_Data_Appears_In_The_Outbound_Provider_Request()
    {
        var (client, ownerId, handler) = await AuthenticatedAsync();
        await SeedPrivateLibraryAsync(ownerId);

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
        await SeedPrivateLibraryAsync(ownerId);

        await client.PostAsync("/api/help/ai/chat", Question($"What is {FileSentinel}?"));

        Assert.Contains(FileSentinel, handler.Body!, StringComparison.Ordinal);
        // The ones they did NOT type are still absent.
        Assert.DoesNotContain(PersonSentinel, handler.Body!, StringComparison.Ordinal);
        Assert.DoesNotContain(AlbumSentinel, handler.Body!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_Request_Contract_Cannot_Carry_A_Private_Object_Reference()
    {
        // Fields a client might hope to smuggle context through. The DTO has no
        // such properties, so they are ignored at binding — and the assertion is
        // that they reach neither the provider nor the answer.
        var (client, ownerId, handler) = await AuthenticatedAsync();
        await SeedPrivateLibraryAsync(ownerId);

        var smuggle = JsonContent.Create(new
        {
            message = "How do albums work?",
            fileId = Guid.NewGuid(),
            albumId = Guid.NewGuid(),
            personId = Guid.NewGuid(),
            currentMedia = FileSentinel,
            context = AlbumSentinel,
            url = "https://example.invalid/leak",
        });
        var response = await client.PostAsync("/api/help/ai/chat", smuggle);
        response.EnsureSuccessStatusCode();

        var body = handler.Body!;
        Assert.DoesNotContain(FileSentinel, body, StringComparison.Ordinal);
        Assert.DoesNotContain(AlbumSentinel, body, StringComparison.Ordinal);
        Assert.DoesNotContain("example.invalid", body, StringComparison.Ordinal);
    }

    // ---- fail closed without approved product knowledge --------------------

    [Fact]
    public async Task No_Approved_Knowledge_Means_Zero_Outbound_Provider_Calls()
    {
        // A corpus that is simply not there. The OTHER way into this state — a
        // corpus built from a different revision — is proven by
        // HelpKnowledgeBoundaryTests.A_Corpus_From_A_Different_Revision_Is_Refused,
        // at the retriever, where the revision logic lives.
        //
        // It is deliberately NOT re-proven here. Doing so would mean setting
        // NUBARCA_GIT_SHA, which is process-wide, while xUnit runs test classes in
        // parallel and that other class sets the same variable — a race between
        // classes, which is exactly the intermittent failure this suite keeps
        // trying to eliminate. Both tests reach the same service boundary
        // (IsAvailable == false); only one of them needs to own the global.
        var corpusPath = Path.Combine(Path.GetTempPath(), $"help-corpus-missing-{Guid.NewGuid():N}.json");
        var handler = new CapturingProviderHandler(_ => CapturingProviderHandler.Answer("unused"));

        using var factory = new SqliteWebApplicationFactory(new Dictionary<string, string?>
        {
            ["ExternalHelp:Enabled"] = "true",
            ["ExternalHelp:BaseUrl"] = "https://provider.example",
            ["ExternalHelp:ApiKey"] = Key,
            ["ExternalHelp:Model"] = "test-model-1",
            ["ExternalHelp:ProviderLabel"] = "Test Provider",
            ["ExternalHelp:CorpusPath"] = corpusPath,
        });
        factory.ConfigureExtraServices = services =>
            services.AddHttpClient<IExternalHelpChatClient, OpenAiCompatibleChatCompletionClient>()
                .ConfigurePrimaryHttpMessageHandler(() => handler);
        factory.EnsureDatabaseCreated();

        var (_, client) = await factory.CreateAuthenticatedClientAsync();
        var response = await client.PostAsync("/api/help/ai/chat", Question("How do albums work?"));

        // An optional feature that cannot work, not an application error.
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains(HelpFailureReasons.KnowledgeUnavailable, body, StringComparison.Ordinal);

        // THE ASSERTION THAT MATTERS. The provider is fully configured, so nothing
        // but the guard stops the call — and the user's question must not leave
        // NubArca to buy an answer improvised with no product documentation
        // behind it.
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

    // ---- C. the API key -----------------------------------------------------

    [Fact]
    public async Task The_Api_Key_Appears_Only_In_The_Provider_Authorization_Header()
    {
        var (client, ownerId, handler) = await AuthenticatedAsync();
        await SeedPrivateLibraryAsync(ownerId);

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
        await SeedPrivateLibraryAsync(ownerId);

        var response = await client.PostAsync("/api/help/ai/chat", Question("How do albums work?"));

        // Not a 5xx: an unusable provider is a state of an optional feature.
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain(Key, body, StringComparison.Ordinal);
        Assert.DoesNotContain("rejected key", body, StringComparison.Ordinal);
        Assert.Contains(HelpFailureReasons.ProviderUnauthorized, body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Status_Exposes_Only_Safe_Product_Metadata()
    {
        var (client, _, _) = await AuthenticatedAsync();
        var body = await (await client.GetAsync("/api/help/ai/status")).Content.ReadAsStringAsync();

        Assert.Contains("Test Provider", body, StringComparison.Ordinal);
        foreach (var secret in new[] { Key, "provider.example", "test-model-1", "Authorization", "apiKey" })
        {
            Assert.DoesNotContain(secret, body, StringComparison.OrdinalIgnoreCase);
        }
    }
}
