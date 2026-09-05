namespace NubArca.Api.Print;

/// <summary>What a guest asked to print. Ids are party item ids, nothing else.</summary>
public sealed record PartyPrintSubmitRequest(
    string Product,
    string Theme,
    IReadOnlyList<PartyPrintSlotRequest> Slots);

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
