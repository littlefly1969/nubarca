using Microsoft.Extensions.DependencyInjection;

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
        services.AddScoped<MailArchiveService>();
        services.AddScoped<MailImportRunner>();
        services.AddScoped<MailMessageQueryService>();
        services.AddScoped<MailMessageDeletionService>();
        return services;
    }
}
