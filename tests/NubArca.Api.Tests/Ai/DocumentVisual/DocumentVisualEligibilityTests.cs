using Microsoft.EntityFrameworkCore;
using NubArca.Api.Ai.DocumentVisual;
using NubArca.Api.Domain.Ai;
using NubArca.Api.Domain;
using Xunit;

namespace NubArca.Api.Tests.Ai.DocumentVisual;

// THE VISUAL BOUNDARY, exercised with every stale row deliberately left behind.
//
// A `DocumentVisualIndex` records a rendering that happened at some point in
// the past. Between then and now the file may have been deleted, moved into the
// Private Vault, excluded from the library, or had its bytes replaced — and in
// every one of those cases the derived rows are still sitting there, because
// cleaning them up is housekeeping that runs on a schedule.
//
// So each test below creates exactly the situation a sweeper has not caught up
// with, and asserts that the join produces nothing. Every one of them would
// pass trivially if the fixture deleted the rows, which is why none of them
// does.
public sealed class DocumentVisualEligibilityTests : IDisposable
{
    private readonly DocumentVisualHarness _harness = new();

    public DocumentVisualEligibilityTests()
    {
        _harness.SeedProfile();
    }

    public void Dispose() => _harness.Dispose();

    private IQueryable<EligibleVisualUnit> Eligible(Guid owner, Guid? profileOverride = null)
        => OwnerDocumentVisualEligibility.EligibleUnits(
            _harness.Db.DocumentVisualUnits.AsNoTracking(),
            _harness.Db.DocumentVisualIndexes.AsNoTracking(),
            _harness.Db.FileItems.AsNoTracking(),
            owner,
            profileOverride ?? _harness.Profile.Id,
            _harness.Renderers.ActiveRenderProfileKeys);

    [Fact]
    public async Task An_Ordinary_Completed_Index_Is_Reachable()
    {
        // The positive control. Without it every assertion below could pass by
        // the join returning nothing for everybody.
        var file = _harness.SeedFile(_harness.OwnerA, "report.pdf");
        _harness.SeedVisualIndex(file, new[] { DocumentVisualHarness.Vector(1) });

        Assert.Equal(1, await Eligible(_harness.OwnerA).CountAsync());
    }

    [Fact]
    public async Task Another_Owners_Units_Are_Never_Reachable()
    {
        var mine = _harness.SeedFile(_harness.OwnerA, "mine.pdf");
        var theirs = _harness.SeedFile(_harness.OwnerB, "theirs.pdf");
        _harness.SeedVisualIndex(mine, new[] { DocumentVisualHarness.Vector(1) });
        _harness.SeedVisualIndex(theirs, new[] { DocumentVisualHarness.Vector(1) });

        var units = await Eligible(_harness.OwnerA).Select(r => r.File.Id).ToListAsync();

        Assert.Equal(new[] { mine.Id }, units);
    }

    [Fact]
    public async Task A_Vaulted_File_Is_Unreachable_With_Its_Visual_Rows_Intact()
    {
        var vault = _harness.SeedVault(_harness.OwnerA);
        var file = _harness.SeedFile(_harness.OwnerA, "secret.pdf", vaultId: vault.Id);
        _harness.SeedVisualIndex(file, new[] { DocumentVisualHarness.Vector(1) });

        // The rows exist. They are simply unreachable.
        Assert.True(await _harness.Db.DocumentVisualUnits.CountAsync() > 0);
        Assert.Empty(await Eligible(_harness.OwnerA).ToListAsync());
    }

    [Fact]
    public async Task A_Deleted_File_Is_Unreachable_On_The_Very_Next_Question()
    {
        var file = _harness.SeedFile(_harness.OwnerA, "notes.pdf");
        _harness.SeedVisualIndex(file, new[] { DocumentVisualHarness.Vector(1) });
        Assert.Equal(1, await Eligible(_harness.OwnerA).CountAsync());

        // Deleted NOW, with every derived row left in place — the state an
        // installation is in for as long as the sweeper is behind.
        var tracked = await _harness.Db.FileItems.SingleAsync(f => f.Id == file.Id);
        tracked.DeletedAt = DateTime.UtcNow;
        await _harness.Db.SaveChangesAsync();

        Assert.Empty(await Eligible(_harness.OwnerA).ToListAsync());
        Assert.True(await _harness.Db.DocumentVisualUnits.CountAsync() > 0);
    }

    [Fact]
    public async Task A_File_Excluded_From_The_Library_Is_Unreachable()
    {
        var file = _harness.SeedFile(
            _harness.OwnerA, "excluded.pdf", state: MediaLibraryState.Excluded);
        _harness.SeedVisualIndex(file, new[] { DocumentVisualHarness.Vector(1) });

        Assert.Empty(await Eligible(_harness.OwnerA).ToListAsync());
    }

    [Fact]
    public async Task A_Partially_Rendered_Index_Contributes_Nothing_At_All()
    {
        // TWENTY PAGES, PAGE 13 FAILED. The rows for the twelve that worked are
        // present — this is exactly the state a partial-publication bug leaves —
        // and none of them is a search result, because the index is not
        // `Completed`.
        var file = _harness.SeedFile(_harness.OwnerA, "contract.pdf");
        _harness.SeedVisualIndex(
            file,
            Enumerable.Range(0, 12).Select(i => DocumentVisualHarness.Vector(i)).ToArray(),
            status: AiArtifactStatuses.Failed);

        Assert.Equal(12, await _harness.Db.DocumentVisualUnits.CountAsync());
        Assert.Empty(await Eligible(_harness.OwnerA).ToListAsync());
    }

