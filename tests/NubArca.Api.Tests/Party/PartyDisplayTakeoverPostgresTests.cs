using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using NubArca.Api.Data;
using NubArca.Api.Domain;
using NubArca.Api.Party;
using NubArca.Api.Tests.Integration;
using NubArca.Api.Tv;
using Xunit;

namespace NubArca.Api.Tests.Party;

/// <summary>
/// The display grant and the takeover's control plane, under REAL concurrency
/// on real PostgreSQL.
///
/// SQLite serialises every writer on one file lock, so it cannot show what these
/// tests are about: two statements of two transactions interleaving. Here every
/// racer has its own pooled connection, and the claims are decided by
/// PostgreSQL's row locks and READ COMMITTED re-evaluation — exactly as in
/// production.
///
/// The party itself is resolved through a stub of the link service, because
/// what is under test is the grant's own boundary — the television's session
/// row — and not the party policy, which has its own suites.
/// </summary>
[Collection(PostgresIntegrationCollection.Name)]
[Trait("Category", "External")]
public sealed class PartyDisplayTakeoverPostgresTests : IAsyncLifetime
{
    private readonly PostgresContainerFixture _fixture;
    private DbContextOptions<AppDbContext>? _dbOptions;

    private readonly Guid _ownerId = Guid.NewGuid();
    private readonly Guid _partyA = Guid.NewGuid();
    private readonly Guid _albumA = Guid.NewGuid();
    private readonly Guid _linkA = Guid.NewGuid();
    private readonly Guid _partyB = Guid.NewGuid();
    private readonly Guid _albumB = Guid.NewGuid();
    private readonly Guid _linkB = Guid.NewGuid();
    private readonly Guid _tvA = Guid.NewGuid();
    private readonly Guid _tvB = Guid.NewGuid();

    // The raw session credentials the two televisions hold. Only their digests
    // are in the database, exactly as pairing stores them.
    private const string TokenA = "tv-session-a-0123456789abcdefghijklmnopqrstuvwxyz";
    private const string TokenB = "tv-session-b-0123456789abcdefghijklmnopqrstuvwxyz";

    private readonly HashSet<Guid> _showable = [];

    public PartyDisplayTakeoverPostgresTests(PostgresContainerFixture fixture) => _fixture = fixture;

