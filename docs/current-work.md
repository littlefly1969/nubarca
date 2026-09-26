# NubArca current baseline

Short, current-state context for development agents. This file is **not** a work
log: it carries no slice narratives, no branch names, no commit SHAs and no
"next step" notes. Released work is described by `CHANGELOG.md`; how the system
is built is described by `ARCHITECTURE.md`.

## Baseline

- Release: `0.3.0` (server and web)
- NubArca TV: `1.0.11`, `versionCode` 13, OTA runtime `nubarca-tv-native-12`
- Backend: ASP.NET Core / .NET 10, EF Core, PostgreSQL 17
- Frontend: React, TypeScript, Vite
- Runtime: Docker Compose with separate API, worker and frontend services
- Print foundation: server-owned stations/devices/jobs plus a separately
  packaged headless Print Agent `0.2.3`; Linux fake-agent systemd instances
  cover protocol acceptance while DNP DS620 still requires Windows hardware
- Party printing: guests compose a 10x15 photo or a four-photo strip (printed as
  two twin strips on one sheet) on their own print-capability token. Per-product
  budgets are independent and server-authoritative, reservation and per-party
  numbering are one atomic update, and submission is idempotent by contract. To
  the Print Agent these are ordinary `10x15` jobs
- CI: GitHub Actions verifies identity, backend, frontend, TV and mobile on pull
  requests and `main`; the external backend lane runs nightly or on demand; a
  separate manual, `main`-only native TV workflow builds and validates the
  definitively signed APK and optionally publishes an immutable GHCR bundle. A
  separate manual, `main`-only OTA workflow is the sole ordinary OTA signer and
  publishes a signed immutable GHCR bundle without contacting production. A
  third manual, `main`-only Print Agent workflow tests and emits self-contained
  `win-x64`/`win-arm64` Windows Service or `linux-x64` fake-simulator bundles.
  Production pulls verified application/APK/OTA artifacts by digest; the guided
  `deploy/update-production.sh check|apply` path can back up and apply explicitly
  confirmed, policy-approved additive migrations and never builds on the server
- Storage: local content-addressed blobs with database-owned logical paths
- Installation locations are operator configuration, never source constants:
  `NUBARCA_PRODUCTION_SSH`, `NUBARCA_PRODUCTION_CHECKOUT`,
  `NUBARCA_PUBLIC_ORIGIN`, `NUBARCA_STORAGE_ROOT`, `NUBARCA_SERVICE_ROOT`,
  `NUBARCA_IMPORT_ROOT`, `NUBARCA_TV_APK_DIR`,
  `NUBARCA_TV_OTA_STORAGE_ROOT`, `NUBARCA_TV_OTA_CERTIFICATE`,
  `NUBARCA_TV_NODE` and
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

- **The Party workspace is SEVEN sections, and the same seven in every phase.**
  Riepilogo, Esperienza, Ospiti, Foto, Attività, Schermi e stampa,
  Impostazioni — plus Live, the one section that appears while the party is
  happening and goes away when it ends. What changes with the lifecycle is
  which section a host lands on, what each leads with and which steps are still
  open, never the shape of the product; there is deliberately no `PartyType`
  and no mode switch. `frontend/src/party/workspace/partyWorkspaceModel.ts`
  decides the sections, the landing section, the one contextual action, the
  steps and the problems as PURE FUNCTIONS, so two surfaces cannot disagree and
  none of it needs a browser to test. Three things are easy to undo by
  accident. The summary's action is not the lifecycle transition: a draft with
  no album is not one button away from a party, and while the album's settings
  have not arrived the intent is `unknown` and the panel renders a placeholder
  rather than guessing. Every section but the guest console reads the guest
  directory with `take: 0` — the COUNTS alone — so no name reaches a surface
  that is not the console. And an open party (no guest list) is a complete
  party: it is offered "presenze registrate" and never "Attesi 0 · Mancano 0".
  See [party-workspace.md](party-workspace.md).

- **A party can be run by somebody who has no NubArca account, and that is
  deliberately not a narrow user role.** `PartyCollaborator` is an identity
  inside ONE party; its capability vocabulary
  (`PartyCrewCapabilities`) is disjoint from `PermissionCatalog`, and nothing
  converts between them. Pairing takes two factors — a personal link whose token
  rides in the URL fragment, and a six-digit code emailed to the address the
  HOST chose — because a link forwarded in a chat must not be access. The stored
  proof of a code is keyed (`HMAC-SHA256`), never a bare hash, and the signing
  secret has no built-in value: the API refuses to start without
  `Party__CollaboratorOtpSecret` or an explicitly configured
  `Party__TokenSecret`. At most TWO devices per collaborator, counted on grants
  (so one phone can help at two parties) and serialised on the collaborator's
  own row so two simultaneous pairings cannot both take the last slot; hitting
  the limit is not an error — the challenge stays verified and freeing a slot
  finishes the pairing with no second code. Nothing is cached into the cookie,
  so a revoke, a role change or a permission taken off the HOST takes effect on
  the next request. Every crew route is under `/api/party-crew` and carries no
  party, album or owner id at all. Three things are easy to undo by accident:
  the façade must keep naming no ids, `AuditActor` must keep recording a
  collaborator as a collaborator rather than as the host, and a role without
  `guests.read` must keep making NO guest request — not a request that gets
  refused. See `ARCHITECTURE.md` §14.3.9.

- **The party's layout is measured in a real browser, not asserted in jsdom.**
  `frontend/src/party/workspace/partyWorkspace.fixtures.tsx` renders the real
  components against mocked responses and `frontend/scripts/check-party-workspace-layout.mjs`
  opens each state in headless Chromium at 320/375/430/820/1440 to assert that
  nothing overflows, no target is under 44px, the side gutter holds and two
  sticky regions never cover each other. It is a dev/QA tool (it needs a
  Chromium binary), and it is what caught a 28px switch, a button whose
  `min-height` did nothing because it never declared a display, and a section
  rail that never scrolled its own selection into view.

- **A production migration is automated only by an explicit compatibility
  contract.** `deploy/migration-policy.json` does not guess from generated SQL:
  each migration must be newly added, explicitly approved, and state that the
  previous application remains compatible with the upgraded schema. From its
  `classificationRequiredFrom` boundary onward, the TV operational tests require
  every migration to carry an explicit policy entry, even when that entry says
  automation is forbidden; a missing classification therefore fails CI instead
  of first surprising an operator during production `check`. The guided
  updater verifies backup capacity during `check`; `apply --confirm-migrations`
  pulls and verifies the candidate image, creates and
  validates a PostgreSQL dump, applies with that image, verifies migration
  history, and only then changes pins. Existing migration rewrites and missing
  or incompatible policies fail before production mutation. The old image may
  be restored after a smoke failure only because that compatibility was
  reviewed; the script never automatically reverses schema.

- **A print-agent retry is not permission to print twice.** The server owns the
  job and lease; the Windows station journals `submitting` locally immediately
  before invoking a driver. A missing result acknowledgement retries only the
  acknowledgement. A restart or exception across the driver boundary becomes
  `delivery-unknown` and requires an owner decision. Enrollment and station
  credentials are separate CSPRNG capabilities stored server-side only as
  digests; the station credential cannot act as an owner or browse files.

- **Authorization is permissions; a user holds exactly one role and the role owns
  its permissions.** An endpoint names a permission
  (`.RequirePermission(Permissions.PeopleAccess)`), never a role, and the handler
  reads the database on every request — which is why assigning a role OR editing
  one takes effect on the next request, for everybody in that role, with no
  re-login and no second session subsystem. There is deliberately **no per-user
  exception**: a grant/deny model was implemented and then removed, because
  `USER → ROLE → PERMISSIONS` has one answer per person that is also the answer
  for everybody else in that role. If a different combination is needed, make
  another role. Two things are easy to undo by accident. The authorization
  handler is registered **scoped**, so it receives the request's own service
  provider; as a singleton it would capture the ROOT provider and answer every
  later request from the first one's cached permissions, and a role change would
  appear not to work at all. And **Member carries every non-administrative
  permission** by default — that is the migration contract, because every
  pre-role non-admin account became a Member. Adding a key to the catalogue
  without adding it to Member silently removes a capability from every existing
  account. (Member's set is the operator's after seeding: the seeder creates it
  if missing and never rewrites it.)
- **An Administrator's authority is not deniable, and cannot be delegated into
  existence.** The resolver returns the complete catalogue for an Administrator
  without querying at all, so no missing row and no edit can strip it; the
  Administrator role itself refuses every edit and delete. `admin.roles.manage`
  is marked Administrator-only in the catalogue, so it can never be put on
  another role — and assigning the Administrator role requires holding it. A
  user manager with `admin.users.manage` alone may therefore only assign roles
  whose permissions are a subset of their own, at creation time as well as at
  assignment time. Without all of that, one administrator could quietly remove
  another's ability to put it back, or a user manager could mint themselves a
  role that grants administration.
- **A role preview is rendered from ROLE data, never from a user's detail.** The
  admin Users page once carried a per-user permission list beside the role
  selector; changing the role refreshed the user object and left that list
  describing the PREVIOUS role. `AdminUserDetailDto` therefore carries no
  permissions at all — the Access tab reads the role catalogue, which is also
  what makes "select another role, see what it contains" true before saving.
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
- **The topmost modal owns the keyboard.** Viewers (media, face context, vault)
  register their shortcuts on `window` so the photo answers arrows wherever focus
  is — which means a modal opened on top of one used to page the photo behind it
  with the arrows meant for its search field, and one Escape closed both.
  `keyboardOwnership.ts` states the rule from both ends: a topmost modal consumes
  `isModalOwnedKey` keys in the CAPTURE phase (`stopPropagation` from a listener
  on the same target does not stop a SIBLING listener), and a viewer ignores any
  key whose nearest `[role="dialog"][aria-modal="true"]` ancestor is not its own
  root — compared against the root, because a bare "is there an aria-modal" check
  would match the viewer itself — plus arrows from an `isEditableKeyboardTarget`.
  Nothing is ever `preventDefault`-ed: caret movement, selection, typing and IME
  stay the browser's. `Tab` is deliberately NOT owned, so focus traps keep
  working. `Overlay`'s `ownsKeyboard` consumes Escape even when `dismissable` is
  false, since an overlay refusing to close must not close the viewer behind it.
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
- **There is ONE album destination, and ownership is a property of a card.**
  `/albums` renders the caller's own albums and the accepted shares together;
  `All / Mine / Shared` lives in the URL (`?scope=shared`), `/shared-albums`
  redirects there, and `/shared-albums/{id}` is untouched — owner and recipient
  must never resolve to one route. The two API shapes stay two shapes and are
  normalised only at the presentation boundary (`albums/albumCardModel.ts`),
  which is what keeps "whose album is this" explicit while the grid is uniform.
  Two things are easy to undo by accident. `ownerKind` is STATED by whichever
  constructor built the card, never inferred from a field being absent, so a
  shared album cannot drift into looking owned; and a shared summary carries no
  per-kind split, so the card renders no photo/video counts for it rather than
  rendering zeroes the server never said. Pending invitations and received
  copies stay in their own sections: an invitation is a decision, and a copy is
  an album nobody can revoke — neither is a thing you can open.
- **The recipient's album is the SAME browser, over a different authority.**
  `SharedAlbumBrowser` reuses `MediaKindTabs`, `useJustifiedWall`,
  `useWallSentinel` and the common `MediaViewer`; the bespoke shared lightbox is
  gone. What separates it from the owner's workspace is not components but
  `albums/albumCapabilities.ts`, a pure model whose owner-only entries are false
  for every membership role — actions a caller may not perform are ABSENT from
  the tree, never disabled. `MediaViewer` therefore takes its media SOURCES
  explicitly (`ownerFile` | `albumScoped`), with no fallback from the second to
  the first: a shared item with a missing URL renders as unavailable rather than
  quietly becoming `/api/files/{id}/...`, which the recipient holds no grant on.
  Curation follows the server's `canEdit`, never the role string.
- **Album Play plays what is on screen.** One `useSequencePlayback` drives both
  the owner's workspace and the recipient's browser over the CURRENT result —
  tab, search and filters included — so "Videos, then Play" plays the videos and
  nothing else. Photos hold for a bounded moment; a video advances when it ENDS,
  never on a clock, because a timer would cut off anything longer than the
  interval and linger on anything shorter. Running out of a loaded page is a
  `wait`, not a `finish`: Play must not end early because pagination has not
  caught up. It mutates nothing, which is what makes it safe for a shared
  Viewer — and it is not Party and not Show-on-TV, which stay owner publication
  settings in Settings.
- **`GET /api/shared-albums/{id}/items` is a PAGE, and `kind` is its only
  filter.** The envelope carries `items`, `nextCursor` and the album's per-kind
  counts, so a tab label needs no second request and does not change meaning
  with the tab that is open. `kind` is safe precisely because it is nothing new:
  it is answered from the media category the shared item shape already carried.
  A filter that needed owner-private metadata to answer would BE that metadata.
  The cursor is `(SortOrder, FileItemId)` bound to the kind it was issued for,
  and it is not a capability: membership is resolved BEFORE it is read, so a
  stranger's malformed cursor is a 404 rather than the 400 that would confirm
  the album exists.
- **The curation list reads the album a page at a time, and the version is the
  page boundary.** `GET /api/albums/{id}/content?limit=` answers one page plus
  `totalCount`. A continuation names the last row the client HOLDS as `cursor`,
  together with `expectedVersion`. A changed album answers `409`, and the client
  then re-reads from the top instead of stitching two versions together. The
  cursor is deliberately not opaque. After the curator's OWN confirmed edit, the
  client applies it locally (the server claimed exactly the version it holds)
  and continues after whichever row is now last. "Re-read after every edit" is
  therefore not the fix it looks like: it is the O(album) cost this replaced. A
  request naming none of `limit`/`cursor`/`expectedVersion` is the legacy
  whole-album read, kept for clients that predate paging. Reorder is
  `POST /api/shared-albums/{id}/items/{albumItemId}/move` (item, 0-based
  target, version). It renumbers one range set-based, keeps the order dense, and
  first renumbers, once, any album a copy numbered from zero. The complete-list
  `PUT .../order` remains server-side for older clients; the web no longer calls
  it. `AlbumSharedContentPanel` is ONE component for Owner and Editor. It is
  virtualized, with a single Actions control per row. While it is open, the
  page's wall (`MediaWorkspace` / `SharedAlbumBrowser`) is UNMOUNTED, not
  hidden.
- **There is ONE media-selection experience, and it is the Media Library.** A
  shared album's "Add from library" navigates to `/media` with the album in
  transient router state — never a URL, never a second route, never a fork of
  `MediaWorkspace`. Only `AlbumPickerModal` then decides WHERE: owned albums
  through `bulkAddAlbumItems`, shared Contributor/Editor albums through
  `bulkContributeToSharedAlbum`, with the two groups rendered as separate
  sections because whose album it is is a real difference. Viewer albums are
  absent rather than disabled. The predecessor was a shared-album-only photo
  grid, which is why "add a video to a shared album" was impossible and why the
  reachable media depended on the page you started from. `SharedAlbumDetailPage`
  deliberately keeps everything else: its album-scoped media URLs carry
  membership authorization, `allowOriginalDownload`, `canWithdraw` and
  revocation, none of which the owner's workspace knows about.
- **A bulk contribution reports counts, never ids.** `ContributeManyAsync`
  shares its authority check and its "contributable" query with the single-item
  path rather than restating them; the role gate answers the whole request (a
  Viewer gets `403`), and every per-file outcome — duplicate, already present,
  foreign, deleted, excluded, vaulted, non-media — collapses into `skipped`,
  because naming a skipped id would say whether it exists. Each item that lands
  still leaves the audit row a single contribution would have.
