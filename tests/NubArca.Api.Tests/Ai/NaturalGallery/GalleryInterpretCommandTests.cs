using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using NubArca.Api.Data;
using NubArca.Api.Domain.Ai;
using NubArca.Api.Tests.Endpoints;

namespace NubArca.Api.Tests.Ai.NaturalGallery;

// The AUTHENTICATED owner web NL endpoint (POST /api/images/interpret-command).
// It reuses the SAME interpreter service as the TV endpoint but is gated by the
// normal owner session — no TV session, no unlock grant. Covers auth, no-store,
// owner-scoped (no cross-owner) person resolution, ambiguity, and that the owner
// gallery GET routes ?semanticQuery physical-first.
public sealed class GalleryInterpretCommandTests : IDisposable
{
    private const string Url = "/api/images/interpret-command";

    private readonly SqliteWebApplicationFactory _factory = new();

    public GalleryInterpretCommandTests() => _factory.EnsureDatabaseCreated();
    public void Dispose() => _factory.Dispose();

    private static object Request(string command) => new
    {
        command,
        locale = "it-IT",
        timeZone = "Europe/Rome",
        currentDate = "2026-07-12T12:00:00Z",
        currentFilters = new { },
    };

    private async Task SeedPersonAsync(Guid ownerUserId, string name)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        db.People.Add(new Person
        {
            Id = Guid.NewGuid(), OwnerUserId = ownerUserId, DisplayName = name,
            IsArchived = false, CreatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task Unauthenticated_Is_Denied()
    {
        var anon = _factory.CreateClient();
        var response = await anon.PostAsJsonAsync(Url, Request("Mostrami le preferite"));
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Authenticated_Owner_Gets_A_Draft_No_Store()
    {
        var (_, client) = await _factory.CreateAuthenticatedClientAsync();
        var response = await client.PostAsJsonAsync(Url, Request("Mostrami solo le preferite"));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("no-store", response.Headers.CacheControl?.ToString() ?? "");

        var root = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        var draft = root.GetProperty("draft");
        Assert.Equal("replace", draft.GetProperty("operation").GetString());
        Assert.True(draft.GetProperty("favorite").GetBoolean());
        Assert.False(root.GetProperty("requiresClarification").GetBoolean());
    }

    [Fact]
    public async Task Person_Resolution_Is_Owner_Scoped()
    {
        var (ownerId, client) = await _factory.CreateAuthenticatedClientAsync();
        await SeedPersonAsync(ownerId, "Anna");

        var response = await client.PostAsJsonAsync(Url, Request("Foto di Anna al mare"));
        var root = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal(1, root.GetProperty("resolvedPeople").GetArrayLength());
        Assert.Equal("Anna", root.GetProperty("resolvedPeople")[0].GetProperty("name").GetString());
        Assert.Equal(1, root.GetProperty("draft").GetProperty("peopleInclude").GetArrayLength());
        Assert.False(string.IsNullOrWhiteSpace(
            root.GetProperty("draft").GetProperty("semanticQuery").GetString()));
    }

    [Fact]
    public async Task Foreign_Owner_Person_Does_Not_Resolve()
    {
        var (ownerAId, _) = await _factory.CreateAuthenticatedClientAsync("a@example.com");
        await SeedPersonAsync(ownerAId, "Anna");
        var (_, clientB) = await _factory.CreateAuthenticatedClientAsync("b@example.com");

        var response = await clientB.PostAsJsonAsync(Url, Request("Foto di Anna"));
        var root = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal(0, root.GetProperty("resolvedPeople").GetArrayLength());
        Assert.Equal(0, root.GetProperty("draft").GetProperty("peopleInclude").GetArrayLength());
    }

    [Fact]
    public async Task Ambiguous_Person_Requires_Clarification()
    {
        var (ownerId, client) = await _factory.CreateAuthenticatedClientAsync();
        await SeedPersonAsync(ownerId, "Marco Rossi");
        await SeedPersonAsync(ownerId, "Marco Bianchi");

        var response = await client.PostAsJsonAsync(Url, Request("Foto di Marco"));
        var root = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        Assert.True(root.GetProperty("requiresClarification").GetBoolean());
        Assert.Equal(2, root.GetProperty("ambiguities")[0].GetProperty("candidates").GetArrayLength());
        Assert.Equal(0, root.GetProperty("draft").GetProperty("peopleInclude").GetArrayLength());
    }

    [Fact]
    public async Task Empty_Command_Is_Unsupported()
    {
        var (_, client) = await _factory.CreateAuthenticatedClientAsync();
        var response = await client.PostAsJsonAsync(Url, Request("   "));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
    }

    [Fact]
    public async Task Owner_Gallery_Routes_Semantic_Query_Physical_First()
    {
        var (_, client) = await _factory.CreateAuthenticatedClientAsync();
        // No active profile seeded → semantic path is cleanly unavailable, but
        // the owner gallery endpoint must ROUTE the semantic query.
        var response = await client.GetAsync(
            "/api/images?semanticQuery=mare%20al%20tramonto&semanticTopK=300");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var root = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        Assert.True(root.GetProperty("semanticActive").GetBoolean());
        Assert.Equal(0, root.GetProperty("count").GetInt32());
    }

    [Fact]
    public async Task Non_Semantic_Gallery_Query_Is_Unchanged()
    {
        var (_, client) = await _factory.CreateAuthenticatedClientAsync();
        var response = await client.GetAsync("/api/images?limit=10");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var root = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        // semanticActive defaults to false for a normal physical-only page.
        Assert.False(root.GetProperty("semanticActive").GetBoolean());
    }
}
