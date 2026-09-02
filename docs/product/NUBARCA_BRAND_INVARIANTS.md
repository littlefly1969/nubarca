# NubArca Brand Invariants v1

These invariants are the non-negotiable software contract for product identity.

The canonical visual-asset catalog is `assets/brand/nubarca/brand-manifest.json`. Asset counts, dimensions, runtime readiness and hashes must be read from that file; descriptive documentation must not become a competing source of truth.

## Invariant catalog

### BRAND-NAME-01 — Product spelling

User-facing product identity is exactly:

- `NubArca`
- `NubArca TV`

Brand names are not translated.

### BRAND-ASSET-01 — Canonical asset provenance

Only assets catalogued as `runtimeReady: true` may ship.

`source/` and `reference/` assets are never runtime application assets.

Consumer copies must remain byte-identical to their canonical source unless the manifest explicitly defines a generated derivative.

### BRAND-ASSET-02 — No asset-count duplication

Counts such as total/runtime/source/reference asset quantities are machine facts owned by `brand-manifest.json`.

Documentation may say “see manifest” but must not require hand-maintained counts for correctness.

### BRAND-LOGO-01 — Correct artwork by context

- 16–48 px UI slots use the approved flat mark.
- Launcher/PWA artwork is not used as a small UI mark.
- Dark surfaces use on-dark artwork.
- Light surfaces use on-light artwork.
- Minimum icon and wordmark sizes and clear-space rules remain enforced.

### BRAND-PALETTE-01 — Primitive palette

The only brand primitives are those in the canonical manifest:

- Midnight Navy `#0A0F1A`
- Deep Blue `#0F1E3A`
- Electric Blue `#1565FF`
- Cyan Glow `#00D4FF`
- Soft Violet `#9A6CFF`
- Cloud White `#F5F7FB`

Components consume semantic tokens, not primitive hex values.

### BRAND-COLOR-SEMANTICS-01 — Accent meaning

- primary action/selection → Electric Blue semantic role;
- focus/connection/activity → Cyan semantic role;
- intelligence/inference → Violet semantic role;
- destructive → warm red;
- success → green.

Cyan and Violet are not generic decoration.

### BRAND-CONTRAST-01 — Legibility overrides literal accent use

Approved accessibility tints may be used for text/border roles where literal Electric Blue fails AA. Exact Electric Blue remains the principal brand fill where its foreground contrast is valid.

A tint is a semantic implementation detail and never becomes a seventh brand primitive.

### BRAND-TYPE-01 — Type families and weights

- Space Grotesk: 500 / 600 / 700 for heading/display roles.
- Exo 2: 400 / 500 / 600 for body/UI roles.
- Monospace only for machine/code content.

Font binaries are locally bundled per platform. No runtime CDN font dependency.

### BRAND-GEOMETRY-01 — Shape language

Canonical roles:

- base spacing: 8 px/dp
- radius.compact: 8
- radius.control: 12
- radius.card: 16
- radius.hero: 24
- radius.pill: full

A platform may map values where native constraints require it, but components may not invent local geometry.

### BRAND-MOTION-01 — Motion

Canonical durations:

- fast: 120 ms
- standard: 180 ms
- navigation: 240 ms
- deliberate: 320 ms max

Default easing: `cubic-bezier(0.2, 0, 0, 1)`.

Reduced-motion preferences disable non-essential motion.

### BRAND-FOCUS-01 — Focus and touch

- Web/TV focus is always visually explicit.
- Mobile touch targets are at least 48 dp class.
- Focus/selection is not communicated by color alone where that would impair accessibility.

### BRAND-SURFACE-01 — Product surface roles

Surfaces use semantic roles (`canvas`, `raised`, `overlay`, `subtle`, media chrome). A feature may not create its own palette or “mini-theme”.

### BRAND-AI-01 — Intelligence signal

Soft Violet identifies AI/inference/highlight states. It never replaces Electric Blue for ordinary primary actions.

### BRAND-SPLASH-01 — Mobile launch identity

Native mobile launch must use:

- Midnight Navy background;
- approved flat on-dark mark;
- no launcher-icon reuse;
- no arbitrary/generated logo;
- no network dependency.

The in-app restoring state visually continues the native splash and is allowed to show a restrained Cyan activity indicator.

### BRAND-BOOT-01 — Native splash lifetime

The native splash is a boot bridge, not a loading screen for remote/session work.

It stays only until the local visual foundation is ready (JS + critical fonts/theme wiring). Potentially slower session restoration continues in an in-app branded boot state.

### BRAND-CROSS-PLATFORM-01 — Identity parity, not pixel parity

Web, Mobile and TV may differ in layout and density. They must share:

- palette semantics;
- typography roles;
- geometry roles;
- state semantics;
- approved identity assets.

### BRAND-DEBT-01 — No new visual debt

Existing exceptions may be baselined during migration. New/changed code may not introduce:

- hard-coded brand colors in components;
- arbitrary radii/shadows;
- unapproved fonts;
- alternate product spellings;
- reference/source artwork in runtime paths.

The baseline can only shrink.

## Enforcement maturity

### Stage A — contract
Token files and this invariant catalog exist; automated checker runs in report mode.

### Stage B — mobile strict
`mobile/app`, `mobile/src/ui`, mobile config and mobile brand assets are strict.

### Stage C — web strict
Web semantic tokens and component primitives are strict; legacy CSS debt is baselined and then removed.

### Stage D — TV strict
TV uses the shared semantic contract with platform-specific focus/10-foot mappings.

### Stage E — repository strict
All product surfaces pass the checker without migration exceptions.