- **Video face analysis is generation-gated, not read-gated.**
  `Ai:VideoFaceAnalysis:Enabled` governs post-segmentation scheduling and
  backfill execution only. With it off, every persisted track, decision,
  person-video result and co-presence answer stays readable, and
  assign/ignore/clear keep working. Enabling generation is an operator capacity
  decision, not outstanding development.
- **Nothing automated writes a person decision.** Suggestions are advisory and
  never persisted; there is no auto-assignment job and no way to create a person
  from a track.
- **Reclustering exists twice, at two scopes, on ONE algorithm.**
  `ai-faces-cluster-backfill` is the administrator's: it walks every eligible
  owner. `ai.faces.cluster.owner` is the owner's own, started from the Cloud hub,
  and clusters EXACTLY the account that asked — no owner enumeration, no
  `SELECT DISTINCT OwnerUserId`, one job = one `ClusterOwnerAsync`. The owner id
  lives in the job payload, is written only server-side from the authenticated
  caller, and is re-read from that payload to decide who may watch the job, so
  the boundary travels with the work rather than with whoever asks about it. The
  status endpoint answers for one owner's own job and 404s (never 403) for
  anything else, so watching your own recluster never needs
  `admin.jobs.manage`. Refusing with 409 when the installation cannot cluster —
  AI off, clustering off, no face profile — is deliberate: a queued job that is
  certain to no-op would "succeed" and change nothing. `people.cluster.rebuild`
  is a FEATURE permission with `people.access` as its Parent, so Member carries
  it (derived from the non-administrative keys) and it grants no administration
  surface whatsoever.
- **A person is a TEMPLATE of 1–6 reference faces, not one arbitrary face.**
  Similar-face search once queried with whichever completed assigned embedding
  came back first, so suggestions for someone photographed across decades
  depended on a coin flip. `PersonFaceReference` persists up to
  `MaxPersonReferenceFaces` = 6 confirmed faces per (person, profile); the search
  runs one ANN query per reference at the SAME threshold and takes the **best**
  score per candidate. Three things are easy to undo by accident. The set is
  built by embedding DIVERSITY (`novelty * (0.5 + 0.5 * quality)`, stopping at
  candidates already covered above the configured default search threshold) —
  never by classifying age, film or colour, which would need a model that does
  not exist here; an ordinary person therefore settles at 1–3 references and only
  a genuinely wide appearance span reaches 6. It is bootstrapped **lazily** on
  the first search and maintained incrementally at assign time, so the only
  historical embedding scans are bootstrap and replenishment — a deploy starts no
  background face work and a normal search reads zero history, zero photos and
  runs zero inference. And the table is DERIVED: `PersonFaceAssignments` stays
  authoritative, an empty table is valid, and a reference that stops being a
  confirmed, surfaceable, embedded face of that person is dropped rather than
  trusted.
- **A correction invalidates the WHOLE reference set, which is then reselected
  from zero.** The selection is global over quality, diversity and coverage — #3
  is only optimal given #1, #2, #4 — so deleting the one row the owner disowned
  and topping the set back up leaves the survivors frozen in an arrangement
  chosen partly BECAUSE of a face that turned out to be somebody else. Every
  mutation that takes evidence away (remove from person, remove assignment,
  ignore, move to another person, group ignore, cluster assign with
  `moveAssigned`) calls `InvalidateSetsContainingFacesAsync` BEFORE the
  authoritative write — it queues the whole-set delete on the caller's unit of
  work and returns the affected `(PersonId, ProfileId)` keys, deduped — and
  `RebuildAsync` AFTER `SaveChanges`, so the reselection sees the assignments
  that actually remain. `MaintainAfterAssignAsync` is the GAINING side only. The
  result is 1–6 references, not necessarily 6 again: the selector stops when the
  person is covered, so 4/6 after a correction is a correct answer. A rebuild
  reads existing embeddings and writes at most six rows — no detection, no
  inference, no re-embedding, no reclustering — and leaving a set EMPTY on a lost
  race is deliberate, because the next `EnsureAsync` bootstraps it, whereas a
  partial stale set would never be repaired.
  `POST /api/people/{id}/reference-faces/rebuild` is the same path on demand.
- **An ignored face is not a candidate ANYWHERE.** It must not come back through
  similar-face search (filtered on the deduped ANN candidate ids, BEFORE ordering
  and paging, so a page is never short and a cursor never skips), as a suggested
  or review group's persisted representative (replaced read-side by the lowest-id
  surfaceable non-ignored member — displaying a list must not become a write), or
  in the group viewer. It stays owner-private and reversible: the row is an
  `IgnoredFace`, never a deletion of the detection, the embedding or the vector.
- **A similar-face proposal already on ANOTHER person is kept, and says so.**
  Only the CURRENT person's faces are excluded. A candidate the owner filed under
  someone else is exactly how a past mistake gets corrected, so `SimilarFaceDto`
  carries `AssignedPersonId`/`AssignedPersonName` (owner-scoped on both the
  assignment and the person) and the UI labels it and offers "Sposta qui" instead
  of "Aggiungi" — the backend already moves the assignment, and the action must
  not pretend it is an ordinary add.
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
- **A Cast grant is a capability, never a session.** It reaches one video, for
  one user, with an expiry — and every request re-reads the account, the
  `cast.access` permission and the file, so a role edit stops the NEXT segment.
  Two things are easy to undo by accident. The Cast route asks
  `VideoHlsServingService` for the **raw** master (`VideoHlsMasterForm.Raw`),
  because the owner form already prefixes `video/` and un-picking that would be
  exactly the unchecked string surgery the rewriter exists to avoid. And the
  **variant** playlists must be rewritten too, not just the master: HLS resolves
  a relative URI against the playlist's URL and DISCARDS the query, so an
  untouched variant sends the receiver at token-less segment URLs and it stalls
  on the first one. A password change deliberately does NOT revoke a grant —
  `SecurityVersion` signs other browsers out, and a television in the owner's
  own home is not a browser session. See [google-cast.md](google-cast.md).
- **`MediaItem.takenAt` falls back to `CreatedAt`.** Only
  `FileMetadata.effective.dateTakenSource` distinguishes a real capture date, so
  the viewer suppresses the `uploaded` source rather than presenting it as a
  Date Taken.
- **Exact-media cleanup is logical and owner-scoped.** The Cloud Function uses
  the immutable `BlobObject.Sha256` as full-file identity and only accepts
  server-detected image/video metadata. It keeps the oldest `FileItem.CreatedAt`
  (normalized full path, then ID, break ties) and sends every redundant
  `FileItem` through the canonical Trash transition. Private Vault, Trash,
  Party media and every other owner's logical files are outside the scan; a
  shared physical blob is never deleted by the function.
- **Permanent purge has ONE implementation, and it is opt-in.** Individual
  permanent delete, Empty Trash and retention expiry all run
  `IFileItemService.PurgeTrashedFileAsync`; `FileItemSweeper` owns only the
  schedule. A dependent row added to that cascade is therefore picked up by all
  three triggers at once — never add a retention-only deletion path. Reclaiming
  bytes needs BOTH `FileItemSweeper__Enabled=true` (logical row, grace from
  entering Trash) and `BlobJanitor__Enabled=true` (physical bytes, grace from
  the hard purge). Both default to `false`, so an installation that sets neither
  keeps every trashed file's bytes forever, however long the retention window.
- **A FileItem is pinned by any `Restrict` FK nobody deletes.** `photo_export_entries`
  and `print_job_sources` are append-only, so before the purge cascade covered
  them one export or print made a file permanently unpurgeable through every
  trigger. `PrintJob.FileItemId` is nulled rather than deleted: the nullable
  column exists so completed print history outlives its source photograph.
- **The janitor records `pending_blob_purges` before it unlinks.** The blob row
  (and its storage key) is deleted in the same transaction that writes the
  pending record; only a successful unlink clears it. Bytes are never removed
  before the row, because a concurrent same-SHA `StoreAsync` would skip the
  write and resurrect the row onto missing bytes.
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
- **A production TV build derives all endpoints from its pinned origin.**
  `app.config.js` is evaluated twice — once by `expo prebuild` and again by the
  Gradle JS-bundling step — and only the second decides what the shipped app
  talks to. `NUBARCA_PUBLIC_ORIGIN` is required under `NODE_ENV=production`;
  API base URL and OTA URL are derived from it plus `tv/release-contract.json`.
  APK keystore credentials remain a Gradle-only gate and are never OTA inputs.
- **A TV is paired only after its limited session is both valid and durable.**
  The native client extracts exactly `NubArca.TvSession=name-value` from
  `Set-Cookie` (never by splitting on the comma inside `Expires`), serializes
  local remove/write operations and uses that manual cookie as the sole fetch
  authority. A `paired` status alone never opens the menu: `/api/tv/session`
  must succeed and the exact cookie must finish writing to app-private storage;
  transient network/storage failures retry the same approved pairing until its
  deadline; pairing/session requests are aborted after 10 seconds and on screen
  exit or absolute expiry. A missing/invalid claim cookie requires an explicit
  new QR instead of claiming a connection that will disappear on the next cold
  launch.
  A legacy install whose session survived only in the native HTTP cookie jar
  (and not under the unchanged AsyncStorage key) therefore re-pairs once; JS
  cannot safely extract that HttpOnly value into the durable single authority.
- **Pairing says WHO a television is; assignment says WHAT it shows, and they
  are two things.** `TvSession.DisplayAssignment` is `general` or `party`, and a
  party assignment names a `PartyAlbumLink` — so a television moves between the
  ordinary NubArca TV experience and one specific party, or between two parties,
  with no second PIN, no second QR code and no walk to the television. The
  credential is untouched by any of it, which is the whole point: putting the
  assignment inside the pairing would have made "change what this screen shows"
  cost a re-pair. Six things are easy to undo by accident. The default is
  `general`, so every device paired before the columns existed keeps exactly the
  behaviour it had, and the migration needs no data script. A CHECK CONSTRAINT
  holds both halves — a party assignment always names a link, a general one
  never does — because a television switched back to general that kept its link
  would still be pointing at a party. The owner API takes an ALBUM, never a
  party link id: a link id is internal, and the album is what every other owner
  Party route is already scoped by, so there is no identifier a client could
  guess, replay or borrow from another account (the television and the album are
  BOTH re-matched against the caller in the same query, so a session from one
  account and an album from another can never meet). And the row names the LINK
  rather than the album, so a revoked party leaves the television saying "that
  party is over" instead of silently adopting whatever party the album has next
  — re-enabling party mode mints a new link, and a new party is a new party
  everywhere else in this feature. Deleting an album returns its televisions to
  `general` in `AlbumService`, because the assignment carries a restricting
  foreign key and would otherwise block the delete the way every Party table
  once did. The television READS its assignment on `/api/tv/session` and can
  never set one — those routes live outside `/api/tv`, where its path-scoped
  cookie is not even sent — and it FOLLOWS it: the assignment changes what the
  television shows (next entry).
- **An assigned television is taken over by its party, and the SERVER picks the
  surface.** Beside a party assignment `/api/tv/session` projects a
  `presentation` — `slideshow`, `game` or `unavailable` (`general` otherwise) —
  from the party's own state in `TvPartyPresentations.Decide`: `game` while a
  game is switched on, the host may run one and the party is LIVE (the Games
  capability is phase-folded: before or after the party the lobby's code would
  lead nowhere), from before the first match through every phase EXCEPT
  `intermission`; `slideshow` otherwise; `unavailable` when the display resolver
  refuses the link — fail
  closed, never general, never another party, never the new link of the same
  album. It is a PROJECTION: FINISHED stays FINISHED, the television returns to
  the slideshow because the projection says so once `FinishedDwell` (15 s from
  the session's `FinishedAt`, on the server's clock) has passed, an
  `intermission` returns it with NO dwell at all (a closing card is for a room
  that is not waiting; "back to the party" is said to one that is), and
  `restart_game → lobby` brings the takeover back with no special case. Five
  things are easy to undo by accident. The shell never learns a phase: it
  switches on four words and mounts one surface per word, keyed by an opaque
  `assignmentKey` (session + link digest, accepted by no endpoint) so Party A →
  B is always a fresh mount. The control plane is a five-second READ in the
  foreground whatever the television shows — a general one is the one waiting to
  be taken over — and the heartbeat POST, the only write, is the same read once
  a minute; the first read at boot already decides the first screen, at boot
  only a `401` unpairs, and a session is admitted into a party only after the
  Personal Area status says its association is complete (a PIN-less one still
  goes to the incomplete-pairing recovery, never straight into a party). The assignment PREEMPTS every local surface (mode
  selector, manual Party, Updates, PIN, Personal Area, Beauty Lab); preempting a
  personal screen is a LOCK in `flowEffects`, an unlock that lands after its PIN
  screen was preempted is revoked, and BACK at the root of an assigned party
  closes the app rather than returning it to general. The slideshow is the
  existing `ViewerScreen` behind a thin adapter, never a second slideshow. And
  an assigned television may read ITS party's album — items, greetings, media
  bytes — even when that album is not ShowOnTv: the grant is the assignment
  re-read on every request (`TvViewer`), for that one device and only while its
  party is display-resolvable by the control plane's own projection (never once
  the television would be told `unavailable`, whatever the reason), and the
  album LIST is unchanged, so an assignment is not a way to browse.
- **The display grant lives only while the game holds the screen, and the shell
  keeps it alive.** The server enforces the first half: a grant is minted, and
  honoured on every `/api/party-display/*` request, only while the party's
  projected presentation is `game` — through `ITvPartyPresentationService`, the
  control plane's own projection — so a party that is not live, a game switched
  off, or FINISHED past its closing card refuses a new grant and stops a live
  one at that instant, with nothing written. One live grant per television, minted under a write lock on
  the television's own session row, so two mints racing (a remount and a
  renewal) are ordered and never leave two usable credentials — a race proved on
  real PostgreSQL. The shell renews before expiry from `expiresInSeconds` (a
  server-measured duration; the device clock is not trusted), re-mints when the
  page reports `display-auth-failed` — a capability failure is not silence, and
  the heartbeat only ever means "the JavaScript is alive" — retries transient
  mint failures on a capped backoff while the presentation is still `game`,
  fails closed on a `404` and pairs on a `401`. The watchdog keeps the
  hardware-verified 2 s / 10 s / 2 s / 5 numbers and adds a 30 s first-heartbeat
  deadline, a slow retry that MOUNTS a real probe behind the native fallback
  (lifted only by heartbeat + first snapshot), a crash cycle forgiven after 60 s
  of health, idempotent death reports and top-level load errors. A native cover
  stays over the WebView until the page is alive and drawn. The page retries the
  lobby QR and activity photographs on a capped backoff, cancelled when the
  scene or the activity changes; keep-awake takes only the page's coarse
  `display-presentation` signal.
- **OTA isolation is structural.** Publications and channel pointers are keyed by
  runtime version, so bundles built for one native contract cannot be offered to
  a device asking for another.
- **The TV Personal Area secret is entered BLIND, and that is structural, not a
  masking rule.** The retired 6-digit PIN was shown on a visible keypad: masking
  the digits changed nothing, because the FOCUS RING walked from key to key and
  anyone in the room could read the code off the television. The directional code
  (`dpad-v1`: nine presses of UP/DOWN/LEFT/RIGHT/CENTER, 5^9 = 1,953,125 — about
  twice the numeric space) is safe only because the unlock screen contains no
  focusable secret controls at all, renders no symbol, and has no state that
  varies with which button was pressed; the remote diagram on it is static by
  contract. Nothing logs a symbol either — a debug line naming the direction
  would move the same leak into logcat. `TvPersonalPin.Scheme` names the
  generation so ONE row describes both: `pin-v1` still VERIFIES so an
  already-paired television keeps working, but nothing creates one, and the
  status endpoint's `scheme` is what tells a television to say "configure the new
  code from your account" instead of offering entry that can never succeed.
