# Changelog

This changelog begins at the NubArca product baseline. Earlier development
happened under a different product name; that history is preserved in the
originating repository and is deliberately not reproduced here.

## Unreleased

### Safer production updates with database changes

- **One reviewed command path now covers additive database migrations.** The
  production updater validates an explicit migration compatibility policy,
  checks backup capacity, creates and verifies a PostgreSQL dump, applies the
  schema with the immutable candidate API image and verifies migration history
  before changing any application pin.
- **Unsafe migration shapes still stop.** Rewriting migration history, omitting
  the compatibility declaration or introducing a schema that the previous
  application cannot use leaves production untouched and requires a separately
  reviewed manual plan.
- **Migration classification is now complete before release.** From the tracked
  policy boundary onward, CI requires every migration to be classified, even
  when automation is explicitly refused. The first production jump governed by
  this policy now includes the additive document-derivation, visual-retrieval
  and Party challenge migrations instead of discovering the two document
  entries only when the server checks the release.
- **Rollback has an argument, not a hope.** An old application image is restored
  after a failed smoke test only for migrations reviewed as compatible with it;
  the verified pre-migration backup and its checksum remain available throughout.

### Videos play the same way everywhere

- **Videos play on Android again.** The phone app used to decide for itself
  whether a video was playable by looking at the content type the server
  happened to label the stream with — and a stream labelled anything other than
  `video/...`, or not labelled at all, was written off as unavailable even
  though the server had already authorised it and was serving it. That check is
  gone. If the server says here is your video, it is your video.
- **One contract, three players.** The web viewer, the phone and the television
  now read the `/video` answer with the same rules, from the same file: the same
  test matrix runs in all three and fails in all three if any one of them starts
  to disagree. Before this, each had grown its own slightly different idea of
  what the same response meant, which is why a video could play on a television
  and refuse on a phone.
- **A slow conversion is no longer mistaken for a broken one.** When a video is
  still being prepared for adaptive playback, every client now waits the same
  way — quickly at first, then backing off, and never longer than the server
  asks for. The phone used to give up after ten tries and show an error for a
  video that was simply taking a while; it now waits for as long as the work
  actually takes, and stops the moment you leave the screen.
- **A momentary network hiccup no longer looks like a missing file.** A dropped
  connection or a busy server is retried quietly a few times before anything is
  said, instead of replacing your video with an error the next attempt would
  have cleared. A file that really is gone, a session that has expired and a
  server that is briefly unhappy stay three different things.

### Find a document by how its pages look

- **Your documents can now be found by their layout, not only by their words.**
  A table of quarterly costs, a form with fields to sign, a slide, a spreadsheet
  grid, a heading hierarchy: these are what a document IS, and they are exactly
  what a text search flattens away. NubArca now renders your documents' pages,
  looks at them locally with the same image model the photo library already
  uses, and uses what it sees to decide **which document to read**. What it then
  says still comes from that document's text, with the same evidence rules as
  before — so a page that merely looks right, and says nothing useful, produces
  no answer at all rather than a confident guess.
- **Nothing leaves, and no page is kept.** Rendering, looking and searching all
  happen on your own server. A page is drawn, looked at, and dropped — no copy
  of it is stored anywhere — and no image is ever sent to a language model. The
  model that writes the answer still receives only bounded text, exactly as it
  did before.
- **Word, Excel and PowerPoint are laid out in a sealed container.** Working out
  where things sit on a page of a `.docx` means running a real office suite over
  a file somebody uploaded, so NubArca does it somewhere that has no network
  connection, no database password and no idea whose document it is holding. It
  is handed bytes and told only what format they are; it can be asked nothing
  else. Without that container deployed, PDFs and notes still get visual search
  and Office documents keep working from their text.
- **A citation still points where the document says, not where the renderer
  broke the page.** A rendered page number for a Word file is an artefact of the
  layout engine — a different version puts the table somewhere else — so answers
  keep citing the section, sheet or slide the document itself declares. A real
  PDF page stays a real PDF page.
- **An incomplete reading is never published.** If page 13 of a twenty-page
  contract cannot be drawn, none of that contract's pages become search results.
  A document that reads as whole and is not is worse than one that is simply
  unavailable.
