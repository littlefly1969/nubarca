# NubArca Product Character v1

**Status:** proposed product-design authority  
**Scope:** Web, Mobile, TV, public surfaces  
**Brand asset authority:** `assets/brand/nubarca/brand-manifest.json`  
**Identity authority:** `docs/brand.md` + this document  
**Implementation authority:** semantic tokens and platform adapters, never local component hex values

## 1. Product character

NubArca is a private personal cloud that should feel **calm, precise, luminous, media-first, and owned by the user**.

The visual system must communicate five traits without relying on the logo:

1. **Private calm** — deep surfaces, controlled contrast, no visual noise.
2. **Luminous precision** — Electric Blue for action, Cyan Glow for focus/connection/activity.
3. **Content first** — photos, video and user files remain the visual subject; chrome recedes.
4. **Crafted utility** — clear hierarchy, consistent geometry, no generic admin-dashboard styling.
5. **Cross-device continuity** — Web, Mobile and TV are different contexts but unmistakably one product.

NubArca is not a neon/gaming interface, a generic blue SaaS dashboard, or a glassmorphism showcase.

## 2. Signature language

### 2.1 Surfaces

Dark is the identity-first environment.

- `Midnight Navy` is the product canvas and boot surface.
- `Deep Blue` is the primary raised surface.
- Surface separation comes primarily from luminance, one-pixel borders and spacing.
- Heavy shadows are exceptional; elevation is restrained.

Light mode is a fully supported accessibility/user-preference mode derived from the same identity, not a separate visual brand.

### 2.2 Accent semantics

Accent colors have meaning and may not be chosen decoratively.

| Signal | Meaning | Typical use |
| --- | --- | --- |
| Electric Blue | primary action / selection | primary buttons, selected navigation, confirmed active choice |
| Cyan Glow | focus / connection / live activity | focus ring, sync, pairing, cast, processing activity |
| Soft Violet | intelligence / inference | People, Similar, AI-derived findings, Laboratory highlights |
| Warm red | destructive / failure | delete, revoke, destructive validation |
| Green | successful completion | completed sync, healthy/ready state |

**Rule:** Cyan and Violet are scarce. Their rarity is part of NubArca's recognizability.

### 2.3 Luminous edge

A recurring product signature may use a **thin luminous edge**, never a large glow:

- 1–2 px active/focus indicator;
- Electric Blue as the base;
- Cyan may appear at the energetic end of the indicator on dark surfaces;
- no large blurred neon panels;
- no gradient text.

This pattern is intended for selected navigation, device connection state and other high-salience identity moments. It is not a default border treatment.

### 2.4 Geometry

The canonical shape language is:

- base spacing rhythm: 8 px;
- compact radius: 8 px;
- control radius: 12 px;
- card/surface radius: 16 px;
- hero/large-media radius: 24 px only where scale justifies it;
- pill: full/999 only for chips, badges and circular controls.

Controls within the same hierarchy must use the same radius role across platforms. Platform-native constraints may alter physical size, not semantic role.

### 2.5 Typography

- **Space Grotesk**: display, page title, prominent section title.
- **Exo 2**: UI, body, labels, controls.
- **Monospace**: hashes, logs, codes, machine identifiers only.

Approved weights:

- Space Grotesk: 500 / 600 / 700
- Exo 2: 400 / 500 / 600

No synthesized `700` Exo 2. No screen-specific font family.

Mobile must bundle native font files locally; no font is fetched from a CDN at runtime.

## 3. Hierarchy and density

NubArca uses three density modes.

### Media
Low chrome density. Media dominates. Controls float only when needed.

### Library / Files
Medium-high information density. Alignment and metadata clarity matter more than decorative cards.

### Administration / specialist tools
Structured density. Strong section hierarchy, minimal decorative identity effects.

The same tokens and components apply in all three modes; density changes, brand semantics do not.

## 4. Motion character

Motion is functional and quiet.

