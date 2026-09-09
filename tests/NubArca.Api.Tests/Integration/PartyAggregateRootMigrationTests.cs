using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using NubArca.Api.Access;
using NubArca.Api.Data;
using NubArca.Api.Domain;
using Npgsql;
using Testcontainers.PostgreSql;
using Xunit;

namespace NubArca.Api.Tests.Integration;

// The Party cutover meeting a database that ALREADY EXISTS.
//
// Everything the P1 code does is easy to verify on a fresh installation, where
// a party is created by the same service that reads it. What cannot be assumed
// is the upgrade: parties that are RUNNING, and parties whose QR was revoked
// three weddings ago, all become rows in a table that did not exist. The rules
// are worth proving on real PostgreSQL rather than trusting to a CTE that
// looked right.
//
//   * ONE party per historical party album, however many links it has been
//     through — that is what makes "the event" a thing rather than "the QR".
//   * The status is what the DATA says: an album with a live link is published,
//     everything else is draft. Never "ended", which would claim an event
//     happened and finished, something no row here records.
//   * Not one token hash, participant, greeting or upload row is rewritten.
//   * Member gains the five Party keys, and nobody else is widened.
[Trait("Category", "External")]
[Collection("RoleMigration")]
public sealed class PartyAggregateRootMigrationTests : IAsyncLifetime
{
    private const string PreviousMigration = "20260909090022_AddTvDisplayAssignment";
    private const string MigrationUnderTest = "20260909164555_AddPartyAggregateRoot";
    private const string CustomRole = "custom:party-operator";

    private static readonly string[] PartyKeys =
    [
        "party.access", "party.contributions", "party.games",
        "party.print", "party.face-search",
    ];

    private readonly Guid _owner = Guid.NewGuid();
    private readonly Guid _liveAlbum = Guid.NewGuid();
    private readonly Guid _pastAlbum = Guid.NewGuid();
    private readonly Guid _plainAlbum = Guid.NewGuid();
    private readonly Guid _liveLink = Guid.NewGuid();
    private readonly Guid _pastLinkOne = Guid.NewGuid();
    private readonly Guid _pastLinkTwo = Guid.NewGuid();

    private PostgreSqlContainer? _container;
    private string? _connectionString;

    private bool Available => _connectionString is not null;

    public async Task InitializeAsync()
    {
        try
        {
            _container = new PostgreSqlBuilder()
                .WithImage("postgres:17-alpine")
                .WithDatabase("nubarca_partyroot")
                .WithUsername("nubarca")
                .WithPassword("nubarca")
                .Build();

            await _container.StartAsync();
            _connectionString = _container.GetConnectionString();
        }
        catch (Exception)
        {
            // No reachable Docker: the tests skip rather than fail.
            _connectionString = null;
        }
    }

    public async Task DisposeAsync()
    {
        if (_container is not null)
        {
            await _container.DisposeAsync();
        }
    }

