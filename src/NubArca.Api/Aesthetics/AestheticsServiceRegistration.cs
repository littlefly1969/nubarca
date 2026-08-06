using Microsoft.Extensions.DependencyInjection;
using NubArca.Api.Aesthetics.Sidecar;

namespace NubArca.Api.Aesthetics;

// SINGLE SOURCE OF TRUTH for the owner-private Aesthetics Lab service graph,
// registered by ALL hosts: the web API (Program.cs), the CLI/worker host
// (CliEntryPoint) — which runs the analysis job — and the test fixture
// (SqliteWebApplicationFactory). One list prevents the "worker has no
// handler/service for the enqueued aesthetics job" divergence.
//
// The HumanAesExpert sidecar client is registered as a typed HttpClient. The
// feature is disabled by default (HumanAesExpert:Enabled=false) and the sidecar
// base URL is empty in committed config, so IAestheticModelClient reports
// unavailable and no analysis can run until an operator enables it.
public static class AestheticsServiceRegistration
{
    public static IServiceCollection AddNubArcaAesthetics(this IServiceCollection services)
    {
        services.AddScoped<IAestheticLabService, AestheticLabService>();
        services.AddScoped<IAestheticAnalysisService, AestheticAnalysisService>();
        // TV "Beauty Lab" QR mobile-upload capability (hash-only token; upload-
        // into-lab authority only). Registered here so the web API, worker, and
        // test fixture share one graph.
        services.AddScoped<IAestheticUploadSessionService, AestheticUploadSessionService>();
        services.AddHttpClient<IAestheticModelClient, HttpAestheticModelClient>(client =>
        {
            // HttpClient otherwise imposes its own 100-second default timeout,
            // which can pre-empt HumanAesExpert:RequestTimeoutSeconds (120s by
            // default). The linked CTS in HttpAestheticModelClient is the one
            // authoritative, configurable per-inference deadline.
            client.Timeout = Timeout.InfiniteTimeSpan;
        });
        return services;
    }
}
