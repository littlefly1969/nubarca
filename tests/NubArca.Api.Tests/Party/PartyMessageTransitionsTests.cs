using NubArca.Api.Domain;

namespace NubArca.Api.Tests.Party;

// The moderation state machine, tested exhaustively rather than by example.
//
// The endpoint tests prove the routes honour it; this proves the table itself
// is total and small. A matrix is worth having only if the set of things it
// permits is enumerable, and the point of enumerating it here is that ADDING a
// permitted transition has to be a deliberate edit to this list, not something
// that arrives as a side effect of touching the switch.
public sealed class PartyMessageTransitionsTests
{
    private static readonly PartyMessageModeration[] AllActions =
        Enum.GetValues<PartyMessageModeration>();

    // Every pair the domain permits, and its result. Everything else is refused.
    public static TheoryData<string, PartyMessageModeration, string> Allowed() => new()
    {
        { PartyMessageStatuses.Pending, PartyMessageModeration.Approve, PartyMessageStatuses.Visible },
        { PartyMessageStatuses.Pending, PartyMessageModeration.Reject, PartyMessageStatuses.Rejected },
        { PartyMessageStatuses.Visible, PartyMessageModeration.Hide, PartyMessageStatuses.Hidden },
        { PartyMessageStatuses.Hidden, PartyMessageModeration.Restore, PartyMessageStatuses.Visible },
        { PartyMessageStatuses.Rejected, PartyMessageModeration.Restore, PartyMessageStatuses.Visible },
    };

    [Theory]
    [MemberData(nameof(Allowed))]
    public void A_Permitted_Transition_Lands_Where_The_Product_Says(
        string from, PartyMessageModeration action, string expected)
    {
        Assert.Equal(expected, PartyMessageTransitions.Target(from, action));
    }

    [Fact]
    public void Exactly_Five_Of_The_Sixteen_Pairs_Are_Permitted()
    {
        var permitted = (
            from status in PartyMessageStatuses.All
            from action in AllActions
            where PartyMessageTransitions.Target(status, action) is not null
            select (status, action)).ToList();

        // Four states times four actions. If this count moves, somebody widened
        // what a manager can do, and that is a product decision rather than a
        // refactor.
        Assert.Equal(4 * 4, PartyMessageStatuses.All.Count * AllActions.Length);
        Assert.Equal(5, permitted.Count);
    }

    [Fact]
    public void Every_Permitted_Transition_Actually_Changes_The_State()
    {
        // v1 is a strict machine: there is no permitted pair that succeeds while
        // doing nothing. That is what makes "approve something already visible"
        // an answer rather than a silent no-op.
        foreach (var status in PartyMessageStatuses.All)
        {
            foreach (var action in AllActions)
            {
                var target = PartyMessageTransitions.Target(status, action);
                if (target is not null)
                {
                    Assert.NotEqual(status, target);
                }
            }
        }
    }

    [Fact]
    public void Nothing_Can_Be_Put_Back_Into_The_Waiting_Queue()
    {
        // Pending is a birth state. A decision, once taken, is visible in the
        // queue as taken — it does not become unread again.
        foreach (var status in PartyMessageStatuses.All)
        {
            foreach (var action in AllActions)
            {
                Assert.NotEqual(
                    PartyMessageStatuses.Pending,
                    PartyMessageTransitions.Target(status, action));
            }
        }
    }

    [Fact]
    public void Both_Down_States_Come_Back_Through_Restore_And_Only_Restore()
    {
        // Whichever way a message left the wall, the manager's intent for
        // bringing it back is the same one, so it is the same route.
        Assert.Equal(PartyMessageStatuses.Visible,
            PartyMessageTransitions.Target(PartyMessageStatuses.Hidden, PartyMessageModeration.Restore));
        Assert.Equal(PartyMessageStatuses.Visible,
            PartyMessageTransitions.Target(PartyMessageStatuses.Rejected, PartyMessageModeration.Restore));

        // Approve is not a second way back: it is the decision on something
        // nobody has read yet, and an audit line saying "approved" about a
        // message that had already been rejected would describe the wrong act.
        Assert.Null(PartyMessageTransitions.Target(PartyMessageStatuses.Hidden, PartyMessageModeration.Approve));
        Assert.Null(PartyMessageTransitions.Target(PartyMessageStatuses.Rejected, PartyMessageModeration.Approve));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("approved")] // the party UPLOAD vocabulary, which is not this one
    [InlineData("removed_from_album")]
    public void An_Unknown_Current_State_Permits_Nothing(string? status)
    {
        // A hand-edited or migrated row fails closed rather than being treated
        // as some default the table never mentions.
        foreach (var action in AllActions)
        {
            Assert.Null(PartyMessageTransitions.Target(status, action));
        }
    }
}
