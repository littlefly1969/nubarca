# NubArca TV

Native OTA operation, signing, publication, rollback, and bootstrap instructions are documented in [`../docs/tv-ota-updates.md`](../docs/tv-ota-updates.md).

A **separate** Expo React Native application for the 10-foot TV experience,
targeting **Fire Stick / Android TV** first. It is intentionally NOT the mobile
app and NOT a full NubArca client — see the architecture strategy in
[../docs/current-work.md](../docs/current-work.md).

## Application identity

**NubArca and NubArca TV are separate applications sharing one backend and one
account ecosystem.** There is no universal mobile/TV binary. The mobile app will
sync and upload through the shared backend; this app stays remote-first and uses
only the limited TV pairing and `/api/tv/*` contracts.

| | NubArca TV (this app) | NubArca (mobile, reserved) |
| --- | --- | --- |
| Display name | `NubArca TV` | `NubArca` |
| Android applicationId | `it.littlefly.nubarca.tv` | `it.littlefly.nubarca` |
| Android namespace | `it.littlefly.nubarca.tv` | — |
| iOS bundle identifier | — | `it.littlefly.nubarca` |
| Expo slug | `nubarca-tv` | `nubarca` |
| Deep-link scheme | `nubarca-tv` | `nubarca` |
| Version / versionCode | `1.0.1` / `2` | — |
| OTA runtime series | `nubarca-tv-native-*` | — |
| AsyncStorage session key | `nubarca.tv.session.cookie` | — |
| Published artifact | `nubarca-tv.apk` | — |

`tv/scripts/appIdentity.test.mjs` pins every value in the left column.

An Android `applicationId` has **no in-place rename**. Changing it is a new
application with its own private storage sandbox, so there is no upgrade path and
no way to carry a session across: every install of a new package id starts
unpaired and pairs once. Install fresh rather than with `install -r` whenever the
package id differs from what is on the device.

### Identifiers that must match the backend exactly

These are server contracts, not local choices. Changing one here without changing
it in the backend un-pairs the fleet:

| Identifier | Where | Contract |
| --- | --- | --- |
| `NubArca.TvSession` | `src/api/client.ts`, `TvPairingService.CookieName` | Limited TV session cookie for `/api/tv/*`. |
| `NubArca.Auth` | server cookie, never received here | Owner session cookie. The TV app must never send or store it. |
| `nubarca.tv.session.cookie` | `src/api/client.ts` AsyncStorage key | Package-local; immutable for in-place upgrades. |

## TV runtime decision

This app runs on the **React Native TV fork** (`react-native-tvos`) via an npm
alias, with the official Expo TV config plugin.

**Current baseline — Expo SDK 56:**

| Piece | Version |
| --- | --- |
| Expo SDK | `~56.0.17` |
| React | `19.2.3` |
| React Native (via alias) | `npm:react-native-tvos@0.85-stable` (0.85.3-3) |
| `@react-native-tvos/config-tv` | `^0.1.6` (latest; SDK-56 compatible) |
| `@react-native-async-storage/async-storage` | `2.2.0` |
| TypeScript / `@types/react` / `babel-preset-expo` | `~6.0.3` / `~19.2.14` / `~56.0.0` |
| **Node.js** | **>= 22.13.x** (SDK 56 requirement; verified on v22.22.3) — pinned repo-wide in [`../.nvmrc`](../.nvmrc) |

Host prerequisites (JDK, Android SDK, Node) and the repo-wide canonical version
matrix live in [`../docs/development-environment.md`](../docs/development-environment.md).

- `package.json` → `"react-native": "npm:react-native-tvos@0.85-stable"`. The fork
  is a superset of RN 0.85 that also builds plain phone/tablet. `react-native` is
  listed in `expo.install.exclude` so `expo install --fix` / `expo-doctor` do not
  try to "correct" the alias back to vanilla `react-native` (expo-doctor is
  **21/21 green** with this exclusion).
- **`tv/.npmrc` sets `legacy-peer-deps=true`.** Because `react-native` is aliased
  to the prerelease-tagged `react-native-tvos`, npm's strict peer resolver rejects
  it against packages declaring a `react-native` peer range. This is the
  documented Expo-TV approach — **not** `--force` (which would also mask genuine
  conflicts).
- `@react-native-tvos/config-tv` (dev dependency) in `app.config.js` `plugins`,
  with `{ "isTV": true, "androidTVRequired": true, "androidTVBanner": … }`. During
  `expo prebuild` it configures the native Android project for TV:
  `LEANBACK_LAUNCHER` category, `android.software.leanback` **required**, and
  `android.hardware.touchscreen` **not required** — i.e. an Android TV / Fire TV
  app, not a phone app. (Verified in the SDK-56 generated `AndroidManifest.xml`.)
- `expo-status-bar` is registered as a config plugin (SDK 56 ships one; a dynamic
  `app.config.js` cannot be auto-written by `expo install --fix`, so it is added
  explicitly).
