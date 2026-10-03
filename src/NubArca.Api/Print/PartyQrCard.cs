namespace NubArca.Api.Print;

/// <summary>
/// What a host asks to print: the party's QR card, with one of their
/// photographs over the code. Everything that is authority — the party's
/// address, its printer, the paper, the photograph's bytes — is the server's to
/// resolve; the browser names a photograph, how it is framed, and the language
/// of the line under it, and nothing else.
/// </summary>
public sealed record PartyQrCardPrintRequest(
    Guid? FileItemId,
    PartyPrintPlacementRequest? Placement,
    /// <summary>"it", "en", "es" or "de" — the language of the line over the code.</summary>
    string? Locale);

/// <summary>Everything the composer needs for the QR card, and nothing about who asked.</summary>
public sealed record PartyQrCardComposition(
    byte[] Photo,
    PhotoPlacement Placement,
    /// <summary>The party's public address, absolute. Encoded on the sheet and nowhere else.</summary>
    string Url,
    string PartyName,
    string Line,
    PrintCalibration? Calibration = null);

/// <summary>
/// The line over the code, in the host's language. One table, mirrored by the
/// browser (<c>partyPrintGeometry.ts</c>), so the preview shows the very words
/// the paper gets.
/// </summary>
public static class PartyQrCardText
{
    private static readonly IReadOnlyDictionary<string, string> Lines = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["it"] = "Inquadra ed entra nella festa",
        ["en"] = "Scan to join the party",
        ["es"] = "Escanea y entra en la fiesta",
        ["de"] = "Scannen und mitfeiern",
    };

    public static bool IsKnownLocale(string? locale) => locale is not null && Lines.ContainsKey(locale);

    public static string Line(string locale) => Lines[locale];
}

/// <summary>Why a QR card is refused, beyond the direct print's own reasons (<see cref="OwnerPhotoPrintErrors"/>).</summary>
public static class PartyQrCardErrors
{
    /// <summary>The party is not open to guests: there is no address to print.</summary>
    public const string PartyClosed = "party_closed";

    /// <summary>No printer is chosen for this party.</summary>
    public const string NoPrinter = "no_printer";

    /// <summary>The installation has no public address configured, so a code would point nowhere a phone can reach.</summary>
    public const string OriginUnavailable = "origin_unavailable";
}
