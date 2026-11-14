namespace NubArca.Api.Lithophanes;

public sealed record LithophaneCreateRequest(Guid FileItemId, Guid RequestId, LithophaneSettings Settings);
public sealed record LithophanePreviewRequest(Guid FileItemId, LithophaneSettings Settings);
public sealed record LithophaneStatusRequest(IReadOnlyList<Guid> Ids);
public sealed record LithophaneQueueRequest(IReadOnlyList<Guid>? FileItemIds);
public sealed record LithophaneQueueEntry(Guid FileItemId, string Name, string ThumbnailUrl, DateTime AddedAt);
public sealed record LithophaneQueueSkip(Guid ItemId, string Reason);
public sealed record LithophaneQueueResult(IReadOnlyList<Guid> Added, IReadOnlyList<LithophaneQueueSkip> Skipped);
public sealed record LithophaneJobPayload(Guid ProjectId);
public sealed record LithophaneSummary(Guid Id, string Name, Guid? FileItemId, LithophaneSettings Settings,
    string Status, int CompletedRows, int TotalRows, int TriangleCount, long SizeBytes,
    DateTime CreatedAt, bool DownloadReady);
public sealed record LithophanePage(IReadOnlyList<LithophaneSummary> Items, Guid? NextCursor);
public sealed record LithophanePreview(string ImageBase64, int Columns, int Rows, int TopRows,
    double BottomAngle, int TriangleCount, long SizeBytes, double? MinimumZoom = null);
public sealed class LithophaneException(string code, int statusCode = 400) : Exception(code)
{
    public string Code { get; } = code;
    public int StatusCode { get; } = statusCode;
}