- **The TV browses the SAME query the web does — not a copy kept in agreement.**
  `/api/tv/personal/media` and `/api/tv/personal/albums/{id}/media` bind through
  `MediaCollectionQueryBinder` and run through `IMediaCollectionQueryService`,
  exactly like `/api/media`. Kind/filter compatibility (photo filters ⇒
  kind=image, video ⇒ kind=video, neither with kind=all), the library-only
  album-membership rule and the cursor fingerprint are therefore inherited, not
  reimplemented. `tv/src/personal/mediaWorkspaceQuery.ts` mirrors the web model
  field-for-field; the two packages cannot share code, so the SERVER is the
  safety net.
- **Fire TV D-pad navigation has ONE authority: native focus.** Every media
  wall uses the same proportional-row layout, computed before render from the
  dimensions in its DTO; loading and error placeholders occupy that exact box,
  so bitmap arrival never changes geometry. Row and item keys come only from
  stable content identity. A native focus guide traps LEFT/RIGHT inside each
  row; the vertical list uses item snap and `scrollAnimationEnabled={false}`, so
  a held D-pad produces native one-step repeats without cross-row lateral jumps
  or queued scroll animations. Never add a parallel JavaScript focus graph or
  repeat debounce, gate focus or paging on bitmap readiness, or move
  `additionalRenderRegions` with the focused row: virtualization and focus
  retention remain native concerns.
- **A TV media download is owned by its current subscribers, not by its first
  caller.** A tile leaving the virtualized window releases its pending demand
  and cache reservation immediately. Work for the same URL remains shared while
  any consumer is live; when a download slot opens, every orphaned waiter ahead
  of the current viewport is discarded in that same handoff and is never
  recorded as a media failure. Stale work therefore cannot block the current
  viewport after the next slot handoff, without reviving the first-consumer
  cancellation race that made live previews vanish.
- **Party face search is phone-local by default.**
  `Party:FaceSearch:TvActivationEnabled` is independent from the search switch:
  while it is false, local matching and cancellation keep working, old clients
  cannot activate a TV, TV polls return the normal inactive projection, and no
  TV-only face crop is stored. The dormant activation implementation is retained.
  If it is deliberately re-enabled, it targets one `owner + album` state, not a
  device: every paired TV showing that Party album receives it, and clearing it
  on one clears it for the others.
- **A Party browser upload requests Screen Wake Lock only while its XHR is in
  flight.** The first request stays inside the upload click for WebKit, a visible
  in-flight page reacquires after `visibilitychange`, and completion/unmount
  releases it. This is best-effort screen control, never a promise of background
  execution: explicit screen-off, browser closure, battery policy or OS page
  suspension can still interrupt the upload.
- **`BackHandler.exitApp()` does not close an Android app.** It maps to
  `Activity.moveTaskToBack(true)` — it BACKGROUNDS the task, which on a Fire
  Stick left NubArca in recents and resumed the old Activity on relaunch. The
  navigation root calls the native `Activity.finishAndRemoveTask()` through
  `NubArcaTvPlatform` (`tv/plugins/withTvPlatformModule.js`, re-applied on every
  prebuild because prebuild regenerates `android/`). "Closed" means the Activity
  is finished, the task removed, nothing playing and a relaunch creating a new
  Activity — NOT that the Linux process is gone. Never `System.exit` or
  `killProcess`.
- **A browsing tile never mounts a player.** Video tiles use derived STILL images
  only: poster → a derived still → an EXPLICIT "video, no preview" placeholder.
  A blank focusable rectangle is the failure mode to avoid. `previewStripUrl` is
  a six-cell 2880x270 sprite and is deliberately never used as a tile image. In
  the viewer exactly one `VideoPlayer` exists at a time (keyed by source), with
  an explicit Android buffer budget — the platform default byte budget is
  UNLIMITED, which on a constrained Fire Stick is a memory climb with no ceiling.
- **The `/video` delivery contract is ONE file, vendored three times.**
  `shared/video-delivery/videoDelivery.ts` owns the probe classification, the
  HLS-vs-progressive discrimination, `Retry-After` parsing and the retry policy;
  `scripts/sync-video-delivery.sh` copies it byte-for-byte into
  `frontend/src/video/`, `mobile/src/media/` and `tv/src/video/`, and each
  project's `videoDeliveryParity` test fails if its copy drifts or if a row of
  `shared/video-delivery/parity-matrix.json` classifies differently there. It is
  vendored rather than packaged because the three are independent npm projects
  with independent toolchains and no workspace root — wiring Metro watchFolders
  and a second tsconfig project into two Expo apps costs far more than the
  hundred lines it would share. The rules that used to drift, and must not
  again: **`200`/`206` are ALWAYS playable** (the MIME only says HLS vs
  progressive, so a typeless `206` is progressive, not "unavailable" — that
  mobile-only gate is what made real videos unplayable on Android); **a `202`
  has no attempt ceiling** (a long transcode is not a failure); `Retry-After` is
  a floor over the local ramp, never a replacement for it; and not-found, auth,
  transient and protocol stay four distinct verdicts even where one screen draws
  them the same. Both native clients pass `contentType` for BOTH containers —
  ExoPlayer cannot infer one from an extension-less `/video` URL — and the
  probed URL is always the played URL, so a shared album is never rewritten to
  an owner route. `node scripts/video-parity-table.mjs` prints the derived
  cross-consumer table.
- **The TV filter panel renders from a CATALOG, not from hand-written rows.**
  `tv/src/personal/tvFilterCatalog.ts` decides which filters apply to the
  current tab and source, how the remote edits each one, and whether each is
  active; `LibraryFilterPanel` draws what it returns. It holds no filter values
  — `MediaWorkspaceFilters` remains the single source of truth — and exists
  because the panel used to BE the list of filters, so a row written as a
  read-only summary (people: clearable, never settable) was a filter the
  television could not operate and nothing could notice. Two rules keep that
  from recurring: `TV_FILTER_OWNER` claims every field of
  `MediaWorkspaceFilters` through a `satisfies`, so a new domain filter does not
  compile until a TV row owns it, and `TvFilterEditor` has no read-only member,
  so a row the remote cannot operate is not expressible. The panel's half is a
  `Record<TvFilterId, RowView>`, so a row the catalog offers and the panel
  cannot draw is also a build failure. Applicability and
  `queryToWire`'s emission rules are two independent barriers over the same
  rule and are checked against each other in `tvFilterCatalog.test.ts` — a
  filter that is hidden must also be unsendable.
- **A party guest's quota is a server-issued participant session, and the claim
  is one SQL statement.** The party upload token is shared by everyone holding
  the QR, so it identifies the PARTY, not a person. `PartyParticipant` supplies
  the missing identity without fingerprinting: the server mints a random token,
  returns it as an HttpOnly cookie PATH-scoped to `/api/party/{uploadToken}`
  (which is what keeps two parties' allowances apart with one cookie name), and
  stores only its SHA-256. IP, User-Agent and any client-supplied id were all
  rejected — the first two are not identities and the third is a quota the
  client can reset. The counter is claimed by a conditional
  `UPDATE … WHERE Id = @id AND (@max = 0 OR Count < @max)` inside the upload's
  transaction, never COUNT-then-INSERT: two phones racing for the last slot both
  read "one free" but only one can win a row lock. Photo and video quotas are
  independent, `0` means unlimited in the domain (`null` on the wire), invalid
  media consumes nothing because the slot is claimed only AFTER the server
  decides what the bytes are, and moderation never refunds — hiding a photo is a
  visibility decision, and giving the slot back would let a guest re-upload the
  thing the owner just hid.
- **Party is its own root, and it is not the QR, the album, or the television.**
  `Party` is the EVENT; `PartyAlbumLink` is a public capability OF it, minted and
  revoked many times for one evening; `Album` is where its media happens to live,
  reached through `PartyMediaSource` (composite key `(PartyId, AlbumId)`, one
  validated role — `main` — with `official`/`guest-contributions`/
  `selected-memories` deliberately possible as rows rather than schema). One seam
  does the whole walk — `token → PartyAlbumLink → Party → PartyMediaSource(main)
  → Album` — and hands every existing service the `(ownerUserId, albumId)` pair it
  already handles correctly, which is why there is no `PartyMediaServiceV2` and no
  service carries a second PartyId-shaped copy of itself. `PartyAlbumLink.
  OwnerUserId`/`AlbumId` survive as a COMPATIBILITY PROJECTION of the party and
  its main source, written from them and authoritative nowhere. Three things are
  easy to undo by accident. **Status is descriptive, not a gate**: `draft →
  published → live → ended` is three moves in a pure `PartyLifecycle.Target`, the
  caller names an ACTION and a re-publish is refused rather than silently
  succeeding, and nothing moves a party on a clock — what closes a party to guests
  is still the link's own switches plus the owner's permission, so there is one
  answer to "why is this closed". **Enabling party mode is what CREATES the
  party** (`Album → Party Mode` finds or creates it, adds the `main` source and
  publishes a Draft, all in one transaction with the capability) — there is no
  second creator and no Party UI yet. And **Party no longer implies Show-on-TV**:
  enabling a party does not switch the album onto the owner's television, turning
  Show-on-TV off no longer revokes live QR codes, and the TV surfaces still
  require their own flag. The migration is classified NOT automated: `PartyId` is
  NOT NULL with a restricting FK the previous application cannot satisfy, so the
  cutover takes a short window in which Party is unavailable, in preference to a
  dual-write transition spread through the services.
- **One QR, three surfaces, and the SERVER picks which.** `/party/{token}` never
  changes and is never rotated at a lifecycle change: the same code is the
  invitation, the party and the memories. `PartyGuestExperience` is the whole
  rule — a pure function of status, the two windows and the clock — resolved at
  the same seam that resolves the token, with the phase FOLDED INTO the
  capabilities there. That fold is what keeps every endpoint's single existing
  check honest: a live capability outside the party is not a capability, so no
  endpoint grew an `if (status …)` and a surface the browser stops drawing cannot
  be reached by typing its route. `/items` and every media byte obey it too —
  hiding a gallery in a browser is not a rule. The ONE exception on the album
  route is the album's CHOSEN cover, which is the invitation's hero when the
  invitation has no photograph of its own and the only ALBUM file id that
  resolves before the party; a slot's own photograph is not album media and is
  reached only through its slot. With neither, the invitation gets a branded
  composition, not a broken frame. Three things stay separate throughout: status is the phase, the
  link is the capability and its revocation, and the windows are product
  decisions — a status never revokes a token, and a token is never invalid merely
  because the party has not started or has finished.
- **`LibraryAccessExpiresAt` is now read, and null is not a second window.** It
  decides how long the memories last: unset means "as long as guest access", a
  value may OUTLIVE guest access — which is the whole point of the After surface
  — and may also fall short of it, in which case the memories close and the
  thank-you stays. Once the party is over and guest access has closed, a still
  open library is `library-only`: the same QR narrowed to a greeting and the
  album, with no capability deck, no info and no dead CTA. Both closed is the
  same generic unavailable an unknown token gets. It rides on the party's own
  metadata PATCH — one endpoint, one version — and is configured in the After tab
  because that is where it means something.
- **Guest content is six typed slots, NOT a page builder.** `PartyGuestContent`
  keyed `(PartyId, Kind)` — invitation, location, dress-code, menu, info,
  thank-you — with no slug, no sort order, no blocks, no HTML and no Markdown;
  the order a guest reads them in is a product decision, not data somebody drags.
  Every payload is validated server-side against its kind AND re-serialized from
  the parsed object, so an unknown field is dropped rather than stored: knowing
  the route is not permission to persist arbitrary documents. Location holds an
  ADDRESS rather than a map URL, because an arbitrary external link kept as
  authority is somebody else's page one QR away. Visibility defaults are the
  SERVER's, each slot carries its OWN version (editing the menu never contends
  with renaming the party), and a slot that is disabled or scoped elsewhere is
  ABSENT from the guest context rather than sent with a flag to respect.
  A slot answers THREE independent questions: what it says (`ContentJson`),
  which image it uses (`MediaFileItemId`) and HOW that image participates
  (`MediaPresentation`: `inline` or `poster`). `inline` is the default and is
  what every row written before the column meant. `poster` makes the photograph
  the guest-facing DOCUMENT: the surface shows a deterministic, product-labelled
  row and opens the picture whole in the shared viewer, which is what lets a
  1080x1920 graphic be read rather than cropped into a hero. It is a RENDERING
  and not an authority — same reference, same eligibility rule, same
  relation-scoped route, no download in either mode. Switching never destroys
  the typed text, a poster is never the invitation's or the thank-you's hero
  (both fall back to the cover/product greeting and offer the picture
  separately), and a poster that stops being servable is withdrawn rather than
  silently rewritten to `inline`, which would publish words the host replaced.
- **A Party feature may REFERENCE an owner's file, and a reference is not album
  membership.** `PartyGuestContent.MediaFileItemId` (one photograph per slot)
  and `PartyChallenge.MediaFileItemId` point at the owner's ordinary `FileItem`.
  There is no Party media library, no hidden album, and `AlbumItem` gained no
  flag: a row still means "this file is in this album", so a menu graphic
  uploaded for the party lands in the owner's library through the ordinary
  upload and in NO album, and the slideshow, gallery, TV, shares, exports and
  downloads never see it. What is INDEPENDENT is album membership, not
  media-library eligibility: a file the owner moved out of their library is out
  of Party too, and "extra-album" is not another word for `Excluded`.
  Eligibility is ONE rule, `PartyMediaReference`: owner-owned, not in Trash, not
  in the Private Vault, in the ACTIVE media library (through
  `MediaLibraryScopePolicy`, the one scope every media surface narrows by,
  never a second comparison of its own), and a SERVER-DETECTED
  image — `MediaCategory` image AND a non-null `DetectedContentType`, because
  ingestion takes the category from the client MIME when the sniffer recognises
  nothing, so a text file sent as `image/png` has the category but not the
  detection. The rule is asked when the owner writes a reference and again on
  every guest request, so a trashed or vaulted file stops being served with
  nobody rewriting anything; a reference a slot ALREADY holds is not re-judged
  on save, so a host can still fix a typo after the photo went to Trash. Three
  things are easy to undo by accident. **The token is not a grant over the
  owner's files**: a slot's photograph is served only by
  `/api/party/{token}/content/{kind}/media`, which re-resolves the slot on the
  guest's CURRENT surface; an activity's by `/challenges/{id}/media` behind a
  running game (and on the owner's TV by its album's game, never by
  `/api/tv/media/{file}`, which serves only TV albums); and
  `/api/party/{token}/media/{fileId}` keeps meaning album media and refuses the
  same file. **Authorization and bytes are separate**: each route authorizes
  through its relation and then calls the one `ServeAuthorizedDerivativeAsync`
  — derived, metadata-stripped, never an original, never an attachment — and the
  guest DTO (`PartyGuestContentViewDto`) carries an address, never the file id.
  And **both foreign keys are `ON DELETE SET NULL`**: a permanent purge nulls the
  reference instead of being blocked by it, and a teardown deletes the
  references and never the owner's files. On the invitation, the hero is the
  invitation's own photograph, then the album's chosen cover, then a
  composition.
