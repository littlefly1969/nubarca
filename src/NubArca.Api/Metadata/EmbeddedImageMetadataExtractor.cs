using System.Globalization;
using System.Text;
using System.Text.Json;
using MetadataExtractor;
using MetadataExtractor.Formats.Exif;
using MetadataExtractor.Formats.Icc;
using MetadataExtractor.Formats.Xmp;
using NubArca.Api.Domain;
using Directory = MetadataExtractor.Directory;

namespace NubArca.Api.Metadata;

// Embedded image metadata extraction built on the MetadataExtractor library
// (pure-managed, no native dependencies). Reads EXIF / GPS IFD / IPTC / XMP /
// ICC / MakerNotes / format-specific chunks for the formats the library
// supports (JPEG, PNG, WebP, TIFF, GIF, BMP, HEIF/AVIF, …).
//
// Hard contract: Extract NEVER throws. Corrupt, unsupported, oversized, or
// otherwise hostile metadata always resolves to a safe Status + sanitized
// ErrorCode so the upload pipeline can complete.
public sealed class EmbeddedImageMetadataExtractor : IEmbeddedMetadataExtractor
{
    // Bump when extraction logic changes so a future backfill can re-run only
    // the rows produced by an older extractor.
    public const int Version = 1;

    // Bound the internal raw document so pathological metadata payloads can't
    // bloat the row or the jsonb column.
    private const int MaxRawJsonBytes = 64 * 1024;
    private const int MaxTagValueLength = 1024;
    private const int MaxTagsPerDirectory = 256;
    private const int MaxDirectories = 64;

    public ImageMetadataExtractionResult Extract(Stream imageStream)
    {
        ArgumentNullException.ThrowIfNull(imageStream);

        IReadOnlyList<Directory> directories;
        try
        {
            directories = ImageMetadataReader.ReadMetadata(imageStream);
        }
        catch (ImageProcessingException)
        {
            // The bytes are not a format MetadataExtractor recognizes.
            return ImageMetadataExtractionResult.ForStatus(
                MetadataStatuses.Skipped, MetadataErrorCodes.UnsupportedFormat, Version);
        }
        catch (IOException)
        {
            return ImageMetadataExtractionResult.ForStatus(
                MetadataStatuses.Failed, MetadataErrorCodes.IoError, Version);
        }
        catch (Exception)
        {
            // Defensive: any unexpected parser failure is non-fatal.
            return ImageMetadataExtractionResult.ForStatus(
                MetadataStatuses.Failed, MetadataErrorCodes.Unexpected, Version);
        }

        try
        {
            return BuildResult(directories);
        }
        catch (Exception)
        {
            // BuildResult should be total, but never let mapping crash the host.
            return ImageMetadataExtractionResult.ForStatus(
                MetadataStatuses.Failed, MetadataErrorCodes.Unexpected, Version);
        }
    }

