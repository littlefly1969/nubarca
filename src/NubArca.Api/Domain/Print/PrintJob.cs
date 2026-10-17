namespace NubArca.Api.Domain.Print;

public sealed class PrintJob
{
    public Guid Id { get; set; }
    public Guid OwnerUserId { get; set; }
    public Guid PrintStationId { get; set; }
    public Guid PrinterDeviceId { get; set; }
    public Guid? FileItemId { get; set; }
    public string Kind { get; set; } = PrintJobKinds.Diagnostic;
    public long? PublicSequence { get; set; }
    public string Format { get; set; } = PrintFormats.Photo10x15;
    public string State { get; set; } = PrintJobStates.Requested;
    public string RenderSpecificationJson { get; set; } = "{}";
    public string? ArtifactStorageKey { get; set; }
    public string? ArtifactContentType { get; set; }
    public long? ArtifactByteLength { get; set; }
    public string? ClaimTokenHash { get; set; }
    public DateTime? LeaseUntil { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? RenderedAt { get; set; }
    public DateTime? ClaimedAt { get; set; }
    public DateTime? SubmittedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public string? FailureCode { get; set; }
    /// <summary>
    /// Fixed when the job is created, from its printer's simulation then: the
    /// agent sends a simulated job to the simulated twin, never to paper, and a
    /// switch changed later moves no job already queued.
    /// </summary>
    public bool Simulated { get; set; }
}

public static class PrintJobKinds
{
    public const string Diagnostic = "diagnostic";

    // An owner's own sheets, from their library or an album: one photograph,
    // four, or the twin strip the printer cuts in two (PrintFormats.Strip2x6Pair)
    // — the formats of the shared catalogue (Print/PrintLayouts).
    public const string OwnerPhoto = "owner-photo";
    public const string OwnerGrid4 = "owner-grid4";
    public const string OwnerStrip4 = "owner-strip4";
    // A 20x15 the printer cuts: two 10x15 photographs, or four strips.
    public const string OwnerPhoto2 = "owner-photo2";
    public const string OwnerQuadStrip4 = "owner-quad-strip4";

    public static bool IsOwner(string value) =>
        value is OwnerPhoto or OwnerGrid4 or OwnerStrip4 or OwnerPhoto2 or OwnerQuadStrip4;

    // Guest prints from a party. A photo and four photos compose one sheet of
    // the printer's loaded paper; the twin strip composes a 10x15 sheet that
    // the printer cuts in two, sent as PrintFormats.Strip2x6Pair. The kind is
    // what the sheet IS, and stays as it was first stored: "party-strip4" is
    // the twin strip, both strips of it.
    public const string PartyPhoto = "party-photo";
    public const string PartyGrid4 = "party-grid4";
    public const string PartyStrip4 = "party-strip4";

    /// <summary>
    /// The HOST's sheet for the tables: the party's QR under a photograph, two
    /// cards the printer cuts apart (PrintFormats.Strip2x6Pair). A party's
    /// sheet but no guest's print — so not in <see cref="Party"/>, which is
    /// what a guest may ask about.
    /// </summary>
    public const string PartyQrCard = "party-qr-card";

    /// <summary>Every guest print kind — as a list, so a database query can ask it too.</summary>
    public static readonly string[] Party = [PartyPhoto, PartyGrid4, PartyStrip4];

    public static bool IsParty(string value) => value is PartyPhoto or PartyGrid4 or PartyStrip4;
}

public static class PrintFormats
{
    // One sheet of each paper, as the Print Agent is asked for it. The same
    // strings as PrintPapers: a paper's format is its own name.
    public const string Photo10x15 = PrintPapers.Photo10x15;
    public const string Photo13x18 = PrintPapers.Photo13x18;
    public const string Photo20x15 = PrintPapers.Photo20x15;

    /// <summary>
    /// The same 10x15 sheet, cut down the middle by the printer into two 2x6
    /// strips. Only a printer that reports it receives it: on the DNP that is a
    /// second Windows queue with the driver's "2inch cut" enabled.
    /// </summary>
    public const string Strip2x6Pair = "2x6x2";

    /// <summary>
    /// A 20x15 sheet of two 10x15, as the DNP makes it: two 10x15 JPEGs, each
    /// rendered on its own, which the Print Agent hands to one CUPS job; the
    /// driver joins them on the 6x8 and the printer cuts it in two. Only a
    /// printer that reports it receives it.
    /// </summary>
    public const string Photo4x6Pair = "4x6x2";

    /// <summary>
    /// A 20x15 sheet of four strips: two 10x15 JPEGs, each a twin strip, sent
    /// as one job to the strip queue, joined on the 6x8 and cut with the
    /// 2-inch cut into four 2x6. Only a printer that reports it receives it.
    /// </summary>
    public const string Strip2x6Quad = "2x6x4";

    /// <summary>The joined 20x15: an artifact of two 10x15 files (<c>PrintArtifactParts</c>), never one sheet.</summary>
    public static readonly IReadOnlyList<string> Joined = [Photo4x6Pair, Strip2x6Quad];

    /// <summary>Every format that is a cut sheet, in the order they are reported.</summary>
    public static readonly IReadOnlyList<string> Cuts = [Strip2x6Pair, Photo4x6Pair, Strip2x6Quad];
}

public static class PrintJobStates
{
    public const string Requested = "requested";
    public const string Rendering = "rendering";
    public const string Ready = "ready";
    public const string Claimed = "claimed";
    public const string Submitting = "submitting";
    public const string Submitted = "submitted";
    public const string Completed = "completed";
    public const string Failed = "failed";
    public const string Cancelled = "cancelled";
    public const string DeliveryUnknown = "delivery-unknown";

    /// <summary>
    /// The states a job never leaves. Held as a list as well as a predicate
    /// because a database query cannot call the predicate — and the predicate
    /// reads the list, so the two cannot drift apart.
    /// </summary>
    public static readonly string[] Terminal =
        [Completed, Failed, Cancelled, DeliveryUnknown];

    public static bool IsTerminal(string value) => Terminal.Contains(value);
}
