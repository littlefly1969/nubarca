using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NubArca.Api.Ingestion;
using NubArca.Api.Uploads;

namespace NubArca.Api.MailArchive;

// SINGLE SOURCE OF TRUTH for the MailArchive service graph, called by every
// host: the web API (Program.cs), the CLI/worker host that runs the
// mail.import job (CliEntryPoint) and the endpoint test fixture.
public static class MailArchiveServiceRegistration
{
    public static IServiceCollection AddNubArcaMailArchive(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<MailArchiveOptions>(configuration.GetSection(MailArchiveOptions.SectionName));
        services.AddSingleton<IPstEngine, ProcessPstEngine>();
        services.AddSingleton<MailWorkspace>();
        // The runner creates attachments under upload-operation claims and hands
        // each new file to the ordinary post-ingestion pipeline. The web host
        // registers both for owner uploads already; the worker host does not,
        // so add them here without duplicating the web registrations.
        services.TryAddScoped<IUploadIdempotencyService, UploadIdempotencyService>();
        services.TryAddScoped<IPostIngestionMediaPipelineService, PostIngestionMediaPipelineService>();
        services.AddScoped<MailArchiveService>();
        services.AddScoped<MailImportRunner>();
        services.AddScoped<MailMessageQueryService>();
        services.AddScoped<MailMessageDeletionService>();
        return services;
    }
}
