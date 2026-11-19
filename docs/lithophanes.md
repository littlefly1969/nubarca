# Spherical lithophanes

## Specification

Laboratory → Lithophanes (`/lab/lithophanes`) converts one owner-library photograph
into a hollow, watertight spherical STL. The user chooses the external diameter,
minimum/maximum wall thickness, bottom LED opening, surface resolution, photo
coverage, framing and tonal correction. Editor defaults: 80 mm, 0.8–2.8 mm,
20 mm opening, 0.3 mm sampling, external relief, a 4:3 crop and 80% photo size.
Requests without the new crop fields retain the legacy 160° × 110° area.
The remaining surface has its own uniform
background wall (0.4–2 mm, default 0.8 mm), independent of the photo's minimum and
maximum. A coverage channel in the persisted map (grey + alpha) tells background
from white photo pixels, and the photo's edge ramps into the background over
`LithophaneSettings.BlendMm` (2 mm, smoothstep) outside the photograph.
With `externalRelief: true`, the inner radius stays at `diameter/2 - backgroundWall`
and the outer radius is that inner radius plus the local wall. Diameter is the
nominal background sphere, not the largest relief extent. Absent/false retains
the original smooth outer sphere and variable inner surface.
Maps saved before coverage existed load fully covered, and
settings without a background value use the minimum wall, so older projects keep
their exact geometry. Previews show light through the actual wall.

Photos are chosen the same way as for Plates: *Add to → Lithophanes* from the
Library, a list or an album puts them on an owner-private waiting list
(`lithophane_queue_items`, references only, at most 200) shown at the top of the
page. A generation ends that photo's wait, as does removing it; a trashed photo
is hidden and its purge deletes the reference.
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
Bottom opening is
the clear internal diameter, with a closed, flat annular rim. Model units are mm.

### Vertical pedestal

New projects enable `baseCollar` by default in the editor. A short cylindrical
annular foot starts vertically before the sphere, with a flat bottom and a clear
bore exactly equal to `openingMm` through its full height. Height is 10% of the
opening (bounded to 1–4 mm); minimum radial width is 20% of the opening (bounded
to 1.2–5 mm), retaining a wider rim if the original geometry already requires
one. A 20 mm opening therefore has a 2 mm high base with an outer diameter of
at least 28 mm. The sphere is truncated at the collar's outer radius, and the
surfaces share their seam: one watertight shell, no internal cap or overlapping
solids. The sphere's diameter, photo thickness, and upper dome are unchanged.

The last latitude strip emits the collar once, including on retries. Its faces
count toward the same triangle budget, and previews show its shape and wall.
`baseCollar` absent/false uses the original geometry; false is omitted from
persisted settings, preserving old idempotency fingerprints and in-flight maps
and strips. No database migration is needed. Existing downloaded STLs are not
rewritten: generate a new project to print the pedestal. Support generation and
removal remain slicer decisions; the pedestal does not make the dome support-free.

### Crop and sphere size

The editor shows the owner-private medium photo preview in the shared
`PhotoCropFrame`: choose square/portrait/landscape or original proportions,
drag or use arrow keys, and zoom inside that window. `centerX`, `centerY` and
`zoom` keep the shared photo-placement semantics. `frameAspect` (0.2–5) is the
window's width/height, independently of the photo's angular footprint.
`pictureScale` (0.2–1, default 0.8) scales this fixed crop on the sphere, keeping
its aspect; it does not crop the photo a second time. The largest footprint
fits within 280° width and 140° height and leaves the opening plus blend clear.
Preview and generation calculate this area with the same settings. Changing a
photo resets its pan/zoom; changing sphere size does not reset the crop.

Both new numeric fields are absent from legacy settings, preserving their
serialized fingerprint and old map. An old project's angular settings still
render exactly as saved; editing its size/proportions opts into the new framing.
Fresh image previews use only the medium derivative; the worker still decodes
the original through the existing bounded reader.

### Hanging sphere and flush cap

`hanging: true` requires external relief, an opening of at least 16 mm (still
at most 65% of diameter) and a background wall of at least 0.8 mm. The body is
printed opening-down, like the standing sphere. Map sampling rotates photograph
AND words by a proper 180° rotation about Y, never a horizontal mirror, so both
are upright when the opening is at the top in use. The preview offers both
assembled appearance and printing orientation; its lighting is illustrative.

New hanging projects use `internalFit: true` and `printAnchor: false`. The
body ends at the original spherical cut plane: no exterior collar, flange or
flared foot. A 1 mm thick cylindrical sleeve extends only 2 mm into the body,
with its bore equal to `openingMm`. The root meets the regular inner sphere
at their exact intersection; the inner surface is sampled to that intersection
and the last strip follows the socket's continuous boundary. It is one closed
shell, not overlapping cylinders. The cap continues the same nominal sphere
and has a matching 2 mm straight spigot with 1 mm wall and 0.15 mm radial
clearance. This is a locating fit, without a bayonet or guaranteed retention;
test fit and holding strength on the chosen printer and filament.

