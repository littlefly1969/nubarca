# NubArca current baseline

Short, current-state context for development agents. This file is **not** a work
log: it carries no slice narratives, no branch names, no commit SHAs and no
"next step" notes. Released work is described by `CHANGELOG.md`; how the system
is built is described by `ARCHITECTURE.md`.

## Baseline

- Release: `0.3.0` (server and web)
- NubArca TV: `1.0.10`, `versionCode` 12, OTA runtime `nubarca-tv-native-11`
- Backend: ASP.NET Core / .NET 10, EF Core, PostgreSQL 17
- Frontend: React, TypeScript, Vite
- Runtime: Docker Compose with separate API, worker and frontend services
- CI: GitHub Actions verifies identity, backend, frontend, TV and mobile on pull
  requests and `main`; the external backend lane runs nightly or on demand; a
  separate manual, `main`-only native TV workflow builds and validates the
  definitively signed APK and optionally publishes an immutable GHCR bundle. A
  separate manual, `main`-only OTA workflow is the sole ordinary OTA signer and
  publishes a signed immutable GHCR bundle without contacting production.
  Production pulls verified application/APK/OTA artifacts by digest; the guided
  `deploy/update-production.sh check|apply` path refuses migrations and never
  builds on the server
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
- **One shared source cannot hold two snapshots.** A source row owns its
  revision, content hash and chunks, so indexing domain B at a different commit
  would rewrite what domain A is serving. That is refused
  (`shared-source-snapshot-conflict`), not resolved: reindex every domain that
  shares the source at the same revision. A source only one domain claims moves
  forward normally.
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
- **The mobile Android test binary and Play binary are ONE release variant.**
  `Mobile Android release` is manual, protected-main-only and emits a signed APK
  for direct physical-phone testing plus an AAB for Play from the same source,
  version contract and dedicated upload key. The release contract pins package,
  monotonically increasing versionCode, API 36 target, Android 7 floor and the
  public signer fingerprint; the Expo config consumes it rather than duplicating
  identity. GitHub sees private key bytes only after repository tests pass,
  validates the AAB with pinned Google bundletool, validates the universal APK
  generated back FROM that AAB, gates 16 KB native-library alignment and emits
  provenance attestations. The APK is the pre-Play sideload path; after Play App
  Signing enrollment, internal testing is the accepted path because Play signs
  delivered APKs with its separate app-signing key. The key may never be shared
  with TV or replaced to fix a build. `docs/mobile-release.md` is the runbook.
