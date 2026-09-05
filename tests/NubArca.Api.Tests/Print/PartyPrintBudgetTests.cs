using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NubArca.Api.Data;
using NubArca.Api.Domain;
using NubArca.Api.Domain.Print;
using NubArca.Api.Print;
using Microsoft.Data.Sqlite;
using NubArca.Api.Tests.Endpoints;

namespace NubArca.Api.Tests.Print;

/// <summary>
/// The budget is the part of party printing where a mistake costs paper, so it
/// is tested against the database rather than against a mock: the guarantee is
/// the conditional UPDATE, and a mock would only assert that the code calls
/// itself.
/// </summary>
public sealed class PartyPrintBudgetTests : IDisposable
{
    private readonly SqliteWebApplicationFactory _factory = new();
    public PartyPrintBudgetTests() => _factory.EnsureDatabaseCreated();
    public void Dispose() => _factory.Dispose();

    private int _seeded;

    private async Task<Guid> SeedProfileAsync(
        int photoMax = 3, int stripMax = 2,
        bool enabled = true, bool photoEnabled = true, bool stripEnabled = true)
    {
        // A profile hangs off a real album owned by a real user: the foreign keys
        // are part of what is being tested, so the seed honours them.
        var ownerId = await _factory.SeedUserAsync($"owner{Interlocked.Increment(ref _seeded)}@example.com");
        var albumId = Guid.NewGuid();
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        db.Albums.Add(new Album
        {
            Id = albumId, OwnerUserId = ownerId,
            Name = "Festa", CreatedAt = DateTime.UtcNow,
        });
        db.PartyPrintProfiles.Add(new PartyPrintProfile
        {
            Id = Guid.NewGuid(),
            PartyAlbumId = albumId,
            OwnerUserId = ownerId,
            Enabled = enabled,
            PhotoEnabled = photoEnabled,
            PhotoMaxPrints = photoMax,
            StripEnabled = stripEnabled,
            StripMaxPrints = stripMax,
            PublicSequenceNext = 1,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
        return albumId;
    }

    private async Task<T> WithBudgetAsync<T>(Func<IPartyPrintBudget, Task<T>> body)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await body(new PartyPrintBudget(db));
    }

    private async Task<PartyPrintProfile> ReadAsync(Guid albumId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.PartyPrintProfiles.AsNoTracking()
            .SingleAsync(x => x.PartyAlbumId == albumId);
    }

    [Fact]
    public async Task Photo_And_Strip_Budgets_Are_Independent()
    {
        // Two products, two numbers the host set separately. Exhausting one must
        // leave the other exactly where it was.
        var albumId = await SeedProfileAsync(photoMax: 2, stripMax: 2);

        Assert.NotNull(await WithBudgetAsync(b =>
            b.TryReserveAsync(albumId, PartyPrintProducts.Strip4, default)));
        Assert.NotNull(await WithBudgetAsync(b =>
            b.TryReserveAsync(albumId, PartyPrintProducts.Strip4, default)));
        // Strips are gone.
        Assert.Null(await WithBudgetAsync(b =>
            b.TryReserveAsync(albumId, PartyPrintProducts.Strip4, default)));

        // Photos are untouched by that, and are never summed with it.
        var photo = await WithBudgetAsync(b =>
            b.TryReserveAsync(albumId, PartyPrintProducts.Photo, default));
        Assert.NotNull(photo);
        Assert.Equal(1, photo!.RemainingAfter);

        var profile = await ReadAsync(albumId);
        Assert.Equal(1, profile.PhotoAcceptedCount);
        Assert.Equal(2, profile.StripAcceptedCount);
    }

    [Fact]
    public async Task One_Accepted_Job_Costs_Exactly_One_Unit()
    {
        // A strip composes four photographs and still costs one strip.
        var albumId = await SeedProfileAsync(photoMax: 10, stripMax: 10);
        await WithBudgetAsync(b => b.TryReserveAsync(albumId, PartyPrintProducts.Strip4, default));
        await WithBudgetAsync(b => b.TryReserveAsync(albumId, PartyPrintProducts.Photo, default));

        var profile = await ReadAsync(albumId);
        Assert.Equal(1, profile.StripAcceptedCount);
        Assert.Equal(1, profile.PhotoAcceptedCount);
    }

    [Fact]
    public async Task Disabled_Product_And_Disabled_Party_Reserve_Nothing()
    {
        var photoOff = await SeedProfileAsync(photoEnabled: false);
        Assert.Null(await WithBudgetAsync(b =>
            b.TryReserveAsync(photoOff, PartyPrintProducts.Photo, default)));
        // The other product still works: one switch does not close both.
        Assert.NotNull(await WithBudgetAsync(b =>
            b.TryReserveAsync(photoOff, PartyPrintProducts.Strip4, default)));

        var partyOff = await SeedProfileAsync(enabled: false);
        Assert.Null(await WithBudgetAsync(b =>
            b.TryReserveAsync(partyOff, PartyPrintProducts.Photo, default)));
        Assert.Null(await WithBudgetAsync(b =>
            b.TryReserveAsync(partyOff, PartyPrintProducts.Strip4, default)));
    }

    [Fact]
    public async Task Concurrent_Guests_Cannot_Overspend_The_Last_Print()
    {
        // The case the conditional UPDATE exists for: eight guests reaching for
        // three prints at the same moment. Exactly three may win — never four,
        // whatever the interleaving.
        foreach (var product in new[] { PartyPrintProducts.Photo, PartyPrintProducts.Strip4 })
        {
            var albumId = await SeedProfileAsync(photoMax: 3, stripMax: 3);
            var attempts = Enumerable.Range(0, 8).Select(_ => Task.Run(async () =>
            {
                // SQLite serialises writers with a single file lock, so eight
                // simultaneous transactions collide on the harness in a way
                // PostgreSQL's row locking does not. Waiting out that lock keeps
                // the interleaving real — every attempt still reaches the
                // conditional UPDATE — while removing an artefact of the test
                // database. A collision is retried; nothing else is.
                for (var attempt = 0; ; attempt++)
                {
                    try
                    {
                        using var scope = _factory.Services.CreateScope();
                        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                        return await new PartyPrintBudget(db)
                            .TryReserveAsync(albumId, product, default);
                    }
                    catch (SqliteException e) when (e.SqliteErrorCode is 5 or 6 && attempt < 50)
                    {
                        await Task.Delay(10);
                    }
                }
            }));
            var results = await Task.WhenAll(attempts);

            var granted = results.Where(r => r is not null).ToList();
            Assert.Equal(3, granted.Count);
            // And each winner was handed a DIFFERENT public number: two guests
            // must never be able to claim the same print at the station.
            Assert.Equal(3, granted.Select(r => r!.PublicSequence).Distinct().Count());

            var profile = await ReadAsync(albumId);
            var accepted = product == PartyPrintProducts.Photo
                ? profile.PhotoAcceptedCount
                : profile.StripAcceptedCount;
            Assert.Equal(3, accepted);
        }
    }

    [Fact]
    public async Task Public_Sequence_Is_Per_Party_And_Advances_Once_Per_Print()
    {
        // A guest reads "print #3" and finds it at the station: the number counts
        // this party's prints, across both products, and is not a database id.
        var first = await SeedProfileAsync(photoMax: 5, stripMax: 5);
        var second = await SeedProfileAsync(photoMax: 5, stripMax: 5);

        var a = await WithBudgetAsync(b => b.TryReserveAsync(first, PartyPrintProducts.Photo, default));
        var b2 = await WithBudgetAsync(b => b.TryReserveAsync(first, PartyPrintProducts.Strip4, default));
        var c = await WithBudgetAsync(b => b.TryReserveAsync(first, PartyPrintProducts.Photo, default));
        Assert.Equal(1, a!.PublicSequence);
        Assert.Equal(2, b2!.PublicSequence);
        Assert.Equal(3, c!.PublicSequence);

        // Another party counts from one: the sequence belongs to the party.
        var other = await WithBudgetAsync(b => b.TryReserveAsync(second, PartyPrintProducts.Photo, default));
        Assert.Equal(1, other!.PublicSequence);
    }

    [Fact]
    public async Task Releasing_Returns_A_Unit_Without_Ever_Manufacturing_One()
    {
        // The only refund there is: a request that never became a job. It gives
        // back exactly what it took, and a repeat cannot invent budget.
        var albumId = await SeedProfileAsync(photoMax: 2, stripMax: 2);
        await WithBudgetAsync(b => b.TryReserveAsync(albumId, PartyPrintProducts.Photo, default));
        Assert.Equal(1, (await ReadAsync(albumId)).PhotoAcceptedCount);

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var budget = new PartyPrintBudget(db);
            await budget.ReleaseAsync(albumId, PartyPrintProducts.Photo, default);
            await budget.ReleaseAsync(albumId, PartyPrintProducts.Photo, default);
            await budget.ReleaseAsync(albumId, PartyPrintProducts.Photo, default);
        }

        // Back to zero and no further: the counter never goes negative.
        Assert.Equal(0, (await ReadAsync(albumId)).PhotoAcceptedCount);
    }

