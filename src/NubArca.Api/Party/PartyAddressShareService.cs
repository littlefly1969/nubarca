using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using NubArca.Api.Data;
using NubArca.Api.Domain;

namespace NubArca.Api.Party;

/// <summary>
/// WHERE THE PARTY IS, ready to be handed to somebody who will pass it on.
///
/// <para>The party's name and its venue. No guest, no invitation group, no
/// RSVP, no token and no URL of any kind — which is the whole point of it.
/// Telling a neighbour the address and inviting them are different acts, and
/// until now the product could only do the second: the only way to say where
/// the party was, was to create a guest group and share a personal invitation.
/// A host with nobody on a list had nothing to send.</para>
///
/// <para><b>It carries no bearer of any sort, deliberately.</b> Not the guest
/// link, not an invitation token, not the upload or print capability, not an
/// email address. An address travels through chats and gets forwarded; a
/// capability must not travel with it.</para>
///
/// <para>The TEXT is composed by the client, in the reader's language, from
/// these facts. The server states what is true and the browser says it — the
/// same division the product already uses for error codes, and the reason a
/// fourth language does not mean a server change.</para>
/// </summary>
public interface IPartyAddressShareService
{
    /// <summary>
    /// The party's name and venue, or null when the party is not this owner's.
    /// A party WITHOUT an address still resolves — with
    /// <see cref="PartyAddressShareDto.HasAddress"/> false — so the caller can
    /// tell "not yours" apart from "nothing to share yet".
    /// </summary>
    Task<PartyAddressShareDto?> GetAsync(
        Guid ownerUserId, Guid partyId, CancellationToken cancellationToken = default);
}

/// <summary>
/// The facts a share is composed from. Every field is something the host typed
/// about their own party.
/// </summary>
public sealed record PartyAddressShareDto(
    string Title,
    DateTime? EventStartsAt,
    string? VenueName,
    string? Address,
    string? Note)
{
    /// <summary>
    /// Whether there is anything to send. An address alone is enough — a venue
    /// name without a street is a place nobody can find.
    ///
    /// <para>Never serialized: it is the ENDPOINT's question, asked before it
    /// decides between a share and a 409, and by the time a body exists the
    /// answer is already yes. Putting it on the wire would add a field whose
    /// only possible value is <c>true</c> to something whose whole discipline
    /// is that it carries the five facts and nothing else.</para>
    /// </summary>
    [JsonIgnore]
    public bool HasAddress => !string.IsNullOrWhiteSpace(Address);
}

public sealed class PartyAddressShareService : IPartyAddressShareService
{
    private readonly AppDbContext _db;

    public PartyAddressShareService(AppDbContext db) => _db = db;

    public async Task<PartyAddressShareDto?> GetAsync(
        Guid ownerUserId, Guid partyId, CancellationToken cancellationToken = default)
    {
        // OWNERSHIP IN THE QUERY, not after it: a party that is not this
        // owner's is the same generic nothing as one that does not exist.
        var party = await _db.Parties
            .AsNoTracking()
            .Where(p => p.Id == partyId && p.OwnerUserId == ownerUserId)
            .Select(p => new { p.Title, p.EventStartsAt })
            .FirstOrDefaultAsync(cancellationToken);
        if (party is null)
        {
            return null;
        }

        // The LOCATION slot is where the address lives — the same row the
        // invitation reads, so a host who corrects the street corrects it
        // everywhere at once. Its `Enabled` flag governs whether GUESTS see the
        // section; it does not govern whether the host knows their own address,
        // so it is deliberately not part of this predicate.
        var json = await _db.PartyGuestContents
            .AsNoTracking()
            .Where(c => c.PartyId == partyId && c.Kind == PartyGuestContentKinds.Location)
            .Select(c => c.ContentJson)
            .FirstOrDefaultAsync(cancellationToken);

        var (venue, address, note) = ReadLocation(json);
        return new PartyAddressShareDto(party.Title, party.EventStartsAt, venue, address, note);
    }

    /// <summary>
    /// The stored, already-validated location document, read back.
    ///
    /// <para>It was canonically re-serialized from
    /// <see cref="PartyLocationContent"/> when it was written, so this is a
    /// plain deserialize. It still tolerates a missing or malformed document
    /// rather than throwing: a slot the host never wrote is "no address yet",
    /// which is a state the caller already handles.</para>
    /// </summary>
    private static (string? Venue, string? Address, string? Note) ReadLocation(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return (null, null, null);
        }

        try
        {
            var content = JsonSerializer.Deserialize<PartyLocationContent>(json, Options);
            return content is null
                ? (null, null, null)
                : (Trimmed(content.VenueName), Trimmed(content.Address), Trimmed(content.Note));
        }
        catch (JsonException)
        {
            return (null, null, null);
        }
    }

    private static string? Trimmed(string? value)
    {
        var trimmed = value?.Trim();
        return string.IsNullOrEmpty(trimmed) ? null : trimmed;
    }

    private static readonly JsonSerializerOptions Options =
        new(JsonSerializerDefaults.Web);
}