- `env.d.ts` declares the minimal `process.env.EXPO_PUBLIC_*` shape. SDK 56's
  `react-jsx` automatic runtime + strict TS 6 mean bare `import React` is unused in
  JSX-only files (removed), and neither `react-native` nor `expo/types` declares
  `process` — `env.d.ts` covers it without pulling full Node globals into the RN
  type space.
- `EXPO_TV=1` forces a TV build regardless of the flag; a clean prebuild is
  required when toggling TV mode (`--clean`).

### Brand assets

The NubArca TV artwork lives in `assets/brand/` (generated reproducibly by
`../scripts/generate-brand-assets.py`; do not hand-edit). Wiring in
`app.config.js`:

| Asset | Config field | Result |
| --- | --- | --- |
| `tv-icon-512.png` | `icon`, `android.icon` | app icon (all platforms / Android fallback) |
| `tv-adaptive-icon-432.png` | `android.adaptiveIcon.foregroundImage` (`backgroundColor` `#0a0f1a`) | Android adaptive launcher icon |
| `tv-banner-320x180.png` | `@react-native-tvos/config-tv` → `androidTVBanner` | `android:banner` in the manifest = the Android TV / Fire TV home-row banner |
| `tv-lockup-1280.png`, `tv-lockup-640.png` | *(not wired)* | the "NubArca TV" lockup, kept for stores/docs; the in-app pairing header is text, not an image |
| `tv-splash-1920x1080.png` | *(not wired)* | see below |

**No splash screen is configured.** Expo SDK 56 removed the top-level `splash`
key from the app-config schema (only `web.splash` for PWAs remains) and moved
splash configuration into the `expo-splash-screen` config plugin, which this app
does not depend on. A `splash` key in `app.config.js` would be silently ignored by
prebuild, so it was left out; the asset is ready if `expo-splash-screen` is added
later.

### JDK for the native build

React Native 0.85 generates a **Gradle 9.3.x** project. Build it with **JDK 17**
(21 also works). **JDK 26 does NOT work** — the foojay toolchain resolver fails at
configuration with `NoSuchFieldError: JvmVendorSpec ... IBM_SEMERU`. On a build
host:

```bash
export JAVA_HOME=/usr/lib/jvm/java-17-openjdk   # adjust to your distro's path
export PATH="$JAVA_HOME/bin:$PATH"
java -version   # confirm 17
```

Native project directories (`android/`, `ios/`) are **generated** by prebuild
(Continuous Native Generation) and are git-ignored — never committed.

## What it does

- **Pairing screen** — `POST /api/tv/pairing/start`, renders a **real QR code**
  (pure-JS `qrcode-generator`, no native module) of the phone approval URL, plus
  the short code; polls `GET /api/tv/pairing/{code}/status` (secret in the
  `X-Tv-Pairing-Secret` header) until the phone approves.
- **Paired bootstrap** — on launch, the persisted limited TV session cookie (if
  any) is rehydrated from AsyncStorage and **validated** with `GET /api/tv/session`
  before it is trusted; a live session skips straight to browsing, a
  revoked/expired/absent one clears the stored cookie and shows pairing.
- **Albums** — `GET /api/tv/albums` shows only the owner's ShowOnTv albums;
  refreshes every 20s so a disabled album disappears without a restart.
- **Personal videos** — the PIN/grant-gated Personal Area exposes a separate
  newest-first video library, independent of `ShowOnTv` albums. Its adaptive
  16:9 grid keeps portrait and landscape sources readable using the cinematic
  blurred-fill poster; focusing a tile lazily cycles the six-frame preview strip.
  SELECT opens native `expo-video` playback; LEFT/RIGHT seek ±10 seconds and
  UP/DOWN move between videos. A recorded strip failure is not regenerated on
  every focus.
- **Items (album grid)** — `GET /api/tv/albums/{id}/items`; a 404 (album
  disabled meanwhile) routes back to the album list. The grid is a **dense,
  image-only** surface that **starts near the top of the screen**: adaptive
  columns from the window dimensions (~**6 columns** on a 1080p-class 960dp
  layout, ~**5** on a 720dp layout, ~3.5–4 rows visible), square
  image-dominant tiles, **no filename or metadata text** under tiles (the
  focused tile shows a small **"7 / 42" position badge** instead; the name stays
  as the accessibility label). By default there is **no chrome at all** — no
  header, no QR, no menu; **SELECT opens the focused photo**. **Live party
  refresh:** when the open album is in **PartyMode**, the item list is polled
  every **15s** so guest uploads appear on the TV without leaving/reopening the
  album. The server returns a stable list ordered ascending by `AddedAt`, so new
  uploads **append to the end**; the poll skips the state update when nothing
  changed. A 404 (party/ShowOnTv revoked) drops back to the album list; a 401
  returns to pairing.
