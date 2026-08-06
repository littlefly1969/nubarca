using System.Collections.Concurrent;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using NubArca.Api.Admin;
using NubArca.Api.Ai;
using NubArca.Api.Albums;
using NubArca.Api.Audit;
using NubArca.Api.Auth;
using NubArca.Api.Data;
using NubArca.Api.Domain;
using NubArca.Api.Files;
using NubArca.Api.Folders;
using NubArca.Api.Jobs;
using NubArca.Api.Jobs.Handlers;
using NubArca.Api.MediaLibrary;
using NubArca.Api.Metadata;
using NubArca.Api.Organizer;
using NubArca.Api.PhotoExport;
using NubArca.Api.Plates;
using NubArca.Api.ShareLinks;
using NubArca.Api.Storage;
using NubArca.Api.Tv;
using NubArca.Api.Uploads;
using NubArca.Api.Users;

namespace NubArca.Api.Tests.Endpoints;

// Shared WebApplicationFactory for endpoint tests that need a real HTTP host
// but no Docker. Each instance allocates its own per-test SQLite in-memory
// database and storage root, mirroring what Program.cs's connection-string
// conditional would register.
//
// Why we don't try to replace the Npgsql-bound DbContext: when Program.cs has
// already registered AppDbContext with UseNpgsql, an additional AddDbContext
// call in ConfigureServices doesn't fully replace the internal
// IDbContextOptionsConfiguration<AppDbContext> registration. Both provider
// extensions end up in the same options builder and EF Core rejects the
// double registration. The simpler answer is: leave ConnectionStrings:Postgres
// empty so Program.cs's conditional skips entirely, then register everything
// from the test side. If a new application service is added to Program.cs's
// conditional block, mirror it in ConfigureWebHost below.
public sealed class SqliteWebApplicationFactory : WebApplicationFactory<Program>
{
    public const string TestPassword = "test-password-9f3a";

    private static readonly object SchemaTemplateLock = new();
    private static SqliteConnection? _schemaTemplate;
    private static readonly ConcurrentDictionary<string, ConcurrentBag<PooledHost>> HostPools = new();
    private static readonly int MaxPooledHostsPerConfiguration = Math.Max(2, Environment.ProcessorCount);

    private SqliteConnection? _connection;
    private bool _databaseInitialized;
    private readonly bool _poolable;
    private readonly string? _poolKey;
    private readonly List<HttpClient> _clients = [];
    private PooledHost? _pooledHost;
    private bool _disposed;
    private readonly IReadOnlyDictionary<string, string?>? _extraSettings;
    private readonly TimeProvider? _clockOverride;

    public string StorageRoot { get; private set; }

    public SqliteWebApplicationFactory()
        : this(extraSettings: null)
    {
    }

