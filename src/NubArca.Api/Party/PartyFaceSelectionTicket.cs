using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Configuration;

namespace NubArca.Api.Party;

/// <summary>
/// THE FACE THE GUEST WAS SHOWN, carried back to the search that embeds it.
///
/// <para>Detection and search are two requests, and between them sits a
/// detector that is not required to be deterministic: a re-run may return the
/// same people in another order, with slightly different geometry, or with a
/// second face that has come closer to the camera. If the search simply asked
/// <see cref="Domain.PartyFaceSelection"/> again, it could legitimately reach a
/// DIFFERENT face from the one framed on the phone — and the guest would be
/// shown a picture of one decision while their results came from another. This
/// is what closes that gap: the choice is made once, in the detection, and the
/// search is handed it rather than making it again.</para>
///
/// <para><b>It is a binding, not a capability.</b> It authorises nothing. A
/// stolen ticket lets its holder search the selfie it was minted for, which is
/// exactly what they could do by uploading that selfie themselves.</para>
///
/// <para><b>What is in it.</b> A version, a CSPRNG nonce, an expiry, and the
/// chosen box. Nothing else: no person, no identity, no face id, no embedding
/// and no album. The selfie and the face package are bound WITHOUT being
/// disclosed — they go into the MAC's message and not into the payload, so a
/// ticket cannot be presented with another selfie or after the installation has
/// changed face model, and yet a holder learns neither.</para>
///
/// <para><b>Nothing is persisted</b>, so there is no stored ticket to hash, to
/// leak, or to clean up. The cost is that a restart invalidates tickets in
/// flight, which lands in the failure mode this feature already has: the guest
/// is asked for another selfie.</para>
/// </summary>
public interface IPartyFaceSelectionTickets
{
    /// <summary>
    /// Mint a ticket for the face a detection just chose. The ONLY moment this
    /// value exists — it is handed to the phone and kept nowhere.
    /// </summary>
    string Issue(ReadOnlySpan<byte> selfieBytes, Guid profileId, PartyFaceBox face, DateTime utcNow);

    /// <summary>
    /// The face this ticket was minted for, or null when it cannot be honoured:
    /// missing, malformed, tampered with, expired, minted for another selfie, or
    /// minted under a face package this installation no longer runs.
    ///
    /// <para>Null is never "search anyway": the caller refuses, because the
    /// whole point of the ticket is that a search without one is a search whose
    /// face nobody confirmed.</para>
    /// </summary>
    PartyFaceBox? Open(string? ticket, ReadOnlySpan<byte> selfieBytes, Guid profileId, DateTime utcNow);
}

public sealed class PartyFaceSelectionTickets : IPartyFaceSelectionTickets
{
    /// <summary>
    /// How long a confirmed face stays usable.
    ///
    /// <para>Long enough for the scanner's three passes and a slow upload on a
    /// party's contended wifi; short enough that a ticket found later is a
    /// ticket that has already stopped working. It is not a session: the guest
    /// is looking at the frame the whole time.</para>
    /// </summary>
    internal static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(3);

    // The MAC's domain separator. A ticket is for the search and for nothing
    // else, and this is what makes that true of the bytes rather than of a
    // convention: a value minted here cannot be verified by any other
    // construction that shares the party secret.
    private const string Purpose = "nubarca-party-face-selection-v1";

    private const byte Version = 1;
    private const int NonceBytes = 16;
    private const int PayloadBytes = 1 + NonceBytes + 8 + (8 * 4); // version, nonce, expiry, box
    private const int MacBytes = 32;

    // The same key material as every other party token: an installation
    // configures ONE party secret, not a fourth one for this.
    private const string DefaultSecret = "nubarca-party-token-secret-v1";

    private readonly byte[] _secret;

    public PartyFaceSelectionTickets(IConfiguration config)
    {
        var configured = config["Party:TokenSecret"];
        _secret = Encoding.UTF8.GetBytes(
            string.IsNullOrWhiteSpace(configured) ? DefaultSecret : configured);
    }

