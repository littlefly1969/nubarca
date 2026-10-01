using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NubArca.Api.Data;
using NubArca.Api.Domain;
using NubArca.Api.Storage;
using NubArca.Api.Tests.Endpoints;
using NubArca.Api.Tests.Metadata;

namespace NubArca.Api.Tests.Party;

// PARTY-GUESTBOOK-01, as a book of PHOTOGRAPH MEMORIES. A guest chooses a
// photograph from the party's album, frames it, writes a dedication and signs
// it — for the hosts to keep.
//
// The properties worth defending, and what would break if each stopped holding:
//   * the book is OPT-IN, so no party acquires one by being upgraded;
//   * a party with the book closed has no book to read and refuses a
//     hand-built memory with a stable code, storing nothing;
//   * a memory ALWAYS carries a photograph from THIS party's main album, a
//     dedication and a signature — and the photograph is re-checked at the
//     moment of publishing, not trusted from the chooser;
//   * publishing is ONE unit: the memory's own blob reference, the row and the
//     guest's slot are all taken, or none of them is;
//   * the memory OWNS its photograph: removing it from the album, or deleting
//     the file outright, leaves the memory showing it, and the blob alive;
//   * only what a manager let in is public, recomputed from the CURRENT state;
//   * the book belongs to the PARTY, not to the album, so it survives a
//     re-minted link.
public sealed class PartyGuestbookTests : IDisposable
{
    private const string OwnerEmail = "host@example.com";
    private const string StrangerEmail = "stranger@example.com";

    private readonly SqliteWebApplicationFactory _factory = new();

    public PartyGuestbookTests() => _factory.EnsureDatabaseCreated();

    public void Dispose() => _factory.Dispose();

    // ── Closed by default ───────────────────────────────────────────────────

    [Fact]
    public async Task A_party_keeps_no_book_until_its_host_opens_one()
    {
        var (_, owner) = await _factory.CreateAuthenticatedClientAsync(OwnerEmail);
        var party = await OpenPartyAsync(owner);

        // The guest surface is told nothing at all: no URL, so no card, no tab,
        // no empty state and no fetch.
        var capabilities = (await GuestContextAsync(party.ViewToken)).GetProperty("capabilities");
        Assert.Equal(JsonValueKind.Null, capabilities.GetProperty("guestbookUrl").ValueKind);

        // And the routes behind it are the same generic nothing every absent
        // Party capability is.
        var guest = _factory.CreateClient();
        Assert.Equal(HttpStatusCode.NotFound, (await guest.GetAsync($"/api/party/{party.ViewToken}/guestbook")).StatusCode);
        Assert.Equal(
            HttpStatusCode.NotFound,
            (await guest.GetAsync($"/api/party/{party.ViewToken}/guestbook/photos")).StatusCode);
    }

    [Fact]
    public async Task A_memory_sent_by_hand_to_a_party_with_no_book_is_refused_and_stored_nowhere()
    {
        var (_, owner) = await _factory.CreateAuthenticatedClientAsync(OwnerEmail);
        var party = await OpenPartyAsync(owner);
        var before = await ReferenceCountAsync(party.PhotoId);

        var refused = await SubmitRawAsync(party.ViewToken, Memory(party.PhotoId, "Ada", "Auguri"));
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Equal("guestbook_disabled", await ErrorOf(refused));

        // Refused BEFORE anything sent was looked at: nothing kept, no
        // reference taken on the photograph.
        var queue = await ManagerListAsync(owner, party.PartyId);
        Assert.Equal(0, queue.GetProperty("entries").GetArrayLength());
        Assert.False(queue.GetProperty("guestbookEnabled").GetBoolean());
        Assert.Equal(before, await ReferenceCountAsync(party.PhotoId));
    }

    // ── An open book ────────────────────────────────────────────────────────

    [Fact]
    public async Task A_published_memory_reads_back_complete_enough_to_draw_without_the_album()
    {
        var (_, owner) = await _factory.CreateAuthenticatedClientAsync(OwnerEmail);
        var party = await OpenPartyAsync(owner, guestbook: true);

        var capabilities = (await GuestContextAsync(party.ViewToken)).GetProperty("capabilities");
        Assert.Equal(
            $"/party/{party.ViewToken}/guestbook",
            capabilities.GetProperty("guestbookUrl").GetString());

        var submitted = await SubmitAsync(
            party.ViewToken,
            Memory(party.PhotoId, "Giulia", "Che serata. Grazie di tutto.", template: "polaroid",
                centerX: 0.25, centerY: 0.75, zoom: 1.5));
        Assert.Equal(PartyMessageStatuses.Visible, submitted.GetProperty("status").GetString());

        var page = await PublicBookAsync(party.ViewToken);
        Assert.True(page.GetProperty("canWrite").GetBoolean());
        Assert.Equal(PartyGuestbookLimits.MaxBodyLength, page.GetProperty("maxBodyLength").GetInt32());

        var entry = page.GetProperty("entries")[0];
        Assert.Equal(submitted.GetProperty("id").GetGuid(), entry.GetProperty("id").GetGuid());
        Assert.Equal("Giulia", entry.GetProperty("authorDisplayName").GetString());
        Assert.Equal("Che serata. Grazie di tutto.", entry.GetProperty("body").GetString());
        Assert.NotEqual(JsonValueKind.Undefined, entry.GetProperty("createdAt").ValueKind);

        // The design and the version the SERVER chose.
        var template = entry.GetProperty("template");
        Assert.Equal("polaroid", template.GetProperty("key").GetString());
        Assert.Equal(1, template.GetProperty("version").GetInt32());

        // The photograph's shape and framing, in fractions, never in pixels of
        // any screen — and a URL on the token that asked.
        var media = entry.GetProperty("media");
        Assert.Equal(PhotoWidth, media.GetProperty("width").GetInt32());
        Assert.Equal(PhotoHeight, media.GetProperty("height").GetInt32());
        Assert.Equal("landscape", media.GetProperty("orientation").GetString());
        var crop = media.GetProperty("crop");
        Assert.Equal(0.25, crop.GetProperty("centerX").GetDouble());
        Assert.Equal(0.75, crop.GetProperty("centerY").GetDouble());
        Assert.Equal(1.5, crop.GetProperty("zoom").GetDouble());

        var url = media.GetProperty("url").GetString()!;
        Assert.Equal($"/api/party/{party.ViewToken}/guestbook/{entry.GetProperty("id").GetGuid()}/photo", url);
        await AssertServesJpegAsync(url);

        // The submit answers with the memory exactly as it now reads, so the
        // guest's page can show what was published without a second fetch.
        Assert.Equal(
            "polaroid",
            submitted.GetProperty("entry").GetProperty("template").GetProperty("key").GetString());
    }

    // THE CONTRIBUTION PAGE READS THE BOOK IT OFFERS. A guest reaches the book
    // from the contribution page holding the UPLOAD token, and the same book
    // must read — fully — through both doors, each with URLs on its own token.
    [Fact]
    public async Task The_whole_read_model_opens_on_either_token_with_urls_on_the_token_that_asked()
    {
        var (_, owner) = await _factory.CreateAuthenticatedClientAsync(OwnerEmail);
        var party = await OpenPartyAsync(owner, guestbook: true);

        await SubmitAsync(party.UploadToken, Memory(party.PhotoId, "Giulia", "Che serata."));

        var fromContribution = (await PublicBookAsync(party.UploadToken)).GetProperty("entries")[0];
        var fromParty = (await PublicBookAsync(party.ViewToken)).GetProperty("entries")[0];

        // The SAME memory, not a second one.
        Assert.Equal(fromContribution.GetProperty("id").GetGuid(), fromParty.GetProperty("id").GetGuid());

        // Each URL is BUILT for the capability that asked — which is how we
        // know it is not a stored property of the row.
        var viaUpload = fromContribution.GetProperty("media").GetProperty("url").GetString()!;
        var viaView = fromParty.GetProperty("media").GetProperty("url").GetString()!;
        Assert.Contains(party.UploadToken, viaUpload, StringComparison.Ordinal);
        Assert.Contains(party.ViewToken, viaView, StringComparison.Ordinal);
        await AssertServesJpegAsync(viaUpload);
        await AssertServesJpegAsync(viaView);
    }

