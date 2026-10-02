using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace NubArca.PrintAgent.Adapters;

/// <summary>The marker attributes of one CUPS queue, as the IPP query returned them.</summary>
public sealed record CupsMarkerSnapshot(
    IReadOnlyList<string> Messages, IReadOnlyList<int> Levels, long? ChangeTime, long? UpTime);

/// <summary>
/// Reads the physical media count a dye-sublimation printer reports through
/// CUPS. Pure, so every parse is tested without a printer.
///
/// Gutenprint's dye-sub backend talks to the DNP and publishes what it learns
/// as CUPS marker attributes; its <c>marker-message</c> carries the count
/// itself — <c>187 native prints remaining on '4x6' media</c>. That integer is
/// the ONLY thing turned into a number here. <c>marker-levels</c> is a
/// percentage, and no capacity is assumed to turn a percentage into prints: a
/// printer that reports only a level reports no count.
/// </summary>
public static partial class CupsMarkers
{
    /// <summary>No media a photo printer takes holds anywhere near this many prints.</summary>
    public const int MaxPlausiblePrints = 100_000;

    /// <summary>A reading this old is still a reading, but no older is believed.</summary>
    public const long MaxAgeSeconds = 30L * 24 * 60 * 60;

    [GeneratedRegex(@"^\s*(\d{1,9})\s+(?:native\s+)?prints?\s+remaining\b",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex RemainingPrints();

    /// <summary>
    /// The marker attributes in ipptool's XML (plist) report, or null when the
    /// report is not a successful one. Never resolves the plist DTD.
    /// </summary>
    public static CupsMarkerSnapshot? ParsePlist(string plist)
    {
        if (string.IsNullOrWhiteSpace(plist)) return null;
        XDocument document;
        try
        {
            using var reader = XmlReader.Create(new StringReader(plist), new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Ignore,
                XmlResolver = null,
                MaxCharactersInDocument = 1_000_000,
            });
            document = XDocument.Load(reader);
        }
        catch (XmlException)
        {
            return null;
        }

        var test = document.Descendants("key")
            .FirstOrDefault(k => k.Value == "Tests")?.ElementsAfterSelf("array").FirstOrDefault()
            ?.Elements("dict").FirstOrDefault();
        if (test is null || !BoolAfter(test, "Successful")) return null;

        var messages = new List<string>();
        var levels = new List<int>();
        long? changeTime = null, upTime = null;
        var responses = ValueAfter(test, "ResponseAttributes");
        foreach (var group in responses?.Elements("dict") ?? [])
        {
            foreach (var key in group.Elements("key"))
            {
                var value = key.ElementsAfterSelf().FirstOrDefault();
                if (value is null) continue;
                switch (key.Value)
                {
                    case "marker-message":
                        messages.AddRange(Strings(value));
                        break;
                    case "marker-levels":
                        levels.AddRange(Integers(value).Where(v => v is >= int.MinValue and <= int.MaxValue)
                            .Select(v => (int)v));
                        break;
                    case "marker-change-time":
                        changeTime = Integers(value).Cast<long?>().FirstOrDefault();
                        break;
                    case "printer-up-time":
                        upTime = Integers(value).Cast<long?>().FirstOrDefault();
                        break;
                }
            }
        }
        return new CupsMarkerSnapshot(messages, levels, changeTime, upTime);
    }

    /// <summary>
    /// The count in the marker messages, or null when none states one — or
    /// when several state DIFFERENT ones: a photo printer has one media, and
    /// two disagreeing numbers are no number at all.
    /// </summary>
    public static int? RemainingFrom(IReadOnlyList<string> messages)
    {
        int? found = null;
        foreach (var message in messages)
        {
            var match = RemainingPrints().Match(message);
            if (!match.Success) continue;
            if (!long.TryParse(match.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var value)
                || value > MaxPlausiblePrints)
                return null;
            if (found is not null && found != (int)value) return null;
            found = (int)value;
        }
        return found;
    }

    /// <summary>The media status the snapshot supports, never more than it says.</summary>
    public static PrinterMediaStatus StatusOf(CupsMarkerSnapshot? snapshot)
    {
        if (snapshot is null) return PrinterMediaStatus.Unavailable;
        var remaining = RemainingFrom(snapshot.Messages);
        if (remaining is null) return PrinterMediaStatus.Unavailable;
        int? age = null;
        if (snapshot.ChangeTime is long change && change > 0 && snapshot.UpTime is long up && up >= change)
            age = (int)Math.Min(up - change, MaxAgeSeconds);
        return new PrinterMediaStatus(remaining, age);
    }

    private static XElement? ValueAfter(XElement dict, string key) =>
        dict.Elements("key").FirstOrDefault(k => k.Value == key)?.ElementsAfterSelf().FirstOrDefault();

    private static bool BoolAfter(XElement dict, string key) => ValueAfter(dict, key)?.Name.LocalName == "true";

    private static IEnumerable<string> Strings(XElement value) => value.Name.LocalName switch
    {
        "string" => [value.Value],
        "array" => value.Elements("string").Select(e => e.Value),
        _ => [],
    };

    private static IEnumerable<long> Integers(XElement value)
    {
        var items = value.Name.LocalName == "array" ? value.Elements("integer") : [value];
        foreach (var item in items)
        {
            if (item.Name.LocalName == "integer"
                && long.TryParse(item.Value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var n))
                yield return n;
        }
    }
}