    public string Issue(
        ReadOnlySpan<byte> selfieBytes, Guid profileId, PartyFaceBox face, DateTime utcNow)
    {
        var token = new byte[PayloadBytes + MacBytes];
        var payload = token.AsSpan(0, PayloadBytes);

        payload[0] = Version;
        RandomNumberGenerator.Fill(payload.Slice(1, NonceBytes));
        BinaryPrimitives.WriteInt64LittleEndian(
            payload.Slice(1 + NonceBytes, 8),
            new DateTimeOffset(utcNow.Add(Lifetime), TimeSpan.Zero).ToUnixTimeSeconds());
        WriteBox(payload[(1 + NonceBytes + 8)..], face);

        Sign(payload, selfieBytes, profileId, token.AsSpan(PayloadBytes));
        return Base64Url(token);
    }

    public PartyFaceBox? Open(
        string? ticket, ReadOnlySpan<byte> selfieBytes, Guid profileId, DateTime utcNow)
    {
        if (string.IsNullOrWhiteSpace(ticket))
        {
            return null;
        }

        if (!TryFromBase64Url(ticket, out var token) || token.Length != PayloadBytes + MacBytes)
        {
            return null;
        }

        var payload = token.AsSpan(0, PayloadBytes);
        if (payload[0] != Version)
        {
            return null;
        }

        // THE MAC FIRST, and in fixed time. Nothing below reads a field this
        // has not yet vouched for — an expiry or a box taken from an unverified
        // payload is a value the caller chose.
        Span<byte> expected = stackalloc byte[MacBytes];
        Sign(payload, selfieBytes, profileId, expected);
        if (!CryptographicOperations.FixedTimeEquals(expected, token.AsSpan(PayloadBytes)))
        {
            return null;
        }

        var expiry = BinaryPrimitives.ReadInt64LittleEndian(payload.Slice(1 + NonceBytes, 8));
        if (DateTimeOffset.FromUnixTimeSeconds(expiry) <= new DateTimeOffset(utcNow, TimeSpan.Zero))
        {
            return null;
        }

        return ReadBox(payload[(1 + NonceBytes + 8)..]);
    }

    /// <summary>
    /// HMAC over the payload AND the two things bound but never disclosed: the
    /// selfie these coordinates were measured in, and the face package that
    /// measured them.
    /// </summary>
    private void Sign(
        ReadOnlySpan<byte> payload, ReadOnlySpan<byte> selfieBytes, Guid profileId, Span<byte> destination)
    {
        Span<byte> selfie = stackalloc byte[32];
        SHA256.HashData(selfieBytes, selfie);

        Span<byte> profile = stackalloc byte[16];
        profileId.TryWriteBytes(profile);

        var purpose = Encoding.UTF8.GetBytes(Purpose);
        var message = new byte[purpose.Length + payload.Length + selfie.Length + profile.Length];
        var at = 0;
        purpose.CopyTo(message.AsSpan(at));
        at += purpose.Length;
        payload.CopyTo(message.AsSpan(at));
        at += payload.Length;
        selfie.CopyTo(message.AsSpan(at));
        at += selfie.Length;
        profile.CopyTo(message.AsSpan(at));

        HMACSHA256.HashData(_secret, message, destination);
    }

    private static void WriteBox(Span<byte> destination, PartyFaceBox face)
    {
        BinaryPrimitives.WriteDoubleLittleEndian(destination[..8], face.X);
        BinaryPrimitives.WriteDoubleLittleEndian(destination.Slice(8, 8), face.Y);
        BinaryPrimitives.WriteDoubleLittleEndian(destination.Slice(16, 8), face.Width);
        BinaryPrimitives.WriteDoubleLittleEndian(destination.Slice(24, 8), face.Height);
    }

    private static PartyFaceBox ReadBox(ReadOnlySpan<byte> source) =>
        new(BinaryPrimitives.ReadDoubleLittleEndian(source[..8]),
            BinaryPrimitives.ReadDoubleLittleEndian(source.Slice(8, 8)),
            BinaryPrimitives.ReadDoubleLittleEndian(source.Slice(16, 8)),
            BinaryPrimitives.ReadDoubleLittleEndian(source.Slice(24, 8)));

    private static string Base64Url(byte[] value) =>
        Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static bool TryFromBase64Url(string value, out byte[] bytes)
    {
        bytes = [];
        var padding = (value.Length % 4) switch
        {
            0 => "",
            2 => "==",
            3 => "=",
            // A length of 1 mod 4 is not base64 of anything.
            _ => null,
        };
        if (padding is null)
        {
            return false;
        }

        try
        {
            bytes = Convert.FromBase64String(
                value.Replace('-', '+').Replace('_', '/') + padding);
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }
}
