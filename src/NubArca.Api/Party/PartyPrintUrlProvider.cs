using Microsoft.EntityFrameworkCore;
using NubArca.Api.Data;
using NubArca.Api.Print;

namespace NubArca.Api.Party;

/// <summary>
/// Answers one question for the public party landing: is there a print URL to
/// offer right now?
///
/// It exists so the landing endpoint does not have to know how a print token is
/// derived, and so the answer is computed the same way everywhere: derive the
/// party's print token, then ask the capability resolver whether that token
/// would actually work. If it would not — printing off, station revoked, printer
/// cannot do 10x15, both budgets spent — the answer is null and the guest hub
/// shows no card at all.
/// </summary>
public interface IPartyPrintUrlProvider
{
    Task<string?> GetAsync(Guid partyAlbumLinkId, Guid albumId, CancellationToken cancellationToken);
}

public sealed class PartyPrintUrlProvider : IPartyPrintUrlProvider
{
    private readonly AppDbContext _db;
    private readonly IPartyLinkService _links;
    private readonly IPartyPrintAccessResolver _resolver;

    public PartyPrintUrlProvider(
        AppDbContext db, IPartyLinkService links, IPartyPrintAccessResolver resolver)
    {
        _db = db;
        _links = links;
        _resolver = resolver;
    }

    public async Task<string?> GetAsync(
        Guid partyAlbumLinkId, Guid albumId, CancellationToken cancellationToken)
    {
        // Cheap gate first: no profile, no printing, and no token derivation.
        var configured = await _db.PartyPrintProfiles.AsNoTracking()
            .AnyAsync(p => p.PartyAlbumId == albumId && p.Enabled, cancellationToken);
        if (!configured) return null;

        if (_links is not PartyLinkService concrete) return null;
        var token = concrete.DerivePrintToken(partyAlbumLinkId);

        // The hash is persisted the first time the capability is offered, which
        // is what makes the token resolvable later. Deriving is deterministic, so
        // this converges rather than rotating anything.
        var hash = PartyLinkService.HashToken(token);
        var stored = await _db.PartyAlbumLinks
            .Where(l => l.Id == partyAlbumLinkId && l.PrintTokenHash != hash)
            .ExecuteUpdateAsync(s => s.SetProperty(l => l.PrintTokenHash, hash), cancellationToken);
        _ = stored;

        // The single source of truth on whether printing is open: if this token
        // would not resolve, there is nothing to offer.
        var access = await _resolver.ResolveAsync(token, cancellationToken);
        return access is null ? null : PartyLinkService.BuildPrintUrl(token);
    }
}
