using System.Text.Json;

namespace NubArca.Api.Tests.Albums;

/// <summary>
/// Finds a semantic token in a JSON payload by reading its SHAPE.
///
/// A privacy assertion that greps the raw text of a response is not a privacy
/// assertion, it is a coincidence detector. GUIDs are hexadecimal and "face" is
/// a valid hex string, so `Assert.DoesNotContain("face", body)` fails whenever
/// a random identifier happens to spell it — which turns a real gate into noise
/// people learn to re-run until it goes quiet.
///
/// Reading the shape is both deterministic AND stricter: a field named
/// `faceCount` is caught by its name whatever it holds, a leaked value is caught
/// wherever in the tree it sits, and the report names the JSON path instead of a
/// character offset.
/// </summary>
internal static class JsonLeakScan
{
    /// <summary>
    /// JSON paths where <paramref name="token"/> appears as part of a property
    /// name, or inside a string value that is not itself an identifier.
    /// </summary>
    internal static IReadOnlyList<string> Find(string body, string token)
    {
        using var document = JsonDocument.Parse(body);
        var offenders = new List<string>();
        Walk(document.RootElement, "$");
        return offenders;

        void Walk(JsonElement element, string at)
        {
            switch (element.ValueKind)
            {
                case JsonValueKind.Object:
                    foreach (var property in element.EnumerateObject())
                    {
                        if (property.Name.Contains(token, StringComparison.OrdinalIgnoreCase))
                        {
                            offenders.Add($"{at}.{property.Name} (property name)");
                        }

                        Walk(property.Value, $"{at}.{property.Name}");
                    }

                    break;

                case JsonValueKind.Array:
                    var index = 0;
                    foreach (var item in element.EnumerateArray())
                    {
                        Walk(item, $"{at}[{index++}]");
                    }

                    break;

                case JsonValueKind.String:
                    var value = element.GetString() ?? string.Empty;
                    // An identifier's hexadecimal is not content. This single
                    // exclusion is the whole difference between a gate that
                    // means something and one that fires at random.
                    if (Guid.TryParse(value, out _))
                    {
                        break;
                    }

                    if (value.Contains(token, StringComparison.OrdinalIgnoreCase))
                    {
                        offenders.Add($"{at} (value)");
                    }

                    break;
            }
        }
    }
}
