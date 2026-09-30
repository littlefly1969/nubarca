using NubArca.Api.Domain.Print;

namespace NubArca.Api.Print;

/// <summary>
/// What a resolved party print capability grants, and to what.
///
/// Deliberately narrow: the party and the printer this token may print on, and
/// nothing else. It carries no owner session, no album membership and no ability
/// to read anything beyond the party's own guest-visible photographs.
/// </summary>
public sealed record PartyPrintAccess(
    /// <summary>
    /// The link this capability rides on. Carried because a guest's identity is
    /// LINK-scoped: the same participant cookie that counts their uploads counts
    /// their prints, so one guest is one guest across both flows.
    /// </summary>
    Guid PartyAlbumLinkId,
    Guid PartyAlbumId,
    Guid OwnerUserId,
    Guid PrintStationId,
    Guid PrinterDeviceId,
    string PartyName,
    string? FooterText,
    PartyPrintProductState Photo,
    /// <summary>The twin strip.</summary>
    PartyPrintProductState Strip,
    /// <summary>
    /// The printer cuts a 10x15 sheet into its two strips itself — sent as
    /// 2x6x2. The twin strip exists only when it does.
    /// </summary>
    bool StripCutByPrinter = false,
    /// <summary>The printer's tone compensation. Null is neutral.</summary>
    PrintCalibration? Calibration = null,
    /// <summary>Four photographs on one sheet. Null is off.</summary>
    PartyPrintProductState? Grid = null,
    /// <summary>The paper the operator says is loaded, which the printer reports it can print.</summary>
    string Paper = PrintPapers.Photo10x15)
{
    private static readonly PartyPrintProductState Off = new(false, 0);

    /// <summary>
    /// Whether this printer, with its paper, can make the product at all — the
    /// matrix, and for the twin strip the printer's own cut. Whether the host
    /// turned it on and has sheets left is the product state's business.
    /// </summary>
    public bool Offers(string product) =>
        PartyPrintProducts.Allowed(Paper, product)
        && (product != PartyPrintProducts.TwinStrip4 || StripCutByPrinter);

    /// <summary>True when the printer, not the guest, cuts this product's sheet.</summary>
    public bool CutByPrinter(string product) =>
        product == PartyPrintProducts.TwinStrip4 && StripCutByPrinter;

    /// <summary>The job format this product's sheet is printed as: the paper, or the cut 10x15.</summary>
    public string PrintFormat(string product) =>
        product == PartyPrintProducts.TwinStrip4 ? PrintFormats.Strip2x6Pair : Paper;

    /// <summary>
    /// This party's state for one product — null when there is no such
    /// product, or this printer and paper cannot make it.
    /// </summary>
    public PartyPrintProductState? Product(string product)
    {
        if (!Offers(product)) return null;
        return product switch
        {
            PartyPrintProducts.Photo => Photo,
            PartyPrintProducts.Grid4 => Grid ?? Off,
            PartyPrintProducts.TwinStrip4 => Strip,
            _ => null,
        };
    }
}

/// <summary>One product's live state, as the guest is allowed to see it.</summary>
public sealed record PartyPrintProductState(bool Enabled, int Remaining, int PerGuest = 0)
{
    /// <summary>Offerable when the host turned it on AND there is paper left for it.</summary>
    public bool Available => Enabled && Remaining > 0;
}

/// <summary>
/// Resolves the print capability on every request.
///
/// Re-checked EVERY time rather than trusted from when the link was handed out:
/// a host who turns printing off, revokes the party, changes the printer or runs
/// the budget to zero must stop new prints immediately, and the only way that is
/// true is if each request asks again.
/// </summary>
public interface IPartyPrintAccessResolver
{
    /// <summary>Null when the token is unknown, or printing is not currently open.</summary>
    Task<PartyPrintAccess?> ResolveAsync(string printToken, CancellationToken cancellationToken);
}
