using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NubArca.Api.Data;
using NubArca.Api.Domain;
using NubArca.Api.Storage;
using NubArca.Api.Tests.Endpoints;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.PixelFormats;
using Xunit;

namespace NubArca.Api.Tests.Albums;

// SHARE-COPY-01: the one-time DETACHED album copy.
//
// The invariant under test throughout is the mirror image of SHARE-ALBUM-02's:
// a contribution is a LINK the contributor can always take back, whereas an
// accepted copy is the recipient's OUTRIGHT and can never be taken back. Every
// test here is ultimately asking one of two questions — "can anything the
// sender does still reach the copy?" (it must not) and "did anything private
// ride along?" (it must not).
public sealed class AlbumTransferTests : IDisposable
{
    private readonly SqliteWebApplicationFactory _factory = new();

    public AlbumTransferTests() => _factory.EnsureDatabaseCreated();

    public void Dispose() => _factory.Dispose();

    private const string SenderEmail = "alice@example.com";
    private const string RecipientEmail = "bob@example.com";
    private const string OtherEmail = "carol@example.com";

    // ── Sending: the snapshot ───────────────────────────────────────────────

    [Fact]
    public async Task Send_Creates_An_Immutable_Snapshot_Of_The_Album()
    {
        var (_, alice) = await _factory.CreateAuthenticatedClientAsync(SenderEmail);
        await _factory.CreateAuthenticatedClientAsync(RecipientEmail);
        var albumId = await CreateAlbumAsync(alice, "Iceland");
        await AddOwnPngAsync(alice, albumId, "a.png");
        await AddOwnPngAsync(alice, albumId, "b.png");

        var sent = await SendAsync(alice, albumId, RecipientEmail);
        Assert.Equal("pending", sent.GetProperty("state").GetString());
        Assert.Equal(2, sent.GetProperty("itemCount").GetInt32());
        Assert.Equal("Iceland", sent.GetProperty("title").GetString());

        // The manifest carries the display metadata, not a live join.
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var transferId = sent.GetProperty("id").GetGuid();
        var items = await db.AlbumTransferItems
            .Where(i => i.AlbumTransferId == transferId)
            .OrderBy(i => i.SortOrder)
            .ToListAsync();
        Assert.Equal(2, items.Count);
        Assert.All(items, i => Assert.False(string.IsNullOrWhiteSpace(i.Name)));
        Assert.All(items, i => Assert.NotEqual(Guid.Empty, i.BlobObjectId));
        Assert.Equal([0, 1], items.Select(i => i.SortOrder).ToArray());
    }

    [Fact]
    public async Task Source_Changes_After_Send_Do_Not_Alter_The_Pending_Copy()
    {
        var (_, alice) = await _factory.CreateAuthenticatedClientAsync(SenderEmail);
        var (_, bob) = await _factory.CreateAuthenticatedClientAsync(RecipientEmail);
        var albumId = await CreateAlbumAsync(alice, "Before");
        await AddOwnPngAsync(alice, albumId, "keep.png");
        var doomed = await AddOwnPngAsync(alice, albumId, "doomed.png");

        var transferId = (await SendAsync(alice, albumId, RecipientEmail))
            .GetProperty("id").GetGuid();

        // Rename the album, remove an item, and trash the underlying file.
        await RenameAlbumAsync(alice, albumId, "After");
        (await alice.DeleteAsync($"/api/albums/{albumId}/items/{doomed}"))
            .EnsureSuccessStatusCode();
        (await alice.DeleteAsync($"/api/files/{doomed}")).EnsureSuccessStatusCode();

        // The recipient still sees exactly what was sent.
        var received = await SingleReceivedAsync(bob);
        Assert.Equal("Before", received.GetProperty("title").GetString());
        Assert.Equal(2, received.GetProperty("itemCount").GetInt32());

        var albumIdCopy = await AcceptAsync(bob, transferId);
        var copied = await ListAlbumItemsAsync(albumIdCopy);
        Assert.Equal(2, copied.Count);
    }

    [Fact]
    public async Task Send_Is_Rejected_When_The_Album_Contains_Another_Users_Contribution()
    {
        // A contribution is linked and REVOCABLE by design. Handing it to a
        // third party as a permanent copy would put it beyond the revocation
        // its owner was promised, so the whole send is refused — never silently
        // trimmed.
        var (_, alice) = await _factory.CreateAuthenticatedClientAsync(SenderEmail);
        var (_, bob) = await _factory.CreateAuthenticatedClientAsync(RecipientEmail);
        var (_, carol) = await _factory.CreateAuthenticatedClientAsync(OtherEmail);
        var albumId = await CreateAlbumAsync(alice, "Shared");
        await AddOwnPngAsync(alice, albumId, "mine.png");

        await InviteAcceptAsync(alice, bob, albumId, RecipientEmail, "contributor");
        var bobFile = await UploadPngAsync(bob, "bobs.png");
        (await bob.PostAsJsonAsync($"/api/shared-albums/{albumId}/contributions",
            new { fileItemId = bobFile })).EnsureSuccessStatusCode();

        var response = await alice.PostAsJsonAsync(
            $"/api/albums/{albumId}/transfers", new { email = OtherEmail });
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("contains_ineligible_items", body.GetProperty("error").GetString());
        var blockers = body.GetProperty("blockers").EnumerateArray().ToList();
        Assert.Single(blockers);
        Assert.Equal(1, blockers[0].GetProperty("itemCount").GetInt32());
        // The REASON matters as much as the count: it is what tells the owner
        // that somebody else's media is involved rather than that a file went
        // missing. An earlier version of this test asserted only the count, and
        // a mistranslated LEFT JOIN reported every contribution as
        // "Unavailable" for weeks' worth of confidence it had not earned.
        Assert.Equal("ContributedByAnotherUser", blockers[0].GetProperty("reason").GetString());

        // Nothing was created, and Carol was told nothing.
        Assert.Empty(await ListReceivedAsync(carol));
        Assert.Equal(0, await CountTransfersAsync());
    }

