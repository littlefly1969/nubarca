using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NubArca.Api.Admin;
using NubArca.Api.Cli;
using NubArca.Api.Jobs;
using NubArca.Api.Jobs.Handlers;
using NubArca.Api.Files;
using NubArca.Api.MediaLibrary;
using NubArca.Api.Storage;
using NubArca.Api.Uploads;
using Xunit;

namespace NubArca.Api.Tests.Cli;

// Slice 97 (bug 1) — the CLI/worker host must bind the SAME option sections
// the web host binds. Field failure this guards: the worker container had
// Staging__RootPath in its environment, but `jobs worker` runs through the
// CLI host, which never called Configure<StagingOptions>() — so a
// staging-sourced admin import failed with "Staging storage is not
// configured" despite a perfectly configured deployment. Same family:
// IDerivedBlobStorage was web-host-only, silently sending worker-generated
// derivatives to the original root on split-root deployments.
public sealed class CliHostRegistrationTests : IDisposable
{
    private readonly string _storageRoot;
    private readonly string _derivedRoot;
    private readonly string _stagingRoot;

    public CliHostRegistrationTests()
    {
        var id = Guid.NewGuid().ToString("N");
        _storageRoot = Path.Combine(Path.GetTempPath(), $"nc-cli97-storage-{id}");
        _derivedRoot = Path.Combine(Path.GetTempPath(), $"nc-cli97-derived-{id}");
        _stagingRoot = Path.Combine(Path.GetTempPath(), $"nc-cli97-staging-{id}");
        Directory.CreateDirectory(_storageRoot);
        Directory.CreateDirectory(_derivedRoot);
        Directory.CreateDirectory(_stagingRoot);
    }

    public void Dispose()
    {
        foreach (var dir in new[] { _storageRoot, _derivedRoot, _stagingRoot })
        {
            try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
            catch { /* best effort */ }
        }
    }

    private ServiceProvider BuildCliHost(Dictionary<string, string?>? extra = null)
    {
        var settings = new Dictionary<string, string?>
        {
            // Registration only — nothing in these tests opens a connection.
            ["ConnectionStrings:Postgres"] = "Host=localhost;Database=nc-cli-host-test;Username=x;Password=x",
            ["Storage:RootPath"] = _storageRoot,
            ["Storage:DerivedRootPath"] = _derivedRoot,
            ["Staging:Enabled"] = "true",
            ["Staging:RootPath"] = _stagingRoot,
        };
        if (extra is not null)
        {
            foreach (var (k, v) in extra) settings[k] = v;
        }

        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        CliEntryPoint.ConfigureCliServices(services, configuration);
        return services.BuildServiceProvider(validateScopes: true);
    }

    [Fact]
    public void Worker_Host_Binds_StagingOptions_From_Configuration()
    {
        using var sp = BuildCliHost();

        var staging = sp.GetRequiredService<IOptions<StagingOptions>>().Value;
        Assert.True(staging.Enabled);
        Assert.Equal(_stagingRoot, staging.RootPath);
    }

    [Fact]
    public void Worker_Host_Binds_Configurable_Gallery_Derivative_Geometry()
    {
        using var sp = BuildCliHost(new Dictionary<string, string?>
        {
            ["MediaDerivatives:SmallMaxEdge"] = "640",
            ["MediaDerivatives:PosterWidth"] = "960",
            ["MediaDerivatives:PosterHeight"] = "540",
            ["MediaDerivatives:VideoPreviewFrameWidth"] = "200",
            ["MediaDerivatives:VideoPreviewFrameHeight"] = "112",
        });

        var options = sp.GetRequiredService<IOptions<MediaDerivativesOptions>>().Value;
        Assert.Equal(640, options.EdgeFor(ThumbnailSizes.Small));
        Assert.Equal((960, 540), options.PosterSize);
        Assert.Equal((200, 112, 6, 1200, 112), options.VideoPreviewStripSize);
    }

    [Fact]
    public void Worker_Host_Resolves_The_Admin_Import_Graph_That_Consumes_Staging()
    {
        using var sp = BuildCliHost();
        using var scope = sp.CreateScope();

        // The exact consumer that threw "Staging storage is not configured"
        // in the field: the admin-import service executed by `jobs worker`.
        var import = scope.ServiceProvider.GetRequiredService<IAdminImportService>();
        Assert.NotNull(import);

        // ...driven through the registered admin-import job handler.
        var handlers = scope.ServiceProvider.GetServices<IJobHandler>().ToList();
        Assert.Contains(handlers, h => h is AdminImportJobHandler);
        Assert.Contains(handlers, h =>
            h is GalleryDerivativesRegenerationJobHandler
            && h.JobType == JobTypes.MediaGalleryDerivativesRegenerate);
    }