- **Tearing a party down keeps its album, and finalizes the guests' media
  first.** Owner-added media always survives — it was never a guest contribution.
  A guest upload survives if and only if its final `PartyUploadItem.Status` is
  `approved`, automatic or manual; everything else goes to Trash through the
  ORDINARY `IFileItemService` lifecycle, restorable, with no blob touched. The
  provenance rows then go with the rest, and that is the point: afterwards the
  album is SELF-CONTAINED, and what is visible in it is decided the way it is for
  every other album — by the files being active. `PartyStateEraser` holds what a
  party owns as ONE list, shared with the album delete that erases a party from
  the other direction.
- **Party is a DESTINATION, and a party is created before its photographs.**
  `/parties` and `/parties/{id}` are the owner product; the navigation entry is
  ABSENT without `party.access`, never disabled. `POST /api/parties` takes a name
  and optionally a date and makes a Draft with NOTHING else — no album, no
  capability, no token, no television, no game session, no print profile — so
  "is this party public" is never a question about when it was made. A party with
  no album is an ordinary state the surface invites the host to finish: nothing
  album-scoped is requested until there is a real album id, so no endpoint is
  ever called with an invented one. Three things are easy to undo by accident.
  **The metadata PATCH writes DATA only** — title, description, `EventStartsAt`,
  `GuestAccessExpiresAt` — under the same optimistic concurrency an album uses,
  and it cannot write `Status`/`LiveStartedAt`/`LiveEndedAt` by construction
  rather than by a filter somebody could relax. **`Party.Title` and `Album.Name`
  are independent** in both directions with no sync; they were only ever the same
  string because one was made from the other. And **the workspace MOUNTS what
  already worked** — contributions, moderation, slideshow, game, deck, control
  room, print — rather than cloning it, which is why `AlbumSettingsPanel` is now
  a bridge of one sentence and one door instead of the whole Party application.
- **The main media source is choosable until the first capability, then fixed.**
  Free to pick and replace while the party has never had a `PartyAlbumLink`;
  `media_source_locked` (409) from the moment one has EXISTED — active, revoked
  or superseded — because participants, uploads, greetings, prints, games and
  face searches are scoped to a link naming that album, and moving it would turn
  a UI edit into a domain migration. Revoking the party does NOT unlock it.
  `PartyDto.CanChangeMainMediaSource` carries the answer so the surface says so
  before the host chooses rather than after. OWNERSHIP decides eligibility, not
  authority: a shared album's Editor may curate it and can never make it a
  party's source, and a foreign or missing album is the same generic 404. One
  album is one party's `main` source and the DATABASE enforces it
  (`ux_party_media_sources_album_role`, `UNIQUE (AlbumId, Role)`), keyed on Role
  so future roles inherit the rule without a migration or a database enum — P1
  left this as an application check while `EnsureForAlbumAsync` needed one
  answer, and a check that runs first is not a rule.
- **A guest holds a capability; the HOST holds a permission.** `party.access` is
  the product and `party.contributions` / `party.games` / `party.print` /
  `party.face-search` are feature keys whose Parent is `party.access`, so a role
  carrying only one of them opens nothing. Every public Party request resolves the
  OWNER's effective permissions at the seam through the same
  `IUserPermissionService` the authenticated endpoints use
  (`IPartyCapabilityPolicy`) — which is what makes revoking a key reach guests who
  are already at the party, on their next request, with no token rotation and
  nobody signing in again. Both directions are enforced and both are tested: a
  valid token cannot outrank a missing permission, and a permission cannot rescue
  a revoked capability. A capability the host may not run is ABSENT — from the
  guest hub, from Album settings, from the owner's routes — never a disabled tile,
  and its endpoints answer the same generic 404 as an unknown token. The rule for
  where to check is "anything that hands a guest access, or that a guest
  presents", which is why the TV's party QR (`GetActivePartyUrlsAsync`) and the
  print-token resolver both ask. Owner-side MODERATION is `party.access`, not
  `party.contributions`: closing the contribution channel must not lock the host
  out of the queue it filled.
- **Party publishes one Guest Hub QR; the old capabilities remain capabilities.**
  The view token's `/party/{token}` route is the canonical mobile Hub and the
  only link rendered as a QR. Browse/download, face search, contribution, the
  Game and printing are entries in that Hub — and the GAME is ONE entry. The hub
  used to render "Game" and "Vote the challenges" side by side from one signal,
  as if a guest were meant to choose between two words for two different votes;
  `/party/{token}/challenges` now redirects to `/party/{token}/game`. A guest
  already on the hub never needs a second QR either: the party carries a
  persistent, non-invasive bar naming what the game is doing (*choose the
  activities* / *game in progress* / *vote now* / *game paused*, and NOTHING once
  the match is over, because a dead CTA is the one thing every Party surface
  refuses) and offering one tap. It is a link and never a takeover — the guest
  decides — and it reads the PUBLIC snapshot without joining, because a hub that
  minted a participant would count everybody who ever opened the party in the one
  number the control room reads out loud. The television's QR stays what it is:
  the way in for somebody not yet at the party. The distinct upload token and
  `/party/{uploadToken}/upload` route are deliberately still valid for already
  printed codes; hiding the second QR is a presentation change, not a token
  migration or redirect that could weaken its upload-only authority.
- **The interval-driven challenge HOLD is retired, and a guest's vote chose the
  last thing it will ever choose.** `PartyChallengeSession` used to freeze the
  slideshow on the `MostVotedRemaining` activity at a media boundary and wait for
  the remote's NEXT — the room decided and nobody conducted. The Party Game
  replaced all of it, so `PartyChallengePolicy.Select` and the whole hold are
  gone: nothing selects an activity from votes, and nothing interrupts a
  slideshow. The three TV routes (`party-playback`, `/boundary`, `/next`) still
  answer, inertly, and write NO row — an installed TV APK calls them on every
  photograph, and a 404 per boundary is a worse answer than "the slideshow
  continues", which is why retiring a behaviour is not the same as breaking a
  client. The table survives in the schema and is simply never reached; the
  "currently held, immutable until NEXT" guard on deleting an activity went with
  it, because a stale row must not block a host for ever. What SURVIVES of that
  feature is its storage: `PartyChallengeVote` and the participant's conditional
  vote-budget claim are now the PRE-GAME PREFERENCE (next entry), which is why
  there is no third voting system.
- **A party MESSAGE is scoped to the Party link, and its authority is a
  capability rather than a role.** `PartyMessage` is a text-only domain beside
  the media pipeline — no `FileItem`, no blob, no derivative — so `TvAlbumItem`
  stays `image | video` and an older TV APK keeps working by never calling the
  new feed. The row's scope is `PartyAlbumLinkId`, not `AlbumId`: re-enabling
  party mints a new link, which is what makes last year's greetings stay away
  from this year's wall without anybody rewriting rows, and what makes a
  revoked party empty the TV on the next poll. Moderation authority is exactly
  `owner || activeMembership.CanManagePartyMessages`, resolved once in
  `IPartyMessageAccessResolver` and re-read per request. The album ROLE is
  deliberately not in that predicate: an `editor` curates an album, and running
  the party is not curation, so widening the role would grant every existing
  editor a capability nobody chose to give them. Two easy mistakes: promoting a
  Hero does NOT need clearing when the message is hidden (every projection
  filters on `Visible` first, so a hidden Hero cannot survive a projection that
  forgot), and the message length limit is counted in **Unicode code points** —
  the single unit `EnumerateRunes()` and `[...text].length` agree on. UTF-16
  units would charge two per emoji; grapheme clusters would be friendlier but
  are defined by an ICU table .NET and the browser upgrade separately, and the
  day they disagree the guest's counter and the server's validator disagree too.
  `PartyMessageText.cs` and `frontend/packages/api-client/src/partyMessageText.ts`
  are mirrors, and their two test files share fixtures on purpose. The third
  easy mistake is the capability's lifetime: an `AlbumMembership` row is REUSED
  when the same person is invited again, so `CanManagePartyMessages` is cleared
  on revoke AND on re-invite — otherwise a delegation the owner took away comes
  back with the next invitation, which nobody would see in a diff.
- **What a manager may do to a message is a TABLE, not a target state.**
  `PartyMessageTransitions.Target(current, action)` is the whole answer: five
  permitted moves, everything else null. Routes therefore carry an ACTION —
  `approve` and `restore` both end at `visible` but start from different places,
  and only the action can tell them apart, which is what lets the domain refuse
  `visible → rejected` and `pending → hidden`. A `status` parameter cannot
  express that and silently permitted both. v1 is deliberately STRICT rather
  than idempotent: approving something already live is `invalid_transition`,
  because "you are late, somebody else approved it" and "done, nothing happened"
  are different things to tell a manager. Authorization is resolved BEFORE the
  transition, so a stranger gets the generic 404 and never a 400 that would
  confirm the message exists. Hero is not in the table — promote requires
  `visible`, demote always works.
- **A party takes THREE contributions, and each is its own switch.**
  Photographs (`UploadEnabled`, the switch that already existed — no second one
  was invented for them), greetings for the slideshow
  (`SlideshowMessagesEnabled`) and the guest book (`GuestbookEnabled`) are
  independent product decisions on one `PartyAlbumLink`, saved one at a time:
  `PartyContributionSettingsRequest` is nullable in every field, so a client
  that knows about one switch cannot clear the ones it has never heard of, and
  two people configuring one party do not silently undo each other. The
  migration defaults matter more than the model defaults: `SlideshowMessages
  Enabled` is `DEFAULT TRUE` because every party that existed before the column
  had a composer, and `GuestbookEnabled` is `DEFAULT FALSE` because nobody's
  party should acquire a guest book by being upgraded. When greetings are off,
  the refusal is a 409 `party_messages_disabled` — the request is well formed
  and the party's configuration is what declines it — and what guests already
  wrote is KEPT: the TV projection returns nothing, the manager queue stays
  reachable and reports the flag, and re-enabling brings the greetings back with
  no row rewritten.
- **The upload TOKEN opens the contribution page; the upload SWITCH opens only
  photographs.** `ResolveUploadAsync` once required `UploadEnabled`, which made
  the photograph switch the master switch for all three contributions: a host
  who wanted a guest book and no photographs got a guest page that resolved to
  nothing. The token now resolves whenever the party link is live, and the
  photograph rule moved down to the one action it governs, which refuses with a
  409 `party_uploads_disabled`. Reaching the page and being allowed to do a
  particular thing on it are separate questions, and only the second one is
  about photographs. The backend says which of the three are open on the upload
  session, and the page renders the halves it is offered — so the three switches
  are three independent decisions rather than three names for one. Guestbook
  writing accepts EITHER token (`ResolvePublicAsync` then `ResolveUploadAsync`),
  because a guest arriving by QR and a guest arriving by contribution link are
  writing in the same book.
- **Every contribution a guest can exhaust reports what is left.** Dedications
  gained `MaxGuestbookEntriesPerParticipant` and `SubmittedGuestbookCount`
  alongside the photograph and greeting quotas, claimed by the same conditional
  single-statement UPDATE the others use (`... WHERE "Id" = @id AND (@max = 0 OR
  "SubmittedGuestbookCount" < @max)`), so two devices sharing one participant
  cannot both take the last slot. `0` is unlimited, which is what every party
  that predates the column already meant, so the migration grants nobody a limit
  they never had.
- **An album is shared by LINK, and that is not the sharing it already had.**
  `AlbumMember` invites an ACCOUNT by address, who signs in and holds a role;
  `AlbumShareLink` is a token anybody holding may exercise. Neither is the
  other's fallback and no caller resolves through both. It is also not a party
  link: the album token is `HMAC(secret, "nubarca-album-share-v1" ‖ linkId)` and
  the party's is `HMAC(secret, linkId)`, so one cannot open the other's surface
  — not because a check forbids it, but because the digests live in different
  spaces and cannot collide. A check can be forgotten on a new endpoint; this
  cannot. There is no delete route at all: a visitor may add and take, and the
  one power an owner cannot lend by accident is the power to destroy.
- **The share's ceiling belongs to the LINK, not to a person**, because a share
  has no people in it — anybody holding the address is the same anonymous
  caller, so a per-person quota has no honest subject. Reaching it refuses the
  upload and leaves reading open: a link that switched itself off is one the
  owner would hear about from complaints. The same count governs BOTH ways onto
  the guest list, because reactivating a removed address and adding a new one
  are the same act as far as a limit is concerned.
- **Every album-share mutation is ordered behind the ALBUM's row lock.** The
  album is the anchor rather than the link because it is the one row that
  predates the first link and outlives the last — locking the link could not
  order a create against another create. The resend does the same on the
  guest's row. These are the invariants `AlbumShareConcurrencyPostgresTests`
  exists for, and they cannot be tested on the SQLite host: it hands every
  scope one pooled connection, so concurrency is serialised by the transport
  before it reaches the code.
- **Watching a video and taking a copy are different powers**, so they are
  different URLs. `playbackUrl` serves HLS — a transcoded ladder, never the
  camera's file — and is offered whatever the download switch says;
  `downloadUrl` is null when there is nothing safe to hand over. Originals are
  off by default because an original carries the GPS the camera wrote and a
  link is a public share.
- **A one-time code has exactly one observable end.** Delivered, or counted
  under `DroppedCapacity`, `UndeliveredSend` or `DroppedShutdown` — kept apart
  because they ask an operator for different things. `BoundedChannelFullMode.
  DropWrite` makes `TryWrite` return TRUE while discarding the item, so the
  runtime's drop callback is the only honest detector; and shutdown completes
  the WRITER before draining, so codes already accepted are delivered rather
  than cancelled with the host. No log line ever carries an address or a code.
- **The share's residual timing channel is accepted, deliberately.** Status and
  body are identical for listed, unlisted and malformed addresses — including
  under the resend cooldown and after exhausted attempts — and no answer waits
  on SMTP. A database-scale difference remains, because only a listed address
  causes a write. Equalising it would mean creating state for strangers, and a
  fixed delay is a constant an attacker subtracts. The rate limit of ten
  attempts an hour is PER SOURCE IP — the policy partitions on the remote
  address, not on the email being probed — so it bounds one source and not a
  distributed attacker, whose sample budget grows with the sources they
  control. The residue is accepted on that basis, not on a per-address limit
  that does not exist. Recorded at
  `IAlbumShareAuth.ChallengeAsync` so it stays a decision rather than an
  accident.
- **The guest book is its own table, and it never reaches the wall.**
  `PartyGuestbookEntry` is not a flag on `PartyMessage`, and the difference is
  the point: a greeting is written to be read out during the evening, a
  dedication is written to be kept. There is no route that promotes one, no
  shape shared with the TV feed, and no `PartyGuestbookEntry` anywhere in a TV
  projection — the invariant is expressed as an ABSENCE, which is also how it is
  tested. Its scope is the PARTY and not the album (unlike a message, whose
  scope is the link), because a book survives a QR rotation, an album change and
  a party that has no album yet — so its routes are `/api/parties/{partyId}/
  guestbook` and there is no album id on that page. Moderation reuses
  `PartyMessageTransitions` and `IPartyMessageAccessResolver`: contributions are
  one job, and a party where somebody may take a greeting down but not a
  dedication would be a distinction nobody asked for. Reading the book rides the
  VIEW token (a host may keep a book and accept no photographs at all), while
  writing additionally needs `Capabilities.Contributions`, which is what lets an
  ended party's book stay readable and closed.
