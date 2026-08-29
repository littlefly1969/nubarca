using Microsoft.EntityFrameworkCore;
using NubArca.Api.Ai.DocumentVisual;
using NubArca.Api.Domain.Ai;
using Xunit;

namespace NubArca.Api.Tests.Ai.DocumentVisual;

// THE SEMANTIC HALF OF OWNER ISOLATION, and the aggregation rule.
//
// Lexical isolation is easy to believe: the corpus is built from one owner's
// rows. The vector path is where the plausible-looking mistake lives — a global
// nearest-neighbour search with `WHERE OwnerUserId = …` reads like an
// owner-prefiltered search and is not one, because the ranking happens over
// everybody's vectors and the predicate filters what it surfaces.
//
// So the fixture below gives the OTHER owner vectors that are strictly closer to
// the query than anything the asker has. Under a filter-after-search
// implementation the asker gets fewer, worse results — or nothing at all.
public sealed class DocumentVisualRetrievalTests : IDisposable
{
    private readonly DocumentVisualHarness _harness = new();

    public DocumentVisualRetrievalTests() => _harness.SeedProfile();

    public void Dispose() => _harness.Dispose();

    private static float[] Query => DocumentVisualHarness.Vector(7);

    private Task<DocumentVisualRetrievalResult> SearchAsync(
        Guid owner, int units = 20, int files = 8, DocumentVisualOptions? options = null)
        => _harness.BuildRetriever(Query, options)
            .RetrieveAsync(new DocumentVisualQuery(owner, "a question about a table", units, files));

    [Fact]
    public async Task Owner_Filtering_Precedes_The_Limit()
    {
        // ADVERSARIAL. Owner B holds thirty pages that are all EXACTLY the query
        // vector; owner A holds one page that merely resembles it. A `LIMIT 5`
        // over an unfiltered ranking returns five of B's — and after a
        // post-filter, nothing.
        var mine = _harness.SeedFile(_harness.OwnerA, "mine.pdf");
        _harness.SeedVisualIndex(mine, new[] { DocumentVisualHarness.Vector(7, 0.9f) });

        var theirs = _harness.SeedFile(_harness.OwnerB, "theirs.pdf");
        _harness.SeedVisualIndex(
            theirs, Enumerable.Repeat(DocumentVisualHarness.Vector(7), 30).ToArray());

        var result = await SearchAsync(_harness.OwnerA, units: 5, files: 5);

        Assert.True(result.IsAvailable, result.Reason);
        Assert.NotEmpty(result.Hits);
        Assert.Equal(new[] { mine.Id }, result.CandidateFileIds);
    }

    [Fact]
    public async Task Vaulted_And_Deleted_Pages_Are_Not_Candidates()
    {
        var vault = _harness.SeedVault(_harness.OwnerA);
        var ok = _harness.SeedFile(_harness.OwnerA, "ok.pdf");
        var vaulted = _harness.SeedFile(_harness.OwnerA, "vaulted.pdf", vaultId: vault.Id);
        var deleted = _harness.SeedFile(_harness.OwnerA, "deleted.pdf", deleted: true);

        // The two that must not appear are the two that match the query PERFECTLY.
        _harness.SeedVisualIndex(ok, new[] { DocumentVisualHarness.Vector(7, 0.7f) });
        _harness.SeedVisualIndex(vaulted, new[] { DocumentVisualHarness.Vector(7) });
        _harness.SeedVisualIndex(deleted, new[] { DocumentVisualHarness.Vector(7) });

        var result = await SearchAsync(_harness.OwnerA);

        Assert.Equal(new[] { ok.Id }, result.CandidateFileIds);
        // Their embedding rows are still there.
        Assert.Equal(3, await _harness.Db.DocumentVisualEmbeddings.CountAsync());
    }