    [Fact]
    public void Worker_Host_Registers_Derived_Storage_And_Media_Library_Like_The_Web_Host()
    {
        using var sp = BuildCliHost();

        // Split-root parity: without this, BlobService in the worker silently
        // wrote derivatives into the ORIGINAL root.
        var derived = sp.GetRequiredService<IDerivedBlobStorage>();
        Assert.NotNull(derived);

        using var scope = sp.CreateScope();
        Assert.NotNull(scope.ServiceProvider.GetRequiredService<IMediaLibraryService>());
    }

    [Fact]
    public void Worker_Host_Registers_Gallery_Regeneration_Handler()
    {
        using var sp = BuildCliHost();
        using var scope = sp.CreateScope();

        var handler = scope.ServiceProvider.GetServices<IJobHandler>()
            .SingleOrDefault(h =>
                h.JobType == JobTypes.MediaGalleryDerivativesRegenerate);

        Assert.IsType<GalleryDerivativesRegenerationJobHandler>(handler);
    }

    // VSEM-01: the worker runs ai.videos.segments.backfill, so it must both
    // REGISTER the handler and BIND the same "Ai:VideoSegmentation" section the
    // web host binds. A divergence would segment at the wrong version — or,
    // with the flag unbound, never segment at all.
    [Fact]
    public void Worker_Host_Binds_Video_Segmentation_Options_And_Registers_Its_Handler()
    {
        using var sp = BuildCliHost(new Dictionary<string, string?>
        {
            ["Ai:VideoSegmentation:Enabled"] = "true",
            ["Ai:VideoSegmentation:SegmentationVersion"] = "3",
            ["Ai:VideoSegmentation:MaximumSegmentsPerVideo"] = "42",
        });

        var options = sp.GetRequiredService<
            IOptions<NubArca.Api.Ai.Video.VideoSemanticSegmentationOptions>>().Value;
        Assert.True(options.Enabled);
        Assert.Equal(3, options.SegmentationVersion);
        Assert.Equal(42, options.MaximumSegmentsPerVideo);

        using var scope = sp.CreateScope();
        var handler = scope.ServiceProvider.GetServices<IJobHandler>()
            .SingleOrDefault(h => h.JobType == JobTypes.AiVideosSegmentsBackfill);
        Assert.IsType<NubArca.Api.Ai.Video.AiVideosSegmentsBackfillJobHandler>(handler);
    }

    // VFACE-01/01C: the worker runs ai.videos.faces.backfill, so it must both
    // REGISTER the handler and BIND the same "Ai:VideoFaceAnalysis" section the
    // web host binds — including FrameMaxEdge, which is this pipeline's OWN
    // resolution and must not be taken from Ai:VideoVisualEmbeddings.
    [Fact]
    public void Worker_Host_Binds_Video_Face_Options_And_Registers_Its_Handler()
    {
        using var sp = BuildCliHost(new Dictionary<string, string?>
        {
            ["Ai:VideoFaceAnalysis:Enabled"] = "true",
            ["Ai:VideoFaceAnalysis:AnalysisVersion"] = "4",
            ["Ai:VideoFaceAnalysis:FrameMaxEdge"] = "1280",
            ["Ai:VideoVisualEmbeddings:FrameMaxEdge"] = "4096",
        });

        var face = sp.GetRequiredService<
            IOptions<NubArca.Api.Ai.Video.Faces.VideoFaceAnalysisOptions>>().Value;
        Assert.True(face.Enabled);
        Assert.Equal(4, face.AnalysisVersion);
        Assert.Equal(1280, face.FrameMaxEdge);

        // The two sections are independent: the video-embedding value is bound
        // too, and neither leaks into the other.
        var semantic = sp.GetRequiredService<
            IOptions<NubArca.Api.Ai.Video.VideoVisualEmbeddingOptions>>().Value;
        Assert.Equal(4096, semantic.FrameMaxEdge);

        using var scope = sp.CreateScope();
        var handler = scope.ServiceProvider.GetServices<IJobHandler>()
            .SingleOrDefault(h => h.JobType == JobTypes.AiVideosFacesBackfill);
        Assert.IsType<NubArca.Api.Ai.Video.Faces.AiVideosFacesBackfillJobHandler>(handler);
    }

    [Fact]
    public void Worker_Host_Defaults_The_Face_Frame_Edge_To_768()
    {
        using var sp = BuildCliHost();

        Assert.Equal(768, sp.GetRequiredService<
            IOptions<NubArca.Api.Ai.Video.Faces.VideoFaceAnalysisOptions>>().Value.FrameMaxEdge);
    }