- **Off until you turn it on.** Enabling it re-draws and re-reads every document
  in every library, which is real time and real disk, so it is a decision an
  operator makes rather than one an upgrade makes for them.
  `documents visual-index`, `visual-status` and `visual-evaluate` do the work
  and report what it cost. Existing text search is completely unaffected by all
  of this, before or after.
- **Measured, and reported honestly.** On a mixed set of thirteen questions the
  visual pass takes Recall@5 from 0.833 to 0.917 and MRR from 0.778 to 0.917,
  recovering one question and regressing none. With the real image model in
  place it identifies the right document first for eleven of those thirteen
  questions.
- **A newer class of "late interaction" model was tried, and turned down.**
  `colSmol-500M` was run against the same questions on the same pages: it
  changed the results by nothing at all, while needing 97× the storage per page,
  four times as long to index, three gigabytes of memory and an extra
  eighth of a second per question. So it is not enabled. The measurement, the
  numbers and the tooling to repeat it with a different model all ship —
  turning a switch on without having looked would have been a guess wearing a
  version number.

### Party guest messages

- **Guests can now leave a short written greeting as well as photos and videos.**
  The public Party upload page presents two explicit contributions — *Foto o
  video* and *Messaggio* — and a message is at most 120 characters plus an
  optional 40-character name. Both limits are counted in **Unicode code
  points**, the one unit .NET and a browser measure identically, so the live
  counter under the box can never disagree with the validator that decides. The
  server normalizes before it measures: line endings and whitespace collapse to
  single spaces, and bidi overrides, zero-width padding and soft hyphens are
  removed, while the joiners that hold emoji families and Persian/Indic text
  together are kept. Text stays plain end to end — no HTML, no Markdown, no
  interpreted links — and message bodies never reach an application or security
  log.
- **Approval is optional and set per Party.** With it off a greeting is live
  immediately; with it on it waits, and the guest is told so. It is independent
  of the photo/video approval switch, and it governs new submissions only —
  turning it off does not publish a backlog somebody declined to approve.
- **An owner can delegate message moderation, and only that.** A new per-member
  capability, *Può gestire i messaggi Party*, lets one member approve, reject,
  hide, restore and Hero-promote guest messages. It is not a role: the album's
  `editor` gains nothing, and the delegate gains no Party governance — no
  tokens, settings, slideshow timing, pairing, face-search, photo/video
  moderation or member management. Only the owner may grant it, and clearing it
  or revoking the membership takes effect on the very next request.
- **Messages belong to one event.** They are scoped to the Party link rather
  than the album, so re-enabling Party mode starts a silent wall instead of
  resurrecting last year's greetings, and revoking a Party empties the feed
  without rewriting a single row.
- **A revoked delegation cannot come back with a re-invitation.** Membership
  rows are reused when the same person is invited again, so the capability is
  now cleared explicitly on both revoke and re-invite: a new invitation
  lifecycle always needs a new decision by the owner. The runtime check is
  unchanged and still the enforcement — an accepted, unrevoked membership
  carrying the flag — so this is defence in depth rather than a new gate.
- **Moderation is a state machine in the domain.** The five moves the product
  offers — approve, reject, hide and the two ways back through restore — are the
  only ones the backend permits; `visible → rejected`, `pending → hidden` and
  the idempotent-looking no-ops are refused with `400 invalid_transition`. The
  UI and the API now expose exactly the same set, and an audit line can no
  longer describe a decision nobody made. Authority is still checked first, so a
  stranger attempting an impossible move gets a generic not-found rather than a
  400 confirming the message exists.
- **Deleting an album that has hosted a Party works again.** Every Party table
  carries a restricting foreign key to the album and none of them was cleaned
  up, so deleting such an album failed on the constraint — a defect that
  predates guest messages and that the new table would have added to. Album
  deletion now removes its Party state in foreign-key order, exactly as it
  already removed its shares. Guest photos are untouched and stay in the owner's
  library.
- **Fire TV shows them two ways.** An *Elegant Ribbon* holds one message at a
  time in a still, high-contrast band across the bottom — never a ticker — and
  steps aside while the MENU/QR overlay is up. A *Hero* card, promoted only by
  the owner or their delegate, appears full-screen roughly every ten media
  transitions in a genuinely autoplaying slideshow: it holds the current item
  rather than moving the index, waits for the boundary a video was going to
  reach anyway instead of truncating it, and is suspended entirely while a guest
  is running a Party face filter. Older TV builds are unaffected —
  `TvAlbumItem.mediaType` is still `image | video`, and the message feed is a
  separate endpoint they never call.