    private static ImageMetadataExtractionResult BuildResult(IReadOnlyList<Directory> directories)
    {
        var ifd0 = directories.OfType<ExifIfd0Directory>().FirstOrDefault();
        var sub = directories.OfType<ExifSubIfdDirectory>().FirstOrDefault();
        var gps = directories.OfType<GpsDirectory>().FirstOrDefault();
        var icc = directories.OfType<IccDirectory>().FirstOrDefault();
        var xmp = directories.OfType<XmpDirectory>().FirstOrDefault();

        var (dateTaken, dateSource) = ReadDateTaken(ifd0, sub);
        var (gpsLat, gpsLon, gpsAlt) = ReadGps(gps, xmp);

        var (rawJson, truncated) = BuildRawDocument(directories);

        return new ImageMetadataExtractionResult
        {
            Status = MetadataStatuses.Completed,
            ErrorCode = truncated ? MetadataErrorCodes.RawTruncated : null,
            Version = Version,
            RawMetadataJson = rawJson,

            DateTaken = dateTaken,
            DateTakenSource = dateSource,
            DateTakenOffset = CleanString(FirstString(ExifDirectoryBase.TagTimeZoneOriginal, sub, ifd0), 16),

            Orientation = FirstInt(ExifDirectoryBase.TagOrientation, ifd0, sub),

            CameraMake = CleanString(FirstString(ExifDirectoryBase.TagMake, ifd0, sub), 128),
            CameraModel = CleanString(FirstString(ExifDirectoryBase.TagModel, ifd0, sub), 128),
            LensMake = CleanString(FirstString(ExifDirectoryBase.TagLensMake, sub, ifd0), 128),
            LensModel = CleanString(FirstString(ExifDirectoryBase.TagLensModel, sub, ifd0), 128),
            Software = CleanString(FirstString(ExifDirectoryBase.TagSoftware, ifd0, sub), 256),
            BodySerialNumber = CleanString(FirstString(ExifDirectoryBase.TagBodySerialNumber, sub, ifd0), 128),
            LensSerialNumber = CleanString(FirstString(ExifDirectoryBase.TagLensSerialNumber, sub, ifd0), 128),

            IsoSpeed = FirstInt(ExifDirectoryBase.TagIsoEquivalent, sub, ifd0),
            FNumber = FirstDouble(ExifDirectoryBase.TagFNumber, sub, ifd0),
            ExposureTime = CleanString(FirstDescription(ExifDirectoryBase.TagExposureTime, sub, ifd0), 64),
            FocalLength = FirstDouble(ExifDirectoryBase.TagFocalLength, sub, ifd0),
            FocalLength35mm = FirstInt(ExifDirectoryBase.Tag35MMFilmEquivFocalLength, sub, ifd0),
            ExposureBias = CleanString(FirstDescription(ExifDirectoryBase.TagExposureBias, sub, ifd0), 64),
            ExposureProgram = CleanString(FirstDescription(ExifDirectoryBase.TagExposureProgram, sub, ifd0), 64),
            MeteringMode = CleanString(FirstDescription(ExifDirectoryBase.TagMeteringMode, sub, ifd0), 64),
            Flash = CleanString(FirstDescription(ExifDirectoryBase.TagFlash, sub, ifd0), 128),
            WhiteBalance = CleanString(FirstDescription(ExifDirectoryBase.TagWhiteBalance, sub, ifd0), 64),

            ColorSpace = CleanString(FirstDescription(ExifDirectoryBase.TagColorSpace, sub, ifd0), 64),
            HasIccProfile = icc is not null,
            IccProfileName = CleanString(ReadIccName(icc), 256),

            GpsLatitude = gpsLat,
            GpsLongitude = gpsLon,
            GpsAltitude = gpsAlt,
        };
    }

    private static (DateTime? Value, string? Source) ReadDateTaken(
        ExifIfd0Directory? ifd0, ExifSubIfdDirectory? sub)
    {
        // EXIF dates carry no timezone unless an Offset tag is present. We
        // normalize the wall-clock value to a UTC-nominal DateTime so it is
        // storable in a timestamptz column; the offset (when known) is kept
        // separately. Invalid / ambiguous values simply yield no date.
        if (sub is not null && sub.TryGetDateTime(ExifDirectoryBase.TagDateTimeOriginal, out var orig))
        {
            return (DateTime.SpecifyKind(orig, DateTimeKind.Utc), "DateTimeOriginal");
        }
        if (sub is not null && sub.TryGetDateTime(ExifDirectoryBase.TagDateTimeDigitized, out var digi))
        {
            return (DateTime.SpecifyKind(digi, DateTimeKind.Utc), "DateTimeDigitized");
        }
        if (ifd0 is not null && ifd0.TryGetDateTime(ExifDirectoryBase.TagDateTime, out var dt))
        {
            return (DateTime.SpecifyKind(dt, DateTimeKind.Utc), "DateTime");
        }
        return (null, null);
    }

