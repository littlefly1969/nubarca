# Changelog

This changelog begins at the NubArca product baseline. Earlier development
happened under a different product name; that history is preserved in the
originating repository and is deliberately not reproduced here.

## 0.3.0

NubArca `0.3.0` is the consolidated product baseline: one coherent identity
across source, runtime, database and clients.

### The product

A self-hosted, local-first personal cloud for a single operator and their users,
running on one server through Docker Compose.

- **Storage** — originals are immutable and content-addressed by SHA-256, so
  identical content is stored once. Folders, albums, moves and renames are
  database-only operations that never rewrite a byte. Reference accounting is
  exact, and physical deletion happens only after the last reference is released
  and a configured grace period has elapsed.
- **Files** — owner-scoped files, folders, search, rename, move, Trash and
  restore. Missing and foreign resources are indistinguishable in responses.
- **Sharing** — revocable share links with expiry and download limits; albums
  with viewer, contributor and editor roles, plus detached album copies.
- **Media** — photo and video galleries with small thumbnails, medium previews,
  posters and metadata; a full-screen viewer; HTTP Range video streaming and
  optional adaptive fMP4 HLS.
- **Photo organization** — date-based foldering through previewable, cancellable
  background jobs. Moves are logical: originals, derivatives, metadata and
  shares survive intact.
- **Ingestion** — resumable staged browser uploads and resumable server-side
  imports with persisted per-item manifests, progress, cancellation and
  diagnostics.
- **Jobs** — a durable PostgreSQL-backed queue with leases, heartbeats, retries,
  bounded slices, priorities and cooperative cancellation.
- **Local AI** — semantic text-to-photo and text-to-video search, image
  similarity, face detection/embedding/clustering and owner-level People,
  including faces in videos. Everything runs locally and is disabled by default.
  Every derived artifact belongs to the owner of the file it came from.
- **Living room** — NubArca TV for Fire TV and Android TV: QR pairing,
  remote-first albums and slideshows, personal videos, and Party Mode refresh.
- **Laboratory** — Plates and the experimental Aesthetics Lab, owner-private and
  isolated from the main library. Disabled by default.

### Identity

One product name, applied everywhere it can be applied:

- solution, projects, assembly and namespaces are `NubArca.*`;
- operator configuration is `NUBARCA_*`;
- the PostgreSQL database and role are `nubarca`;
- Docker images, containers, network, named volumes and the Compose project are
  `nubarca-*`;
- container and host filesystem paths are `/var/lib/nubarca`, `/srv/nubarca`,
  `/mnt/nubarca`, `/var/cache/nubarca` and `/opt/nubarca`;
- backup archives are `nubarca-<timestamp>`;
- browser storage keys are `nubarca.*` and the photo-export folder is
  `NubArcaExport`;
- persisted logical container keys are `__nubarca_plates_` and
  `__nubarca_aesthetics_` (migration `RenameLogicalContainerKeyPrefixes`).

`scripts/check-nubarca-identity.sh` asserts this as a positive contract, and
`NubArcaIdentityTests` runs it inside `dotnet test`. The contract states what the
product is — solution, assembly, namespaces, package identifiers, cookie prefix,
Compose resource names, release version — so any drift fails, and it carries no
compatibility allowlist.

Installation-specific values are operator configuration rather than source
constants — the public origin, the PostgreSQL credentials, the hash peppers and
the TV signing material all live outside Git. Where a source constant previously
pinned the production origin, the pin is preserved and still fails closed: a
production TV build or an OTA publication with the origin unset is refused rather
than defaulted.

### Breaking changes

- **Every web user is signed out once.** The authentication cookie is now
  `NubArca.Auth`.
- **Every paired television must be paired again once.** The TV session cookie is
  now `NubArca.TvSession`. An Android `applicationId` cannot be renamed and the
  released APK is not rebuilt, so a single re-pair was accepted rather than
  carrying the former cookie name forward indefinitely.
- **The one-shot browser-preference migration is removed.** Theme, language and
  navigation preferences already moved to `nubarca.*` keys; a browser that has
  not loaded the app since then returns to the defaults (dark theme, Italian,
  expanded navigation).
- **The unadvertised pre-rename APK download alias is removed.** The canonical
  artifact is `/download/tv/nubarca-tv.apk`, with `/tv.apk` as a
  remote-friendly short alias.
- **Operator configuration is renamed.** Every environment variable that carried
  the former product prefix is now `NUBARCA_*`, with no fallback read of the old
  spelling. `.env` must be migrated before the new images start, or configuration
  silently reverts to defaults.
- **The PostgreSQL database and role are renamed** to `nubarca` by an in-place
  rename. Connection strings and backup/restore configuration must match.

### Native clients

Native clients version independently of the server and were not advanced:

| Client | Version | Runtime |
| --- | --- | --- |
| NubArca TV | `1.0.1` (`versionCode` 2) | `nubarca-tv-native-2` |

The released TV package `it.littlefly.nubarca.tv`, its signing certificate, its
OTA runtime contract and its published APK are unchanged.

### Verification

- **Browser end-to-end gate.** `tests/e2e` drives real browser engines against
  an ephemeral, self-contained stack that needs no credentials and never
  contacts an installation. The release gate is 72 Chromium tests across
  desktop, mobile and effective-200% layout, run against the *production-built*
  frontend behind a same-origin nginx front door rather than a development
  server. A run counts as passed only when the Playwright exit code, the JSON
  report's own totals and the required test count all agree.

### Fixed

- **Login on an installation served from a non-default port.** A reverse proxy
  configured from `deploy/nginx.conf.example` forwarded `Host $host`, which
  drops the port. The CSRF origin check then compared the browser's `Origin`
  against an inferred port 80/443, disagreed, and rejected every state-changing
  `/api` request with `403` — so the login form appeared to do nothing at all.
  Both the example proxy and the E2E front door now forward `$http_host`. An
  installation on `:443` was never affected. The check itself is unchanged: it
  was being given a host that had lost the port, not applying the wrong rule.
