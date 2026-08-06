using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NubArca.Api.Cli;
using NubArca.Api.Jobs;
using Xunit;

namespace NubArca.Api.Tests.Cli;

// Regression for the CLI / worker host DI graph. The other CLI tests inject
// their own service provider into CliEntryPoint.RunAsync, so the REAL
// BuildDefaultHost graph (used by `jobs run-once` / `jobs worker` in
// production) was never exercised — which let a missing IVideoPosterProvider
// registration ship undetected (FileThumbnailService could not be constructed,
// breaking every handler that pulls it in: media-derivatives backfill + admin
// import). This test resolves the full job-handler graph to keep the CLI host
// in sync with the web host. Resolution constructs objects only — it never
// touches the database, so a dummy connection string is enough.
public sealed class CliHostGraphTests
{
    [Fact]
    public void DefaultHost_ResolvesEveryJobHandler_AndProcessor()
    {
        var prevCs = Environment.GetEnvironmentVariable("ConnectionStrings__Postgres");
        var prevRoot = Environment.GetEnvironmentVariable("Storage__RootPath");
        var tempRoot = Path.Combine(Path.GetTempPath(), $"nc-clihost-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        Environment.SetEnvironmentVariable(
            "ConnectionStrings__Postgres",
            "Host=localhost;Port=5432;Database=nc;Username=nc;Password=nc");
        Environment.SetEnvironmentVariable("Storage__RootPath", tempRoot);
        try
        {
            var build = typeof(CliEntryPoint).GetMethod(
                "BuildDefaultHost", BindingFlags.NonPublic | BindingFlags.Static);
            Assert.NotNull(build);

            using var host = (IHost)build!.Invoke(null, null)!;
            using var scope = host.Services.CreateScope();

            // Must not throw: builds JobProcessor + the whole handler graph,
            // including FileThumbnailService -> IVideoPosterProvider.
            var processor = scope.ServiceProvider.GetRequiredService<JobProcessor>();
            Assert.NotNull(processor);

            var handlers = scope.ServiceProvider.GetServices<IJobHandler>().ToList();
            Assert.Contains(handlers, h => h.JobType == JobTypes.AdminImport);
            Assert.Contains(handlers, h => h.JobType == JobTypes.MediaDerivativesBackfill);
            Assert.Contains(handlers, h => h.JobType == JobTypes.MetadataEmbeddedBackfill);
            Assert.Contains(handlers, h => h.JobType == JobTypes.StorageReconcile);
        }
        finally
        {
            Environment.SetEnvironmentVariable("ConnectionStrings__Postgres", prevCs);
            Environment.SetEnvironmentVariable("Storage__RootPath", prevRoot);
            try { Directory.Delete(tempRoot, recursive: true); } catch { /* best effort */ }
        }
    }
}