    // Test overload: lets a test enable e.g. ForwardedHeaders without each
    // test class having to duplicate the whole service registration list.
    // `clockOverride` (slice 83) swaps the singleton TimeProvider so a test can
    // drive wall-clock budgets deterministically.
    public SqliteWebApplicationFactory(
        IReadOnlyDictionary<string, string?>? extraSettings,
        TimeProvider? clockOverride = null,
        bool poolHost = false)
    {
        StorageRoot = Path.Combine(Path.GetTempPath(), $"nubarca-endpoint-{Guid.NewGuid():N}");
        Directory.CreateDirectory(StorageRoot);
        _extraSettings = extraSettings;
        _clockOverride = clockOverride;
        _poolable = clockOverride is null && (extraSettings is null || poolHost);
        _poolKey = _poolable ? BuildPoolKey(extraSettings) : null;
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        builder.UseEnvironment("Testing");
        builder.UseSetting("ConnectionStrings:Postgres", string.Empty);
        builder.UseSetting("Storage:RootPath", StorageRoot);
        // Slice 100: pin the stable ImageSharp backend by default so the broad
        // existing suite exercises the exact known-good path. Tests that target
        // the libvips backend override MediaDerivatives:ImageBackend via
        // extraSettings (applied below, after this default).
        builder.UseSetting("MediaDerivatives:ImageBackend", "imagesharp");
        // Plates ALPR: enable the deterministic dev/test pipeline by default so
        // worker analysis tests produce detections. The disabled/model-not-
        // configured path is covered by a service-level unit test.
        builder.UseSetting("Plates:Alpr:Enabled", "true");

        if (_poolable)
        {
            // A pooled host spans multiple isolated test cases. Dedicated
            // rate-limit tests use their own configured factories; ordinary
            // endpoint tests must not consume one another's in-memory buckets.
            foreach (var policy in new[]
            {
                "Login", "Share", "ExportCreate", "VaultUnlock",
                "TvPairingStart", "TvPersonalUnlock", "Party", "PartyMedia",
                "PartyUpload", "BeautyLabUpload", "PartyFaceSearch",
                "SemanticSearch", "TvPersonalInterpret"
            })
            {
                builder.UseSetting($"RateLimits:{policy}:PermitLimit", "100000");
            }
        }

        if (_extraSettings is not null)
        {
            foreach (var (key, value) in _extraSettings)
            {
                builder.UseSetting(key, value);
            }
        }

        builder.ConfigureServices((context, services) =>
        {
            // Endpoint tests exercise authentication/authorization behavior,
            // while AuthServiceTests cover ASP.NET Core's real PBKDF2 hasher.
            // Replacing all three production hashers here avoids making every
            // seeded user, Vault unlock and TV PIN flow CPU-bound.
            services.Replace(ServiceDescriptor.Singleton<IPasswordHasher<User>,
                TestPasswordHasher<User>>());
            services.Replace(ServiceDescriptor.Singleton<IPasswordHasher<PrivateVault>,
                TestPasswordHasher<PrivateVault>>());
            services.Replace(ServiceDescriptor.Singleton<IPasswordHasher<TvPersonalPin>,
                TestPasswordHasher<TvPersonalPin>>());

            services.AddDbContext<AppDbContext>(options => options.UseSqlite(_connection));
            // Slice 83: optional deterministic clock (last registration wins).
            if (_clockOverride is not null)
            {
                services.AddSingleton(_clockOverride);
            }
            services.AddScoped<IBlobService, BlobService>();
            services.AddScoped<IUserService, UserService>();
            services.AddScoped<IFileItemService, FileItemService>();
            // deleted-content-import-skip: ledger + import skip evaluator
            // (Program.cs registers these inside the Postgres-only block).
            services.AddScoped<IDeletedContentTombstoneService, DeletedContentTombstoneService>();
            services.AddScoped<IImportSkipEvaluator, ImportSkipEvaluator>();
            // Slice 100: image-derivative backends (libvips fast path with
            // ImageSharp fallback) + renderer. Mirrors Program.cs.
            services.Configure<MediaDerivativesOptions>(
                context.Configuration.GetSection(MediaDerivativesOptions.SectionName));
            services.AddSingleton<VipsRuntime>();
            services.AddSingleton<ImageSharpDerivativeBackend>();
            services.AddSingleton<VipsDerivativeBackend>();
            services.AddSingleton<IImageDerivativeBackend>(
                sp => sp.GetRequiredService<VipsDerivativeBackend>());
            services.AddSingleton<ImageDerivativeRenderer>();
            // Slice 94: media-library rules + eligibility.
            services.AddScoped<IMediaLibraryService, MediaLibraryService>();
            // Slice 3: per-file media-library exclusion (bulk exclude/restore).
            services.AddScoped<IMediaLibraryExclusionService, MediaLibraryExclusionService>();
            // Slice 77: FolderService needs IFileItemService for recursive delete.
            services.AddScoped<IFolderService>(sp =>
                new FolderService(
                    sp.GetRequiredService<AppDbContext>(),
                    sp.GetRequiredService<TimeProvider>(),
                    sp.GetRequiredService<IFileItemService>(),
                    sp.GetRequiredService<IMediaLibraryService>()));
            services.AddScoped<IFileThumbnailService, FileThumbnailService>();
            services.AddScoped<IAuthService, AuthService>();
            services.AddScoped<IAdminUserService, AdminUserService>();
            services.AddScoped<ITvPairingService, TvPairingService>();
            services.AddScoped<ITvMediaService, TvMediaService>();
            services.AddScoped<ITvPersonalAreaService, TvPersonalAreaService>();
            services.AddScoped<ITvPersonalGalleryService, TvPersonalGalleryService>();
            services.AddScoped<IShareLinkService, ShareLinkService>();
            services.AddScoped<IAuditLogger, AuditLogger>();
            services.AddSingleton<StorageStatsCache>();
            services.AddScoped<IStorageStatsService, StorageStatsService>();
            services.AddScoped<IStorageAccountingService, StorageAccountingService>();
            services.AddScoped<IMetadataService, MetadataService>();
            services.AddScoped<IAlbumService, AlbumService>();
            // SHARE-ALBUM-01: live album sharing. Mirrors Program.cs
            // (Postgres-only block).
            services.AddScoped<NubArca.Api.Albums.Sharing.IAlbumAccessResolver,
                NubArca.Api.Albums.Sharing.AlbumAccessResolver>();
            services.AddScoped<NubArca.Api.Albums.Sharing.IAlbumSharingService,
                NubArca.Api.Albums.Sharing.AlbumSharingService>();
            services.AddScoped<NubArca.Api.Albums.Sharing.IAlbumEditingService,
                NubArca.Api.Albums.Sharing.AlbumEditingService>();
            // SHARE-COPY-01: detached album copies. Mirrors Program.cs
            // (Postgres-only block).
            services.AddScoped<NubArca.Api.Albums.Sharing.IAlbumTransferService,
                NubArca.Api.Albums.Sharing.AlbumTransferService>();
            // Registered as a plain singleton, NOT as a hosted service: the
            // tests drive RunOnceAsync explicitly so the sweep is deterministic
            // rather than racing the test on a timer.
            services.AddSingleton<NubArca.Api.Albums.Sharing.AlbumTransferCleanupService>();
            // Slice 5: unified media-workspace query service (/api/media,
            // /api/albums/{id}/media). Mirrors Program.cs (Postgres-only block).
            services.AddScoped<NubArca.Api.Media.IMediaCollectionQueryService,
                NubArca.Api.Media.MediaCollectionQueryService>();
            services.AddScoped<NubArca.Api.Party.IPartyLinkService, NubArca.Api.Party.PartyLinkService>();
            services.AddScoped<NubArca.Api.Party.IPartyMediaService, NubArca.Api.Party.PartyMediaService>();
            services.AddScoped<NubArca.Api.Party.IPartyUploadService, NubArca.Api.Party.PartyUploadService>();
            services.AddScoped<NubArca.Api.Party.IPartyModerationService, NubArca.Api.Party.PartyModerationService>();
            services.AddScoped<NubArca.Api.Party.IPartyFaceSearchService, NubArca.Api.Party.PartyFaceSearchService>();
            services.AddScoped<StorageReconciliationService>();
            // Slice 97: refcount audit/repair.
            services.AddScoped<BlobReferenceAuditService>();
            services.AddSingleton<IEmbeddedMetadataExtractor, EmbeddedImageMetadataExtractor>();
            services.AddSingleton<IImageMetadataStripper, ImageSharpMetadataStripper>();
            services.AddSingleton<IImageMetadataWriter, ImageSharpMetadataWriter>();
            services.AddSingleton<IVideoSignatureDetector, VideoSignatureDetector>();
            // Slice 68: always use the synthetic poster provider in tests; no real FFmpeg required.
            services.AddSingleton<SyntheticVideoPosterProvider>();
            services.AddSingleton<IVideoPosterProvider>(
                sp => sp.GetRequiredService<SyntheticVideoPosterProvider>());
            services.AddSingleton<IProcessRunner, SystemProcessRunner>();
            services.AddSingleton<IVideoMetadataExtractor, NoopVideoMetadataExtractor>();
            // Video-hls slice 1: same defaults as the web host with
            // Media:VideoHlsProvider unset (no-op transcoder; the generation
            // service refuses work while disabled).
            services.AddSingleton<IDirectoryProcessRunner, SystemProcessRunner>();
            services.AddSingleton<HlsDerivativeStorage>();
            services.AddSingleton<IVideoHlsTranscoder, NoopVideoHlsTranscoder>();
            services.AddScoped<VideoHlsGenerationService>();
            services.AddScoped<VideoHlsBackfillService>();
            // Slice 2: HLS serving + lazy-enqueue seam (endpoints resolve these
            // regardless of whether the provider is enabled).
            services.AddScoped<IJobQueueAccessor, NubArca.Api.Jobs.VideoHlsJobQueueAccessor>();
            services.AddScoped<VideoHlsServingService>();
            services.AddScoped<NubArca.Api.Admin.AdminJobCatalogService>();
            services.AddScoped<MetadataBackfillService>();
            services.AddScoped<VideoMetadataBackfillService>();
            // Slice 99: durable derivative diagnostics (failure recording, retry
            // gating, aggregates) — registered before the backfill that uses it.
            services.AddScoped<DerivativeDiagnosticsService>();
            services.AddScoped<MediaDerivativesBackfillService>();
            services.AddScoped<GalleryDerivativesRegenerationService>();
            services.AddScoped<MediumPreviewRegenerationService>();
            // Slice 95: poster regeneration.
            services.AddScoped<PosterRegenerationService>();
            // Slice 96: derived-bytes placement audit/repair.
            services.AddScoped<MediaDerivativeBytesService>();
            // Slice 100: backend benchmark.
            services.AddScoped<DerivativeBenchmarkService>();
            // Slice 70: background jobs graph (worker stays off; tests drive
            // JobProcessor / IJobQueue directly or via the `jobs` CLI).
            services.AddScoped<IJobQueue, JobQueue>();
            services.AddScoped<JobProcessor>();
            // AI substrate (Phase 0B): mirror the web/CLI host registration so
            // endpoint tests can resolve the AI services. AiOptions is bound by
            // Program.cs (outside the Postgres conditional), so it resolves here.
            services.AddAiSubstrate();
            // Slice 81: admin server-side import service.
            services.AddScoped<IAdminImportService, AdminImportService>();
            // Phase 2: photo date-taken organizer service.
            services.AddScoped<PhotoDateTakenOrganizerService>();
            // Photo archive export service.
            services.AddScoped<PhotoExportService>();
            // Private Vault (v0).
            services.AddScoped<NubArca.Api.Vault.IPrivateVaultService,
                NubArca.Api.Vault.PrivateVaultService>();
            // Plates (Targhe): owner-private segregated image surface + ALPR
            // pipeline (mirrors the production/CLI registration).
            services.Configure<NubArca.Api.Plates.PlatesAlprOptions>(
                context.Configuration.GetSection(NubArca.Api.Plates.PlatesAlprOptions.SectionName));
            services.Configure<NubArca.Api.Plates.PlatesFaceRedactionOptions>(
                context.Configuration.GetSection(NubArca.Api.Plates.PlatesFaceRedactionOptions.SectionName));
            services.AddNubArcaPlates();
            // Aesthetics Lab: mirror production/CLI registration, then override
            // the sidecar client with a strict FAKE so worker analysis tests
            // never load the real model. AestheticsOptions binds from config
            // (tests set HumanAesExpert:* via extraSettings when needed).
            services.Configure<NubArca.Api.Aesthetics.AestheticsOptions>(
                context.Configuration.GetSection(NubArca.Api.Aesthetics.AestheticsOptions.SectionName));
            services.AddScoped<NubArca.Api.Aesthetics.IAestheticLabService,
                NubArca.Api.Aesthetics.AestheticLabService>();
            services.AddScoped<NubArca.Api.Aesthetics.IAestheticAnalysisService,
                NubArca.Api.Aesthetics.AestheticAnalysisService>();
            // TV "Beauty Lab" QR mobile-upload capability service (mirror prod).
            services.AddScoped<NubArca.Api.Aesthetics.IAestheticUploadSessionService,
                NubArca.Api.Aesthetics.AestheticUploadSessionService>();
            services.AddSingleton<NubArca.Api.Tests.Aesthetics.FakeAestheticModelClient>();
            services.AddScoped<NubArca.Api.Aesthetics.Sidecar.IAestheticModelClient>(
                sp => sp.GetRequiredService<NubArca.Api.Tests.Aesthetics.FakeAestheticModelClient>());
            // Post-ingestion media pipeline (direct-upload → background media work).
            services.AddScoped<NubArca.Api.Ingestion.IPostIngestionMediaPipelineService,
                NubArca.Api.Ingestion.PostIngestionMediaPipelineService>();
            // Background-job handlers — the SHARED production list, so tests
            // exercise exactly what the web + worker hosts register.
            services.AddNubArcaJobHandlers();
            // Slice 93: web remote-staging upload. The cleanup sweeper is a
            // singleton driven via RunOnceAsync by tests (never hosted here).
            services.AddScoped<IStagingUploadService, StagingUploadService>();
            services.AddSingleton<StagingCleanupService>();
            services.AddSingleton<BlobJanitor>();
            services.AddSingleton<FileItemSweeper>();
            // Intentionally NOT registered as IHostedService: tests drive the
            // janitor / sweeper via RunOnceAsync to avoid timing races with
            // the host's background loop.
        });
    }

