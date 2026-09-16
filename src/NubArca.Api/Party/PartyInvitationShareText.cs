namespace NubArca.Api.Party;

/// <summary>
/// The message a host shares — on WhatsApp, or wherever a copied link goes —
/// composed on the SERVER, in the host's persisted UI language, exactly as the
/// invitation email is.
///
/// <para>Short on purpose. It says who is invited to what, where the personal
/// invitation is, and that the reply happens there. Everything else — the day,
/// the place, "Sono qui" and "Entra nel Party" once the party is live — is on the
/// invitation itself, which is always current; a message cannot be taken
/// back.</para>
///
/// <para>The one URL in it is the personal link, built by the caller on the
/// operator's public origin, never on a request's Host header. The text is
/// returned to the host and never stored, logged or audited.</para>
/// </summary>
public static class PartyInvitationShareText
{
    public static string Compose(string hostLanguage, string partyTitle, string invitationUrl)
    {
        var title = PartyInvitationEmail.OneLine(partyTitle);
        return string.Equals(hostLanguage, "en", StringComparison.OrdinalIgnoreCase)
            ? $"You're invited to \"{title}\" 🎉\n\nOpen your personal invitation:\n{invitationUrl}\n\nFrom there you can let us know if you're coming."
            : $"Sei invitato a \"{title}\" 🎉\n\nApri il tuo invito personale:\n{invitationUrl}\n\nDa qui puoi confermare la partecipazione.";
    }
}
