namespace NubArca.Api.Storage;

public class BlobJanitorOptions
{
    public const string SectionName = "BlobJanitor";

    public bool Enabled { get; set; } = false;

    public int IntervalMinutes { get; set; } = 5;

    public int GraceMinutes { get; set; } = 1440;
}