    private static (double? Lat, double? Lon, double? Alt) ReadGps(GpsDirectory? gps, XmpDirectory? xmp)
    {
        double? lat = null, lon = null, alt = null;

        if (gps is not null)
        {
            try
            {
                var geo = gps.GetGeoLocation();
                if (geo is { } g && !g.IsZero)
                {
                    lat = g.Latitude;
                    lon = g.Longitude;
                }
            }
            catch
            {
                // malformed GPS IFD — leave coordinates null, try XMP below.
            }

            if (gps.TryGetDouble(GpsDirectory.TagAltitude, out var altitude))
            {
                // AltitudeRef == 1 means below sea level.
                alt = gps.TryGetInt32(GpsDirectory.TagAltitudeRef, out var altRef) && altRef == 1
                    ? -altitude
                    : altitude;
            }
        }

        // Slice 86: many images (edited/exported, Google Photos, some Android /
        // HEIC pipelines) carry GPS ONLY in the XMP packet, which the EXIF GPS
        // IFD read above misses entirely. Fall back to XMP so `hasGps` is
        // accurate. Coordinates remain owner-internal (never exposed).
        if ((lat is null || lon is null) && xmp is not null)
        {
            try
            {
                var props = xmp.GetXmpProperties();
                var xlat = ParseXmpGpsCoordinate(
                    GetXmpValue(props, "exif:GPSLatitude"), GetXmpValue(props, "exif:GPSLatitudeRef"));
                var xlon = ParseXmpGpsCoordinate(
                    GetXmpValue(props, "exif:GPSLongitude"), GetXmpValue(props, "exif:GPSLongitudeRef"));
                if (xlat is { } la && xlon is { } lo
                    && la is >= -90 and <= 90 && lo is >= -180 and <= 180
                    && !(la == 0 && lo == 0))
                {
                    lat = la;
                    lon = lo;
                    if (alt is null)
                    {
                        var xalt = ParseXmpGpsCoordinate(GetXmpValue(props, "exif:GPSAltitude"), null);
                        if (xalt is { } a && a is > -100000 and < 100000)
                        {
                            alt = a;
                        }
                    }
                }
            }
            catch
            {
                // malformed XMP — leave whatever EXIF produced (possibly null).
            }
        }

        return (lat, lon, alt);
    }

    private static string? GetXmpValue(IDictionary<string, string> props, string key)
        => props.TryGetValue(key, out var v) ? v : null;

    // Parse an XMP/EXIF-style GPS coordinate string into signed decimal degrees.
    // Handles: "DDD,MM.mmmmH" / "DDD,MM,SSH" (degrees, minutes[, seconds] +
    // hemisphere letter), a separate ref letter (N/S/E/W), and plain signed
    // decimals ("-37.81"). Returns null on anything it can't parse cleanly.
    // `internal` so it can be unit-tested directly without crafting image bytes.
    internal static double? ParseXmpGpsCoordinate(string? value, string? refValue)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var s = value.Trim();
        char hemi = '\0';
        if (char.IsLetter(s[^1]))
        {
            hemi = char.ToUpperInvariant(s[^1]);
            s = s[..^1].Trim();
        }
        else if (!string.IsNullOrWhiteSpace(refValue))
        {
            var r = refValue.Trim();
            if (r.Length > 0 && char.IsLetter(r[0]))
            {
                hemi = char.ToUpperInvariant(r[0]);
            }
        }

