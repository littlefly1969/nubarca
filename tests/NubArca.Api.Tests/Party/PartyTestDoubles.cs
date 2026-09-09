using NubArca.Api.Data;
using NubArca.Api.Domain;
using NubArca.Api.Party;

namespace NubArca.Api.Tests.Party;

/// <summary>
/// The two things a test needs when it drives a Party service directly rather
/// than through HTTP.
///
/// <para>A test that exercises a race or a transition is not testing the role
/// system, so it states the host's capabilities as a literal instead of seeding
/// a user, a role and its permission rows. Stating them is deliberate: there is
/// no default, so a test can never accidentally assert that something works
/// while silently granting itself the permission that makes it work.</para>
/// </summary>
internal static class PartyTestCapabilities
{
    /// <summary>A host permitted to run every Party capability.</summary>
    internal static readonly PartyCapabilities All = new(true, true, true, true, true);
}

internal sealed class FixedPartyCapabilityPolicy : IPartyCapabilityPolicy
{
    private readonly PartyCapabilities _capabilities;

    internal FixedPartyCapabilityPolicy(PartyCapabilities? capabilities = null) =>
        _capabilities = capabilities ?? PartyTestCapabilities.All;

    public Task<PartyCapabilities> ForOwnerAsync(
        Guid ownerUserId, CancellationToken cancellationToken = default) =>
        Task.FromResult(_capabilities);
}

/// <summary>
/// The party a hand-seeded <see cref="PartyAlbumLink"/> now needs.
///
/// <para>A link is a capability OF a party, and the foreign key says so — so a
/// test that builds a link row directly builds the party and its <c>main</c>
/// media source too, exactly as <c>EnableAsync</c> would. Doing it here rather
/// than in each fixture keeps "what a party is made of" in one place: a table
/// added under the root is added once.</para>
/// </summary>
internal static class PartySeed
{
    internal static Guid Party(
        AppDbContext db, Guid ownerUserId, Guid albumId,
        string title = "Festa", string status = PartyStatuses.Published)
    {
        var partyId = Guid.NewGuid();
        Party(db, partyId, ownerUserId, albumId, title, status);
        return partyId;
    }

    internal static void Party(
        AppDbContext db, Guid partyId, Guid ownerUserId, Guid albumId,
        string title = "Festa", string status = PartyStatuses.Published)
    {
        var now = DateTime.UtcNow;
        db.Parties.Add(new NubArca.Api.Domain.Party
        {
            Id = partyId,
            OwnerUserId = ownerUserId,
            Title = title,
            Status = status,
            Version = 1,
            CreatedAt = now,
            UpdatedAt = now,
        });
        db.PartyMediaSources.Add(new PartyMediaSource
        {
            PartyId = partyId,
            AlbumId = albumId,
            Role = PartyMediaSourceRoles.Main,
            SortOrder = 0,
            CreatedAt = now,
        });
    }
}
