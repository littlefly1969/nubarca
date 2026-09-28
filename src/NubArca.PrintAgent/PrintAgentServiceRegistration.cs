using NubArca.PrintAgent.Api;
using NubArca.PrintAgent.Networking;
using NubArca.PrintAgent.Setup;

namespace NubArca.PrintAgent;

public static class PrintAgentServiceRegistration
{
    /// <summary>
    /// The Linux Print Box: NetworkManager provisioning and the local setup page,
    /// in this same process beside the cloud worker.
    /// </summary>
    public static IServiceCollection AddPrintBoxSetup(this IServiceCollection services,
        NetworkProvisioningOptions options)
    {
        services.AddSingleton(options);
        services.AddSingleton<INetworkManager, NetworkManagerCli>();
        services.AddSingleton<NetworkProvisioningService>();
        services.AddHostedService(sp => sp.GetRequiredService<NetworkProvisioningService>());
        services.AddSingleton<PrintBoxStatusService>();
        services.AddHostedService<SetupWebHost>();
        return services;
    }

    public static IServiceCollection AddSharedPrintAgentApiClient(this IServiceCollection services)
    {
        // The station credential is in-memory state on this client. The worker
        // loads it once, and the execution coordinator must use that SAME
        // instance for download/submission/result calls. AddHttpClient<T>
        // alone registers a transient and silently splits those two paths.
        services.AddSingleton(sp => new PrintAgentApiClient(
            sp.GetRequiredService<IHttpClientFactory>()
                .CreateClient(nameof(PrintAgentApiClient))));
        return services;
    }
}
