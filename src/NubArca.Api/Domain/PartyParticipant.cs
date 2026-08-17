namespace NubArca.Api.Domain;

// One anonymous guest's upload session on ONE party link.
//
// The party upload token is shared by everyone holding the QR code, so it says
// "this party accepts uploads" and nothing about WHO is uploading. A per-person
// quota therefore needs an identity the upload token cannot supply, and the
// three obvious sources are all wrong: an IP address is shared by every phone
// on the house Wi-Fi and changes when a guest steps outside, a User-Agent is
// not remotely unique, and a client-supplied id is just a number the client can
// change to reset its own quota. This row is the alternative — a
// SERVER-generated random token handed back as a cookie, so the identity is
// something the server issued rather than something the client asserted.
//
// SECURITY: the raw participant token is NEVER persisted. Only its SHA-256 hash
// lives here, exactly like the party view/upload tokens, so a database read
// cannot impersonate a guest. The row carries no name, no email, no device
// fingerprint and no IP — it is a counter with a key, not a profile, and it is
// never exposed through any public or owner-facing DTO.
//
// SCOPE: a participant belongs to ONE PartyAlbumLink. The same phone at two
// different parties gets two rows and two independent quotas, and re-enabling
// party mode mints a new link, so counters start fresh — which is the same
// lifecycle the party tokens already have.
//
// HONEST LIMITATION: this is a per-participant/per-browser quota, not proof of
// human identity. Clearing site data or switching device yields a new session
// and a new allowance. That is accepted deliberately: the alternatives are
// fingerprinting and IP identity, which this codebase does not do.
public class PartyParticipant
{
    public Guid Id { get; set; }

    // The party link this participant is scoped to. Counters never cross links.
    public Guid PartyAlbumLinkId { get; set; }

    // SHA-256 hex of the server-issued participant token. Raw token never stored.
    public string TokenHash { get; set; } = string.Empty;

    // Media ACCEPTED from this participant, per kind. Incremented only by the
    // atomic quota claim, and never decremented: moderation is a visibility
    // decision, so hiding or rejecting a guest's photo must not hand back a slot
    // they could use to upload the same thing again.
    public int AcceptedPhotoCount { get; set; }
    public int AcceptedVideoCount { get; set; }

    public DateTime CreatedAt { get; set; }
    public DateTime LastSeenAt { get; set; }
}
