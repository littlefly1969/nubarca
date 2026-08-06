# Media derivatives & failure diagnostics

> **Backends:** image derivatives are rendered by a pluggable
> backend — the high-performance **libvips** path with the original
> **ImageSharp** path as an always-available fallback. See
> [Image backends](#image-backends-libvips--imagesharp) below.

NubArca produces four derived artifacts from a source blob:

| Size     | What it is                          | Generator           |
| -------- | ----------------------------------- | ------------------- |
| `small`  | 768 px gallery thumbnail (JPEG; no upscale) | libvips / ImageSharp |
| `medium` | 1920 px lightbox preview by default (JPEG; configurable) | libvips / ImageSharp |
| `poster` | source-aspect video poster (JPEG; longer edge ≤ 1280) | synthetic / FFmpeg |
| `video-preview-strip` | six 480×270 video frames in one 2880×270 JPEG sprite | FFmpeg |

The values above are defaults, not duplicated provider constants. Override the
geometry and JPEG quality in `.env`; Compose passes the same values to API and
worker:

```dotenv
MediaDerivatives__SmallMaxEdge=768
MediaDerivatives__SmallQuality=80
MediaDerivatives__MediumPreviewMaxEdge=1920
MediaDerivatives__MediumQuality=80
MediaDerivatives__PosterWidth=1280
MediaDerivatives__PosterHeight=720
MediaDerivatives__VideoPreviewFrameWidth=480
MediaDerivatives__VideoPreviewFrameHeight=270
```

The preview strip is always six cells wide because six frames are part of the
web/TV animation contract; changing either frame dimension automatically
changes the sprite canvas to `6 × frame width` by `frame height`. Configuration
never changes the logical size names, URLs, directories, or query strings.

**Poster geometry — source aspect ratio.** The FFmpeg poster is scaled to fit
within a `maxEdge × maxEdge` box (`maxEdge = max(PosterWidth, PosterHeight)`,
1280 by default) **preserving the source aspect ratio** — no 16:9 staging, no
crop/pad, and **no blurred backdrop baked into the JPEG**. A 16:9 source still
yields ≈1280×720; a portrait `9:16` source yields ≈720×1280. The media wall draws
the blurred backdrop client-side behind a `contain` foreground, so the tile uses
the video's real shape (see [media-wall.md](media-wall.md)). FFmpeg autorotates
the frame (display orientation), and the `/api/media` DTO swaps coded width/height
by `blob_metadata.rotation` for a 90°/270° turn (`VideoDisplayDimensions`), so the
DTO aspect ratio matches the poster. The **preview strip is unchanged**: it still
bakes each frame into a fixed 16:9 cell over a blurred backdrop.

> **Migrating existing posters.** Posters generated before this change are still
> the old fixed 16:9 bytes; the missing-row backfill will not touch them. Rewrite
> them in place with `media posters regenerate --force`, which deletes each old
> poster row + releases its derived blob and regenerates at the new geometry. The
> orphaned old-geometry blobs are reclaimed by the (default-disabled) blob janitor.
> Dropped `PosterHeight` no longer constrains the poster shape, but the `.env`
> keys are retained for the `maxEdge` bound and backward compatibility.

Preview strips use six independent input-side seeks at the centres of evenly
spaced timeline buckets. FFmpeg therefore decodes only around six keyframes;
generation time does not grow with the full duration of the video. With local
filesystem storage, the already-authorized read-only source path is passed
directly to FFmpeg, avoiding a full copy to `/tmp`; non-file streams retain the
GUID temporary-file fallback. Paths are never logged or exposed. The strip has
its own `Media__VideoPreviewStripTimeoutSeconds` setting (default 45 s), separate
from the 10 s poster timeout.

Successful artifacts live in **`file_thumbnails`** (one row per FileItem × size,
each pointing at a content-addressed derived blob). That table is the *only*
record of success — nothing else is written when generation works.

This document covers what happens when generation does **not** work: the durable
diagnostics that explain *why* a derivative is missing, the retry policy, and how
to inspect it all.

## Successful artifacts vs. diagnostic failures

The two are kept in separate tables with a deliberate invariant:

- `file_thumbnails` — a row means the derivative **exists and is valid**.
- `derivative_diagnostics` — a row means the derivative is **currently missing
  for a known reason**. No fake `file_thumbnails` rows are ever created for a
  failure, and no diagnostic row is ever kept for a derivative that exists.

A diagnostic row carries only safe, bounded fields: the FileItem reference, the
size, a status, a stable error code, an optional sanitized message, the
*detected* content type / format (a sniffed MIME string — never a name or
path), attempt count, first/last attempt timestamps, an optional next-retry
time, and the generator backend + version. It never stores a StorageKey, SHA,
BlobId, owner id, GPS, raw metadata, secrets, or a stack trace.

### Statuses (`DerivativeStatuses`)

| Status             | Meaning                                                       | Retried by default? |
| ------------------ | ------------------------------------------------------------- | ------------------- |
| `failed_permanent` | deterministic for these bytes (corrupt / unsupported / over a limit) | no — only `--retry-failed` |
| `failed_transient` | likely temporary (storage hiccup, source bytes unavailable)   | yes, after a backoff |
| `not_eligible`     | deliberately not generated (e.g. thumbnails disabled)         | no                  |
| `skipped`          | deliberately skipped                                          | no                  |
| `pending`          | recorded placeholder (not used by the backfill today)         | yes                 |
| `cancelled`        | *never persisted* — a cancelled slice records nothing         | n/a                 |

`succeeded` is intentionally **not** a status: success is the presence of a
`file_thumbnails` row plus the absence of a diagnostic row.

### Error codes (`DerivativeErrorCodes`)

`unsupported_format`, `identify_failed`, `decode_failed`, `too_large_bytes`,
`too_large_dimensions`, `too_many_pixels`, `no_dimensions`,
`source_blob_missing`, `media_library_excluded`, `not_eligible`, `cancelled`,
`storage_error`, `db_error`, `timeout`, `unknown`.

These are a stable contract used by Admin stats, the CLI, and operator SQL.

## How "missing" is computed

Admin stats reports the bare missing counts (active image with detected image
metadata but no `file_thumbnails` row of that size):

- `ImagesMissingSmall`, `ImagesMissingMedium`, `VideosMissingPoster`.

The **Derivative diagnostics** section then partitions each missing count per
size:

```
never_attempted   = missing − recorded_diagnostic_rows(size)   (clamped ≥ 0)
failed_permanent  = diagnostic rows, status = failed_permanent
failed_transient  = diagnostic rows, status = failed_transient
not_eligible      = diagnostic rows, status = not_eligible
skipped           = diagnostic rows, status = skipped
```

A diagnostic row is counted only while its derivative is still missing (a row
whose thumbnail now exists — e.g. produced by the lazy endpoint — is treated as
resolved and ignored, and pruned on the next backfill / `failures` run).

So before any backfill attempts the missing files they all show as
**never-attempted**; after a backfill, each becomes a concrete reason.

> **Media-library-excluded images** are intentionally *not* attempted by the
> batch backfill (lazy on-request generation still serves them). They therefore
> remain in **never-attempted** until included — they are not failures.

## Retry semantics

The default backfill **skips** files whose missing derivatives are *blocked* by a
diagnostic — `failed_permanent` / `not_eligible` / `skipped`, or a
`failed_transient` whose `NextRetryAt` has not elapsed. This is what stops a
broken JPEG from being re-decoded on every run.

Transient failures get an exponential backoff (15 min, 30 min, 1 h, … capped at
6 h) recorded in `NextRetryAt`; once it elapses the default backfill retries them
automatically.

`video-preview-strip` is deliberately stricter: a failed post-ingest or lazy
generation is recorded as `failed_permanent`, regardless of the underlying
cause. Hover/focus must never become an implicit FFmpeg retry loop. It is retried
only by the explicit command below.

To re-attempt blocked failures explicitly:

```
media derivatives backfill --retry-failed     # alias: --force-failed, --failed-only
```

A successful (re)generation **clears** the diagnostic. Cancellation never records
a permanent failure — a yielded/cancelled slice writes nothing for the
in-flight file.

## Inspecting failures

### CLI

```
# Aggregate reasons, by size / status / error code / detected format.
dotnet NubArca.Api.dll media derivatives failures

# Re-attempt previously-failed derivatives.
dotnet NubArca.Api.dll media derivatives backfill --retry-failed
```

`failures` prints counts only — never a file name, path, key, id, or metadata.

### Admin stats

`GET /api/admin/storage-stats` includes a `derivativeDiagnostics` block (per size:
never-attempted, failed-permanent, failed-transient, not-eligible, skipped,
retryable-now, last-failure time, plus a by-error-code and top-detected-format
breakdown). Aggregate counts only; the Storage Stats admin page renders it under
**Derivative diagnostics**.

### SQL (operator)

```sql
-- Why are derivatives missing, by size / status / code / detected format?
SELECT "Size", "Status", "ErrorCode", "DetectedContentType", count(*)
FROM derivative_diagnostics
GROUP BY "Size", "Status", "ErrorCode", "DetectedContentType"
ORDER BY count(*) DESC;

-- Retryable-now transient failures.
SELECT "Size", count(*)
FROM derivative_diagnostics
WHERE "Status" = 'failed_transient'
  AND ("NextRetryAt" IS NULL OR "NextRetryAt" <= now())
GROUP BY "Size";
```

## Backward compatibility

Existing deployments already have missing derivatives with **no** diagnostic
rows. After the migration:

- existing `file_thumbnails` rows are untouched and remain valid;
- missing derivatives appear as **never-attempted** until a backfill attempts
  them — no failure reason is fabricated for historical items;
- the migration is safe on PostgreSQL (`derivative_diagnostics` table) and on the
  SQLite test schema (`EnsureCreated`);
- API clients are unaffected — the new admin field is additive and optional.

## Image backends (libvips / ImageSharp)

Image derivative generation (small + medium) is performed by a pluggable
backend behind `IImageDerivativeBackend`. The orchestration around it — owner
scoping, the safety gates (byte / dimension / pixel caps, the EnableThumbnails
kill-switch), the content-addressed derived-blob store, `file_thumbnails` rows,
refcounting, race handling, and diagnostics — is **backend-independent**, so a
backend swap can never change storage semantics.

| Backend      | Engine                         | Notes                                        |
| ------------ | ------------------------------ | -------------------------------------------- |
| `vips`       | libvips via NetVips (native)   | Preferred. Shrink-on-load, low memory, fast. |
| `imagesharp` | SixLabors.ImageSharp (managed) | Always available. The fallback + the default before this slice. |

### Why libvips

libvips' `thumbnail` operation **shrinks on load**: the JPEG/TIFF/WebP decoder
reads only the resolution the thumbnail needs instead of decompressing the full
image, then resizes in a single low-memory streaming pass. The win grows with
source size — on representative photos (rendering both small + medium):

| Source     | ImageSharp | libvips | Speedup |
| ---------- | ---------- | ------- | ------- |
| 12 MP JPEG | ~157 ms    | ~82 ms  | ~1.9×   |
| 24 MP JPEG | ~301 ms    | ~92 ms  | ~3.3×   |

Output bytes are within ~1% of ImageSharp at the same quality. Run your own
comparison with `media derivatives benchmark` (below).

### Semantics are preserved

The vips backend is configured to match ImageSharp exactly: fit inside the
`edge×edge` box preserving aspect ratio (`size=down`, **no upscale**), EXIF
auto-rotation **disabled** (`no-rotate`) so output dimensions equal the
identified source dimensions, and metadata kept so the orientation tag behaves
identically. The renderer **validates** every produced derivative against the
no-upscale bounding-box contract; an out-of-contract result is treated as a
backend failure and falls back. (Output JPEG bytes differ between backends — a
different encoder — so a vips-produced derivative deduplicates with other
vips-produced bytes; existing ImageSharp rows are never regenerated, so they
stay valid.)

### Configuration (`MediaDerivatives` section)

| Key                    | Default      | Meaning                                                         |
| ---------------------- | ------------ | --------------------------------------------------------------- |
| `ImageBackend`         | `auto`       | `auto` (prefer vips when available) / `vips` / `imagesharp`.    |
| `FallbackToImageSharp` | `true`       | Retry with ImageSharp when the preferred backend fails.         |
| `VipsEnabled`          | `true`       | Master switch for the vips backend.                             |
| `VipsConcurrency`      | `0`          | libvips worker threads per op (0 = #cores). Cap on small hosts. |
| `SmallQuality`         | `80`         | JPEG quality for small thumbnails.                              |
| `MediumQuality`        | `80`         | JPEG quality for medium previews.                               |
| `MediumPreviewMaxEdge` | `1920`       | Medium preview bounding-box max edge. Clamped to 256–8192.      |
| `RenderTimeoutSeconds` | `30`         | Per-render ceiling; a timeout falls back (0 disables).          |

Environment-variable form (double underscore): `MediaDerivatives__ImageBackend=vips`,
`MediaDerivatives__FallbackToImageSharp=true`, `MediaDerivatives__MediumPreviewMaxEdge=1920`,
`MediaDerivatives__VipsConcurrency=4`, …

### Fallback behaviour

When the preferred backend is unavailable, throws, times out, cannot decode, or
produces output that violates the bounding-box contract, the renderer retries
with ImageSharp (when `FallbackToImageSharp=true`). The backfill records, per
image, which backend was used and how many fell back:

```
media derivatives backfill backends: vips 1180, imagesharp 5, fallback 5.
```

If a render fails on **both** backends (a genuinely unprocessable file), a
diagnostic is recorded with the final `Backend` and a `fell_back_to_imagesharp`
message — visible via `media derivatives failures`. Cancellation never records a
failure or a fallback.

### Concurrency

This slice tunes libvips' **internal** worker concurrency (`VipsConcurrency`),
which parallelizes a single resize across cores — safe because it does not touch
the (non-thread-safe) `DbContext` or the slice/refcount accounting. App-level
parallelism across images would require per-task DI scopes and is intentionally
left for a future slice; the backfill stays single-lane and scheduler-sliceable.
The libvips operation cache is disabled (we never re-run an identical op) to keep
memory bounded.

**Small-first.** Within each image the small thumbnail is rendered before the
medium preview (the gallery grid is the priority), and the medium preview is
also generated lazily on first lightbox view, so it is never on the critical
path. A separate cross-image small-first pass (all smalls, then all mediums) is a
future option; with libvips making the whole backfill ~2–3× faster and medium
generation already lazy, it was not needed here. Per-size counts are reported
separately (`small +g/~s/!f/o`, `medium …`); render timing is aggregate (one
backend call produces both sizes).

### Benchmark

```sh
dotnet NubArca.Api.dll media derivatives benchmark --limit 50
```

Samples up to N real image source blobs, renders small + medium with **each
available backend in memory** (nothing is stored — no rows, no refcount
changes), and reports per-backend `total_ms` / `avg_ms` / `output_bytes` plus
the vips speedup. Counts and milliseconds only — never a name, path, key, or id.

### Deployment

The native libvips ships **bundled** via the `NetVips.Native.linux-x64` NuGet
package — `dotnet publish` places `libvips.so.42` (image codecs statically
linked) under `runtimes/linux-x64/native/`, and it runs on the glibc-based
`aspnet:10.0` image with **no apt packages**. For an Alpine/musl base, reference
`NetVips.Native.linux-musl-x64` instead. If the native library cannot load
(unsupported RID, missing base lib), `VipsRuntime` logs a warning at startup,
reports the backend unavailable, and the pipeline runs entirely on ImageSharp.

### Troubleshooting

- **"libvips backend unavailable" at startup** — the native lib could not load;
  the app runs on ImageSharp. Check the RID matches a referenced
  `NetVips.Native.<rid>` package. Force ImageSharp with
  `MediaDerivatives__ImageBackend=imagesharp` to silence the probe.
- **Lots of `fell_back_to_imagesharp` in diagnostics** — a class of inputs vips
  rejects (e.g. an exotic format/profile). They still render via ImageSharp;
  inspect with `media derivatives failures`.
- **High CPU on a small host** — lower `MediaDerivatives__VipsConcurrency`.

## Preparing for future backends (Rust / FFmpeg / new libvips)

Each diagnostic records the `Backend` (`vips` / `imagesharp` / `synthetic` /
`ffmpeg`) and `GeneratorVersion` that produced the outcome. When a new backend
or generator version is introduced, bump `DerivativeGenerators.ImageVersion` (or
run `--retry-failed`) and previously-permanent failures (e.g. a format the old
backend could not decode) can be re-attempted by the new backend without a
schema change.
