using System.Text.Json;
using NubArca.Api.Domain.Print;

namespace NubArca.Api.Print;

public static class PrintCapabilityMatcher
{
    public static bool SupportsFormat(string snapshotJson, string requiredFormat)
    {
        try
        {
            using var document = JsonDocument.Parse(snapshotJson);
            if (!TryGetFormats(document.RootElement, out var formats)) return false;
            return formats.EnumerateArray().Any(value => value.ValueKind == JsonValueKind.String
                && string.Equals(value.GetString(), requiredFormat, StringComparison.OrdinalIgnoreCase));
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>The cut sheets a printer reports it makes, in <see cref="PrintFormats.Cuts"/> order.</summary>
    public static IReadOnlyList<string> CutFormats(string snapshotJson) =>
        [.. PrintFormats.Cuts.Where(format => SupportsFormat(snapshotJson, format))];

    private static bool TryGetFormats(JsonElement root, out JsonElement formats)
    {
        if (root.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in root.EnumerateObject())
            {
                if (property.Name.Equals("formats", StringComparison.OrdinalIgnoreCase)
                    && property.Value.ValueKind == JsonValueKind.Array)
                {
                    formats = property.Value;
                    return true;
                }
            }
        }
        formats = default;
        return false;
    }
}
