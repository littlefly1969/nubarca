using NubArca.Api.Files;
using Xunit;
using static NubArca.Api.Tests.Media.MediaSemanticTestHarness;

namespace NubArca.Api.Tests.Media;

// VSEM-03: the privacy boundary of unified semantic search. Candidates are
// owner-visible FileItems BEFORE any vector ranking, so nothing here relies on
// post-filtering: a foreign, vaulted, deleted or excluded file's embedding must
// be structurally unreachable, however well it scores.
public sealed class MediaSemanticPrivacyTests
{
    [Fact]
    public async Task Owner_A_Cannot_Retrieve_Owner_B_Files()
    {
        using var factory = Factory();
        var profile = await SeedProfileAsync(factory);
        var (ownerA, _) = await factory.CreateAuthenticatedClientAsync();
        var (ownerB, _) = await factory.CreateAuthenticatedClientAsync("owner-b@example.com");
        var q = QueryVector(profile);

        var (aPhoto, aPhotoBlob) = await UploadPhotoAsync(factory, ownerA, 40);
        await SeedPhotoEmbeddingAsync(factory, profile, aPhotoBlob, WithSimilarity(q, 0.5));
        var (bPhoto, bPhotoBlob) = await UploadPhotoAsync(factory, ownerB, 80);
        await SeedPhotoEmbeddingAsync(factory, profile, bPhotoBlob, WithSimilarity(q, 0.99));
        var (bVideo, bVideoBlob) = await UploadVideoAsync(factory, ownerB);
        await SeedVideoManifestAsync(factory, profile, bVideoBlob, q,
            [new SeedSample(0, 8000, 4000, 0.98)]);

        var page = await SearchAsync(factory, ownerA);

        var item = Assert.Single(page.Items);
        Assert.Equal(aPhoto, item.Media.Id);
        Assert.DoesNotContain(page.Items, i => i.Media.Id == bPhoto || i.Media.Id == bVideo);
    }

    [Fact]
    public async Task A_Shared_Canonical_Blob_Does_Not_Cross_Owner_Boundaries()
    {
        using var factory = Factory();
        var profile = await SeedProfileAsync(factory);
        var (ownerA, _) = await factory.CreateAuthenticatedClientAsync();
        var (ownerB, _) = await factory.CreateAuthenticatedClientAsync("owner-b@example.com");
        var q = QueryVector(profile);

        // Identical bytes → ONE canonical blob referenced by BOTH owners; the
        // blob-level embedding is shared by construction.
        var bytes = NubArca.Api.Tests.Metadata.ImageFixtures.MinimalMp4();
        var (aFile, blobId) = await UploadVideoAsync(factory, ownerA, bytes, "a.mp4");
        var (bFile, bBlob) = await UploadVideoAsync(factory, ownerB, bytes, "b.mp4");
        Assert.Equal(blobId, bBlob);
        await SeedVideoManifestAsync(factory, profile, blobId, q,
            [new SeedSample(0, 8000, 4000, 0.9)]);

        var aPage = await SearchAsync(factory, ownerA);
        var bPage = await SearchAsync(factory, ownerB);

        Assert.Equal(aFile, Assert.Single(aPage.Items).Media.Id);
        Assert.Equal(bFile, Assert.Single(bPage.Items).Media.Id);
    }

    [Fact]
    public async Task A_Normal_Reference_Does_Not_Expose_The_Vault_Reference_On_The_Same_Blob()
    {
        using var factory = Factory();
        var profile = await SeedProfileAsync(factory);
        var (owner, _) = await factory.CreateAuthenticatedClientAsync();
        var q = QueryVector(profile);

        var bytes = NubArca.Api.Tests.Metadata.ImageFixtures.MinimalMp4();
        var (normalFile, blobId) = await UploadVideoAsync(factory, owner, bytes, "normal.mp4");
        var (vaultedFile, _) = await UploadVideoAsync(factory, owner, bytes, "secret.mp4");
        await MoveToVaultAsync(factory, owner, vaultedFile);
        await SeedVideoManifestAsync(factory, profile, blobId, q,
            [new SeedSample(0, 8000, 4000, 0.9)]);

        var page = await SearchAsync(factory, owner);

        // The blob is searchable through the normal reference ONLY.
        var item = Assert.Single(page.Items);
        Assert.Equal(normalFile, item.Media.Id);
    }