    [Fact]
    public void Product_Rules_State_What_Each_Print_Composes()
    {
        Assert.Equal(1, PartyPrintProducts.RequiredPhotos(PartyPrintProducts.Photo));
        Assert.Equal(4, PartyPrintProducts.RequiredPhotos(PartyPrintProducts.Strip4));
        Assert.True(PartyPrintProducts.IsKnown("photo"));
        Assert.False(PartyPrintProducts.IsKnown("collage"));
        // Both party kinds print on the same paper: the strip is a composition,
        // not a second hardware capability.
        Assert.True(PrintJobKinds.IsParty(PrintJobKinds.PartyStrip4));
        Assert.False(PrintJobKinds.IsParty(PrintJobKinds.OwnerPhoto));
    }

    [Fact]
    public void Crop_Validation_Refuses_What_It_Cannot_Print()
    {
        Assert.True(PrintJobSource.IsValidCrop(0, 0, 1, 1));
        Assert.True(PrintJobSource.IsValidCrop(0.25, 0.1, 0.5, 0.5));
        Assert.False(PrintJobSource.IsValidCrop(0, 0, 0, 0.5));       // no area
        Assert.False(PrintJobSource.IsValidCrop(-0.1, 0, 0.5, 0.5));  // outside
        Assert.False(PrintJobSource.IsValidCrop(0.8, 0, 0.5, 0.5));   // past the edge
        Assert.False(PrintJobSource.IsValidCrop(double.NaN, 0, 1, 1));
    }
}
