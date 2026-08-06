namespace NubArca.Api.Files;

public interface IFileThumbnailService
{
    // Best-effort: generates the "small" thumbnail from the source blob and
    // persists a FileThumbnail row. Returns true if a thumbnail was created.
    // Any decoding, resizing, encoding, or persistence failure is swallowed
    // and returns false so the upload path never fails on thumbnail issues.
    Task<bool> TryGenerateSmallAsync(
        Guid fileItemId,
        Guid sourceBlobId,
        CancellationToken cancellationToken = default);

    // Owner-safe + soft-delete-aware. Returns null indistinguishably for any
    // missing / foreign / soft-deleted / non-image / no-thumbnail case.
    Task<ThumbnailContent?> OpenAsync(
        Guid fileItemId,
        Guid ownerUserId,
        string size,
        CancellationToken cancellationToken = default);

    // Private Vault variant of OpenAsync. Serves an EXISTING derivative for a
    // file that lives inside the owner's Private Vault — the ONLY reader that
    // deliberately bypasses the global "PrivateVaultId == null" query filter,
    // and only after the caller has resolved a valid unlock token to `vaultId`.
    // Authorization is expressed in the query: the file must be owner-scoped,
    // active (not soft-deleted), AND currently marked with exactly this vault
    // id. NEVER generates: a missing derivative is a plain null (indistinct
    // from missing / foreign / wrong-vault / wrong-kind). No original bytes and
    // no side effects — a vault view must trigger zero background work.
    Task<ThumbnailContent?> OpenVaultAsync(
        Guid fileItemId,
        Guid ownerUserId,
        Guid vaultId,
        string size,
        CancellationToken cancellationToken = default);

    // Owner-scoped lazy ensure (slice 59). If a FileThumbnail row of the given
    // size already exists for the owned active FileItem, opens it. Otherwise
    // generates the derivative on demand, persists a FileThumbnail row, and
    // opens it. Returns null indistinguishably for missing / foreign /
    // soft-deleted / non-image / generation-failed. Mostly used by the medium
    // preview endpoint (size = "medium"), which would be wasteful to pre-
    // generate at upload time; the small grid thumbnail keeps using the
    // existing eager TryGenerateSmallAsync.
    Task<ThumbnailContent?> EnsureAsync(
        Guid fileItemId,
        Guid ownerUserId,
        string size,
        CancellationToken cancellationToken = default);

    // ── Slice 95: GENERATION-ONLY entry points for batch work ──────────────
    // The HTTP endpoints need streams because they serve clients; the
    // derivatives backfill does not — these persist rows without ever
    // reopening the just-stored derived bytes.

    // Generates every missing requested IMAGE derivative (small/medium) for
    // an owned active file in ONE pass: the source is identified and decoded
    // AT MOST ONCE, each size is cloned/resized/encoded/stored independently
    // (partial success preserved: one size failing never undoes another), and
    // existing rows are skipped. Never throws for per-size failures.
    Task<ImageDerivativesResult> EnsureImageDerivativesAsync(
        Guid fileItemId,
        Guid ownerUserId,
        IReadOnlyCollection<string> sizes,
        CancellationToken cancellationToken = default);

    // Ensures the video poster row exists for an owned active file without
    // opening/returning the poster bytes.
    Task<DerivativeOutcome> EnsurePosterGeneratedAsync(
        Guid fileItemId,
        Guid ownerUserId,
        CancellationToken cancellationToken = default);

    // Ensures the six-frame video sprite exists without opening its bytes.
    // Failures are persisted by the caller and are retried only by an explicit
    // forced backfill, never by repeated hover/focus requests.
    Task<DerivativeOutcome> EnsureVideoPreviewStripGeneratedAsync(
        Guid fileItemId,
        Guid ownerUserId,
        CancellationToken cancellationToken = default);

    // Maintenance-only replacement path for the gallery derivatives whose
    // rendering contract changed. A replacement is generated and stored first;
    // the existing FileThumbnail row is then repointed atomically and the old
    // blob reference released. The old derivative remains servable if rendering
    // or persistence fails. Only small/poster/video-preview-strip are accepted.
    Task<GalleryDerivativeReplacementOutcome> RegenerateGalleryDerivativeAsync(
        Guid fileItemId,
        Guid ownerUserId,
        string size,
        bool force,
        CancellationToken cancellationToken = default);
}

public sealed record ThumbnailContent(
    Stream Content,
    string MimeType,
    int Width,
    int Height,
    long SizeBytes);

// Slice 95 — per-derivative outcome of a generation-only call.
public enum DerivativeOutcome
{
    Generated,
    SkippedExisting,
    Failed,
    // Missing/foreign/deleted file, non-image source, or safety limits hit —
    // nothing to (re)try.
    NotEligible,
}

public enum GalleryDerivativeReplacementOutcome
{
    Replaced,
    CreatedMissing,
    SkippedExisting,
    Failed,
    NotEligible,
}

// Slice 99 — a per-size outcome now also carries the precise diagnostic so the
// backfill can record WHY a derivative is missing. ErrorCode is one of
// DerivativeErrorCodes (null for Generated / SkippedExisting). Permanent marks
// a deterministic failure (corrupt / unsupported / over a safety limit) that a
// default backfill should not keep retrying.
// Slice 100 — Backend names the engine that produced/attempted the size (see
// DerivativeBackends) and FellBack records that the preferred backend failed and
// ImageSharp produced the result. Both are recorded in diagnostics on failure.
public sealed record ImageDerivativeOutcome(
    string Size,
    DerivativeOutcome Outcome,
    string? ErrorCode = null,
    bool Permanent = false,
    string? Backend = null,
    bool FellBack = false);

// Mutable accumulator so the backfill can aggregate timings across files.
// Slice 100: decode/resize/encode are now a single backend "render" step
// (libvips does them in one shrink-on-load pass), so they collapse into
// RenderMillis; Identify (gate) and Store/Db (orchestration) remain separate.
public sealed class ImageDerivativesTimings
{
    public long IdentifyMillis;
    public long RenderMillis;
    public long StoreMillis;
    public long DbMillis;
}

public sealed record ImageDerivativesResult(
    // True when the source image was actually decoded (at most once per call).
    bool SourceDecoded,
    IReadOnlyList<ImageDerivativeOutcome> Outcomes,
    ImageDerivativesTimings Timings);
