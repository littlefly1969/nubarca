using Microsoft.Extensions.DependencyInjection;
using NubArca.Api.Aesthetics;
using NubArca.Api.Aesthetics.Sidecar;

namespace NubArca.Api.Tests.Aesthetics;

public sealed class AestheticHttpClientRegistrationTests
{
    [Fact]
    public void Typed_client_disables_implicit_100_second_transport_timeout()
    {
        var services = new ServiceCollection();
        services.AddOptions();
        services.Configure<AestheticsOptions>(options =>
        {
            options.SidecarBaseUrl = "http://human-aesexpert:8091";
            options.RequestTimeoutSeconds = 120;
        });
        services.AddNubArcaAesthetics();

        using var provider = services.BuildServiceProvider();
        var client = Assert.IsType<HttpAestheticModelClient>(
            provider.GetRequiredService<IAestheticModelClient>());

        Assert.Equal(Timeout.InfiniteTimeSpan, client.TransportTimeout);
    }
}