    public async Task InitializeAsync()
    {
        if (!_fixture.Available) return;
        _dbOptions = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(_fixture.ConnectionString!).Options;
        await _fixture.ResetDatabaseAsync();

        var now = DateTime.UtcNow;
        await using var db = NewContext();
        db.Users.Add(new User
        {
            Id = _ownerId, Email = $"owner-{_ownerId:N}@example.com",
            DisplayName = "Owner", CreatedAt = now,
        });
        foreach (var (party, album, link, name, hash) in new[]
        {
            (_partyA, _albumA, _linkA, "Festa", new string('a', 64)),
            (_partyB, _albumB, _linkB, "Compleanno", new string('b', 64)),
        })
        {
            db.Albums.Add(new Album
            {
                Id = album, OwnerUserId = _ownerId, Name = name, CreatedAt = now, UpdatedAt = now,
            });
            db.Parties.Add(new NubArca.Api.Domain.Party
            {
                Id = party, OwnerUserId = _ownerId, Title = name,
                Status = PartyStatuses.Live, CreatedAt = now, UpdatedAt = now,
            });
            db.PartyAlbumLinks.Add(new PartyAlbumLink
            {
                Id = link, PartyId = party, OwnerUserId = _ownerId, AlbumId = album,
                TokenHash = hash, Enabled = true, GameEnabled = true,
                CreatedAt = now, UpdatedAt = now,
            });
            _showable.Add(link);
        }
        // Two televisions, both assigned to party A.
        foreach (var (tv, token) in new[] { (_tvA, TokenA), (_tvB, TokenB) })
            db.TvSessions.Add(new TvSession
            {
                Id = tv, OwnerUserId = _ownerId,
                SessionTokenHash = PartyDisplayService.HashToken(token),
                CreatedAt = now, LastSeenAt = now, ExpiresAt = now.AddDays(30),
                DisplayAssignment = TvDisplayAssignments.Party,
                AssignedPartyAlbumLinkId = _linkA,
            });
        await db.SaveChangesAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [SkippableFact]
    public async Task Racing_mints_of_one_television_leave_exactly_one_usable_grant()
    {
        Skip.IfNot(_fixture.Available, "Docker is not available; integration test skipped.");
        const int racers = 8;
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var contexts = Enumerable.Range(0, racers).Select(_ => NewContext()).ToList();
        try
        {
            // Released together, each on its own connection: a remount racing a
            // renewal racing a retry, as hard as it can be done.
            var mints = contexts.Select(async db =>
            {
                await gate.Task;
                return await Display(db).MintAsync(TokenA);
            }).ToList();
            gate.SetResult();
            var results = await Task.WhenAll(mints);

            // Ordered, not refused: every mint succeeded, one after another.
            Assert.All(results, r => Assert.Null(r.Error));
            Assert.Equal(racers, results.Select(r => r.Token).Distinct().Count());

            await using var verify = NewContext();
            Assert.Equal(racers, await verify.PartyDisplayGrants.CountAsync(g => g.TvSessionId == _tvA));
            // …and exactly ONE of them is still a credential.
            Assert.Single(await verify.PartyDisplayGrants
                .Where(g => g.TvSessionId == _tvA && g.RevokedAt == null).ToListAsync());
            var usable = 0;
            foreach (var result in results)
                if (await Display(verify).ResolveAsync(result.Token) is not null) usable++;
            Assert.Equal(1, usable);
        }
        finally
        {
            foreach (var db in contexts) await db.DisposeAsync();
        }
    }

    [SkippableFact]
    public async Task Two_televisions_minting_at_once_never_touch_each_others_grant()
    {
        Skip.IfNot(_fixture.Available, "Docker is not available; integration test skipped.");
        PartyDisplayGrantResult lastA = null!, lastB = null!;
        for (var round = 0; round < 5; round++)
        {
            await using var dbA = NewContext();
            await using var dbB = NewContext();
            var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var a = Task.Run(async () => { await gate.Task; return await Display(dbA).MintAsync(TokenA); });
            var b = Task.Run(async () => { await gate.Task; return await Display(dbB).MintAsync(TokenB); });
            gate.SetResult();
            (lastA, lastB) = (await a, await b);
            Assert.Null(lastA.Error);
            Assert.Null(lastB.Error);
        }

        // Two rows locked are two different rows: each device holds exactly one
        // live grant, and both of the last ones read the same party.
        await using var verify = NewContext();
        foreach (var tv in new[] { _tvA, _tvB })
            Assert.Single(await verify.PartyDisplayGrants
                .Where(g => g.TvSessionId == tv && g.RevokedAt == null).ToListAsync());
        Assert.Equal(_linkA, (await Display(verify).ResolveAsync(lastA.Token))?.PartyAlbumLinkId);
        Assert.Equal(_linkA, (await Display(verify).ResolveAsync(lastB.Token))?.PartyAlbumLinkId);
        Assert.Empty(await verify.PartyParticipants.ToListAsync());
    }

    [SkippableFact]
    public async Task A_mint_racing_an_assignment_change_writes_nothing_for_the_old_party()
    {
        Skip.IfNot(_fixture.Available, "Docker is not available; integration test skipped.");

        // An owner's assignment change, holding the television's row.
        await using var owner = NewContext();
        await using var ownerTx = await owner.Database.BeginTransactionAsync();
        await owner.TvSessions.Where(t => t.Id == _tvA)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.AssignedPartyAlbumLinkId, (Guid?)_linkB));

