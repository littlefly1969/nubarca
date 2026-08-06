using NubArca.Api.Domain;

namespace NubArca.Api.Organizer;

// Pure, side-effect-free planning helpers for the date-taken organizer. Kept
// separate from the service so they are trivially unit-testable: effective-date
// resolution, template → folder segments, segment validation (no traversal),
// and deterministic conflict naming. No DB, no clock, no I/O.
public static class OrganizerPaths
{
    public const string UnknownDateFolder = "Unknown Date";

    // A valid single folder/file segment: non-empty after trim, ≤255 chars, no
    // '/' or '\', not '.'/'..'. Mirrors the file/folder name rules so a planned
    // path can never traverse, escape, or hit a reserved segment.
    public static bool IsValidSegment(string? segment)
    {
        if (string.IsNullOrWhiteSpace(segment)) return false;
        var s = segment.Trim();
        if (s.Length > 255) return false;
        if (s.Contains('/') || s.Contains('\\')) return false;
        if (s is "." or "..") return false;
        return true;
    }

    // Date → template folder segments. Always purely numeric/derived, so a
    // template can never inject a traversal segment.
    public static IReadOnlyList<string> TemplateSegments(OrganizerTemplate template, DateTime date)
    {
        var yyyy = date.Year.ToString("D4");
        var mm = date.Month.ToString("D2");
        var dd = date.Day.ToString("D2");
        return template switch
        {
            OrganizerTemplate.Year => new[] { yyyy },
            OrganizerTemplate.YearMonth => new[] { yyyy, mm },
            OrganizerTemplate.YearMonthDay => new[] { yyyy, mm, dd },
            _ => new[] { yyyy, $"{yyyy}-{mm}-{dd}" }, // YearDatedDay
        };
    }
}

// The resolved capture date for one file: which source won, the date to bucket
// by (null only when the file has no date AND is being skipped), and whether it
// should be skipped (missing + skip) or routed to the Unknown Date folder.
public readonly record struct DateResolution(
    string Source,
    DateTime? BucketDate,
    bool SkipMissing,
    bool UnknownFolder);

public static class PhotoDateTakenPlanner
{
    // Effective DateTaken precedence for organizing:
    //   1. user DateTakenOverride
    //   2. embedded original capture (EXIF DateTimeOriginal)  → metadata_original
    //   2b. other embedded capture date                        → metadata_fallback
    //   3. file created/import date — ONLY if the user opted into the fallback
    //   else: missing → skip or Unknown Date, per the chosen behavior.
    //
    // EXIF dates have no timezone in this model: the stored DateTaken is the
    // camera's wall-clock instant (kind Utc, no conversion), so its Y/M/D is the
    // date shown on the photo. We bucket on those components directly — stable
    // and deterministic regardless of the viewer's timezone.
    public static DateResolution Resolve(
        DateTime? userOverride,
        DateTime? embeddedDate,
        string? embeddedSource,
        DateTime createdAt,
        MissingDateBehavior missingBehavior)
    {
        if (userOverride is DateTime u)
        {
            return new DateResolution(PhotoOrganizerDateSources.UserOverride, u, false, false);
        }
        if (embeddedDate is DateTime e)
        {
            var src = string.Equals(embeddedSource, "DateTimeOriginal", StringComparison.Ordinal)
                ? PhotoOrganizerDateSources.MetadataOriginal
                : PhotoOrganizerDateSources.MetadataFallback;
            return new DateResolution(src, e, false, false);
        }

        // No override, no embedded date.
        return missingBehavior switch
        {
            MissingDateBehavior.FileCreated =>
                new DateResolution(PhotoOrganizerDateSources.FileCreatedFallback, createdAt, false, false),
            MissingDateBehavior.UnknownFolder =>
                new DateResolution(PhotoOrganizerDateSources.Missing, null, false, true),
            _ /* Skip */ =>
                new DateResolution(PhotoOrganizerDateSources.Missing, null, true, false),
        };
    }

    // The target folder segments BELOW the target root folder (excluding the
    // optional target-root-name segment, which the caller prepends). Empty only
    // when the resolution says skip.
    public static IReadOnlyList<string> TargetSegments(DateResolution resolution, OrganizerTemplate template)
    {
        if (resolution.SkipMissing) return Array.Empty<string>();
        if (resolution.UnknownFolder) return new[] { OrganizerPaths.UnknownDateFolder };
        return OrganizerPaths.TemplateSegments(template, resolution.BucketDate!.Value);
    }

    // Deterministic conflict resolution. `taken` is the set of names already
    // present (or reserved) in the target folder. Returns the final name to use,
    // or null when the policy is Skip and the base name is taken. KeepBoth
    // appends " (1)", " (2)", … before the extension.
    public static string? PickName(string baseName, ISet<string> taken, ConflictPolicy policy)
    {
        if (!taken.Contains(baseName)) return baseName;
        if (policy == ConflictPolicy.Skip) return null;

        var (stem, ext) = SplitExtension(baseName);
        for (var n = 1; n < int.MaxValue; n++)
        {
            var candidate = $"{stem} ({n}){ext}";
            if (!taken.Contains(candidate)) return candidate;
        }
        return null; // unreachable in practice
    }

    // Splits "name.jpg" → ("name", ".jpg"); "archive.tar.gz" → ("archive.tar",
    // ".gz"); "README" → ("README", ""). A leading-dot file like ".env" keeps
    // its whole name as the stem (no extension).
    public static (string Stem, string Ext) SplitExtension(string name)
    {
        var dot = name.LastIndexOf('.');
        if (dot <= 0) return (name, string.Empty);
        return (name[..dot], name[dot..]);
    }
}
