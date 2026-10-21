using System.Text.Json;

namespace NubArca.Api.MailArchive;

// The bounded list of scan errors kept on a run (MailImportRun.ScanIssuesJson).
public static class MailScanIssues
{
    public const int Max = 100;

    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static IReadOnlyList<MailScanIssueDto> Parse(string? json)
    {
        if (string.IsNullOrEmpty(json))
        {
            return [];
        }
        try
        {
            return JsonSerializer.Deserialize<List<MailScanIssueDto>>(json, Json) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }
}
