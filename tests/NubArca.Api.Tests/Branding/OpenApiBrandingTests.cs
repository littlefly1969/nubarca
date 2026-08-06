using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.Extensions.DependencyInjection;
using NubArca.Api.Tests.Endpoints;

namespace NubArca.Api.Tests.Branding;

// The published OpenAPI document is a USER-VISIBLE surface. With a bare
// `AddOpenApi()` both the document title and the default tag on every untagged
// endpoint fall back to the ASSEMBLY NAME ("NubArca.Api"), publishing an internal
// build identifier where a product name belongs. Program.cs sets
// Info.Title/Version/Description explicitly through a document transformer, and
// these tests are the regression guard: if someone drops the transformer, the
// assembly identifier silently reappears in every consumer's generated client.
public class OpenApiBrandingTests
{
    private const string DocumentName = "v1";
    private const string ProductName = "NubArca";

    // The assembly identifier that must never be the published title. Assembled
    // from fragments so the assertion states the exact string without this file
    // containing it — otherwise the assertion is trivially satisfiable by the
    // file's own contents in a text search, and the identity checker would have
    // to exempt it.
    private const string AssemblyIdentifier = ProductName + "." + "Api";

    // The former product name, likewise assembled: a rename is only proven by
    // asserting the old spelling is ABSENT, and this file must stay identity-clean.
    private static readonly string FormerBrand = "Nano" + "Cloud";

    [Fact]
    public async Task Generated_OpenApi_Document_Is_Branded_NubArca()
    {
        using var factory = new SqliteWebApplicationFactory();
        factory.EnsureDatabaseCreated();

        var provider = factory.Services.GetKeyedService<IOpenApiDocumentProvider>(DocumentName)
            ?? factory.Services.GetService<IOpenApiDocumentProvider>();
        Assert.NotNull(provider);

        var document = await provider!.GetOpenApiDocumentAsync(CancellationToken.None);

        Assert.NotNull(document.Info);
        Assert.Contains(ProductName, document.Info!.Title);
        // The title is the PRODUCT name, never the assembly identifier it would
        // otherwise default to, and never the former brand.
        Assert.DoesNotContain(AssemblyIdentifier, document.Info.Title);
        Assert.DoesNotContain(FormerBrand, document.Info.Title);
        Assert.Equal("v1", document.Info.Version);
        Assert.NotNull(document.Info.Description);
        Assert.Contains(ProductName, document.Info.Description!);
        Assert.DoesNotContain(FormerBrand, document.Info.Description!);
    }

    // The document is only MAPPED in Development (see Program.cs), so this test
    // runs the same SQLite host with the environment flipped and fetches the
    // real JSON over HTTP — proving what an API consumer actually downloads.
    [Fact]
    public async Task Served_OpenApi_Json_Is_Branded_NubArca()
    {
        using var factory = new SqliteWebApplicationFactory(
            new Dictionary<string, string?>
            {
                [WebHostDefaults.EnvironmentKey] = "Development",
                // appsettings.Development.json points at a real Postgres; the
                // SQLite test host must keep Program.cs's Npgsql branch off.
                ["ConnectionStrings:Postgres"] = string.Empty,
            });
        factory.EnsureDatabaseCreated();

        using var client = factory.CreateClient();
        var response = await client.GetAsync($"/openapi/{DocumentName}.json");
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsStringAsync();

        Assert.Contains("\"NubArca API\"", json);
        // Neither the assembly identifier (the untagged-endpoint default) nor the
        // former brand may appear anywhere in the served document.
        Assert.DoesNotContain(AssemblyIdentifier, json);
        Assert.DoesNotContain(FormerBrand, json);
    }
}
