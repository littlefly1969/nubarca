using NubArca.Api.Rag;
using NubArca.Api.Rag.Domains;
using NubArca.Api.Rag.Evaluation;
using Xunit;

namespace NubArca.Api.Tests.Rag;

// The golden evaluation, run against the corpus this release actually ships.
//
// It exists to make retrieval quality a NUMBER that a change moves, rather than
// an opinion. The floors below are deliberately set at what the current
// implementation achieves with a little headroom: a change that improves
// retrieval raises them, and a change that quietly costs a few points fails
// here instead of being noticed in production three weeks later.
//
// No generative model is involved. Asking an LLM to judge the answer would
// measure the LLM, cost money per run, and not be reproducible — so it could
// never be the thing a change is held against.
public sealed class RagGoldenEvaluationTests
{
    private static readonly Lazy<Task<RagEvaluationReport>> Report = new(() =>
        new RagEvaluator(RagTestHarness.ForProductHelp(RagTestHarness.ShippedProductHelp()))
            .EvaluateAsync(RagDomains.ProductHelp, RagGoldenSet.ProductHelp));

    [Fact]
    public async Task ProductHelp_MeetsItsRecallFloor()
    {
        var report = await Report.Value;

        Assert.Equal(RagGoldenSet.ProductHelp.Count, report.Queries);
        Assert.True(report.RecallAtFive >= 0.90,
            $"recall@5 fell to {report.RecallAtFive:0.000}");
    }

    [Fact]
    public async Task ProductHelp_MeetsItsRankingFloor()
    {
        var report = await Report.Value;

        // MRR, not just recall: a right answer at rank 5 is worse than at rank
        // 1, because the first chunk of context dominates what the model writes.
        Assert.True(report.MeanReciprocalRank >= 0.80,
            $"MRR fell to {report.MeanReciprocalRank:0.000}");
    }

    [Fact]
    public async Task Every_Golden_Question_Puts_Its_Expected_Source_In_The_Top_Three()
    {
        var report = await Report.Value;
        var failures = report.Outcomes.Where(o => !o.Passed).ToList();

        Assert.True(failures.Count == 0, string.Join("\n", failures.Select(f =>
            $"[{f.Case.Language}] \"{f.Case.Query}\" expected {string.Join("|", f.Case.ExpectedSourcePrefixes)} "
            + $"but the top sources were {(f.TopSources.Count == 0 ? "(nothing)" : string.Join(", ", f.TopSources))}")));
    }

    [Fact]
    public async Task The_Faces_Canary_Leads_With_User_Guidance()
    {
        // PERMANENT. Before Slice 1's retrieval rewrite this question returned
        // `docs/OPERATIONS.md` — a backup-and-restore runbook that happens to
        // mention faces, and is longer, so it won on word count. A technical
        // reference to `face_previews` is not an acceptable answer to "how do I
        // use faces?" either.
        var retriever = RagTestHarness.ForProductHelp(RagTestHarness.ShippedProductHelp());

        var result = await retriever.RetrieveAsync(new RagQuery(
            RagDomainKey.ProductHelp, RagGoldenSet.FacesCanary, 6, 12000));

        Assert.True(result.HasStrongEvidence);
        Assert.StartsWith("docs/help/faces", result.Evidence[0].Path, StringComparison.Ordinal);
        Assert.Equal(RagSourceKinds.UserGuide, result.Evidence[0].SourceKind);
        Assert.Equal(RagIntents.HowTo, result.Evidence[0].Intent);

        // The real workflow, not a paragraph that merely contains the word.
        var context = string.Join("\n", result.Evidence.Select(e => e.Text));
        Assert.Contains("Gruppi suggeriti", context, StringComparison.Ordinal);
        Assert.Contains("Assegna nome", context, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Technical_Documents_Do_Not_Lead_A_How_To_Answer()
    {
        var report = await Report.Value;

        // The half of the measurement recall cannot see: the right document
        // being present somewhere is not the same as the wrong one not leading.
        Assert.DoesNotContain(report.Outcomes, o => o.ForbiddenAtTop);
    }

    [Fact]
    public async Task The_Golden_Set_Covers_Both_Interface_Languages()
    {
        var report = await Report.Value;

        // The interface is Italian and much of the documentation is English. A
        // golden set in one language would keep passing while the other half of
        // the product got worse.
        Assert.Contains(report.Outcomes, o => o.Case.Language == RagLanguages.Italian);
        Assert.Contains(report.Outcomes, o => o.Case.Language == RagLanguages.English);
        Assert.True(report.Outcomes.Count(o => o.Case.Language == RagLanguages.Italian) >= 5);
        Assert.True(report.Outcomes.Count(o => o.Case.Language == RagLanguages.English) >= 5);
    }

    [Fact]
    public void Every_Golden_Expectation_Names_A_Source_The_Domain_Can_Actually_Return()
    {
        // A golden case pointing at a file nobody classified would fail forever
        // for a reason that has nothing to do with ranking.
        var approved = NubArca.Api.Rag.ProductHelp.ProductHelpSources.Manifest
            .Select(s => s.Path).ToList();

        foreach (var golden in RagGoldenSet.ProductHelp)
        {
            Assert.All(golden.ExpectedSourcePrefixes, prefix => Assert.True(
                approved.Any(p => p.StartsWith(prefix, StringComparison.Ordinal)),
                $"golden case \"{golden.Query}\" expects {prefix}, which is not in the Product Help manifest"));
        }
    }

    [Fact]
    public void The_Repository_Golden_Set_Names_Files_That_Exist()
    {
        // The repository set is measured with `rag evaluate` against a real
        // index, which a fast unit test has no business building. What IS worth
        // asserting cheaply is that every expectation still names a real file:
        // a rename that silently invalidated half the set would otherwise be
        // discovered only by an operator running the evaluation by hand.
        var root = RagTestHarness.RepositoryRoot();

        foreach (var golden in RagGoldenSet.Repository)
        {
            Assert.All(golden.ExpectedSourcePrefixes, prefix => Assert.True(
                File.Exists(Path.Combine(root, prefix.Replace('/', Path.DirectorySeparatorChar))),
                $"repository golden case \"{golden.Query}\" expects {prefix}, which no longer exists"));
        }
    }
}
