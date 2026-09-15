using System.Net.Mail;
using System.Text.Json;

namespace NubArca.Api.Domain;

// THE GUEST LIST — the Before half of a party, stated as private facts.
//
// Three identities meet at a party and this file adds exactly one of them. An
// INVITATION GROUP is who the host invited: Mario, Mario and Laura, the Rossi
// family. It is reached by a personal capability that opens that group's own
// invitation and RSVP and nothing else. It is not the party's public QR
// (PartyAlbumLink), which opens the party for anybody holding it, and it is not
// a PartyParticipant, which is the anonymous browser that uploads and votes on
// the night. The person who answered "Mario, attending" and the phone that later
// scans the room's QR are allowed to remain two separate facts: nothing here
// binds one to the other, and nothing infers it from an email, a name or a
// cookie.
//
// Every row below is owner-private PII — names, addresses, phone numbers,
// allergies, answers. None of it reaches the public party context, the TV, the
// game, print, face search, a link preview, a log line or an audit record.

/// <summary>
/// The unit ONE invitation is sent to — a person, a couple or a family.
///
/// <para>Its capability is personal: <see cref="CapabilityId"/> is the random
/// derivation input of the current link generation, <see cref="TokenHash"/> the
/// SHA-256 of the derived raw token, and the raw token itself is never stored.
/// Rotating replaces both, so every link sent before stops opening anything.
/// Changing the recipient address rotates automatically: the old mailbox's link
/// must not keep answering for a group it no longer reaches.</para>
///
/// <para><see cref="Version"/> is the concurrency boundary of the WHOLE RSVP
/// aggregate — the group, its guests, their answers and the group's custom
/// answers. The owner's edits and the guest's replies both spend it, so neither
/// can silently overwrite the other.</para>
/// </summary>
public class PartyInvitationGroup
{
    public Guid Id { get; set; }
    public Guid PartyId { get; set; }

    /// <summary>What the host calls the group: "Famiglia Rossi".</summary>
    public string Label { get; set; } = string.Empty;

    /// <summary>
    /// Where the invitation is SENT. Deliberately not unique: a household may
    /// share one mailbox across two groups, and that is the host's business.
    /// </summary>
    public string RecipientEmail { get; set; } = string.Empty;

    /// <summary>For the host's own records. Nothing sends to it.</summary>
    public string? Phone { get; set; }

    /// <summary>How many guests the group may bring beyond the named ones.</summary>
    public int MaxAdditionalGuests { get; set; }

    /// <summary>The current link generation's derivation input. Never exposed.</summary>
    public Guid CapabilityId { get; set; }

    /// <summary>SHA-256 hex of the derived raw capability. The raw value is never stored.</summary>
    public string TokenHash { get; set; } = string.Empty;

    public DateTime CapabilityIssuedAt { get; set; }

    public int Version { get; set; } = 1;

    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

/// <summary>
/// One PERSON of an invitation group.
///
/// <para>Membership IS the invited fact for a named guest, so there is no
/// <c>Invited</c> flag to disagree with it. An additional guest
/// (<see cref="IsAdditionalGuest"/>) is a +1 the group itself added through its
/// RSVP, within <see cref="PartyInvitationGroup.MaxAdditionalGuests"/>; the host's
/// editor never round-trips them, so saving the named list cannot lose one.</para>
/// </summary>
public class PartyGuest
{
    public Guid Id { get; set; }
    public Guid PartyInvitationGroupId { get; set; }

    public string Name { get; set; } = string.Empty;

    public string? Email { get; set; }
    public string? Phone { get; set; }

    public bool IsAdditionalGuest { get; set; }
    public int SortOrder { get; set; }

    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

/// <summary>
/// One person's answer. A named guest starts <c>pending</c>; the first explicit
/// answer stamps <see cref="RespondedAt"/>, and later changes keep it and move
/// <see cref="UpdatedAt"/> instead, so "when did they first reply" survives a
/// change of mind.
/// </summary>
public class PartyRsvp
{
    /// <summary>Primary key and foreign key: one RSVP per guest, never two.</summary>
    public Guid PartyGuestId { get; set; }

    public string Status { get; set; } = PartyRsvpStatuses.Pending;

    /// <summary>Allergies and dietary needs. Owner-private PII.</summary>
    public string? DietaryNotes { get; set; }