- **A Hero card cannot strand the wall or talk over a video.** The advance a
  card postpones is now an explicit ledger with a single function able to spend
  it, so a card withdrawn early still hands the wall on — a finished clip can
  raise no second boundary — and a card timing out as the poll withdraws it
  advances once rather than twice. A paused wall keeps the owed advance and takes
  it on resume; a face filter or manual navigation discards it, because the
  index then belongs to whoever just chose it. A card raised at a video's
  configured cap also withholds playback for its duration, so the clip no longer
  keeps running, audio included, behind an opaque card.

### Mobile Android

- **Physical Android HLS probing no longer discards the response type before
  classifying it.** The bounded Range probe snapshots status and `Content-Type`
  before aborting the body transfer. This preserves React Native's native
  response head and prevents a real HLS `200` from becoming the false “Video
  non disponibile per la riproduzione” state. Mobile advances to `0.2.7`
  (`versionCode` 8) for physical playback acceptance.
- **Long viewer sessions no longer drift after mixed portrait/landscape use or
  falsely retire playable videos.** Pager geometry now comes from the list's
  measured layout rather than the window notification that precedes it;
  rotation re-anchors by direct pixel offset, and a scroll completion is
  accepted only when it belongs to a user gesture begun at the current width;
  the correction repeats when native content reaches its new size, so a
  high-index landscape target cannot be clamped against the old portrait width.
  Only the focused video probes the server, it refreshes the manual session
  cookie when focus returns, and exhausted preparation, rate limiting, server
  faults or transport timeouts surface as retryable playback failures instead
  of the permanent “video unavailable” verdict. Mobile advances to `0.2.6`
  (`versionCode` 7) for sustained physical viewer acceptance.
- **The media viewer now preserves the selected item through rotation and waits
  for real native video readiness.** Full-screen photos and fallback posters use
  contain without changing thumbnail crop behavior; a width change re-anchors
  the mounted pager to its current logical index without becoming a fake swipe.
  Video status starts from `player.status`, follows replacement-player events,
  and mounts the native surface only at `readyToPlay`, leaving probe,
  preparation, loading, unavailable and error states explicit. Mobile advances
  to `0.2.5` (`versionCode` 6) for physical acceptance of the completed viewer.
- **The full-screen media viewer now owns a real pager viewport.** The pager
  fills the available height and gives every photo/video cell exactly one device
  width. Back navigation leaves first and clears the viewer sequence on route
  unmount, while render-time index clamping prevents stale positions from
  dereferencing an absent slide. Mobile advances to `0.2.4` (`versionCode` 5)
  for physical acceptance of the viewer fix.
- **Authenticated images no longer stop globally after sustained browsing.**
  The six-request image semaphore now transfers an occupied permit directly to
  a queued fetch without incrementing its active count. Diagnostics expose only
  aggregate active/queued counts, and regression coverage proves liveness after
  queued bursts, permanent failures and logout generation changes. Mobile
  advances to `0.2.3` (`versionCode` 4) for physical acceptance of the fix.
- **The signed APK now reaches the login screen instead of closing from the
  Android splash.** The release had autolinked `expo-font` 57 beside Expo SDK
  54's `expo-modules-core` 3, producing a native `NoSuchMethodError` before
  JavaScript could start. `expo-font` is now an explicit SDK-owned dependency,
  every autolinked Expo Android module is checked against the SDK 54 version
  map, and both PR CI and the signed release run cold-launch the generated APK
  in an Android emulator. Mobile advances to `0.2.2` (`versionCode` 3).
- **Cold start on a fresh installation no longer initializes authenticated
  sync storage before login.** The SQLite ledger now uses Expo's database-name
  contract, its parent is created synchronously by Expo, and an optional sync
  initialization failure cannot take down the gallery. SecureStore reads are
  bounded so a native storage fault cannot leave the restore screen spinning
  forever. The Expo status-bar package is aligned with SDK 54. Mobile advances
  to `0.2.1` (`versionCode` 2) so the corrected signed APK can update the first
  test build in place.