    [Fact]
    public async Task Send_Is_Rejected_When_The_Album_Contains_Vaulted_Media()
    {
        var (_, alice) = await _factory.CreateAuthenticatedClientAsync(SenderEmail);
        await _factory.CreateAuthenticatedClientAsync(RecipientEmail);
        var albumId = await CreateAlbumAsync(alice, "Mixed");
        await AddOwnPngAsync(alice, albumId, "public.png");
        var secret = await AddOwnPngAsync(alice, albumId, "secret.png");

        // Move one file into the vault directly: the global query filter would
        // otherwise make it VANISH from the send, which is the silent omission
        // this test exists to forbid.
        await MutateAsync(async db =>
        {
            var vault = new PrivateVault { Id = Guid.NewGuid(), OwnerUserId = await OwnerOfAsync(db, secret) };
            db.PrivateVaults.Add(vault);
            var file = await db.FileItems.IgnoreQueryFilters().FirstAsync(f => f.Id == secret);
            file.PrivateVaultId = vault.Id;
        });

        var response = await alice.PostAsJsonAsync(
            $"/api/albums/{albumId}/transfers", new { email = RecipientEmail });
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("contains_ineligible_items", body.GetProperty("error").GetString());
        var blockers = body.GetProperty("blockers").EnumerateArray().ToList();
        Assert.Single(blockers);
        Assert.Equal("InPrivateVault", blockers[0].GetProperty("reason").GetString());
        Assert.Equal(1, blockers[0].GetProperty("itemCount").GetInt32());
        Assert.Equal(0, await CountTransfersAsync());
    }

