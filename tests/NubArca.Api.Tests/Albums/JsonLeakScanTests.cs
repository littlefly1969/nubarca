using Xunit;

namespace NubArca.Api.Tests.Albums;

public sealed class JsonLeakScanTests
{
    [Fact]
    public void A_Guid_That_Spells_The_Token_Is_Not_A_Leak()
    {
        // THE FLAKE THIS ENDS. A GUID is hexadecimal, so it can spell "face" —
        // and this exact identifier once failed the shared-album privacy test on
        // CI, for reasons that had nothing to do with what the payload
        // disclosed.
        const string body = """
        [{"albumItemId":"11bd1c54-8994-4b4e-8a89-3acface14f84","kind":"image"}]
        """;
        Assert.Empty(JsonLeakScan.Find(body, "face"));
    }

    [Fact]
    public void A_Field_Named_After_The_Token_Is_A_Leak()
    {
        const string body = """{"items":[{"faceCount":0}]}""";
        Assert.Equal(["$.items[0].faceCount (property name)"], JsonLeakScan.Find(body, "face"));
    }

    [Fact]
    public void A_Value_Carrying_The_Token_Is_A_Leak_Wherever_It_Sits()
    {
        const string body = """{"a":{"b":[{"note":"detected face cluster"}]}}""";
        Assert.Equal(["$.a.b[0].note (value)"], JsonLeakScan.Find(body, "face"));
    }

    [Fact]
    public void Nested_Structures_Are_Reported_By_Path()
    {
        const string body = """{"outer":{"personName":"x","list":[{"ok":1},{"person":"y"}]}}""";
        var found = JsonLeakScan.Find(body, "person");
        Assert.Equal(
            ["$.outer.personName (property name)", "$.outer.list[1].person (property name)"],
            found);
    }

    [Fact]
    public void A_Clean_Payload_Reports_Nothing()
    {
        const string body = """
        [{"albumId":"9c5cf278-0f6e-4cdc-b831-ad1a8763c36e","name":"Estate","itemCount":3}]
        """;
        foreach (var token in new[] { "person", "face", "cluster", "embedding", "suggest" })
        {
            Assert.Empty(JsonLeakScan.Find(body, token));
        }
    }
}