        // The television's mint has already read "party A" (reads do not wait)
        // and now blocks on the claim.
        await using var tvDb = NewContext();
        var mint = Display(tvDb).MintAsync(TokenA);
        await WaitForALockWaiterAsync();
        Assert.False(mint.IsCompleted, "the claim must wait for the owner's transaction");

        await ownerTx.CommitAsync();

        // Re-evaluated under the lock against the row the owner left behind:
        // not party A any more. Refused, and nothing written.
        var result = await mint;
        Assert.Equal(PartyDisplayGrantError.NotAssigned, result.Error);
        await using var verify = NewContext();
        Assert.Empty(await verify.PartyDisplayGrants.Where(g => g.TvSessionId == _tvA).ToListAsync());
    }

    [SkippableFact]
    public async Task A_grant_dies_the_moment_its_television_is_pointed_elsewhere()
    {
        Skip.IfNot(_fixture.Available, "Docker is not available; integration test skipped.");
        await using var db = NewContext();
        var grant = await Display(db).MintAsync(TokenA);
        Assert.NotNull(await Display(db).ResolveAsync(grant.Token));
        var before = await Assignments(db).ResolveAsync(_tvA);
        Assert.Equal(TvPartyPresentations.Game, before.Presentation);

        await db.TvSessions.Where(t => t.Id == _tvA)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.AssignedPartyAlbumLinkId, (Guid?)_linkB));
        Assert.Null(await Display(db).ResolveAsync(grant.Token));
        // The television is told it is a different party now.
        var after = await Assignments(db).ResolveAsync(_tvA);
        Assert.Equal(_albumB, after.AlbumId);
        Assert.NotEqual(before.AssignmentKey, after.AssignmentKey);

        // And a party that stops being showable is unavailable, not general.
        _showable.Remove(_linkB);
        Assert.Equal(TvPartyPresentations.Unavailable, (await Assignments(db).ResolveAsync(_tvA)).Presentation);
        // Television B, still on party A, is untouched by any of it.
        Assert.Equal(TvPartyPresentations.Game, (await Assignments(db).ResolveAsync(_tvB)).Presentation);
    }

    [SkippableFact]
    public async Task Tearing_down_a_party_with_live_grants_is_all_or_nothing()
    {
        Skip.IfNot(_fixture.Available, "Docker is not available; integration test skipped.");
        string tokenA, tokenB;
        await using (var db = NewContext())
        {
            tokenA = (await Display(db).MintAsync(TokenA)).Token!;
            tokenB = (await Display(db).MintAsync(TokenB)).Token!;
        }

        // An album delete that fails half-way — here, rolled back after the
        // eraser has run — must leave every television and every grant exactly
        // as it found them. The eraser writes inside its caller's transaction,
        // which is what makes that true.
        await using (var db = NewContext())
        {
            await using var tx = await db.Database.BeginTransactionAsync();
            await new PartyStateEraser(db).EraseAsync(_partyA, _albumA);
            Assert.Empty(await db.PartyDisplayGrants.ToListAsync());
            await tx.RollbackAsync();
        }
        await using (var db = NewContext())
        {
            Assert.NotNull(await Display(db).ResolveAsync(tokenA));
            Assert.NotNull(await Display(db).ResolveAsync(tokenB));
            Assert.All(await db.TvSessions.AsNoTracking().ToListAsync(),
                t => Assert.Equal(_linkA, t.AssignedPartyAlbumLinkId));
        }

        // The real teardown: grants gone, both televisions really general.
        await using (var db = NewContext())
        {
            await using var tx = await db.Database.BeginTransactionAsync();
            await new PartyStateEraser(db).EraseAsync(_partyA, _albumA);
            await tx.CommitAsync();
        }
        await using (var db = NewContext())
        {
            Assert.Empty(await db.PartyDisplayGrants.ToListAsync());
            Assert.Null(await Display(db).ResolveAsync(tokenA));
            foreach (var tv in new[] { _tvA, _tvB })
            {
                var assignment = await Assignments(db).ResolveAsync(tv);
                Assert.Equal(TvDisplayAssignments.General, assignment.Kind);
                Assert.Equal(TvPartyPresentations.General, assignment.Presentation);
            }
        }
    }

    // --- helpers -----------------------------------------------------------

    private PartyDisplayService Display(AppDbContext db) =>
        new(db, TimeProvider.System, new StubLinks(this), NullLogger<PartyDisplayService>.Instance);

    private TvDisplayAssignmentService Assignments(AppDbContext db) =>
        new(db, TimeProvider.System, new StubLinks(this), NullLogger<TvDisplayAssignmentService>.Instance);

    private AppDbContext NewContext() => new(_dbOptions!);

    /// Waits until some backend is blocked on a lock — the mint's claim.
    private async Task WaitForALockWaiterAsync()
    {
        await using var probe = new NpgsqlConnection(_fixture.ConnectionString);
        await probe.OpenAsync();
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (DateTime.UtcNow < deadline)
        {
            await using var command = new NpgsqlCommand(
                "SELECT count(*) FROM pg_stat_activity WHERE wait_event_type = 'Lock' AND datname = current_database()",
                probe);
            if ((long)(await command.ExecuteScalarAsync())! > 0) return;
            await Task.Delay(20);
        }
        Assert.Fail("the mint never reached the television's row lock");
    }

    /// <summary>
    /// The party policy, reduced to "is this link showable". Everything else on
    /// the interface is outside what these tests exercise.
    /// </summary>
    private sealed class StubLinks(PartyDisplayTakeoverPostgresTests test) : IPartyLinkService
    {
        public Task<PartyAccess?> ResolveDisplayAsync(Guid partyAlbumLinkId, CancellationToken cancellationToken = default)
        {
            if (!test._showable.Contains(partyAlbumLinkId)) return Task.FromResult<PartyAccess?>(null);
            var (party, album) = partyAlbumLinkId == test._linkA
                ? (test._partyA, test._albumA)
                : (test._partyB, test._albumB);
            return Task.FromResult<PartyAccess?>(new PartyAccess(
                party, test._ownerId, album, partyAlbumLinkId,
                new PartyCapabilities(Access: true, Contributions: true, Games: true, Print: true, FaceSearch: true),
                new PartyGuestExperience(PartyGuestPhase.Live, PartyGuestAccessMode.Full, true, null)));
        }

        public Task<PartyEnableResult?> EnableAsync(Guid ownerUserId, Guid albumId, Guid createdByUserId,
            bool? uploadEnabled = null, bool? requireApproval = null, bool? requireMessageApproval = null,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<bool> DisableAsync(Guid ownerUserId, Guid albumId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task<AlbumPartyStatusDto?> GetOwnerStatusAsync(Guid ownerUserId, Guid albumId,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyDictionary<Guid, PartyLinkUrls>> GetActivePartyUrlsAsync(Guid ownerUserId,
            IReadOnlyCollection<Guid> albumIds, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task<bool> UpdateSlideshowSettingsAsync(Guid ownerUserId, Guid albumId, int? photoSlideSeconds,
            int? maxVideoSlideSeconds, int? maxPhotoUploadsPerParticipant, int? maxVideoUploadsPerParticipant,
            int? maxMessagesPerParticipant, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task<bool> UpdateGameSettingsAsync(Guid ownerUserId, Guid albumId, bool gameEnabled,
            int minChallengeIntervalSeconds, int maxChallengeIntervalSeconds, int votesPerGuest,
            int? maxChallengesPerSession, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public string DeriveViewToken(Guid linkId) => throw new NotSupportedException();
        public Task<PartyAccess?> ResolvePublicAsync(string token, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task<PartyAccess?> ResolveUploadAsync(string uploadToken, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