- **MENU overlay (grid + slideshow — the one interaction model)** — the remote's
  **MENU button is the ONLY command that shows/hides menus and QR overlays**, in
  both the album grid and the slideshow. SELECT never toggles an overlay: in the
  grid it opens the focused photo; in the slideshow it is **reserved/unused**.
  The overlay **auto-hides after ~6s** of inactivity (any remote/focus activity
  re-arms it) and hardware **BACK hides it first**, then navigates up. Layout
  (inside the ~3.5% overscan safe area, `overscan()` in `src/theme.ts`):
  - **grid:** party QR **top-left**, **album title top-center**, party upload QR
    **top-right**, compact command bar (**← Albums / ▶ Slideshow**) at the
    bottom. **Explicit overlay focus mode** — no spatial hunting: opening the
    overlay moves focus straight to the **first bar command** (mount-time
    `hasTVPreferredFocus`); LEFT/RIGHT move between commands, SELECT activates
    one, UP returns into the grid. **Closing the overlay (MENU/BACK/auto-hide)
    restores focus to the previously focused tile** — the tile's
    `hasTVPreferredFocus` flips false→true, which natively calls
    `requestFocus()` on the mounted view (verified in
    `ReactViewManager.setTVPreferredFocus`, which acts on every change to
    `true`; the fork's focus-recovery explicitly yields to a
    `hasTVPreferredFocus` view appearing in the same frame). The overlay is
    absolute-positioned — it never reflows or shrinks the grid.
  - **slideshow:** purely **informational** — party QR **top-left** / upload QR
    **top-right** (a single QR stays consistently top-left) and a small
    centered bottom pill showing ONLY the **"current / total" counter**
    (e.g. `7 / 42`, 26dp bold on a dark pill). **No buttons, no filenames, no
    status text** — nothing that can clip on 720p/1080p. There are no focusable
    overlay controls, so **LEFT/RIGHT keep changing the photo even while the
    overlay is visible** (the counter updates live); the **play/pause media
    key** toggles auto-advance.
  - **QR sizing is responsive** (`overlayQrSize()`): ~13% of window height ≈
    70dp → **~140 physical px on 1080p**, proportionally smaller on 720p; QRs
    are non-focusable and never resize/push the photo or grid.
  - **MENU event (verified in the shipped native source, not guessed):**
    `ReactRootView.dispatchKeyEvent` → `modules.core.ReactAndroidHWInputDeviceHelper`
    maps `KeyEvent.KEYCODE_MENU` → the `'menu'` eventType in `KEY_EVENTS_ACTIONS`
    (react-native-tvos 0.85), delivered to JS via `useTVEventHandler`
    (`onHWKeyEvent`, unfiltered). The Fire TV remote's ≡ button sends
    KEYCODE_MENU (82) per Amazon's documented remote mapping. MENU is dispatched
    on key UP only and has no long-press variant — exactly one event per press.
    On-device confirmation: flip `TV_DEBUG_MEDIA` locally and watch the
    sanitized `remote <eventType>` lines in `adb logcat`.
- **Slideshow / viewer** — from the grid overlay, **▶ Slideshow** starts an
  auto-advancing show (default 9s/item) that loops at the end; SELECT on a tile
  opens it paused. **Remote-first (D-pad):** with the overlay hidden (the
  default — **only the photo**, no controls/QR/hint), **LEFT = previous**,
  **RIGHT = next**, **MENU = show the overlay**, **play/pause media key =
  toggle auto-advance**, **SELECT/UP/DOWN = nothing** (SELECT reserved);
  hardware **Back** exits to the grid. D-pad is handled by a global
  `useTVEventHandler` (plus an always-mounted transparent full-screen focus
  anchor so the screen reliably owns the remote — the overlay has no focusable
  controls). Manual Prev/Next re-arms the auto-advance timer, and the
  next/previous preview is prefetched at low priority (only ±1 — never the
  whole album).
  **Fire TV event semantics:** Android dispatches D-pad/select events on **key UP
  (`eventKeyAction === 1`) only** (key-down exists only behind an RN feature
  flag — see `ReactAndroidHWInputDeviceHelper`), so the handler acts on anything
  that is not an explicit key-down; long presses (`longLeft`/`longRight`) map to
  the same actions. Filtering out key-up events silently kills the remote.
  - **Cinematic rendering** (`SlideImage`): the MEDIUM **preview** (poster for
    video) is drawn in two layers — a **blurred, dimmed `cover` background** of the
    same image as ambient fill, and an **aspect-preserving `contain` foreground**.
    Vertical photos fit fully with blurred side-fill; wide photos fill as much as
    possible with a blurred letterbox. No hardcoded dimensions, no upscaled
    thumbnail, **never** original full-resolution bytes. Both layers share the
    ONE downloaded local file (no independent downloads).
    **Smooth transitions (two-slot stage):** the current slide stays fully
    visible while the next one decodes offscreen; the swap happens the moment
    the incoming **foreground** has decoded, so photo changes never flash
    "Caricamento…" over an already-visible photo (the loading state appears
    only before the FIRST image or after a failure). Slides are keyed by uri so
    the promotion moves the decoded element between slots without remounting
    (no re-decode). **The foreground is never blocked by the blurred
    background**: within a slide the background starts transparent and fades in
    (~250ms, native driver) when its slower blur decode completes — the
    side-fill effect is preserved, it just arrives a beat after the photo. A
    failed preview shows the centered "Anteprima non disponibile" placeholder
    (and clears the stale slide) while LEFT/RIGHT navigation keeps working.
  - **Live party refresh:** a PartyMode slideshow polls items every 15s and
    **merges** new uploads mid-playback — the current item is tracked **by id** so
    playback is not reset. If the current item is removed, the show clamps to a
    safe index; an emptied/revoked album exits.
