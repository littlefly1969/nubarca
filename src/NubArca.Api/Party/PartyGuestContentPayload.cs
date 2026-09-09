using System.Text.Json;
using System.Text.Json.Serialization;
using NubArca.Api.Domain;

namespace NubArca.Api.Party;

// The six payload shapes, and the ONE place they are validated.
//
// A browser that knows the route must not be able to persist arbitrary JSON, so
// every payload is parsed into the shape its kind declares, checked against
// stated limits, and RE-SERIALIZED from the parsed object. What is stored is
// therefore what was validated: an unknown field is dropped rather than kept,
// and the stored document can never contain something no reader expects.
//
// Nothing here accepts HTML or Markdown. These are strings the guest surface
// renders as text, which is what makes "no custom HTML" a property of the data
// rather than a promise about the renderer.

public sealed record PartyInvitationContent(string? Headline, string? Message);

// No map URL, deliberately: an arbitrary external link stored as authority is
// somebody else's page one QR code away. The address is data, and the guest
// surface builds a safe maps link from it if it wants one.
public sealed record PartyLocationContent(string VenueName, string Address, string? Note);

public sealed record PartyDressCodeContent(string Headline, string? Description);

public sealed record PartyMenuSection(string Title, IReadOnlyList<string> Items);

public sealed record PartyMenuContent(string? Intro, IReadOnlyList<PartyMenuSection> Sections);

public sealed record PartyInfoContent(string Title, string Body);

public sealed record PartyThankYouContent(string? Headline, string? Message);

/// <summary>Why a payload was refused. The HTTP layer maps this to one code.</summary>
public enum PartyGuestContentError
{
    UnknownKind,
    InvalidPayload,
}

public sealed record PartyGuestContentValidation(
    string? CanonicalJson, PartyGuestContentError? Error)
{
    public static PartyGuestContentValidation Ok(string json) => new(json, null);
    public static PartyGuestContentValidation Fail(PartyGuestContentError error) => new(null, error);
}

public static class PartyGuestContentPayload
{
    // Generous enough for anything a host would actually write, small enough
    // that no slot can become a document. Counted in Unicode code points, the
    // one unit .NET and a browser agree on exactly.
    public const int MaxHeadline = 120;
    public const int MaxShortText = 400;
    public const int MaxLongText = 2000;
    public const int MaxSections = 12;
    public const int MaxItemsPerSection = 40;
    public const int MaxItemLength = 200;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    /// <summary>
    /// Parses, validates and canonicalises one payload for its kind.
    ///
    /// <para>Strings are trimmed; an optional field that trims to nothing
    /// becomes null rather than an empty string, so "present but blank" is not a
    /// state the guest surface has to render.</para>
    /// </summary>
    public static PartyGuestContentValidation Validate(string kind, JsonElement? content)
    {
        if (!PartyGuestContentKinds.IsKnown(kind))
        {
            return PartyGuestContentValidation.Fail(PartyGuestContentError.UnknownKind);
        }
        if (content is not JsonElement element || element.ValueKind != JsonValueKind.Object)
        {
            return PartyGuestContentValidation.Fail(PartyGuestContentError.InvalidPayload);
        }

        try
        {
            return kind switch
            {
                PartyGuestContentKinds.Invitation => Canonical(ReadInvitation(element)),
                PartyGuestContentKinds.Location => Canonical(ReadLocation(element)),
                PartyGuestContentKinds.DressCode => Canonical(ReadDressCode(element)),
                PartyGuestContentKinds.Menu => Canonical(ReadMenu(element)),
                PartyGuestContentKinds.Info => Canonical(ReadInfo(element)),
                _ => Canonical(ReadThankYou(element)),
            };
        }
        catch (InvalidPayloadException)
        {
            return PartyGuestContentValidation.Fail(PartyGuestContentError.InvalidPayload);
        }
        catch (JsonException)
        {
            return PartyGuestContentValidation.Fail(PartyGuestContentError.InvalidPayload);
        }
    }