    protected override IHost CreateHost(IHostBuilder builder)
    {
        var pool = _poolKey is null
            ? null
            : HostPools.GetOrAdd(_poolKey, static _ => new ConcurrentBag<PooledHost>());
        if (pool is not null && pool.TryTake(out var pooled))
        {
            var unusedRoot = StorageRoot;
            StorageRoot = pooled.StorageRoot;
            _connection = pooled.Connection;
            _databaseInitialized = true;
            _pooledHost = pooled;
            DeleteDirectoryBestEffort(unusedRoot);
            return pooled.Host;
        }

        var host = base.CreateHost(builder);
        if (_poolable)
        {
            if (_connection is null)
            {
                host.Dispose();
                throw new InvalidOperationException("The pooled test host has no SQLite connection.");
            }

            _pooledHost = new PooledHost(host, _connection, StorageRoot);
        }

        return host;
    }

    public new HttpClient CreateClient()
    {
        var client = base.CreateClient();
        _clients.Add(client);
        return client;
    }

    public void EnsureDatabaseCreated()
    {
        if (_databaseInitialized)
        {
            return;
        }

        // Starting Services runs ConfigureWebHost and opens this factory's
        // private in-memory connection. Building the large EF schema afresh in
        // every xUnit test was one of the suite's dominant costs (xUnit creates
        // a new test-class instance per method). Keep one process-wide EMPTY
        // schema and clone it with SQLite's native backup API. Test data is
        // never copied: every factory still owns an isolated connection.
        _ = Services;

        lock (SchemaTemplateLock)
        {
            if (_databaseInitialized)
            {
                return;
            }

            if (_connection is null)
            {
                throw new InvalidOperationException("The test SQLite connection was not initialized.");
            }

            if (_schemaTemplate is null)
            {
                using var scope = Services.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                db.Database.EnsureCreated();

                _schemaTemplate = new SqliteConnection("DataSource=:memory:");
                _schemaTemplate.Open();
                _connection.BackupDatabase(_schemaTemplate);
            }
            else
            {
                _schemaTemplate.BackupDatabase(_connection);
            }

            _databaseInitialized = true;
        }
    }