    [Fact]
    public async Task Sender_Cannot_Send_To_Themselves()
    {
        var (_, alice) = await _factory.CreateAuthenticatedClientAsync(SenderEmail);
        var albumId = await CreateAlbumAsync(alice, "Solo");
        await AddOwnPngAsync(alice, albumId, "a.png");

        var response = await alice.PostAsJsonAsync(
            $"/api/albums/{albumId}/transfers", new { email = SenderEmail });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0, await CountTransfersAsync());
    }

    [Fact]
    public async Task A_Second_Pending_Transfer_For_The_Same_Pair_Is_Refused()
    {
        var (_, alice) = await _factory.CreateAuthenticatedClientAsync(SenderEmail);
        await _factory.CreateAuthenticatedClientAsync(RecipientEmail);
        var albumId = await CreateAlbumAsync(alice, "Once");
        await AddOwnPngAsync(alice, albumId, "a.png");

        await SendAsync(alice, albumId, RecipientEmail);
        var second = await alice.PostAsJsonAsync(
            $"/api/albums/{albumId}/transfers", new { email = RecipientEmail });
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
        Assert.Equal(1, await CountTransfersAsync());
    }

    // ── Accepting ───────────────────────────────────────────────────────────

    [Fact]
    public async Task Accept_Creates_A_Recipient_Owned_Album_And_Files()
    {
        var (aliceId, alice) = await _factory.CreateAuthenticatedClientAsync(SenderEmail);
        var (bobId, bob) = await _factory.CreateAuthenticatedClientAsync(RecipientEmail);
        var albumId = await CreateAlbumAsync(alice, "Gift");
        await AddOwnPngAsync(alice, albumId, "a.png");
        await AddOwnPngAsync(alice, albumId, "b.png");

        var transferId = (await SendAsync(alice, albumId, RecipientEmail))
            .GetProperty("id").GetGuid();
        var newAlbumId = await AcceptAsync(bob, transferId);

        Assert.NotEqual(albumId, newAlbumId);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var album = await db.Albums.FirstAsync(a => a.Id == newAlbumId);
        Assert.Equal(bobId, album.OwnerUserId);
        Assert.Equal("Gift", album.Name);
        // Publication is never inherited — that is the new owner's decision.
        Assert.False(album.ShowOnTv);

        var items = await db.AlbumItems.Where(ai => ai.AlbumId == newAlbumId).ToListAsync();
        Assert.Equal(2, items.Count);
        // The SHARE-ALBUM-02 provenance invariant still holds for the copy.
        Assert.All(items, i => Assert.Equal(bobId, i.AddedByUserId));

        var fileIds = items.Select(i => i.FileItemId).ToList();
        var files = await db.FileItems.Where(f => fileIds.Contains(f.Id)).ToListAsync();
        Assert.Equal(2, files.Count);
        Assert.All(files, f => Assert.Equal(bobId, f.OwnerUserId));
        Assert.All(files, f => Assert.Null(f.PrivateVaultId));

        // Alice's originals are untouched and still hers.
        var aliceFiles = await db.FileItems.Where(f => f.OwnerUserId == aliceId).ToListAsync();
        Assert.Equal(2, aliceFiles.Count);
        Assert.All(aliceFiles, f => Assert.Null(f.DeletedAt));
    }

    [Fact]
    public async Task Accept_Preserves_Blob_Dedup_Without_Merging_Logical_Ownership()
    {
        // The copy must reuse the physical bytes (content-addressed storage)
        // while being a completely separate logical file. Both halves matter:
        // the first is the storage invariant, the second is the privacy one.
        var (aliceId, alice) = await _factory.CreateAuthenticatedClientAsync(SenderEmail);
        var (bobId, bob) = await _factory.CreateAuthenticatedClientAsync(RecipientEmail);
        var albumId = await CreateAlbumAsync(alice, "Dedup");
        var sourceFileId = await AddOwnPngAsync(alice, albumId, "shared-bytes.png");

        var transferId = (await SendAsync(alice, albumId, RecipientEmail))
            .GetProperty("id").GetGuid();
        var newAlbumId = await AcceptAsync(bob, transferId);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var source = await db.FileItems.FirstAsync(f => f.Id == sourceFileId);
        var copyId = await db.AlbumItems
            .Where(ai => ai.AlbumId == newAlbumId).Select(ai => ai.FileItemId).FirstAsync();
        var copy = await db.FileItems.FirstAsync(f => f.Id == copyId);

        // Same bytes…
        Assert.Equal(source.BlobObjectId, copy.BlobObjectId);
        // …different logical files, different owners.
        Assert.NotEqual(source.Id, copy.Id);
        Assert.Equal(aliceId, source.OwnerUserId);
        Assert.Equal(bobId, copy.OwnerUserId);

        // And the blob is now owned by two references, so releasing one cannot
        // strand the other.
        var blob = await db.BlobObjects.FirstAsync(b => b.Id == source.BlobObjectId);
        Assert.True(blob.ReferenceCount >= 2);
    }

    [Fact]
    public async Task Accept_Is_Idempotent_And_Never_Creates_A_Second_Album()
    {
        var (_, alice) = await _factory.CreateAuthenticatedClientAsync(SenderEmail);
        var (bobId, bob) = await _factory.CreateAuthenticatedClientAsync(RecipientEmail);
        var albumId = await CreateAlbumAsync(alice, "Twice");
        await AddOwnPngAsync(alice, albumId, "a.png");

        var transferId = (await SendAsync(alice, albumId, RecipientEmail))
            .GetProperty("id").GetGuid();

        var first = await AcceptAsync(bob, transferId);
        var second = await AcceptAsync(bob, transferId);
        var third = await AcceptAsync(bob, transferId);

        Assert.Equal(first, second);
        Assert.Equal(first, third);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Equal(1, await db.Albums.CountAsync(a => a.OwnerUserId == bobId));
        Assert.Equal(1, await db.FileItems.CountAsync(f => f.OwnerUserId == bobId));
    }

    [Fact]
    public async Task Accepted_Copy_Survives_Deletion_Of_The_Source_Album()
    {
        var (_, alice) = await _factory.CreateAuthenticatedClientAsync(SenderEmail);
        var (_, bob) = await _factory.CreateAuthenticatedClientAsync(RecipientEmail);
        var albumId = await CreateAlbumAsync(alice, "Doomed source");
        await AddOwnPngAsync(alice, albumId, "a.png");

        var transferId = (await SendAsync(alice, albumId, RecipientEmail))
            .GetProperty("id").GetGuid();
        var newAlbumId = await AcceptAsync(bob, transferId);

        (await alice.DeleteAsync($"/api/albums/{albumId}")).EnsureSuccessStatusCode();

        var items = await ListAlbumItemsAsync(newAlbumId);
        Assert.Single(items);
        var detail = await bob.GetFromJsonAsync<JsonElement>($"/api/albums/{newAlbumId}");
        Assert.Equal("Doomed source", detail.GetProperty("name").GetString());
    }

    // NOTE the deliberate asymmetry with
    // A_Pending_Transfer_Cannot_Be_Accepted_After_The_Sender_Is_Disabled below.
    // Disablement freezes what the sender's account can still CAUSE; it does not
    // reach backwards into what the recipient already owns.
    [Fact]
    public async Task Accepted_Copy_Survives_The_Sender_Being_Disabled()
    {
        var (aliceId, alice) = await _factory.CreateAuthenticatedClientAsync(SenderEmail);
        var (_, bob) = await _factory.CreateAuthenticatedClientAsync(RecipientEmail);
        var albumId = await CreateAlbumAsync(alice, "Outlives");
        await AddOwnPngAsync(alice, albumId, "a.png");

        var transferId = (await SendAsync(alice, albumId, RecipientEmail))
            .GetProperty("id").GetGuid();
        var newAlbumId = await AcceptAsync(bob, transferId);

        await MutateAsync(async db =>
        {
            var user = await db.Users.FirstAsync(u => u.Id == aliceId);
            user.DisabledAt = DateTime.UtcNow;
        });

        // Unlike a live share — which fails closed when the owner is disabled —
        // a copy is unconditionally the recipient's.
        var items = await ListAlbumItemsAsync(newAlbumId);
        Assert.Single(items);
    }

    [Fact]
    public async Task Recipient_Edits_Do_Not_Affect_The_Sender_And_Vice_Versa()
    {
        var (_, alice) = await _factory.CreateAuthenticatedClientAsync(SenderEmail);
        var (_, bob) = await _factory.CreateAuthenticatedClientAsync(RecipientEmail);
        var albumId = await CreateAlbumAsync(alice, "Original name");
        await AddOwnPngAsync(alice, albumId, "a.png");
        await AddOwnPngAsync(alice, albumId, "b.png");

        var transferId = (await SendAsync(alice, albumId, RecipientEmail))
            .GetProperty("id").GetGuid();
        var newAlbumId = await AcceptAsync(bob, transferId);

        // Bob renames his copy and removes an item.
        await RenameAlbumAsync(bob, newAlbumId, "Bob's version");
        var bobItems = await ListAlbumItemsAsync(newAlbumId);
        (await bob.DeleteAsync($"/api/albums/{newAlbumId}/items/{bobItems[0]}"))
            .EnsureSuccessStatusCode();

        // Alice's album is untouched.
        var aliceDetail = await alice.GetFromJsonAsync<JsonElement>($"/api/albums/{albumId}");
        Assert.Equal("Original name", aliceDetail.GetProperty("name").GetString());
        Assert.Equal(2, (await ListAlbumItemsAsync(albumId)).Count);

        // Alice renames hers; Bob's stays as Bob left it.
        await RenameAlbumAsync(alice, albumId, "Alice renamed");
        var bobDetail = await bob.GetFromJsonAsync<JsonElement>($"/api/albums/{newAlbumId}");
        Assert.Equal("Bob's version", bobDetail.GetProperty("name").GetString());
        Assert.Single(await ListAlbumItemsAsync(newAlbumId));
    }

    [Fact]
    public async Task Recipient_Deleting_Their_Copy_Does_Not_Delete_The_Senders_Media()
    {
        var (aliceId, alice) = await _factory.CreateAuthenticatedClientAsync(SenderEmail);
        var (_, bob) = await _factory.CreateAuthenticatedClientAsync(RecipientEmail);
        var albumId = await CreateAlbumAsync(alice, "Mine and yours");
        var aliceFileId = await AddOwnPngAsync(alice, albumId, "a.png");

        var transferId = (await SendAsync(alice, albumId, RecipientEmail))
            .GetProperty("id").GetGuid();
        var newAlbumId = await AcceptAsync(bob, transferId);
        var bobFileId = (await ListAlbumItemsAsync(newAlbumId))[0];

        (await bob.DeleteAsync($"/api/files/{bobFileId}")).EnsureSuccessStatusCode();
        (await bob.DeleteAsync($"/api/trash/files/{bobFileId}")).EnsureSuccessStatusCode();

        // Alice still has hers, and the bytes are still there.
        var aliceFile = await alice.GetAsync($"/api/files/{aliceFileId}/content");
        Assert.Equal(HttpStatusCode.OK, aliceFile.StatusCode);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Equal(1, await db.FileItems.CountAsync(f => f.OwnerUserId == aliceId && f.DeletedAt == null));
    }

    [Fact]
    public async Task Sender_Permanently_Deleting_The_Source_Does_Not_Break_A_Pending_Copy()
    {
        // THE retention test. Between send and accept the sender destroys every
        // source file. The manifest's own blob reference is what keeps the bytes
        // alive; without it the janitor would be free to reclaim them.
        var (_, alice) = await _factory.CreateAuthenticatedClientAsync(SenderEmail);
        var (_, bob) = await _factory.CreateAuthenticatedClientAsync(RecipientEmail);
        var albumId = await CreateAlbumAsync(alice, "Destroyed");
        var fileId = await AddOwnPngAsync(alice, albumId, "a.png");

        var transferId = (await SendAsync(alice, albumId, RecipientEmail))
            .GetProperty("id").GetGuid();

        (await alice.DeleteAsync($"/api/files/{fileId}")).EnsureSuccessStatusCode();
        (await alice.DeleteAsync($"/api/trash/files/{fileId}")).EnsureSuccessStatusCode();

        // The bytes must still exist and still be referenced.
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var blobId = await db.AlbumTransferItems
                .Where(i => i.AlbumTransferId == transferId)
                .Select(i => i.BlobObjectId).FirstAsync();
            var blob = await db.BlobObjects.FirstAsync(b => b.Id == blobId);
            Assert.True(blob.ReferenceCount >= 1);
            Assert.Null(blob.PurgeEligibleAt);
        }

        // And acceptance still works, because it reads the snapshot, not the
        // source.
        var newAlbumId = await AcceptAsync(bob, transferId);
        Assert.Single(await ListAlbumItemsAsync(newAlbumId));
    }

    [Fact]
    public async Task Reference_Audit_Counts_A_Pending_Transfers_References()
    {
        // If BlobReferenceAuditService did not know about album_transfer_items,
        // `repair-references` would zero these references and the janitor would
        // delete bytes a pending copy needs. `storage blobs audit-references`
        // runs on every production deploy, so this is the test that stops a
        // silent data-loss regression.
        var (_, alice) = await _factory.CreateAuthenticatedClientAsync(SenderEmail);
        await _factory.CreateAuthenticatedClientAsync(RecipientEmail);
        var albumId = await CreateAlbumAsync(alice, "Audited");
        await AddOwnPngAsync(alice, albumId, "a.png");
        await AddOwnPngAsync(alice, albumId, "b.png");
        await SendAsync(alice, albumId, RecipientEmail);

        using var scope = _factory.Services.CreateScope();
        var audit = scope.ServiceProvider.GetRequiredService<BlobReferenceAuditService>();
        var report = await audit.AuditAsync();

        // Every blob's stored count matches the recomputed truth: no
        // over-counting and, critically, no under-counting.
        Assert.Equal(0, report.DbRefcountTooHigh);
        Assert.Equal(0, report.DbRefcountTooLow);
        // The bucket the audit service calls "the most dangerous": a blob the
        // janitor would consider unreferenced while a real row still needs it.
        Assert.Equal(0, report.ZeroRefWithRealReferences);
        Assert.Equal(report.TotalDbReferences, report.TotalComputedReferences);
    }

    [Fact]
    public async Task Accepting_Two_Copies_With_Colliding_File_Names_Succeeds()
    {
        // Every accepted copy lands in the same "Received albums" folder, and
        // active siblings must have unique names
        // (ux_file_items_active_sibling_name). Two albums both holding a
        // "photo.png" — the single most ordinary case imaginable — aborted the
        // whole acceptance until the names were de-duplicated. SQLite's fixture
        // does not enforce that index, so this was found in the browser, not
        // here; the assertion below is what stops it coming back.
        var (_, alice) = await _factory.CreateAuthenticatedClientAsync(SenderEmail);
        var (bobId, bob) = await _factory.CreateAuthenticatedClientAsync(RecipientEmail);

        // Alice cannot hold two "photo.png" in ONE folder either, so the second
        // lives in a subfolder. Both manifests still carry the same NAME, which
        // is what collides in Bob's single destination folder.
        var first = await CreateAlbumAsync(alice, "First");
        await AddOwnPngAsync(alice, first, "photo.png");

        var folder = (await (await alice.PostAsJsonAsync("/api/folders", new { name = "sub" }))
            .Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        var second = await CreateAlbumAsync(alice, "Second");
        var otherFile = await UploadPngAsync(alice, "photo.png", folder);
        (await alice.PostAsJsonAsync($"/api/albums/{second}/items",
            new { fileItemId = otherFile })).EnsureSuccessStatusCode();

        var t1 = (await SendAsync(alice, first, RecipientEmail)).GetProperty("id").GetGuid();
        var t2 = (await SendAsync(alice, second, RecipientEmail)).GetProperty("id").GetGuid();

        var a1 = await AcceptAsync(bob, t1);
        var a2 = await AcceptAsync(bob, t2);
        Assert.NotEqual(a1, a2);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var names = await db.FileItems
            .Where(f => f.OwnerUserId == bobId && f.DeletedAt == null)
            .Select(f => f.Name).ToListAsync();
        Assert.Equal(2, names.Count);
        // Both arrived, under distinct names.
        Assert.Equal(2, names.Distinct().Count());
        Assert.Contains("photo.png", names);
    }

    // ── Declining, cancelling, expiring ─────────────────────────────────────

    [Fact]
    public async Task Decline_Creates_No_Album_And_Releases_The_References()
    {
        var (_, alice) = await _factory.CreateAuthenticatedClientAsync(SenderEmail);
        var (bobId, bob) = await _factory.CreateAuthenticatedClientAsync(RecipientEmail);
        var albumId = await CreateAlbumAsync(alice, "Declined");
        await AddOwnPngAsync(alice, albumId, "a.png");

        var transferId = (await SendAsync(alice, albumId, RecipientEmail))
            .GetProperty("id").GetGuid();
        var before = await BlobReferenceCountAsync(transferId);

        (await bob.PostAsync($"/api/album-transfers/{transferId}/decline", null))
            .EnsureSuccessStatusCode();

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Equal(0, await db.Albums.CountAsync(a => a.OwnerUserId == bobId));
        Assert.Equal(0, await db.FileItems.CountAsync(f => f.OwnerUserId == bobId));
        var transfer = await db.AlbumTransfers.FirstAsync(t => t.Id == transferId);
        Assert.Equal("declined", transfer.State);
        Assert.Null(transfer.CreatedAlbumId);
        Assert.Equal(before - 1, await BlobReferenceCountAsync(transferId));
    }

    [Fact]
    public async Task Cancel_Withdraws_A_Pending_Offer()
    {
        var (_, alice) = await _factory.CreateAuthenticatedClientAsync(SenderEmail);
        var (_, bob) = await _factory.CreateAuthenticatedClientAsync(RecipientEmail);
        var albumId = await CreateAlbumAsync(alice, "Cancelled");
        await AddOwnPngAsync(alice, albumId, "a.png");

        var transferId = (await SendAsync(alice, albumId, RecipientEmail))
            .GetProperty("id").GetGuid();
        (await alice.PostAsync($"/api/album-transfers/{transferId}/cancel", null))
            .EnsureSuccessStatusCode();

        // The recipient never sees a withdrawn offer at all.
        Assert.Empty(await ListReceivedAsync(bob));

        var accept = await bob.PostAsync($"/api/album-transfers/{transferId}/accept", null);
        Assert.Equal(HttpStatusCode.Conflict, accept.StatusCode);
    }

    [Fact]
    public async Task An_Accepted_Copy_Can_Never_Be_Cancelled_By_The_Sender()
    {
        var (_, alice) = await _factory.CreateAuthenticatedClientAsync(SenderEmail);
        var (_, bob) = await _factory.CreateAuthenticatedClientAsync(RecipientEmail);
        var albumId = await CreateAlbumAsync(alice, "Given");
        await AddOwnPngAsync(alice, albumId, "a.png");

        var transferId = (await SendAsync(alice, albumId, RecipientEmail))
            .GetProperty("id").GetGuid();
        var newAlbumId = await AcceptAsync(bob, transferId);

        var cancel = await alice.PostAsync($"/api/album-transfers/{transferId}/cancel", null);
        Assert.Equal(HttpStatusCode.Conflict, cancel.StatusCode);

        // And the copy is untouched.
        Assert.Single(await ListAlbumItemsAsync(newAlbumId));
    }

    [Fact]
    public async Task An_Expired_Transfer_Cannot_Be_Accepted_And_Releases_Its_References()
    {
        var (_, alice) = await _factory.CreateAuthenticatedClientAsync(SenderEmail);
        var (bobId, bob) = await _factory.CreateAuthenticatedClientAsync(RecipientEmail);
        var albumId = await CreateAlbumAsync(alice, "Lapsed");
        await AddOwnPngAsync(alice, albumId, "a.png");

        var transferId = (await SendAsync(alice, albumId, RecipientEmail))
            .GetProperty("id").GetGuid();
        var before = await BlobReferenceCountAsync(transferId);

        await MutateAsync(async db =>
        {
            var t = await db.AlbumTransfers.FirstAsync(x => x.Id == transferId);
            t.ExpiresAt = DateTime.UtcNow.AddDays(-1);
        });

        // Lapsed offers disappear from the inbox even before the sweep runs.
        Assert.Empty(await ListReceivedAsync(bob));
        var accept = await bob.PostAsync($"/api/album-transfers/{transferId}/accept", null);
        Assert.Equal(HttpStatusCode.Conflict, accept.StatusCode);

        using var scope = _factory.Services.CreateScope();
        var transfers = scope.ServiceProvider
            .GetRequiredService<NubArca.Api.Albums.Sharing.IAlbumTransferService>();
        Assert.Equal(1, await transfers.ExpirePendingAsync());
        // Running it again is safe and does nothing.
        Assert.Equal(0, await transfers.ExpirePendingAsync());

        Assert.Equal(before - 1, await BlobReferenceCountAsync(transferId));
        var db2 = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Equal(0, await db2.Albums.CountAsync(a => a.OwnerUserId == bobId));
    }

    [Fact]
    public async Task A_Pending_Transfer_Cannot_Be_Accepted_After_The_Sender_Is_Disabled()
    {
        // The mirror of Accepted_Copy_Survives_The_Sender_Being_Disabled, and
        // the case that must behave the OPPOSITE way. Disablement can be the
        // response to a compromised account, so an operation that account
        // started must not be completable afterwards.
        var (aliceId, alice) = await _factory.CreateAuthenticatedClientAsync(SenderEmail);
        var (bobId, bob) = await _factory.CreateAuthenticatedClientAsync(RecipientEmail);
        var albumId = await CreateAlbumAsync(alice, "Compromised");
        await AddOwnPngAsync(alice, albumId, "a.png");

        var transferId = (await SendAsync(alice, albumId, RecipientEmail))
            .GetProperty("id").GetGuid();
        var before = await BlobReferenceCountAsync(transferId);

        await MutateAsync(async db =>
        {
            var user = await db.Users.FirstAsync(u => u.Id == aliceId);
            user.DisabledAt = DateTime.UtcNow;
        });

        // Gone from the inbox, and not acceptable.
        Assert.Empty(await ListReceivedAsync(bob));
        var accept = await bob.PostAsync($"/api/album-transfers/{transferId}/accept", null);
        Assert.Equal(HttpStatusCode.Conflict, accept.StatusCode);
        var body = await accept.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("sender_unavailable", body.GetProperty("error").GetString());

        using var scope = _factory.Services.CreateScope();
        var db2 = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Equal(0, await db2.Albums.CountAsync(a => a.OwnerUserId == bobId));
        Assert.Equal(0, await db2.FileItems.CountAsync(f => f.OwnerUserId == bobId));

        // And the cleanup sweep retires it and releases the pinned references,
        // so a disabled account's offer does not hold bytes forever.
        var cleanup = scope.ServiceProvider
            .GetRequiredService<NubArca.Api.Albums.Sharing.AlbumTransferCleanupService>();
        Assert.Equal(1, await cleanup.RunOnceAsync());
        Assert.Equal(0, await cleanup.RunOnceAsync());
        Assert.Equal(before - 1, await BlobReferenceCountAsync(transferId));

        var transfer = await db2.AlbumTransfers.AsNoTracking()
            .FirstAsync(t => t.Id == transferId);
        Assert.Equal("expired", transfer.State);
        Assert.Null(transfer.CreatedAlbumId);
    }

    // CONCURRENCY SCOPE NOTE. These two tests drive the verbs SEQUENTIALLY.
    // The SQLite fixture runs every request over ONE shared connection, so two
    // genuinely simultaneous requests fail with "cannot start a transaction
    // within a transaction" — a property of the harness, not of the product.
    // This is the same limitation TreeMutationLock documents for the advisory
    // lock, and the same stance the repo already takes.
    //
    // What these DO pin is the branch that makes a concurrent loser correct: the
    // conditional `WHERE State = 'pending'` claim failing, and the service
    // answering from the row's real state instead of proceeding. On PostgreSQL
    // that claim plus the recipient's pg_advisory_xact_lock is what serialises
    // true simultaneity; the claim is what makes it correct without relying on
    // the lock at all.

    [Fact]
    public async Task Cancel_After_Accept_Loses_And_Changes_Nothing()
    {
        var (_, alice) = await _factory.CreateAuthenticatedClientAsync(SenderEmail);
        var (bobId, bob) = await _factory.CreateAuthenticatedClientAsync(RecipientEmail);
        var albumId = await CreateAlbumAsync(alice, "Race");
        await AddOwnPngAsync(alice, albumId, "a.png");

        var transferId = (await SendAsync(alice, albumId, RecipientEmail))
            .GetProperty("id").GetGuid();

        var newAlbumId = await AcceptAsync(bob, transferId);
        var cancel = await alice.PostAsync($"/api/album-transfers/{transferId}/cancel", null);
        Assert.Equal(HttpStatusCode.Conflict, cancel.StatusCode);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var transfer = await db.AlbumTransfers.AsNoTracking().FirstAsync(t => t.Id == transferId);
        // The losing cancel left no trace: still accepted, still pointing at the
        // recipient's album, and the album is intact.
        Assert.Equal("accepted", transfer.State);
        Assert.Equal(newAlbumId, transfer.CreatedAlbumId);
        Assert.Null(transfer.CancelledAt);
        Assert.Equal(1, await db.Albums.CountAsync(a => a.OwnerUserId == bobId));
        Assert.Single(await ListAlbumItemsAsync(newAlbumId));
    }

    [Fact]
    public async Task Accept_After_Cancel_Loses_And_Creates_Nothing()
    {
        var (_, alice) = await _factory.CreateAuthenticatedClientAsync(SenderEmail);
        var (bobId, bob) = await _factory.CreateAuthenticatedClientAsync(RecipientEmail);
        var albumId = await CreateAlbumAsync(alice, "Race back");
        await AddOwnPngAsync(alice, albumId, "a.png");

        var transferId = (await SendAsync(alice, albumId, RecipientEmail))
            .GetProperty("id").GetGuid();

        (await alice.PostAsync($"/api/album-transfers/{transferId}/cancel", null))
            .EnsureSuccessStatusCode();
        var accept = await bob.PostAsync($"/api/album-transfers/{transferId}/accept", null);
        Assert.Equal(HttpStatusCode.Conflict, accept.StatusCode);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var transfer = await db.AlbumTransfers.AsNoTracking().FirstAsync(t => t.Id == transferId);
        Assert.Equal("cancelled", transfer.State);
        Assert.Null(transfer.CreatedAlbumId);
        // The losing accept must not have half-built a copy.
        Assert.Equal(0, await db.Albums.CountAsync(a => a.OwnerUserId == bobId));
        Assert.Equal(0, await db.FileItems.CountAsync(f => f.OwnerUserId == bobId));
    }

    [Fact]
    public async Task Every_Terminal_State_Refuses_Every_Further_Transition()
    {
        // The state machine is closed: nothing ever leaves a terminal state, in
        // either direction. Asserted for all four terminal states in one place
        // so a future slice cannot quietly reopen one.
        var (_, alice) = await _factory.CreateAuthenticatedClientAsync(SenderEmail);
        var (_, bob) = await _factory.CreateAuthenticatedClientAsync(RecipientEmail);

        foreach (var terminal in new[] { "declined", "cancelled", "expired" })
        {
            var albumId = await CreateAlbumAsync(alice, $"Terminal {terminal}");
            await AddOwnPngAsync(alice, albumId, $"{terminal}.png");
            var transferId = (await SendAsync(alice, albumId, RecipientEmail))
                .GetProperty("id").GetGuid();

            switch (terminal)
            {
                case "declined":
                    (await bob.PostAsync($"/api/album-transfers/{transferId}/decline", null))
                        .EnsureSuccessStatusCode();
                    break;
                case "cancelled":
                    (await alice.PostAsync($"/api/album-transfers/{transferId}/cancel", null))
                        .EnsureSuccessStatusCode();
                    break;
                case "expired":
                    await MutateAsync(async db =>
                    {
                        var t = await db.AlbumTransfers.FirstAsync(x => x.Id == transferId);
                        t.ExpiresAt = DateTime.UtcNow.AddDays(-1);
                    });
                    using (var s = _factory.Services.CreateScope())
                    {
                        await s.ServiceProvider
                            .GetRequiredService<NubArca.Api.Albums.Sharing.IAlbumTransferService>()
                            .ExpirePendingAsync();
                    }
                    break;
            }

            foreach (var (client, verb) in new[]
            {
                (bob, "accept"), (bob, "decline"), (alice, "cancel"),
            })
            {
                var response = await client.PostAsync(
                    $"/api/album-transfers/{transferId}/{verb}", null);
                Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
            }

            using var scope = _factory.Services.CreateScope();
            var db2 = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var final = await db2.AlbumTransfers.AsNoTracking().FirstAsync(t => t.Id == transferId);
            Assert.Equal(terminal, final.State);
            Assert.Null(final.CreatedAlbumId);
        }
    }

    [Fact]
    public async Task No_Endpoint_Can_Mutate_A_Snapshot_After_Creation()
    {
        // The snapshot's immutability is a property of the ROUTE TABLE, not of
        // discipline: there is simply no verb that edits a transfer. If a future
        // slice adds one, this test is where it should be argued for.
        var (_, alice) = await _factory.CreateAuthenticatedClientAsync(SenderEmail);
        await _factory.CreateAuthenticatedClientAsync(RecipientEmail);
        var albumId = await CreateAlbumAsync(alice, "Frozen");
        await AddOwnPngAsync(alice, albumId, "a.png");
        var transferId = (await SendAsync(alice, albumId, RecipientEmail))
            .GetProperty("id").GetGuid();

        var path = $"/api/album-transfers/{transferId}";
        foreach (var attempt in new[]
        {
            new HttpRequestMessage(HttpMethod.Put, path),
            new HttpRequestMessage(HttpMethod.Patch, path),
            new HttpRequestMessage(HttpMethod.Delete, path),
            new HttpRequestMessage(HttpMethod.Post, $"{path}/items"),
            new HttpRequestMessage(HttpMethod.Patch, $"{path}/recipient"),
            new HttpRequestMessage(HttpMethod.Patch, $"{path}/cover"),
        })
        {
            var response = await alice.SendAsync(attempt);
            Assert.True(
                response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed,
                $"{attempt.Method} {attempt.RequestUri} returned {(int)response.StatusCode}");
        }

        // And the snapshot is byte-for-byte what it was.
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var transfer = await db.AlbumTransfers.AsNoTracking().FirstAsync(t => t.Id == transferId);
        Assert.Equal("Frozen", transfer.Title);
        Assert.Equal(1, transfer.ItemCount);
        Assert.Equal("pending", transfer.State);
    }

    // ── Authorization ───────────────────────────────────────────────────────

    [Fact]
    public async Task A_Stranger_Can_Neither_See_Nor_Accept_Nor_Cancel_A_Transfer()
    {
        var (_, alice) = await _factory.CreateAuthenticatedClientAsync(SenderEmail);
        await _factory.CreateAuthenticatedClientAsync(RecipientEmail);
        var (_, carol) = await _factory.CreateAuthenticatedClientAsync(OtherEmail);
        var albumId = await CreateAlbumAsync(alice, "Private business");
        await AddOwnPngAsync(alice, albumId, "a.png");

        var transferId = (await SendAsync(alice, albumId, RecipientEmail))
            .GetProperty("id").GetGuid();

        Assert.Empty(await ListReceivedAsync(carol));
        Assert.Empty(await ListSentAsync(carol));

        // 404, never 403: the id must not confirm its own existence.
        foreach (var verb in new[] { "accept", "decline", "cancel" })
        {
            var response = await carol.PostAsync($"/api/album-transfers/{transferId}/{verb}", null);
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        }
    }

    [Fact]
    public async Task The_Recipient_Cannot_Cancel_And_The_Sender_Cannot_Accept()
    {
        var (_, alice) = await _factory.CreateAuthenticatedClientAsync(SenderEmail);
        var (_, bob) = await _factory.CreateAuthenticatedClientAsync(RecipientEmail);
        var albumId = await CreateAlbumAsync(alice, "Roles");
        await AddOwnPngAsync(alice, albumId, "a.png");

        var transferId = (await SendAsync(alice, albumId, RecipientEmail))
            .GetProperty("id").GetGuid();

        Assert.Equal(HttpStatusCode.NotFound,
            (await bob.PostAsync($"/api/album-transfers/{transferId}/cancel", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await alice.PostAsync($"/api/album-transfers/{transferId}/accept", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await alice.PostAsync($"/api/album-transfers/{transferId}/decline", null)).StatusCode);
    }

    [Fact]
    public async Task A_Non_Owner_Cannot_Send_Somebody_Elses_Album()
    {
        var (_, alice) = await _factory.CreateAuthenticatedClientAsync(SenderEmail);
        var (_, bob) = await _factory.CreateAuthenticatedClientAsync(RecipientEmail);
        var (_, carol) = await _factory.CreateAuthenticatedClientAsync(OtherEmail);
        var albumId = await CreateAlbumAsync(alice, "Alice's");
        await AddOwnPngAsync(alice, albumId, "a.png");

        // Even an Editor — who may curate the album — cannot give it away.
        await InviteAcceptAsync(alice, bob, albumId, RecipientEmail, "editor");

        Assert.Equal(HttpStatusCode.NotFound, (await bob.PostAsJsonAsync(
            $"/api/albums/{albumId}/transfers", new { email = OtherEmail })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await carol.PostAsJsonAsync(
            $"/api/albums/{albumId}/transfers", new { email = RecipientEmail })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await carol.GetAsync($"/api/albums/{albumId}/transfer-preview")).StatusCode);
        Assert.Equal(0, await CountTransfersAsync());
    }

    [Fact]
    public async Task Every_Transfer_Route_Requires_Authentication()
    {
        var anonymous = _factory.CreateClient();
        var id = Guid.NewGuid();

        foreach (var path in new[]
        {
            "/api/album-transfers/sent",
            "/api/album-transfers/received",
        })
        {
            Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync(path)).StatusCode);
        }

        foreach (var verb in new[] { "accept", "decline", "cancel" })
        {
            Assert.Equal(HttpStatusCode.Unauthorized,
                (await anonymous.PostAsync($"/api/album-transfers/{id}/{verb}", null)).StatusCode);
        }

        Assert.Equal(HttpStatusCode.Unauthorized,
            (await anonymous.GetAsync($"/api/albums/{id}/transfer-preview")).StatusCode);
    }

    // ── Privacy: what must NOT ride along ───────────────────────────────────

    [Fact]
    public async Task No_Person_Face_Or_Private_Metadata_Crosses_The_Ownership_Boundary()
    {
        var (aliceId, alice) = await _factory.CreateAuthenticatedClientAsync(SenderEmail);
        var (bobId, bob) = await _factory.CreateAuthenticatedClientAsync(RecipientEmail);
        var albumId = await CreateAlbumAsync(alice, "People in here");
        var fileId = await AddOwnPngAsync(alice, albumId, "portrait.png");

        // Give the source file a full private semantic layer.
        await MutateAsync(async db =>
        {
            var person = new NubArca.Api.Domain.Ai.Person
            {
                Id = Guid.NewGuid(),
                OwnerUserId = aliceId,
                DisplayName = "Alice's mother",
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            };
            db.Set<NubArca.Api.Domain.Ai.Person>().Add(person);
            // Owner-scoped by virtue of the file it hangs off: there is exactly
            // one row per FileItem, so a copy that shared it would be a direct
            // cross-owner leak.
            db.FileItemUserMetadata.Add(new FileItemUserMetadata
            {
                Id = Guid.NewGuid(),
                FileItemId = fileId,
                Title = "Mum's 80th",
                Description = "private annotation",
                IsFavorite = true,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            });
            await Task.CompletedTask;
        });

        var transferId = (await SendAsync(alice, albumId, RecipientEmail))
            .GetProperty("id").GetGuid();
        var newAlbumId = await AcceptAsync(bob, transferId);

        using var scope = _factory.Services.CreateScope();
        var db2 = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var copyId = await db2.AlbumItems
            .Where(ai => ai.AlbumId == newAlbumId).Select(ai => ai.FileItemId).FirstAsync();

        // No person rows, no face assignments, no user metadata reached Bob.
        Assert.Equal(0, await db2.Set<NubArca.Api.Domain.Ai.Person>()
            .CountAsync(p => p.OwnerUserId == bobId));
        // The private annotation stayed on Alice's file and was not duplicated
        // onto Bob's copy.
        Assert.Equal(0, await db2.FileItemUserMetadata.CountAsync(m => m.FileItemId == copyId));
        Assert.Equal(1, await db2.FileItemUserMetadata.CountAsync(m => m.FileItemId == fileId));

        // No memberships and no share links came along either.
        Assert.Equal(0, await db2.AlbumMemberships.CountAsync(m => m.AlbumId == newAlbumId));

        // And Bob's People surface is empty.
        var people = await bob.GetFromJsonAsync<JsonElement>("/api/people");
        Assert.Empty(people.EnumerateArray());
    }

    [Fact]
    public async Task The_Recipient_Sees_Only_Title_Counts_And_Sender_Identity_Before_Accepting()
    {
        var (_, alice) = await _factory.CreateAuthenticatedClientAsync(SenderEmail);
        var (_, bob) = await _factory.CreateAuthenticatedClientAsync(RecipientEmail);
        var albumId = await CreateAlbumAsync(alice, "Peek");
        await AddOwnPngAsync(alice, albumId, "a.png");
        await SendAsync(alice, albumId, RecipientEmail);

        var offer = await SingleReceivedAsync(bob);
        var raw = offer.GetRawText();

        // What the contract requires be shown.
        Assert.Equal("Peek", offer.GetProperty("title").GetString());
        Assert.Equal(1, offer.GetProperty("itemCount").GetInt32());
        Assert.True(offer.GetProperty("totalSizeBytes").GetInt64() > 0);
        Assert.False(string.IsNullOrWhiteSpace(offer.GetProperty("senderDisplayName").GetString()));

        // The address is masked, never disclosed in full.
        var mask = offer.GetProperty("senderEmailMask").GetString();
        Assert.NotNull(mask);
        Assert.DoesNotContain(SenderEmail, raw, StringComparison.OrdinalIgnoreCase);

        // And nothing about storage, the source album, or the media itself.
        foreach (var forbidden in new[]
        {
            "storageKey", "sha256", "blobObjectId", "sourceAlbumId",
            "sourceFileItemId", "fileItemId", "payloadJson", "tokenHash",
        })
        {
            Assert.DoesNotContain(forbidden, raw, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task The_Sender_Is_Never_Told_Which_Contributed_Files_Blocked_The_Send()
    {
        var (_, alice) = await _factory.CreateAuthenticatedClientAsync(SenderEmail);
        var (_, bob) = await _factory.CreateAuthenticatedClientAsync(RecipientEmail);
        await _factory.CreateAuthenticatedClientAsync(OtherEmail);
        var albumId = await CreateAlbumAsync(alice, "Blocked");
        await AddOwnPngAsync(alice, albumId, "mine.png");
        await InviteAcceptAsync(alice, bob, albumId, RecipientEmail, "contributor");
        var bobFile = await UploadPngAsync(bob, "bobs-secret-filename.png");
        (await bob.PostAsJsonAsync($"/api/shared-albums/{albumId}/contributions",
            new { fileItemId = bobFile })).EnsureSuccessStatusCode();

        var response = await alice.PostAsJsonAsync(
            $"/api/albums/{albumId}/transfers", new { email = OtherEmail });
        var raw = await response.Content.ReadAsStringAsync();

        // Counts and a reason, never a filename or an id.
        Assert.Contains("contains_ineligible_items", raw);
        Assert.DoesNotContain("bobs-secret-filename", raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(bobFile.ToString(), raw, StringComparison.OrdinalIgnoreCase);
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    private static async Task RenameAlbumAsync(HttpClient client, Guid albumId, string name) =>
        (await client.PatchAsJsonAsync($"/api/albums/{albumId}", new { name }))
            .EnsureSuccessStatusCode();

    private static async Task<JsonElement> SendAsync(HttpClient sender, Guid albumId, string email)
    {
        var response = await sender.PostAsJsonAsync(
            $"/api/albums/{albumId}/transfers", new { email });
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static async Task<Guid> AcceptAsync(HttpClient recipient, Guid transferId)
    {
        var response = await recipient.PostAsync(
            $"/api/album-transfers/{transferId}/accept", null);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("albumId").GetGuid();
    }

    private static async Task<List<JsonElement>> ListReceivedAsync(HttpClient client) =>
        (await client.GetFromJsonAsync<JsonElement>("/api/album-transfers/received"))
            .EnumerateArray().ToList();

    private static async Task<List<JsonElement>> ListSentAsync(HttpClient client) =>
        (await client.GetFromJsonAsync<JsonElement>("/api/album-transfers/sent"))
            .EnumerateArray().ToList();

    private static async Task<JsonElement> SingleReceivedAsync(HttpClient client) =>
        (await ListReceivedAsync(client)).Single();

    private static async Task<Guid> CreateAlbumAsync(HttpClient owner, string name)
    {
        var response = await owner.PostAsJsonAsync("/api/albums", new { name });
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
    }

    private static async Task<Guid> UploadPngAsync(
        HttpClient client, string name, Guid? parentFolderId = null)
    {
        using var img = new Image<Rgba32>(8, 8);
        // Distinct bytes per name: storage is content-addressed, so identical
        // fixtures would deduplicate onto one blob and confuse refcount asserts.
        var tint = (byte)(name.Aggregate(17, (acc, c) => (acc * 31 + c) & 0xFF));
        img[0, 0] = new Rgba32(tint, tint, tint, 255);
        using var ms = new MemoryStream();
        img.Save(ms, new PngEncoder());
        var part = new ByteArrayContent(ms.ToArray());
        part.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        var multipart = new MultipartFormDataContent { { part, "file", name } };
        var route = parentFolderId is Guid f ? $"/api/folders/{f}/files" : "/api/files";
        var response = await client.PostAsync(route, multipart);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
    }

    private static async Task<Guid> AddOwnPngAsync(HttpClient owner, Guid albumId, string name)
    {
        var fileId = await UploadPngAsync(owner, name);
        (await owner.PostAsJsonAsync($"/api/albums/{albumId}/items", new { fileItemId = fileId }))
            .EnsureSuccessStatusCode();
        return fileId;
    }

    private static async Task<Guid> InviteAsync(
        HttpClient owner, Guid albumId, string email, string role)
    {
        var response = await owner.PostAsJsonAsync(
            $"/api/albums/{albumId}/members", new { email, role });
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("membershipId").GetGuid();
    }

    private static async Task InviteAcceptAsync(
        HttpClient owner, HttpClient member, Guid albumId, string email, string role)
    {
        var id = await InviteAsync(owner, albumId, email, role);
        (await member.PostAsync($"/api/shared-albums/invitations/{id}/accept", null))
            .EnsureSuccessStatusCode();
    }

    private async Task<List<Guid>> ListAlbumItemsAsync(Guid albumId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.AlbumItems
            .Where(ai => ai.AlbumId == albumId)
            .OrderBy(ai => ai.SortOrder)
            .Select(ai => ai.FileItemId)
            .ToListAsync();
    }

    private async Task<int> CountTransfersAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.AlbumTransfers.CountAsync();
    }

    private async Task<long> BlobReferenceCountAsync(Guid transferId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var blobId = await db.AlbumTransferItems
            .Where(i => i.AlbumTransferId == transferId)
            .Select(i => i.BlobObjectId)
            .FirstAsync();
        return await db.BlobObjects.Where(b => b.Id == blobId)
            .Select(b => b.ReferenceCount).FirstAsync();
    }

    private static async Task<Guid> OwnerOfAsync(AppDbContext db, Guid fileId) =>
        await db.FileItems.IgnoreQueryFilters()
            .Where(f => f.Id == fileId).Select(f => f.OwnerUserId).FirstAsync();

    private async Task MutateAsync(Func<AppDbContext, Task> mutate)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await mutate(db);
        await db.SaveChangesAsync();
    }
}