- **Telling somebody WHERE the party is does not go through the guest list.**
  Until `POST /api/parties/{partyId}/address-share`, the only way to say where a
  party was, was to create an invitation group and share a personal invitation —
  so a host running an open evening had nothing to send. The payload is the
  party's name, date and venue and deliberately nothing else: no guest link, no
  invitation, upload, print or RSVP token, no email address, and no URL at all
  (the client builds a map search from the address). `HasAddress` is
  `[JsonIgnore]` for the same reason — in a 200 its only possible value is true.
  On the Party Crew side the capability is `details.manage`: the address is one
  of the party's own FACTS, so a co-organizer hands it out and a Regista does
  not.
- **A Party Crew e-mail collision is a CONSTRAINT, translated.**
  `PartyCrewService` catches exactly the PostgreSQL unique violation on
  `ux_party_collaborators_party_email_live` — never a generic `DbException` —
  and converts it to `PartyCrewError.EmailInUse`, a 409 `email_in_use`. The
  whole transaction rolls back, which matters because changing a collaborator's
  address also revokes their devices, invites and challenges: half of that
  surviving a lost race would leave a collaborator whose old address still
  opened the party. Proved against real PostgreSQL in
  `PartyCrewConcurrencyPostgresTests` — two renames onto one address, and a
  create racing a rename — by asserting the LOSER's version and e-mail are
  untouched.
- **`contributions.configure` and `contributions.moderate` are two capabilities
  on purpose.** A Regista decides what stays up tonight; whether guests may
  contribute at all is the host's standing decision about their own party and
  their own library. The surface shows a director the read-only facts instead of
  a switch the server would refuse, and the server refuses it regardless.
- **Deleting an album deletes its Party state, and that was already broken.**
  Every Party table has a restricting FK to the album and `AlbumService.
  DeleteAsync` cleaned up none of them, so deleting an album that had ever had
  Party enabled failed on the constraint — before guest messages existed. The
  delete now removes messages, upload rows, participants and links in FK order,
  on the same terms as the shares immediately above it: none of those tables is
  the audit trail, and the guest's stored PHOTO is untouched (an upload row is a
  visibility control over a surface that is going away). Face-search sessions
  are deliberately absent — they already cascade from the album.
- **The face search is a STATE MACHINE, and the face it shows is the face it
  searched.** The sheet moves through camera → capture → detecting → confirmed →
  scanning → results, with `no_face`, `multiple_faces`, `search_error`,
  `camera_error` and `cancelled` as named states rather than as an error string.
  Detection and search are two calls on purpose: `POST /face-search/detect` runs
  the SAME decoder, detector and selection rule as the search, so "we cannot see
  your face" and "there are several people here" are reached without embedding,
  matching or recording anything — and the crop the guest is shown is provably
  the one the search used. Which face wins when there are several is
  `PartyFaceSelection`, a pure rule with its own tests: the leader must beat the
  runner-up on area AND be near the middle, or the selfie is refused as
  ambiguous rather than guessed at. The selfie is never stored or uploaded to
  the album, and the sheet releases its MediaStream, blobs and object URLs on
  every exit. **The two calls are bound by a SELECTION TICKET**, because
  running the same rule twice is not the same as making the decision once: a
  detector may legitimately reorder its output or report a nearer face on the
  second run, and the search would then embed somebody else under a picture of
  the guest. So the detection issues an opaque, unpersisted, HMAC-bound ticket
  (CSPRNG nonce, 3-minute expiry, the chosen box) whose message also covers the
  selfie's hash and the active `AiProfile` id — bound without being disclosed,
  so a ticket cannot travel to other bytes or survive a model change. The
  search re-runs the detector only to recover landmarks, then MATCHES the
  confirmed box by IoU ≥ 0.9 instead of choosing again; no match, no search
  (`face_selection_changed`), and no ticket at all means no search
  (`invalid_selection`). A refusal issues no ticket, so `no_face` and
  `multiple_faces` cannot reach a search. The deterministic AI backend's synthetic faces are deliberately
  one dominant and one small off-centre face: two identical boxes are a real
  ambiguity, and a fixture that stumbled into it would make every plumbing test
  exercise the refusal instead of the path it is about.
- **The web interface ships in it/en/es/de, and nothing new ships in one
  language.** Italian is the canonical catalogue — `it.ts` defines `MessageKey`,
  every other dictionary is typed against it, and `i18n.test` requires all four
  to carry every key with matching `{placeholder}` tokens. Two traps: the
  language list lives in `LANGUAGES` and is rendered by `LanguageOptions`, since
  the account and admin forms each used to write their own two `<option>`s and
  so offered two languages after the product shipped four; and
  `hardcodedStrings.test.ts` fails on any user-facing string written directly
  into a component, with a NAMED allowlist of the surfaces that predate the
  catalogue (the file browser, which is the Home page, and the administrator's
  import wizard). Nothing may be added to that list to make a new surface pass.
- **A Hero card HOLDS the media, and the postponed advance is a LEDGER.** Every
  automatic slideshow transition goes through one `handleMediaBoundary`; when a
  Hero is due it renders over the current item and returns WITHOUT advancing.
  That the advance is owed is tracked explicitly (`BoundaryDebt`,
  `settleBoundary` in `tv/src/lib/partyMessages.ts`) rather than inferred from
  `hero !== null`, and the difference is a stuck wall: a video that has already
  ended can raise no second boundary, so a card withdrawn early — hidden,
  demoted, party revoked — would otherwise leave the slideshow on its last frame
  for the rest of the evening. `settleBoundary` is the ONLY function that can
  spend a debt, which is what makes "consumed at most once" a property of the
  type rather than of every call site's memory: a card timing out in the same
  tick the poll withdraws it advances once. A merely PAUSED wall keeps the debt
  and settles it on resume; a change of viewing INTENT (face filter, manual
  navigation, leaving the slideshow) discards it, because the index then belongs
  to whoever just chose it. Nothing interrupts a video — a boundary on a video
  IS its natural end or the owner's configured cap — but a card raised at the
  CAP must also withhold the controlled play intent, because the cap is a
  boundary the slideshow observes and not something that stops the player: the
  clip would otherwise keep running, audio and all, behind an opaque card.
  Heroes are suspended entirely during a party face filter, because a guest
  asking to see the photographs they are in has asked a question an editorial
  card does not answer.
- **The party video cap is media time, not wall clock.** `PartyMaxVideoSlideSeconds`
  bounds how long one video may HOLD the slideshow, never the stored file, which
  still plays in full everywhere else. A `setTimeout` would keep counting while
  the video is paused or rebuffering, so it is driven by the player's own
  `timeUpdate` position instead (`tv/src/lib/partySlideshow.ts`). One latch per
  video decides whether it may advance, because a cap crossing and `playToEnd`
  on the same frame would otherwise advance twice and silently skip an item.
- **"Applied but inert" is a defect class, not an incident.** A control the
  television shows as active that the request cannot carry is invisible to
  every status-code assertion: the parameter is accepted, parsed and dropped,
  and the search silently answers a different question than the screen claims.
  It has now appeared three times — a filter emitted on one route only, a
  filter no endpoint declared, and ORDER controls under a relevance ranking
  that never sends `sort` or `direction`. The structural answers are
  `TvSemanticSupport` in the catalog (a row unsupported by the active route is
  not offered at all), `isRelevanceOrdered` (the panel states the order instead
  of pretending it is editable, and `queryFingerprint` stops keying on it so
  two identical searches stay one request), and tests that assert on RESULTS
  rather than on status codes — `TvPersonalSemanticFilterTests` runs a real
  deterministic-backend search over two items that differ only by album
  membership, so an ignored filter cannot survive it.
- **Source-reading tests strip comments, in exactly one place.** Several
  guarantees are structural and are asserted by checking a construct is absent,
  which is where `assert.doesNotMatch` lies: the comment explaining why
  something was retired keeps the assertion red, and a renamed construct keeps
  it green because the old name survives in prose. Both have happened here. The
  stripper had accreted into eight hand-rolled copies across six files — one
  per occurrence of the bug, and one of them had silently drifted to miss `/* */`
  blocks. It is now `tv/src/testing/sourceText.ts`, and reading a source strips
  by default so the unsafe form cannot be reached by accident.
- **Filter applicability is decided from the DRAFT, never from what is applied.**
  `tvFilterRows(identity, draft)` once split its two halves — applicability from
  the committed identity, activity from the draft — which was correct while
  applicability depended only on the tab and the source, neither of which can
  change while the panel is open. `semanticSupport` invalidated that without
  changing the signature: applicability now also depends on the visual query,
  and the user types that INTO the draft. The panel therefore kept offering
  codec, resolution, audio and duration for the whole time between typing a
  query and pressing Apply, while `activeFilterCount` — which does read the
  draft — had already stopped counting them: one panel, two disagreeing answers
  about the same row. The catalog now builds `{ ...identity, filters: draft }`
  internally, so passing the committed identity cannot produce a wrong row and
  the call site is no longer load-bearing. Regression tests deliberately pass
  the COMMITTED identity in both directions (query typed, query cleared) — an
  earlier attempt handed them a pre-drafted identity and consequently proved
  nothing, which is the same flaw that let the original defect through.
  The fixed "Order: Relevance" statement is a non-focusable `FilterInfoRow`,
  not a disabled/no-op button. It explains that semantic ranking determines the
  order and cannot be edited; remembered Sort/Direction focus migrates to Apply
  when those real controls disappear.
- **What the media selection dock OFFERS is one pure model, and it is half
  capability, half permission.** `mediaSelectionCapabilities.ts` answers whether
  an action makes sense for THIS selection (all photos? Excluded scope? inside
  an album?); `mediaSelectionActions.ts` combines that with the caller's
  effective permissions and is the only place a dock entry is created. The two
  halves are separate on purpose: the capability question has nothing to do with
  who is asking, and merging them would put permission logic back into the
  surface that renders it. Three gates are easy to lose. **"Move to Personal" is
  the private vault operation**, not a second name for the library, so it needs
  `private-vault.access` exactly as the Private destination in the navigation
  does. **Plates and Beauty carry the Laboratory's own composite** —
  `laboratory.access` plus the section permission — so a user with Plates but not
  Aesthetics is offered exactly one of them here, just as they get one tab there;
  they were previously built with no permission check at all, which offered two
  doors that answer 403. And **a photo-only destination is withdrawn entirely
  from a mixed selection** rather than run over the photos in it: partially
  applying a bulk action is worse than not offering it. Restore and
  remove-from-album deliberately sit OUTSIDE the Move menu — restore is the
  inverse of Excluded rather than a fourth destination, and removing an album
  membership never touches the file, so listing it beside Trash would misdescribe
  it.
- **"Next photo" in face review is navigation, and resolves nothing.** The queue
  advances by itself when a photo is FINISHED, and that advance removes the photo
  from the list (`advancePhoto`). Next photo is the other thing entirely: parking
  an unresolved photo and coming back to it. It opens the next LOADED photo and
  leaves the current one's undecided faces, its count, its place in the queue and
  the server untouched, and it never wraps — at the last loaded photo the control
  is disabled rather than quietly returning to the top. Implementing it by
  reusing `advancePhoto` is the obvious shortcut and is wrong in exactly the way
  that matters: it would silently discard work the reviewer had not finished.
  `Skip face` is a third, narrower level again — same photo, another undecided
  face, no mutation either.
- **Production images can be BUILT on GitHub before anything depends on them.**
  `Build production images` (`workflow_dispatch` only) produces the same two API
  targets the server builds today and publishes them to GHCR under the immutable
  full-SHA tag — no `latest`, because a deploy must be able to name the commit
  that produced its bytes. When this was established the server still built from
  source and the workflow only proved GitHub could produce the images; the two
  entries below moved production onto them, first the backend and then the
  frontend. Both images are verified BEFORE they are pushed,
  by `scripts/verify-production-image.sh`, so an unverifiable image is never
  something anyone could deploy. The check that made this slice necessary at all:
  only `runtime-openvino` stamped `NUBARCA_GIT_SHA`, so the lean `runtime` target
  could not say what built it; it now carries the same stamp. GPU execution is
  not tested and must not be — `/dev/dri`, the render group and the model mounts
  belong to an installation, so the workflow proves the GPU variant CONTAINS the
  OpenVINO native layer and Intel OpenCL userspace, and leaves the device itself
  to the installation's own smoke checks.
- **The production server no longer compiles the backend.** `api` and `worker`
  run the OpenVINO image CI built, pinned BY DIGEST in the server-local release
  override — the same digest for both, because both run the same target. The
  `:<full-git-sha>` tag stays the readable name a human quotes; the digest is
  what fixes the bytes, because a tag can be moved and the thing production runs
  should be the one that cannot change under it. The production Compose model
  carries no `build:` recipe for either service, so a backend build from that
  stack is not merely discouraged, it is unavailable: `docker compose build api`
  answers "neither an image nor a build context". A measured consequence, kept
  deliberately: the base stack (`prod.yml` + `prod.local.yml`) no longer resolves
  api/worker on its own — which is honest, since it never carried the GPU wiring
  and was never a valid way to run them. Everything hardware stays where it was,
  in the OpenVINO override: `/dev/dri`, `OPENVINO_RENDER_GID`, and the device
  placements. The image cannot carry a device mount, so §6 now proves the GPU
  wiring reached the containers rather than assuming it. Rollback became a pin
  change with no recompilation, in both directions. The frontend is still built
  on the server; that is the next slice.
- **The production server compiles no application code at all.** The frontend
  was the last local build; it is now built, verified and published by CI beside
  the backend, as an INDEPENDENT parallel job — the two share only the source
  SHA, so a frontend failure never withholds a good backend image. With its
  `build:` removed, `docker-compose.prod.yml` carries no application build
  recipe whatsoever, and `up --build` has nothing left to compile. The frontend
  records provenance as `org.opencontainers.image.revision` rather than an
  application variable: nginx has no use for one, and a label is where a build
  says what it came from. Its verifier RUNS the container instead of listing
  files, because a `dist/` that copied cleanly and an nginx that answers
  correctly are different claims — it proves the SPA fallback returns 200 for a
  client-side route while a MISSING `/assets` file still returns 404, which is
  the half that matters: a stale client handed HTML where it expected JavaScript
  fails later, somewhere else, as a parse error. `/tv.apk` and `/download/tv/*`
  are deliberately NOT tested in CI — they come from an installation volume,
  never from the image, the same separation as `/dev/dri` for the backend — and
  are checked after the deploy instead, where replacing the container is exactly
  when that boundary would break.
- **Never `probe … | grep -q` in the image verifiers.** `grep -q` exits at the
  first match and closes the pipe, `docker run` dies of SIGPIPE, and under
  `set -o pipefail` the pipeline reports failure even though the match
  succeeded. Whether it bites depends on whether docker finished writing first,
  so it appears as an intermittent false FAILURE on whichever check is slowest
  to produce output — it was found because `nginx -t` failed verification while
  the same container served every request correctly. Both verifiers now capture
  into a variable and match with `[[ ]]`, so there is no pipe to lose the race
  in. Related to the repository's older rule about piping validation commands
  into `head`/`tail` without `pipefail`; this is the same hazard with the
  opposite sign.
- **TV 1.0.9 is accepted on physical hardware, with one defect left open: the
  launcher icon still does not appear.** The operator accepted the release and
  explicitly did not block on the icon, so it is recorded rather than fixed. It
  matters that this is the FOURTH release to touch that area, because the next
  attempt should not re-try what has already been disproved on real hardware:
  1.0.6 replaced both Android launcher icon slots (legacy tile with transparent
  corners, adaptive foreground inside the 66/108dp safe square) and the tile
  stayed square; 1.0.7 restored `android.intent.category.LAUNCHER` beside
  `LEANBACK_LAUNCHER`, which fixed VISIBILITY in the Applications library but not
  the artwork; 1.0.8 corrected the banner density (320×180 px at xhdpi = 160×90
  dp, `drawable-xhdpi` only). So icon slots, launcher category and banner density
  are each individually ruled out as the whole explanation.
  Whoever picks this up should FIRST establish which artifact is actually missing
  — the Leanback banner on the home row, or the launcher tile in the Applications
  library — because the three fixes above touch different resources and the
  reports so far do not distinguish them. `adb shell dumpsys package
  it.littlefly.nubarca.tv` would settle it, but this operator has no ADB access
  and cannot get it, so the evidence has to come from what the screen shows.