    // Creates a fresh user with a known password and returns their id.
    public async Task<Guid> SeedUserAsync(string email = "owner@example.com", string? password = null)
    {
        using var scope = Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<IUserService>();
        var auth = scope.ServiceProvider.GetRequiredService<IAuthService>();
        var user = await users.CreateAsync(email, "Owner");
        await auth.SetPasswordAsync(user.Id, password ?? TestPassword);
        return user.Id;
    }

    // Promotes an existing user to admin. The admin claim is added to the
    // principal at LOGIN time, so the typical test pattern is:
    //   SeedUserAsync → PromoteToAdminAsync → LoginAsync. The cookie
    //   revalidator will also pick up admin changes after a login.
    public async Task PromoteToAdminAsync(Guid userId)
    {
        using var scope = Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<IUserService>();
        var ok = await users.SetAdminAsync(userId, true);
        if (!ok)
        {
            throw new InvalidOperationException($"PromoteToAdminAsync: no user with id {userId}.");
        }
    }

    public async Task DemoteFromAdminAsync(Guid userId)
    {
        using var scope = Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<IUserService>();
        await users.SetAdminAsync(userId, false);
    }

    // Marks an existing user as disabled. Useful for testing disabled-login paths.
    public async Task DisableUserAsync(Guid userId)
    {
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var user = await db.Users.FirstAsync(u => u.Id == userId);
        user.DisabledAt = DateTime.UtcNow;
        await db.SaveChangesAsync();
    }