- **Device-media synchronization ships.** The mobile client is a first-class
  authenticated gallery; it ingests locally stored photos and videos one-way into
  the owner's private library via the owner-scoped `POST /api/files` path. Sync is
  explicitly opt-in (off by default), defaults to new-media-only via a per-account
  baseline, and is driven by a per-account SQLite ledger with a 16-byte CSPRNG
  operation id sent as the `Idempotency-Key` (operation identity, never a SHA-256
  content hash — blob dedup stays server-side). Uploads default to Wi-Fi only and
  background execution is best-effort.
- **Signed Android release pipeline.** A manual, protected-main workflow emits a
  signed direct-test APK and a Play-ready AAB from one release variant and one
  upload key, pinned by `mobile/release-contract.json`; Google Play App Signing is
  the store boundary.

### TV client

- NubArca TV advances to `1.0.10` (`versionCode` 12) with OTA runtime
  `nubarca-tv-native-11`. OTA bundles are published through the protected-main
  GitHub workflow as signed immutable artefacts that production pulls by digest;
  the ordinary OTA signer never contacts production or rebuilds the APK on the
  server.

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
- **Permissions** — one catalogue of fourteen feature-surface keys
  (`people.access`, `semantic-search.access`, `laboratory.access` and its two
  sections, `cloud-functions.access`, `private-vault.access`, `tv.manage`,
  `cast.access`, and five separate administration permissions). The catalogue is
  authoritative: an
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

### Ask NubArca

An optional assistant that explains NubArca **as a product**. It is off until an
operator configures a model, and it answers from the documentation shipped with
the running release — never from the library.

- **Protocol and trust are separate.** An endpoint speaks the OpenAI-compatible
  chat format whether it is a hosted provider or the operator's own model
  server, so the format says nothing about who holds the bytes. Each named model
  profile states `Trust=External` or `Trust=LocalTrusted`, and NubArca **never
  guesses it from the URL** — `localhost` is what a reverse proxy in front of a
  cloud API looks like, and a trusted GPU server on another host is not on this
  LAN. An unknown, empty or numeric value is invalid rather than "probably
  local", and no browser request can choose or override it.
- **Local means eligible, not powerful.** Effective capability is
  `model trust ∩ feature policy ∩ caller permissions`. A trusted local model is
  eligible for private context and read tools; Help gives it neither, because
  Help's own policy is public product knowledge. Configuring a local model makes
  Help local — it does not make Help able to see anything new. No trust level
  grants write tools or unconfirmed execution.
- **The boundary is structural.** The Help service's constructor holds a
  text-only model runtime, a retriever, the model resolver and a logger — no
  database, no storage, no people, no albums, no search. The outbound request has
  no `tools`, `functions` or `tool_choice`, absent rather than empty, and the
  runtime interface has no optional parameter reserving one. The chat request
  has no field for a file, album, person, search, URL or retrieval domain.
- **Answers are grounded, or there is no answer.** Product knowledge comes from
  an explicit manifest of approved documents, each classified by audience,
  intent and kind, replacing "every `docs/**.md`" — which let an operations
  runbook outrank the guidance somebody asking "how do I use faces?" needed.
  Retrieval is local and deterministic: section-aware chunks, one shared
  Italian/English stopword set, a feature-alias catalogue, field-weighted
  ranking, and an evidence gate. Below the gate NubArca makes **no model call at
  all**, rather than paying a boundary crossing for an answer improvised from
  irrelevant paragraphs.
- **Faces guidance** was written for the assistant, in Italian and English, and
  is held against the interface it describes: a test fails if a tab is renamed
  without the guidance following.
- **The disclosure tells the truth about which model answered** — distinct copy
  and a distinct badge for external and local, and deliberately no claim that a
  local endpoint has no internet egress, because NubArca does not run that
  process and cannot prove it.

Conversations are not stored: they live in the browser, and a bounded slice
rides with each request. See
[docs/help-assistant.md](docs/help-assistant.md).

### Local retrieval platform

The retrieval behind Ask NubArca became a general capability. Product Help is now
one **domain** on it rather than its shape — and the same substrate now carries
owner-private documents and a local assistant, with no redesign.

