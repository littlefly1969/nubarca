# Spherical lithophanes

## Specification

Laboratory → Lithophanes (`/lab/lithophanes`) converts one owner-library photograph
into a hollow, watertight spherical STL. The user chooses the external diameter,
minimum/maximum wall thickness, bottom LED opening, surface resolution, photo
coverage, framing and tonal correction. Defaults: 80 mm, 0.8–2.8 mm, 20 mm opening,
0.3 mm sampling, 160° × 110° photo area. The remaining surface has a uniform wall.
An orientable preview uses the same processed luminance map as generation; its
illumination is illustrative, not a calibrated prediction of a filament.

Creation is idempotent by owner/request id. A PostgreSQL-backed
`lithophane.generate` job uses the existing priority, lease, cancellation,
checkpoint and continuation system. Preprocessing, latitude strips and final
assembly are bounded stages. Completed strips are immutable derived artifacts;
a retried slice never appends to a previous attempt's mutable file. Final assembly
streams strips to a temporary file and publishes atomically through StoragePublish.
No full mesh or complete STL is retained in managed memory.

Projects, previews, status, cancellation, downloads and deletion are owner-private
and require both `laboratory.access` and `laboratory.lithophanes`. Workers re-read
permissions before each slice. Source photos remain untouched. Source removal does
not block purge; a missing source before preprocessing cancels the generation.
Deleting a project cancels its job and releases artifact ownership. Physical cleanup
uses the existing reconciliation mechanism, including shared-content protection.

Hard limits bound dimensions, pixels, triangle count, active history and input
image decoding. No new runtime, AI service or geometry dependency is needed.
The external sphere remains smooth; thickness varies inward. Bottom opening is
the clear internal diameter, with a closed, flat annular rim. Model units are mm.

## Acceptance

- STL topology: every edge used twice in opposite directions, no degenerate faces,
  outward outer normals/inward inner normals, exact bounding diameter, correct rim.
- White and black map to minimum and maximum thickness; framing, gamma and EXIF
  orientation are deterministic; preview and STL use the same map.
- Forced one-unit slices and retry/crash recovery produce byte-identical output to
  uninterrupted generation. Cancellation and permission revocation produce no
  permanent failure. Both API and CLI worker resolve the same services/handler.
- Owner isolation, composite permissions, invalid/non-finite inputs, non-image and
  missing sources, idempotency, pagination, artifact protection and deletion tested.
- Web workflow covers selection, preview, validation, generation, polling, reload,
  cancellation, download and deletion; Italian/English/Spanish/German strings.
- Repository identity, backend build/tests, frontend typecheck/build/tests and
  additive migration compatibility checks pass before delivery.

## Operation and lifecycle

The existing API and `jobs worker` CLI register the same service and handler.
The normal worker must be running; its existing priority/slice settings apply.
No additional container or AI provider is required. Heavy image decoding is
limited to one original per process and uses OriginalImageReader (including
HEIC), first-frame-only decoding, EXIF orientation and a bounded working image.

Limits: 40–200 mm diameter; 0.4–2 mm minimum wall; maximum wall up to 5 mm and at
least 0.1 mm above minimum; 4 mm to 65% of diameter LED opening; 0.15–1 mm surface
sampling; at most 2,000,000 triangles (~100 MB binary STL); 64 MiB/64 MP source;
100 retained projects per owner; 20 history records per keyset page. At the default
0.3 mm step the triangle budget is reached near 95 mm; above that the form
explains it and offers the finest step that fits (for example 0.5 mm at 150 mm). The opening
is the clear internal diameter of the flat bottom rim. The sphere is truncated
at that opening, so its height is less than its full spherical diameter.

The photo uses NubArca's existing centre/zoom framing policy. White maps to the
minimum wall, black to the maximum. Outside the selected angular photo area,
at the north pole and at the opening, the wall is uniformly thin. Brightness,
contrast and gamma are applied before thickness conversion. Transparency is
composited over white. These are monochrome lithophanes; no colour filament
mapping or slicer/G-code generation is included.

A project's photograph and settings are fixed at creation. Repeating a request
id with identical inputs returns the original project; different inputs with
that id return 409. After a successful submission the web page starts a new
request id for a deliberate second generation. A lost response keeps its id.
Generated maps, strips and STLs are retained until the project is deleted.
Source purge uses SET NULL and does not erase a completed lithophane. A source
that is unavailable before preprocessing cancels the work. Workers recheck
current permissions on each slice; revocation cancels rather than fails it.

Final assembly is a bounded streaming stage; a crash during assembly restarts
that stage from immutable strips. Geometry slices resume at the next committed
strip even if the queue checkpoint was not committed. Cancellation is checked
between strips, during asynchronous IO and before final publication. Deletion
locks the project before collecting and releasing its artifact ownership, then
removes unowned bytes under exclusive content locks; a failed physical cleanup
is safely left to ordinary storage reconciliation. The original photo is never
rewritten or duplicated into the Laboratory.

The additive migration creates two tables and grants the new permission to an
existing Member role. Administrator receives it from the catalogue; custom
roles are managed explicitly by the operator. Deployment uses the normal
production runbook and migration policy. When rolling back the application,
do not run the previous version's destructive storage reconciliation: it does
not recognise the new artifact owners. The migration's Down drops project and
strip metadata only; source files stay intact and unowned generated bytes can
be reclaimed by reconciliation.

## HTTP contract

Every route requires the owner cookie and both Laboratory permissions. Responses
are `no-store`. Missing and foreign resources have indistinguishable results;
DTOs never contain storage keys, hashes, blob ids, raw job payloads or paths.

| Method | Path under `/api/lithophanes` | Result |
| --- | --- | --- |
| GET | `?cursor=<project id>` | Bounded history page, newest first |
| POST | `` | Create with `{fileItemId, requestId, settings}` |
| POST | `/preview` | Processed grayscale PNG as base64 plus safe grid dimensions |
| POST | `/statuses` | Batch status for up to 100 owner project ids |
| GET | `/{id}` | Safe settings, progress and lifecycle status |
| GET | `/{id}/preview` | Preview of the persisted map |
| GET | `/{id}/download` | Authenticated binary STL attachment in mm |
| POST | `/{id}/cancel` | Idempotent cancellation request |
| DELETE | `/{id}` | Cancel work and remove project/artifact ownership |

Create and fresh-preview requests share the existing bounded export-create
rate limit. Status polling is single-flight and aborts on unmount; previews
are explicit, so editing a control never starts a burst of original decodes.