- **Focus / D-pad** — every focusable element uses one of two shared components:
  `FocusableTile` (album cards, grid tiles) and `FocusableButton` (screen headers,
  pairing retry, overlay controls). Both draw a **high-contrast, non-color-only**
  focus state built exclusively from properties that reliably render on Android
  TV: **scale-up**, a **thick WHITE outer border + inner ACCENT ring** (a real
  double ring from nested view borders — no iOS-only `shadow*`, no unverified
  `outline*`), a brighter background, and (buttons) a **▸ caret + bolder label
  at 20dp** — obvious from across the room, not just a blue shade. The focused
  grid tile additionally shows the **"position / total" badge**. The first tile
  requests `hasTVPreferredFocus`; the hardware **Back** button closes a visible
  MENU overlay first, then steps **one level at a time** — viewer → album grid →
  album list (each screen owns its own `BackHandler`).

## Security posture

- Uses **only** `/api/tv/*` endpoints (enforced by an `assertTvPath` guard in
  `src/api/client.ts`). No normal owner APIs, no token auth, no full user
  session.
- The limited TV **session cookie** (`NubArca.TvSession` — a *retained legacy
  identifier*, see the table above; the cookie name is a backend wire contract and
  was not rebranded to NubArca) is captured from
  `Set-Cookie` and re-sent via the `Cookie` header (RN has no cookie jar). It is
  **persisted across app restarts** via AsyncStorage (see *Session persistence*
  below) and rehydrated + re-validated on launch. Only that one limited cookie is
  persisted — never the owner `NubArca.Auth` cookie (also a retained legacy
  identifier; never received here), the
  pairing secret (travels in a header, not a cookie), or party tokens (in URLs,
  not cookies).
- DTOs/URLs carry only logical ids, names, counts, and `/api/tv/…` media URLs —
  no storage/blob/SHA/path/GPS/DateTaken/AI/face/vector/score. No face/person
  names. No original full-res. No party/public behavior. The QR encodes only the
  public pairing approval URL (secret lives in the URL fragment, as designed —
  same as the web `/tv` QR); no server-side token hashes are exposed.

## Derived-media loading (thumbnails / previews / posters)

The backend TV item DTOs return **relative** derived-media paths
(`/api/tv/media/{id}/thumbnail|preview|poster`). Grid tiles use `thumbnail`,
the slideshow/viewer uses `preview` (images) or `poster` (videos) — **never** the
original full-resolution bytes.

The RN `<Image>` loader does not forward a custom `Cookie` header reliably, so
media is loaded through a single central loader (`loadTvMedia` in
`src/api/client.ts`, rendered by `AuthedImage`):

1. **Resolve + validate** the path (`resolveTvMediaUrl`): a relative `/api/tv/…`
   path is joined to the configured `apiBaseUrl`; an absolute URL is accepted only
   if it stays on that origin **and** under `/api/tv/`. Anything else is refused
   (mirrors `assertTvPath`).
2. **Download with the TV session cookie** to the app-private cache directory via
   `expo-file-system`'s native `File.downloadFileAsync` (which reliably carries
   request headers and streams to disk, and **rejects on any non-2xx** — so
   401/403/404/5xx never render blank). The bytes are verified non-zero.
3. **Render a local `file://` URI** in `<Image>`, which decodes deterministically
   on Fire TV. Concurrent requests for the same media share one download; the
   cache is bounded (LRU-ish, 200 entries) and is **purged when the TV session is
   cleared/revoked/expired** (`clearTvMediaCache` is called from `clearSession`).

**Download scheduling (grid performance):** the album grid is a **virtualized
`FlatList`** — only tiles near the viewport mount, so opening a large album no
longer fires one download per item up front. The loader runs at most
`MEDIA_MAX_CONCURRENT` (3) native downloads through a **two-level priority
semaphore**: `high` (what the user is looking at — visible thumbnails, the current
slideshow preview) always beats `low` (prev/next prefetch, fallbacks). Each
request supports **abort** (`shouldAbort`) so tiles that scroll away / a left
album drop their queued downloads instead of wasting slots. **Recent failures are
memoized** (~45 s, per exact URL) so recycled tiles don't retry a failing download
in a loop — and since the key includes the variant, a failed thumbnail never
poisons the same item's preview. The grid's **thumbnail→preview fallback is
conservative**: it starts only after the thumbnail genuinely failed, after a short
defer, at low priority, and **strictly one fallback at a time** — still
`/api/tv/media`, never an original. Placeholders appear only where media is
genuinely unavailable.