- **The TV People chooser uses a fixed two-pane landscape layout.** Physical
  Fire Stick evidence disproved two successive structures. First, a native
  list accepted focus and selection without painting usable rows. Replacing it
  with four ordinary rows proved the data path, but a 960x540-ish logical TV
  viewport then exposed the remaining geometry error: summary, Search, Match,
  Clear, four people, page status, Previous/Next, and Done all competed for one
  vertical column. The footer visibly overlaid the first person row. The current
  chooser has no list or scroll viewport and no shared vertical budget: a fixed
  left rail owns selection summary, stacked Search/Match/Clear controls and
  Done; the right pane owns a stable 2x4 people grid, result/page heading and a
  separate Previous/Next footer. Eight people per page reduce a 200-person
  library to 25 pages, while local name search remains the fast route and jumps
  directly to the page containing its focus target. Empty grid slots preserve
  the four-row geometry on the final page. There are no absolute layers, fixed
  row heights, negative offsets, virtualized lists, clipping, or programmatic
  D-pad navigation. The stable `LibraryFilterPanel`/`PanelShell` modal host and
  include/exclude/query contract remain unchanged. Source regressions cover the
  exact overlap mechanism, but physical Fire Stick acceptance is still required
  before the visual defect can be called closed.
- **The Help assistant's model has a TRUST classification, and it is never
  inferred from the URL.** Protocol and trust are separate axes: an endpoint
  speaks the OpenAI-compatible format whether it is a hosted provider or the
  operator's own model server, and the format says nothing about who holds the
  bytes. `Assistant__Models__<name>__Trust` is `External` or `LocalTrusted`,
  stated by the operator per named profile; `ManagedLocal` exists in the enum,
  is refused by validation, and must not be presented as implemented isolation.
  Validation fails closed — unknown, empty, misspelled and NUMERIC values are
  all invalid, and none of them becomes Local — and nothing a browser sends can
  choose or override it, because the chat request has no model, trust or domain
  field. `localhost` and RFC1918 addresses stay External when declared External
  (a reverse proxy in front of a cloud API looks exactly like that), and a public
  hostname stays LocalTrusted when declared LocalTrusted (a trusted GPU server
  on another host is not on this LAN). The legacy `ExternalHelp__*` section is a
  deprecation path adapted into ONE always-External profile, and only when no
  `Assistant__*` value is set. There is deliberately no "allow insecure URL"
  switch: a plaintext endpoint is `Trust=LocalTrusted`.
- **Trust decides what a model is ELIGIBLE for; the feature decides what it
  USES.** Effective capability is `model trust ∩ feature policy ∩ caller
  permissions`. A LocalTrusted model is eligible for private context, private
  RAG and read tools — and Help gives it none of them, because Help's operation
  policy is public product knowledge. Configuring a local model makes Help
  local; it does not make Help able to see anything new, and a test asserts that
  on the outbound bytes. No trust level grants write tools or unconfirmed
  execution: nothing changes because a model suggested it.
- **RAG is a PLATFORM; Product Help is one domain on it.** `IRagRetriever` is
  domain-general, and a domain's policy — scope, privacy class, whether an owner
  is required, whether its evidence may reach an External model — is defined in
  CODE (`RagDomainRegistry`), never in an editable row. The database records
  which sources exist and which revision was indexed; it does not record whether
  evidence may leave the trust boundary, so no `UPDATE`, admin endpoint or
  restored backup can widen one. `product-help` is Public and External-approved;
  `nubarca-repository` is SystemInternal and is **never** available to an
  External model — deliberately so even though NubArca is public on GitHub
  today, because public hosting is a fact about this month rather than a
  property of the domain. `AssistantRagPolicy` intersects model trust with
  domain policy over the EVIDENCE, before a prompt exists.
- **A source exists once and may belong to several domains.** `rag_sources` /
  `rag_domain_sources` / `rag_chunks` / `rag_chunk_embeddings`: adding a domain
  costs a membership row, not a second copy of the text and every vector.
  Domain-specific classification (Product Help's feature, aliases, audience,
  intent, priority) lives on the MEMBERSHIP, because it is that domain's opinion
  — a C# file does not acquire an `intent=how-to` because the schema can hold
  one. These tables are separate from the owner-private `document_*` tables and
  from the photo/face vector tables on purpose.
- **Retrieval is hybrid and lexical stays first-class.** Semantic retrieval is
  OFF by default (`Rag__SemanticEnabled`), uses a LOCAL ONNX text-embedding
  profile (`Rag__TextEmbeddingProfileKey`, 384 dimensions), and searches a
  dimension-specific pgvector table filtered by domain AND profile in the query.
  Fusion is RRF over ranks rather than scores, because BM25F and cosine are not
  calibrated to the same scale. Canonical float32 bytes are the truth and
  pgvector is a rebuildable accelerator, so SQLite and a Postgres without the
  extension degrade to lexical. Every failure — disabled, no profile, missing
  model, no pgvector, unsupported dimension — falls back and reports a reason in
  the retrieval mode. There is NO hosted embedding path and nothing downloads
  weights.
- **A retrieval corpus must not contain the questions it is measured with.**
  `RagGoldenSet.cs` holds the golden queries as string literals, so once the
  repository indexed itself the best lexical match for a golden question became
  the file containing that exact sentence: it led three of four failures and took
  repository MRR from 0.583 to 0.395. `src/NubArca.Api/Rag/Evaluation/` is
  excluded from the repository corpus for that reason, as a rule rather than one
  file's exemption. Do not re-add it to make the corpus "complete".
- **Semantic retrieval helps PROSE and does not currently help the repository.**
  Measured against `multilingual-e5-small` on the full index: `product-help`
  MRR 0.938 → 0.969 (recall already 1.000, 16/16); `nubarca-repository`
  MRR 0.575 → 0.625 but Recall@5 0.800 → 0.700 and top-3 7/10 → 6/10. A
  general-purpose SENTENCE model discriminating among 23,745 chunks of mostly
  source code returns plausible-but-wrong neighbours that displace correct
  lexical hits. Recorded rather than tuned — adjusting fusion weights until the
  ten benchmark questions pass would move the score and not the product. Lexical
  remains the better default for the repository domain, and
  `Rag__SemanticEnabled` is per installation.
- **A partial index run concludes NOTHING about what left the snapshot.**
  `rag index --limit N` sets `Partial`, and reconciliation is skipped. "I did not
  see this source" means "it was deleted" only if the run could have seen it —
  a capped pass over a complete index otherwise removes every membership past
  the cap. Completeness comes from the REQUEST, never from a count of what was
  enumerated, because an empty repository would then look like a complete run
  that found nothing.
- **The REVISION lives on the domain membership, not on the source.** A source
  row is one content interpretation — `(SourceKey, ContentHash,
  IndexFormatVersion)` — and a membership says which snapshot ITS domain is
  using that content at. That is what lets two domains sharing a document
  upgrade one at a time, in either order: the bytes did not change, so nothing
  is re-derived and each membership moves its own revision forward. Putting the
  revision on the source deadlocked the release lifecycle, because advancing the
  repository rewrote what Help was serving and Help could not go first for the
  same reason. A row only THIS domain uses is still rewritten in place, so an
  ordinary `git pull` keeps the ordinal-by-ordinal chunk and embedding reuse; a
  row another domain is serving forks instead, and the superseded row is deleted
  when its LAST membership leaves. Do not "simplify" this back into one row per
  key.
- **Git object reads are bounded BEFORE allocation, and a stalled `cat-file`
  session is killed rather than reused.** `ls-tree -l` carries the blob size, so
  an oversized object is refused from the tree entry; `GitCatFileSession` also
  enforces a ceiling from the response header before `new byte[size]`. Any read
  that stops mid-response leaves bytes queued on a single-conversation stream, so
  the session is faulted and the process killed — resynchronising would mean
  consuming exactly what was being refused. Cancellation is NOT a timeout: it
  reaches the caller as `OperationCanceledException`, because reporting a
  cancelled index as `git-object-read-timeout` records a permanent-looking
  failure for something the operator did on purpose.
- **Semantic retrieval is configured PER DOMAIN**
  (`Rag__Domains__<key>__SemanticEnabled` / `__TextEmbeddingProfileKey`), because
  `multilingual-e5-small` moves Product Help's MRR up and the repository's
  Recall@5 down. The installation-wide values remain an explicit fallback, and
  the per-domain switch is NULLABLE so "unmentioned" and "false" stay different —
  otherwise adding a `Domains` entry for one domain silently disables another.
  An **OwnerPrivate domain never inherits**: it must state both its switch and
  its profile, derived from the domain's privacy class rather than from a list of
  keys.
- **One reading of a file is authority, and it is a stored fact.**
  `DocumentText.IsCurrent`, with a filtered unique index behind it and the shared
  `OwnerDocumentEligibility.EligibleChunks` boundary requiring it alongside
  completion. Rich ingestion gives a file several possible readings — a PDF read
  as native text before the PDF pipeline existed, a workbook re-read by a newer
  extractor — and resolving which one answers by "latest timestamp" or "first
  completed" lets a clock or an index decide, producing a plausible answer from a
  superseded interpretation with no symptom. Two rules that look alike and are
  opposites: when the BYTES change the old reading stops being authority BEFORE
  the replacement is attempted, so a parse that never finishes cannot leave a
  replaced document answering questions; when the bytes are UNCHANGED and a newer
  parser fails or refuses, the working reading keeps authority, because an
  upgrade that withdraws a working document is data loss with a version number.
  Historical rows are kept as provenance and are neither retrieved nor embedded.
- **Rich document format comes from the BYTES; the name and the MIME type only
  decide whether to look.** An OOXML package must declare its own main part, and
  a filename contradicting that declaration is refused rather than routed —
  handing a DOCX to the spreadsheet parser is untrusted input arriving somewhere
  written for a different structure. Archive bounds are read from the ZIP
  DIRECTORY and enforced before the Open XML SDK sees the package, so a
  compression bomb is refused before a byte is expanded. External package
  relationships are never dereferenced, Excel formulas are never evaluated,
  hidden sheets and hidden slides are not ingested, and deleted tracked-change
  text is not part of the document. `DocumentChunk.Page` is a real PDF page and
  nothing else; slides, sheets and Word sections live in the typed locator, since
  a field meaning "page-like thing" cannot be read without knowing the format.
- **OCR is a child process, off by default, and downloads nothing.** A managed
  wrapper binds native code into the API where a hang cannot be interrupted; a
  child can be killed, which is what makes the page timeout a bound. The page
  goes in on stdin so no private page is written to a temp file, stdout is read
  under a hard cap because it is untrusted process output, stderr is drained and
  never logged since it carries paths, and cancellation reaches the caller as
  itself rather than as a timeout. A language that is not installed makes OCR
  not-ready; an installation with no engine boots normally and reports affected
  PDFs as retryable, never permanently refused.
- **Owner-private knowledge is `user-documents`, and derived rows are not
  authority.** Private content lives in `document_texts` / `document_chunks` /
  `document_chunk_embeddings` — owner-scoped by schema — never in `rag_sources`.
  Every private read joins the LIVE `FileItem` through
  `OwnerDocumentEligibility`, so deleting a file or moving it into the Private
  Vault removes its answers on the next question; cleanup is housekeeping and
  never the boundary. Vault exclusion is the global `PrivateVaultId == null`
  query filter — nothing in that context calls `IgnoreQueryFilters()`. Blob
  identity is not knowledge authority: two owners of the same deduplicated bytes
  have independent extractions. The private lexical index is built per request
  and deliberately NOT cached, and private semantic retrieval is exact cosine
  over the owner's eligible vectors — a global ANN index with an owner predicate
  is not an owner-prefiltered search and fails silently.
- **A visual hit finds where to look; an eligible current text chunk is still
  what NubArca may say.** Visual document retrieval renders an owner's pages,
  embeds them locally under a DOCUMENT-specific SigLIP2 profile (shared weights
  with photos, separate identity and separate storage), and uses the result to
  scope the ordinary private text retrieval to the files it named. The two
  ranked lists fuse by RRF and go through the UNCHANGED evidence gate; a
  `DocumentVisualUnit` never becomes `RagEvidence`, no page image reaches a
  generative model, and a visually perfect page with weak text ends in no model
  call. Off by default, because enabling it re-renders and re-embeds every
  library.
- **A visual index is published whole or not at all, and stale rows are inert.**
  Pages render and embed one at a time and reach the database in one write with
  the `Completed` index — page 13 failing means pages 1–12 are not search
  results, and the database refuses a completed index with no units. Retrieval
  adds three conditions to the text ones: the index must be complete, its blob
  must be the file's CURRENT blob, and both the render and the embedding profile
  must be active. Rendered pages are never stored: render, embed, discard, plus
  a pixel hash.
- **Office documents are laid out in a container with no network and no
  credentials.** Open XML parsing is not rendering, and a layout engine over
  hostile input does not belong beside database credentials. The API sends bytes
  and a format ORDINAL over a Unix socket; the protocol cannot express a path, a
  filename, a command, an import filter or a URL. A rendered office page ordinal
  is that engine's pagination and is never a citation — Slice-4 typed text
  provenance stays the authority. Without the worker, PDFs and text still get
  visual search.
- **The visual pgvector accelerator has NO ANN index, deliberately.** An
  approximate index plus an owner predicate is not an owner-prefiltered search,
  so the absence is what leaves PostgreSQL one plan: restrict through the
  eligibility joins, then rank exactly. Without pgvector the same ranking runs
  in process, and a corpus past the exact-search ceiling reports the visual path
  UNAVAILABLE rather than ranking an arbitrary prefix of somebody's library.
- **Narrowing a text pass narrows CANDIDATES, never the index.** BM25 weights a
  term by how rare it is across the corpus, so an index built from three
  documents collapses every score under the minimum-score floor and the evidence
  gate rejects the chunk the visual pass went looking for. And a bare top-K over
  cosine is a sorted copy of the library, not a set of matches — visual hits
  need a positive cosine and a RELATIVE floor under the best match, relative
  because cross-modal cosine is not calibrated across checkpoints.
- **Late interaction is a seam, and its first candidate was measured and turned
  down.** `IVisualLateInteractionProvider` plus an exact MaxSim tested against a
  hand-computable fixture. `vidore/colSmol-500M` (MIT, adapter `0aaa9726…` over
  backbone `650243e9…`) was run through the real pipeline against the real
  SigLIP2 dense baseline on the shared golden set: **0.0% relative nDCG@5
  change, nothing recovered, nothing regressed**, for 97× the storage per page
  (448 KB vs 4.6 KB), 4× the indexing time, 2.9 GB peak RSS and 152 ms per
  question. `evaluated, not promoted`; the switch stays false and no model
  worker ships. Re-run it with `DocumentVisualPhaseZeroTests` plus
  `scripts/measure-colvision-candidate.py`. The lane reports its own
  discriminating power (13/13 distinct candidate sets) and asserts the reranker
  engaged, because a benchmark that cannot detect a difference is not evidence
  that there is none.
