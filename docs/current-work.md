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

- **Authorization is permissions; roles are only bundles.** An endpoint names a
  permission (`.RequirePermission(Permissions.PeopleAccess)`), never a role, and
  the handler reads the database on every request — which is why a role or
  permission change takes effect on the next request with no re-login and no
  second session subsystem. Two things are easy to undo by accident. The
  authorization handler is registered **scoped**, so it receives the request's
  own service provider; as a singleton it would capture the ROOT provider and
  answer every later request from the first one's cached permissions, and a role
  change would appear not to work at all. And **Member carries every
  non-administrative permission** — that is the migration contract, because every
  pre-role non-admin account became a Member. Adding a key to the catalogue
  without adding it to Member silently removes a capability from every existing
  account.
- **An Administrator's authority is not deniable.** The resolver ignores
  overrides entirely for an Administrator, and the admin API refuses to store a
  deny of an administrative permission on one. Without both, one administrator
  could quietly remove another's ability to put it back.
- **`SecurityVersion` invalidates sessions; permissions do not need it.** A
  credential event (self-service change, admin reset, completed recovery) bumps
  the version in the same transaction as the hash, and the cookie carries the
  version it was minted with. A user changing their OWN password is re-issued a
  cookie at the new version, so they sign out their other devices and not the
  browser they are using. A cookie predating the claim is read as version 1 —
  deliberately the migration default, not "whatever the row says now", because
  adopting the current value would let a pre-upgrade cookie survive a password
  reset that happened after the upgrade.
- **The recovery token lives in the fragment, and only as a digest.** The reset
  URL is built on the operator's `Mail__PublicOrigin`, never the request's Host
  header, and `#token=` is never sent to a server — so it cannot reach a
  reverse-proxy access log. The database stores `SHA-256(raw)` only. The request
  endpoint answers one generic 202 for a real address, an unknown one, a
  disabled account and a failed delivery alike; the service returns nothing, so
  there is no result for an endpoint to leak by accident.
- **The authenticated shell owns the viewport; `.app-main` owns the scrolling.**
  `.app-shell` is exactly one dynamic viewport tall, the top bar and the sidebar
  are rows of it, and `.app-main` is the only box with `overflow-y: auto`. The
  sidebar therefore holds no copy of the top bar's height. This is an API and not
  only a look: with the document stationary, anything that measures scrolling has
  to be told where it happens, which is what `AppScrollProvider` /
  `useAppScrollViewport` carry. Two consequences are easy to undo by accident —
  the media wall virtualizes against that element (`useWindowVirtualizer` there
  would read an offset that never changes and mount the first rows forever), and
  the pagination sentinel's `IntersectionObserver` is rooted in it, because a root
  margin never expands an intermediate clip and a document-rooted observer would
  lose its whole 1400px preload lead to `.app-main`'s overflow clip.
- **A new result identity starts at the top; a presentation change never moves the
  scroll.** The media workspace resets `.app-main.scrollTop` when the query
  fingerprint changes — tab, scope, search, filters, sort — and only then.
  Opening or closing the viewer, editing metadata, toggling a selection and live
  patches deliberately leave the position alone, which is why the reset is keyed
  on the fingerprint rather than on a render or a route.
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
