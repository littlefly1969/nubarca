using System.Globalization;

namespace NubArca.Api.Print;

/// <summary>
/// What an owner asks to print: one of their own photographs, on a printer
/// they own or one lent to them. Everything that is authority — the file's
/// bytes, its owner, the printer's paper, the date — is the server's to
/// resolve; the browser names a photograph, a printer, the paper it composed
/// for and how the photograph is framed, and nothing else.
/// </summary>
public sealed record OwnerPhotoPrintSubmitRequest(
    Guid? FileItemId,
    Guid? PrintStationId,
    Guid? PrinterDeviceId,
    /// <summary>The paper the composer was laid out for; the printer must still have it in.</summary>
    string? ExpectedPaperSize,
    /// <summary>"portrait" or "landscape": which way the sheet stands.</summary>
    string? Orientation,
    PartyPrintPlacementRequest? Placement,
    /// <summary>Off by default: a print is the photograph, unless a date is asked for.</summary>
    bool IncludeDate = false,
    /// <summary>"it", "en", "es" or "de" — the date's written form.</summary>
    string? DateLocale = null,
    /// <summary>The browser's IANA zone, so "today" is the day the person sees.</summary>
    string? TimeZone = null,
    /// <summary>A format of the shared catalogue (PrintLayouts). Absent: one photograph.</summary>
    string? Layout = null,
    /// <summary>
    /// "fullBleed" (Piena: the photograph to the edges, the date on it — the
    /// default for one photograph) or "framed" (Cornice: a border and a band).
    /// Four photographs and the strips are always framed.
    /// </summary>
    string? Style = null,
    /// <summary>
    /// The photographs in the order they were arranged, each with its framing.
    /// Absent: <see cref="FileItemId"/> and <see cref="Placement"/>, the
    /// one-photograph request every earlier client sends.
    /// </summary>
    IReadOnlyList<OwnerPrintPhotoRequest>? Photos = null,
    /// <summary>One line of the owner's own in a framed sheet's band (OwnerPrintText).</summary>
    string? Caption = null,
    /// <summary>The NubArca wordmark in a framed sheet's band. Off unless asked for.</summary>
    bool Brand = false);

/// <summary>One photograph of a print and how it is framed in its slot.</summary>
public sealed record OwnerPrintPhotoRequest(Guid? FileItemId, PartyPrintPlacementRequest? Placement);

/// <summary>
/// The bounds on the one line an owner may write on their own print.
///
/// One line, short enough for the band to show at a size that reads, with no
/// control characters or breaks — and nothing the print's typefaces cannot
/// draw: an emoji would come out as an empty box, so it is refused rather than
/// printed as one. Refused, never silently truncated: the owner sees in the
/// preview exactly what the paper will say.
/// </summary>
public static class OwnerPrintText
{
    public const int CaptionMaxLength = 40;

    public static bool TryNormaliseCaption(string? value, out string? normalised)
    {
        normalised = null;
        if (value is null) return true;
        var flat = new string(value.Select(c => char.IsControl(c) ? ' ' : c).ToArray());
        while (flat.Contains("  ", StringComparison.Ordinal)) flat = flat.Replace("  ", " ", StringComparison.Ordinal);
        flat = flat.Trim();
        if (flat.Length == 0) return true;
        if (flat.Length > CaptionMaxLength || flat.Any(char.IsSurrogate)) return false;
        normalised = flat;
        return true;
    }
}

/// <summary>An accepted direct print — the job, its short code, and how many sheets wait ahead of it.</summary>
public sealed record OwnerPhotoPrintAccepted(
    Guid JobId, string ShortCode, string State, int QueueAhead,
    /// <summary>
    /// The printer's physical media count as last reported — a snapshot for
    /// information, never decremented here: the Print Agent reports the new
    /// value once the printer itself does.
    /// </summary>
    int? MediaRemainingPrints);

/// <summary>The date a direct print would carry, resolved the way the print will be.</summary>
public sealed record OwnerPhotoPrintDate(string Date, string Source);

