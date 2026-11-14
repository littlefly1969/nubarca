namespace NubArca.Api.Lithophanes;

public static class LithophaneRegistration
{
    // The API, CLI worker and endpoint fixtures all call this same registration.
    public static IServiceCollection AddNubArcaLithophanes(this IServiceCollection services)
    {
        services.AddSingleton<LithophaneDecodeGate>();
        services.AddScoped<LithophaneService>();
        return services;
    }
}
