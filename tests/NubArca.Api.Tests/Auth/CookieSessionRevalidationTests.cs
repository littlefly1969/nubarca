using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using NubArca.Api.Tests.Endpoints;

namespace NubArca.Api.Tests.Auth;

// Verifies that an existing cookie is re-checked against current User state on
// every authenticated request. A user disabled (or removed) after login must
// stop being able to access /api/* immediately, not at cookie expiration.
public sealed class CookieSessionRevalidationTests : IDisposable
{
    private readonly SqliteWebApplicationFactory _factory;

    public CookieSessionRevalidationTests()
    {
        _factory = new SqliteWebApplicationFactory();
        _factory.EnsureDatabaseCreated();
    }

    public void Dispose() => _factory.Dispose();

    [Fact]
    public async Task Authenticated_Request_Succeeds_Before_User_Is_Disabled()
    {
        var (_, client) = await _factory.CreateAuthenticatedClientAsync();

        var me = await client.GetAsync("/api/auth/me");

        Assert.Equal(HttpStatusCode.OK, me.StatusCode);
    }

    [Fact]
    public async Task Me_Returns_401_After_User_Is_Disabled_In_Db()
    {
        var (userId, client) = await _factory.CreateAuthenticatedClientAsync();

        // Sanity: cookie works before disabling.
        var before = await client.GetAsync("/api/auth/me");
        Assert.Equal(HttpStatusCode.OK, before.StatusCode);

        await _factory.DisableUserAsync(userId);

        var after = await client.GetAsync("/api/auth/me");
        Assert.Equal(HttpStatusCode.Unauthorized, after.StatusCode);
    }

    [Fact]
    public async Task Protected_Endpoints_Return_401_After_User_Is_Disabled_In_Db()
    {
        var (userId, client) = await _factory.CreateAuthenticatedClientAsync();

        // Seed a file via the working session so we can probe download afterwards.
        var fileSummary = await UploadOneFileAsync(client, "before.txt", "v1"u8.ToArray());

        await _factory.DisableUserAsync(userId);

        // GET file content
        var download = await client.GetAsync($"/api/files/{fileSummary.Id}/content");
        Assert.Equal(HttpStatusCode.Unauthorized, download.StatusCode);

        // POST folder create
        var folderCreate = await client.PostAsJsonAsync("/api/folders", new { name = "AfterDisable" });
        Assert.Equal(HttpStatusCode.Unauthorized, folderCreate.StatusCode);

        // POST multipart file upload
        var upload = await client.PostAsync("/api/files", MultipartWithFile(
            "post-disable"u8.ToArray(), "later.txt", "text/plain"));
        Assert.Equal(HttpStatusCode.Unauthorized, upload.StatusCode);

        // POST share-link create
        var shareCreate = await client.PostAsJsonAsync(
            $"/api/files/{fileSummary.Id}/share-links",
            new { });
        Assert.Equal(HttpStatusCode.Unauthorized, shareCreate.StatusCode);
    }

    [Fact]
    public async Task Me_Returns_401_After_User_Is_Deleted_From_Db()
    {
        var (userId, client) = await _factory.CreateAuthenticatedClientAsync();

        await _factory.DeleteUserAsync(userId);

        var response = await client.GetAsync("/api/auth/me");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    private static async Task<FileSummaryProbe> UploadOneFileAsync(
        HttpClient client, string fileName, byte[] bytes)
    {
        var response = await client.PostAsync("/api/files", MultipartWithFile(bytes, fileName, "text/plain"));
        response.EnsureSuccessStatusCode();
        var summary = await response.Content.ReadFromJsonAsync<FileSummaryProbe>();
        Assert.NotNull(summary);
        return summary!;
    }

    private static MultipartFormDataContent MultipartWithFile(byte[] bytes, string fileName, string mimeType)
    {
        var content = new MultipartFormDataContent();
        var byteContent = new ByteArrayContent(bytes);
        byteContent.Headers.ContentType = new MediaTypeHeaderValue(mimeType);
        content.Add(byteContent, "file", fileName);
        return content;
    }

    private sealed record FileSummaryProbe(Guid Id);
}