**Diagnostics:** `src/debug.ts` has a default-OFF `TV_DEBUG_MEDIA` flag; when
flipped locally it traces media loads (variant, opaque cache key, queue-wait /
download ms, byte count, hit/miss, failure class) and remote events via
`adb logcat` — never cookies, tokens, ids, or URLs. Never commit it enabled.

> Why not a data URI? The previous approach (fetch → `Blob` →
> `FileReader.readAsDataURL`) produced no usable URI on the Fire TV / Hermes
> runtime, so images rendered blank even on HTTP 200 — the root cause this change
> fixes. (QR codes kept rendering because they are pure-JS `data:image/gif` URIs
> that never touch `Blob`/`FileReader`.) The download-to-cache path is
> deterministic and header-authenticated.

Failure UX is explicit and localized: a centered spinner + "Caricamento…" /
"Loading…" while loading, and a centered "Anteprima non disponibile" / "Preview
unavailable" on any failure. Placeholders fill the same box as the image, so tiles
and the slideshow stage never collapse. No infinite retry; session revocation is
handled by the existing JSON polls (which `clearSession` → clears the media cache).

## Run — normal dev (phone/tablet form factor, Expo Go)

```bash
cd tv
npm install
npm run lint        # tsc --noEmit
npm run config      # expo config --type introspect
npm start           # Expo dev server (phone form factor)
```

> Note: Expo Go does **not** support TV. Use Expo Go only for quick phone-shaped
> smoke tests; real TV runs require a native build (below).

## API base URL configuration (dev + Fire Stick test builds)

The API base URL is resolved (in `App.tsx` `resolveBaseUrl()` +
`app.config.js`), in order. The `*_NUBARCA_*` variable names are *retained
legacy identifiers* — they kept their pre-NubArca spelling because operators
already have them set in production environments and CI:

1. `EXPO_PUBLIC_NUBARCA_API_BASE_URL` — preferred; an `EXPO_PUBLIC_*` var is
   inlined by Expo at build time and read at runtime, so a Fire Stick test build
   can target production **without editing source**.
2. `NUBARCA_TV_API_BASE_URL` — build-time alias (config only).
3. `expo.extra.apiBaseUrl` from `app.config.js` — a loopback dev default
   (`http://localhost:5177`). A physical Fire Stick cannot reach the
   workstation's loopback address, so device testing sets variable 1 or 2 to the
   workstation's LAN address. That address is yours, not the product's, so it is
   never committed.

There are **no secrets** in config — only a base URL. `app.config.js` also
derives `usesCleartextTraffic`: cleartext (unencrypted `http`) is enabled **only**
when the resolved base URL is `http://` (LAN dev). An `https://` production base
URL builds with cleartext **disabled** — production never requires cleartext.

Point a Fire Stick debug build at production:

```bash
cd tv
EXPO_PUBLIC_NUBARCA_API_BASE_URL=https://nubarca.example.com npm run tv:prebuild
cd android && ./gradlew assembleDebug
# → android/app/build/outputs/apk/debug/app-debug.apk  (cleartext disabled)
```

For LAN dev against a local API, just run `npm run tv:prebuild` (uses the http
dev default, cleartext enabled).

## Session persistence

The limited TV session cookie is persisted with
`@react-native-async-storage/async-storage` under the key
`nubarca.tv.session.cookie` (`src/api/client.ts`). This is the key already
written by the 1.0.0 NubArca TV package; it remains byte-identical so the 1.0.1
in-place upgrade does not sign the device out:

- **Only** the `NubArca.TvSession` cookie string is stored — by construction
  `_cookieJar` can hold nothing else (owner auth is never received on `/api/tv`,
  the pairing secret is header-only, party tokens are in URLs).
- On **launch** the cookie is rehydrated (`restoreSession()`) then **validated**
  against `GET /api/tv/session`; an invalid/revoked/expired session is cleared and
  pairing is shown.
- On **revoke/expire** (any `/api/tv` `401` → `onSessionInvalid`) and on explicit
  clear, the stored cookie is removed so it does not survive a restart.
- **Limitation:** AsyncStorage is device-local, app-private, and **not
  encrypted**. This is acceptable for a limited, server-revocable, expiring TV
  session cookie (not owner credentials). `expo-secure-store` was not used because
  its Android TV support is uncertain; encrypted TV storage is a possible
  follow-up.

## Run — TV mode (Android TV / Fire Stick)

```bash
cd tv
npm run tv:prebuild            # EXPO_TV=1 expo prebuild --platform android --clean
npm run tv:android            # EXPO_TV=1 expo run:android  (device/emulator + Android SDK)
```

`tv:prebuild` generates `android/` with the leanback TV manifest; `tv:android`
builds and installs the debug app onto a connected Android TV / Fire TV device
or emulator.

## Parity with the browser `/tv` baseline

The browser `/tv` surface (`frontend/src/pages/TvBrowser.tsx`) is the functional
baseline and stays the fallback/debug target. The native app matches its features:

