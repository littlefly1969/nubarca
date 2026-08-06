namespace NubArca.Api.Aesthetics;

// The analysis capabilities the HumanAesExpert model family exposes. Only
// `expert_scores` is ENABLED in this slice; the other three are defined so the
// domain/API/sidecar contract is ready, but they are gated OFF by configuration
// (HumanAesExpert:Allow*) until each is separately benchmarked and validated.
//
// These stable string keys are the contract between the API, the durable job,
// and the Python sidecar. They are NEVER localized and NEVER changed in place
// (a new capability meaning = a new key).
public static class AestheticCapabilities
{
    // Expert head: 12 numeric aesthetic sub-dimension scores. The ONLY capability
    // enabled by default in this slice.
    public const string ExpertScores = "expert_scores";

    // Regression "score" head: a single overall score (model.score()). Prepared,
    // disabled by default.
    public const string ScoreHead = "score_head";

    // MetaVoter: aggregates the LM + Regression + Expert heads (model.run_metavoter()).
    // Prepared, disabled by default (slow: ~2x inference).
    public const string MetaVoter = "meta_voter";

    // LM head textual assessment / annotations. Prepared, disabled by default.
    public const string TextAssessment = "text_assessment";

    public static readonly IReadOnlyList<string> All = new[]
    {
        ExpertScores, ScoreHead, MetaVoter, TextAssessment,
    };

    public static bool IsKnown(string? capability) =>
        capability is ExpertScores or ScoreHead or MetaVoter or TextAssessment;
}
