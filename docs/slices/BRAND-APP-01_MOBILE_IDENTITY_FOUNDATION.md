# Slice BRAND-APP-01 — Mobile Identity Foundation

**Status:** ready for implementation  
**Goal:** make the NubArca mobile application visibly and structurally conform to the brand invariants before screen-by-screen redesign.

## Why this slice exists

The mobile client already has:
- semantic light/dark palettes;
- a theme provider;
- shared UI primitives;
- 48 dp-class touch targets;
- Expo icon/adaptive-icon wiring.

It does not yet have:
- native branded splash integration;
- a first-class branded restoring state;
- approved NubArca typefaces wired into native text roles;
- canonical geometry roles aligned with the product brand;
- an automated brand-invariant gate.

This slice fixes the foundation only. It intentionally does not redesign Photos, Videos, Albums, Settings or viewer UX yet.

## Scope

### A. Asset pipeline

Modify:
- `scripts/sync-brand-assets.py`

Add mobile consumer copies:
- `assets/brand/nubarca/runtime/web/nubarca-mark-flat-on-dark-256.png`
  → `mobile/assets/brand/nubarca-mark-flat-on-dark-256.png`
- `assets/brand/nubarca/runtime/web/nubarca-wordmark-on-dark-480w.png`
  → `mobile/assets/brand/nubarca-wordmark-on-dark-480w.png`

Requirements:
- copy only;
- byte identity verified;
- `--check` validates both assets;
- no reference/source assets reach mobile runtime.

### B. Native splash

Modify:
- `mobile/package.json`
- `mobile/app.config.js`
- lockfile

Install SDK-compatible `expo-splash-screen`.

Configure plugin:
- Midnight Navy background;
- approved flat on-dark mark;
- 120 image width;
- contain;
- same identity in OS light/dark splash variants.

### C. Native font bundle

Add:
- `mobile/assets/fonts/SpaceGrotesk-Medium.ttf`
- `mobile/assets/fonts/SpaceGrotesk-SemiBold.ttf`
- `mobile/assets/fonts/SpaceGrotesk-Bold.ttf`
- `mobile/assets/fonts/Exo2-Regular.ttf`
- `mobile/assets/fonts/Exo2-Medium.ttf`
- `mobile/assets/fonts/Exo2-SemiBold.ttf`
- corresponding OFL notices

Source fonts from official upstream/Google Fonts, store locally, and record provenance/checksum in a mobile font manifest.

Add:
- `mobile/src/ui/fonts.ts`

Responsibilities:
- canonical family names;
- loading map;
- no remote font fetch;
- graceful fallback on failure.

### D. Token contract

Modify:
- `mobile/src/ui/tokens.ts`
- `mobile/src/ui/palette.ts`

Add semantic roles required by Product Character v1:

Geometry:
- `radius.compact = 8`
- `radius.control = 12`
- `radius.card = 16`
- `radius.hero = 24`
- `radius.pill = 999`

Typography:
- display/page title → Space Grotesk
- UI/body/labels → Exo 2
- no Exo 2 weight above 600

Identity boot:
- theme-independent boot background/foreground/activity roles

Motion:
- 120 / 180 / 240 / 320 ms
- canonical easing identifier

Migration:
- retain legacy aliases only if required to avoid unrelated screen churn;
- mark them deprecated;
- no new call site may use a deprecated alias.

### E. Shared primitives

Modify/refactor:
- `mobile/src/ui/components.tsx`

Introduce or normalize:
- `Screen`
- `AppHeader`
- `Text`/type-role helper or explicit exported typography styles
- `Button`
- `IconButton`
- `Surface`
- `SectionTitle`
- `StatusBadge`

This slice does not require every screen to migrate. It requires all new/changed identity surfaces to use these primitives.

### F. Branded boot lifecycle

Modify:
- `mobile/app/_layout.tsx`

Add:
- `SplashScreen.preventAutoHideAsync()` at module scope;
- critical font bootstrap;
- `BrandBootState`;
- explicit native splash release when local visual foundation is ready.

Rules:
- native splash must not wait on API/session restore;
- `BrandBootState` owns the potentially longer restoring period;
- release native splash even if font loading fails;
- no white native/root background flash.

### G. Invariant gate

Add:
- `design/brand-contract.json`
- `scripts/check-brand-invariants.py`

First enforcement target:
- brand contract consistency;
- mobile splash config;
- mobile token primitive values;
- approved product spelling;
- local hard-coded brand colors in `mobile/app` and `mobile/src/ui` outside authorized token/palette files.

Add test script:
- `npm`/root CI integration according to existing repository conventions.

## Out of scope

- Photos/Videos grid redesign
- bottom tab redesign
- Albums redesign
- login redesign beyond consuming new boot/type primitives
- media viewer redesign
- web legacy CSS cleanup
- TV browser CSS cleanup
- new brand-logo artwork
- new palette colors

## Acceptance criteria

### Identity
- [ ] Cold-start native splash is Midnight Navy + approved flat mark.
- [ ] Launcher icon is not reused as splash artwork.
- [ ] Session restoring state is visibly NubArca, not spinner-only.
- [ ] Space Grotesk/Exo 2 render on branded mobile surfaces.
- [ ] No runtime font CDN/network dependency.

### Continuity
- [ ] No white flash between native splash and React root.
- [ ] Native splash is released before potentially slow remote/session work completes.
- [ ] Dark boot → Light app transition is intentional and clean when Light is selected.

### Semantics
- [ ] Electric/Cyan/Violet meanings match Product Character v1.
- [ ] New components do not use local hex colors.
- [ ] New controls use canonical geometry roles.

### Accessibility
- [ ] 48 dp-class touch targets remain intact.
- [ ] text scaling remains enabled.
- [ ] reduced motion is honored by any boot animation.
- [ ] startup status has an accessible text/status representation.

### Tests / release
- [ ] TypeScript passes.
- [ ] existing mobile tests pass.
- [ ] brand invariant checker passes for the enforced scope.
- [ ] `sync-brand-assets.py --check` passes.
- [ ] Expo config introspection shows splash plugin/assets.
- [ ] Android and iOS release builds are visually checked on physical/simulator targets.
- [ ] Android production/release build is used for final splash QA.

## Recommended commit sequence

1. `brand: add product character and invariant contract`
2. `brand: sync mobile splash and wordmark assets`
3. `mobile: add native splash and local font bootstrap`
4. `mobile: align tokens and identity primitives`
5. `mobile: replace restoring spinner with BrandBootState`
6. `ci: enforce mobile brand invariants`

Each commit should remain buildable or be clearly paired where lockfile/config changes require it.

## Definition of done

BRAND-APP-01 is done when a cold launch, before any feature screen appears, is unmistakably NubArca **and** the codebase has a machine-enforced path that prevents new mobile identity drift.
