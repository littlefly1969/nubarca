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

    // Guest prints from a party. Both compose a 10x15 sheet: the strip is a
    // COMPOSITION, not a second paper size, so the printer requirement is
    // unchanged and the agent has nothing new to understand.
    public const string PartyPhoto = "party-photo";
    public const string PartyStrip4 = "party-strip4";

    public static bool IsParty(string value) => value is PartyPhoto or PartyStrip4;
}

public static class PrintFormats
{
    public const string Photo10x15 = "10x15";
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
