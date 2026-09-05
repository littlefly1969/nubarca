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
    Guid PartyAlbumId,
    Guid OwnerUserId,
    Guid PrintStationId,
    Guid PrinterDeviceId,
    string PartyName,
    string? FooterText,
    PartyPrintProductState Photo,
    PartyPrintProductState Strip)
{
    /// <summary>This party's state for one product, or null if there is no such product.</summary>
    public PartyPrintProductState? Product(string product) => product switch
    {
        Domain.Print.PartyPrintProducts.Photo => Photo,
        Domain.Print.PartyPrintProducts.Strip4 => Strip,
        _ => null,
    };
}

/// <summary>One product's live state, as the guest is allowed to see it.</summary>
public sealed record PartyPrintProductState(bool Enabled, int Remaining)
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
