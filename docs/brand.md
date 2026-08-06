# NubArca brand

This document is the current-state reference for the brand in software: the name,
the palette, the typography, the geometry and which approved artwork goes where.

## Name

Always written:

```
NubArca
```

Capital N, capital A, nothing between them. The television product is
`NubArca TV`.

Never `Nubarca`, `NUBARCA`, `nubarca` or `Nub Arca` on a user-facing surface.
Lower-case `nubarca` is correct only inside identifiers that are conventionally
lower-case: hostnames, Docker resources, filesystem paths, environment-variable
prefixes and storage keys.

Brand names are **not translated**. Every locale renders the identical string.
In the web frontend the name lives in [`frontend/src/brand/brand.ts`](../frontend/src/brand/brand.ts);
`app.name` in each locale file resolves to the same value so existing
translation call sites keep working.

## Palette

| Token | Name | Hex | Role |
| --- | --- | --- | --- |
| `--brand-midnight-navy` | Midnight Navy | `#0A0F1A` | Principal dark background |
| `--brand-deep-blue` | Deep Blue | `#0F1E3A` | Raised surfaces |
| `--brand-electric-blue` | Electric Blue | `#1565FF` | Primary accent |
| `--brand-cyan-glow` | Cyan Glow | `#00D4FF` | Secondary accent, focus ring, restrained glow |
| `--brand-soft-violet` | Soft Violet | `#9A6CFF` | Limited highlight — never the primary action colour |
| `--brand-cloud-white` | Cloud White | `#F5F7FB` | Primary text, light surfaces |

The six `--brand-*` tokens hold the approved hexes verbatim. Everything else
consumes the **semantic** tokens (`--surface-canvas`, `--accent`, `--text-primary`, …)
which map onto them in [`frontend/src/styles.css`](../frontend/src/styles.css).
No component may introduce a brand colour of its own.

### Two deliberate legibility tints

Electric Blue `#1565FF` reaches only **3.96:1** on Midnight Navy and **4.0:1** on
white — below WCAG AA for text. `--accent` is overwhelmingly a text and border
colour (40 text uses, 22 border uses, 15 fills), so it is set to a tint:

- dark theme `--accent: #3D82FF` — 5.3:1 on the canvas;
- light theme `--accent: #0B4FD6` — 6.3:1 on the canvas.

The exact brand hex is not lost. `--accent-strong` is Electric Blue itself and is
used for **fills**, where the white `--accent-contrast` on it clears AA at 4.84:1.
These are the only two deviations from an official hex, and both are annotated in
the stylesheet.

### Themes

Dark is the first-run default and lives on bare `:root`, so a page still renders
dark if the bootstrap fails. Light is derived from the same palette (Cloud White
canvas, Midnight Navy text). `system` remains selectable and is resolved in JS,
never by a `prefers-color-scheme` rule on the palette — otherwise a dark-mode OS
could override an explicit Light choice.

Both themes declare `color-scheme`, so native controls and scrollbars follow.
Destructive (`--danger`) and successful (`--success`) stay warm-red and green:
they must read as themselves, not as another shade of the brand blue.

## Typography

```
Headings, display text  Space Grotesk   (--font-heading)
UI, body, labels        Exo 2           (--font-ui)
Logs, hashes, code      unchanged mono  (--font-mono)
```

Both families are **SIL Open Font License 1.1**. They are installed as the
`@fontsource/space-grotesk` and `@fontsource/exo-2` packages and imported in
[`frontend/src/main.tsx`](../frontend/src/main.tsx), so Vite bundles the woff2
files into our own `dist` and serves them same-origin. **Nothing is fetched from
a third-party CDN at runtime.**

Only the required weights ship, latin subset only (the UI is en + it):

- Space Grotesk 500, 600, 700 — headings and display text
- Exo 2 400, 500, 600 — UI, body and labels

`@fontsource` sets `font-display: swap`, so text paints immediately in the
fallback stack and is never invisible. Every font token ends in a real
`sans-serif`, so a failed woff2 leaves the UI fully usable.

The licence notices are served at `/fonts/space-grotesk-OFL.txt` and
`/fonts/exo-2-OFL.txt`.

## Geometry

| Rule | Value | Token |
| --- | --- | --- |
| Base grid | 8 px | `--space-unit` |
| Card radius | 16 px | `--radius-card` |
| Button radius | 12 px | `--radius-button` |
| App icon corner radius | 20% | baked into the app-icon artwork |
| Minimum icon size | 24 px | `MIN_ICON_SIZE_PX`, enforced by `.brand-mark__icon` |
| Minimum wordmark width | 120 px | `MIN_WORDMARK_WIDTH_PX` |
| Logo clear space | 25% of logo height | `.brand-mark` inline padding |
| Default theme | dark | `DEFAULT_THEME_PREFERENCE` |

Do not stretch or rotate the logo, change its proportions, recolor it outside the
approved variants, or add heavy shadows.

## Assets