`printBrim: true` (enabled by the hanging editor) includes inward-only brims in
**both** STLs. The brim is 0.16 mm thick, coplanar with the lowest layer: 5 mm
wide inside the body's opening and 3 mm inside the cap's spigot. Neither brim
extends outside the spherical wall or across a mating surface. The body's
aperture is temporarily narrowed by 10 mm in diameter; remove its thin annulus
to restore `openingMm`. Remove the cap's brim too before assembly. Print the
body opening-down and the cap spigot-down; inspect the first layer in the
slicer. Do not add another slicer brim when using these built-in brims. They
are optional, do not simulate bed adhesion and do not eliminate support needs.

`internalFit` requires hanging mode and excludes `printAnchor`; `printBrim`
requires `internalFit`. False values are omitted from saved JSON so old settings,
fingerprints, maps, checkpoints and output remain unchanged. Both socket and
brim faces count toward the body's browser/server triangle budget. The preview
keeps the exterior spherical and indicates a local joint shadow, not a calibrated
optical prediction. No database migration or new persisted artifact is needed.

Legacy `printAnchor: true` adds a broad annular sacrificial foot, 2–4 mm tall, with
3–6 mm extra radial footprint and a 0.6 mm radial neck at a groove 0.32 mm below
the final mating plane. Cut at the groove and dress the remaining rim to that
plane. With it disabled, the complete body ends at its ordinary annular rim.
Standing projects use the existing `baseCollar` instead. Neither option changes
the clear extraction aperture. No partial/test-section STL export exists.

A ready hanging project offers a separate **complete cap STL**, generated
deterministically from its fixed settings under the same owner/permission
checks and download audit, with the export rate limit and `no-store` headers.
No new persisted artifacts or migration are needed. The cap continues the
nominal spherical surface. Legacy settings without `internalFit` retain a
0.8 mm deep, 0.8 mm thick internal locating lip
with 0.2 mm radial clearance, a 10 mm diameter/5 mm high crown, a 4 mm axial
cable bore and two opposing lateral thread windows. That legacy lip is designed
for an adhesive joint, not a snap lock. Fit, brim/foot removal, holding strength and
illuminated seam visibility require physical acceptance on the chosen filament;
the preview cannot predict adhesive transmission or certify the hanging load.
The cap uses a bounded 128-column, at most 128-row lathe mesh with manifold
window walls. Body and cap are separate closed solids; no overlapping fake union.

### Words

Settings may carry `text` (one or two lines separated by `\n`, at most 60
characters, no other control characters, no emoji), `textFont` (a
`TextFonts` id: `nubarca` default, `serif`, `hand`, `marker`, `script`),
`textHeightMm` (cap height on the surface, 3–30, default 8) and
`textPosition` (`below` default, `above`, `onPhoto`). Absent fields are never
serialised, so a project without words keeps its stored settings and its
idempotency fingerprint. The words are rasterised flat in millimetres
(SixLabors.Fonts, the same TTF files the browser previews with) and laid on the
sphere along each circle of latitude and meridian, centred on the photo's
longitude, `BlendMm + 0.25 × height` away from the photo's edge. Their coverage
mixes the wall toward the maximum thickness, so backlit they read dark. They are
drawn into the persisted map, so preview, strips and STL agree.

A create or preview request with `fileItemId: null` is a lithophane of words
only: `text` is required, the background wall is the whole sphere and the words
are centred on the equator facing longitude 0. Such a project stores
`SourceFileItemId = Guid.Empty` and is named after its words; a photo project
whose source was purged keeps its real source id and still cancels as before.

## Acceptance

- STL topology: every edge used twice in opposite directions, no degenerate faces,
  outward outer normals/inward inner normals, correct nominal/background diameter
  (exact bounding diameter in legacy smooth mode), clear aperture and cap fit.
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
0.3 mm step the triangle budget is reached just under 100 mm; above that the form
explains it and offers the finest step that fits (for example 0.5 mm at 150 mm). The opening
is the clear internal diameter of the flat bottom rim. The sphere is truncated
at that opening, so its height is less than its full spherical diameter.

The photo uses NubArca's existing centre/zoom framing policy. Below the zoom
at which the whole photo already fits the area, the map no longer changes; a
fresh preview reports that minimum and the form offers it. White maps to the
minimum wall, black to the maximum. Outside the selected angular photo area,
at the north pole and at the opening, the wall is uniformly thin. Brightness,
contrast and gamma are applied before thickness conversion. Transparency is
composited over white. These are monochrome lithophanes; no colour filament
mapping or slicer/G-code generation is included.

A project's photograph and settings are fixed at creation. Repeating a request
id with identical inputs returns the original project; different inputs with
that id return 409. After a successful submission the web page starts a new
request id for a deliberate second generation. A lost response keeps its id.
Generated maps and STLs are retained until the project is deleted. Strips are
only assembly checkpoints: once the STL is committed their ownership is
released and their bytes removed (shared content stays protected), so a
finished project keeps about one STL plus a small map. The project also stores
the photograph its request named, so a retried request stays idempotent even
after that photograph has been purged.
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
| GET | `/{id}/cap` | Complete cap STL for a ready hanging project; same private access |
| POST | `/{id}/cancel` | Idempotent cancellation request |
| DELETE | `/{id}` | Cancel work and remove project/artifact ownership |

Create and fresh-preview requests share the existing bounded export-create
rate limit. Status polling is single-flight and aborts on unmount; previews
are explicit, so editing a control never starts a burst of original decodes.
