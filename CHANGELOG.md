# Changelog

This changelog begins at the NubArca product baseline. Earlier development
happened under a different product name; that history is preserved in the
originating repository and is deliberately not reproduced here.

## Unreleased

### Identity & Access

Authorization moved from a single `IsAdmin` boolean to roles and feature
permissions, and password recovery by email arrived alongside it. No public
registration was added.

The model is deliberately `USER → ROLE → PERMISSIONS`, with nothing in between:
a user holds exactly one role, the role owns its permissions, and there is no
per-user exception anywhere. When a different combination is needed, the
operator creates another role — a thing they can name, describe and reason
about — rather than an invisible exception on one account.

- **Roles are rows** — created, renamed, described, duplicated and deleted by an
  administrator. Three are built in and cannot be deleted: `Administrator`,
  `Member`, `Restricted`. `Administrator` is immutable and always holds the
  whole catalogue. `Member` carries every non-administrative permission by
  default, because that is what every pre-role non-admin account became:
  existing users keep exactly the access they had. A role's identity is an
  immutable server-generated key, so renaming one never re-points a single user.
- **Permissions** — one catalogue of thirteen feature-surface keys
  (`people.access`, `semantic-search.access`, `laboratory.access` and its two
  sections, `cloud-functions.access`, `private-vault.access`, `tv.manage`, and
  five separate administration permissions). The catalogue is authoritative: an
  unknown key is rejected server-side and never stored, and so is a Laboratory
  section without the Laboratory shell.
- **No privilege escalation** — `admin.roles.manage` can only ever be held
  through the Administrator role, assigning that role requires holding it, and
  any other role may only be assigned by somebody who already holds everything
  it grants. A user manager therefore runs ordinary accounts and can never
  promote anybody — including themselves.
- **Server-side enforcement** — People, semantic search, the Laboratory and its
  sections, Cloud Functions, the Private Vault, TV device management and each
  administration surface are gated by ASP.NET Core policies. Frontend hiding is
  UX only. Ordinary search, files, media, albums, sharing and trash stay open to
  every authenticated user, so a Restricted account keeps the whole core
  personal cloud — and semantic search is refused without disabling the media
  endpoint that also supports it.
- **Immediate effect** — the authorization handler reads current database state
  per request, so assigning a role, or editing one, applies to every affected
  user on their next request, with no re-login and no second session subsystem.
- **Session versioning** — a credential change (self-service, admin reset, or a
  completed recovery) increments `User.SecurityVersion` in the same transaction
  as the new hash, invalidating sessions opened with the old password. Changing
  your own password signs out your other devices, not the browser you used.
- **Password recovery by email** — opt-in and OFF by default. The request
  endpoint answers one generic message for every case, so it discloses nothing
  about which addresses exist; it is rate limited per source IP and per
  normalized email. Tokens carry 256 bits of entropy, are stored only as a
  SHA-256 digest, are single-use with a short expiry, and travel in the URL
  fragment so they never reach a reverse-proxy log. A reset does not sign the
  user in. With mail unconfigured, authentication is unaffected and the
  administrator's manual reset remains the recovery path.
- **Richer profiles** — first/last name, time zone, last login and
  password-changed-at, editable by the user and by an administrator. Email
  remains the login and recovery identity and is not editable from either.
- **Admin user management** — a list of accounts with one way in, instead of a
  row that grew a button per capability. Creating a user opens a real modal over
  the page; managing one opens a side sheet whose Profile / Access / Security
  are tabs, so Security is never behind a long scroll. Access offers the role
  and a read-only preview of what it contains, read from the role itself:
  choosing a different role explains it immediately, before anything is saved.
- **Roles administration** — a first-class `/admin/roles` destination behind its
  own permission. Grouped, described check cards rather than a technical table;
  every change is a draft until one deliberate Save, applied atomically with
  optimistic concurrency; enabling a Laboratory section enables the Laboratory
  with it; a role in use says how many people a change affects, and cannot be
  deleted until they are reassigned.
- **Operator CLI** — `users set-role --email <addr> --role <role>` assigns any
  role this installation has, by key or by name, and refuses to demote the last
  active administrator. `users revoke-admin` now returns an account to `Member`
  rather than removing its feature access.

Migration `AddRolesPermissionsAndPasswordRecovery` adds `RoleKey`, backfills
`Administrator` from `IsAdmin` and `Member` for everybody else, then drops
`IsAdmin`. `MakeRolesFirstClass` then creates and seeds the role tables, reads
the per-user exception rows, turns every distinct effective permission set into
a real role — reusing an existing role where one already matches, and one
`Migrated access N` role for every user who resolved to the same set — and only
then drops the table. Both orderings are deliberate and covered by PostgreSQL
integration tests: for every account, the permissions in force after the upgrade
equal the permissions in force before it. `isAdmin` survives only as a computed
compatibility field on `/api/auth/me` and the admin import user picker; nothing
stores it.

### Google Cast

A video can be sent to a Chromecast, a Google TV, an Android TV or any other
certified Google Cast receiver, from Chrome on a desktop or on Android. The
browser stays a real remote control, and changes made on the television flow
back into it. See [docs/google-cast.md](docs/google-cast.md).

The television has no NubArca session and cannot be given one, so NubArca
authorises *before* playback instead of trying to authenticate the receiver.

- **A temporary, single-video grant** — 256 bits of CSPRNG entropy, returned once
  and held only in the sender tab's memory; the database stores the SHA-256
  digest and nothing else. It reaches one file, for one user, for six hours by
  default (`Cast__GrantLifetimeMinutes`, clamped to 30–720). Never written to web
  storage, browser history, a log or an audit payload.
- **A grant is not standing authority** — every Cast request, including every HLS
  segment, re-reads the grant, the account, the permission and the file. Removing
  `cast.access` from a role or disabling an account stops the *next* segment,
  with no re-login: the same "permissions change on the next request" contract
  every other endpoint has. Every failure answers one indistinguishable `404`.
- **`cast.access`** — a fourteenth catalogue key, *Trasmissione su TV*. Held by
  Administrator and Member (the migration adds it to an existing Member role);
  never granted to a custom role an operator built themselves.
- **The existing video contract is untouched** — `/api/files/{id}/video` and its
  ladder routes stay cookie-only and owner-scoped. Cast is a separate route
  family that reuses the same HLS pipeline, with no second transcoder and no
  second copy of the media. The Cast master *and* every variant are rewritten to
  signed grant-scoped URLs, because HLS resolves a relative URI against the
  playlist's URL and discards the query — an unsigned variant would stall the
  receiver on its first segment. Every URI is validated against the storage
  layer's own whitelist and a playlist with one bad URI is rejected whole.
- **CORS is not enabled globally** — one policy, attached to the Cast media
  routes and nothing else, echoing only the exact origins an operator configured.
  Never a wildcard: these URLs carry a bearer secret.
- **The secret stays out of logs** — it must ride in a URL, so it rides in the
  query string, and `deploy/nginx.conf.example` ships a token-free access-log
  format for `/api/cast/media/`.
- Videos only. Google's Default Media Receiver, so no custom receiver and no Cast
  Developer Console registration. Chrome on iPhone and iPad is not supported by
  the Google Web Sender — every iOS browser is WebKit underneath.

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
