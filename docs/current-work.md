# NubArca current baseline

Short, current-state context for development agents. This file is **not** a work
log: it carries no slice narratives, no branch names, no commit SHAs and no
"next step" notes. Released work is described by `CHANGELOG.md`; how the system
is built is described by `ARCHITECTURE.md`.

## Baseline

- Release: `0.3.0` (server and web)
- NubArca TV: `1.0.9`, `versionCode` 11, OTA runtime `nubarca-tv-native-10`
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