- **`Assistant__PrivateKnowledgeModel` must be `LocalTrusted`, with no
  fallback.** Not to Help's model, which is the one place allowed to be
  External, and not to anything else. An External configuration yields ZERO
  provider calls and its own reason code `private_model_not_local`; the question
  itself never leaves, not only the evidence. The private chat DTO carries a
  message and a bounded history and has no field for an owner, a domain, an
  object id, a model or a trust level.
- **Repository bytes come from the COMMIT, not the working tree.** The provider
  reads Git objects (`ls-tree` + `cat-file --batch`) at a resolved 40-character
  SHA. Tracked symlinks are refused by mode and their targets are never
  resolved or read; submodules are skipped. Git runs at index time only — the
  query path never starts a process.
- **A domain holding two revisions fails closed** (`rag_mixed_revision_index`)
  until a complete reindex converges. There is no modal revision: picking the
  newest, most common or first would let a half-reindexed corpus claim a
  coherence it does not have.
- **Chunk reuse is keyed on bytes AND `RagIndexFormat.Current`.** Changing a
  chunker without bumping it leaves every already-indexed source on the old
  interpretation forever.
- **A benchmark question must not appear in the corpus it is measured against**,
  and the guard is scoped per domain: repository queries against every eligible
  file, Product Help queries against the manifest only. Identifier queries are
  deliberately unguarded — `PhotoVectorIndexService` is SUPPOSED to occur in the
  file that should win. `RagContaminationTests` enforces this, and it has
  already caught a question line-wrapped back into documentation.
- **Indexing is idempotent and revision-aware.** `rag index` is explicit and
  CLI-driven; a source whose content hash is unchanged keeps its chunks, and a
  chunk whose text hash is unchanged keeps its embedding. Sources that leave a
  snapshot lose that domain's membership, and are deleted only when no domain
  still claims them. The repository provider indexes APPROVED TRACKED files —
  `git ls-files` is the first gate, not the last — and resolves the checkout's
  top level, because every path rule is written against repository-root-relative
  paths.
- **Help knowledge is an explicit MANIFEST, not "every `docs/**.md`".**
  `ProductHelpSources` names each approved document with an audience, an intent,
  a source kind, a priority and feature aliases. The previous automatic rule let
  an operations runbook compete on equal footing with the guidance somebody
  asking "how do I use faces?" needs — and runbooks are longer, so they often
  won. It remains an allowlist rather than a denylist of secrets, which now also
  means a NEW public document is out until someone classifies it. User-facing
  Help material lives in `docs/help/`. Retrieval is lexical, local and
  deterministic — section-aware chunks, one shared IT/EN stopword set (Italian
  `come` is also an English verb, so a language-switched list is the bug), a
  bounded feature-alias catalogue, field-weighted BM25F and intent shaping — and
  it is gated: `Score > 0` is not evidence, and below the gate Help makes NO
  model call at all rather than paying a boundary crossing for an answer with no
  documentation behind it. `help_knowledge_unavailable` (an administrator can
  fix it) and `help_no_supporting_knowledge` (nobody can) are deliberately
  different reasons.
- **A keyed upload's FileItem and its idempotency completion are ONE commit.**
  `POST /api/files` accepts an optional `Idempotency-Key`; the claim it takes is
  finished inside the authoritative `FileItemService.CreateAsync` transaction
  (its `uploadOperationClaimToken` parameter), never by a second call after the
  file is already durable. There is deliberately no standalone `CompleteAsync`:
  reintroducing one recreates the window where a crash leaves the file committed
  while its operation stays pending, so a later retry of the same key becomes a
  duplicate-name conflict instead of a replay. If the claim is no longer ours by
  then (expired lease, takeover), the whole ingestion rolls back rather than
  commit a keyed file with no operation association. Unkeyed uploads pass null
  and are untouched. Two 409s exist and are NOT interchangeable: an ordinary
  duplicate name answers a bare 409 (permanent), while an operation already in
  flight answers 409 with `{code: "upload_in_progress", retryable: true}` — the
  mobile classifier reads that structured marker only, never the message text,
  which is why a concurrent retry defers instead of failing the item forever.
  The mobile operation identity is 16 CSPRNG bytes (`expo-crypto`) as 32 hex
  chars, generated once per ledger row and reused across every retry, restart
  and ambiguous response; it is an operation identity, never content identity,
  and carries no account, asset, filename or inventory information.
- **The brand is a machine-checked contract, and mobile is its first strict scope.**
  `design/brand-contract.json` plus `design/tokens/*.json` hold the cross-platform
  palette, geometry, typography and motion; `docs/product/` holds the invariant
  catalog, splash/boot contract, component language and QA matrix.
  `scripts/check-brand-invariants.py` enforces them, strictly for `mobile/app`
  and `mobile/src/ui`: splash configuration, token values, product spelling and
  colour literals. Web and TV stay in report mode until Stages C and D. The
  mobile typefaces are static instances derived from the official Google Fonts
  variable binaries, bundled locally with provenance in
  `mobile/assets/fonts/fonts-manifest.json` — Space Grotesk publishes no
  SemiBold, so that one weight is a declared derivative rather than a released
  instance. `tokens.ts` keeps `radii` and `type` as DEPRECATED aliases so the
  screens BRAND-APP-01 does not redesign keep their proportions; no new call
  site may use them. BRAND-APP-02 added the mobile entry shell: `BrandLockup`
  (theme-aware, sized by the VISIBLE lockup rather than the file), `TextField` /
  `FieldLabel` / `InlineNotice`, a custom `BrandTabBar` that renders React
  Navigation's own state and keeps none, and the redesigned login. The checker
  carries a `MIGRATED_FILES` ratchet: a file joins it when a slice migrates it,
  and from then on a deprecated alias or a family-less heading weight there is
  an error. The list can only grow. BRAND-APP-03 added the media surfaces:
  `grid.gap` is the canonical gallery seam, the tile is a square media frame
  whose selection is a 2 px accent edge plus a filled control rather than a wash
  over the picture, chips and the filter sheet share one applied/inert language,
  the selection tray is accent-on-quiet rather than a row of blue calls to
  action, and the viewer chrome is safe-area correct and theme-independent.
  `media.highlight` was DELETED: it was a generic route to Soft Violet, and
  BRAND-AI-01 reserves that colour for inference — `signalIntelligence` is the
  role that says so.
- **Gallery surfaces are immersive, and the shell is one piece of code.**
  NUBARCA-UX-01: Photos, Videos, Albums and both album-detail screens run
  `ImmersiveGalleryShell` — full viewport, chrome that collapses on the way in
  and returns on a deliberate reversal, bottom navigation floating over the
  media with clearance supplied as scroll-content padding. The hide/show rule
  is pure (`galleryChrome.ts`) and anchor-based: comparing against the previous
  frame makes a shaky thumb strobe the bar. Scrolling never re-renders React.
  There is no Select control — long-press is the entry — and no settings cog:
  the gallery offers a person, and `gallery -> account -> preferences` is the
  hierarchy. Primary navigation is four browsing destinations; Sync moved under
  Account with its engine untouched. UX-01.1 then made a gallery's position an
  ITEM rather than an offset (`galleryAnchor.ts`), so it survives the remount
  that a column change requires; the viewer hands back the item that was on
  screen through a one-shot, identity-scoped anchor, and `setIndex` keeps
  `focusedKey === slides[index].key`. Selection mode REPLACES the bottom
  navigation by state rather than by z-index, and the tray is a floating capsule
  whose count and close sit outside its scrolling actions. `gridMetrics.ts` owns
  the seam arithmetic so the tile size and the space left for it cannot
  disagree, and `AccountButton` is the one Account affordance.
- **A gallery is a list of ROWS, and its position is a computed offset.**
  UX-01.2 replaced the anchor implementation outright. `FlatList numColumns`
  virtualizes rows, so its `scrollToIndex` takes a ROW index — passing a media
  index was out of range by construction and crashed — and `numColumns` cannot
  change in place, so keying the list on the column count destroyed it on every
  rotation and the new list reported its own first row as the position.
  `VirtualizedGalleryRows` renders explicit rows in a single-column list that
  SURVIVES a rotation, declares geometry through `getItemLayout`, and restores
  with one `scrollToOffset` computed from `galleryPosition.ts` — item identity
  plus progress through its row. Capture is suspended while a restore is in
  flight. `galleryVirtualization.test.ts` forbids `scrollToIndex`,
  `onScrollToIndexFailed`, `key={columns}` and `numColumns={columns}` in that
  layer, and refuses a second position engine in the shared album. The viewer's
  return position is scoped to the origin gallery and carries opened-versus-
  focused, so closing on the item you opened moves nothing.
- **Mobile colour is a palette reached through a hook, never a module constant.**
  `mobile/src/ui/palette.ts` holds the two themes; `tokens.ts` deliberately
  exports NO `colors`, so a stylesheet cannot capture one at import time and
  then keep it through a theme switch. Screens write
  `const useStyles = themed((colors) => StyleSheet.create({...}))` and call it
  as a hook; a sheet is built at most once per palette. Viewer, player and
  thumbnail-overlay colours are the exception and are static (`media` in the
  same file): the viewer is dark in both themes because it frames the content,
  not the app. `resolveTheme` in `themePreference.ts` is the same rule the web
  uses — dark is the product default, an explicit choice always beats the OS,
  and an unknown OS answer resolves to the default rather than to light.
  `palette.test.ts` reads `docs/brand.md` and fails if the palette stops
  agreeing with it, and asserts no source file outside `palette.ts` states a
  colour of its own. `expo-system-ui` is a dependency because without it
  Android ignores `userInterfaceStyle: 'automatic'` and the `system` option
  would silently be a second light option.
- **The mobile media route owns pager geometry and ordinary exit cleanup.**
  `/media/[id]` gives its horizontal list the available height and uses the
  list's own `onLayout` width — not the earlier window-dimension notification —
  for every cell, `getItemLayout`, direct re-anchor offset and visible-index
  calculation. A rotation invalidates any gesture begun under the old geometry
  and scrolls directly to `safeIndex * measuredWidth`, bypassing stale
  FlatList frames. The correction repeats from `onContentSizeChange` once the
  native content can reach that target, so a high index is never clamped against
  the old content width; only a completion carrying a drag width equal to the
  current measured width may change logical media. Viewer photos and fallback posters
  request `contain` without changing tile crop behavior. Only the active video
  runs the bounded probe, and each focus transition latches the current manual
  session cookie without replacing a player mid-play. The probe copies status
  and content type out of the native response head BEFORE aborting its body;
  aborting first may invalidate React Native's header accessor and must never
  turn a real HLS 200 into permanent unavailability. Preparation exhaustion,
  429/5xx and transport failure are retryable playback errors, never evidence
  that the media is unavailable; only a deliberate terminal media response uses
  that surface. Native readiness is seeded from the current player snapshot and
  `VideoView` exists only at `readyToPlay`.
  Chrome Back and hardware Back share one navigation-only path, while
  viewer-sequence cleanup runs on route unmount, so a mounted route never
  observes its own sequence being erased. Render-time index clamping is
  defence-in-depth for stale positions. This does not weaken account isolation:
  identity changes still remount the keyed `ViewerProvider` immediately.
- **The mobile Android test binary and Play binary are ONE release variant.**
  `Mobile Android release` is manual, protected-main-only and emits a signed APK
  for direct physical-phone testing plus an AAB for Play from the same source,
  version contract and dedicated upload key. The release contract pins package,
  monotonically increasing versionCode, API 36 target, Android 7 floor and the
  public signer fingerprint; the Expo config consumes it rather than duplicating
  identity. GitHub sees private key bytes only after repository tests pass,
  validates the AAB with pinned Google bundletool, validates the universal APK
  generated back FROM that AAB, gates 16 KB native-library alignment and emits
  provenance attestations. PR CI cold-launches a debug APK with Metro on an API
  35 emulator, and the release workflow cold-launches the exact signed APK with
  no Metro; both require the rendered `NubArca` login surface, because a live
  Android process can still be a React Native error screen. Autolinked Expo
  modules are checked against SDK 54's bundled native-module map, and
  `expo-font` stays an explicit SDK-owned dependency so npm cannot hoist an
  ABI-incompatible peer version. The APK is the pre-Play sideload path; after
  Play App Signing enrollment, internal testing is the accepted path because
  Play signs delivered APKs with its separate app-signing key. The key may never
  be shared with TV or replaced to fix a build. `docs/mobile-release.md` is the
  runbook.

- **The Party Game runtime is a hosted session, and a read of it never writes.**
  `PartyGameSession` / `PartyGameRound` are the owner-conducted game: one
  session per party link, one round per activity, one phase the server owns.
  They are deliberately NOT `PartyChallengeSession`, which is the older
  timer-driven slideshow interruption where the room's votes choose what happens
  next and nobody conducts — merging them would make one row mean two things.
  Three things are easy to undo by accident. A game that has not started has no
  row: the snapshot is synthesized in the lobby at `version = 0` and `start`
  quotes 0, because a television polling a party that has not begun must not
  begin it (the older session type materialises a row on read, on purpose, since
  its deadline has to start ticking somewhere). Every command quotes
  `expectedVersion`, and a refusal returns `409` carrying the CURRENT snapshot
  rather than a bare error — which is what stops a double tap becoming a double
  advance, since the second request is refused *and* re-renders the caller. And
  the transition matrix lives in the pure `PartyGameStateMachine`: the service
  applies transitions and never decides one, so the owner snapshot's
  `availableCommands` can tell a control room what is legal without a second
  copy of the machine in TypeScript. There is no realtime transport, on purpose
  — nothing in this repository has one; clients poll and consume EVERY
  successful snapshot. `Version` is the owner's command token, not a change
  feed: a guest's vote changes what a snapshot says without touching it, which
  is exactly what lets a vote and a `close_voting` contend on the session row
  without the vote defeating the close. The vote/close boundary is that row: a
  vote's transaction opens with a conditional no-op update of the session whose
  WHERE clause is the whole authority, so a late vote blocks, re-evaluates
  against the closed row, and writes nothing.
  Voting has the same shape: the integrity constraint is a unique index on
  `(round, participant)` rather than a code path, a vote names the ROUND it
  answers (stable for a whole round, unlike the version, so a lagging poll never
  costs somebody their vote), and who may know the result is a table — how many
  answered is safe always, the split reaches the owner when voting closes and
  the room only when it is revealed. A television reads the public snapshot but
  never mints a participant, because a display that joined would inflate the
  very count it shows; it SAYS it is a display (`?display=1`) so the control
  room can honestly report whether a screen is on, since the server cannot
  infer that from a missing cookie. And the four surfaces — deck, control room,
  television stage, guest phone — draw an activity with ONE renderer in three
  modes, emitting identical markup, so a preview predicts what the room sees.
  A finished game plays again through `restart_game`, and the tempting
  implementation is the wrong one: DELETING the session and letting the next
  `start` recreate it would send `Version` back to 0 and make every command
  written during the game that just ended quotable again. The row therefore
  survives — only its rounds and their votes are deleted — so 27 → 28 across a
  restart and 27 stays spent. The party link, its token, the guests, their
  photographs, greetings, prints and quotas, the deck and the display heartbeat
  are all untouched, because they belong to the party rather than to the match;
  and the restart takes the session row's write lock through the same
  conditional-update boundary a vote uses, BEFORE deleting anything, so a loser
  cannot destroy the winner's fresh lobby. No migration: it is a delete and an
  update over columns that were already there.
  See [docs/party-game/README.md](party-game/README.md),
  [docs/party-game/runtime.md](party-game/runtime.md) and
  [docs/party-game/ux-integration-contract.md](party-game/ux-integration-contract.md).