- **Domains carry a privacy policy, defined in code.** `product-help` is public
  and may ground an external model. `nubarca-repository` — NubArca's own approved
  tracked source, for development and diagnostics — is system-internal and
  **never** reaches an external model. That holds even though NubArca is public
  on GitHub today: public hosting is a fact about this month, not a property of
  the domain, and the rule has to stay right for an installation carrying local
  patches or a private fork. The policy is a compiled table, so no database edit,
  admin endpoint or restored backup can widen it — and the check runs over the
  evidence itself, before a prompt exists.
- **One source, many domains.** A document that is both repository knowledge and
  approved product help is stored once, chunked once and embedded once, with a
  membership row per domain. Each domain keeps its own classification of it.
- **Retrieval is hybrid, and lexical stays first-class.** Optional local ONNX
  text embeddings, a dimension-scoped pgvector index and Reciprocal Rank Fusion
  join exact matching rather than replacing it — an identifier, a configuration
  key or a file name is a permanent use case that vectors are worse at. The
  fusion works on ranks rather than scores, because BM25 and cosine are not
  calibrated to the same scale.
- **Embeddings are local, and there is no hosted alternative.** Embedding is how
  NubArca decides what to send; routing that through a third party would send the
  whole corpus somewhere in order to work out what may leave. Model weights are
  never committed and never downloaded — a missing model degrades retrieval to
  lexical with a reason, and nothing becomes unhealthy.
- **Semantic retrieval is off by default and fails soft.** Disabled, no profile,
  missing model, missing pgvector and an unsupported dimension all fall back to
  lexical and say which. Product Help keeps working with no database index at
  all, from the corpus in the image.
- **Operator diagnostics** — `rag domains`, `status`, `index`, `coverage`,
  `query`, `evaluate`, `seed-profiles` and `validate-model`. `rag query` shows
  what retrieval found and how each path ranked it, and never calls a generative
  model: "retrieval found the wrong thing" and "the model wrote something wrong"
  are fixed in different places. `rag evaluate` reports Recall@5, MRR and
  top-3-expected-source against a golden set, with no LLM judging anything.
- **Query text, passage text and vectors are never logged, returned or sent.**

Hardening found by reviewing the pushed implementation, before anything private
is introduced:

- **A partial index run no longer deletes what it did not look at.**
  `--limit N` used to reconcile as though it had seen the whole snapshot, so a
  command asking for less work removed every membership past the cap.
- **Repository bytes are read from the commit, not from the working tree.** The
  indexer resolves a revision and then reads Git objects at it, so an index can
  no longer stamp a commit SHA onto whatever was half-edited on disk. Tracked
  symlinks are refused rather than followed, so no target outside the checkout
  can be imported.
- **Content identity and domain snapshot are separate, so two domains sharing a
  source upgrade one at a time.** A source row is one content interpretation
  `(SourceKey, ContentHash, IndexFormatVersion)`; the **membership** records
  which snapshot its own domain reads that content at. A file unchanged between
  two revisions is the same row, so the second domain's upgrade moves one
  revision forward and re-derives nothing — identical content, chunks and
  embeddings are reused. A row only one domain uses is rewritten in place, so an
  ordinary edit still costs one embedding; a row another domain is serving forks
  instead, and the old one is deleted when its last membership leaves. The
  predecessor kept the revision on the source row: advancing the repository
  rewrote what Product Help was serving, that conflict was refused — correctly —
  and Help could not go first for the same reason, which left a release
  lifecycle with no legal first step.
- **A domain holding two revisions refuses to answer** until a reindex
  converges, instead of picking a "most common" revision that means nothing.
- **Reclassifying a document now invalidates the cached index.** All the ranking
  metadata lives on the membership row, so a running server used to keep serving
  the old classification until it restarted.
- **A slow embedding model can no longer exceed its configured concurrency.**
  The inference slot is released when the native call actually stops, not when
  NubArca stops waiting for it, and a timeout is a sanitized resumable reason
  rather than a crash.
- **Improving a chunker now rechunks what is already indexed**, instead of
  applying only to files somebody happens to edit afterwards.
- **A benchmark question may not appear in the corpus it is measured against**,
  enforced by a test — which immediately caught one that had been line-wrapped
  back into the documentation.
- **An oversized tracked file is refused before it is read.** `ls-tree -l`
  carries each entry's blob size, so the verdict comes from the tree entry
  rather than from reading the object — size used to be learned by loading the
  thing being refused, which made a tracked multi-gigabyte file an
  `OutOfMemoryException` inside a service. Underneath it, the `cat-file` session
  enforces its own ceiling from the object header before allocating, because
  that number arrives from a subprocess and a caller who forgot to check must
  not be able to turn it into an allocation.