    public DateTime? RespondedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

/// <summary>
/// A question the host asks every group, from a CLOSED set of three kinds.
///
/// <para>Once any group has answered it, what it asks — prompt, kind, whether it
/// is required, its options — is frozen: an answer recorded against "Carne o
/// pesce?" must not silently become an answer to a different question. The host
/// deactivates it and asks a new one instead; the old answers stay as private
/// history.</para>
/// </summary>
public class PartyRsvpQuestion
{
    public Guid Id { get; set; }
    public Guid PartyId { get; set; }

    public string Prompt { get; set; } = string.Empty;
    public string Kind { get; set; } = string.Empty;
    public bool Required { get; set; }

    /// <summary>The canonical option list, for <c>single_choice</c> only.</summary>
    public string? OptionsJson { get; set; }

    public bool IsActive { get; set; } = true;
    public int SortOrder { get; set; }
    public int Version { get; set; } = 1;

    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

/// <summary>A GROUP's answer to one question, stored as validated canonical JSON.</summary>
public class PartyRsvpAnswer
{
    public Guid PartyInvitationGroupId { get; set; }
    public Guid PartyRsvpQuestionId { get; set; }

    public string ValueJson { get; set; } = "null";

    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

/// <summary>
/// One outbound invitation email, and the idempotency ledger that keeps a
/// double click from sending two.
///
/// <para>The row is committed <c>pending</c> BEFORE the message is handed to
/// SMTP and completed afterwards. A retry with the same
/// <see cref="ClientRequestId"/> finds the row and sends nothing, whatever state
/// it is in; a process that died after SMTP accepted the message leaves it
/// <c>pending</c>, which the host sees as "not confirmed" — never as a failure
/// that invites a duplicate, and never as a success nobody observed.</para>
///
/// <para>It holds no address, no body, no SMTP reply and no token. The
/// capability generation it carried is recorded so a rotation can tell which
/// sends still describe the current link.</para>
/// </summary>
public class PartyInvitationDelivery
{
    public Guid Id { get; set; }
    public Guid PartyInvitationGroupId { get; set; }

    /// <summary>Minted by the caller, once per click.</summary>
    public Guid ClientRequestId { get; set; }

    /// <summary>The capability generation embedded in this email.</summary>
    public Guid CapabilityId { get; set; }

    public string Kind { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;

    public DateTime CreatedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
}

/// <summary>Three answers and deliberately no "maybe".</summary>
public static class PartyRsvpStatuses
{
    public const string Pending = "pending";
    public const string Attending = "attending";
    public const string Declined = "declined";

    public static bool IsKnown(string? status) => status is Pending or Attending or Declined;
}

/// <summary>
/// The three things a question can be. Closed: each is something the form and
/// the validator DO, so a fourth is a slice that decides what it means.
/// </summary>
public static class PartyRsvpQuestionKinds
{
    public const string ShortText = "short_text";
    public const string SingleChoice = "single_choice";
    public const string YesNo = "yes_no";

    public static bool IsKnown(string? kind) => kind is ShortText or SingleChoice or YesNo;
}

public static class PartyInvitationDeliveryKinds
{
    public const string Initial = "initial";
    public const string Resend = "resend";
    public const string Reminder = "reminder";
}

public static class PartyInvitationDeliveryStatuses
{
    public const string Pending = "pending";
    public const string Sent = "sent";
    public const string Failed = "failed";
}

/// <summary>
/// What a host and a guest may type, and how much of it — one set of numbers
/// for the EF configuration, the validators and the client contract. Measured
/// in Unicode code points, like every other Party text limit.
///
/// <para>The ceilings are there to stop accidents and abuse, not to sell
/// anything: no wedding needs eleven +1s per group, and no invitation form
/// needs twenty-one questions.</para>
/// </summary>
public static class PartyInvitationLimits
{
    public const int MaxLabelLength = 120;
    public const int MaxEmailLength = 254;
    public const int MaxPhoneLength = 40;
    public const int MaxGuestNameLength = 120;
    public const int MaxNamedGuests = 20;
    public const int MaxAdditionalGuests = 10;
    public const int MaxGroupsPerParty = 1000;
    public const int MaxDietaryNotesLength = 500;

    public const int MaxQuestionPromptLength = 300;
    public const int MaxShortTextAnswerLength = 500;
    public const int MaxOptions = 20;
    public const int MaxOptionLength = 120;
    public const int MaxActiveQuestions = 20;
    public const int MaxQuestionsPerParty = 100;

