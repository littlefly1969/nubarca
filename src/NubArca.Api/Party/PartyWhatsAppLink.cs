using System.Text;

namespace NubArca.Api.Party;

/// <summary>
/// WhatsApp CLICK-TO-CHAT — a link the host's own browser opens, and nothing
/// more. No WhatsApp Business API, no provider, no webhook: NubArca never sends
/// a WhatsApp message, never learns whether one was sent, and never talks to
/// Meta. The host opens WhatsApp (app, Web or Desktop) with the message ready
/// and sends it themselves.
///
/// <para>A number opens a chat DIRECTLY only when it is certain: written in
/// international form (<c>+39 333 123 4567</c> or <c>0039 …</c>), digits and the
/// usual separators, a country code that does not start with 0, and at most the
/// fifteen digits E.164 allows. Anything else — a national number with no
/// country code, an extension, a letter, the ambiguous "(0)" trunk notation — is
/// not guessed at: the link then carries only the text, and WhatsApp asks the
/// host whom to send it to. Guessing "+39" for a number that belongs to a guest
/// abroad would open a chat with a stranger.</para>
/// </summary>
public static class PartyWhatsAppLink
{
    public const string Origin = "https://wa.me/";

    /// <summary>
    /// The number as click-to-chat wants it — country code and subscriber
    /// digits, no "+", no separators — or null when it is absent or not certain.
    /// </summary>
    public static string? InternationalNumber(string? phone)
    {
        var value = phone?.Trim();
        if (string.IsNullOrEmpty(value) || value.Contains("(0)", StringComparison.Ordinal)) return null;

        var digits = new StringBuilder(value.Length);
        var plus = false;
        for (var i = 0; i < value.Length; i++)
        {
            var c = value[i];
            if (c == '+' && i == 0) plus = true;
            else if (char.IsAsciiDigit(c)) digits.Append(c);
            else if (c is not (' ' or ' ' or '-' or '.' or '/' or '(' or ')')) return null;
        }

        var number = digits.ToString();
        if (!plus)
        {
            // "00" is the international prefix written out; a number without
            // either is national, and its country is a guess.
            if (!number.StartsWith("00", StringComparison.Ordinal)) return null;
            number = number[2..];
        }
        return number.Length is >= 7 and <= 15 && number[0] != '0' ? number : null;
    }

    /// <summary>
    /// <c>https://wa.me/&lt;number&gt;?text=…</c> for a certain number, else
    /// <c>https://wa.me/?text=…</c> — which asks the host for a recipient.
    /// </summary>
    public static string Build(string? phone, string text) =>
        Origin + InternationalNumber(phone) + "?text=" + Uri.EscapeDataString(text);
}
