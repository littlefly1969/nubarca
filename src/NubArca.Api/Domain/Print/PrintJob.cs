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
}

public static class PrintJobKinds
{
    public const string Diagnostic = "diagnostic";
    public const string OwnerPhoto = "owner-photo";

    // Guest prints from a party. A photo and four photos compose one sheet of
    // the printer's loaded paper; the twin strip composes a 10x15 sheet that
    // the printer cuts in two, sent as PrintFormats.Strip2x6Pair. The kind is
    // what the sheet IS, and stays as it was first stored: "party-strip4" is
    // the twin strip, both strips of it.
    public const string PartyPhoto = "party-photo";
    public const string PartyGrid4 = "party-grid4";
    public const string PartyStrip4 = "party-strip4";

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