    [Fact]
    public async Task Vault_Only_References_Never_Appear()
    {
        using var factory = Factory();
        var profile = await SeedProfileAsync(factory);
        var (owner, _) = await factory.CreateAuthenticatedClientAsync();
        var q = QueryVector(profile);

        var (photoId, photoBlob) = await UploadPhotoAsync(factory, owner, 40);
        await SeedPhotoEmbeddingAsync(factory, profile, photoBlob, WithSimilarity(q, 0.99));
        await MoveToVaultAsync(factory, owner, photoId);
        var (videoId, videoBlob) = await UploadVideoAsync(factory, owner);
        await SeedVideoManifestAsync(factory, profile, videoBlob, q,
            [new SeedSample(0, 8000, 4000, 0.98)]);
        await MoveToVaultAsync(factory, owner, videoId);

        var page = await SearchAsync(factory, owner);

        Assert.Empty(page.Items);
    }

    [Fact]
    public async Task Deleted_And_Excluded_Records_Never_Appear()
    {
        using var factory = Factory();
        var profile = await SeedProfileAsync(factory);
        var (owner, _) = await factory.CreateAuthenticatedClientAsync();
        var q = QueryVector(profile);

        var (deletedId, deletedBlob) = await UploadPhotoAsync(factory, owner, 40);
        await SeedPhotoEmbeddingAsync(factory, profile, deletedBlob, WithSimilarity(q, 0.99));
        await SoftDeleteAsync(factory, deletedId);

        var (excludedId, excludedBlob) = await UploadVideoAsync(factory, owner);
        await SeedVideoManifestAsync(factory, profile, excludedBlob, q,
            [new SeedSample(0, 8000, 4000, 0.98)]);
        await ExcludeFromLibraryAsync(factory, excludedId);

        var (visibleId, visibleBlob) = await UploadPhotoAsync(factory, owner, 80);
        await SeedPhotoEmbeddingAsync(factory, profile, visibleBlob, WithSimilarity(q, 0.4));

        var page = await SearchAsync(factory, owner);

        var item = Assert.Single(page.Items);
        Assert.Equal(visibleId, item.Media.Id);
    }

    [Fact]
    public async Task Cursor_Reuse_Under_Another_Owner_Fails_Safely()
    {
        using var factory = Factory();
        var profile = await SeedProfileAsync(factory);
        var (ownerA, _) = await factory.CreateAuthenticatedClientAsync();
        var (ownerB, _) = await factory.CreateAuthenticatedClientAsync("owner-b@example.com");
        var q = QueryVector(profile);

        var aIds = new List<Guid>();
        for (var i = 0; i < 3; i++)
        {
            var (id, blob) = await UploadPhotoAsync(factory, ownerA, (byte)(40 + i * 20));
            await SeedPhotoEmbeddingAsync(factory, profile, blob, WithSimilarity(q, 0.9 - i * 0.1));
            aIds.Add(id);
        }
        var (bId, bBlob) = await UploadPhotoAsync(factory, ownerB, 90);
        await SeedPhotoEmbeddingAsync(factory, profile, bBlob, WithSimilarity(q, 0.5));

        var aFirst = await SearchAsync(factory, ownerA, limit: 1);
        Assert.NotNull(aFirst.NextCursor);

        // Owner B replaying A's cursor is re-scoped to B's OWN candidates on
        // every request: it can never leak an A result. (It either pages B's
        // results or comes back empty — here B has one weaker item that lies
        // beyond A's boundary score.)
        var bPage = await SearchAsync(factory, ownerB, limit: 10, cursor: aFirst.NextCursor);
        Assert.All(bPage.Items, i => Assert.Equal(bId, i.Media.Id));
        Assert.DoesNotContain(bPage.Items, i => aIds.Contains(i.Media.Id));
    }
}