    // The book does not ride in on the PHOTOGRAPH switch. A host who keeps a
    // book and accepts no photographs still has guests who can read and sign
    // it — with photographs the HOST put in the album.
    [Fact]
    public async Task A_party_that_accepts_no_photographs_still_opens_its_book_to_a_contributor()
    {
        var (_, owner) = await _factory.CreateAuthenticatedClientAsync(OwnerEmail);
        var party = await OpenPartyAsync(owner, guestbook: true);
        await SetContributionsAsync(owner, party.AlbumId, new { uploadEnabled = false });

        var page = await PublicBookAsync(party.UploadToken);
        Assert.True(page.GetProperty("canWrite").GetBoolean());

        var submitted = await SubmitAsync(party.UploadToken, Memory(party.PhotoId, "Marco", "Auguri!"));
        Assert.Equal(PartyMessageStatuses.Visible, submitted.GetProperty("status").GetString());
    }

    [Fact]
    public async Task A_closed_book_is_nothing_through_the_contribution_token_too()
    {
        var (_, owner) = await _factory.CreateAuthenticatedClientAsync(OwnerEmail);
        var party = await OpenPartyAsync(owner, guestbook: false);

        var client = _factory.CreateClient();
        foreach (var token in new[] { party.UploadToken, party.ViewToken })
        {
            Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/party/{token}/guestbook")).StatusCode);
            Assert.Equal(
                HttpStatusCode.NotFound,
                (await client.GetAsync($"/api/party/{token}/guestbook/photos")).StatusCode);
            Assert.Equal(
                HttpStatusCode.NotFound,
                (await client.GetAsync($"/api/party/{token}/guestbook/photos/{party.PhotoId}/thumbnail")).StatusCode);
        }
    }

    [Fact]
    public async Task The_book_reads_newest_first_because_that_is_what_somebody_just_wrote()
    {
        var (_, owner) = await _factory.CreateAuthenticatedClientAsync(OwnerEmail);
        var party = await OpenPartyAsync(owner, guestbook: true);

        await SubmitAsync(party.ViewToken, Memory(party.PhotoId, "Ada", "Prima"));
        await SubmitAsync(party.ViewToken, Memory(party.PhotoId, "Ada", "Seconda"));

        var bodies = (await PublicBookAsync(party.ViewToken)).GetProperty("entries")
            .EnumerateArray().Select(e => e.GetProperty("body").GetString()).ToArray();
        Assert.Equal(["Seconda", "Prima"], bodies);
    }

    // ── The photographs a guest may choose ──────────────────────────────────

    [Fact]
    public async Task The_chooser_offers_the_albums_photographs_and_never_a_video()
    {
        var (_, owner) = await _factory.CreateAuthenticatedClientAsync(OwnerEmail);
        var party = await OpenPartyAsync(owner, guestbook: true);
        var video = await UploadAsync(owner, ImageFixtures.MinimalMp4(), "clip.mp4", "video/mp4");
        await AddToAlbumAsync(owner, party.AlbumId, video);

        var photos = (await _factory.CreateClient().GetFromJsonAsync<JsonElement>(
            $"/api/party/{party.UploadToken}/guestbook/photos")).GetProperty("photos");

        var only = Assert.Single(photos.EnumerateArray());
        Assert.Equal(party.PhotoId, only.GetProperty("id").GetGuid());
        Assert.Equal(PhotoWidth, only.GetProperty("width").GetInt32());
        Assert.Equal(PhotoHeight, only.GetProperty("height").GetInt32());
        Assert.Equal("landscape", only.GetProperty("orientation").GetString());

        // Its thumbnails are served, on the token that asked, by the chooser's
        // own rule — and that rule does not serve the video.
        await AssertServesJpegAsync(only.GetProperty("thumbnailUrl").GetString()!);
        await AssertServesJpegAsync(only.GetProperty("previewUrl").GetString()!);
        Assert.Equal(
            HttpStatusCode.NotFound,
            (await _factory.CreateClient().GetAsync(
                $"/api/party/{party.UploadToken}/guestbook/photos/{video}/thumbnail")).StatusCode);
        // …and never an original.
        Assert.Equal(
            HttpStatusCode.NotFound,
            (await _factory.CreateClient().GetAsync(
                $"/api/party/{party.UploadToken}/guestbook/photos/{party.PhotoId}/download")).StatusCode);
    }

    [Fact]
    public async Task A_memory_without_a_photograph_is_refused()
    {
        var (_, owner) = await _factory.CreateAuthenticatedClientAsync(OwnerEmail);
        var party = await OpenPartyAsync(owner, guestbook: true);

        var refused = await SubmitRawAsync(party.ViewToken, new
        {
            authorDisplayName = "Ada",
            body = "Senza foto",
            templateKey = "nubarca",
            crop = new { centerX = 0.5, centerY = 0.5, zoom = 1.0 },
        });
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Equal("guestbook_photo_required", await ErrorOf(refused));
        Assert.Equal(0, await EntryCountAsync());
        Assert.Equal(0, await GuestbookClaimsAsync(party.PartyId));
    }

    [Fact]
    public async Task A_photograph_from_another_album_or_from_nowhere_is_unavailable()
    {
        var (_, owner) = await _factory.CreateAuthenticatedClientAsync(OwnerEmail);
        var party = await OpenPartyAsync(owner, guestbook: true);
        var other = await OpenPartyAsync(owner, guestbook: true, albumName: "Altra");
        var elsewhereBefore = await ReferenceCountAsync(other.PhotoId);

        // The same host's OTHER party, and an id that never existed: one
        // answer, because anything finer would tell a stranger which file ids
        // exist.
        foreach (var photo in new[] { other.PhotoId, Guid.NewGuid() })
        {
            var refused = await SubmitRawAsync(party.ViewToken, Memory(photo, "Ada", "Non sua"));
            Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
            Assert.Equal("guestbook_photo_unavailable", await ErrorOf(refused));
        }

        Assert.Equal(0, await EntryCountAsync());
        Assert.Equal(0, await GuestbookClaimsAsync(party.PartyId));
        Assert.Equal(elsewhereBefore, await ReferenceCountAsync(other.PhotoId));
    }

    [Fact]
    public async Task A_video_in_the_album_is_not_a_photograph()
    {
        var (_, owner) = await _factory.CreateAuthenticatedClientAsync(OwnerEmail);
        var party = await OpenPartyAsync(owner, guestbook: true);
        var video = await UploadAsync(owner, ImageFixtures.MinimalMp4(), "clip.mp4", "video/mp4");
        await AddToAlbumAsync(owner, party.AlbumId, video);

        var refused = await SubmitRawAsync(party.ViewToken, Memory(video, "Ada", "Un video"));
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Equal("guestbook_photo_not_image", await ErrorOf(refused));
        Assert.Equal(0, await EntryCountAsync());
        Assert.Equal(0, await GuestbookClaimsAsync(party.PartyId));
    }

    [Fact]
    public async Task A_photograph_in_the_trash_is_unavailable_and_costs_nothing()
    {
        var (_, owner) = await _factory.CreateAuthenticatedClientAsync(OwnerEmail);
        var party = await OpenPartyAsync(owner, guestbook: true);
        (await owner.DeleteAsync($"/api/files/{party.PhotoId}")).EnsureSuccessStatusCode();
        var before = await ReferenceCountAsync(party.PhotoId);

        var refused = await SubmitRawAsync(party.ViewToken, Memory(party.PhotoId, "Ada", "Cestinata"));
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Equal("guestbook_photo_unavailable", await ErrorOf(refused));

        Assert.Equal(0, await EntryCountAsync());
        Assert.Equal(0, await GuestbookClaimsAsync(party.PartyId));
        Assert.Equal(before, await ReferenceCountAsync(party.PhotoId));
    }

    // THE RACE. The guest saw the photograph in the chooser; the host removed
    // it from the album while the composer was open. The publish re-asks, says
    // so with its own code, and spends nothing.
    [Fact]
    public async Task A_photograph_removed_while_the_composer_was_open_is_refused_as_unavailable()
    {
        var (_, owner) = await _factory.CreateAuthenticatedClientAsync(OwnerEmail);
        var party = await OpenPartyAsync(owner, guestbook: true);
        await SetGuestbookQuotaAsync(owner, party.AlbumId, 3);
        var guest = _factory.CreateClient();

        var offered = (await guest.GetFromJsonAsync<JsonElement>(
            $"/api/party/{party.ViewToken}/guestbook/photos")).GetProperty("photos");
        Assert.Equal(party.PhotoId, offered[0].GetProperty("id").GetGuid());
        var before = await ReferenceCountAsync(party.PhotoId);

        (await owner.DeleteAsync($"/api/albums/{party.AlbumId}/items/{party.PhotoId}")).EnsureSuccessStatusCode();

        var refused = await guest.PostAsJsonAsync(
            $"/api/party/{party.ViewToken}/guestbook", Memory(party.PhotoId, "Ada", "Troppo tardi"));
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Equal("guestbook_photo_unavailable", await ErrorOf(refused));

        Assert.Equal(0, await EntryCountAsync());
        Assert.Equal(0, await GuestbookClaimsAsync(party.PartyId));
        Assert.Equal(before, await ReferenceCountAsync(party.PhotoId));
        // The guest's allowance is whole.
        Assert.Equal(3, (await guest.GetFromJsonAsync<JsonElement>(
            $"/api/party/{party.ViewToken}/guestbook")).GetProperty("remaining").GetInt32());
    }