`assets/brand/nubarca/` is the **canonical source of truth** for every NubArca
visual asset. It is the approved handoff package, imported without redesign:

```
assets/brand/nubarca/
  README.md  NUBARCA_BRAND_HANDOFF.md  brand-manifest.json  checksums.sha256
  source/      8 masters      — preserved originals, never shipped
  runtime/     39 assets      — the only assets applications may use
    favicon/  pwa/  tv/  web/
  reference/   7 boards       — documentation, never runtime UI
```

54 catalogued assets. `sha256sum --check checksums.sha256` must pass; the
manifest records each asset's dimensions, alpha, glow and provenance, and
`frontend/src/brand/brandPackage.test.ts` verifies all of it against the real
binaries — including that every `runtime/` file is runtime-ready and that no
source master or reference board can reach a shipped directory.

The approved binaries are never edited. Metadata may be corrected when it
misdescribes a binary; the affected checksum entry is then refreshed and the
whole file re-verified.

### Source to consumer

`scripts/sync-brand-assets.py` copies `runtime/` assets into the directories the
platforms require. It **only copies** — no resizing, recolouring or
regeneration — so every served file has the same SHA-256 as its canonical
source. `--check` re-verifies that. Destination basenames match the canonical
ones, so any consumer path traces back to the package by name.

| Consumer | From | Used for |
| --- | --- | --- |
| `frontend/public/brand/favicon*` | `runtime/favicon/` | browser tab icons |
| `frontend/public/brand/nubarca-pwa-*`, `nubarca-apple-touch-icon-180.png` | `runtime/pwa/` | PWA install + Apple touch |
| `frontend/public/brand/nubarca-mark-flat-on-{dark,light}-{16..256}.png` | `runtime/web/` | shell, navigation, drawer |
| `frontend/public/brand/nubarca-wordmark-on-dark-{480,960,1440}w.png`, `nubarca-wordmark-on-light.png` | `runtime/web/` | login and prominent placements |
| `tv/assets/brand/nubarca-expo-app-icon-1024.png` | `runtime/pwa/` | Expo launcher + adaptive icon |
| `tv/assets/brand/nubarca-android-tv-banner-320x180.png` | `runtime/tv/` | Android TV banner slot |
| `tv/assets/brand/nubarca-fire-tv-{icon-512,banner-1280x720}.png` | `runtime/tv/` | Fire TV icon and banner |
| `tv/assets/brand/nubarca-tv-lockup-transparent-{640,1280,1800}w.png` | `runtime/tv/` | in-app TV branding |
| `tv/assets/brand/nubarca-tv-splash-1920x1080.png` | `runtime/tv/` | TV splash composition |

Run `python3 scripts/sync-brand-assets.py` after changing the package;
`--check` fails the build if a consumer copy drifts.

### Which artwork goes where

Two rules the code enforces rather than documents:

**Small UI contexts (16–48 px) use the FLAT mark.** The launcher/PWA icon is
luminous and framed; at 26 px its glow smears and its frame competes with the
surrounding chrome. It is an app icon, not a UI icon. `flatMarkUrl()` picks the
smallest shipped size covering the rendered box at 2×.

**Dark and light surfaces get different artwork.** The on-dark mark and wordmark
carry Cloud White; the on-light ones carry Midnight Navy. `BrandMark` selects
from the *resolved* theme, so Cloud White artwork never lands on Cloud White.

The approved light-surface wordmark places the lockup on a much larger
transparent canvas (77.2% width usage, against 98.3% for the dark files).
`wordmarkAsset()` divides by that measured ratio, so a requested width is the
width of the **visible lockup** in either theme and the 120 px minimum is real.

Reference boards are documentation only. They are never copied into
`public/` or `tv/assets/`, and the production build contains none.

## Identity contract

[`scripts/check-nubarca-identity.sh`](../scripts/check-nubarca-identity.sh)
asserts a **positive** identity contract: `NubArca.sln`, the `NubArca.Api`
assembly and namespaces, `NubArca.Api.Tests`, the `nubarca-frontend` package, the
`it.littlefly.nubarca.tv` TV package, the `NubArca.` cookie prefix,
`nubarca`-named Compose containers, volumes and networks, and one agreed release
version across backend and frontend.

Stating what the product *is* beats listing what it must not be called. A
denylist has to carry the forbidden name forever, and every exception argues for
one more; a positive contract fails on drift to *any* other spelling and leaves
the repository with no memory to maintain.

The contract's second half is that source describes the product and never one
installation. An IP literal, a `login@host` target, this installation's public
hostname, a `NUBARCA_*` variable falling back to a path or URL, and a `cd` into a
host checkout directory are all failures.

`--self-test` proves the detectors themselves, and `NubArcaIdentityTests` runs
both the self-test and the tree scan inside `dotnet test`, so a regression fails
the canonical test matrix rather than only a step somebody can forget.

Installation-specific values — the public origin, database credentials, hash
peppers and TV signing material — are operator configuration, never source
constants. Where a source constant used to pin the production origin, the pin is
preserved and still fails closed.