    [Fact]
    public async Task A_Long_Document_Does_Not_Win_By_Length()
    {
        // AGGREGATION BY BEST RANK, NOT BY SUM. A hundred-page report whose
        // every page is mildly relevant must not outrank a one-page invoice that
        // is the answer — length is the one property a visual embedding cannot
        // see, and summing page scores would make it the strongest signal there
        // is.
        var report = _harness.SeedFile(_harness.OwnerA, "long-report.pdf");
        _harness.SeedVisualIndex(
            report, Enumerable.Repeat(DocumentVisualHarness.Vector(7, 0.55f), 100).ToArray());

        var invoice = _harness.SeedFile(_harness.OwnerA, "invoice.pdf");
        _harness.SeedVisualIndex(invoice, new[] { DocumentVisualHarness.Vector(7) });

        var result = await SearchAsync(_harness.OwnerA, units: 200, files: 5);

        Assert.Equal(invoice.Id, result.CandidateFileIds[0]);
    }

    [Fact]
    public async Task Candidate_Files_Are_Bounded()
    {
        for (var i = 0; i < 20; i++)
        {
            var file = _harness.SeedFile(_harness.OwnerA, $"doc-{i}.pdf");
            _harness.SeedVisualIndex(file, new[] { DocumentVisualHarness.Vector(7, 0.5f + (i * 0.02f)) });
        }

        var result = await SearchAsync(_harness.OwnerA, units: 100, files: 3);

        Assert.Equal(3, result.CandidateFileIds.Count);
    }

    [Fact]
    public async Task A_Corpus_Past_The_Exact_Search_Ceiling_Is_Refused_Not_Truncated()
    {
        // NEVER AN ARBITRARY PREFIX. Ranking part of somebody's library and
        // presenting the result as their documents is a wrong answer with a
        // confident tone; reporting the visual path unavailable is legible, and
        // the text pass answers the question regardless.
        var file = _harness.SeedFile(_harness.OwnerA, "many-pages.pdf");
        _harness.SeedVisualIndex(
            file, Enumerable.Repeat(DocumentVisualHarness.Vector(7), 6).ToArray());

        var options = new DocumentVisualOptions
        {
            Enabled = true,
            MaxVisualUnitsPerOwnerExactFallback = 5,
        };

        var result = await SearchAsync(_harness.OwnerA, options: options);

        Assert.False(result.IsAvailable);
        Assert.Equal(DocumentVisualReasons.CorpusTooLarge, result.Reason);
        Assert.Empty(result.CandidateFileIds);
    }

    [Fact]
    public async Task Exactly_At_The_Ceiling_Still_Answers()
    {
        var file = _harness.SeedFile(_harness.OwnerA, "many-pages.pdf");
        _harness.SeedVisualIndex(
            file, Enumerable.Repeat(DocumentVisualHarness.Vector(7), 5).ToArray());

        var result = await SearchAsync(_harness.OwnerA, options: new DocumentVisualOptions
        {
            Enabled = true,
            MaxVisualUnitsPerOwnerExactFallback = 5,
        });

        Assert.True(result.IsAvailable, result.Reason);
    }

    [Fact]
    public async Task Disabled_Reports_Unavailable_And_Reads_Nothing()
    {
        var file = _harness.SeedFile(_harness.OwnerA, "doc.pdf");
        _harness.SeedVisualIndex(file, new[] { DocumentVisualHarness.Vector(7) });

        var result = await SearchAsync(
            _harness.OwnerA, options: new DocumentVisualOptions { Enabled = false });

        Assert.False(result.IsAvailable);
        Assert.Equal(DocumentVisualReasons.Disabled, result.Reason);
    }

    [Fact]
    public async Task No_Owner_Is_Refused_Rather_Than_Answered_From_Everybody()
    {
        var result = await SearchAsync(Guid.Empty);

        Assert.False(result.IsAvailable);
        Assert.Equal(NubArca.Api.Rag.RagFailureReasons.OwnerRequired, result.Reason);
    }