        double dec;
        if (s.Contains(','))
        {
            var parts = s.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0
                || !double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var deg))
            {
                return null;
            }
            double min = 0, sec = 0;
            if (parts.Length >= 2
                && !double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out min))
            {
                return null;
            }
            if (parts.Length >= 3
                && !double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out sec))
            {
                return null;
            }
            var sign = deg < 0 ? -1.0 : 1.0;
            dec = sign * (Math.Abs(deg) + (min / 60.0) + (sec / 3600.0));
        }
        else if (!double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out dec))
        {
            return null;
        }

        if (hemi is 'S' or 'W')
        {
            dec = -Math.Abs(dec);
        }

        return double.IsNaN(dec) || double.IsInfinity(dec) ? null : dec;
    }

    private static string? ReadIccName(IccDirectory? icc)
    {
        if (icc is null)
        {
            return null;
        }
        try
        {
            // 'desc' tag (0x64657363) holds the profile description.
            const int tagDesc = 0x64657363;
            return icc.ContainsTag(tagDesc) ? icc.GetDescription(tagDesc) : null;
        }
        catch
        {
            return null;
        }
    }

    // Builds the internal raw structured document: { directoryName: { tagName:
    // description } }. Values are sanitized + length-capped; tag/dir counts are
    // bounded; the whole document is capped at MaxRawJsonBytes. Returns the
    // JSON plus whether truncation occurred.
    private static (string Json, bool Truncated) BuildRawDocument(IReadOnlyList<Directory> directories)
    {
        var doc = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);

        var dirCount = 0;
        foreach (var dir in directories)
        {
            if (dirCount++ >= MaxDirectories)
            {
                break;
            }

            var tags = new Dictionary<string, string>(StringComparer.Ordinal);
            var tagCount = 0;
            foreach (var tag in dir.Tags)
            {
                if (tagCount++ >= MaxTagsPerDirectory)
                {
                    break;
                }
                var value = CleanString(tag.Description, MaxTagValueLength);
                if (value is null)
                {
                    continue;
                }
                // tag.Name is always non-null; later duplicates are ignored.
                tags[tag.Name] = value;
            }

            if (dir.Errors.Any())
            {
                tags["_errors"] = CleanString(string.Join("; ", dir.Errors), MaxTagValueLength) ?? "";
            }

            if (tags.Count > 0)
            {
                // Distinct directory key (some files have multiple of a kind).
                var key = doc.ContainsKey(dir.Name) ? $"{dir.Name} ({dirCount})" : dir.Name;
                doc[key] = tags;
            }
        }

        var json = JsonSerializer.Serialize(doc);
        if (Encoding.UTF8.GetByteCount(json) > MaxRawJsonBytes)
        {
            return ("{\"_truncated\":true}", true);
        }
        return (json, false);
    }

    // --- multi-directory typed readers -------------------------------------

    private static string? FirstString(int tag, params Directory?[] dirs)
    {
        foreach (var d in dirs)
        {
            if (d is not null && d.ContainsTag(tag))
            {
                var s = d.GetString(tag);
                if (!string.IsNullOrWhiteSpace(s))
                {
                    return s;
                }
            }
        }
        return null;
    }

    private static string? FirstDescription(int tag, params Directory?[] dirs)
    {
        foreach (var d in dirs)
        {
            if (d is not null && d.ContainsTag(tag))
            {
                var s = d.GetDescription(tag);
                if (!string.IsNullOrWhiteSpace(s))
                {
                    return s;
                }
            }
        }
        return null;
    }

    private static int? FirstInt(int tag, params Directory?[] dirs)
    {
        foreach (var d in dirs)
        {
            if (d is not null && d.TryGetInt32(tag, out var v))
            {
                return v;
            }
        }
        return null;
    }

    private static double? FirstDouble(int tag, params Directory?[] dirs)
    {
        foreach (var d in dirs)
        {
            if (d is not null && d.TryGetDouble(tag, out var v))
            {
                return v;
            }
        }
        return null;
    }

    // Trims, strips control characters, and caps length. Returns null for
    // null/blank input so empty values don't pollute the typed columns or raw
    // document.
    private static string? CleanString(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var sb = new StringBuilder(value.Length);
        foreach (var ch in value)
        {
            // Drop C0/C1 control chars (incl. NUL) but keep normal whitespace.
            if (ch is '\t' or '\n' or '\r' || !char.IsControl(ch))
            {
                sb.Append(ch);
            }
        }

        var cleaned = sb.ToString().Trim();
        if (cleaned.Length == 0)
        {
            return null;
        }
        return cleaned.Length > maxLength ? cleaned[..maxLength] : cleaned;
    }
}
