using NubArca.Api.Auth.Recovery;

namespace NubArca.Api.Party;

/// <summary>
/// The invitation message, in the HOST's persisted UI language — the same rule
/// the recovery email follows, and deliberately not a recipient-language
/// system: the host wrote the party in that language, so it is the language
/// the party speaks.
///
/// <para>Plain text only, and it says four things: who is invited, to what,
/// where their personal invitation is, and that no account is needed. No remote
/// image, no tracking pixel and no tracking link — an email that phones home
/// tells a third party when somebody opened a private invitation — and the one
/// URL in it is the personal link itself, built by the caller on the operator's
/// configured origin, never on a request's Host header.</para>
///
/// <para>The date is the DAY in the installation's timezone and never an hour,
/// for the reason the party's link preview gives: the page formats the hour in
/// the reader's own timezone, and an email naming a different time from the page
/// it opens would be worse than none.</para>
/// </summary>
public static class PartyInvitationEmail
{
    public static EmailMessage Compose(
        string hostLanguage,
        string toAddress,
        string groupLabel,
        string partyTitle,
        DateTime? eventStartsAt,
        string invitationUrl,
        bool reminder)
    {
        var english = string.Equals(hostLanguage, "en", StringComparison.OrdinalIgnoreCase);
        // Header values must be one line. A title is the host's own text and
        // may carry a line break the page renders happily; a Subject may not.
        var label = OneLine(groupLabel);
        var title = OneLine(partyTitle);
        var day = eventStartsAt is DateTime at ? PartyLinkPreview.FormatDay(at, english) : null;

        if (english)
        {
            var when = day is null ? string.Empty : $" on {day}";
            return new EmailMessage(
                toAddress,
                label,
                reminder ? $"Reminder: {title}" : $"You're invited: {title}",
                reminder
                    ? $"""
                      Hello {label},

                      we have not received your reply for {title}{when} yet.

                      You can answer from your personal invitation:

                      {invitationUrl}

                      No NubArca account is needed. The link is personal: please do
                      not forward it.

                      — {title}
                      """
                    : $"""
                      Hello {label},

                      you are invited to {title}{when}.

                      Open your personal invitation to see the details and reply:

                      {invitationUrl}

                      No NubArca account is needed. The link is personal: please do
                      not forward it.

                      — {title}
                      """);
        }

        var quando = day is null ? string.Empty : $" il {day}";
        return new EmailMessage(
            toAddress,
            label,
            reminder ? $"Promemoria: {title}" : $"Sei invitato: {title}",
            reminder
                ? $"""
                  Ciao {label},

                  non abbiamo ancora ricevuto la tua risposta per {title}{quando}.

                  Puoi rispondere dal tuo invito personale:

                  {invitationUrl}

                  Non serve un account NubArca. Il link è personale: per favore non
                  inoltrarlo.

                  — {title}
                  """
                : $"""
                  Ciao {label},

                  sei invitato a {title}{quando}.

                  Apri il tuo invito personale per vedere i dettagli e rispondere:

                  {invitationUrl}

                  Non serve un account NubArca. Il link è personale: per favore non
                  inoltrarlo.

                  — {title}
                  """);
    }

    /// <summary>The host's text on one line — for a header, or the first line of a shared message.</summary>
    internal static string OneLine(string value) =>
        string.Join(' ', value.Split(['\r', '\n', '\t'], StringSplitOptions.RemoveEmptyEntries)).Trim();
}
