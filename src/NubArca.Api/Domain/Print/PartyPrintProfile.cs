namespace NubArca.Api.Domain.Print;

/// <summary>
/// One party's physical-print settings: whether guests may print, on which
/// station and printer, and how much of each product they may print.
///
/// The two budgets are DELIBERATELY INDEPENDENT. A party that has run out of
/// photo prints can still print strips, and the reverse. They are never summed
/// into one "prints left": the products cost different things and the host set
/// them separately.
///
/// The counters record what was ACCEPTED into the queue, which is the moment a
/// print becomes real (see PartyPrintBudget). They are never reset: disabling a
/// product and re-enabling it resumes from the same history, because the paper
/// already spent did not come back.
/// </summary>
public sealed class PartyPrintProfile
{
    public Guid Id { get; set; }

    /// <summary>The album this party is, one profile per album.</summary>
    public Guid PartyAlbumId { get; set; }

    /// <summary>Denormalised from the album so every query can scope by owner.</summary>
    public Guid OwnerUserId { get; set; }

    /// <summary>The master switch. False hides printing from guests entirely.</summary>
    public bool Enabled { get; set; }

    public Guid? PrintStationId { get; set; }
    public Guid? PrinterDeviceId { get; set; }

    public bool PhotoEnabled { get; set; }
    /// <summary>Maximum accepted photo prints for the whole party.</summary>
    public int PhotoMaxPrints { get; set; }
    public int PhotoAcceptedCount { get; set; }

    /// <summary>
    /// Maximum photo prints ONE GUEST may take. Zero means no per-guest limit,
    /// the same convention the upload quotas use.
    ///
    /// This is the limit that matters at a party. A party-wide budget alone is
    /// spent by whoever reaches the studio first, and the host discovers it when
    /// the fortieth guest finds nothing left; a per-guest ceiling is what makes
    /// the paper last the evening. Both apply: a print needs a free slot in the
    /// guest's allowance AND in the party's.
    /// </summary>
    public int PhotoPrintsPerGuest { get; set; }

    /// <summary>Four photographs on one sheet: its own switch and budget, like the others.</summary>
    public bool GridEnabled { get; set; }
    public int GridMaxPrints { get; set; }
    public int GridAcceptedCount { get; set; }

    /// <summary>Maximum four-photo sheets ONE GUEST may take. Zero means no per-guest limit.</summary>
    public int GridPrintsPerGuest { get; set; }

    /// <summary>The twin strip: two strips of four photographs on one 10x15 sheet.</summary>
    public bool StripEnabled { get; set; }
    public int StripMaxPrints { get; set; }
    public int StripAcceptedCount { get; set; }

    /// <summary>Maximum strips ONE GUEST may take. Zero means no per-guest limit.</summary>
    public int StripPrintsPerGuest { get; set; }

    /// <summary>
    /// A line the HOST may put on the print. Guests never write on paper — see
    /// PartyPrintText for the bounds this is held to.
    /// </summary>
    public string? FooterText { get; set; }

    /// <summary>
    /// Per-party print number, shown to the guest so they can recognise their
    /// print at the station. Advanced atomically with the accepted counters, so
    /// two guests can never be handed the same number.
    /// </summary>
    public long PublicSequenceNext { get; set; } = 1;

    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

/// <summary>
/// What a guest can print. A PRODUCT is a composition; the paper is the
/// printer's (<see cref="PrintPapers"/>). The two stay independent, and only the
/// combinations in <see cref="Allowed"/> exist.
/// </summary>
public static class PartyPrintProducts
{
    /// <summary>One photograph on the sheet.</summary>
    public const string Photo = "photo";

    /// <summary>Four photographs on one sheet, two by two.</summary>
    public const string Grid4 = "grid4";

    /// <summary>
    /// Two strips of four photographs on one 10x15 sheet, which the printer
    /// cuts apart: always both strips, eight photographs, never one strip.
    /// </summary>
    public const string TwinStrip4 = "twinStrip4";

    /// <summary>The twin strip's first name, still accepted from a page opened before the rename.</summary>
    public const string LegacyStrip4 = "strip4";

    public static readonly IReadOnlyList<string> All = [Photo, Grid4, TwinStrip4];

    /// <summary>The product a client asked for, under its current name; empty when unknown.</summary>
    public static string Normalize(string? value) => value switch
    {
        Photo or Grid4 or TwinStrip4 => value,
        LegacyStrip4 => TwinStrip4,
        _ => string.Empty,
    };

    public static bool IsKnown(string value) => value is Photo or Grid4 or TwinStrip4;

    /// <summary>
    /// How many DIFFERENT source photographs a product composes: one, four,
    /// and for the twin strip four per strip, so the two keepsakes a sheet
    /// yields are not copies of each other.
    /// </summary>
    public static int RequiredPhotos(string product) => product switch
    {
        Grid4 => 4,
        TwinStrip4 => 8,
        _ => 1,
    };

    /// <summary>
    /// THE matrix: which product each paper can make. A photo and four photos
    /// on any of the three; the twin strip only on 10x15, which is the sheet
    /// the printer knows how to cut into two strips. Nothing else exists.
    /// </summary>
    public static bool Allowed(string paper, string product) => product switch
    {
        Photo or Grid4 => PrintPapers.IsKnown(paper),
        TwinStrip4 => paper == PrintPapers.Photo10x15,
        _ => false,
    };
}

/// <summary>Bounds the host's own settings. Not a guess: an explicit contract.</summary>
public static class PartyPrintLimits
{
    public const int MinBudget = 1;
    public const int MaxBudget = 500;
    public const int FooterMaxLength = 60;

    public static bool IsValidBudget(int value) => value is >= MinBudget and <= MaxBudget;
}
