# Photo DateTaken Organizer

Organize photos into date-based folders using each photo's **effective capture
date**, as a safe, previewable, owner-scoped background operation.

Example results (template is configurable):

```
/Photos/2024/2024-05-17/IMG_1234.jpg      # yyyy/yyyy-MM-dd  (default)
/Photos/2024/05/17/IMG_1234.jpg           # yyyy/MM/dd
/Photos/2024/05/IMG_1234.jpg              # yyyy/MM
/Photos/2024/IMG_1234.jpg                 # yyyy
```

## Core principle

This is a **logical, DB-only move**. A `FileItem`'s `ParentFolderId` (and name,
when a conflict needs a suffix) is updated; the original content-addressed blob
is **never rewritten or copied**. Consequently:

- `BlobObject` rows, `StorageKey`, and SHA-256 are untouched.
- `FileThumbnail` rows (small / medium / poster) stay valid — they key off
  `FileItemId`, not location, so previews keep working after a move.
- `FileItemUserMetadata` (title, tags, rating, DateTaken override, …) stays
  attached to the same `FileItem`.
- Share links follow the existing move semantics (a share targets the
  `FileItem`, which still exists).

Photos are identified by image MIME (`image/*`) or a detected image
media-category. Videos are out of scope for this organizer.

## Effective DateTaken

Resolved per photo in this precedence (see
`PhotoDateTakenPlanner.Resolve`):

1. **`user_override`** — the user's `DateTakenOverride`, if set.
2. **`metadata_original`** — embedded EXIF `DateTimeOriginal`.
3. **`metadata_fallback`** — any other embedded capture date
   (`DateTimeDigitized` / `DateTime`).
4. **`file_created_fallback`** — the file's created/import date, **only** when
   the user explicitly chooses the "use upload date" missing-date behavior.
5. **`missing`** — no capture date available.

The import date is **never** silently used as a capture date — it is used only
when the user opts into it.

### Timezone

EXIF capture dates carry no timezone in this model: the stored value is the
camera's **wall-clock** instant (kind `Utc`, no conversion applied). The
organizer buckets on that value's year/month/day directly, so the folder date
matches the date shown on the photo regardless of the viewer's timezone. The
result is stable and deterministic.

## Scopes

| Scope | Meaning |
| --- | --- |
| `selected` | An explicit list of file ids (≤ 10,000). |
| `folder` | A folder's direct photos (`folderId`; null = the user's root). |
| `folder_recursive` | A folder and all of its descendants. |
| `media_library` | All of the owner's media-library photos (respects per-folder media-library exclusions). |
| `all` | All of the owner's photos. |

Every scope is **owner-scoped**: a run only ever sees and moves the caller's own
files. There is no cross-user / admin organize.

## Folder templates

Templates are a fixed, validated set (no custom template language): `yyyy`,
`yyyy/MM`, `yyyy/MM/dd`, `yyyy/yyyy-MM-dd`. Segments are always derived from the
date, so a template can never inject a traversal segment.

The **target root** is `[targetRootName]` under `targetRootFolderId`
(null = the user's root). `targetRootName` defaults to `Photos`, is validated as
a single safe folder segment (no `/`, `\`, `.`, `..`, empty, or over-length),
and may be blank to organize directly under the chosen base folder. Missing
target folders are created on demand and reused if they already exist.

## Dry-run (preview)

`POST /api/photo-organizer/date-taken/dry-run` is **read-only** — it never
mutates anything (no folders created, no files moved, no run row written). It
returns aggregate counts plus a few safe samples:

- candidate / with-date / missing-date counts;
- will-move / already-organized / skipped (no date) / skipped (name conflict);
- folders to create; estimated operations;
- a per-source breakdown;
- up to 20 sample entries (`name`, current logical path, target logical path,
  effective date, source, action).

Samples expose only the owner's own logical paths and the (owner-private)
effective date — never physical paths, storage keys, blob ids, SHA, GPS, or raw
metadata.

## Conflict policy

When the target folder already contains a file with the computed name:

- **`keep_both`** (default) — append a deterministic ` (1)`, ` (2)`, … before
  the extension (`IMG.jpg` → `IMG (1).jpg`).
- **`skip`** — leave the file where it is and count it as a conflict skip.

There is **no overwrite** option in this slice. Two source files that resolve to
the same target are handled deterministically: dry-run simulates per-folder name
reservations; execution re-checks the live folder contents as it goes.

## Execution job

`POST /api/photo-organizer/date-taken/run` creates a `photo_organizer_runs` row
(holding the validated options + live counters) and enqueues a
`photo.organizer.datetaken` background job carrying only the run id. The job runs
on the **existing cooperative scheduler** (Normal priority — a foreground import
preempts it after the current slice):

- **sliceable + checkpointed** — processes a batch of files, then yields and
  re-queues itself with a checkpoint (cumulative counts + a keyset cursor);
- **cancellable** — operator/user cancellation stops it at a safe boundary and
  finalizes the run as `cancelled` (work already done is kept);
- **retry-safe / idempotent** — a file already in its target folder is detected
  as *already organized* and never moved again, so reruns and resumes are safe;
- progress reports moved / already / skipped / failed counts;
- a single failed file is recorded and **never blocks** the rest of the run
  (the run finishes `partial`).

Safe yield points are only **after** a file's move is committed — never
mid-transaction and never between creating a folder and moving the file into it.

## Audit & manifest

- A `photo_organizer_runs` row is the durable record of status + counts.
- A `photo_organizer_moves` row is written per **moved** file recording
  source → target (folder ids + names), the effective date, and its source.
  This is the audit trail and is **undo-ready** (see Limitations).
- Audit-log entries are written for run start / complete / cancel / fail with
  aggregate counts only (no paths or internals).

## API

| Method | Route | Purpose |
| --- | --- | --- |
| POST | `/api/photo-organizer/date-taken/dry-run` | Read-only preview. |
| POST | `/api/photo-organizer/date-taken/run` | Create a run + enqueue the job. |
| GET | `/api/photo-organizer/date-taken/runs/{id}` | Owner-scoped run status. |

All require authentication and are owner-scoped (a run is only visible to its
owner — a foreign id returns `404`). Invalid options (unknown scope / template /
behavior / conflict policy, missing file ids for `selected`, unsafe target-root
name) return `400`.

## UI

The Files UI exposes **Organize by date** in the toolbar. The wizard:

1. choose scope (selection / this folder / recursive / media library / all);
2. choose target root name (+ optionally the current folder as the base);
3. choose folder template;
4. choose missing-date behavior (skip / use upload date / Unknown Date folder);
5. choose conflict policy (keep both / skip);
6. **Preview** (dry-run) — review the summary + examples;
7. **Organize** — starts the background job and shows live progress, then a
   result summary.

It is a single scrollable panel (a full-screen sheet on mobile) and renders only
logical paths + counts.

## Privacy

The feature never exposes physical paths, `StorageKey`, SHA, `BlobObjectId`,
internal `FileItemId`, raw metadata JSON, GPS, or serial numbers. The effective
capture date is shown only in the owner's private dry-run/run surfaces.

## Limitations (this slice)

- **Undo is not yet implemented.** The `photo_organizer_moves` manifest records
  enough (source folder + name → target folder + name, in order) to add an
  "undo last run" later without a schema change.
- **No overwrite** conflict mode (by design).
- The UI base-folder chooser is limited to *Home* or *the current folder*; the
  API accepts any owned `targetRootFolderId`.
- Videos are not organized (photos only).
