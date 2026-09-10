using Microsoft.EntityFrameworkCore;
using Npgsql;
using NubArca.Api.Data;
using NubArca.Api.Domain;
using NubArca.Api.Tests.Integration;
using NubArca.Api.Tv;
using Xunit;

namespace NubArca.Api.Tests.Party;

/// <summary>
/// The display grant's schema, on real PostgreSQL.
///
/// Two things here cannot be proved on SQLite. The unique index on the token
/// digest has to reject a duplicate with a real 23505, and the foreign keys
/// have to behave the way production's do — including the one that decides
/// whether a party can still be torn down once televisions have been showing
/// it. That last one is the shape of bug #116, and this is where it would come
/// back.
/// </summary>
[Collection(PostgresIntegrationCollection.Name)]
[Trait("Category", "External")]
public sealed class PartyDisplayGrantPostgresTests : IAsyncLifetime
{
    private readonly PostgresContainerFixture _fixture;
    private DbContextOptions<AppDbContext>? _dbOptions;

    private readonly Guid _ownerId = Guid.NewGuid();
    private readonly Guid _partyId = Guid.NewGuid();
    private readonly Guid _albumId = Guid.NewGuid();
    private readonly Guid _linkId = Guid.NewGuid();
    private readonly Guid _tvSessionId = Guid.NewGuid();

    public PartyDisplayGrantPostgresTests(PostgresContainerFixture fixture) => _fixture = fixture;

    public async Task InitializeAsync()
    {
        if (!_fixture.Available) return;
        _dbOptions = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(_fixture.ConnectionString!).Options;
        // The migration runs on the fixture's boot, so reaching this line at
        // all is the migration applying cleanly to a real database.
        await _fixture.ResetDatabaseAsync();

        await using var db = NewContext();
        db.Users.Add(new User
        {
            Id = _ownerId, Email = $"owner-{_ownerId:N}@example.com",
            DisplayName = "Owner", CreatedAt = DateTime.UtcNow,
        });
        db.Albums.Add(new Album
        {
            Id = _albumId, OwnerUserId = _ownerId, Name = "Festa",
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        });
        db.Parties.Add(new NubArca.Api.Domain.Party
        {
            Id = _partyId, OwnerUserId = _ownerId, Title = "Festa",
            Status = PartyStatuses.Live,
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        });
        db.PartyAlbumLinks.Add(new PartyAlbumLink
        {
            Id = _linkId, PartyId = _partyId, OwnerUserId = _ownerId, AlbumId = _albumId,
            TokenHash = new string('a', 64), Enabled = true, GameEnabled = true,
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        });
        db.TvSessions.Add(new TvSession
        {
            Id = _tvSessionId, OwnerUserId = _ownerId, SessionTokenHash = new string('c', 64),
            CreatedAt = DateTime.UtcNow, LastSeenAt = DateTime.UtcNow,
            ExpiresAt = DateTime.UtcNow.AddDays(30),
            DisplayAssignment = TvDisplayAssignments.Party,
            AssignedPartyAlbumLinkId = _linkId,
        });
        await db.SaveChangesAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [SkippableFact]
    public async Task Two_grants_can_never_share_a_token_digest()
    {
        Skip.IfNot(_fixture.Available, "Docker is not available; integration test skipped.");
        var hash = new string('d', 64);
        await using (var db = NewContext())
        {
            db.PartyDisplayGrants.Add(Grant(hash));
            await db.SaveChangesAsync();
        }

        await using var second = NewContext();
        second.PartyDisplayGrants.Add(Grant(hash));
        // The uniqueness is the DATABASE's, not a check somebody remembered to
        // write before inserting.
        var error = await Assert.ThrowsAsync<DbUpdateException>(() => second.SaveChangesAsync());
        Assert.Equal("23505", Assert.IsType<PostgresException>(error.InnerException).SqlState);
    }

    [SkippableFact]
    public async Task A_grant_dies_with_its_television_and_holds_up_no_party_teardown()
    {
        Skip.IfNot(_fixture.Available, "Docker is not available; integration test skipped.");
        await using (var db = NewContext())
        {
            db.PartyDisplayGrants.Add(Grant(new string('e', 64)));
            await db.SaveChangesAsync();
        }

        // Deleting the television takes its grants with it: the key to the
        // session cascades, because a grant for a device that no longer exists
        // is not worth keeping.
        await using (var db = NewContext())
        {
            await db.TvSessions.Where(x => x.Id == _tvSessionId).ExecuteDeleteAsync();
            Assert.Empty(await db.PartyDisplayGrants.ToListAsync());
        }
    }

    [SkippableFact]
    public async Task Tearing_down_the_party_removes_its_grants_rather_than_failing_on_them()
    {
        Skip.IfNot(_fixture.Available, "Docker is not available; integration test skipped.");
        await using (var db = NewContext())
        {
            db.PartyDisplayGrants.Add(Grant(new string('f', 64)));
            // An already-revoked one too: teardown must not leave rows behind
            // just because they were spent.
            var revoked = Grant(new string('9', 64));
            revoked.RevokedAt = DateTime.UtcNow;
            db.PartyDisplayGrants.Add(revoked);
            await db.SaveChangesAsync();
        }

        // The key to the link is RESTRICT, so a party delete that forgot these
        // would fail with 23503 — the exact shape of the album-delete bug. The
        // eraser removes them explicitly instead.
        await using (var db = NewContext())
        {
            var eraser = new NubArca.Api.Party.PartyStateEraser(db);
            await eraser.EraseAsync(_partyId, default);
        }

        await using var verify = NewContext();
        Assert.Empty(await verify.PartyDisplayGrants.ToListAsync());
        Assert.Empty(await verify.PartyAlbumLinks.ToListAsync());
        // The television survives the party and returns to the general
        // experience, exactly as it does for every other party table.
        var tv = await verify.TvSessions.SingleAsync();
        Assert.Equal(TvDisplayAssignments.General, tv.DisplayAssignment);
        Assert.Null(tv.AssignedPartyAlbumLinkId);
    }

    private PartyDisplayGrant Grant(string tokenHash) => new()
    {
        Id = Guid.NewGuid(),
        TvSessionId = _tvSessionId,
        PartyAlbumLinkId = _linkId,
        TokenHash = tokenHash,
        CreatedAt = DateTime.UtcNow,
        ExpiresAt = DateTime.UtcNow.AddHours(4),
    };

    private AppDbContext NewContext() => new(_dbOptions!);
}
