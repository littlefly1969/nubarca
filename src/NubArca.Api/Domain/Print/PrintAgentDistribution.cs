namespace NubArca.Api.Domain.Print;

/// <summary>
/// Whether this installation hands out its Print Agent bundle. One row, id 1,
/// switched by an administrator: the bundle itself is software anyone could
/// build, so the switch is about not serving a download nobody asked for.
/// </summary>
public sealed class PrintAgentDistribution
{
    public const int SingletonId = 1;

    public int Id { get; set; } = SingletonId;
    public bool SharingEnabled { get; set; }
    public DateTime UpdatedAt { get; set; }

    /// <summary>The administrator who last switched it; a plain audit column, no FK.</summary>
    public Guid? UpdatedByUserId { get; set; }
}