| Feature | browser `/tv` | native `tv/` |
| --- | --- | --- |
| Pairing (QR + code, poll to approval) | yes | yes |
| Albums (ShowOnTv only, 20s refresh) | yes | yes |
| Album grid (thumbnails, no originals) | yes | yes |
| Slideshow (auto-advance/loop, play/pause/prev/next) | yes | yes |
| Party view/download QR | yes | yes |
| Party upload QR (when enabled) | yes | yes |
| Live party refresh (15s, item kept by id) | yes | yes |
| Face-search filtered slideshow | full-screen supersede | filtered grid + banner¹ |
| Revoked/expired session → pairing | yes | yes |
| Localization (owner language, IT default) | yes | yes |
| Video | HTML `<video>` playback | native `expo-video` (HLS/direct) |
| Back: viewer → grid → albums | yes | yes |
| Session persistence across restart | browser cookie jar | AsyncStorage³ |

¹ The native face-search switches the grid/slideshow to the matching subset with a
banner + "Show all photos" reset; an automatic full-screen viewer takeover (as the
browser does) is a documented follow-up. ³ See *Session persistence*.

## What was validated here (overlay focus + transition fixes)

On Node v22.22.3: `npm run lint` (tsc 6.0.3) clean; `npx expo-doctor` **21/21**;
`expo config --type introspect` default + `EXPO_TV=1` + the prod URL override
(apiBaseUrl → prod, cleartext **false**); `npx expo export --platform android`
OK. Safety audit clean. **No release APK was built in this slice.**

Fixes to verify on device (regressions reported from hardware):
- grid MENU overlay: focus lands on the first bottom command immediately;
  LEFT/RIGHT between commands; closing restores focus to the previous tile;
- slideshow overlay: only the "7 / 42" counter pill at the bottom — no clipped
  text on the right;
- slideshow transitions: previous photo stays until the next foreground is
  decoded (no "Caricamento…" flash between photos); blurred side-fill still
  present (fades in just after the photo).

## What was validated here (MENU-overlay + grid density polish)

On Node v22.22.3: `npm run lint` (tsc 6.0.3) clean; `npx expo-doctor` **21/21**;
`expo config --type introspect` default + `EXPO_TV=1` + the prod URL override
(apiBaseUrl → prod, cleartext **false**); `npx expo export --platform android`
OK. Safety audit clean (`/api/tv` only; the only `console.*` is the default-off
sanitized `tvDebug`). **No release APK was built in this slice** — the developer
builds and sideloads locally (see *Build the APK*).

What changed (verify on device):
- **MENU** (≡, KEYCODE_MENU → `'menu'`) toggles the overlay in the grid **and**
  the slideshow; **SELECT no longer opens any overlay** (grid: opens the focused
  photo; slideshow: reserved no-op);
- grid: dense image-only tiles (~6 cols on 1080p-class), starts near the top,
  no filenames, focused tile shows "7 / 42";
- grid overlay: QR top-left, album title top-center, upload QR top-right,
  compact ← Albums / ▶ Slideshow bar at the bottom, all inside overscan;
- slideshow overlay: smaller corner QRs, bold "current / total" counter,
  compact bottom bar inside overscan (nothing off-screen on 1080p/720p);
- slideshow default state is the bare photo (no hint chrome); blurred background
  and foreground appear **together**.

## What was validated here (runtime performance + remote controls)

On Node v22.22.3: `npm run lint` (tsc 6.0.3) clean; `npx expo-doctor` **21/21**;
`expo config` default + `EXPO_TV` + prod override (cleartext false); `expo export
--platform android` OK. Safety audit clean (`/api/tv` only; the single `console.*`
is the default-off sanitized `tvDebug` helper). **No release APK was built in this
slice** — the developer builds and sideloads locally (see *Build the APK*).

Key fixes to verify on device:
- opening an album renders the grid immediately and thumbnails fill in
  progressively (virtualized FlatList + priority download pool);
- **D-pad LEFT/RIGHT change the photo with the overlay hidden** — root cause was
  the handler ignoring key-UP events, the only events Fire TV dispatches;
- overlay/QR hidden by default, SELECT shows them, ~6 s auto-hide;
- focus reads clearly on tiles, headers, pairing retry, and overlay controls.

## What was validated here (slideshow + grid polish)

On Node v22.22.3, JDK 17, Android SDK (android-36 / build-tools 36 / NDK 27):

- `npm run lint` (tsc 6.0.3) clean; `npx expo-doctor` **21/21**; `expo config`
  default + `EXPO_TV` + prod override (cleartext false); `expo export` (Metro
  bundled OK); **`./gradlew assembleRelease` → BUILD SUCCESSFUL**, real
  `app-release.apk` (~65 MB) built against the prod URL. Safety audit clean
  (`/api/tv` only — JSON via `assertTvPath`, media via `resolveTvMediaUrl`; no
  console/secret logging; no originals). No new dependency (reused
  `expo-file-system`, and core RN `Image.blurRadius` + `useTVEventHandler`).
