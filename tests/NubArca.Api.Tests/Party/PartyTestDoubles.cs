using System.Net.Http.Json;
using System.Text.Json;
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

/// <summary>
/// A party that is happening right now, with both windows open.
///
/// <para>The state a test driving a game or a message is implicitly assuming.
/// Stated rather than defaulted, so a test can never assert that something works
/// during a party while silently being in another phase.</para>
/// </summary>
internal static class PartyTestExperience
{
    internal static readonly PartyGuestExperience Live = new(
        PartyGuestPhase.Live, PartyGuestAccessMode.Full,
        LibraryAvailable: false, LibraryAccessEndsAt: null);
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

/// <summary>
/// Taking a party from "the QR works" to "the party is happening".
///
/// <para>Enabling guest access PUBLISHES a party — it is an invitation, and the
/// invitation surface is deliberately not the party: no gallery, no
/// contributions, no game, no printing, no finding your face. A test that
/// exercises any of those has to start the party first, which is exactly what a
/// host does.</para>
///
/// <para>One helper rather than the same three lines in every fixture, so the
/// step is named and a fixture that forgets it fails loudly rather than
/// mysteriously.</para>
/// </summary>
internal static class PartyTestHost
{
    /// <summary>
    /// Moves the party behind this album's party settings to <c>live</c>.
    /// Takes the settings DTO an enable returned, because that is where the
    /// party id already is.
    /// </summary>
    internal static async Task StartAsync(HttpClient owner, JsonElement partySettings)
    {
        if (partySettings.TryGetProperty("partyId", out var id)
            && id.ValueKind == JsonValueKind.String)
        {
            await StartAsync(owner, id.GetGuid());
        }
    }

    internal static async Task StartAsync(HttpClient owner, Guid partyId)
    {
        var party = await owner.GetFromJsonAsync<JsonElement>($"/api/parties/{partyId}");
        if (party.GetProperty("status").GetString() != PartyStatuses.Published)
        {
            return;
        }
        var response = await owner.PostAsJsonAsync(
            $"/api/parties/{partyId}/start-live",
            new { version = party.GetProperty("version").GetInt32() });
        response.EnsureSuccessStatusCode();
    }
}