- **A dead `cat-file` session is killed, never resynchronized or reused.** Every
  blob read is time-bounded. The stream is a single conversation, so anything
  that abandons a response leaves those bytes queued and the next read would
  parse blob content as a header, returning every later object as somebody
  else's; resynchronising would mean consuming exactly the work being refused.
  The process is killed instead, the session faults, and every later call fails
  immediately with a sanitized reason.
- **Cancelling an index run is no longer reported as a repository timeout.**
  A delay linked to the caller's token completes the instant a run is cancelled,
  so the race read "the delay won" as "Git was too slow" and gave a
  permanent-looking failure to something the operator did on purpose.
  Cancellation now reaches the caller as itself; only the reason differs.

Media metadata, People and Faces did not become retrievable knowledge. See
[docs/rag-platform.md](docs/rag-platform.md).

### Owner-private document knowledge

`user-documents` is NubArca's first **OwnerPrivate** retrieval domain: the first
knowledge in the system that belongs to a person rather than to the product. A
supported native text document in somebody's library becomes extracted text,
chunks and local vectors, and a LocalTrusted model answers questions from it —
with nothing of anybody else's in the request, and nothing at all leaving the
installation.

- **Extraction and chunking are local and native.** A supported text document is
  read, extracted and chunked on the installation's own hardware; no document,
  and no fragment of one, is sent anywhere to be understood.
- **Text embeddings are local and profile-scoped.** Private vectors are produced
  by the same local ONNX embedding path as the rest of the platform and keyed by
  profile, so a profile change re-derives rather than silently mixing
  dimensions. There is no hosted embedding alternative here either.
- **Retrieval is owner-scoped before ranking, not after it.** The lexical index
  is built from one person's rows for that request, and semantic retrieval is
  exact cosine over that owner's eligible vectors. An approximate index with an
  owner predicate would not be an owner-prefiltered search — the traversal would
  cover everybody and the filter would only decide what surfaced, which silently
  gives a person with few documents fewer and worse results. Owner scoping
  therefore happens before ranking and before any limit is applied.
- **Derived rows are not authority; the live `FileItem` is.** A `DocumentText`
  records an extraction that happened in the past, and the file may since have
  been deleted or moved into the Private Vault. Every private read joins the
  live `FileItem`, so a chunk whose file no longer qualifies is not in the
  corpus at all. Cleanup is housekeeping: a boundary that only holds once a
  sweeper has run is broken for as long as that sweeper is behind. Blob identity
  is never knowledge authority — two owners holding the same deduplicated bytes
  have two independent extractions.
- **Private storage is separate, not symmetric.** Private content lives in
  `document_texts` / `document_chunks` / `document_chunk_embeddings`, which are
  owner-scoped by schema, and never in `rag_sources`. Forcing it through the
  system tables for symmetry would put a person's text in the table every system
  domain reads, one forgotten `WHERE` away from a cross-owner answer. What the
  two share is the contracts: chunking, embedding, fusion, the evidence gate and
  domain policy.
- **Only a LocalTrusted model may receive private evidence, and only a bounded,
  approved slice of it.** The policy is the intersection of model trust with
  domain policy, enforced over the evidence itself before a prompt exists. For
  an owner-scoped domain it also requires the authenticated owner, and every
  piece of evidence must carry it: unstamped evidence is refused as firmly as
  wrong evidence, because treating "null" as "probably fine" is exactly how a
  system domain's chunk would reach a private prompt.
- **An External model receives zero calls for the private-documents operation.**
  `Assistant__PrivateKnowledgeModel` must resolve to `LocalTrusted` with no
  fallback; an External configuration produces no provider call at all, so the
  question itself never leaves either. The operation derives the owner from the
  request's identity and the domain from a constant, and its request shape has
  no field for an owner, a domain, an object id, a model or a trust level — a
  client cannot redirect it, because there is nowhere to put the instruction.

Owner-private document knowledge is now supported through the dedicated
`user-documents` boundary. This does not add Assistant tools, actions, arbitrary
filesystem access, cross-owner retrieval, or External private generation.

### Rich document ingestion