    // ── What publishing takes ───────────────────────────────────────────────

    [Fact]
    public async Task Publishing_takes_its_own_reference_to_the_photograph_and_exactly_one_slot()
    {
        var (_, owner) = await _factory.CreateAuthenticatedClientAsync(OwnerEmail);
        var party = await OpenPartyAsync(owner, guestbook: true);
        await SetGuestbookQuotaAsync(owner, party.AlbumId, 2);
        var blobId = await BlobOfAsync(party.PhotoId);
        var before = await ReferenceCountAsync(party.PhotoId);

        var guest = _factory.CreateClient();
        var accepted = await guest.PostAsJsonAsync(
            $"/api/party/{party.ViewToken}/guestbook",
            new
            {
                sourceMediaItemId = party.PhotoId,
                authorDisplayName = "Ada",
                body = "Per sempre",
                templateKey = "editorial",
                // The client does not decide the version. A request that names
                // one is not obeyed.
                templateVersion = 99,
                crop = new { centerX = 0.4, centerY = 0.6, zoom = 2.0 },
            });
        accepted.EnsureSuccessStatusCode();
        Assert.Equal(1, (await accepted.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("remaining").GetInt32());

        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var entry = await db.PartyGuestbookEntries.AsNoTracking().SingleAsync();

        // The SAME blob — no copy, no second blob — and one more reference.
        Assert.Equal(blobId, entry.BlobObjectId);
        Assert.Equal(before + 1, await ReferenceCountAsync(party.PhotoId));
        Assert.Equal(1, await db.BlobObjects.CountAsync(b => b.Id == blobId));

        Assert.Equal("Ada", entry.AuthorDisplayName);
        Assert.Equal("Per sempre", entry.Body);
        Assert.Equal("editorial", entry.TemplateKey);
        Assert.Equal(1, entry.TemplateVersion);
        Assert.Equal(0.4, entry.CropCenterX);
        Assert.Equal(0.6, entry.CropCenterY);
        Assert.Equal(2.0, entry.CropZoom);
        Assert.Equal(PhotoWidth, entry.PhotoWidth);
        Assert.Equal(PhotoHeight, entry.PhotoHeight);
        Assert.Equal(1, await GuestbookClaimsAsync(party.PartyId));

        // And the audit agrees that the memory owns what it holds.
        var audit = await scope.ServiceProvider.GetRequiredService<BlobReferenceAuditService>().AuditAsync();
        Assert.Equal(audit.TotalBlobs, audit.MatchedReferenceCount);
    }

    // ATOMICITY. A failure after the reference was taken and before the commit
    // must leave nothing behind: no memory, no reference, no spent slot.
    [Fact]
    public async Task A_failure_after_the_reference_is_taken_leaves_no_memory_no_reference_and_no_spent_slot()
    {
        // Its own, UNPOOLED host (any settings make it so): a pooled host would
        // carry the failing blob service into whichever test leased it next.
        using var factory = new SqliteWebApplicationFactory(new Dictionary<string, string?>
        {
            ["Party:GuestbookAtomicityProbe"] = "on",
        });
        factory.ConfigureExtraServices = services =>
        {
            services.AddScoped<BlobService>();
            services.AddScoped<IBlobService>(sp => new FailAfterAcquireBlobService(sp.GetRequiredService<BlobService>()));
        };
        factory.EnsureDatabaseCreated();
        var (_, owner) = await factory.CreateAuthenticatedClientAsync(OwnerEmail);
        var party = await OpenPartyAsync(owner, guestbook: true);
        await SetGuestbookQuotaAsync(owner, party.AlbumId, 2);
        var before = await ReferenceCountAsync(factory, party.PhotoId);

        var guest = factory.CreateClient();
        try
        {
            var response = await guest.PostAsJsonAsync(
                $"/api/party/{party.ViewToken}/guestbook", Memory(party.PhotoId, "Ada", "Non arriverà"));
            Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains(FailAfterAcquireBlobService.Marker))
        {
            // The test host rethrows an unhandled failure rather than answering
            // 500; either way the request failed where it was meant to.
        }

        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Equal(0, await db.PartyGuestbookEntries.CountAsync());
        Assert.Equal(before, await ReferenceCountAsync(factory, party.PhotoId));
        Assert.Equal(0, await db.PartyParticipants.SumAsync(p => p.SubmittedGuestbookCount));
    }

    // THE ACCEPTANCE TEST of the whole feature. A memory's photograph is the
    // memory's own: the host removes it from the album, deletes the file and
    // empties the Trash, the janitor runs — and the memory still shows it.
    [Fact]
    public async Task A_memory_keeps_its_photograph_after_the_album_file_is_gone_for_good()
    {
        var (_, owner) = await _factory.CreateAuthenticatedClientAsync(OwnerEmail);
        var party = await OpenPartyAsync(owner, guestbook: true);
        var blobId = await BlobOfAsync(party.PhotoId);
        await SubmitAsync(party.ViewToken, Memory(party.PhotoId, "Ada", "Resta"));

        (await owner.DeleteAsync($"/api/albums/{party.AlbumId}/items/{party.PhotoId}")).EnsureSuccessStatusCode();
        (await owner.DeleteAsync($"/api/files/{party.PhotoId}")).EnsureSuccessStatusCode();
        (await owner.DeleteAsync($"/api/trash/files/{party.PhotoId}")).EnsureSuccessStatusCode();
        await RunJanitorAsync();

        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            Assert.False(await db.FileItems.IgnoreQueryFilters().AnyAsync(f => f.Id == party.PhotoId));
            var blob = await db.BlobObjects.AsNoTracking().SingleAsync(b => b.Id == blobId);
            Assert.Equal(1, blob.ReferenceCount);
        }

        // The book reads the same, and the picture is served.
        var entry = (await PublicBookAsync(party.ViewToken)).GetProperty("entries")[0];
        Assert.Equal("Resta", entry.GetProperty("body").GetString());
        await AssertServesJpegAsync(entry.GetProperty("media").GetProperty("url").GetString()!);

        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var audit = await scope.ServiceProvider.GetRequiredService<BlobReferenceAuditService>().AuditAsync();
            Assert.Equal(audit.TotalBlobs, audit.MatchedReferenceCount);
        }
    }

    // …and the other half of the lifecycle: when the memory goes — with the
    // party it belongs to — its references go back, and a blob nothing else
    // holds is the janitor's again.
    [Fact]
    public async Task Erasing_the_party_releases_the_memorys_references_to_the_ordinary_lifecycle()
    {
        var (_, owner) = await _factory.CreateAuthenticatedClientAsync(OwnerEmail);
        var party = await OpenPartyAsync(owner, guestbook: true);
        var blobId = await BlobOfAsync(party.PhotoId);
        var entry = await SubmitAsync(party.ViewToken, Memory(party.PhotoId, "Ada", "Addio"));
        // Draw the preview, so the memory holds a derived reference too.
        await AssertServesJpegAsync(entry.GetProperty("entry").GetProperty("media").GetProperty("url").GetString()!);
        Guid previewId;
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            previewId = (await db.PartyGuestbookEntries.AsNoTracking().SingleAsync()).PreviewBlobObjectId!.Value;
        }

        // Only the memory holds the original now.
        (await owner.DeleteAsync($"/api/files/{party.PhotoId}")).EnsureSuccessStatusCode();
        (await owner.DeleteAsync($"/api/trash/files/{party.PhotoId}")).EnsureSuccessStatusCode();

        // Deleting the album erases the party, and with it the book.
        (await owner.DeleteAsync($"/api/albums/{party.AlbumId}")).EnsureSuccessStatusCode();

        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            Assert.Equal(0, await db.PartyGuestbookEntries.CountAsync());
            var original = await db.BlobObjects.AsNoTracking().SingleAsync(b => b.Id == blobId);
            Assert.Equal(0, original.ReferenceCount);
            Assert.NotNull(original.PurgeEligibleAt);

            var audit = await scope.ServiceProvider.GetRequiredService<BlobReferenceAuditService>().AuditAsync();
            Assert.Equal(audit.TotalBlobs, audit.MatchedReferenceCount);
        }

        await RunJanitorAsync();
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            Assert.False(await db.BlobObjects.AnyAsync(b => b.Id == blobId));
            Assert.False(await db.BlobObjects.AnyAsync(b => b.Id == previewId));
        }
    }

    // ── A preview's bookkeeping survives the guest leaving ───────────────────
    //
    // Drawing a memory's preview takes a reference (the store) and then decides
    // who keeps it (a compare-and-set on the row). Whatever is released after
    // that is storage bookkeeping, not request work: a guest closing the page
    // at that instant must not cancel the decrement and leave a count that
    // nothing owns. The decorator below cancels the request at exactly that
    // moment and passes on whatever token it was given.

    [Fact]
    public async Task Redrawing_a_lost_preview_keeps_its_count_even_if_the_request_is_cancelled_mid_bookkeeping()
    {
        var (_, owner) = await _factory.CreateAuthenticatedClientAsync(OwnerEmail);
        var party = await OpenPartyAsync(owner, guestbook: true);
        var entry = await SubmitAsync(party.ViewToken, Memory(party.PhotoId, "Ada", "Ridisegnata"));
        var entryId = entry.GetProperty("id").GetGuid();
        await AssertServesJpegAsync(entry.GetProperty("entry").GetProperty("media").GetProperty("url").GetString()!);

        // The derived bytes are lost — a wiped cache — so the next view redraws
        // them and repoints the row, releasing the reference it held before.
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var previewId = (await db.PartyGuestbookEntries.AsNoTracking().SingleAsync()).PreviewBlobObjectId!.Value;
            var key = await db.BlobObjects.Where(b => b.Id == previewId).Select(b => b.StorageKey).SingleAsync();
            await scope.ServiceProvider.GetRequiredService<IBlobStorage>().DeleteAsync(key);
        }

        using var request = new CancellationTokenSource();
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var blobs = new CancelOnReleaseBlobService(scope.ServiceProvider.GetRequiredService<IBlobService>(), request);
            await using var photo = await PhotoCacheWith(scope, blobs).OpenAsync(entryId, request.Token);
            Assert.NotNull(photo);
            Assert.True(blobs.Released > 0, "the redraw released the reference it replaced");
        }

        await AssertPreviewBookkeepingAsync(entryId);
    }

    [Fact]
    public async Task Losing_the_first_drawing_race_gives_its_reference_back_even_if_the_request_is_cancelled()
    {
        var (_, owner) = await _factory.CreateAuthenticatedClientAsync(OwnerEmail);
        var party = await OpenPartyAsync(owner, guestbook: true);
        var entryId = (await SubmitAsync(party.ViewToken, Memory(party.PhotoId, "Ada", "Gara")))
            .GetProperty("id").GetGuid();

        using var request = new CancellationTokenSource();
        Guid lost;
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var blobs = new CancelOnReleaseBlobService(scope.ServiceProvider.GetRequiredService<IBlobService>(), request)
            {
                // Another first view finishes while this one is storing: it
                // points the row at ITS render before our compare-and-set runs.
                AfterStoreDerived = async () =>
                {
                    await using var other = _factory.Services.CreateAsyncScope();
                    var winner = await other.ServiceProvider.GetRequiredService<IBlobService>()
                        .StoreDerivedAsync(new MemoryStream(ImageFixtures.PlainPng(8, 8)));
                    await other.ServiceProvider.GetRequiredService<AppDbContext>().PartyGuestbookEntries
                        .Where(e => e.Id == entryId)
                        .ExecuteUpdateAsync(u => u.SetProperty(e => e.PreviewBlobObjectId, winner.Id));
                },
            };
            await using var photo = await PhotoCacheWith(scope, blobs).OpenAsync(entryId, request.Token);
            // The loser still serves what it drew.
            Assert.NotNull(photo);
            lost = blobs.StoredDerived.Single();
        }

        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            Assert.NotEqual(lost, (await db.PartyGuestbookEntries.AsNoTracking().SingleAsync()).PreviewBlobObjectId);
        }

        // The loser's render is held by exactly its other owners — the album
        // file's own identical thumbnail, when content addressing made them
        // one blob — and by nothing the race left behind.
        await AssertOwnedExactlyAsync(lost);
        await AssertPreviewBookkeepingAsync(entryId);
    }

    private static PartyGuestbookPhotoCacheProbe PhotoCacheWith(AsyncServiceScope scope, IBlobService blobs) =>
        new(new NubArca.Api.Party.PartyGuestbookPhotoCache(
            scope.ServiceProvider.GetRequiredService<AppDbContext>(),
            blobs,
            scope.ServiceProvider.GetRequiredService<NubArca.Api.Files.ImageDerivativeRenderer>(),
            scope.ServiceProvider.GetRequiredService<Microsoft.Extensions.Options.IOptions<NubArca.Api.Files.ImageProcessingOptions>>(),
            scope.ServiceProvider.GetRequiredService<Microsoft.Extensions.Options.IOptions<NubArca.Api.Files.MediaDerivativesOptions>>(),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<NubArca.Api.Party.PartyGuestbookPhotoCache>.Instance));

    /// <summary>
    /// The memory's preview is counted exactly as many times as it is owned,
    /// and so is every other blob in the store.
    /// </summary>
    private async Task AssertPreviewBookkeepingAsync(Guid entryId)
    {
        Guid previewId;
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            previewId = (await db.PartyGuestbookEntries.AsNoTracking().SingleAsync(e => e.Id == entryId))
                .PreviewBlobObjectId!.Value;
        }

        await AssertOwnedExactlyAsync(previewId);
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var audit = await scope.ServiceProvider.GetRequiredService<BlobReferenceAuditService>().AuditAsync();
            Assert.Equal(audit.TotalBlobs, audit.MatchedReferenceCount);
        }
    }

    /// <summary>
    /// A derived blob's count equals its owners: the memories drawn with it and
    /// the album thumbnails that are, byte for byte, the same image.
    /// </summary>
    private async Task AssertOwnedExactlyAsync(Guid blobId)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var owners = await db.PartyGuestbookEntries.CountAsync(e => e.PreviewBlobObjectId == blobId)
            + await db.FileThumbnails.CountAsync(t => t.BlobObjectId == blobId);
        Assert.Equal(
            (long)owners,
            await db.BlobObjects.Where(b => b.Id == blobId).Select(b => b.ReferenceCount).SingleAsync());
    }

    private sealed class PartyGuestbookPhotoCacheProbe(NubArca.Api.Party.PartyGuestbookPhotoCache cache)
    {
        public Task<Stream?> OpenAsync(Guid entryId, CancellationToken cancellationToken) =>
            cache.OpenAsync(entryId, cancellationToken);
    }

    // ── What a memory may be ────────────────────────────────────────────────

    [Fact]
    public async Task A_template_nobody_may_publish_with_is_refused()
    {
        var (_, owner) = await _factory.CreateAuthenticatedClientAsync(OwnerEmail);
        var party = await OpenPartyAsync(owner, guestbook: true);

        foreach (var key in new[] { "canva", "NUBARCA", "", null })
        {
            var refused = await SubmitRawAsync(
                party.ViewToken, Memory(party.PhotoId, "Ada", "Auguri", template: key));
            Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
            Assert.Equal("guestbook_invalid_template", await ErrorOf(refused));
        }

        // Every design the composer offers is accepted, at version 1.
        foreach (var key in PartyGuestbookTemplates.Keys)
        {
            var accepted = await SubmitAsync(party.ViewToken, Memory(party.PhotoId, "Ada", key, template: key));
            Assert.Equal(1, accepted.GetProperty("entry").GetProperty("template").GetProperty("version").GetInt32());
        }
    }

    [Fact]
    public async Task A_framing_outside_the_crop_editors_limits_is_refused()
    {
        var (_, owner) = await _factory.CreateAuthenticatedClientAsync(OwnerEmail);
        var party = await OpenPartyAsync(owner, guestbook: true);

        foreach (var (x, y, zoom) in new[] { (0.5, 0.5, 0.5), (0.5, 0.5, 4.5), (-0.1, 0.5, 1.0), (0.5, 1.2, 1.0) })
        {
            var refused = await SubmitRawAsync(
                party.ViewToken, Memory(party.PhotoId, "Ada", "Auguri", centerX: x, centerY: y, zoom: zoom));
            Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
            Assert.Equal("guestbook_invalid_crop", await ErrorOf(refused));
        }

        var missing = await SubmitRawAsync(party.ViewToken, new
        {
            sourceMediaItemId = party.PhotoId, authorDisplayName = "Ada", body = "Auguri", templateKey = "nubarca",
        });
        Assert.Equal("guestbook_invalid_crop", await ErrorOf(missing));
        Assert.Equal(0, await EntryCountAsync());
    }

    [Fact]
    public async Task A_memory_is_signed()
    {
        var (_, owner) = await _factory.CreateAuthenticatedClientAsync(OwnerEmail);
        var party = await OpenPartyAsync(owner, guestbook: true);

        foreach (var author in new string?[] { null, "", "   ", "​" })
        {
            var refused = await SubmitRawAsync(party.ViewToken, Memory(party.PhotoId, author, "Anonimo"));
            Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
            Assert.Equal("guestbook_invalid_author", await ErrorOf(refused));
        }

        Assert.Equal(0, await EntryCountAsync());
    }

    [Fact]
    public async Task A_blank_dedication_is_not_a_dedication()
    {
        var (_, owner) = await _factory.CreateAuthenticatedClientAsync(OwnerEmail);
        var party = await OpenPartyAsync(owner, guestbook: true);

        foreach (var body in new string?[] { null, "", "   ", "\n\t ​", "\r\n\r\n" })
        {
            var refused = await SubmitRawAsync(party.ViewToken, Memory(party.PhotoId, "Ada", body));
            Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
            Assert.Equal("guestbook_invalid_body", await ErrorOf(refused));
        }

        Assert.Equal(0, (await PublicBookAsync(party.ViewToken)).GetProperty("entries").GetArrayLength());
    }

    [Fact]
    public async Task The_limits_are_measured_on_what_would_be_stored_not_on_what_was_typed()
    {
        var (_, owner) = await _factory.CreateAuthenticatedClientAsync(OwnerEmail);
        var party = await OpenPartyAsync(owner, guestbook: true);

        // Exactly at the limit, buried in whitespace that normalisation
        // removes: accepted, because the limit is about the keepsake and not
        // about how somebody's keyboard behaved.
        var atLimit = new string('a', PartyGuestbookLimits.MaxBodyLength);
        (await SubmitRawAsync(party.ViewToken, Memory(party.PhotoId, "Ada", $"\n\n   {atLimit}   \n")))
            .EnsureSuccessStatusCode();

        var tooLong = await SubmitRawAsync(
            party.ViewToken, Memory(party.PhotoId, "Ada", new string('a', PartyGuestbookLimits.MaxBodyLength + 1)));
        Assert.Equal(HttpStatusCode.BadRequest, tooLong.StatusCode);
        Assert.Equal("guestbook_invalid_body", await ErrorOf(tooLong));

        var longSignature = await SubmitRawAsync(
            party.ViewToken,
            Memory(party.PhotoId, new string('n', PartyGuestbookLimits.MaxAuthorDisplayNameLength + 1), "Auguri"));
        Assert.Equal(HttpStatusCode.BadRequest, longSignature.StatusCode);
        var refusal = await longSignature.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("guestbook_invalid_author", refusal.GetProperty("error").GetString());
        // The refusal states the limits, so a client that has drifted from the
        // shared contract can still say something true.
        Assert.Equal(
            PartyGuestbookLimits.MaxAuthorDisplayNameLength,
            refusal.GetProperty("maxAuthorDisplayNameLength").GetInt32());
        Assert.Equal(PartyGuestbookLimits.MaxBodyLength, refusal.GetProperty("maxBodyLength").GetInt32());

        Assert.Equal(1, (await PublicBookAsync(party.ViewToken)).GetProperty("entries").GetArrayLength());
    }

    // A dedication keeps its PARAGRAPHS. Line endings become one ending, each
    // line is tidied, and a column of blank lines is one paragraph break.
    [Fact]
    public async Task A_dedication_keeps_its_paragraphs_as_plain_text()
    {
        var (_, owner) = await _factory.CreateAuthenticatedClientAsync(OwnerEmail);
        var party = await OpenPartyAsync(owner, guestbook: true);

        await SubmitAsync(party.ViewToken, Memory(party.PhotoId, "  Ada   Lovelace  ", "Riga uno\n\nRiga due"));
        await SubmitAsync(
            party.ViewToken,
            Memory(party.PhotoId, "Ada", "\r\n  Grazie\t\tdi   tutto.  \r\n\r\n\r\n\rA presto ‮così‬\r\n"));

        var entries = (await PublicBookAsync(party.ViewToken)).GetProperty("entries");
        Assert.Equal("Grazie di tutto.\n\nA presto così", entries[0].GetProperty("body").GetString());
        Assert.Equal("Riga uno\n\nRiga due", entries[1].GetProperty("body").GetString());
        // The signature stays on one line.
        Assert.Equal("Ada Lovelace", entries[1].GetProperty("authorDisplayName").GetString());
    }

    [Fact]
    public async Task A_guest_cannot_fill_the_book_faster_than_the_message_limit_allows()
    {
        // The book rides the SAME bucket greetings do — it is the same act,
        // written on a different page.
        using var factory = new SqliteWebApplicationFactory(new Dictionary<string, string?>
        {
            ["RateLimits:PartyMessage:PermitLimit"] = "2",
            ["RateLimits:PartyMessage:WindowSeconds"] = "60",
        });
        factory.EnsureDatabaseCreated();
        var (_, owner) = await factory.CreateAuthenticatedClientAsync(OwnerEmail);
        var party = await OpenPartyAsync(owner, guestbook: true);

        var guest = factory.CreateClient();
        for (var i = 0; i < 2; i++)
        {
            (await guest.PostAsJsonAsync(
                $"/api/party/{party.ViewToken}/guestbook", Memory(party.PhotoId, "Ada", $"Dedica {i}")))
                .EnsureSuccessStatusCode();
        }

        var refused = await guest.PostAsJsonAsync(
            $"/api/party/{party.ViewToken}/guestbook", Memory(party.PhotoId, "Ada", "Una di troppo"));
        Assert.Equal(HttpStatusCode.TooManyRequests, refused.StatusCode);
    }

    // ── Moderation ──────────────────────────────────────────────────────────

    [Fact]
    public async Task With_approval_on_a_memory_waits_and_its_manager_sees_its_picture()
    {
        var (_, owner) = await _factory.CreateAuthenticatedClientAsync(OwnerEmail);
        var party = await OpenPartyAsync(owner, guestbook: true, requireApproval: true);

        var submitted = await SubmitAsync(party.ViewToken, Memory(party.PhotoId, "Marco", "In attesa"));
        Assert.Equal(PartyMessageStatuses.Pending, submitted.GetProperty("status").GetString());
        var entryId = submitted.GetProperty("id").GetGuid();

        // Nowhere in the book, and its picture is nothing to a guest…
        Assert.Equal(0, (await PublicBookAsync(party.ViewToken)).GetProperty("entries").GetArrayLength());
        Assert.Equal(
            HttpStatusCode.NotFound,
            (await _factory.CreateClient().GetAsync(
                $"/api/party/{party.ViewToken}/guestbook/{entryId}/photo")).StatusCode);

        // …and in the host's queue, picture and all — the person deciding has to
        // see what the memory shows.
        var queue = await ManagerListAsync(owner, party.PartyId);
        Assert.True(queue.GetProperty("requireGuestbookApproval").GetBoolean());
        Assert.True(queue.GetProperty("isOwner").GetBoolean());
        var managed = queue.GetProperty("entries")[0];
        Assert.Equal(PartyMessageStatuses.Pending, managed.GetProperty("status").GetString());
        Assert.Equal("nubarca", managed.GetProperty("template").GetProperty("key").GetString());
        var managerUrl = managed.GetProperty("media").GetProperty("url").GetString()!;
        Assert.Equal($"/api/parties/{party.PartyId}/guestbook/{entryId}/photo", managerUrl);
        var picture = await owner.GetAsync(managerUrl);
        Assert.Equal(HttpStatusCode.OK, picture.StatusCode);
        Assert.Equal("image/jpeg", picture.Content.Headers.ContentType?.MediaType);

        Assert.Equal(HttpStatusCode.NoContent, await ModerateAsync(owner, party.PartyId, entryId, "approve"));
        Assert.Equal(1, (await PublicBookAsync(party.ViewToken)).GetProperty("entries").GetArrayLength());
    }

    [Fact]
    public async Task Hiding_takes_a_memory_out_of_the_book_and_restoring_puts_it_back()
    {
        var (_, owner) = await _factory.CreateAuthenticatedClientAsync(OwnerEmail);
        var party = await OpenPartyAsync(owner, guestbook: true);
        var entryId = (await SubmitAsync(party.ViewToken, Memory(party.PhotoId, "Ada", "Evviva")))
            .GetProperty("id").GetGuid();

        Assert.Equal(HttpStatusCode.NoContent, await ModerateAsync(owner, party.PartyId, entryId, "hide"));
        Assert.Equal(0, (await PublicBookAsync(party.ViewToken)).GetProperty("entries").GetArrayLength());
        var hidden = (await ManagerListAsync(owner, party.PartyId)).GetProperty("entries")[0];
        Assert.Equal(PartyMessageStatuses.Hidden, hidden.GetProperty("status").GetString());
        Assert.NotEqual(JsonValueKind.Null, hidden.GetProperty("moderatedAt").ValueKind);

        Assert.Equal(HttpStatusCode.NoContent, await ModerateAsync(owner, party.PartyId, entryId, "restore"));
        Assert.Equal(1, (await PublicBookAsync(party.ViewToken)).GetProperty("entries").GetArrayLength());
    }

    [Fact]
    public async Task A_rejected_memory_stays_out_and_a_refused_transition_is_not_a_missing_entry()
    {
        var (_, owner) = await _factory.CreateAuthenticatedClientAsync(OwnerEmail);
        var party = await OpenPartyAsync(owner, guestbook: true, requireApproval: true);
        var entryId = (await SubmitAsync(party.ViewToken, Memory(party.PhotoId, "Ada", "No")))
            .GetProperty("id").GetGuid();

        Assert.Equal(HttpStatusCode.NoContent, await ModerateAsync(owner, party.PartyId, entryId, "reject"));
        Assert.Equal(0, (await PublicBookAsync(party.ViewToken)).GetProperty("entries").GetArrayLength());
        Assert.Equal(HttpStatusCode.BadRequest, await ModerateAsync(owner, party.PartyId, entryId, "hide"));
    }

    [Fact]
    public async Task Closing_the_book_hides_it_and_keeps_every_memory_for_when_it_re_opens()
    {
        var (_, owner) = await _factory.CreateAuthenticatedClientAsync(OwnerEmail);
        var party = await OpenPartyAsync(owner, guestbook: true);
        await SubmitAsync(party.ViewToken, Memory(party.PhotoId, "Ada", "Da conservare"));

        await SetContributionsAsync(owner, party.AlbumId, new { guestbookEnabled = false });
        Assert.Equal(
            HttpStatusCode.NotFound,
            (await _factory.CreateClient().GetAsync($"/api/party/{party.ViewToken}/guestbook")).StatusCode);

        var queue = await ManagerListAsync(owner, party.PartyId);
        Assert.False(queue.GetProperty("guestbookEnabled").GetBoolean());
        Assert.Equal(1, queue.GetProperty("entries").GetArrayLength());

        await SetContributionsAsync(owner, party.AlbumId, new { guestbookEnabled = true });
        var reopened = await PublicBookAsync(party.ViewToken);
        Assert.Equal("Da conservare", reopened.GetProperty("entries")[0].GetProperty("body").GetString());
    }

    // ── The book's own budget ───────────────────────────────────────────────

    [Fact]
    public async Task A_guest_writes_what_the_host_allowed_and_is_then_refused_with_its_own_code()
    {
        var (_, owner) = await _factory.CreateAuthenticatedClientAsync(OwnerEmail);
        var party = await OpenPartyAsync(owner, guestbook: true);
        await SetGuestbookQuotaAsync(owner, party.AlbumId, 2);
        var before = await ReferenceCountAsync(party.PhotoId);

        // ONE browser, so one budget.
        var guest = _factory.CreateClient();
        for (var i = 1; i <= 2; i++)
        {
            var accepted = await guest.PostAsJsonAsync(
                $"/api/party/{party.ViewToken}/guestbook", Memory(party.PhotoId, "Ada", $"Dedica {i}"));
            accepted.EnsureSuccessStatusCode();
            var body = await accepted.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal(2 - i, body.GetProperty("remaining").GetInt32());
        }

        var refused = await guest.PostAsJsonAsync(
            $"/api/party/{party.ViewToken}/guestbook", Memory(party.PhotoId, "Ada", "Una di troppo"));
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Equal("guestbook_limit_reached", await ErrorOf(refused));

        // Nothing was stored for the refusal, and it took no reference.
        Assert.Equal(2, (await PublicBookAsync(party.ViewToken)).GetProperty("entries").GetArrayLength());
        Assert.Equal(before + 2, await ReferenceCountAsync(party.PhotoId));
    }

    [Fact]
    public async Task Hiding_a_memory_does_not_hand_the_slot_back()
    {
        var (_, owner) = await _factory.CreateAuthenticatedClientAsync(OwnerEmail);
        var party = await OpenPartyAsync(owner, guestbook: true);
        await SetGuestbookQuotaAsync(owner, party.AlbumId, 1);

        var guest = _factory.CreateClient();
        var written = await guest.PostAsJsonAsync(
            $"/api/party/{party.ViewToken}/guestbook", Memory(party.PhotoId, "Ada", "Scritta"));
        written.EnsureSuccessStatusCode();
        var entryId = (await written.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();

        Assert.Equal(HttpStatusCode.NoContent, await ModerateAsync(owner, party.PartyId, entryId, "hide"));

        var again = await guest.PostAsJsonAsync(
            $"/api/party/{party.ViewToken}/guestbook", Memory(party.PhotoId, "Ada", "Ancora"));
        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
        Assert.Equal("guestbook_limit_reached", await ErrorOf(again));
    }

    // ── Two domains ─────────────────────────────────────────────────────────

    // A memory is not a greeting. The television's greeting feed and the
    // host's greeting queue are a different resource, and nothing promotes a
    // memory into it. (Whether a television may one day show the BOOK is a
    // separate question, and nothing here forbids it.)
    [Fact]
    public async Task A_memory_is_not_a_greeting_and_no_route_turns_one_into_the_other()
    {
        var (_, owner) = await _factory.CreateAuthenticatedClientAsync(OwnerEmail);
        var party = await OpenPartyAsync(owner, guestbook: true);
        var tv = await PairTvAsync(owner);

        var entryId = (await SubmitAsync(party.ViewToken, Memory(party.PhotoId, "Ada", "Per il libro")))
            .GetProperty("id").GetGuid();
        await _factory.CreateClient().PostAsJsonAsync(
            $"/api/party/{party.UploadToken}/messages",
            new { displayName = "Giulia", text = "Per la TV" });

        var messages = await TvMessagesAsync(tv, party.AlbumId);
        Assert.Equal(1, messages.GetArrayLength());
        Assert.Equal("Per la TV", messages[0].GetProperty("text").GetString());

        var greetings = await owner.GetFromJsonAsync<JsonElement>($"/api/albums/{party.AlbumId}/party-messages");
        Assert.Equal(1, greetings.GetProperty("items").GetArrayLength());

        foreach (var action in new[] { "promote-hero", "demote-hero" })
        {
            var missing = await owner.PostAsync($"/api/parties/{party.PartyId}/guestbook/{entryId}/{action}", null);
            Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        }
    }

    // ── Whose book it is ────────────────────────────────────────────────────

    [Fact]
    public async Task A_stranger_reads_nothing_sees_no_picture_and_moderates_nothing()
    {
        var (_, owner) = await _factory.CreateAuthenticatedClientAsync(OwnerEmail);
        var (_, stranger) = await _factory.CreateAuthenticatedClientAsync(StrangerEmail);
        var party = await OpenPartyAsync(owner, guestbook: true);
        var entryId = (await SubmitAsync(party.ViewToken, Memory(party.PhotoId, "Ada", "Privata")))
            .GetProperty("id").GetGuid();

        Assert.Equal(
            HttpStatusCode.NotFound,
            (await stranger.GetAsync($"/api/parties/{party.PartyId}/guestbook")).StatusCode);
        Assert.Equal(
            HttpStatusCode.NotFound,
            (await stranger.GetAsync($"/api/parties/{party.PartyId}/guestbook/{entryId}/photo")).StatusCode);
        foreach (var action in new[] { "approve", "reject", "hide", "restore" })
        {
            Assert.Equal(HttpStatusCode.NotFound, await ModerateAsync(stranger, party.PartyId, entryId, action));
        }

        Assert.Equal(
            PartyMessageStatuses.Visible,
            (await ManagerListAsync(owner, party.PartyId)).GetProperty("entries")[0]
                .GetProperty("status").GetString());
    }

    [Fact]
    public async Task An_entry_id_from_another_party_is_the_same_nothing_as_one_that_never_existed()
    {
        var (_, owner) = await _factory.CreateAuthenticatedClientAsync(OwnerEmail);
        var first = await OpenPartyAsync(owner, guestbook: true, albumName: "Prima");
        var second = await OpenPartyAsync(owner, guestbook: true, albumName: "Seconda");
        var entryId = (await SubmitAsync(first.ViewToken, Memory(first.PhotoId, "Ada", "Della prima")))
            .GetProperty("id").GetGuid();

        Assert.Equal(HttpStatusCode.NotFound, await ModerateAsync(owner, second.PartyId, entryId, "hide"));
        // Nor is its picture reachable through the other party's token.
        Assert.Equal(
            HttpStatusCode.NotFound,
            (await _factory.CreateClient().GetAsync(
                $"/api/party/{second.ViewToken}/guestbook/{entryId}/photo")).StatusCode);
        Assert.Equal(
            PartyMessageStatuses.Visible,
            (await ManagerListAsync(owner, first.PartyId)).GetProperty("entries")[0]
                .GetProperty("status").GetString());
    }

    [Fact]
    public async Task The_public_book_carries_no_identity_and_no_storage_detail()
    {
        var (_, owner) = await _factory.CreateAuthenticatedClientAsync(OwnerEmail);
        var party = await OpenPartyAsync(owner, guestbook: true);
        await SubmitAsync(party.ViewToken, Memory(party.PhotoId, "Ada", "Auguri"));
        var blobId = await BlobOfAsync(party.PhotoId);

        // Provenance and plumbing are recorded and never published: no owner,
        // participant, link, party, blob, file or token anywhere in what a
        // guest receives.
        var raw = await _factory.CreateClient().GetStringAsync($"/api/party/{party.ViewToken}/guestbook");
        foreach (var forbidden in new[]
                 {
                     "ownerUserId", "partyId", "partyAlbumLinkId", "partyParticipantId",
                     "tokenHash", "storageKey", "moderatedByUserId", "blobObjectId",
                     "previewBlobObjectId", "sourceMediaItemId", "sha256",
                 })
        {
            Assert.DoesNotContain(forbidden, raw, StringComparison.OrdinalIgnoreCase);
        }
        Assert.DoesNotContain(blobId.ToString(), raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(party.PhotoId.ToString(), raw, StringComparison.OrdinalIgnoreCase);

        var entry = (await PublicBookAsync(party.ViewToken)).GetProperty("entries")[0];
        Assert.Equal(
            ["id", "authorDisplayName", "body", "createdAt", "template", "media"],
            entry.EnumerateObject().Select(p => p.Name).ToArray());
        Assert.Equal(
            ["url", "width", "height", "orientation", "crop"],
            entry.GetProperty("media").EnumerateObject().Select(p => p.Name).ToArray());
    }

    // ── Fixture ─────────────────────────────────────────────────────────────

    // A LANDSCAPE photograph, so a test reads its shape back meaningfully.
    private const int PhotoWidth = 48;
    private const int PhotoHeight = 32;

    private sealed record OpenParty(Guid AlbumId, Guid PartyId, string ViewToken, string UploadToken, Guid PhotoId);

    private static object Memory(
        Guid photoId,
        string? author,
        string? body,
        string? template = "nubarca",
        double centerX = 0.5,
        double centerY = 0.5,
        double zoom = 1.0) => new
        {
            sourceMediaItemId = photoId,
            authorDisplayName = author,
            body,
            templateKey = template,
            crop = new { centerX, centerY, zoom },
        };

    private async Task<OpenParty> OpenPartyAsync(
        HttpClient owner,
        bool guestbook = false,
        bool requireApproval = false,
        string albumName = "Festa")
    {
        var album = await owner.PostAsJsonAsync("/api/albums", new { name = albumName });
        album.EnsureSuccessStatusCode();
        var albumId = (await album.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();

        // One photograph in the album, as a host would have before the party.
        var photoId = await UploadAsync(
            owner, ImageFixtures.PlainPng(PhotoWidth, PhotoHeight), $"{albumName}.png", "image/png");
        await AddToAlbumAsync(owner, albumId, photoId);

        (await owner.PatchAsJsonAsync($"/api/albums/{albumId}/tv-settings", new { showOnTv = true }))
            .EnsureSuccessStatusCode();
        var settings = await owner.PatchAsJsonAsync(
            $"/api/albums/{albumId}/party-settings", new { enabled = true });
        settings.EnsureSuccessStatusCode();
        var status = await settings.Content.ReadFromJsonAsync<JsonElement>();
        await PartyTestHost.StartAsync(owner, status);

        if (guestbook || requireApproval)
        {
            status = await SetContributionsAsync(
                owner, albumId,
                new { guestbookEnabled = guestbook, requireGuestbookApproval = requireApproval });
        }

        var uploadUrl = status.GetProperty("uploadUrl").GetString()!;
        var rest = uploadUrl["/party/".Length..];
        return new OpenParty(
            albumId,
            status.GetProperty("partyId").GetGuid(),
            status.GetProperty("partyUrl").GetString()!["/party/".Length..],
            rest[..rest.IndexOf("/upload", StringComparison.Ordinal)],
            photoId);
    }

    private static async Task<Guid> UploadAsync(HttpClient owner, byte[] bytes, string name, string contentType)
    {
        var part = new ByteArrayContent(bytes);
        part.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(contentType);
        var upload = await owner.PostAsync("/api/files", new MultipartFormDataContent { { part, "file", name } });
        upload.EnsureSuccessStatusCode();
        return (await upload.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
    }

    private static async Task AddToAlbumAsync(HttpClient owner, Guid albumId, Guid fileId) =>
        (await owner.PostAsJsonAsync($"/api/albums/{albumId}/items", new { fileItemId = fileId }))
            .EnsureSuccessStatusCode();

    private static async Task<JsonElement> SetContributionsAsync(
        HttpClient owner, Guid albumId, object body)
    {
        var response = await owner.PatchAsJsonAsync($"/api/albums/{albumId}/party-contributions", body);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static async Task SetGuestbookQuotaAsync(HttpClient owner, Guid albumId, int max) =>
        (await owner.PatchAsJsonAsync(
            $"/api/albums/{albumId}/party-slideshow-settings",
            new { maxGuestbookEntriesPerParticipant = max })).EnsureSuccessStatusCode();

    private Task<JsonElement> GuestContextAsync(string viewToken) =>
        _factory.CreateClient().GetFromJsonAsync<JsonElement>($"/api/party/{viewToken}");

    private Task<JsonElement> PublicBookAsync(string token) =>
        _factory.CreateClient().GetFromJsonAsync<JsonElement>($"/api/party/{token}/guestbook");

    private Task<HttpResponseMessage> SubmitRawAsync(string token, object payload) =>
        _factory.CreateClient().PostAsJsonAsync($"/api/party/{token}/guestbook", payload);

    private async Task<JsonElement> SubmitAsync(string token, object payload)
    {
        var response = await SubmitRawAsync(token, payload);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private async Task AssertServesJpegAsync(string url)
    {
        var response = await _factory.CreateClient().GetAsync(url);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("image/jpeg", response.Content.Headers.ContentType?.MediaType);
        Assert.NotEmpty(await response.Content.ReadAsByteArrayAsync());
    }

    /// <summary>
    /// The janitor, enabled with no grace window, so "nothing holds this blob
    /// any more" is observable inside one test. Built here rather than through
    /// configuration, which would cost the test its pooled host.
    /// </summary>
    private Task<int> RunJanitorAsync() =>
        new BlobJanitor(
            _factory.Services.GetRequiredService<IServiceScopeFactory>(),
            Microsoft.Extensions.Options.Options.Create(new BlobJanitorOptions { Enabled = true, GraceMinutes = 0 }),
            TimeProvider.System,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<BlobJanitor>.Instance)
        .RunOnceAsync(CancellationToken.None);

    private Task<long> ReferenceCountAsync(Guid fileId) => ReferenceCountAsync(_factory, fileId);

    private static async Task<long> ReferenceCountAsync(SqliteWebApplicationFactory factory, Guid fileId)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var blobId = await db.FileItems.IgnoreQueryFilters().AsNoTracking()
            .Where(f => f.Id == fileId).Select(f => f.BlobObjectId).SingleAsync();
        return await db.BlobObjects.AsNoTracking()
            .Where(b => b.Id == blobId).Select(b => b.ReferenceCount).SingleAsync();
    }

    private async Task<Guid> BlobOfAsync(Guid fileId)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.FileItems.IgnoreQueryFilters().AsNoTracking()
            .Where(f => f.Id == fileId).Select(f => f.BlobObjectId).SingleAsync();
    }

    private async Task<int> EntryCountAsync()
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<AppDbContext>().PartyGuestbookEntries.CountAsync();
    }

    /// <summary>Slots claimed by every guest of this party.</summary>
    private async Task<int> GuestbookClaimsAsync(Guid partyId)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var links = db.PartyAlbumLinks.Where(l => l.PartyId == partyId).Select(l => l.Id);
        return await db.PartyParticipants
            .Where(p => links.Contains(p.PartyAlbumLinkId))
            .SumAsync(p => p.SubmittedGuestbookCount);
    }

    private static Task<JsonElement> ManagerListAsync(HttpClient client, Guid partyId) =>
        client.GetFromJsonAsync<JsonElement>($"/api/parties/{partyId}/guestbook");

    private static async Task<HttpStatusCode> ModerateAsync(
        HttpClient client, Guid partyId, Guid entryId, string action) =>
        (await client.PostAsync($"/api/parties/{partyId}/guestbook/{entryId}/{action}", null)).StatusCode;

    private static async Task<string> ErrorOf(HttpResponseMessage response)
    {
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return body.TryGetProperty("error", out var error) ? error.GetString() ?? "" : "";
    }

    private async Task<string> PairTvAsync(HttpClient owner)
    {
        var tvClient = _factory.CreateClient();
        var start = await tvClient.PostAsync("/api/tv/pairing/start", null);
        start.EnsureSuccessStatusCode();
        var started = (await start.Content.ReadFromJsonAsync<NubArca.Api.Tv.TvPairingStartedDto>())!;
        (await owner.PostAsJsonAsync(
            $"/api/tv/pairing/{started.PublicCode}/approve",
            new
            {
                pairingSecret = started.PairingSecret,
                personalCode = "URDLSUDLR",
                personalCodeConfirmation = "URDLSUDLR",
            })).EnsureSuccessStatusCode();
        var poll = new HttpRequestMessage(
            HttpMethod.Get, $"/api/tv/pairing/{started.PublicCode}/status");
        poll.Headers.Add(NubArca.Api.Tv.TvPairingService.PairingSecretHeader, started.PairingSecret);
        var response = await tvClient.SendAsync(poll);
        response.EnsureSuccessStatusCode();
        return response.Headers.GetValues("Set-Cookie").Single();
    }

    private async Task<JsonElement> TvMessagesAsync(string tvCookie, Guid albumId)
    {
        var request = new HttpRequestMessage(
            HttpMethod.Get, $"/api/tv/albums/{albumId}/party-messages");
        var cookie = tvCookie.Split(';', 2)[0];
        request.Headers.Add(
            "Cookie", $"{NubArca.Api.Tv.TvPairingService.CookieName}={cookie[(cookie.IndexOf('=') + 1)..]}");
        var response = await _factory.CreateClient().SendAsync(request);
        response.EnsureSuccessStatusCode();
        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();
        return body.GetProperty("messages");
    }

    /// <summary>
    /// The real blob service, except that the REQUEST is cancelled at the very
    /// moment a reference is released — after the store and the compare-and-set
    /// — and the release then runs with whatever token it was handed. A release
    /// made with the request's token is cancelled with it; one made with
    /// CancellationToken.None is not.
    /// </summary>
    private sealed class CancelOnReleaseBlobService(IBlobService inner, CancellationTokenSource request) : IBlobService
    {
        public Func<Task>? AfterStoreDerived { get; init; }
        public List<Guid> StoredDerived { get; } = [];
        public int Released { get; private set; }

        public async Task ReleaseAsync(Guid blobObjectId, CancellationToken cancellationToken = default)
        {
            await request.CancelAsync();
            await inner.ReleaseAsync(blobObjectId, cancellationToken);
            Released++;
        }

        public async Task<NubArca.Api.Domain.BlobObject> StoreDerivedAsync(Stream content, CancellationToken cancellationToken = default)
        {
            var blob = await inner.StoreDerivedAsync(content, cancellationToken);
            StoredDerived.Add(blob.Id);
            if (AfterStoreDerived is not null) await AfterStoreDerived();
            return blob;
        }

        public Task<NubArca.Api.Domain.BlobObject> AcquireExistingAsync(Guid blobObjectId, CancellationToken cancellationToken = default) =>
            inner.AcquireExistingAsync(blobObjectId, cancellationToken);

        public Task<NubArca.Api.Domain.BlobObject> StoreAsync(Stream content, CancellationToken cancellationToken = default) =>
            inner.StoreAsync(content, cancellationToken);

        public Task<BlobStoreResult> StoreMeasuredAsync(Stream content, CancellationToken cancellationToken = default) =>
            inner.StoreMeasuredAsync(content, cancellationToken);

        public Task<Stream> OpenContentAsync(Guid blobObjectId, CancellationToken cancellationToken = default) =>
            inner.OpenContentAsync(blobObjectId, cancellationToken);

        public Task<Stream?> OpenDerivedContentAsync(Guid blobObjectId, CancellationToken cancellationToken = default) =>
            inner.OpenDerivedContentAsync(blobObjectId, cancellationToken);

        public Task MarkPurgeEligibleIfUnreferencedAsync(Guid blobObjectId, CancellationToken cancellationToken = default) =>
            inner.MarkPurgeEligibleIfUnreferencedAsync(blobObjectId, cancellationToken);

        public Task<bool> TryRestoreDerivedFromOriginalAsync(Guid blobObjectId, CancellationToken cancellationToken = default) =>
            inner.TryRestoreDerivedFromOriginalAsync(blobObjectId, cancellationToken);
    }

    /// <summary>
    /// The real blob service, except that taking a reference to an existing
    /// blob SUCCEEDS and then fails — the moment between the increment and the
    /// commit that the publish's transaction exists to make harmless.
    /// </summary>
    private sealed class FailAfterAcquireBlobService(IBlobService inner) : IBlobService
    {
        public const string Marker = "injected failure after the reference was taken";

        public async Task<NubArca.Api.Domain.BlobObject> AcquireExistingAsync(
            Guid blobObjectId, CancellationToken cancellationToken = default)
        {
            await inner.AcquireExistingAsync(blobObjectId, cancellationToken);
            throw new InvalidOperationException(Marker);
        }

        public Task<NubArca.Api.Domain.BlobObject> StoreAsync(Stream content, CancellationToken cancellationToken = default) =>
            inner.StoreAsync(content, cancellationToken);

        public Task<BlobStoreResult> StoreMeasuredAsync(Stream content, CancellationToken cancellationToken = default) =>
            inner.StoreMeasuredAsync(content, cancellationToken);

        public Task<Stream> OpenContentAsync(Guid blobObjectId, CancellationToken cancellationToken = default) =>
            inner.OpenContentAsync(blobObjectId, cancellationToken);

        public Task<NubArca.Api.Domain.BlobObject> StoreDerivedAsync(Stream content, CancellationToken cancellationToken = default) =>
            inner.StoreDerivedAsync(content, cancellationToken);

        public Task<Stream?> OpenDerivedContentAsync(Guid blobObjectId, CancellationToken cancellationToken = default) =>
            inner.OpenDerivedContentAsync(blobObjectId, cancellationToken);

        public Task ReleaseAsync(Guid blobObjectId, CancellationToken cancellationToken = default) =>
            inner.ReleaseAsync(blobObjectId, cancellationToken);

        public Task MarkPurgeEligibleIfUnreferencedAsync(Guid blobObjectId, CancellationToken cancellationToken = default) =>
            inner.MarkPurgeEligibleIfUnreferencedAsync(blobObjectId, cancellationToken);

        public Task<bool> TryRestoreDerivedFromOriginalAsync(Guid blobObjectId, CancellationToken cancellationToken = default) =>
            inner.TryRestoreDerivedFromOriginalAsync(blobObjectId, cancellationToken);
    }
}
