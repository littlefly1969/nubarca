using System.Linq;
using Microsoft.Extensions.DependencyInjection;
using NubArca.Api.Jobs;
using NubArca.Api.Jobs.Handlers;
using Xunit;

namespace NubArca.Api.Tests.Jobs;

// Regression guard for the recurring "worker reports UnknownJobType" bug: the
// web API host and the CLI `jobs worker` host historically kept SEPARATE handler
// lists, so a handler added to one (the API) was missing from the other (the
// worker) — organizer (2026-06-20) and photo export both hit this. Both hosts
// now register the SAME shared list (AddNubArcaJobHandlers); these tests pin
// that list so a new handler can't be added to one host only.
public sealed class JobHandlerRegistrationTests
{
    private static List<Type> RegisteredHandlerTypes()
    {
        var services = new ServiceCollection();
        services.AddNubArcaJobHandlers();
        return services
            .Where(d => d.ServiceType == typeof(IJobHandler))
            .Select(d => d.ImplementationType!)
            .ToList();
    }

    [Fact]
    public void Shared_List_Includes_Photo_Export_Build_Handler()
    {
        Assert.Contains(typeof(PhotoExportBuildJobHandler), RegisteredHandlerTypes());
    }

    [Fact]
    public void Shared_List_Includes_All_NonAi_Handlers()
    {
        var types = RegisteredHandlerTypes();
        Assert.Contains(typeof(MetadataBackfillJobHandler), types);
        Assert.Contains(typeof(MediaDerivativesBackfillJobHandler), types);
        Assert.Contains(typeof(StorageReconcileJobHandler), types);
        Assert.Contains(typeof(AdminImportJobHandler), types);
        Assert.Contains(typeof(PhotoOrganizerJobHandler), types);
        Assert.Contains(typeof(PhotoExportBuildJobHandler), types);
    }
}