- micro feedback: 120 ms;
- standard state change: 180 ms;
- navigation/surface transition: 240 ms;
- deliberate reveal: 320 ms maximum.

Default easing: `cubic-bezier(0.2, 0, 0, 1)`.

Avoid:
- elastic/bouncy motion for ordinary controls;
- continuous ambient animation;
- large parallax;
- glow pulsing except a deliberately bounded boot/live indicator.

All non-essential animation must respect reduced-motion settings.

## 5. Iconography

Icons should be:

- outline-first;
- optically consistent;
- simple at 18–24 px;
- filled only for a selected/active state when it materially improves recognition.

The NubArca brand mark is not an application-navigation icon. Product icons do not imitate or remix the brand mark.

## 6. Product surfaces

### App shell
The shell establishes identity before any feature content:

- Midnight/Deep hierarchy;
- precise NubArca lockup;
- selected navigation with restrained luminous edge;
- utility controls quiet by default;
- content region visually primary.

### Media
- dark media chrome regardless of light/dark application preference;
- minimal overlays;
- metadata subordinate to content;
- Cyan reserved for focus/live transport;
- Violet reserved for inference.

### Files
- clean rows/grid;
- precise breadcrumb and selection states;
- less decorative card use;
- strong filename/metadata hierarchy.

### Home
Home is an editorial product surface, not an admin dashboard.

Priority:
1. recent/meaningful user content;
2. continuity (recent activity, sync);
3. health/status only when actionable;
4. quick actions;
5. operator/administrative detail last.

### Laboratory / AI
Soft Violet identifies inference without replacing standard action semantics. A primary action in Laboratory remains Electric Blue.

### Private / Vault
Privacy is communicated with restraint: secure state, deliberate access boundary, low-noise surfaces. Do not invent a separate “black/red security theme”.

### TV
TV is cinematic and 10-foot:
- dark background;
- larger target geometry;
- strong focus states;
- content-dominant layout;
- same Electric/Cyan/Violet semantics.

### Public Party
Party may be more expressive and social, but remains inside the palette and typography contract.

## 7. Mobile launch character

The mobile launch is one continuous identity sequence:

`native splash → branded boot/restoring state → login or authenticated app`

### Native splash
- fixed Midnight Navy background;
- approved **flat on-dark mark**, not the luminous launcher icon;
- no tagline;
- no spinner;
- no full-screen illustration;
- no stretch/crop of an approved asset.

### In-app boot/restoring state
- same Midnight Navy background;
- approved on-dark wordmark or mark;
- small Cyan live indicator;
- one short status line only when startup is not instantaneous;
- no generic platform spinner as the primary identity element.

The native splash must disappear as soon as the JS visual foundation is ready. Session/network restoration continues in the branded in-app state rather than holding the native splash indefinitely.

## 8. Accessibility is part of the brand

A NubArca-branded state that fails contrast, focus visibility, text scaling or reduced motion is not brand-compliant.

- WCAG AA for ordinary text;
- visible focus state on every interactive web/TV control;
- 48 dp class mobile touch target;
- text scaling remains enabled;
- no color-only status;
- media overlays must remain legible over arbitrary imagery.

## 9. Anti-patterns

Do not introduce:

- local hex values in components;
- a second blue that “looks close” to Electric Blue;
- arbitrary radii;
- heavy drop shadows;
- gradient text;
- cyan as a generic decoration;
- violet primary buttons;
- launcher/PWA artwork inside small UI chrome;
- unapproved logo transformations;
- per-feature themes;
- system/default fonts on branded identity surfaces once the approved fonts are available.

## 10. Decision rule

When a new component or screen requires a visual decision:

1. choose an existing semantic role;
2. use an existing primitive/component;
3. add a semantic token only if the meaning is genuinely new;
4. add a brand primitive only through a brand-system change.

A feature is never allowed to solve a local styling problem by inventing a color, radius, shadow or type role.