/// <summary>The stable reasons a direct print is refused, as the wire spells them.</summary>
public static class OwnerPhotoPrintErrors
{
    public const string InvalidRequest = "invalid_request";
    /// <summary>A format, style or count of photographs the catalogue does not have.</summary>
    public const string InvalidLayout = "invalid_layout";
    /// <summary>A line too long for the band, or one the print cannot draw.</summary>
    public const string InvalidCaption = "invalid_caption";
    public const string NotFound = "not_found";
    public const string InvalidSource = "invalid_source";
    public const string NotImage = "not_image";
    public const string PrinterUnavailable = "printer_unavailable";
    public const string PrinterNotFound = "printer_not_found";
    public const string PrinterOffline = "printer_offline";
    public const string PaperChanged = "paper_changed";
    public const string FormatUnsupported = "format_unsupported";
    public const string ShareExhausted = "share_exhausted";
    public const string ShareRevoked = "share_revoked";
    public const string InvalidOrientation = "invalid_orientation";
    public const string InvalidPlacement = "invalid_placement";
    public const string InvalidTimezone = "invalid_timezone";
    public const string IdempotencyConflict = "idempotency_conflict";
    public const string RenderFailed = "render_failed";
}

public sealed record OwnerPhotoPrintResult(OwnerPhotoPrintAccepted? Accepted, string? Error)
{
    public static OwnerPhotoPrintResult Accept(OwnerPhotoPrintAccepted accepted) => new(accepted, null);
    public static OwnerPhotoPrintResult Refuse(string error) => new(null, error);
}

/// <summary>Everything the renderer needs for one owner print, and nothing about who asked.</summary>
public sealed record OwnerPhotoComposition(
    byte[] Bytes,
    string Paper,
    bool Portrait,
    PhotoPlacement Placement,
    /// <summary>The text to print bottom-right of the visible photograph, or null for none.</summary>
    string? DateText = null,
    PrintCalibration? Calibration = null);

/// <summary>
/// The date a direct print carries, and how it is written. One table, mirrored
/// by the browser (<c>ownerPhotoPrintDate.ts</c>), so the preview shows the very
/// characters the paper gets — never left to a locale library to decide.
/// </summary>
public static class OwnerPhotoPrintDates
{
    public const string SourceUser = "user";
    public const string SourceEmbedded = "embedded";
    public const string SourceToday = "today";
    public const string SourceNone = "none";

    private static readonly IReadOnlyDictionary<string, string> Formats = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["it"] = "dd/MM/yyyy",
        ["es"] = "dd/MM/yyyy",
        ["de"] = "dd.MM.yyyy",
        ["en"] = "MM/dd/yyyy",
    };

    public static bool IsKnownLocale(string? locale) => locale is not null && Formats.ContainsKey(locale);

    public static string Format(DateOnly date, string locale) =>
        date.ToString(Formats[locale], CultureInfo.InvariantCulture);

    /// <summary>
    /// The photograph's date, in this order and nothing else: the owner's own
    /// correction, the date the camera wrote, else today in the person's zone.
    /// Never the upload date — a photograph with no date of its own is printed
    /// with today's, as the person asked.
    ///
    /// A stored capture date is the camera's WALL-CLOCK time kept as UTC (see
    /// the metadata extractor), so its calendar day is read straight off it,
    /// with no zone conversion that could move it across midnight.
    /// </summary>
    public static (DateOnly Date, string Source) Resolve(
        DateTime? userOverride, DateTime? embedded, DateTime utcNow, TimeZoneInfo zone)
    {
        if (userOverride is DateTime user) return (DateOnly.FromDateTime(user), SourceUser);
        if (embedded is DateTime camera) return (DateOnly.FromDateTime(camera), SourceEmbedded);
        return (DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(
            DateTime.SpecifyKind(utcNow, DateTimeKind.Utc), zone)), SourceToday);
    }

    /// <summary>An IANA zone the server knows, or null — never a fallback that would print another day.</summary>
    public static TimeZoneInfo? Zone(string? id)
    {
        if (string.IsNullOrWhiteSpace(id) || id.Length > 64) return null;
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(id);
        }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            return null;
        }
    }
}
