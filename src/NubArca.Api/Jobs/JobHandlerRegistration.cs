using Microsoft.Extensions.DependencyInjection;
using NubArca.Api.Jobs.Handlers;

namespace NubArca.Api.Jobs;

// SINGLE SOURCE OF TRUTH for the background-job handlers that BOTH hosts must
// register: the web API (Program.cs) and the CLI host that runs `jobs worker`
// (CliEntryPoint). Keeping one list here prevents the recurring "worker reports
// UnknownJobType" divergence — the API enqueues a job but the worker, built from
// a second handler list that was missed, has no handler for it (organizer,
// 2026-06-20; photo export, this change).
//
// The dependent SERVICES each handler needs (e.g. PhotoExportService,
// PhotoDateTakenOrganizerService, AdminImportService) are registered by each
// host alongside this call. AI handlers are registered separately by
// AddAiSubstrate (also called by both hosts).
public static class JobHandlerRegistration
{
    public static IServiceCollection AddNubArcaJobHandlers(this IServiceCollection services)
    {
        services.AddScoped<IJobHandler, MetadataBackfillJobHandler>();
        services.AddScoped<IJobHandler, VideoMetadataBackfillJobHandler>();
        services.AddScoped<IJobHandler, MediaDerivativesBackfillJobHandler>();
        services.AddScoped<IJobHandler, GalleryDerivativesRegenerationJobHandler>();
        services.AddScoped<IJobHandler, MediumPreviewRegenerationJobHandler>();
        services.AddScoped<IJobHandler, PosterRegenerationJobHandler>();
        services.AddScoped<IJobHandler, VideoHlsGenerateJobHandler>();
        services.AddScoped<IJobHandler, VideoHlsBackfillJobHandler>();
        services.AddScoped<IJobHandler, StorageReconcileJobHandler>();
        services.AddScoped<IJobHandler, AdminImportJobHandler>();
        services.AddScoped<IJobHandler, PhotoOrganizerJobHandler>();
        services.AddScoped<IJobHandler, PhotoExportBuildJobHandler>();
        services.AddScoped<IJobHandler, PlateAnalysisJobHandler>();
        services.AddScoped<IJobHandler, AestheticAnalysisJobHandler>();
        return services;
    }
}