- **There are TWO party votes and they never meet.** A PREFERENCE is cast before
  the match, on an ACTIVITY, and says "I would like to see this"; a LIVE VOTE is
  cast during an activity, on a ROUND, and says "they did it". They share the
  anonymous `PartyParticipant` the party already had and nothing else — no
  table, no budget, no phase and no consequence. Preferences reuse
  `PartyChallengeVote` and the participant's `ChallengeVoteCount` claim precisely
  so this is not a third voting system, and `PartyGamePreferencePolicy` is the
  one pure rule the guest surface, the write path and the control room all ask:
  OFFERED while the Games capability holds (phase-folded, so only while the party
  is LIVE), the game is on and `PriorityVotingEnabled` is on; OPEN while the
  match has not begun — no session row, or one still in its `lobby`. Four things
  are easy to undo by accident. Preferences are ADVISORY by construction: there
  is no code path from one to the game moving, and the tests assert that
  negatively against persisted rows rather than against status codes — they pick
  no activity, interrupt no slideshow, enter no `yes`/`no` result and move no
  phase. The first `start` freezes them and `restart_game` REOPENS them without
  deleting one, because they belong to the party and the guests who cast them
  rather than to the match that was discarded. An INTERMISSION is deliberately
  not open — the match has begun, and a preference arriving then would change a
  count the host is reading while they plan. And the GUEST surface carries no
  vote counts at all: a guest choosing must not be told what everybody else
  picked first, so the numbers reach the host, in the control room, where they
  inform a decision. The retired guest routes still answer for printed QR codes
  and are ONE thin adapter over the preference path — two write paths onto one
  table is how the budget claim, the uniqueness race and the lobby gate would
  come to disagree.

- **The host plans the FUTURE, and the past and the present are not plannable.**
  The owner snapshot carries a `plan`: the whole deck in play order with each
  entry's state (`played` / `current` / `remaining`), its position among the
  activities the game would actually play, whether it is enabled, whether it is
  excluded from this match, and the preferences the room cast for it.
  `POST …/party-game/plan` takes `move`, `exclude` or `include`, quotes
  `expectedVersion` like every other owner write and spends a version when the
  authoritative plan changes; it moves no phase, so a host may plan from the
  lobby, mid-activity or in an intermission. Anything already played and whatever
  is on the screen is `invalid_plan` — refused out loud rather than silently
  reordered around — and a move renumbers the remaining activities into the
  `SortOrder` slots they already occupied, so a played round keeps the number it
  had. **An exclusion destroys no preference**: "the room wanted this and there
  was no time" is information about the evening, and deleting the count would
  erase the evidence the host's own decision was about. `party_game_exclusions`
  is keyed on the SESSION, so a restart discards it with the rest of the match.

- **INTERMISSION is a pause, and it is not FINISHED.** `return_to_party` is
  legal only from `RESULT` and COMPLETES the round the room just saw the outcome
  of: a pause happens between two finished activities, never instead of
  finishing one. The status stays `live`; the rounds, the plan and the
  preferences all survive. What hands the television back is the presentation
  projection reading the phase — `TvPartyPresentations.Decide` answers
  `slideshow` for `intermission`, immediately and with no dwell, because a
  FINISHED game earns its 15-second closing card while a host who has just said
  "back to the party" is standing in front of a room expecting the music.
  `next_challenge` resumes on the same edge it takes from a result, minus the
  round to complete, and the takeover returns with no special case. The session
  clears `CurrentRoundId` on the way in, so the control room never describes an
  activity nobody is looking at.

- **`PartyChallenge.Kind` is dormant metadata, not behaviour.** The column, its
  four values and the wire field all stay, for the adaptive game they were
  designed for. What went is the DECISION: the composer no longer asks (a room is
  shown an activity, not a taxonomy, and "dare, penalty, guess or custom?" was a
  required step that changed nothing anybody sees), the canonical card draws
  neither the word nor a `data-kind` a stylesheet could colour by, and the four
  per-kind accents are gone with it. `kind` is OPTIONAL on the write in both
  directions: omitted, a new activity becomes `custom` and an EXISTING one keeps
  whatever it was written with — which is what makes the composer's silence
  preserve history instead of rewriting it. A client that still sends one still
  has to send a value the domain knows.

- **Duplicating a party copies the DECISIONS and none of the history.** Title,
  windows, guest slots, the deck (new ids, same title/body/`kind`/media/enabled/
  order/duration/voting mode/question), the slideshow timings, the quotas, both
  approval modes, the game switches, the preference budget and the print budgets
  travel. Participants, preferences, votes, rounds, uploads, greetings, prints,
  face searches, televisions, display grants, the heartbeat and every token do
  not — last year's guests did not attend this year's party, and last year's QR
  must open nothing. Three things are easy to undo by accident. **Media is
  SHARED, not copied**: the clone gets its own album with its own membership rows
  pointing at the same `FileItem`s and therefore the same blobs, so nothing is
  re-uploaded and either party can be edited freely. **The clone is a DRAFT that
  already has its capability** — the new link carries the settings that live on
  it and therefore new tokens, while the party's own `draft` status is what makes
  the public seam refuse every one of them until the host publishes. And the
  clone's ALBUM needs its own name, numbered until it is free: album names are
  unique per owner and a party title is not, so taking the title verbatim failed
  on the constraint the second time anybody duplicated anything. Print counters
  and the public print sequence restart at zero; paper spent at another party is
  not this party's history.

- **An arrival is its own fact, and the guest list is optional.** Attendance
  (`PartyGuestAttendance` for a person on the list, `PartyAttendanceGuest` for
  anybody else) is separate from what a person declared (`PartyRsvp`) and from the
  anonymous browser (`PartyParticipant`): `PartyGuest ≠ PartyAttendanceGuest ≠
  PartyParticipant`. There is no party type — a party with no invitation group is
  an open party and no Party feature requires a group, a guest or an RSVP. Four
  things are easy to undo by accident. **An arrival never writes an RSVP** —
  `declined + arrived` is valid and stays declined. **The public QR never records
  an arrival, and nothing binds a guest to a participant**: "Entra nel Party" on a
  live invitation is navigation to the party's own public page (only when
  `ResolvePublicAsync` would open it), never an identity carried across. **One
  row per guest, two sources**: the host's check-in and the group's own "Sono qui"
  (invitation token, own group only, Live only) write the same row; the first to
  succeed fixes `CheckedInAt` and `Source` (`owner`/`invitation`, a closed check
  constraint), and a group may take back only its own mark. **Host writes are open
  in Live and Ended only** (`409 attendance_not_open` before); an other arrival is
  idempotent by `(PartyId, ClientRequestId)` and renamed by `Version`. Its
  migration is NOT automated and NOT previous-application compatible, for the same
  restricting-key reason as the guest list's. See
  [party-attendance.md](party-attendance.md).

- **The guest list is a private Before domain, and its link is not the party's.**
  `PartyInvitationGroup` / `PartyGuest` / `PartyRsvp` / `PartyRsvpQuestion` /
  `PartyRsvpAnswer` / `PartyInvitationDelivery` are the host's private facts, and a
  group's personal token (`HMAC(Party:TokenSecret, CapabilityId ‖
  "invitation-rsvp")`, stored only as its SHA-256) opens THAT group's invitation and
  reply and nothing live: no upload, game, print, greeting or face search. It is
  resolved under `/api/party-invitations/{token}` and drawn at
  `/party/invite/{token}`, never under `/api/party/{token}`, and only while the
  guest experience is FULL — once only the memories remain, the QR opens them and
  the personal link is the same 404 as an unknown one. **Its key has no
  built-in fallback**: `Party:InvitationTokenSecret`, else an explicitly
  configured `Party:TokenSecret`, else the API and worker refuse to start, since
  a key public in the source plus the stored capability ids would make a
  database dump into every group's link. Its migration is classified NOT
  automated and NOT previous-application compatible: the old teardown does not
  know the new restricting child tables. Five things are easy to undo by
  accident. **`PartyParticipant` stays the anonymous runtime browser
  identity, and nothing binds the two** — opening or answering an invitation
  mints no participant and sets no cookie, and nothing infers a binding from an
  email, a name or a device. **Every write to a group spends its version first**,
  through one conditional `UPDATE … WHERE Version = @quoted` issued inside the
  transaction — guest replies, owner edits, rotation and removal alike — so a
  stale writer writes nothing rather than half a family. **A send is idempotent
  through its ledger row**: `(group, ClientRequestId)` is unique, the row is
  committed `pending` before SMTP, a replay returns the row and never calls SMTP,
  and an attempt that died after SMTP stays `pending` ("esito non confermato")
  until the host presses resend, which is a new click. **The first send from a
  Draft publishes through `PartyLifecycle`** quoting the party version the page
  read, and a failed email leaves the party published. **A changed recipient
  address rotates the link**, and only deliveries that carried the CURRENT
  generation count as "sent" — which is also what gates reminders. Replies are
  writable only while `published`; "required" means required of a group that is
  coming; duplicate copies active question definitions and nothing of the list;
  `PartyStateEraser` erases all six tables explicitly. See
  [party-rsvp.md](party-rsvp.md).

- **The host reads the guest list in PAGES, and `shared` is not `sent`.** "Ospiti"
  is a console over the guest DIRECTORY
  (`POST /api/parties/{id}/guest-directory/query`), a projection beside the full
  guest list: the database searches, filters and orders, a page carries only what
  a card shows, and one group's detail is a second read. The full
  `guest-list` projection remains for editing and configuration. Five things are
  easy to undo by accident. **The search is the server's**, over each row's folded
  `SearchText` (accents and case dropped, a phone also as digits) — a derived
  cache every write maintains and `PartySearchTextReconciler` re-derives at API
  start, so a row written by an application that did not know the column is
  searchable again; a client-side search would disagree with it, which is why
  none exists. **The cursor carries the last item's own sort key**, not an
  offset, so a group added or renamed between two pages neither repeats nor skips
  the rows around it; it is ENCRYPTED with the data-protection keys (it holds a
  label, and URLs reach access logs) and bound to a hash of party+search+filter,
  so a replayed cursor is refused rather than reinterpreted. **A delivery now has
  a CHANNEL** (`email`, `whatsapp`, `copy`) and the database holds channel and
  status together: an email is `pending`/`sent`/`failed`, a share is `shared` and
  can be nothing else — NubArca hands the host a click-to-chat link and never
  learns whether a message was sent, delivered or read, and there is no provider,
  API or webhook. A national phone number is never guessed into a country code.
  **Invited means invited on the CURRENT link by any channel** (`sent` or
  `shared`, never a reminder) — one definition behind initial/resend, the reminder
  rule and the *Da invitare* filter — and a rotation still resets all of it.
  **Owner mutations answer minimally when asked** (`Prefer: return=minimal`,
  RFC 7240): without the header every route answers exactly as before, so the
  console can page a thousand groups without an edit returning all of them. Its
  migration IS automated and previous-application compatible: additive columns
  with defaults, a widened status constraint, and one index.

- **The guest SEARCH is personal data, so it lives in a body and in memory.**
  Reading a page of the directory is the one read in NubArca that is a POST: a
  host looking for a guest types a name, an address or a number, and a query
  string is copied by default into the address bar, the browser history, the
  `Referer` of the next request and every proxy's access log. So there is no GET
  that accepts a search — a second way in would be a second way to leak — the
  endpoint is owner-only/`party.access`/`no-store`/404-on-foreign and passes the
  ordinary same-origin check like any unsafe method, and **the search is never
  audited or logged**. The console keeps `guestState` and `guestGroup` in the
  URL and the needle only in React state: it survives opening and closing a
  group and is deliberately lost on a reload, and a legacy `?guestSearch=` is
  stripped by a replacing navigation rather than honoured. Paired with it, the
  card list is VIRTUALIZED with `@tanstack/react-virtual` against the shell's
  scroll viewport (`useAppScrollMargin`, shared with the media wall): a thousand
  loaded groups mount a couple of dozen cards, the page keeps its own single
  scrollbar, and the next page is asked for when the visible range nears the
  end. A card offers `[ primary ] [ Dettagli ] [ ⋮ ]` — the primary is a
  recommendation from `primaryInvitationAction`/`primaryInvitationLabel` and
  never a status, Dettagli is never only in the menu, and the menu drops
  whichever action the card already shows.

- **A browser on `/tv` is a NubArca display, not a preview.** It pairs, is
  listed, assigned, taken over and revoked exactly as a Fire TV, because it
  reads the same control plane with the same rules: `frontend/src/tv/` holds a
  PORT of the app's pure policy (assignment view, navigation state machine,
  greetings/Hero/boundary ledger, slideshow timing, live items, game grant,
  viewer remote) and `semantics/nativeParity.test.ts` loads the app's modules
  from `tv/src` and runs both on the same cases — a rule changed on one side
  fails the web suite, and a new native export must be ported or excused.
  Three things are easy to undo by accident. **Only a 401 unpairs** — the TV
  session was revoked or has expired: a boot or a poll that fails for any
  other reason keeps the session and retries on a capped backoff. **Every
  request the display waits on is bounded**: polls are single-flight with a
  timeout (`platform/usePoll.ts` — a read asked for mid-flight runs after the
  one in the air, so an old answer can never land last), including the game
  stage's snapshot poll; the one-off requests — the assigned slideshow's first
  load, the game grant mint, the party boundary POST — carry a `startDeadline`
  and treat its expiry as a transient failure, so a half-open socket after a
  sleep can hold nothing for ever, and a boundary is settled at most once.
  **Nothing platform-specific leaks into the Party
  logic**: wake lock, fullscreen, lifecycle/resume (including a sleep detected
  from the clock) and key mapping are the injected `DisplayPlatform`, which the
  tests replace. The game renders the canonical stage IN-PROCESS
  (`PartyDisplayStage`, shared with `/party-display/stage`) from a grant minted
  by the TV session — never a party token. The greetings band's position is a
  `RibbonCursor` (index AND the message on screen, one piece of state) in both
  renderers: an id kept in a ref written during render was overwritten by a new
  feed before the refresh read it, so hiding an earlier greeting skipped the one
  being read — fixed in the app and the browser alike. `scripts/tv-browser-e2e.sh`
  proves the whole life of a display in CI, a real Chromium restart on the same
  profile included; hardware sleep/resume, Windows/Edge and real remotes are a
  manual matrix.

- **The console's numbers ARE its filters.** One row, not numbers above a row of
  chips naming the same things differently: each tile counts PEOPLE, opens the
  groups it counts, and is pressed while it is what the list shows; pressing it
  again returns to everybody. The order is `guestDirectoryStatesFor` — before
  the party *In lista · Confermati · Da rispondere · Non presenti · Da
  invitare*, from live *In lista · Attesi · Arrivati · Mancano · Altri arrivi*
  (live now offers `attending`, which "Attesi" opens). *Da invitare* is
  `notInvitedGuests`, the named guests of the groups the `not_invited` filter
  itself lists — never a second definition of "not invited" — and a server
  without it shows "–", not a false zero. The Live section's numbers are the
  same shortcuts into the console.

## Next: NUBARCA-UX-01.5 — Viewer Pagination Continuation

Known, scoped, deliberately NOT fixed by the portrait/rotation slice.

The viewer receives only the media the gallery had already loaded when it
opened, so reaching the end of that sequence does not fetch the next page.
Observed on the acceptance build: a gallery holding two pages opens a viewer
that reports `10 / 120` even though the library has 172 items. Returning to the
gallery lets pagination continue normally, so nothing is lost — the sequence
simply stops where the gallery had stopped.

Keeping it out of the orientation slice was deliberate: orientation policy is a
navigation decision, and this is a data-continuation one.
