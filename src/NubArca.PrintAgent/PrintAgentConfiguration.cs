using Microsoft.Extensions.Configuration;

namespace NubArca.PrintAgent;

public static class PrintAgentConfiguration
{
    public static void AddInstanceFile(ConfigurationManager configuration, string[] args)
    {
        var indexes = args.Select((value, index) => (value, index))
            .Where(x => x.value.Equals("--config", StringComparison.OrdinalIgnoreCase))
            .Select(x => x.index)
            .ToArray();
        if (indexes.Length == 0) return;
        if (indexes.Length != 1 || indexes[0] + 1 >= args.Length
            || string.IsNullOrWhiteSpace(args[indexes[0] + 1]))
            throw new InvalidOperationException("--config requires exactly one absolute JSON file path.");

        var path = args[indexes[0] + 1];
        if (!Path.IsPathFullyQualified(path))
            throw new InvalidOperationException("--config requires an absolute JSON file path.");

        configuration.AddJsonFile(path, optional: false, reloadOnChange: false);
    }
}
