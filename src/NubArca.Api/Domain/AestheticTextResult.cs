namespace NubArca.Api.Domain;

// Optional textual output for a run (LM head / MetaVoter). No rows exist when
// only expert_scores runs. Every row is bound to the EXACT immutable run (and
// thus its model revision + capability set). Prepared now; the text-producing
// capabilities are disabled by config until validated.
public class AestheticTextResult
{
    public Guid Id { get; set; }

    public Guid RunId { get; set; }

    // Stable text kind (AestheticTextKinds): summary / detailed_assessment / …
    public string TextKind { get; set; } = string.Empty;

    // BCP-47-ish language tag the text was generated in (e.g. "it", "en").
    public string Language { get; set; } = string.Empty;

    public string Text { get; set; } = string.Empty;

    // Version of the prompt template that produced the text (null when N/A).
    public int? PromptTemplateVersion { get; set; }

    public DateTime CreatedAt { get; set; }
}