    [Fact]
    public async Task Every_Historical_Party_Album_Becomes_Exactly_One_Party()
    {
        Skip.IfNot(Available, "Docker is not available for the PostgreSQL integration container.");

        await MigrateAsync(PreviousMigration);
        await SeedAsync();

        await MigrateAsync(MigrationUnderTest);

        // Two albums have ever held a party link, so there are two parties —
        // and the album with three revoked/rotated links has ONE of them, not
        // three, because those were all the same evening.
        var parties = await QueryAsync(
            """SELECT "Id", "OwnerUserId", "Title", "Status", "Version" FROM parties ORDER BY "Title";""",
            r => (Id: r.GetGuid(0), Owner: r.GetGuid(1), Title: r.GetString(2),
                  Status: r.GetString(3), Version: r.GetInt32(4)));
        Assert.Equal(2, parties.Count);
        Assert.All(parties, p => Assert.Equal(_owner, p.Owner));
        Assert.All(parties, p => Assert.Equal(1, p.Version));

        var live = parties.Single(p => p.Title == "Festa di stasera");
        var past = parties.Single(p => p.Title == "Festa dell'anno scorso");

        // The status is the data's own answer. A live link is a party a guest
        // can walk into right now; everything else is simply not announced.
        Assert.Equal(PartyStatuses.Published, live.Status);
        Assert.Equal(PartyStatuses.Draft, past.Status);

        // One `main` media source each, pointing back at the album the party
        // was made from. Nothing was invented for the album that never had one.
        var sources = await QueryAsync(
            """SELECT "PartyId", "AlbumId", "Role", "SortOrder" FROM party_media_sources;""",
            r => (Party: r.GetGuid(0), Album: r.GetGuid(1), Role: r.GetString(2), Sort: r.GetInt32(3)));
        Assert.Equal(2, sources.Count);
        Assert.All(sources, s => Assert.Equal(PartyMediaSourceRoles.Main, s.Role));
        Assert.All(sources, s => Assert.Equal(0, s.Sort));
        Assert.Equal(_liveAlbum, sources.Single(s => s.Party == live.Id).Album);
        Assert.Equal(_pastAlbum, sources.Single(s => s.Party == past.Id).Album);
        Assert.DoesNotContain(sources, s => s.Album == _plainAlbum);

        // Every link now names its album's party — the revoked ones included,
        // because a revoked QR still belonged to an evening that happened.
        var links = await QueryAsync(
            """SELECT "Id", "PartyId" FROM party_album_links;""",
            r => (Id: r.GetGuid(0), Party: r.GetGuid(1)));
        Assert.Equal(3, links.Count);
        Assert.Equal(live.Id, links.Single(l => l.Id == _liveLink).Party);
        Assert.Equal(past.Id, links.Single(l => l.Id == _pastLinkOne).Party);
        Assert.Equal(past.Id, links.Single(l => l.Id == _pastLinkTwo).Party);

        // And the column is required in its own right afterwards, rather than
        // relying on a foreign key to reject a zero GUID somebody defaulted to.
        Assert.Null(await ScalarAsync(
            """
            SELECT column_default FROM information_schema.columns
            WHERE table_name = 'party_album_links' AND column_name = 'PartyId';
            """));
    }

    [Fact]
    public async Task Nothing_A_Guest_Holds_Is_Rewritten()
    {
        Skip.IfNot(Available, "Docker is not available for the PostgreSQL integration container.");

        await MigrateAsync(PreviousMigration);
        await SeedAsync();

        var tokensBefore = await QueryAsync(
            """SELECT "Id", "TokenHash", "UploadTokenHash" FROM party_album_links ORDER BY "Id";""",
            r => (Id: r.GetGuid(0), Token: r.GetString(1), Upload: r.GetString(2)));

        await MigrateAsync(MigrationUnderTest);

        // A raw token was never read, derived or persisted here — so the stored
        // hashes are byte-for-byte the hashes that were already there, and the
        // QR in somebody's pocket keeps working across the upgrade.
        var tokensAfter = await QueryAsync(
            """SELECT "Id", "TokenHash", "UploadTokenHash" FROM party_album_links ORDER BY "Id";""",
            r => (Id: r.GetGuid(0), Token: r.GetString(1), Upload: r.GetString(2)));
        Assert.Equal(tokensBefore, tokensAfter);

        // The guest's own identity and everything hanging off it are untouched:
        // their quota counters still say what they had used.
        var participants = await QueryAsync(
            """
            SELECT "PartyAlbumLinkId", "AcceptedPhotoCount", "SubmittedMessageCount"
            FROM party_participants;
            """,
            r => (Link: r.GetGuid(0), Photos: r.GetInt32(1), Messages: r.GetInt32(2)));
        var participant = Assert.Single(participants);
        Assert.Equal(_liveLink, participant.Link);
        Assert.Equal(2, participant.Photos);
        Assert.Equal(1, participant.Messages);

        Assert.Equal("Auguri!", await ScalarAsync("""SELECT "Body" FROM party_messages;"""));
    }

