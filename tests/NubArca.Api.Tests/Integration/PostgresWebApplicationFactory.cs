using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace NubArca.Api.Tests.Integration;

// A WebApplicationFactory that points Program.cs at a REAL PostgreSQL database
// (the Testcontainers fixture) instead of SQLite. Because the connection string
// is non-empty, Program.cs registers its full production service graph on
// Npgsql — no manual service wiring, exact-production behaviour (separate
// pooled connections, so the job engine's heartbeat context never collides with
// a running handler the way it would on a single shared SQLite connection).
//
// Used by the mid-run import cancellation smoke test, which needs the real
// lease/heartbeat timer to flip a running handler's cancellation flag.
public sealed class PostgresWebApplicationFactory : WebApplicationFactory<Program>
{
    private readonly string _connectionString;
    private readonly IReadOnlyDictionary<string, string?> _settings;

    public string StorageRoot { get; }

    public PostgresWebApplicationFactory(
        string connectionString, IReadOnlyDictionary<string, string?> settings)
    {
        _connectionString = connectionString;
        _settings = settings;
        StorageRoot = Path.Combine(Path.GetTempPath(), $"nubarca-pgwaf-{Guid.NewGuid():N}");
        Directory.CreateDirectory(StorageRoot);
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("ConnectionStrings:Postgres", _connectionString);
        builder.UseSetting("Storage:RootPath", StorageRoot);
        // The worker stays OFF; the smoke test drives JobProcessor explicitly.
        builder.UseSetting("Jobs:WorkerEnabled", "false");
        foreach (var (key, value) in _settings)
        {
            builder.UseSetting(key, value);
        }
        // Program.cs registers the full graph on Npgsql from the connection
        // string above — nothing to register here.
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing)
        {
            try { if (Directory.Exists(StorageRoot)) Directory.Delete(StorageRoot, recursive: true); }
            catch { /* best effort */ }
        }
    }
}
