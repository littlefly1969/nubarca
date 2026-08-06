namespace NubArca.Api.TvUpdates;

public sealed class TvUpdateOptions
{
    public const string SectionName = "TvUpdates";
    public string RootPath { get; set; } = string.Empty;
    public string CodeSigningCertificatePath { get; set; } = string.Empty;
}