    [Fact]
    public async Task Member_Gains_The_Party_Keys_And_Nobody_Else_Is_Widened()
    {
        Skip.IfNot(Available, "Docker is not available for the PostgreSQL integration container.");

        await MigrateAsync(PreviousMigration);
        await SeedCustomRoleAsync();
        var memberBefore = await PermissionsOfAsync(RoleKeys.Member);
        var customBefore = await PermissionsOfAsync(CustomRole);
        Assert.All(PartyKeys, key => Assert.DoesNotContain(key, memberBefore));

        await MigrateAsync(MigrationUnderTest);

        var memberAfter = await PermissionsOfAsync(RoleKeys.Member);
        foreach (var key in PartyKeys)
        {
            Assert.Contains(key, memberAfter);
            Assert.Equal(1, memberAfter.Count(k => k == key));
        }
        Assert.Equal(memberBefore.Concat(PartyKeys).OrderBy(k => k, StringComparer.Ordinal), memberAfter);

        // Restricted is empty by design; the operator's own role is exactly as
        // they left it. Administrator is catalogue-driven and re-synced on boot,
        // so what matters is that the keys are part of what it always holds.
        Assert.All(PartyKeys, key =>
            Assert.DoesNotContain(key, PermissionsOfAsync(RoleKeys.Restricted).GetAwaiter().GetResult()));
        Assert.Equal(customBefore, await PermissionsOfAsync(CustomRole));
        Assert.All(PartyKeys, key => Assert.Contains(key, RoleDefaults.AdministratorPermissions));
        Assert.Equal(
            PartyKeys.OrderBy(k => k, StringComparer.Ordinal),
            new[]
            {
                Permissions.PartyAccess, Permissions.PartyContributions, Permissions.PartyGames,
                Permissions.PartyPrint, Permissions.PartyFaceSearch,
            }.OrderBy(k => k, StringComparer.Ordinal));
    }

    [Fact]
    public async Task Rolling_Back_Removes_Only_What_This_Migration_Added()
    {
        Skip.IfNot(Available, "Docker is not available for the PostgreSQL integration container.");

        await MigrateAsync(PreviousMigration);
        await SeedAsync();
        await SeedCustomRoleAsync();
        await MigrateAsync(MigrationUnderTest);

        // An operator who granted a Party key to their OWN role afterwards keeps
        // it: the Down names Member explicitly and cannot reach anything else.
        await ExecuteAsync(
            """INSERT INTO role_permissions ("RoleKey", "PermissionKey") VALUES (@role, 'party.access');""",
            ("role", CustomRole));

        await MigrateAsync(PreviousMigration);

        Assert.All(PartyKeys, key =>
            Assert.DoesNotContain(key, PermissionsOfAsync(RoleKeys.Member).GetAwaiter().GetResult()));
        Assert.Contains("party.access", await PermissionsOfAsync(CustomRole));

        // The links survive the rollback with their tokens; only the root goes.
        Assert.Equal(3L, Convert.ToInt64(await ScalarAsync("SELECT count(*) FROM party_album_links;")));
        Assert.Equal(
            0L,
            Convert.ToInt64(await ScalarAsync(
                "SELECT count(*) FROM information_schema.tables WHERE table_name = 'parties';")));
    }

    // ---- plumbing ---------------------------------------------------------