    [Fact]
    public async Task A_Skipped_Index_Contributes_Nothing()
    {
        var file = _harness.SeedFile(_harness.OwnerA, "huge.pdf");
        _harness.SeedVisualIndex(
            file, new[] { DocumentVisualHarness.Vector(1) }, status: AiArtifactStatuses.Skipped);

        Assert.Empty(await Eligible(_harness.OwnerA).ToListAsync());
    }

    [Fact]
    public async Task Replacing_A_Files_Bytes_Makes_Its_Visual_Index_Unreachable()
    {
        // THE INSTANT-INVALIDATION MECHANISM. No sweeper, no version column: the
        // index names the blob it rendered, and the join requires it to be the
        // file's current one.
        var file = _harness.SeedFile(_harness.OwnerA, "invoice.pdf");
        _harness.SeedVisualIndex(file, new[] { DocumentVisualHarness.Vector(1) });
        Assert.Equal(1, await Eligible(_harness.OwnerA).CountAsync());

        var replacement = _harness.SeedFile(_harness.OwnerA, "spare.pdf");
        var tracked = await _harness.Db.FileItems.SingleAsync(f => f.Id == file.Id);
        tracked.BlobObjectId = replacement.BlobObjectId;
        await _harness.Db.SaveChangesAsync();

        var reachable = await Eligible(_harness.OwnerA).Select(r => r.File.Id).ToListAsync();
        Assert.DoesNotContain(file.Id, reachable);
    }

    [Fact]
    public async Task A_Superseded_Render_Profile_Is_Unreachable()
    {
        // What a renderer upgrade costs: pixels drawn by an engine this
        // installation no longer runs stop being search results at once.
        var file = _harness.SeedFile(_harness.OwnerA, "slides.pdf");
        _harness.SeedVisualIndex(
            file, new[] { DocumentVisualHarness.Vector(1) },
            renderProfileKey: "pdfium-page-render-v0");

        Assert.Empty(await Eligible(_harness.OwnerA).ToListAsync());
    }

    [Fact]
    public async Task A_Different_Embedding_Profile_Is_Unreachable()
    {
        // Two profiles are two coordinate systems, and a cosine between them is
        // a number with no meaning. Matched exactly, never "the newest".
        var file = _harness.SeedFile(_harness.OwnerA, "budget.pdf");
        _harness.SeedVisualIndex(file, new[] { DocumentVisualHarness.Vector(1) });

        Assert.Empty(await Eligible(_harness.OwnerA, profileOverride: Guid.NewGuid()).ToListAsync());
    }

    [Fact]
    public async Task A_Forged_Owner_Column_Loses_To_The_Live_File()
    {
        // The denormalized owner on the derived row is a COPY. Corrupt it to
        // point at the asker and the live-file join still refuses, because
        // authority is the FileItem's owner and never the cached one.
        var theirs = _harness.SeedFile(_harness.OwnerB, "theirs.pdf");
        var index = _harness.SeedVisualIndex(theirs, new[] { DocumentVisualHarness.Vector(1) });

        var tracked = await _harness.Db.DocumentVisualIndexes.SingleAsync(i => i.Id == index.Id);
        tracked.OwnerUserId = _harness.OwnerA;
        await _harness.Db.SaveChangesAsync();

        Assert.Empty(await Eligible(_harness.OwnerA).ToListAsync());
    }

    [Fact]
    public async Task A_Shared_Blob_Does_Not_Share_Authority()
    {
        // Two people holding the same bytes is a storage fact. It does not
        // follow that either may read the other's document — and the visual
        // index of one must not become reachable through the other's file.
        var mine = _harness.SeedFile(_harness.OwnerA, "shared.pdf");
        var theirs = _harness.SeedFile(_harness.OwnerB, "shared.pdf");

        var tracked = await _harness.Db.FileItems.SingleAsync(f => f.Id == theirs.Id);
        tracked.BlobObjectId = mine.BlobObjectId;
        await _harness.Db.SaveChangesAsync();

        _harness.SeedVisualIndex(theirs, new[] { DocumentVisualHarness.Vector(1) },
            blobOverride: mine.BlobObjectId);

        Assert.Empty(await Eligible(_harness.OwnerA).ToListAsync());
        Assert.Equal(1, await Eligible(_harness.OwnerB).CountAsync());
    }

    [Fact]
    public async Task The_File_Level_Rule_Agrees_With_The_Unit_Level_One()
    {
        // Two spellings of one rule are two spellings that will drift. The
        // candidate-expansion path reads the file-level projection, so it is
        // compared against the unit-level join on the same adversarial fixture.
        var vault = _harness.SeedVault(_harness.OwnerA);
        var ok = _harness.SeedFile(_harness.OwnerA, "ok.pdf");
        var vaulted = _harness.SeedFile(_harness.OwnerA, "vaulted.pdf", vaultId: vault.Id);
        var deleted = _harness.SeedFile(_harness.OwnerA, "deleted.pdf", deleted: true);
        var other = _harness.SeedFile(_harness.OwnerB, "other.pdf");

        foreach (var file in new[] { ok, vaulted, deleted, other })
        {
            _harness.SeedVisualIndex(file, new[] { DocumentVisualHarness.Vector(1) });
        }

        var byUnit = (await Eligible(_harness.OwnerA).Select(r => r.File.Id).Distinct().ToListAsync())
            .OrderBy(id => id).ToList();
        var byFile = (await OwnerDocumentVisualEligibility.EligibleFileIds(
                _harness.Db.DocumentVisualIndexes.AsNoTracking(),
                _harness.Db.FileItems.AsNoTracking(),
                _harness.OwnerA,
                _harness.Profile.Id,
                _harness.Renderers.ActiveRenderProfileKeys)
            .ToListAsync()).OrderBy(id => id).ToList();

        Assert.Equal(new[] { ok.Id }, byUnit);
        Assert.Equal(byUnit, byFile);
    }
}
