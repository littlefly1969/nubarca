using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using NubArca.Api.Tests.Endpoints;

namespace NubArca.Api.Tests.Party;

/// <summary>
/// CHOICE ROUNDS. The room is not judging what happened — it is deciding what
/// happens next, between answers the host wrote.
///
/// What these defend:
///   * two to six answers, because below two there is nothing to decide and
///     above six the television stops being readable from across a room;
///   * a ballot only exists for the mode that is answered with one: switching a
///     round back to binary takes its answers with it, rather than leaving
///     answers to a question nobody will be asked;
///   * options are REPLACED on save, never merged by position — a host who
///     reorders their answers has not edited them;
///   * a write that sends NO options leaves the stored ballot alone, so a
///     client that predates choice rounds cannot wipe one by saving the form it
///     does understand;
///   * the outcome — "so the guest of honour has to do this" — is the half that
///     makes the mode worth having, and it survives the round trip.
/// </summary>
public sealed class PartyChallengeOptionTests : IDisposable
{
    private readonly SqliteWebApplicationFactory _factory = new();
    public PartyChallengeOptionTests() => _factory.EnsureDatabaseCreated();
    public void Dispose() => _factory.Dispose();

    [Fact]
    public async Task A_choice_round_keeps_its_answers_and_what_each_one_costs()
    {
        var (_, owner) = await _factory.CreateAuthenticatedClientAsync();
        var album = await AlbumAsync(owner);

        var created = await WriteAsync(owner, album, new
        {
            title = "La penitenza",
            body = "La sala decide",
            votingMode = "choice",
            voteQuestion = "Cosa deve fare il festeggiato?",
            options = new object[]
            {
                new { label = "Ballare", outcome = "Balla da solo per un minuto" },
                new { label = "Cantare", outcome = "Canta il ritornello" },
                new { label = "Niente", outcome = (string?)null },
            },
        });
        created.EnsureSuccessStatusCode();
        var body = await created.Content.ReadFromJsonAsync<JsonElement>();

        var options = body.GetProperty("options").EnumerateArray().ToList();
        Assert.Equal(3, options.Count);
        // The order is the host's, and it is the order it comes back in.
        Assert.Equal(["Ballare", "Cantare", "Niente"],
            options.Select(o => o.GetProperty("label").GetString()).ToArray());
        Assert.Equal("Balla da solo per un minuto", options[0].GetProperty("outcome").GetString());
        // An answer may speak for itself: no outcome is null, not an empty line.
        Assert.Equal(JsonValueKind.Null, options[2].GetProperty("outcome").ValueKind);
    }

