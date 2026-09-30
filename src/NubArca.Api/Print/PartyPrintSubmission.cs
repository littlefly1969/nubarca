namespace NubArca.Api.Print;

/// <summary>What a guest asked to print. Ids are party item ids, nothing else.</summary>
public sealed record PartyPrintSubmitRequest(
    string Product,
    string Theme,
    IReadOnlyList<PartyPrintSlotRequest> Slots,
    /// <summary>
    /// "portrait", "landscape", or absent to follow the photograph. Anything
    /// else follows the photograph too: an unreadable preference is not worth
    /// refusing a print over.
    /// </summary>
    string? Orientation = null,
    /// <summary>"white", "black" or "red": the words of an "On the photo" print.</summary>
    string? OverlayText = null,
    /// <summary>"light" or "dark": its NubArca symbol, independent of the words.</summary>
    string? OverlayLogo = null,
    /// <summary>
    /// The paper the guest composed for, as the manifest named it. It must be
    /// the paper still loaded: a sheet composed for 10x15 is not printed on
    /// 20x15 because the operator changed rolls in between. Absent (a page from
    /// before papers) means 10x15, the only paper there was.
    /// </summary>
    string? PaperSize = null);

public sealed record PartyPrintSlotRequest(
    Guid ItemId, double CropX, double CropY, double CropWidth, double CropHeight);

/// <summary>Why a submission was refused, in terms the guest UI can speak.</summary>
public enum PartyPrintRefusal
{
    /// <summary>This GUEST has taken their share; the party may still have paper.</summary>
    GuestBudgetExhausted = 100,
    None,
    /// <summary>The capability no longer resolves: printing was turned off, revoked, expired.</summary>
    Unavailable,
    /// <summary>Wrong product, wrong number of photos, duplicates, or a crop that is not a crop.</summary>
    Invalid,
    /// <summary>A chosen photograph is not a printable photograph of this party any more.</summary>
    InvalidSource,
    /// <summary>This product's budget is gone. The OTHER product may still have some.</summary>
    BudgetExhausted,
    /// <summary>The station or printer cannot take work right now. Costs nothing.</summary>
    PrinterUnavailable,
    /// <summary>Composing the sheet failed. Costs nothing.</summary>
    RenderFailed,
    /// <summary>
    /// The printer has other paper in than the sheet was composed for. Costs
    /// nothing; the studio reloads and offers what the new paper can make.
    /// </summary>
    PaperChanged,
}

/// <summary>What the guest is told after a successful submission.</summary>
public sealed record PartyPrintAccepted(
    Guid JobId,
    long PublicSequence,
    string Product,
    int RemainingForProduct,
    /// <summary>
    /// Sheets already accepted for this party's printer and not yet finished —
    /// this one excluded. A guest standing at the printer wants to know how long
    /// to wait, and "in the queue" without a number answers nothing.
    /// </summary>
    int QueueAhead);

public sealed record PartyPrintSubmitResult(
    PartyPrintRefusal Refusal,
    PartyPrintAccepted? Accepted = null)
{
    public bool Ok => Refusal == PartyPrintRefusal.None && Accepted is not null;

    public static PartyPrintSubmitResult Refuse(PartyPrintRefusal reason) => new(reason);
    public static PartyPrintSubmitResult Accept(PartyPrintAccepted accepted) =>
        new(PartyPrintRefusal.None, accepted);
}