- **Not** validated on-**device** (no Fire Stick connected): the grid
  progressive-load, the D-pad left/right + SELECT overlay + auto-hide, the blurred
  side-fill, and focus visibility must be confirmed on hardware (checklist below).
  The `useTVEventHandler` + focus-anchor pattern is the documented react-native-tvos
  approach but the actual remote behavior is unverified here.

## What was validated here (derived-media rendering fix)

On Node v22.22.3, **JDK 17**, Android SDK (platform android-36, build-tools 36,
NDK 27) — all green:

- `npm run lint` (`tsc --noEmit`, TS 6.0.3) clean; `npx expo-doctor` **21/21**.
- `expo config --type introspect` default + `EXPO_TV=1` + the
  `EXPO_PUBLIC_NUBARCA_API_BASE_URL=https://…` prod override (apiBaseUrl → prod,
  `usesCleartextTraffic` → **false**).
- `npx expo export --platform android` (Metro bundled **613 modules**, incl.
  `expo-file-system`).
- `EXPO_TV=1 expo prebuild --platform android --clean` then **`./gradlew
  assembleRelease` → BUILD SUCCESSFUL**, producing a real sideloadable
  `android/app/build/outputs/apk/release/app-release.apk` (~65 MB) whose
  `assets/index.android.bundle` contains the new media loader and whose native
  libs include the `expo-file-system` download module. Built against
  `https://nubarca.example.com` (cleartext disabled).
- Safety audit clean: only `/api/tv/*` (JSON via `assertTvPath`; media via
  `resolveTvMediaUrl`, same-origin `/api/tv/` only), no console/secret logging, no
  originals.
- **Not** validated: on-**device** Fire Stick runtime (no device connected) — the
  APK is built and ready to sideload. The download-to-cache media path is designed
  to be deterministic on Fire TV but the actual pixels-on-screen result must be
  confirmed on hardware (device checklist below).

## What was validated here (Expo SDK 56 upgrade)

On Node v22.22.3, all green:

- `npm install` (clean, `.npmrc` legacy-peer-deps) → **expo 56.0.15, react 19.2.3,
  react-native-tvos 0.85.3-3, async-storage 2.2.0, config-tv 0.1.6**.
- `npx expo-doctor` → **21/21 checks pass** (react-native alias excluded from the
  version check via `expo.install.exclude`).
- `npm run lint` (`tsc --noEmit`, **TypeScript 6.0.3**) clean.
- `expo config --type introspect` default **and** `EXPO_TV=1`: `sdkVersion 56.0.0`,
  `isTV`, `android.software.leanback`, and the `expo-status-bar` plugin present;
  the `EXPO_PUBLIC_NUBARCA_API_BASE_URL=https://…` override flips `apiBaseUrl` to
  production and `usesCleartextTraffic` to **`false`**.
- `npx expo export --platform android` (Metro bundled **595 modules**) succeeds.
- `EXPO_TV=1 expo prebuild --platform android --clean` **succeeds** and generates a
  correct TV `AndroidManifest.xml` (leanback **required**, touchscreen **not**
  required, `LEANBACK_LAUNCHER`); `package.json` untouched (alias exclusion works).
- Safety audit clean: only `/api/tv/*` calls (both `fetch`es go through the
  `assertTvPath` guard), no console/secret logging. The API client + all screen
  logic are unchanged — the only source edits are removing now-unused `import React`
  defaults (JSX-only files under the `react-jsx` runtime).
- **Not** validated here: the **Gradle APK build** and any Fire Stick / Android TV
  **device/emulator** runtime. Two independent environment blockers on this host,
  neither in the app/deps: (1) **JDK 26 is too new** — `./gradlew assembleDebug`
  fails at configuration with `NoSuchFieldError: JvmVendorSpec ... IBM_SEMERU` from
  the foojay toolchain resolver (needs JDK 17); (2) **no Android SDK**
  (`ANDROID_HOME` unset, no `~/Android/Sdk`). Build on a proper host (below).

## Build the APK (needs JDK 17 + an Android SDK)

```bash
cd tv
npm install                              # uses tv/.npmrc (legacy-peer-deps)
export JAVA_HOME=/usr/lib/jvm/java-17-openjdk   # JDK 17 — NOT 26 (foojay resolver fails)
export ANDROID_HOME="$HOME/Android/Sdk"; export PATH="$JAVA_HOME/bin:$PATH"
# production target (cleartext disabled); omit the env var for the LAN dev default:
EXPO_PUBLIC_NUBARCA_API_BASE_URL=https://nubarca.example.com npm run tv:prebuild
cd android
./gradlew assembleRelease   # → app/build/outputs/apk/release/app-release.apk  (built here ✔)
# or a debug variant:
./gradlew assembleDebug     # → app/build/outputs/apk/debug/app-debug.apk
```

`assembleRelease` succeeds locally against platform android-36 / build-tools 36 /
NDK 27 and produces a ~72 MB sideloadable APK.