    [Theory]
    [InlineData(1)]   // nothing to decide
    [InlineData(7)]   // past what a television can show
    public async Task A_ballot_outside_two_to_six_is_refused(int count)
    {
        var (_, owner) = await _factory.CreateAuthenticatedClientAsync();
        var album = await AlbumAsync(owner);

        var response = await WriteAsync(owner, album, new
        {
            title = "Scelta",
            body = "Corpo",
            votingMode = "choice",
            options = Enumerable.Range(1, count)
                .Select(i => new { label = $"Risposta {i}", outcome = (string?)null })
                .ToArray(),
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Answers_belong_to_the_mode_that_is_answered_with_them()
    {
        var (_, owner) = await _factory.CreateAuthenticatedClientAsync();
        var album = await AlbumAsync(owner);

        // Sending a ballot for a verdict round is a client bug, not something to
        // store: the rows would be unreachable either way.
        var refused = await WriteAsync(owner, album, new
        {
            title = "Verdetto",
            body = "Corpo",
            votingMode = "binary",
            options = new object[] { new { label = "A", outcome = (string?)null } },
        });
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);

        // And a round that STOPS being a choice loses the ballot it had.
        var id = await ChoiceAsync(owner, album);
        var back = await owner.PutAsJsonAsync($"/api/albums/{album}/party-challenges/{id}", new
        {
            title = "Ora è un verdetto",
            body = "Corpo",
            isEnabled = true,
            votingMode = "binary",
            options = Array.Empty<object>(),
        });
        back.EnsureSuccessStatusCode();
        var after = await back.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(JsonValueKind.Null, after.GetProperty("options").ValueKind);
    }

    [Fact]
    public async Task Saving_replaces_the_ballot_rather_than_merging_it()
    {
        var (_, owner) = await _factory.CreateAuthenticatedClientAsync();
        var album = await AlbumAsync(owner);
        var id = await ChoiceAsync(owner, album);

        var updated = await owner.PutAsJsonAsync($"/api/albums/{album}/party-challenges/{id}", new
        {
            title = "Scelta",
            body = "Corpo",
            isEnabled = true,
            votingMode = "choice",
            options = new object[]
            {
                new { label = "Solo questa", outcome = "E questo succede" },
                new { label = "E questa", outcome = (string?)null },
            },
        });
        updated.EnsureSuccessStatusCode();

        var options = (await updated.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("options").EnumerateArray().ToList();
        Assert.Equal(2, options.Count);
        Assert.Equal(["Solo questa", "E questa"],
            options.Select(o => o.GetProperty("label").GetString()).ToArray());
    }

    [Fact]
    public async Task A_client_that_never_heard_of_answers_cannot_wipe_them()
    {
        var (_, owner) = await _factory.CreateAuthenticatedClientAsync();
        var album = await AlbumAsync(owner);
        var id = await ChoiceAsync(owner, album);

        // The whole form an older composer knows — and no `options` at all.
        var saved = await owner.PutAsJsonAsync($"/api/albums/{album}/party-challenges/{id}", new
        {
            title = "Rinominata",
            body = "Corpo aggiornato",
            isEnabled = true,
            votingMode = "choice",
        });
        saved.EnsureSuccessStatusCode();

        // Read ONCE: the response body is a stream, and a second read finds it
        // closed.
        var after = await saved.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(3, after.GetProperty("options").GetArrayLength());
        Assert.Equal("Rinominata", after.GetProperty("title").GetString());
    }

    [Fact]
    public async Task The_deck_lists_every_ballot_and_leaves_other_rounds_without_one()
    {
        var (_, owner) = await _factory.CreateAuthenticatedClientAsync();
        var album = await AlbumAsync(owner);
        await ChoiceAsync(owner, album);
        (await WriteAsync(owner, album, new
        {
            title = "Verdetto",
            body = "Corpo",
            votingMode = "binary",
        })).EnsureSuccessStatusCode();

        var list = await owner.GetFromJsonAsync<JsonElement>($"/api/albums/{album}/party-challenges");
        var items = list.GetProperty("items").EnumerateArray().ToList();
        Assert.Equal(2, items.Count);

        var choice = items.Single(i => i.GetProperty("votingMode").GetString() == "choice");
        Assert.Equal(3, choice.GetProperty("options").GetArrayLength());
        var binary = items.Single(i => i.GetProperty("votingMode").GetString() == "binary");
        Assert.Equal(JsonValueKind.Null, binary.GetProperty("options").ValueKind);
    }

    [Fact]
    public async Task A_label_longer_than_the_limit_is_refused()
    {
        var (_, owner) = await _factory.CreateAuthenticatedClientAsync();
        var album = await AlbumAsync(owner);

        var response = await WriteAsync(owner, album, new
        {
            title = "Scelta",
            body = "Corpo",
            votingMode = "choice",
            options = new object[]
            {
                new { label = new string('x', 61), outcome = (string?)null },
                new { label = "Va bene", outcome = (string?)null },
            },
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // --- helpers -----------------------------------------------------------

    private static Task<HttpResponseMessage> WriteAsync(HttpClient owner, Guid album, object body) =>
        owner.PostAsJsonAsync($"/api/albums/{album}/party-challenges", body);

    private static async Task<Guid> ChoiceAsync(HttpClient owner, Guid album)
    {
        var response = await WriteAsync(owner, album, new
        {
            title = "Scelta",
            body = "Corpo",
            votingMode = "choice",
            options = new object[]
            {
                new { label = "Ballare", outcome = "Balla" },
                new { label = "Cantare", outcome = "Canta" },
                new { label = "Niente", outcome = (string?)null },
            },
        });
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
    }

    private static async Task<Guid> AlbumAsync(HttpClient owner)
    {
        var response = await owner.PostAsJsonAsync("/api/albums", new { name = "Festa" });
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
    }
}