    // Hard-deletes a user row, clearing dependent audit-log rows first so the
    // FK Restrict on audit_logs.UserId doesn't block the delete. Used to verify
    // cookie revalidation rejects principals whose user no longer exists.
    public async Task DeleteUserAsync(Guid userId)
    {
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await db.AuditLogs.Where(a => a.UserId == userId).ExecuteDeleteAsync();
        var user = await db.Users.FirstAsync(u => u.Id == userId);
        db.Users.Remove(user);
        await db.SaveChangesAsync();
    }

    // Performs an actual /api/auth/login round-trip so the test exercises the
    // real auth path. Returns a client whose cookie jar holds the auth cookie.
    public async Task<HttpClient> LoginAsync(string email = "owner@example.com", string? password = null)
    {
        var client = CreateClient();
        var response = await client.PostAsJsonAsync(
            "/api/auth/login",
            new { email, password = password ?? TestPassword });
        response.EnsureSuccessStatusCode();
        return client;
    }

    // One-shot helper for the common "seed a user, log them in" pattern used
    // by virtually every authenticated endpoint test.
    public async Task<(Guid UserId, HttpClient Client)> CreateAuthenticatedClientAsync(
        string email = "owner@example.com")
    {
        var userId = await SeedUserAsync(email);
        var client = await LoginAsync(email);
        return (userId, client);
    }

