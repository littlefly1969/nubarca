# NubArca current baseline

Short, current-state context for development agents. This file is **not** a work
log: it carries no slice narratives, no branch names, no commit SHAs and no
"next step" notes. Released work is described by `CHANGELOG.md`; how the system
is built is described by `ARCHITECTURE.md`.

## Baseline

- Release: `0.3.0` (server and web)
- NubArca TV: `1.0.1`, `versionCode` 2, OTA runtime `nubarca-tv-native-2`
- Backend: ASP.NET Core / .NET 10, EF Core, PostgreSQL 17
- Frontend: React, TypeScript, Vite
- Runtime: Docker Compose with separate API, worker and frontend services
- Storage: local content-addressed blobs with database-owned logical paths
- Installation locations are operator configuration, never source constants:
  `NUBARCA_PRODUCTION_SSH`, `NUBARCA_PRODUCTION_CHECKOUT`,
  `NUBARCA_PUBLIC_ORIGIN`, `NUBARCA_STORAGE_ROOT`, `NUBARCA_SERVICE_ROOT`,
  `NUBARCA_IMPORT_ROOT`, `NUBARCA_TV_APK_DIR` and
  `NUBARCA_ENCRYPTED_BACKUP_TARGET`, validated by
  `scripts/lib/operator-config.sh`, which fails closed on a missing value.

## Development rules

- Read `CLAUDE.md`, `ARCHITECTURE.md` and this file before repository work.
- Preserve the storage, privacy, ownership and reference-count invariants in
  `ARCHITECTURE.md`.
- Add migrations for schema changes and verify both the upgrade and runtime paths.
- Keep fast tests representative; do not weaken assertions or coverage to make
  the suite faster.
- Run `scripts/check-nubarca-identity.sh` before committing. It asserts the
  NubArca identity contract and fails on any installation-specific value — an IP
  literal, a `login@host` target, a public hostname, a `NUBARCA_*` variable that
  falls back to a path or URL, or a `cd` into a host checkout directory.
- Read `deploy/FAST_DEPLOY.md` in full immediately before any production
  deployment, rebuild, release-pin change or production migration.

## Standing decisions worth knowing

These describe current behaviour, not history. Each is easy to "fix" wrongly.

- **Semantic search is uncalibrated by design.** `SemanticResultPolicy`
  implements a score floor, a soft limit and a safety bound, but the thresholds
  are DISABLED and effective behaviour is a deterministic top-300 cut. Live
  search runs the real 1152-dimension SigLIP2 profile while the automated
  fixtures run the deterministic 32-dimension backend, whose scores cannot
  calibrate it. Disabled means disabled, never an implicit zero; `IsCalibrated`
  reports the mode. Calibration needs a representative corpus and human relevance
  judgements — it is not a defect or a release follow-up.
- **Candidate coverage is unbounded; results are bounded.** Ranking walks the
  whole eligible set by keyset paging into a fixed-capacity accumulator, so peak
  memory follows the result limit rather than library size. A test asserting
  `total > 300` would be wrong: coverage is proven by *which* results come back,
  never by how many.
- **The ranking cache is owner-keyed and invalidated on album mutation.** The key
  is `(ownerUserId, fingerprint)` — owner is part of the key, not a check
  afterwards, so a replayed cursor cannot address another account's ranking.
  Album membership is edited from inside the grid the cached ranking describes,
  so `AlbumService` mutations call `InvalidateOwner`; a TTL alone made the
  product feel broken for up to a minute.
- **Album membership is a physical filter, applied before ranking.** It lives in
  the query fingerprint, so a ranking built with the filter off can never be
  served with it on.
- **Video face analysis is generation-gated, not read-gated.**
  `Ai:VideoFaceAnalysis:Enabled` governs post-segmentation scheduling and
  backfill execution only. With it off, every persisted track, decision,
  person-video result and co-presence answer stays readable, and
  assign/ignore/clear keep working. Enabling generation is an operator capacity
  decision, not outstanding development.
- **Nothing automated writes a person decision.** Suggestions are advisory and
  never persisted; there is no auto-assignment job and no way to create a person
  from a track.
- **Co-presence requires temporal overlap within one canonical analysis**, with a
  strict half-open predicate and deliberately no tolerance derived from the
  sampling interval — a query about persisted evidence must not change answer
  when an operator retunes sampling.
- **Video face tracks persist no crop.** The representative timestamp and
  normalized bounding box are stored instead, so a crop is regenerated from the
  immutable original on demand.
- **HLS master playlists list the highest rendition first** (the `-var_stream_map`
  order). hls.js re-sorts by bitrate, so nothing of ours may assume either order;
  the level selector sorts by pixel count itself.
- **`Retry-After` is a minimum wait, not an appointment.** The preparation
  endpoint is stateless and cannot estimate a transcode, so it sends a small
  constant and the client takes `max(localRamp, header)`.
- **`MediaItem.takenAt` falls back to `CreatedAt`.** Only
  `FileMetadata.effective.dateTakenSource` distinguishes a real capture date, so
  the viewer suppresses the `uploaded` source rather than presenting it as a
  Date Taken.
- **A reverse proxy must forward `Host $http_host`, never `$host`.** The CSRF
  middleware rejects a state-changing `/api` request whose `Origin` disagrees
  with the request's own scheme/host/port, so `Request.Host` has to be the
  address the browser actually used. `$host` drops the port: the API then infers
  80/443, disagrees with a browser on any other port, and answers `403` to every
  write — login included, which presents as a login form that silently does
  nothing. An installation on `:443` never sees it, because the stripped port and
  the inferred one agree. `deploy/nginx.conf.example` and `nginx.e2e.conf` both
  carry `$http_host`; the browser E2E gate runs on `:5273` precisely so a
  regression here fails a test instead of an operator's first login.
- **A production TV build fails closed without its pinned origin.**
  `app.config.js` is evaluated twice — once by `expo prebuild` and again by the
  Gradle JS-bundling step — and only the second decides what the shipped app
  talks to. Both `NUBARCA_PUBLIC_ORIGIN` and `NUBARCA_TV_OTA_UPDATE_URL` are
  required under `NODE_ENV=production`.
- **OTA isolation is structural.** Publications and channel pointers are keyed by
  runtime version, so bundles built for one native contract cannot be offered to
  a device asking for another.
