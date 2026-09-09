using NubArca.Api.Access;

namespace NubArca.Api.Party;

/// <summary>
/// What the HOST's role allows their party to offer, right now.
///
/// <para>Every value is derived from the owner's effective permissions and
/// nothing else. A guest never holds a permission — they hold a capability
/// token — so the question "may this party run a game" is a question about the
/// host, asked at the seam where the token is resolved and answered once for
/// the whole request.</para>
/// </summary>
public sealed record PartyCapabilities(
    bool Access,
    bool Contributions,
    bool Games,
    bool Print,
    bool FaceSearch)
{
    /// <summary>
    /// A host who may not run parties at all. Everything is off, including
    /// <see cref="Access"/>, so a caller that forgets to check the product
    /// permission still gets nothing.
    /// </summary>
    public static readonly PartyCapabilities None = new(false, false, false, false, false);
}

/// <summary>
/// The ONE place a public Party request asks what the host is permitted to run.
///
/// <para>It exists so the answer is not spelled out five slightly different
/// ways across the endpoints. It is not a capability framework: it maps five
/// named permission keys onto five booleans and applies the single structural
/// rule the catalogue already states — a feature is meaningless without
/// <see cref="Permissions.PartyAccess"/> — so a role holding
/// <c>party.games</c> alone opens nothing.</para>
///
/// <para>Resolved from CURRENT database state on every request, through the
/// same <see cref="IUserPermissionService"/> the authenticated endpoints use.
/// That is what makes revoking a Party permission take effect for guests who
/// are already at the party, on their next request, with no token rotation and
/// nobody signing in again.</para>
/// </summary>
public interface IPartyCapabilityPolicy
{
    Task<PartyCapabilities> ForOwnerAsync(Guid ownerUserId, CancellationToken cancellationToken = default);
}

public sealed class PartyCapabilityPolicy : IPartyCapabilityPolicy
{
    private readonly IUserPermissionService _permissions;

    public PartyCapabilityPolicy(IUserPermissionService permissions) => _permissions = permissions;

    public async Task<PartyCapabilities> ForOwnerAsync(
        Guid ownerUserId, CancellationToken cancellationToken = default)
    {
        var effective = await _permissions.GetEffectiveAsync(ownerUserId, cancellationToken);

        // The parent rule, once. Without the product permission the party does
        // not exist for anybody, so there is nothing for a feature key to
        // qualify — and a disabled account resolves to no permissions at all,
        // which closes its parties for the same reason.
        if (!effective.Has(Permissions.PartyAccess))
        {
            return PartyCapabilities.None;
        }

        return new PartyCapabilities(
            Access: true,
            Contributions: effective.Has(Permissions.PartyContributions),
            Games: effective.Has(Permissions.PartyGames),
            Print: effective.Has(Permissions.PartyPrint),
            FaceSearch: effective.Has(Permissions.PartyFaceSearch));
    }
}