    protected override void Dispose(bool disposing)
    {
        if (!disposing || _disposed)
        {
            return;
        }

        _disposed = true;

        foreach (var client in _clients)
        {
            client.Dispose();
        }
        _clients.Clear();

        if (_poolable && _pooledHost is not null)
        {
            var pooled = _pooledHost;
            _pooledHost = null;

            // Some HTTP-only tests start the host without ever creating the
            // database. Such a host is valid for that test but must never enter
            // the pool: a later factory would otherwise treat the leased
            // connection as schema-ready and fail with "no such table".
            if (!_databaseInitialized)
            {
                pooled.Dispose();
                return;
            }

            try
            {
                ResetPooledHost(pooled);
                var pool = HostPools.GetOrAdd(_poolKey!, static _ => new ConcurrentBag<PooledHost>());
                if (pool.Count < MaxPooledHostsPerConfiguration)
                {
                    pool.Add(pooled);
                    return;
                }
            }
            catch
            {
                // A poisoned host must never be handed to another test.
            }

            pooled.Dispose();
            return;
        }

        base.Dispose(disposing);

        _connection?.Dispose();
        _connection = null;
        DeleteDirectoryBestEffort(StorageRoot);
    }

    private static void ResetPooledHost(PooledHost pooled)
    {
        var tables = new List<string>();
        using (var select = pooled.Connection.CreateCommand())
        {
            select.CommandText =
                "SELECT name FROM sqlite_master WHERE type = 'table' AND name NOT LIKE 'sqlite_%'";
            using var reader = select.ExecuteReader();
            while (reader.Read())
            {
                tables.Add(reader.GetString(0));
            }
        }

        using (var foreignKeysOff = pooled.Connection.CreateCommand())
        {
            foreignKeysOff.CommandText = "PRAGMA foreign_keys = OFF";
            foreignKeysOff.ExecuteNonQuery();
        }

        try
        {
            using var transaction = pooled.Connection.BeginTransaction();
            foreach (var table in tables)
            {
                using var delete = pooled.Connection.CreateCommand();
                delete.Transaction = transaction;
                delete.CommandText = $"DELETE FROM \"{table.Replace("\"", "\"\"")}\"";
                delete.ExecuteNonQuery();
            }
            transaction.Commit();
        }
        finally
        {
            using var foreignKeysOn = pooled.Connection.CreateCommand();
            foreignKeysOn.CommandText = "PRAGMA foreign_keys = ON";
            foreignKeysOn.ExecuteNonQuery();
        }

        if (Directory.Exists(pooled.StorageRoot))
        {
            foreach (var entry in Directory.EnumerateFileSystemEntries(pooled.StorageRoot))
            {
                if (Directory.Exists(entry))
                {
                    Directory.Delete(entry, recursive: true);
                }
                else
                {
                    File.Delete(entry);
                }
            }
        }

        pooled.Host.Services.GetRequiredService<StorageStatsCache>().Clear();
        pooled.Host.Services
            .GetRequiredService<NubArca.Api.Tests.Aesthetics.FakeAestheticModelClient>()
            .Reset();
    }

    private static void DeleteDirectoryBestEffort(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch
        {
            // Test cleanup is best effort.
        }
    }

    private static string BuildPoolKey(IReadOnlyDictionary<string, string?>? settings)
    {
        if (settings is null || settings.Count == 0)
        {
            return "default";
        }

        // Length-prefix both key and value so distinct configurations cannot
        // collide even when either contains separators. Callers must opt in to
        // custom-config pooling; path/clock/rate-limit factories stay dedicated.
        return string.Join(
            "|",
            settings
                .OrderBy(pair => pair.Key, StringComparer.Ordinal)
                .Select(pair =>
                {
                    var value = pair.Value ?? string.Empty;
                    return $"{pair.Key.Length}:{pair.Key}{value.Length}:{value}";
                }));
    }

    private sealed record PooledHost(
        IHost Host,
        SqliteConnection Connection,
        string StorageRoot) : IDisposable
    {
        public void Dispose()
        {
            Host.Dispose();
            Connection.Dispose();
            DeleteDirectoryBestEffort(StorageRoot);
        }
    }
}
