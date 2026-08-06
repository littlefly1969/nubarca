using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NubArca.Api.Audit;
using NubArca.Api.Data;
using NubArca.Api.Domain;
using NubArca.Api.Files;
using NubArca.Api.ShareLinks;
using NubArca.Api.Tests.Endpoints;

namespace NubArca.Api.Tests.Audit;

public sealed class AuditLoggingTests : IDisposable
{
    private readonly SqliteWebApplicationFactory _factory;

    public AuditLoggingTests()
    {
        _factory = new SqliteWebApplicationFactory();
        _factory.EnsureDatabaseCreated();
    }

    public void Dispose() => _factory.Dispose();

    private async Task<List<AuditLog>> ReadAuditAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.AuditLogs.AsNoTracking().OrderBy(a => a.CreatedAt).ToListAsync();
    }

    private async Task<FileItem> SeedFileAsAsync(Guid ownerId, string name, string mime, byte[] payload)
    {
        using var scope = _factory.Services.CreateScope();
        var files = scope.ServiceProvider.GetRequiredService<IFileItemService>();
        return await files.CreateAsync(ownerId, null, name, mime, new MemoryStream(payload));
    }

    private static MultipartFormDataContent MultipartWithFile(byte[] payload, string filename, string contentType)
    {
        var multipart = new MultipartFormDataContent();
        var part = new ByteArrayContent(payload);
        part.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        multipart.Add(part, "file", filename);
        return multipart;
    }

    [Fact]
    public async Task Successful_Login_Writes_LoginSuccess_Audit_Row()
    {
        var userId = await _factory.SeedUserAsync("alice@example.com");
        await _factory.LoginAsync("alice@example.com");

        var rows = await ReadAuditAsync();
        var entry = Assert.Single(rows, r => r.Action == AuditActions.LoginSuccess);
        Assert.Equal(AuditEntityTypes.User, entry.EntityType);
        Assert.Equal(userId, entry.UserId);
        Assert.Equal(userId, entry.EntityId);
        Assert.NotNull(entry.MetadataJson);
        Assert.Contains("alice@example.com", entry.MetadataJson!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Failed_Login_Writes_LoginFailure_Audit_Row_Without_Password()
    {
        await _factory.SeedUserAsync("alice@example.com");

        var client = _factory.CreateClient();
        var response = await client.PostAsJsonAsync(
            "/api/auth/login",
            new { email = "alice@example.com", password = "the-wrong-password-xyz" });
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);

        var rows = await ReadAuditAsync();
        var entry = Assert.Single(rows, r => r.Action == AuditActions.LoginFailure);
        Assert.Null(entry.UserId);
        Assert.Equal(AuditEntityTypes.User, entry.EntityType);
        Assert.NotNull(entry.MetadataJson);
        Assert.Contains("alice@example.com", entry.MetadataJson!, StringComparison.Ordinal);
        Assert.DoesNotContain("the-wrong-password-xyz", entry.MetadataJson!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Logout_Writes_Logout_Audit_Row()
    {
        var (userId, client) = await _factory.CreateAuthenticatedClientAsync();

        var response = await client.PostAsync("/api/auth/logout", content: null);
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        var rows = await ReadAuditAsync();
        var entry = Assert.Single(rows, r => r.Action == AuditActions.Logout);
        Assert.Equal(userId, entry.UserId);
        Assert.Equal(userId, entry.EntityId);
        Assert.Equal(AuditEntityTypes.User, entry.EntityType);
    }

    [Fact]
    public async Task Folder_Create_Writes_FolderCreate_Audit_Row()
    {
        var (userId, client) = await _factory.CreateAuthenticatedClientAsync();

        var response = await client.PostAsJsonAsync("/api/folders", new { name = "Photos" });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var rows = await ReadAuditAsync();
        var entry = Assert.Single(rows, r => r.Action == AuditActions.FolderCreate);
        Assert.Equal(userId, entry.UserId);
        Assert.Equal(AuditEntityTypes.Folder, entry.EntityType);
        Assert.NotNull(entry.EntityId);
        Assert.NotNull(entry.MetadataJson);
        Assert.Contains("Photos", entry.MetadataJson!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task File_Upload_Writes_FileUpload_Audit_Row()
    {
        var (userId, client) = await _factory.CreateAuthenticatedClientAsync();

        var response = await client.PostAsync("/api/files",
            MultipartWithFile("hi"u8.ToArray(), "hi.txt", "text/plain"));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var rows = await ReadAuditAsync();
        var entry = Assert.Single(rows, r => r.Action == AuditActions.FileUpload);
        Assert.Equal(userId, entry.UserId);
        Assert.Equal(AuditEntityTypes.File, entry.EntityType);
        Assert.NotNull(entry.MetadataJson);
        Assert.Contains("hi.txt", entry.MetadataJson!, StringComparison.Ordinal);
        Assert.Contains("text/plain", entry.MetadataJson!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task File_Download_Writes_FileDownload_Audit_Row()
    {
        var (userId, client) = await _factory.CreateAuthenticatedClientAsync();
        var file = await SeedFileAsAsync(userId, "doc.txt", "text/plain", "x"u8.ToArray());

        var response = await client.GetAsync($"/api/files/{file.Id}/content");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var rows = await ReadAuditAsync();
        var entry = Assert.Single(rows, r => r.Action == AuditActions.FileDownload);
        Assert.Equal(userId, entry.UserId);
        Assert.Equal(file.Id, entry.EntityId);
        Assert.Equal(AuditEntityTypes.File, entry.EntityType);
    }

    [Fact]
    public async Task Share_Create_And_Revoke_Write_Audit_Rows_Without_Raw_Token()
    {
        var (userId, client) = await _factory.CreateAuthenticatedClientAsync();
        var file = await SeedFileAsAsync(userId, "doc.txt", "text/plain", "x"u8.ToArray());

        var createResponse = await client.PostAsJsonAsync(
            $"/api/files/{file.Id}/share-links", new { });
        Assert.Equal(HttpStatusCode.Created, createResponse.StatusCode);
        var created = (await createResponse.Content.ReadFromJsonAsync<ShareLinkCreatedResponse>())!;

        var revokeResponse = await client.PostAsync(
            $"/api/share-links/{created.Id}/revoke", content: null);
        Assert.Equal(HttpStatusCode.NoContent, revokeResponse.StatusCode);

        var rows = await ReadAuditAsync();
        var create = Assert.Single(rows, r => r.Action == AuditActions.ShareCreate);
        var revoke = Assert.Single(rows, r => r.Action == AuditActions.ShareRevoke);

        Assert.Equal(userId, create.UserId);
        Assert.Equal(created.Id, create.EntityId);
        Assert.Equal(AuditEntityTypes.ShareLink, create.EntityType);
        Assert.Equal(userId, revoke.UserId);
        Assert.Equal(created.Id, revoke.EntityId);

        // Neither audit row may contain the raw token.
        foreach (var entry in new[] { create, revoke })
        {
            if (entry.MetadataJson is { } meta)
            {
                Assert.DoesNotContain(created.Token, meta, StringComparison.Ordinal);
            }
        }
    }

    [Fact]
    public async Task Public_Share_Download_Writes_Audit_Row_With_Null_UserId_And_No_Raw_Token()
    {
        var (userId, client) = await _factory.CreateAuthenticatedClientAsync();
        var file = await SeedFileAsAsync(userId, "doc.txt", "text/plain", "x"u8.ToArray());
        var created = (await (await client.PostAsJsonAsync(
            $"/api/files/{file.Id}/share-links", new { }))
            .Content.ReadFromJsonAsync<ShareLinkCreatedResponse>())!;

        var anonymous = _factory.CreateClient();
        var response = await anonymous.GetAsync(created.Url);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var rows = await ReadAuditAsync();
        var entry = Assert.Single(rows, r => r.Action == AuditActions.SharePublicDownload);
        Assert.Null(entry.UserId);
        Assert.Equal(AuditEntityTypes.ShareLink, entry.EntityType);
        if (entry.MetadataJson is { } meta)
        {
            Assert.DoesNotContain(created.Token, meta, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Audit_Rows_Never_Contain_StorageKey_Or_Objects_Path()
    {
        var (userId, client) = await _factory.CreateAuthenticatedClientAsync();
        var file = await SeedFileAsAsync(userId, "doc.txt", "text/plain", "verify-no-leak"u8.ToArray());

        await client.GetAsync($"/api/files/{file.Id}/content");
        var created = (await (await client.PostAsJsonAsync(
            $"/api/files/{file.Id}/share-links", new { }))
            .Content.ReadFromJsonAsync<ShareLinkCreatedResponse>())!;
        await (_factory.CreateClient()).GetAsync(created.Url);

        var rows = await ReadAuditAsync();
        foreach (var row in rows.Where(r => r.MetadataJson is not null))
        {
            Assert.DoesNotContain("objects/", row.MetadataJson!, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("StorageKey", row.MetadataJson!, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("storage_key", row.MetadataJson!, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("PasswordHash", row.MetadataJson!, StringComparison.OrdinalIgnoreCase);
        }
    }
}