    /// <summary>Trimmed, or null when there was nothing but whitespace.</summary>
    public static string? Normalize(string? value)
    {
        var trimmed = value?.Trim();
        return string.IsNullOrEmpty(trimmed) ? null : trimmed;
    }

    public static bool Fits(string? normalized, int maxCodePoints) =>
        normalized is null || CodePoints(normalized) <= maxCodePoints;

    /// <summary>
    /// One address, syntactically. Only the shape is checked — whether the
    /// mailbox exists is SMTP's to say — and a display-name form such as
    /// <c>Mario &lt;m@x.it&gt;</c> is refused, because this is where a message is
    /// sent, not how it is addressed.
    /// </summary>
    public static bool IsValidEmail(string? normalized) =>
        normalized is not null
        && normalized.Length <= MaxEmailLength
        && !normalized.Any(char.IsWhiteSpace)
        && MailAddress.TryCreate(normalized, out var parsed)
        && parsed.Address == normalized
        && parsed.Host.Contains('.');

    public static int CodePoints(string value)
    {
        var count = 0;
        foreach (var _ in value.EnumerateRunes()) count++;
        return count;
    }
}

/// <summary>
/// The WHOLE of the custom-question rule, as pure functions: what a question may
/// be, and what an answer to it may be. The service and the tests ask these
/// rather than restating them.
/// </summary>
public static class PartyRsvpQuestionRules
{
    /// <summary>
    /// The canonical option list for a question of this kind, or null when the
    /// options are not acceptable. Options exist for <c>single_choice</c> only,
    /// trimmed, non-empty, at most <see cref="PartyInvitationLimits.MaxOptions"/>,
    /// and distinct regardless of case — "Carne" and "carne" are one option a
    /// guest could not tell apart. Other kinds must carry none.
    /// </summary>
    public static IReadOnlyList<string>? NormalizeOptions(string kind, IReadOnlyList<string?>? options)
    {
        if (kind != PartyRsvpQuestionKinds.SingleChoice)
        {
            return options is null || options.Count == 0 ? [] : null;
        }
        if (options is null || options.Count < 2 || options.Count > PartyInvitationLimits.MaxOptions)
        {
            return null;
        }

        var normalized = new List<string>(options.Count);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var option in options)
        {
            var value = PartyInvitationLimits.Normalize(option);
            if (value is null
                || !PartyInvitationLimits.Fits(value, PartyInvitationLimits.MaxOptionLength)
                || !seen.Add(value))
            {
                return null;
            }
            normalized.Add(value);
        }
        return normalized;
    }

    public static string? SerializeOptions(string kind, IReadOnlyList<string> options) =>
        kind == PartyRsvpQuestionKinds.SingleChoice ? JsonSerializer.Serialize(options) : null;

    public static IReadOnlyList<string> ParseOptions(string? optionsJson) =>
        optionsJson is null ? [] : JsonSerializer.Deserialize<List<string>>(optionsJson) ?? [];

    /// <summary>
    /// The canonical stored form of one answer. Returns false for anything the
    /// kind does not accept — a number, an object, an option that is not in the
    /// list, an over-long text. A true result with a null <paramref name="canonicalJson"/>
    /// is an EMPTY answer (a blank text), which is the same as not answering.
    /// </summary>
    public static bool TryCanonicalAnswer(
        string kind, IReadOnlyList<string> options, JsonElement value, out string? canonicalJson)
    {
        canonicalJson = null;
        switch (kind)
        {
            case PartyRsvpQuestionKinds.ShortText when value.ValueKind == JsonValueKind.String:
                var text = PartyInvitationLimits.Normalize(value.GetString());
                if (!PartyInvitationLimits.Fits(text, PartyInvitationLimits.MaxShortTextAnswerLength))
                {
                    return false;
                }
                canonicalJson = text is null ? null : JsonSerializer.Serialize(text);
                return true;

            case PartyRsvpQuestionKinds.SingleChoice when value.ValueKind == JsonValueKind.String:
                // EXACT, ordinal: the option list is the vocabulary, and a value
                // that only resembles one of its words is not one of them.
                var choice = value.GetString();
                if (choice is null || !options.Contains(choice, StringComparer.Ordinal))
                {
                    return false;
                }
                canonicalJson = JsonSerializer.Serialize(choice);
                return true;

            case PartyRsvpQuestionKinds.YesNo
                when value.ValueKind is JsonValueKind.True or JsonValueKind.False:
                canonicalJson = value.ValueKind == JsonValueKind.True ? "true" : "false";
                return true;

            default:
                return false;
        }
    }
}
