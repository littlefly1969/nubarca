namespace NubArca.Api.Files;

public class FileItemSweeperOptions
{
    public const string SectionName = "FileItemSweeper";

    public bool Enabled { get; set; } = false;

    public int IntervalMinutes { get; set; } = 5;

    public int GraceMinutes { get; set; } = 1440;
}
