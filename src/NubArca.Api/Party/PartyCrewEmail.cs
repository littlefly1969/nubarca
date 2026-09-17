using NubArca.Api.Auth.Recovery;

namespace NubArca.Api.Party;

/// <summary>
/// The one-time code, in the HOST's persisted UI language — the same rule the
/// recovery and invitation emails follow.
///
/// <para><b>What it deliberately does not contain.</b> No link, no token, no
/// hash, no owner id, no party id, no album id, no guest data, no remote image
/// and no tracking pixel. It carries six digits and the context needed to
/// recognise them: which party, and as what.</para>
///
/// <para><b>And it cannot be used on its own.</b> The code is bound to the
/// challenge held in the browser that asked for it, so somebody who reads this
/// message — over a shoulder, in a forwarded thread, in a shared mailbox — has
/// nothing to type it into. That is the property that lets the email be this
/// plain: it is a second factor, not a way in.</para>
///
/// <para>The digits are printed grouped (<c>482 117</c>) because they are read
/// aloud and typed by hand, and ungrouped six-digit strings are misread.</para>
/// </summary>
public static class PartyCrewEmail
{
    public static EmailMessage Compose(
        string hostLanguage,
        string toAddress,
        string toName,
        string partyTitle,
        string roleKey,
        string otp)
    {
        var english = string.Equals(hostLanguage, "en", StringComparison.OrdinalIgnoreCase);
        var title = OneLine(partyTitle);
        var name = OneLine(toName);
        var grouped = $"{otp[..3]} {otp[3..]}";
        var role = RoleName(roleKey, english);
        var minutes = (int)PartyCrewLimits.ChallengeLifetime.TotalMinutes;

        if (english)
        {
            return new EmailMessage(
                toAddress,
                name,
                $"Access code for \"{title}\"",
                $"""
                 Hello {name},

                 your access code for {title} is:

                 {grouped}

                 You are connecting a device as: {role}

                 The code expires in {minutes} minutes and works only in the browser
                 that asked for it. If you did not ask for it, ignore this message —
                 nobody can use the code without that browser.
                 """);
        }

        return new EmailMessage(
            toAddress,
            name,
            $"Codice di accesso a «{title}»",
            $"""
             Ciao {name},

             il tuo codice di accesso a {title} è:

             {grouped}

             Stai associando un dispositivo come: {role}

             Il codice scade tra {minutes} minuti e funziona solo nel browser da cui
             è stato richiesto. Se non sei stato tu, ignora questo messaggio: senza
             quel browser il codice non serve a nessuno.
             """);
    }

    /// <summary>
    /// The role, in the product's words.
    ///
    /// <para>Here rather than shared with the frontend dictionary, because a
    /// message composed on the server cannot reach into a browser's bundle —
    /// and because the host's language, not the reader's, is what the party
    /// speaks.</para>
    /// </summary>
    private static string RoleName(string roleKey, bool english) => roleKey switch
    {
        PartyCrewRoles.CoOrganizer => english ? "Co-organizer" : "Co-organizzatore",
        PartyCrewRoles.Director => english ? "Director" : "Regista",
        PartyCrewRoles.Dj => "DJ",
        PartyCrewRoles.Reception => english ? "Reception" : "Accoglienza",
        PartyCrewRoles.Honoree => english ? "Guest of honour" : "Festeggiato",
        _ => english ? "Collaborator" : "Collaboratore",
    };

    // A Subject is one line. A party's title is the host's own text and may
    // carry a break the page renders happily and a header may not.
    private static string OneLine(string value) =>
        value.Replace('\r', ' ').Replace('\n', ' ').Trim();
}
