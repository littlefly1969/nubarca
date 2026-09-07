using NubArca.PrintAgent.Api;

namespace NubArca.PrintAgent;

public static class PrintAgentServiceRegistration
{
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
