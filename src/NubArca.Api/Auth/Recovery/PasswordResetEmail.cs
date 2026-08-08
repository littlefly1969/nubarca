using System.Globalization;
using NubArca.Api.Domain;

namespace NubArca.Api.Auth.Recovery;

// The recovery message itself, in the recipient's own persisted UI language.
//
// It states three things and nothing else: a reset was requested, the link
// expires shortly, ignore this if it was not you. It never contains the
// existing password (nobody has it — only a hash is stored), never any other
// secret, and never a remote image or tracking pixel: an email that phones home
// on open tells a third party when a NubArca account was targeted.
public static class PasswordResetEmail
{
    public static EmailMessage Compose(User user, string resetUrl, int lifetimeMinutes)
    {
        var minutes = lifetimeMinutes.ToString(CultureInfo.InvariantCulture);
        var italian = !string.Equals(user.UiLanguage, "en", StringComparison.OrdinalIgnoreCase);

        return italian
            ? new EmailMessage(
                user.Email,
                user.DisplayName,
                "NubArca — reimposta la password",
                $"""
                Ciao {user.DisplayName},

                abbiamo ricevuto una richiesta di reimpostazione della password del tuo
                account NubArca.

                Apri questo link per scegliere una nuova password:

                {resetUrl}

                Il link scade tra {minutes} minuti e può essere usato una sola volta.

                Se non hai richiesto tu la reimpostazione, ignora questo messaggio: la
                tua password attuale resta valida e non è stata modificata.

                — NubArca
                """)
            : new EmailMessage(
                user.Email,
                user.DisplayName,
                "NubArca — reset your password",
                $"""
                Hello {user.DisplayName},

                we received a request to reset the password of your NubArca account.

                Open this link to choose a new password:

                {resetUrl}

                The link expires in {minutes} minutes and can be used only once.

                If you did not request a reset, ignore this message: your current
                password remains valid and has not been changed.

                — NubArca
                """);
    }
}
