# Spike — can a WebView host the canonical Party Game stage on Fire TV?

**This branch is a technical probe, not a solution.** Nothing here is the Party
Game implementation, nothing here is meant to be merged, and the spike screen is
expected to be deleted once the question is answered.

The question is narrow and it is the only one that matters for the Slice 3
architecture decision:

> Is a WebView reliable enough, on the Fire TV hardware NubArca actually ships
> to, to be the product's television renderer for Party Game?

Everything else about strategy A2 (native shell + canonical web renderer) is
already known to be sound on paper. This is the part that cannot be reasoned
about — it has to be measured on the panel.

## What is already established without hardware

- `react-native-webview@13.16.1` is the Expo SDK 56 pinned version and **compiles
  cleanly against `react-native-tvos@0.85-stable`** — deprecation warnings only.
  The native-contract question is answered: it builds.
- `minSdk` is 24 (Android 7 / Fire OS 6+), so every supported device has a
  Chromium-based system WebView rather than the old Android 4 one.
- The spike takes **no party capability token** and exercises **no guest cookie**.

## Two constraints this spike is built around

`docs/current-work.md` records that **this operator has no ADB access and cannot
get it** — "the evidence has to come from what the screen shows". That rules out
`dumpsys meminfo` for memory and `am force-stop` for the kill test, so both are
reachable from the remote instead (the *stress* probe below). Every check in the
protocol is screen-readable.

And the spike APK is **debug-signed**, so Android will not install it over the
release-signed production app. It therefore builds with
`applicationIdSuffix ".spike"`: it lands beside NubArca TV as a separate
application and **does not disturb a paired television**. It also bundles its JS
(`debuggableVariants = []`), so it needs no Metro server.

Both are build-time settings on the generated, gitignored `android/` project —
`tv/release-contract.json` is untouched and the identity contract stays green.

## A third constraint, found while building

A JS-bundled TV APK is bundled in PRODUCTION mode whatever the Gradle variant,
because `expo export:embed` sets `NODE_ENV=production` itself. `app.config.js` is
then fail-closed and demands the operator's own build inputs —
`NUBARCA_PUBLIC_ORIGIN` (https, no path) and `NUBARCA_TV_OTA_CERTIFICATE`. That
is correct and deliberate: it is the rule that stops a perfectly signed APK from
shipping unable to reach any server. It also means **a spike APK cannot be built
by anyone who does not hold those inputs**.

Two ways out, answering different parts of the protocol:

- **`NODE_ENV=development`** — needs no operator input at all. The offline
  *replica*, *stress* and *unreachable* probes work fully, which covers checks
  1-5, 7, 8, 11, 12 and 14 — including both checks that decide the architecture.
  Only *Live origin* is meaningless, because the base URL falls back to the dev
  default.
- **The operator's own inputs** — the full protocol, *Live origin* included.

## Building and installing

```bash
cd tv
npx expo prebuild --platform android --clean

# standalone + side-by-side, applied to the generated project only
python3 - <<'EOF'
import pathlib
p = pathlib.Path('android/app/build.gradle'); s = p.read_text()
s = s.replace('react {', 'react {\n    debuggableVariants = []', 1)
s = s.replace('        debug {\n            signingConfig signingConfigs.debug',
              '        debug {\n            applicationIdSuffix ".spike"\n'
              '            signingConfig signingConfigs.debug', 1)
p.write_text(s)
EOF

# offline probes only — no operator inputs required
(cd android && NODE_ENV=development ./gradlew :app:assembleDebug \
   -PreactNativeArchitectures=armeabi-v7a,arm64-v8a)

# …or the full protocol, with the operator's own build inputs:
# (cd android && NUBARCA_PUBLIC_ORIGIN=https://<origin> \
#    NUBARCA_TV_OTA_CERTIFICATE=<path> ./gradlew :app:assembleDebug \
#    -PreactNativeArchitectures=armeabi-v7a,arm64-v8a)
```