A person can now put a PDF, Word document, Excel workbook or PowerPoint
presentation in their library and ask NubArca about it. A scanned PDF is
recognised locally. Citations keep a location a person can turn to — a page, a
heading path, a sheet, a slide.

Nothing about the privacy boundary moved. Rich extraction changes WHAT can become
eligible document text; it does not change who owns it, who may retrieve it,
which model may see it, or how that model is authorized.

- **Format is decided from the bytes.** The declared content type and the
  filename answer one question — is opening this file worth doing — and never
  what it is. An Office package must declare its own main part, and a filename
  that contradicts the declaration is refused rather than resolved in either
  direction: a DOCX renamed `.xlsx` reaching the spreadsheet parser is untrusted
  input arriving somewhere written for a different structure. Legacy binary
  Office, macro-enabled packages and password-protected documents are refused by
  name — legacy and encrypted share their first eight bytes, so without looking
  further every protected file would be reported as a 1997 Word document.
- **Packages are treated as hostile structured input.** Entry count, per-entry
  size and total uncompressed size are read from the archive DIRECTORY and
  enforced before the Open XML SDK is handed the package, so a compression bomb
  is refused on the strength of what it claims about itself, before a byte is
  expanded. Nothing is extracted to disk; traversal-shaped entries are refused
  anyway; external package relationships are never dereferenced. **Parsing means
  reading visible document information — it never means executing document
  behaviour.**
- **Word keeps what the document says.** The heading path comes from the
  document's own outline levels, tables are rows rather than loose cells, and
  deleted tracked-change text is excluded: somebody who struck a clause and sent
  the file for review has said that clause is gone. A hyperlink contributes its
  display text and never its target. No page numbers, because Open XML does not
  describe pages — pagination is a layout-engine result and any number here would
  be invented.
- **Excel is read, never run.** A formula is extracted as its expression plus the
  value the workbook last stored, and NubArca does not recalculate it or claim it
  did. Hidden sheets are not ingested: that is where lookup tables and scratch
  work live, and importing them would surface material the author took out of
  their own view. Rows carry their headers, because the relationship between a
  label and its value is the only reason the table exists.
- **PowerPoint keeps slide boundaries, and speaker notes are ingested** as their
  own block — the slide says "Pilot" and the notes say why the date moved.
  Hidden slides are not.
- **PDF renders only what it cannot read.** Page text first; a page is recognised
  only when its own text is unusable, judged by a bounded deterministic heuristic
  rather than by a length check that a scanner's header stamp passes and a
  broken-font page passes too. A page needing recognition that cannot get it
  makes the whole extraction unavailable rather than publishing a document
  quietly missing its scanned pages.
- **OCR is local, off by default, and a child process.** A managed wrapper binds
  native code into the API where a hang cannot be interrupted; a child can be
  killed, which is what makes the page timeout a bound rather than a report. The
  page travels on stdin, so no private page is written to a temp file. Stdout is
  read under a hard cap because it is untrusted process output. Nothing is
  downloaded — a language that is not installed makes OCR report not-ready, and
  an installation with no engine boots normally with affected PDFs reported
  retryable rather than permanently refused. Tesseract is the baseline behind a
  provider seam, not a claim about final OCR quality.
- **One reading of a file is authority.** `DocumentText.IsCurrent`, with a
  filtered unique index and the shared eligibility boundary requiring it.
  Resolving that by timestamp or query order would let a clock decide which
  interpretation of somebody's document answers them, and every such answer looks
  correct. When bytes change the old reading stops being authority before the
  replacement is attempted; when bytes are unchanged and a newer parser fails,
  the working reading keeps it.
- **Location is typed, and `Page` stays a real PDF page.** Slides, sheets and
  Word sections live in `LocatorKind`/`LocatorIndex`/`LocatorLabel`, because a
  field meaning "page-like thing" cannot be read without knowing the format and
  the reader that forgets renders "Page 4" for a spreadsheet.

No visual or page embeddings, no reranker, no query rewriting and no retrieval
tuning: rich text becomes ordinary passage text and flows through the retrieval
that already existed. No Assistant tools or actions were added, no legacy or
macro-enabled Office is parsed, and images embedded in documents are not
recognised.

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

### Albums

There is now exactly ONE way to choose photos and videos for an album: the
Media Library, with the multi-selection it already had. The shared-album flow
used to carry a second, smaller picker of its own — a photo-only grid with no
tabs, no search, no filters and no videos — so which media a user could reach
depended on which page they had started from.

