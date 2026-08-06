using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NubArca.Api.Audit;
using NubArca.Api.Auth;
using NubArca.Api.Data;
using NubArca.Api.Domain;
using NubArca.Api.Files;
using NubArca.Api.Folders;
using NubArca.Api.ShareLinks;
using NubArca.Api.Storage;
using NubArca.Api.Users;

namespace NubArca.Api.Tests.Audit;

// Rate-limit tests run against a tightened policy (3 requests / 60 s) so the
// 4th request is rejected without hammering the host. The default policy is
// 10/min (login) and 60/min (share). The factory below mirrors
// SqliteWebApplicationFactory but layers in the RateLimits:* overrides.
public sealed class RateLimitTests : IDisposable
{
    private readonly TightLimitsFactory _factory;

    public RateLimitTests()
    {
        _factory = new TightLimitsFactory();
        _factory.EnsureDatabaseCreated();
    }

    public void Dispose() => _factory.Dispose();

    [Fact]
    public async Task Login_Rate_Limited_After_Threshold_Returns_429()
    {
        var client = _factory.CreateClient();
        var payload = new { email = "ghost@example.com", password = "anything" };

        for (var i = 0; i < TightLimitsFactory.LoginLimit; i++)
        {
            var response = await client.PostAsJsonAsync("/api/auth/login", payload);
            Assert.NotEqual(HttpStatusCode.TooManyRequests, response.StatusCode);
        }

        var overLimit = await client.PostAsJsonAsync("/api/auth/login", payload);
        Assert.Equal(HttpStatusCode.TooManyRequests, overLimit.StatusCode);
    }

    [Fact]
    public async Task Public_Share_Endpoint_Rate_Limited_After_Threshold_Returns_429()
    {
        var (owner, client) = await _factory.CreateAuthenticatedClientAsync();

        using (var scope = _factory.Services.CreateScope())
        {
            var files = scope.ServiceProvider.GetRequiredService<IFileItemService>();
            await files.CreateAsync(owner, null, "doc.txt", "text/plain", new MemoryStream("x"u8.ToArray()));
        }

        FileItem file;
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            file = await db.FileItems.AsNoTracking().SingleAsync();
        }

        var created = (await (await client.PostAsJsonAsync(
            $"/api/files/{file.Id}/share-links", new { }))
            .Content.ReadFromJsonAsync<ShareLinkCreatedResponse>())!;

        var anonymous = _factory.CreateClient();

        for (var i = 0; i < TightLimitsFactory.ShareLimit; i++)
        {
            var response = await anonymous.GetAsync(created.Url);
            Assert.NotEqual(HttpStatusCode.TooManyRequests, response.StatusCode);
        }

        var overLimit = await anonymous.GetAsync(created.Url);
        Assert.Equal(HttpStatusCode.TooManyRequests, overLimit.StatusCode);
    }

    [Fact]
    public async Task RateLimited_Response_Includes_RetryAfter_Header()
    {
        var client = _factory.CreateClient();
        var payload = new { email = "ghost@example.com", password = "x" };

        for (var i = 0; i < TightLimitsFactory.LoginLimit; i++)
        {
            await client.PostAsJsonAsync("/api/auth/login", payload);
        }
        var overLimit = await client.PostAsJsonAsync("/api/auth/login", payload);

        Assert.Equal(HttpStatusCode.TooManyRequests, overLimit.StatusCode);
        Assert.True(overLimit.Headers.Contains("Retry-After"),
            $"Expected Retry-After header. Got: {string.Join(", ", overLimit.Headers.Select(h => h.Key))}");
    }

    private sealed class TightLimitsFactory : WebApplicationFactory<Program>
    {
        public const string TestPassword = "test-password-9f3a";
        public const int LoginLimit = 3;
        public const int ShareLimit = 5;

        private SqliteConnection? _connection;
        public string StorageRoot { get; }

        public TightLimitsFactory()
        {
            StorageRoot = Path.Combine(Path.GetTempPath(), $"nubarca-ratelimit-{Guid.NewGuid():N}");
            Directory.CreateDirectory(StorageRoot);
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            _connection = new SqliteConnection("DataSource=:memory:");
            _connection.Open();

            builder.UseEnvironment("Testing");
            builder.UseSetting("ConnectionStrings:Postgres", string.Empty);
            builder.UseSetting("Storage:RootPath", StorageRoot);
            builder.UseSetting("RateLimits:Login:PermitLimit", LoginLimit.ToString());
            builder.UseSetting("RateLimits:Login:WindowSeconds", "60");
            builder.UseSetting("RateLimits:Share:PermitLimit", ShareLimit.ToString());
            builder.UseSetting("RateLimits:Share:WindowSeconds", "60");

            builder.ConfigureServices(services =>
            {
                services.AddDbContext<AppDbContext>(options => options.UseSqlite(_connection));
                services.AddScoped<IBlobService, BlobService>();
                services.AddScoped<IUserService, UserService>();
                services.AddScoped<IFolderService, FolderService>();
                services.AddScoped<IFileItemService, FileItemService>();
                services.AddScoped<IFileThumbnailService, FileThumbnailService>();
                services.AddScoped<IAuthService, AuthService>();
                services.AddScoped<IShareLinkService, ShareLinkService>();
                services.AddScoped<IAuditLogger, AuditLogger>();
            });
        }

        public void EnsureDatabaseCreated()
        {
            using var scope = Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.Database.EnsureCreated();
        }

        public async Task<(Guid UserId, HttpClient Client)> CreateAuthenticatedClientAsync(string email = "owner@example.com")
        {
            using var scope = Services.CreateScope();
            var users = scope.ServiceProvider.GetRequiredService<IUserService>();
            var auth = scope.ServiceProvider.GetRequiredService<IAuthService>();
            var user = await users.CreateAsync(email, "Owner");
            await auth.SetPasswordAsync(user.Id, TestPassword);

            var client = CreateClient();
            var response = await client.PostAsJsonAsync(
                "/api/auth/login",
                new { email, password = TestPassword });
            response.EnsureSuccessStatusCode();
            return (user.Id, client);
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (disposing)
            {
                _connection?.Dispose();
                _connection = null;
                try
                {
                    if (Directory.Exists(StorageRoot))
                    {
                        Directory.Delete(StorageRoot, recursive: true);
                    }
                }
                catch
                {
                    // best effort
                }
            }
        }
    }
}
