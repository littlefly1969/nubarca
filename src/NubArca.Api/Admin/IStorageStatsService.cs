namespace NubArca.Api.Admin;

public interface IStorageStatsService
{
    // Slice 84: `refresh` bypasses the short-lived cache and forces a recompute.
    // Slice 85b: `includePhysicalScan` runs the expensive blob-store filesystem
    // walk (physical/missing/unreferenced counts). Defaults true for backward
    // compatibility; the admin UI passes false for fast loads and true only for
    // an on-demand integrity check.
    Task<StorageStatsResponse> GetAsync(
        bool refresh = false,
        bool includePhysicalScan = true,
        CancellationToken cancellationToken = default);
}