    [Fact]
    public void Cli_Host_Resolves_Every_Service_A_Cli_Verb_Dispatches_To()
    {
        // Anti-drift net: every service a CLI subcommand resolves via
        // GetService<T>() must be registered in ConfigureCliServices, or the
        // verb fails with a MISLEADING "database is not configured" even
        // though the connection string is fine (the 311518e field bug:
        // verify-bytes / audit-references worked on the web host but not in
        // the CLI container). When adding a CLI verb, add its service here.
        using var sp = BuildCliHost();
        using var scope = sp.CreateScope();
        var services = scope.ServiceProvider;

        Assert.NotNull(services.GetService<NubArca.Api.Users.IUserService>());        // users *
        // users set-role resolves a role by key or NAME against the role table,
        // and db migrate verifies the built-ins afterwards. Missing here, both
        // print "database is not configured" against a perfectly good one.
        Assert.NotNull(services.GetService<NubArca.Api.Access.IRoleService>());        // users set-role, db migrate
        Assert.NotNull(services.GetService<NubArca.Api.Data.AppDbContext>());          // db migrate
        Assert.NotNull(services.GetService<NubArca.Api.Metadata.MetadataBackfillService>());        // metadata backfill
        Assert.NotNull(services.GetService<NubArca.Api.Files.IFileItemService>());     // metadata recompute-effective-dates
        Assert.NotNull(services.GetService<NubArca.Api.Files.MediaDerivativesBackfillService>());   // media derivatives backfill
        Assert.NotNull(services.GetService<NubArca.Api.Files.GalleryDerivativesRegenerationService>()); // gallery derivatives regenerate
        Assert.NotNull(services.GetService<NubArca.Api.Files.MediaDerivativeBytesService>());       // media derivatives verify-/repair-bytes
        Assert.NotNull(services.GetService<NubArca.Api.Files.PosterRegenerationService>());         // media posters regenerate
        Assert.NotNull(services.GetService<NubArca.Api.Storage.StorageReconciliationService>());    // storage reconcile
        Assert.NotNull(services.GetService<BlobReferenceAuditService>());                // storage blobs audit-/repair-references
        Assert.NotNull(services.GetService<IJobQueue>());                                // jobs enqueue
        Assert.NotNull(services.GetService<JobProcessor>());                             // jobs list/run-once/worker
    }

    [Fact]
    public void The_Rag_Substrate_Resolves_In_The_Cli_Host()
    {
        // The bug this exists for: `AddRagSubstrate` registered
        // `RagDatabaseServices` as a factory resolving `RagDatabaseServices`,
        // as a stand-in for "optional dependency". Wherever it ran AFTER
        // `AddRagDatabase` — which is both the web host and the CLI — that
        // registration won and the container recursed forever, so `rag query`
        // hung with no output and no exception.
        //
        // Nothing caught it: the unit tests construct RagRetriever directly, and
        // the endpoint test host registers its own graph LAST, which put the
        // real registration back on top. Resolving through the actual CLI graph
        // is the only shape of test that would have.
        using var sp = BuildCliHost();
        using var scope = sp.CreateScope();

        Assert.NotNull(scope.ServiceProvider.GetService<NubArca.Api.Rag.IRagRetriever>());
        Assert.NotNull(scope.ServiceProvider.GetService<NubArca.Api.Rag.Indexing.IRagIndexer>());
        Assert.NotNull(scope.ServiceProvider.GetService<NubArca.Api.Rag.Domains.IRagDomainRegistry>());
        Assert.NotNull(scope.ServiceProvider.GetService<NubArca.Api.Rag.Storage.RagVectorIndexService>());
        Assert.NotNull(scope.ServiceProvider.GetService<NubArca.Api.Ai.TextEmbeddings.TextEmbeddingResolver>());

        // One provider per domain, and exactly the two domains that exist.
        var providers = scope.ServiceProvider
            .GetServices<NubArca.Api.Rag.Sources.IRagSourceProvider>()
            .Select(p => p.Domain)
            .OrderBy(d => d, StringComparer.Ordinal)
            .ToList();
        Assert.Equal(
            new[]
            {
                NubArca.Api.Rag.Domains.RagDomains.NubArcaRepository,
                NubArca.Api.Rag.Domains.RagDomains.ProductHelp,
            },
            providers);
    }

    [Fact]
    public void The_Rag_Retriever_Still_Resolves_Without_A_Database()
    {
        // An installation with no connection string answers Product Help from
        // the corpus bundled in its image. The retriever must therefore build
        // with its database half ABSENT rather than fail to resolve.
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>())
            .Build();
        var services = new ServiceCollection();
        services.AddLogging();
        CliEntryPoint.ConfigureCliServices(services, configuration);
        using var sp = services.BuildServiceProvider(validateScopes: true);
        using var scope = sp.CreateScope();

        Assert.NotNull(scope.ServiceProvider.GetService<NubArca.Api.Rag.IRagRetriever>());
        Assert.Null(scope.ServiceProvider.GetService<NubArca.Api.Rag.Retrieval.RagDatabaseServices>());
    }

    [Fact]
    public void Worker_Host_Without_Connection_String_Builds_But_Offers_No_Job_Services()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>())
            .Build();
        var services = new ServiceCollection();
        services.AddLogging();
        CliEntryPoint.ConfigureCliServices(services, configuration);
        using var sp = services.BuildServiceProvider();

        // The CLI prints the EX_CONFIG message in this state; no half-wired graph.
        using var scope = sp.CreateScope();
        Assert.Null(scope.ServiceProvider.GetService<IAdminImportService>());
        Assert.Null(scope.ServiceProvider.GetService<JobProcessor>());
    }
}
