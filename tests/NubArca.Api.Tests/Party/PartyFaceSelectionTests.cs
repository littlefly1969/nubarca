using NubArca.Api.Domain;
using Xunit;

namespace NubArca.Api.Tests.Party;

/// <summary>
/// WHICH FACE THE PARTY SEARCHES FOR, and when it refuses to guess.
///
/// <para>A pure function with no database, no HTTP and no AI backend, which is
/// the point of it: the decision that matters most for a guest's privacy is
/// the one that is cheapest to state and to check.</para>
/// </summary>
public class PartyFaceSelectionTests
{
    private static PartyFaceSelection.DetectedFaceBox Box(
        double x, double y, double w, double h) => new(x, y, w, h);

    /// <summary>A face of side <paramref name="side"/> centred on the frame.</summary>
    private static PartyFaceSelection.DetectedFaceBox Centred(double side) =>
        Box(0.5 - side / 2, 0.5 - side / 2, side, side);

    [Fact]
    public void No_Faces_Is_Empty_Not_Ambiguous()
    {
        var choice = PartyFaceSelection.Choose([]);

        // "We cannot see a face" and "there are several of you" are different
        // sentences with different advice under them.
        Assert.True(choice.Empty);
        Assert.False(choice.Ambiguous);
        Assert.Null(choice.Face);
    }

    [Fact]
    public void One_Face_Is_That_Face_Wherever_It_Is()
    {
        // A single face is never ambiguous, even right at the edge: there is
        // nobody it could be confused with.
        var edge = Box(0.0, 0.0, 0.2, 0.2);

        var choice = PartyFaceSelection.Choose([edge]);

        Assert.False(choice.Ambiguous);
        Assert.Equal(edge, choice.Face);
    }

    [Fact]
    public void Two_Faces_Of_The_Same_Size_Are_Refused()
    {
        // THE CASE THIS RULE EXISTS FOR. Two people side by side: the old
        // behaviour took whichever box was a pixel larger and handed one of
        // them the other's evening.
        var left = Box(0.10, 0.35, 0.28, 0.28);
        var right = Box(0.62, 0.35, 0.28, 0.28);

        var choice = PartyFaceSelection.Choose([left, right]);

        Assert.True(choice.Ambiguous);
        Assert.Null(choice.Face);
    }

    [Fact]
    public void Two_Faces_Barely_Different_In_Size_Are_Refused()
    {
        // Ten per cent bigger is standing slightly closer, not "this is me".
        var bigger = Box(0.10, 0.35, 0.30, 0.30);
        var other = Box(0.60, 0.35, 0.285, 0.285);

        Assert.True(PartyFaceSelection.Choose([bigger, other]).Ambiguous);
    }

    [Fact]
    public void A_Dominant_Centred_Face_Wins()
    {
        // Somebody holding the phone, with a friend further back.
        var subject = Centred(0.34);
        var behind = Box(0.04, 0.12, 0.16, 0.16);

        var choice = PartyFaceSelection.Choose([behind, subject]);

        Assert.False(choice.Ambiguous);
        Assert.Equal(subject, choice.Face);
    }

    [Fact]
    public void A_Big_Face_At_The_Edge_Is_Refused()
    {
        // Somebody walking past close to the camera is the WORST case to guess
        // in: they are large, and they are not the guest.
        var passerBy = Box(0.0, 0.0, 0.40, 0.40);
        var subject = Box(0.60, 0.60, 0.16, 0.16);

        var choice = PartyFaceSelection.Choose([passerBy, subject]);

        Assert.True(choice.Ambiguous);
    }

    [Fact]
    public void Degenerate_Boxes_Neither_Win_Nor_Create_Ambiguity()
    {
        // A backend that emits a zero-width box must not be able to turn a
        // perfectly good single-face selfie into a refusal.
        var real = Centred(0.30);
        var zeroWidth = Box(0.2, 0.2, 0, 0.3);
        var negative = Box(0.7, 0.7, -0.1, 0.2);

        var choice = PartyFaceSelection.Choose([zeroWidth, real, negative]);

        Assert.False(choice.Ambiguous);
        Assert.Equal(real, choice.Face);
    }

    [Fact]
    public void Only_Degenerate_Boxes_Are_No_Face_At_All()
    {
        var choice = PartyFaceSelection.Choose([Box(0.1, 0.1, 0, 0)]);

        Assert.True(choice.Empty);
        Assert.False(choice.Ambiguous);
    }

    [Fact]
    public void The_Runner_Up_Decides_Dominance_Not_The_Average()
    {
        // One large face and four tiny ones would look "dominant" against an
        // average. Against the RUNNER-UP — another large face — it is not, and
        // that is the situation that matters.
        var one = Box(0.05, 0.35, 0.30, 0.30);
        var two = Box(0.62, 0.35, 0.29, 0.29);
        var tiny = Box(0.45, 0.05, 0.04, 0.04);

        Assert.True(PartyFaceSelection.Choose([one, two, tiny, tiny, tiny]).Ambiguous);
    }

    [Fact]
    public void The_Rule_Is_Order_Independent()
    {
        var subject = Centred(0.34);
        var behind = Box(0.04, 0.12, 0.16, 0.16);

        // Whatever order a detector returns, the same face is chosen: the rule
        // is a property of the geometry, not of an array.
        Assert.Equal(subject, PartyFaceSelection.Choose([subject, behind]).Face);
        Assert.Equal(subject, PartyFaceSelection.Choose([behind, subject]).Face);
    }
}