    private AppDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(_connectionString!)
            .Options;
        return new AppDbContext(options);
    }

    private async Task MigrateAsync(string target)
    {
        await using var ctx = CreateContext();
        await ctx.GetInfrastructure().GetRequiredService<IMigrator>().MigrateAsync(target);
    }

    // Three albums: one holding a live party, one whose party was revoked and
    // re-minted (so the "one party per album" rule has something to get wrong),
    // and one that never had a party at all.
    private async Task SeedAsync()
    {
        await ExecuteAsync(
            """
            INSERT INTO users ("Id", "Email", "DisplayName", "PasswordHash", "CreatedAt",
                               "RoleKey", "UiLanguage", "SecurityVersion")
            VALUES (@owner, @email, 'Host', 'not-a-real-hash', now(), 'Member', 'it', 1);
            """,
            ("owner", _owner), ("email", $"host-{_owner:N}@example.invalid"));

        foreach (var (id, name) in new[]
        {
            (_liveAlbum, "Festa di stasera"),
            (_pastAlbum, "Festa dell'anno scorso"),
            (_plainAlbum, "Vacanze"),
        })
        {
            await ExecuteAsync(
                """
                INSERT INTO albums ("Id", "OwnerUserId", "Name", "Description", "ShowOnTv",
                                    "Version", "CreatedAt", "UpdatedAt")
                VALUES (@id, @owner, @name, NULL, false, 1, now(), now());
                """,
                ("id", id), ("owner", _owner), ("name", name));
        }

        await SeedLinkAsync(_liveLink, _liveAlbum, enabled: true, revoked: false);
        await SeedLinkAsync(_pastLinkOne, _pastAlbum, enabled: false, revoked: true);
        await SeedLinkAsync(_pastLinkTwo, _pastAlbum, enabled: false, revoked: true);

        // A guest of the live party, with things they have already spent.
        var participant = Guid.NewGuid();
        await ExecuteAsync(
            """
            INSERT INTO party_participants
                ("Id", "PartyAlbumLinkId", "TokenHash", "AcceptedPhotoCount", "AcceptedVideoCount",
                 "SubmittedMessageCount", "ChallengeVoteCount",
                 "AcceptedPhotoPrintCount", "AcceptedStripPrintCount",
                 "CreatedAt", "LastSeenAt")
            VALUES (@id, @link, @hash, 2, 0, 1, 0, 0, 0, now(), now());
            """,
            ("id", participant), ("link", _liveLink), ("hash", new string('c', 64)));

        await ExecuteAsync(
            """
            INSERT INTO party_messages
                ("Id", "PartyAlbumLinkId", "AlbumId", "OwnerUserId", "PartyParticipantId",
                 "DisplayName", "Body", "Status", "CreatedAt", "UpdatedAt")
            VALUES (@id, @link, @album, @owner, @participant, 'Anna', 'Auguri!', 'visible', now(), now());
            """,
            ("id", Guid.NewGuid()), ("link", _liveLink), ("album", _liveAlbum),
            ("owner", _owner), ("participant", participant));
    }

    private Task SeedLinkAsync(Guid id, Guid albumId, bool enabled, bool revoked) =>
        ExecuteAsync(
            """
            INSERT INTO party_album_links
                ("Id", "OwnerUserId", "AlbumId", "TokenHash", "UploadTokenHash",
                 "Enabled", "UploadEnabled", "RequireUploadApproval", "RequireMessageApproval",
                 "PhotoSlideSeconds", "MaxVideoSlideSeconds",
                 "MaxPhotoUploadsPerParticipant", "MaxVideoUploadsPerParticipant",
                 "CreatedAt", "UpdatedAt", "RevokedAt")
            VALUES (@id, @owner, @album, @token, @upload,
                    @enabled, @enabled, false, false, 9, 60, 0, 0,
                    now(), now(), CASE WHEN @revoked THEN now() ELSE NULL END);
            """,
            ("id", id), ("owner", _owner), ("album", albumId),
            ("token", $"{id:N}{id:N}"), ("upload", $"u{id:N}{id:N}"[..64]),
            ("enabled", enabled), ("revoked", revoked));

    private Task SeedCustomRoleAsync() => ExecuteAsync(
        """
        INSERT INTO access_roles
            ("Key", "Name", "Description", "IsSystem", "IsAdministrator",
             "CreatedAt", "UpdatedAt", "Version")
        VALUES (@role, 'Operator built', null, false, false, now(), now(), 1)
        ON CONFLICT ("Key") DO NOTHING;

        INSERT INTO role_permissions ("RoleKey", "PermissionKey")
        SELECT @role, 'people.access'
        WHERE NOT EXISTS (
            SELECT 1 FROM role_permissions
            WHERE "RoleKey" = @role AND "PermissionKey" = 'people.access');
        """,
        ("role", CustomRole));

    private async Task<string[]> PermissionsOfAsync(string roleKey)
    {
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT "PermissionKey" FROM role_permissions
            WHERE "RoleKey" = @role ORDER BY "PermissionKey";
            """;
        command.Parameters.AddWithValue("role", roleKey);
        var keys = new List<string>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            keys.Add(reader.GetString(0));
        }
        return [.. keys];
    }

    private async Task<List<T>> QueryAsync<T>(string sql, Func<NpgsqlDataReader, T> read)
    {
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        var rows = new List<T>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            rows.Add(read(reader));
        }
        return rows;
    }

    private async Task<object?> ScalarAsync(string sql)
    {
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        var value = await command.ExecuteScalarAsync();
        return value is DBNull ? null : value;
    }

    private async Task ExecuteAsync(string sql, params (string Name, object Value)[] parameters)
    {
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }
        await command.ExecuteNonQueryAsync();
    }
}