- **One destination picker** — "Add to album" opens a single dialog listing
  *your albums* and, separately, the *shared albums you may contribute to*, each
  with the cover mosaic the album card already returns. The two groups are never
  flattened together: a shared row names the owner and the role held there,
  because that difference is real. A Viewer's album is not offered at all —
  a destination the server would refuse is worse offered than absent.
- **The endpoint distinction is ours, not the user's** — one *Add* button files
  into an owned album through the ordinary bulk membership route, and into a
  shared album through a new bulk contribution route. Contribution semantics are
  unchanged: a reference to media the contributor still owns, no copy, no
  transfer, still withdrawable.
- **`POST /api/shared-albums/{id}/contributions/bulk`** — up to 1000 ids, the
  same ceiling the owned-album bulk routes use, sharing one definition of
  "contributable" with the single-item endpoint rather than restating it. A
  Viewer is refused outright; everything per-file becomes counts, because naming
  a skipped id would say whether it exists. Duplicated, already-present, foreign,
  deleted, excluded, vaulted and non-media ids are skipped, never a reason to
  discard the valid ones. Each item that lands leaves the same audit row a single
  contribution would have.
- **A shared album's "Add from library"** goes to `/media` carrying the album as
  transient router state — not a URL, because "I am filling this album" is a
  moment in a session and not an addressable place. The Library stays the
  ordinary Library: every tab, search, filter, sort and the normal grid, plus a
  notice naming the album and a way back. The picker simply opens with that album
  already chosen.
- **Videos work** — media now comes from the library selection rather than a
  photo-only list.

An album is an album. "Shared with me" used to be a second primary destination,
which made somebody else's album read as a different product: a different list,
a different card, a bespoke wall and a bespoke lightbox. Ownership and role
decide WHAT a person may do with an album — never whether it looks and behaves
like a different thing.

- **One Albums destination** — `/albums` holds the user's own albums and the
  ones other people have shared with them, in one grid with one search and one
  sort, filtered by *All / Mine / Shared*. The collection lives in the URL, so
  `/albums?scope=shared` is a real address; the old `/shared-albums` list
  redirects to it and `/shared-albums/{id}` is untouched, because that is the
  RECIPIENT's album and owner and recipient must never resolve to one route.
- **Ownership stays unmistakable** — every card says whose album it is and, for
  a shared one, what the membership may do. A shared card carries no Delete at
  all: an action this caller may not perform is absent rather than disabled. The
  two collections remain two API shapes and are normalised only at the
  presentation boundary.
- **A pending invitation is not an album** — invitations keep their own compact
  section above the grid, and a received *copy* keeps its own, because accepting
  an invitation gives you a view of somebody else's album while accepting a copy
  gives you an album of your own that nobody can revoke.
- **The recipient gets the real browser** — All / Photos / Videos with the
  album's own counts, the same justified wall geometry as the library, and the
  same full-screen viewer. The bespoke shared lightbox is gone. The viewer now
  takes its media SOURCES explicitly: an album-scoped item uses only URLs the
  server built, and there is no branch that falls back to `/api/files/{id}`.
- **`GET /api/shared-albums/{id}/items` pages and filters** — `kind`, `cursor`
  and `limit`, in an envelope that also carries the album's per-kind counts. It
  used to serve a whole album on every open. `kind` is the only filter a shared
  album gets, and it is safe because it is nothing new: it is answered from the
  media category the item shape already carried. Filename, People, capture date,
  GPS, favourites and ratings stay absent — a filter that needed owner-private
  metadata to answer would BE that metadata, leaked one question at a time.
- **Album Play** — an explicit ▶ Play on owned and shared albums alike. It walks
  the current sequence (the active tab, search and filters), holds a photo for a
  bounded moment, waits for a video to end, pages as it goes and stops on the
  last item with an offer to run it again. It mutates nothing, which is why a
  shared Viewer gets the identical control. It is not Party and not
  Show-on-TV: those remain the owner's publication settings.
- **Nothing else moved** — download stays independently permission-gated per
  item, withdrawal stays the caller's own contribution only, curation stays the
  server's `canEdit` rather than the role label, and revocation still takes
  effect on the very next request, cursor in hand or not.

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