Release builds require the NubArca TV release key
(`NUBARCA_TV_RELEASE_STORE_FILE` and friends, as Gradle properties or
environment variables). `plugins/withReleaseSigning.js` wires it in on every
prebuild and **fails the build** when it is missing, rather than falling back to
the React Native template's public debug keystore. See
[`../docs/tv-apk-distribution.md`](../docs/tv-apk-distribution.md#signing).

Sideload to a Fire Stick (only with a device IP; enable ADB debugging on the
device first):

```bash
adb connect <FIRE_STICK_IP>:5555
adb install android/app/build/outputs/apk/release/app-release.apk
```

Use plain `adb install`, not `adb install -r`, when replacing an app whose
package name differs — uninstall the old package first.

Or via EAS Build with a TV profile (`EXPO_TV=1`) for reproducible cloud builds.

## Device checklist (run on a real Fire Stick)

- install + launch APK; pairing QR/code visible and readable at TV distance
- approve pairing from phone/browser → album list appears
- **album covers + grid thumbnails render real photos, progressively** (not a long
  blank load then all placeholders); a brief "Caricamento…" spinner may flash first
- **relaunch the app → session persists** (goes straight to albums, no re-pair)
- **grid opens fast, starts near the top, and is dense** (~6 columns on 1080p,
  ~3.5-4 rows visible), **no filenames under tiles**, default view has **no
  menu/QR chrome**
- **focus is unmistakable without relying on color** — focused tiles/controls
  scale up + thick white border + accent inner ring + brighter background (tiles
  add a "7 / 42" badge, controls a ▸ caret + bolder label); initial focus sensible
- **SELECT on a focused tile opens the photo** (and does NOT toggle any overlay)
- **MENU in the grid shows the overlay**: QR top-left, album title top-center,
  upload QR top-right, ← Albums / ▶ Slideshow bar at the bottom — all fully
  on-screen; **focus lands directly on the first bar command**, LEFT/RIGHT move
  between commands, SELECT activates, UP returns into the grid;
  **auto-hides after ~6s**; MENU/BACK hides it and **focus returns to the tile
  the user was on**; grid density unchanged underneath
- open album → MENU → start slideshow → **the preview photo renders sharply,
  aspect preserved**; a **vertical photo fits with blurred side-fill**, a wide
  photo fills with a blurred letterbox (the blur may fade in a beat after the
  photo — that is by design)
- **photo changes are smooth**: LEFT/RIGHT (and auto-advance) keep the current
  photo visible until the next one is decoded — **no "Caricamento…" flash
  between photos** (loading appears only before the very first image); a broken
  item shows the centered unavailable placeholder and navigation keeps working
- slideshow starts with **no controls/QR** (bare photo); **D-pad LEFT =
  previous, RIGHT = next**; **MENU shows the overlay** — corner QRs + ONLY a
  small centered **"7 / 42" counter pill** (no buttons/labels, nothing clipped),
  which **auto-hides after ~6s**; LEFT/RIGHT still change the photo while it is
  visible; **SELECT does nothing**; the **play/pause media key** toggles
  auto-advance; **Back hides the overlay first, then exits** to the grid;
  nothing renders off-screen on 1080p or 720p
- a broken/oversized item shows a centered "Anteprima non disponibile" (not a
  blank tile), and does not break the rest of the grid/slideshow
- Party view/download QR + upload QR (when enabled) appear **only in the MENU
  overlay** (top-left / top-right, small); upload from phone appears within ~15s
  without resetting the current slide
- face-search filtered slideshow (if a guest runs a search) + "Show all photos"
  (banner in the grid; also available in the grid MENU bar)
- revoke the TV session → app returns to pairing; disable ShowOnTv/PartyMode →
  album/features drop safely on next poll
- video shows its poster placeholder (native playback is a follow-up)

## Remaining follow-ups (unvalidated / out of scope)

- NubArca TV 1.0.1 still requires its definitive merged-main APK build and the
  physical Fire Stick in-place/OTA cold-launch gate before public replacement.
- Automatic full-screen viewer takeover on an active party face-search (native
  currently filters the grid + banner; browser supersedes full-screen).
- Exact-tile focus restore when returning from the slideshow (currently re-lands
  on the grid's first tile).
- The default grid/slideshow states are deliberately chrome-free, so MENU is not
  discoverable on screen; a one-time first-run "press MENU for controls" hint is
  a possible follow-up (product rule: MENU is the only overlay command).
- If some remote/firmware ever delivers the menu key differently, the documented
  fallback is a **long-press SELECT** (`longSelect`) — NOT implemented, since
  KEYCODE_MENU → `'menu'` is verified in the native source; confirm on hardware
  with `TV_DEBUG_MEDIA`.
- Optional `TVFocusGuideView` for explicit focus routing on complex grids and
  encrypted TV session storage. (The Fire TV / Android TV banner
  asset is now shipped — see *Brand assets*; it has not yet been verified on
  hardware.)
- Splash screen: needs the `expo-splash-screen` dependency before
  `tv-splash-1920x1080.png` can be wired (see *Brand assets*).

Party mode (public read-only view), QR upload/download, live party refresh, and
face-search filtered slideshow are implemented. Out of scope (future TV-only
slices): moderation/approval of guest uploads and offline sync.