    /// <summary>The empty document a slot the host has never written carries.</summary>
    public static string Empty(string kind) => kind switch
    {
        PartyGuestContentKinds.Location => Serialize(new PartyLocationContent("", "", null)),
        PartyGuestContentKinds.DressCode => Serialize(new PartyDressCodeContent("", null)),
        PartyGuestContentKinds.Menu => Serialize(new PartyMenuContent(null, [])),
        PartyGuestContentKinds.Info => Serialize(new PartyInfoContent("", "")),
        PartyGuestContentKinds.Invitation => Serialize(new PartyInvitationContent(null, null)),
        _ => Serialize(new PartyThankYouContent(null, null)),
    };

    private static PartyGuestContentValidation Canonical<T>(T payload) =>
        PartyGuestContentValidation.Ok(Serialize(payload));

    private static string Serialize<T>(T payload) => JsonSerializer.Serialize(payload, Json);

    // --- per-kind readers ---------------------------------------------------

    private static PartyInvitationContent ReadInvitation(JsonElement e) =>
        new(Optional(e, "headline", MaxHeadline), Optional(e, "message", MaxLongText));

    private static PartyLocationContent ReadLocation(JsonElement e) =>
        new(Required(e, "venueName", MaxShortText),
            Required(e, "address", MaxShortText),
            Optional(e, "note", MaxLongText));

    private static PartyDressCodeContent ReadDressCode(JsonElement e) =>
        new(Required(e, "headline", MaxHeadline), Optional(e, "description", MaxLongText));

    private static PartyInfoContent ReadInfo(JsonElement e) =>
        new(Required(e, "title", MaxHeadline), Required(e, "body", MaxLongText));

    private static PartyThankYouContent ReadThankYou(JsonElement e) =>
        new(Optional(e, "headline", MaxHeadline), Optional(e, "message", MaxLongText));

    private static PartyMenuContent ReadMenu(JsonElement e)
    {
        var intro = Optional(e, "intro", MaxLongText);
        var sections = new List<PartyMenuSection>();

        if (e.TryGetProperty("sections", out var raw) && raw.ValueKind == JsonValueKind.Array)
        {
            if (raw.GetArrayLength() > MaxSections) throw new InvalidPayloadException();
            foreach (var section in raw.EnumerateArray())
            {
                if (section.ValueKind != JsonValueKind.Object) throw new InvalidPayloadException();
                var title = Required(section, "title", MaxHeadline);
                var items = new List<string>();
                if (section.TryGetProperty("items", out var rawItems)
                    && rawItems.ValueKind == JsonValueKind.Array)
                {
                    if (rawItems.GetArrayLength() > MaxItemsPerSection) throw new InvalidPayloadException();
                    foreach (var item in rawItems.EnumerateArray())
                    {
                        if (item.ValueKind != JsonValueKind.String) throw new InvalidPayloadException();
                        var text = Trim(item.GetString());
                        if (text is null || CodePoints(text) > MaxItemLength)
                        {
                            throw new InvalidPayloadException();
                        }
                        items.Add(text);
                    }
                }
                sections.Add(new PartyMenuSection(title, items));
            }
        }

        return new PartyMenuContent(intro, sections);
    }

    // --- primitives ---------------------------------------------------------

    private static string Required(JsonElement e, string name, int max)
    {
        var value = Read(e, name, max);
        return value ?? throw new InvalidPayloadException();
    }

    private static string? Optional(JsonElement e, string name, int max) => Read(e, name, max);

    private static string? Read(JsonElement e, string name, int max)
    {
        if (!e.TryGetProperty(name, out var property)) return null;
        if (property.ValueKind == JsonValueKind.Null) return null;
        if (property.ValueKind != JsonValueKind.String) throw new InvalidPayloadException();
        var value = Trim(property.GetString());
        if (value is not null && CodePoints(value) > max) throw new InvalidPayloadException();
        return value;
    }

    private static string? Trim(string? value)
    {
        var trimmed = value?.Trim();
        return string.IsNullOrEmpty(trimmed) ? null : trimmed;
    }

    private static int CodePoints(string value)
    {
        var count = 0;
        foreach (var _ in value.EnumerateRunes()) count++;
        return count;
    }

    private sealed class InvalidPayloadException : Exception;
}
