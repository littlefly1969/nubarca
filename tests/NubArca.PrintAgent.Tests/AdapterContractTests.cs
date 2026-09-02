using NubArca.PrintAgent.Adapters;
using NubArca.PrintAgent;

namespace NubArca.PrintAgent.Tests;

public sealed class AdapterContractTests
{
    [Fact]
    public async Task Fake_Adapter_Discovers_Ready_10x15_Color_Printer()
    {
        var output = Path.Combine(Path.GetTempPath(), $"nubarca-fake-{Guid.NewGuid():N}");
        try
        {
            var adapter = new FakePrinterAdapter(output);
            var printer = Assert.Single(await adapter.DiscoverAsync(default));
            Assert.Equal("fake", printer.AdapterKind);
            var capabilities = await adapter.GetCapabilitiesAsync(printer, default);
            Assert.Contains("10x15", capabilities.Formats);
            Assert.True(capabilities.Color);
            Assert.Equal("ready", (await adapter.GetStatusAsync(printer, default)).State);
        }
        finally
        {
            if (Directory.Exists(output)) Directory.Delete(output, recursive: true);
        }
    }

    [Fact]
    public void Cups_Is_An_Explicit_Future_Adapter_Not_A_Silent_Fallback()
    {
        var options = new PrintAgentOptions { Adapter = PrintAdapterKinds.Cups };
        options.NormalizeAndValidate();
        var error = Assert.Throws<NotSupportedException>(() => PrintAgentPlatform.CreatePrinterAdapter(options));
        Assert.Contains("not implemented", error.Message, StringComparison.OrdinalIgnoreCase);
    }
}
