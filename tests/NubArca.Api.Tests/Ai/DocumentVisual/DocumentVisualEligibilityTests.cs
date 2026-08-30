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
    private readonly AiProfile _extraction;

    public DocumentVisualEligibilityTests()
    {
        _harness.SeedProfile();
        _extraction = _harness.SeedExtractionProfile();
    }

    public void Dispose() => _harness.Dispose();

    /// A file with a CURRENT, COMPLETED extraction of its current bytes.
    ///
    /// Every fixture here starts from an answerable file, including the ones
    /// that must NOT be reachable — so each test isolates the single condition
    /// it is named for rather than passing because the extraction was missing
    /// too.
    private FileItem Answerable(
        Guid owner, string name, Guid? vaultId = null, bool deleted = false,
        MediaLibraryState state = MediaLibraryState.Active)
    {
        var file = _harness.SeedFile(owner, name, vaultId, deleted, state);
        _harness.SeedExtraction(file, _extraction);
        return file;
    }

    private IQueryable<EligibleVisualUnit> Eligible(Guid owner, Guid? profileOverride = null)
        => OwnerDocumentVisualEligibility.EligibleUnits(
            _harness.Db.DocumentVisualUnits.AsNoTracking(),
            _harness.Db.DocumentVisualIndexes.AsNoTracking(),
            _harness.Db.DocumentTexts.AsNoTracking(),
            _harness.Db.FileItems.AsNoTracking(),
            owner,
            profileOverride ?? _harness.Profile.Id,
            _harness.Renderers.ActiveRenderProfileKeys);

    [Fact]
    public async Task An_Ordinary_Completed_Index_Is_Reachable()
    {
        // The positive control. Without it every assertion below could pass by
        // the join returning nothing for everybody.
        var file = Answerable(_harness.OwnerA, "report.pdf");
        _harness.SeedVisualIndex(file, new[] { DocumentVisualHarness.Vector(1) });

        Assert.Equal(1, await Eligible(_harness.OwnerA).CountAsync());
    }

    [Fact]
    public async Task Another_Owners_Units_Are_Never_Reachable()
    {
        var mine = Answerable(_harness.OwnerA, "mine.pdf");
        var theirs = Answerable(_harness.OwnerB, "theirs.pdf");
        _harness.SeedVisualIndex(mine, new[] { DocumentVisualHarness.Vector(1) });
        _harness.SeedVisualIndex(theirs, new[] { DocumentVisualHarness.Vector(1) });

        var units = await Eligible(_harness.OwnerA).Select(r => r.File.Id).ToListAsync();

        Assert.Equal(new[] { mine.Id }, units);
    }

    [Fact]
    public async Task A_Vaulted_File_Is_Unreachable_With_Its_Visual_Rows_Intact()
    {
        var vault = _harness.SeedVault(_harness.OwnerA);
        var file = Answerable(_harness.OwnerA, "secret.pdf", vaultId: vault.Id);
        _harness.SeedVisualIndex(file, new[] { DocumentVisualHarness.Vector(1) });

        // The rows exist. They are simply unreachable.
        Assert.True(await _harness.Db.DocumentVisualUnits.CountAsync() > 0);
        Assert.Empty(await Eligible(_harness.OwnerA).ToListAsync());
    }

    [Fact]
    public async Task A_Deleted_File_Is_Unreachable_On_The_Very_Next_Question()
    {
        var file = Answerable(_harness.OwnerA, "notes.pdf");
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
        var file = Answerable(
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
        var file = Answerable(_harness.OwnerA, "contract.pdf");
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
        var file = Answerable(_harness.OwnerA, "huge.pdf");
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
        var file = Answerable(_harness.OwnerA, "invoice.pdf");
        _harness.SeedVisualIndex(file, new[] { DocumentVisualHarness.Vector(1) });
        Assert.Equal(1, await Eligible(_harness.OwnerA).CountAsync());

        var replacement = Answerable(_harness.OwnerA, "spare.pdf");
        var tracked = await _harness.Db.FileItems.SingleAsync(f => f.Id == file.Id);
        tracked.BlobObjectId = replacement.BlobObjectId;
        // The TEXT side keeps up: a re-extraction of the new bytes is current
        // and complete. So the only thing still describing the old content is
        // the VISUAL index, which is exactly the condition under test.
        var extraction = await _harness.Db.DocumentTexts.SingleAsync(d => d.FileItemId == file.Id);
        extraction.SourceBlobObjectId = replacement.BlobObjectId;
        await _harness.Db.SaveChangesAsync();

        var reachable = await Eligible(_harness.OwnerA).Select(r => r.File.Id).ToListAsync();
        Assert.DoesNotContain(file.Id, reachable);
    }

    [Fact]
    public async Task A_Superseded_Render_Profile_Is_Unreachable()
    {
        // What a renderer upgrade costs: pixels drawn by an engine this
        // installation no longer runs stop being search results at once.
        var file = Answerable(_harness.OwnerA, "slides.pdf");
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
        var file = Answerable(_harness.OwnerA, "budget.pdf");
        _harness.SeedVisualIndex(file, new[] { DocumentVisualHarness.Vector(1) });

        Assert.Empty(await Eligible(_harness.OwnerA, profileOverride: Guid.NewGuid()).ToListAsync());
    }

    [Fact]
    public async Task A_Forged_Owner_Column_Loses_To_The_Live_File()
    {
        // The denormalized owner on the derived row is a COPY. Corrupt it to
        // point at the asker and the live-file join still refuses, because
        // authority is the FileItem's owner and never the cached one.
        var theirs = Answerable(_harness.OwnerB, "theirs.pdf");
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
        var mine = Answerable(_harness.OwnerA, "shared.pdf");
        var theirs = Answerable(_harness.OwnerB, "shared.pdf");

        var tracked = await _harness.Db.FileItems.SingleAsync(f => f.Id == theirs.Id);
        tracked.BlobObjectId = mine.BlobObjectId;
        // Owner B's own extraction follows their file onto the shared bytes, so
        // B is fully answerable and the question is purely whether A can reach
        // it through the deduplicated blob.
        var extraction = await _harness.Db.DocumentTexts.SingleAsync(d => d.FileItemId == theirs.Id);
        extraction.SourceBlobObjectId = mine.BlobObjectId;
        await _harness.Db.SaveChangesAsync();

        _harness.SeedVisualIndex(theirs, new[] { DocumentVisualHarness.Vector(1) },
            blobOverride: mine.BlobObjectId);

        Assert.Empty(await Eligible(_harness.OwnerA).ToListAsync());
        Assert.Equal(1, await Eligible(_harness.OwnerB).CountAsync());
    }

    // ---- the text derivation is authority, and the visual index derives from it

    [Fact]
    public async Task A_Superseded_Extraction_Makes_The_Visual_Index_Unreachable()
    {
        // THE CASE THIS RULE EXISTS FOR. Everything about the visual side is
        // still perfect: a completed index, the current blob, active profiles,
        // a live eligible file. What changed is that the file's authoritative
        // READING was superseded — an extractor upgrade, a withdrawn
        // interpretation — and a derivative of a document must not outlive the
        // authority it derives from.
        var file = Answerable(_harness.OwnerA, "contract.pdf");
        _harness.SeedVisualIndex(file, new[] { DocumentVisualHarness.Vector(1) });
        Assert.Equal(1, await Eligible(_harness.OwnerA).CountAsync());

        var extraction = await _harness.Db.DocumentTexts.SingleAsync(d => d.FileItemId == file.Id);
        extraction.IsCurrent = false;
        await _harness.Db.SaveChangesAsync();

        Assert.Empty(await Eligible(_harness.OwnerA).ToListAsync());
        // The visual rows are all still there. This is a live join, not cleanup.
        Assert.True(await _harness.Db.DocumentVisualUnits.CountAsync() > 0);
    }

    [Theory]
    [InlineData(AiArtifactStatuses.Failed)]
    [InlineData(AiArtifactStatuses.Skipped)]
    public async Task A_Current_But_Unsuccessful_Extraction_Is_Not_Authority(string status)
    {
        // A re-extraction that FAILED or was SKIPPED leaves a row that is
        // current and answers nothing. "Current" alone is not the test.
        var file = Answerable(_harness.OwnerA, "scan.pdf");
        _harness.SeedVisualIndex(file, new[] { DocumentVisualHarness.Vector(1) });

        var extraction = await _harness.Db.DocumentTexts.SingleAsync(d => d.FileItemId == file.Id);
        extraction.Status = status;
        await _harness.Db.SaveChangesAsync();

        Assert.Empty(await Eligible(_harness.OwnerA).ToListAsync());
    }

    [Fact]
    public async Task An_Extraction_Of_The_Previous_Bytes_Cannot_Vouch_For_The_Current_Ones()
    {
        // The file's content was replaced and its VISUAL index was rebuilt for
        // the new bytes, but the text side has not caught up: the current
        // extraction still describes what the file used to be. Nothing here
        // reads that document authoritatively yet, so it introduces nothing.
        var file = Answerable(_harness.OwnerA, "invoice.pdf");
        var replacement = Answerable(_harness.OwnerA, "spare.pdf");

        var tracked = await _harness.Db.FileItems.SingleAsync(f => f.Id == file.Id);
        tracked.BlobObjectId = replacement.BlobObjectId;
        await _harness.Db.SaveChangesAsync();

        _harness.SeedVisualIndex(
            file, new[] { DocumentVisualHarness.Vector(1) },
            blobOverride: replacement.BlobObjectId);

        Assert.DoesNotContain(
            file.Id, await Eligible(_harness.OwnerA).Select(r => r.File.Id).ToListAsync());
    }

    [Fact]
    public async Task A_Forged_Extraction_Cannot_Restore_Visual_Eligibility()
    {
        // Three ways to fake an authoritative reading, none of which works: an
        // extraction owned by somebody else, one belonging to a DIFFERENT file
        // of the same owner, and one describing different bytes. Each is
        // current and completed, which is precisely what makes them worth
        // refusing individually.
        var file = Answerable(_harness.OwnerA, "target.pdf");
        // Deliberately NOT answerable: the "wrong file" forgery below claims to
        // be this file's current reading, and a file that already had one could
        // not hold a second (the filtered unique index sees to that).
        var other = _harness.SeedFile(_harness.OwnerA, "other.pdf");
        _harness.SeedVisualIndex(file, new[] { DocumentVisualHarness.Vector(1) });

        var genuine = await _harness.Db.DocumentTexts.SingleAsync(d => d.FileItemId == file.Id);
        _harness.Db.DocumentTexts.Remove(genuine);
        await _harness.Db.SaveChangesAsync();
        Assert.Empty(await Eligible(_harness.OwnerA).ToListAsync());

        // Wrong owner on an otherwise perfect extraction of this very file.
        var forgedOwner = _harness.SeedExtraction(
            file, _extraction, ownerOverride: _harness.OwnerB);
        Assert.Empty(await Eligible(_harness.OwnerA).ToListAsync());
        _harness.Db.DocumentTexts.Remove(forgedOwner);
        await _harness.Db.SaveChangesAsync();

        // Right owner, wrong file: `other`'s reading does not vouch for `file`.
        var wrongFile = _harness.SeedExtraction(file, _extraction, fileOverride: other.Id);
        Assert.DoesNotContain(
            file.Id, await Eligible(_harness.OwnerA).Select(r => r.File.Id).ToListAsync());
        _harness.Db.DocumentTexts.Remove(wrongFile);
        await _harness.Db.SaveChangesAsync();

        // Right owner, right file, wrong bytes.
        _harness.SeedExtraction(file, _extraction, blobOverride: Guid.NewGuid());
        Assert.Empty(await Eligible(_harness.OwnerA).ToListAsync());
    }

    [Fact]
    public async Task A_Replacement_Current_Extraction_Restores_Eligibility()
    {
        // THE POSITIVE HALF, without which every assertion above could pass by
        // the rule refusing everything. Supersede the reading, confirm the file
        // goes dark, then publish a NEW current completed extraction of the same
        // live bytes — as an extractor upgrade would — and it comes back.
        var file = Answerable(_harness.OwnerA, "manual.pdf");
        _harness.SeedVisualIndex(file, new[] { DocumentVisualHarness.Vector(1) });

        var superseded = await _harness.Db.DocumentTexts.SingleAsync(d => d.FileItemId == file.Id);
        superseded.IsCurrent = false;
        await _harness.Db.SaveChangesAsync();
        Assert.Empty(await Eligible(_harness.OwnerA).ToListAsync());

        // A second extraction profile, exactly as a re-read by a better parser
        // would produce. The superseded row stays as provenance.
        _harness.SeedExtraction(file, _harness.SeedExtractionProfile("doc-upgraded-v2"));

        Assert.Equal(1, await Eligible(_harness.OwnerA).CountAsync());
        Assert.Equal(2, await _harness.Db.DocumentTexts.CountAsync(d => d.FileItemId == file.Id));
    }

    [Fact]
    public async Task The_File_Level_Rule_Enforces_The_Text_Requirement_Too()
    {
        // The candidate-expansion projection is the one a visual hit actually
        // travels through, so the rule has to hold there as well — otherwise a
        // file with no authoritative reading is still introduced as a candidate
        // and still displaces another document from a bounded list.
        var answerable = Answerable(_harness.OwnerA, "answerable.pdf");
        var superseded = Answerable(_harness.OwnerA, "superseded.pdf");
        _harness.SeedVisualIndex(answerable, new[] { DocumentVisualHarness.Vector(1) });
        _harness.SeedVisualIndex(superseded, new[] { DocumentVisualHarness.Vector(2) });

        var stale = await _harness.Db.DocumentTexts.SingleAsync(d => d.FileItemId == superseded.Id);
        stale.IsCurrent = false;
        await _harness.Db.SaveChangesAsync();

        var byFile = await OwnerDocumentVisualEligibility.EligibleFileIds(
            _harness.Db.DocumentVisualIndexes.AsNoTracking(),
            _harness.Db.DocumentTexts.AsNoTracking(),
            _harness.Db.FileItems.AsNoTracking(),
            _harness.OwnerA,
            _harness.Profile.Id,
            _harness.Renderers.ActiveRenderProfileKeys).ToListAsync();

        Assert.Equal(new[] { answerable.Id }, byFile);
    }

    [Fact]
    public async Task The_File_Level_Rule_Agrees_With_The_Unit_Level_One()
    {
        // Two spellings of one rule are two spellings that will drift. The
        // candidate-expansion path reads the file-level projection, so it is
        // compared against the unit-level join on the same adversarial fixture.
        var vault = _harness.SeedVault(_harness.OwnerA);
        var ok = Answerable(_harness.OwnerA, "ok.pdf");
        var vaulted = Answerable(_harness.OwnerA, "vaulted.pdf", vaultId: vault.Id);
        var deleted = Answerable(_harness.OwnerA, "deleted.pdf", deleted: true);
        var other = Answerable(_harness.OwnerB, "other.pdf");

        foreach (var file in new[] { ok, vaulted, deleted, other })
        {
            _harness.SeedVisualIndex(file, new[] { DocumentVisualHarness.Vector(1) });
        }

        var byUnit = (await Eligible(_harness.OwnerA).Select(r => r.File.Id).Distinct().ToListAsync())
            .OrderBy(id => id).ToList();
        var byFile = (await OwnerDocumentVisualEligibility.EligibleFileIds(
                _harness.Db.DocumentVisualIndexes.AsNoTracking(),
                _harness.Db.DocumentTexts.AsNoTracking(),
                _harness.Db.FileItems.AsNoTracking(),
                _harness.OwnerA,
                _harness.Profile.Id,
                _harness.Renderers.ActiveRenderProfileKeys)
            .ToListAsync()).OrderBy(id => id).ToList();

        Assert.Equal(new[] { ok.Id }, byUnit);
        Assert.Equal(byUnit, byFile);
    }
}
