namespace NubArca.Api.Aesthetics;

// The AUTHORITATIVE Expert-head metric mapping, verified against the pinned
// official checkpoint code (KlingTeam/HumanAesExpert-1B, MIT; the original
// KwaiVGI/HumanAesExpert-1B redirects here) at revision
// b8f7ee3f3a1217ecd331fd6d57b6959f5c0da183.
//
// Source of truth: modeling_internvl_chat.py, expert_score(), which returns a
// 12-element tensor plus `{name: score}` using this exact `names` list and
// ORDER (indices 0..11):
//
//   0  Facial Brightness
//   1  Facial Feature Clarity
//   2  Facial Skin Tone
//   3  Facial Structure
//   4  Facial Contour Clarity
//   5  Facial Aesthetic Score            (parent; FFN over the 5 facial dims)
//   6  Outfit
//   7  Body Shape
//   8  Looks
//   9  Environment
//   10 General Appearance Aesthetic Score (parent; FFN over outfit/body/looks)
//   11 Comprehensive Aesthetic Score      (overall; FFN over env + 2 parents)
//
// Scale: each dimension is in the range [0,1] — VERIFIED from the pinned code,
// not just the paper: modeling_qwen.py Expert_Head.forward() ends with
// `return F.sigmoid(pooled_expert_scores)`, so every output is a sigmoid in
// [0,1] (consistent with arXiv:2503.23907's MOS in [0,1]). A real inference on
// the pinned checkpoint returned all 12 values within [0,1]. We persist the
// model's own [0,1] value plus the declared
// range on every AestheticMetric row so a future rescale is unambiguous.
//
// The STABLE INTERNAL KEYS below are snake_case of the model's own names. They
// are the contract keys used by the sidecar response, the durable job, and the
// DB. Curated, localized display labels live SEPARATELY in the frontend i18n —
// never derived from these keys at the API boundary.
public static class AestheticMetricCatalog
{
    public const double ScaleMin = 0.0;
    public const double ScaleMax = 1.0;

    // Metric version for the expert_scores capability persisted on each row.
    // Bump only if the mapping/scale semantics change (they must not change in
    // place).
    public const int ExpertScoresVersion = 1;

    // Metric groups (used only to organize the UI; not model-defined).
    public const string GroupFace = "face";
    public const string GroupAppearance = "appearance";
    public const string GroupEnvironment = "environment";
    public const string GroupOverall = "overall";

    public sealed record MetricDefinition(
        int Index,
        string Key,
        string Group,
        string ModelName,
        bool IsDerived);

    // Ordered exactly as the model's Expert-head output tensor.
    public static readonly IReadOnlyList<MetricDefinition> ExpertScores = new[]
    {
        new MetricDefinition(0, "facial_brightness", GroupFace, "Facial Brightness", false),
        new MetricDefinition(1, "facial_feature_clarity", GroupFace, "Facial Feature Clarity", false),
        new MetricDefinition(2, "facial_skin_tone", GroupFace, "Facial Skin Tone", false),
        new MetricDefinition(3, "facial_structure", GroupFace, "Facial Structure", false),
        new MetricDefinition(4, "facial_contour_clarity", GroupFace, "Facial Contour Clarity", false),
        new MetricDefinition(5, "facial_aesthetic", GroupFace, "Facial Aesthetic Score", true),
        new MetricDefinition(6, "outfit", GroupAppearance, "Outfit", false),
        new MetricDefinition(7, "body_shape", GroupAppearance, "Body Shape", false),
        new MetricDefinition(8, "looks", GroupAppearance, "Looks", false),
        new MetricDefinition(9, "environment", GroupEnvironment, "Environment", false),
        new MetricDefinition(10, "general_appearance_aesthetic", GroupAppearance, "General Appearance Aesthetic Score", true),
        new MetricDefinition(11, "overall_aesthetic", GroupOverall, "Comprehensive Aesthetic Score", true),
    };

    // The single dimension surfaced as the item's headline score.
    public const string OverallKey = "overall_aesthetic";

    private static readonly IReadOnlyDictionary<string, MetricDefinition> ByKey =
        ExpertScores.ToDictionary(m => m.Key, m => m);

    public static bool TryGet(string key, out MetricDefinition definition) =>
        ByKey.TryGetValue(key, out definition!);

    public static string GroupFor(string key) =>
        ByKey.TryGetValue(key, out var def) ? def.Group : GroupOverall;

    public static bool IsExpertScoreKey(string key) => ByKey.ContainsKey(key);

    // The full expected key set for a completed expert_scores run (used by the
    // strict sidecar-response validator: exactly these 12 keys, no more/less).
    public static readonly IReadOnlyList<string> ExpertScoreKeys =
        ExpertScores.Select(m => m.Key).ToList();
}
