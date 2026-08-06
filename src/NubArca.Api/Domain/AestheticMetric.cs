namespace NubArca.Api.Domain;

// One normalized, separately queryable Expert-head dimension score for a run.
// Every dimension the model emits is persisted as its OWN row (never a single
// blob of scores), so metrics are queryable/aggregatable independently of the
// raw provenance JSON.
public class AestheticMetric
{
    public Guid Id { get; set; }

    public Guid RunId { get; set; }

    // Stable internal metric key (AestheticMetricCatalog), e.g. overall_aesthetic.
    public string MetricKey { get; set; } = string.Empty;

    // Grouping for the UI (face/appearance/environment/overall).
    public string MetricGroup { get; set; } = string.Empty;

    // The model's own value on its declared scale (see ScaleMin/ScaleMax).
    public double NumericValue { get; set; }

    public double ScaleMin { get; set; }
    public double ScaleMax { get; set; }

    // Model-reported confidence, when available (null otherwise).
    public double? Confidence { get; set; }

    // Mapping/scale version for this metric key (AestheticMetricCatalog).
    public int MetricVersion { get; set; }

    public DateTime CreatedAt { get; set; }
}