The APK lands at `android/app/build/outputs/apk/debug/app-debug.apk`. Put it
somewhere the Fire TV can fetch (the same way the product APK is served) and
install it with the usual on-device downloader — no ADB needed.

Then: launch **NubArca TV (spike)** → pair → mode selector → **WebView spike**.

Four probes, chosen on the spike's own menu:

| Probe | What it isolates |
| --- | --- |
| **Replica stage (offline soak)** | Pure WebView runtime: full-bleed layout, `vw` typography, a decoded raster, a compositor animation, a 2.5 s scene loop matching the stage's poll cadence. No server, no TLS, no token — so a failure here is the WebView, not the network. |
| **Live origin** | The real deployed web origin over the real network: the actual CSS/font/bundle pipeline on the actual panel. |
| **Kill the renderer (memory stress)** | Allocates until the renderer process dies. The finding is not the crash — it is whether the NATIVE shell survives it, reports it, and still answers BACK. |
| **Unreachable host** | A name that cannot resolve: the error path and recovery without an app restart. |

### Reading the HUD

The HUD is **native**, drawn outside the WebView, because a HUD inside the thing
being measured disappears exactly when the evidence matters.

```
replica · loaded · loads 1 · shell up 412s
page up 410s · scenes 164 · frames 24730 · heap 38MB
last beat 2s ago
```

`last beat` is the reading that matters. The page posts a heartbeat every 2 s; if
that number climbs past 10 s the HUD says **← WEDGED**. A white screen, an
OS-killed renderer and a frozen JS context all announce themselves there and
nowhere else.

## The protocol

Record device, Fire OS version, screen mode and RAM before starting.

| # | Check | How | Pass |
| --- | --- | --- | --- |
| 1 | Startup / teardown | Enter and leave the spike 10× | No leak in `shell up`, no crash, returns to native shell every time |
| 2 | Stage rendering | Replica probe | All five scenes legible from 3 m; no clipped text |
| 3 | Full-screen | Both probes | No letterbox, no system chrome, no overscan cut |
| 4 | D-pad / focus | Press every direction, repeatedly | Nothing inside the page takes focus; BACK always exits first press |
| 5 | Images / media | Replica probe | The raster decodes and paints every scene |
| 6 | Auth boundary | — | Deferred: the spike deliberately carries no credential. Verified by design review, not here |
| 7 | Background / resume | Home, wait 60 s, return | Page resumes; `last beat` recovers within a few seconds |
| 8 | App restart | Kill the app, relaunch, re-enter | Clean start, no stale renderer |
| 9 | Network loss | Live origin probe, pull the Wi-Fi, restore | Error surfaces, then recovers on reload without app restart |
| 10 | Assignment change | Not exercised by the spike | Deferred to implementation |
| 11 | Memory | Replica probe: read `heap` on the HUD at 0/15/60 min | Heap flat, not climbing. Renderer-gone under normal load = memory pressure |
| 12 | Prolonged stability | **Replica probe, 2 h minimum, screen on** | `last beat` never exceeds 10 s; `scenes` keeps incrementing; no renderer-gone entry |
| 13 | Real Fire TV | Every check above on real hardware | Emulator results do not count |
| 14 | Failure fallback | **Kill the renderer (memory stress)** probe, and **Unreachable host** | HUD reports `renderer-gone` / an error, the native shell SURVIVES it, and BACK still returns to the mode selector |

**12 and 14 are the ones that decide the architecture.** A WebView that renders
beautifully for ten minutes and wedges after ninety is worse than a plainer
native renderer, because it fails during the party rather than before it.

## What a failure means

- **Wedges under soak (12)** → strategy A2 is not viable as the primary
  renderer; take strategy B.
- **Renderer-gone occasionally but recovers (14)** → A2 is viable *with* a native
  watchdog and fallback scene, which the implementation must then carry.
- **Clean through 12 and 14** → A2 is viable, and the architecture optimises for
  presentation single-source-of-truth as intended.
