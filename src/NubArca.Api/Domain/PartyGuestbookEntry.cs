namespace NubArca.Api.Domain;

/// <summary>
/// A DEDICATION left in the party's guest book — the third, and last, thing a
/// guest may contribute.
///
/// <para><b>It is deliberately not a <see cref="PartyMessage"/>, and no flag on
/// one.</b> The two answer different questions. A message is written to be READ
/// OUT: it goes on the television, it is short, it competes for the room's
/// attention, and it stops mattering when the party ends. A guest book entry is
/// written to be KEPT: it is longer, nobody projects it, and it is worth having
/// the morning after. Giving one row a boolean would make every query about
/// either of them a query about both, and would put a keepsake one mistaken
/// projection away from a wall somebody is looking at.</para>
///
/// <para><b>Scope is the PARTY, not the link.</b> This is the one place the
/// guest book departs from <see cref="PartyMessage"/>, and it departs
/// deliberately. A message belongs to one QR because last year's greetings must
/// not reappear at this year's party. A guest book is the opposite: it is the
/// book of the EVENT, and revoking and re-minting a QR in the middle of an
/// evening — which the product does routinely — must not start a second book.
/// The link the entry arrived through is kept beside it as provenance and is
/// authoritative for nothing.</para>
///
/// <para><b>It never reaches the slideshow.</b> There is no promotion, no hero,
/// no conversion, and no projection that reads this table for the television.
/// See <c>PartyGuestbookService</c>.</para>
/// </summary>
public class PartyGuestbookEntry
{
    public Guid Id { get; set; }

    /// <summary>
    /// The event this dedication was left at. Required, and the reason the book
    /// survives a QR rotation.
    /// </summary>
    public Guid PartyId { get; set; }

    /// <summary>
    /// The party's owner at submission time. A projection of
    /// <see cref="Party.OwnerUserId"/>, kept for the same reason
    /// <see cref="PartyMessage.OwnerUserId"/> is: every owner-side listing is
    /// already owner-scoped, and joining the party on each read would be churn
    /// with no behaviour change.
    /// </summary>
    public Guid OwnerUserId { get; set; }

    /// <summary>
    /// Which QR the entry came in on, when known. PROVENANCE, never authority:
    /// nothing filters on it, and a revoked link does not hide a dedication
    /// somebody left. Nullable so a future non-QR entry point needs no backfill.
    /// </summary>
    public Guid? PartyAlbumLinkId { get; set; }

    /// <summary>
    /// Which participant session wrote it, when known. Owner-private, NEVER in
    /// a DTO — it exists so one abusive guest's whole run can be found during
    /// an incident, exactly as on <see cref="PartyMessage"/>.
    /// </summary>
    public Guid? PartyParticipantId { get; set; }

    /// <summary>
    /// The signature the guest typed, or null. Never an empty string, so "did
    /// not sign it" has exactly one representation.
    /// </summary>
    public string? AuthorDisplayName { get; set; }

    /// <summary>
    /// The dedication. Normalised plain text (see <see cref="PartyGuestbookText"/>):
    /// never HTML, never Markdown, never a link anything is expected to activate.
    /// </summary>
    public string Body { get; set; } = string.Empty;

    /// <summary>
    /// One of <see cref="PartyMessageStatuses"/>. The vocabulary and the state
    /// machine are SHARED with the party's other contributions on purpose:
    /// "what can happen to something a guest left" has one answer in this
    /// product, and a second moderation framework would be two places to read it
    /// and two places for it to drift.
    /// </summary>
    public string Status { get; set; } = PartyMessageStatuses.Visible;

    public DateTime CreatedAt { get; set; }

    public DateTime? ModeratedAt { get; set; }

    /// <summary>Owner-private; never in a DTO.</summary>
    public Guid? ModeratedByUserId { get; set; }

    public DateTime UpdatedAt { get; set; }

    /// <summary>Only a Visible entry is ever shown to a guest.</summary>
    public bool IsPublic => Status == PartyMessageStatuses.Visible;
}

/// <summary>
/// What a dedication is, and how long it may be.
///
/// <para>Longer than a greeting because it is a different act: a message is
/// read out over music, a dedication is read afterwards, alone. Counted in
/// Unicode code points for the reason <see cref="PartyMessageText"/> gives at
/// length — it is the one unit .NET and a browser agree on exactly.</para>
/// </summary>
public static class PartyGuestbookLimits
{
    public const int MaxAuthorDisplayNameLength = 80;
    public const int MaxBodyLength = 1000;
}

/// <summary>
/// The guest book's text rules, which are the party's text rules with the guest
/// book's limits.
///
/// <para>Normalisation is <see cref="PartyMessageText.Normalize"/> itself, not
/// a copy of it. That is where the security-relevant part lives — the bidi
/// overrides that make stored text render as something other than what it says,
/// the zero-width padding, the control characters — and there must be exactly
/// one implementation of it. What differs is only how much text is allowed, so
/// only that is stated here.</para>
///
/// <para>A dedication is therefore ONE LINE, like a greeting: every line ending
/// becomes a space. That is a deliberate product decision and not an oversight.
/// A second normaliser that preserved paragraphs would be a second place for
/// the rules above to be got wrong, and the value of a blank line in a
/// dedication does not pay for that.</para>
/// </summary>
public static class PartyGuestbookText
{
    /// <summary>
    /// The optional signature: normalised, then absent rather than empty.
    /// False when it is present but too long — a name over the limit is a
    /// refusal, never a silent truncation of what somebody calls themselves.
    /// </summary>
    public static bool TryNormalizeAuthor(string? value, out string? normalized)
    {
        var text = PartyMessageText.Normalize(value);
        if (text.Length == 0)
        {
            normalized = null;
            return true;
        }

        if (PartyMessageText.Length(text) > PartyGuestbookLimits.MaxAuthorDisplayNameLength)
        {
            normalized = null;
            return false;
        }

        normalized = text;
        return true;
    }

    /// <summary>
    /// The dedication: normalised, non-empty, within the limit. There is no
    /// such thing as a blank dedication.
    /// </summary>
    public static bool TryNormalizeBody(string? value, out string normalized)
    {
        normalized = PartyMessageText.Normalize(value);
        if (normalized.Length == 0)
        {
            return false;
        }

        return PartyMessageText.Length(normalized) <= PartyGuestbookLimits.MaxBodyLength;
    }
}