    [Fact]
    public async Task A_Malformed_Embedding_Row_Is_Skipped_Not_Guessed_At()
    {
        var good = _harness.SeedFile(_harness.OwnerA, "good.pdf");
        _harness.SeedVisualIndex(good, new[] { DocumentVisualHarness.Vector(7, 0.8f) });

        var broken = _harness.SeedFile(_harness.OwnerA, "broken.pdf");
        _harness.SeedVisualIndex(broken, new[] { DocumentVisualHarness.Vector(7) });

        // Corrupt the strongest match's bytes. A corruption is repaired by
        // re-embedding, not by failing somebody's question — and never by
        // reshaping the blob into something that ranks.
        var brokenIndexId = await _harness.Db.DocumentVisualIndexes
            .Where(i => i.FileItemId == broken.Id).Select(i => i.Id).SingleAsync();
        var unitId = await _harness.Db.DocumentVisualUnits
            .Where(u => u.DocumentVisualIndexId == brokenIndexId).Select(u => u.Id).SingleAsync();
        var embedding = await _harness.Db.DocumentVisualEmbeddings
            .SingleAsync(e => e.DocumentVisualUnitId == unitId);
        embedding.EmbeddingBytes = new byte[] { 1, 2, 3 };
        await _harness.Db.SaveChangesAsync();

        var result = await SearchAsync(_harness.OwnerA);

        Assert.True(result.IsAvailable, result.Reason);
        Assert.Equal(new[] { good.Id }, result.CandidateFileIds);
    }

    [Fact]
    public async Task Results_Are_Deterministic_Across_Runs()
    {
        for (var i = 0; i < 6; i++)
        {
            var file = _harness.SeedFile(_harness.OwnerA, $"doc-{i}.pdf");
            // Deliberate TIES: RRF and the evidence gate above this are unstable
            // if equal scores come back in a different order each run.
            _harness.SeedVisualIndex(file, new[] { DocumentVisualHarness.Vector(7, 0.6f) });
        }

        var first = await SearchAsync(_harness.OwnerA, units: 10, files: 3);
        for (var run = 0; run < 5; run++)
        {
            var again = await SearchAsync(_harness.OwnerA, units: 10, files: 3);
            Assert.Equal(first.CandidateFileIds, again.CandidateFileIds);
        }
    }

    [Fact]
    public async Task A_Hit_Carries_No_Pixels_And_No_Vector()
    {
        var file = _harness.SeedFile(_harness.OwnerA, "doc.pdf");
        _harness.SeedVisualIndex(file, new[] { DocumentVisualHarness.Vector(7) });

        var result = await SearchAsync(_harness.OwnerA);
        var hit = Assert.Single(result.Hits);

        // The shape asserted as a TYPE. A future field carrying a page, a
        // thumbnail or an embedding would fail here before it could be wired to
        // anything downstream.
        var properties = typeof(DocumentVisualHit).GetProperties().Select(p => p.Name).ToArray();
        Assert.Equal(
            new[] { "FileItemId", "Mode", "Rank", "Score", "VisualUnitId" },
            properties.OrderBy(n => n, StringComparer.Ordinal).ToArray());
        Assert.Equal(1, hit.Rank);
    }

    [Fact]
    public async Task Without_A_Promoted_Late_Profile_The_Dense_Order_Stands()
    {
        // The shipped state of this release: a seam and a harness, no production
        // late-interaction model. It must be indistinguishable from "no late
        // interaction exists" as far as results are concerned.
        var a = _harness.SeedFile(_harness.OwnerA, "a.pdf");
        var b = _harness.SeedFile(_harness.OwnerA, "b.pdf");
        _harness.SeedVisualIndex(a, new[] { DocumentVisualHarness.Vector(7) });
        _harness.SeedVisualIndex(b, new[] { DocumentVisualHarness.Vector(7, 0.5f) });

        var result = await SearchAsync(_harness.OwnerA, options: new DocumentVisualOptions
        {
            Enabled = true,
            LateInteractionEnabled = true,
            LateProfileKey = "",
        });

        Assert.Equal(DocumentVisualModes.DenseExact, result.Mode);
        Assert.Equal(new[] { a.Id, b.Id }, result.CandidateFileIds);
    }
}
