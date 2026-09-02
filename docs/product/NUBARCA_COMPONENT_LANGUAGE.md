# NubArca Component Language v1

This document translates Product Character v1 into reusable component behavior. It defines semantics, not framework APIs.

## 1. Component principles

1. **Semantic first:** a component receives intent (`primary`, `danger`, `connected`) rather than raw colors.
2. **Quiet default:** neutral components recede; active state earns visual energy.
3. **One dominant signal:** a control should not combine glow, shadow, border and fill to communicate the same state.
4. **Content first:** media and file content remains visually stronger than surrounding chrome.
5. **Platform-native interaction:** keyboard, touch, D-pad and accessibility behavior stay native even when visual tokens are shared.

## 2. Typography roles

Recommended starting metrics. Platforms may adjust line-height optically while preserving role hierarchy.

| Role | Family | Weight | Mobile | Web |
| --- | --- | ---: | ---: | ---: |
| Hero | Space Grotesk | 700 | 32 | 40 |
| Page title | Space Grotesk | 600 | 24 | 28 |
| Section title | Space Grotesk | 600 | 18 | 20 |
| Body | Exo 2 | 400 | 16 | 16 |
| Secondary | Exo 2 | 400 | 14 | 14 |
| Label | Exo 2 | 500 | 13–14 | 13–14 |
| Button | Exo 2 | 600 | 15 | 14–15 |
| Badge | Exo 2 | 600 | 12 | 12 |

Avoid uppercase headings as a default brand device.

## 3. Button

### Primary
Use for one dominant action in a local decision context.

- fill: `action.primaryFill`;
- foreground: `text.onAccent`;
- radius: `radius.control`;
- mobile minimum height: 48;
- web target height: 40–44;
- no glow at rest;
- focus may use `signal.focus`.

### Secondary
- transparent/raised surface;
- semantic border;
- primary text;
- no blue fill.

### Quiet
- no persistent container unless hovered/pressed/focused;
- useful for toolbar actions.

### Danger
- warm destructive semantics;
- never Electric Blue with a red icon;
- destructive confirmation remains explicit.

### Disabled
Disabled state must remain identifiable without relying only on opacity when text contrast becomes insufficient.

## 4. IconButton

- minimum mobile hit area: 48;
- visual icon: 20–24;
- circular or control-radius container according to context;
- accessible label mandatory;
- selected/active is a semantic state, not a second icon style invented locally.

## 5. Surface / Card

### Raised surface
- Deep Blue in dark;
- white/raised in light;
- `radius.card`;
- one-pixel/hairline semantic border when separation is needed;
- shadow restrained.

### Hero surface
`radius.hero` only for high-scale home/editorial or large-media compositions. Do not use it for ordinary settings rows.

## 6. Navigation item

Default:
- quiet text/icon;
- no filled blue capsule.

Selected:
- clear text/icon hierarchy;
- subtle accent wash allowed;
- one 2 px-class luminous edge on layouts where the edge has a stable orientation;
- Cyan is optional and limited to the edge/focus energy, not the whole item.

Collapsed rail:
- icon remains optically centered;
- tooltip/accessible label preserves meaning.

## 7. Input / Search

- control radius;
- semantic surface;
- explicit label unless search context is self-evident and accessible name exists;
- focus ring uses semantic focus signal;
- error uses warm destructive semantics;
- placeholder is muted, never used as the only label for forms.

## 8. Chip / Filter

- pill geometry;
- neutral default;
- selected uses accent wash + accent text/border;
- primary fill is not used for every selected chip;
- Violet only when the chip represents inference/intelligence.

## 9. StatusBadge

Statuses use icon/shape/text plus color.

Suggested mapping:
- active/selected: Electric Blue semantics;
- connected/live/syncing: Cyan semantics;
- AI-derived: Violet semantics;
- success/complete: green;
- destructive/failure: red;
- neutral/pending: muted semantic surface.

## 10. EmptyState

An empty state contains:
1. optional simple icon/brand-neutral illustration;
2. clear heading;
3. one sentence of explanation;
4. at most one dominant CTA.

Do not use the NubArca logo as a generic empty-state illustration.

## 11. BootState

Identity-specific component.

- invariant Midnight Navy background;
- approved on-dark mark/wordmark;
- Cloud White primary identity;
- Cyan activity;
- no card container;
- no generic app header/navigation.

It is the only normal mobile surface intentionally allowed to remain brand-dark before a saved Light preference is applied to the main UI.

## 12. MediaTile

- content occupies the component;
- chrome appears on interaction or when persistent metadata is essential;
- unknown imagery gets semantic scrims for overlay legibility;
- selection is obvious but does not recolor the media itself;
- AI badge may use Violet;
- video/playback state remains readable over any image.

## 13. Feedback / notices

### Inline notice
Use a quiet semantic surface plus icon/title/body.

### Toast
Short-lived confirmation only. It must not carry critical information that disappears.

### Progress
Electric Blue for determinate progress; Cyan may represent live/connection/boot activity where that semantic meaning is intentional.

## 14. Focus language

Web/TV:
- 2 px-class focus ring or border;
- focus never depends on hover;
- TV may add restrained scale (about 1.02–1.04) because 10-foot focus requires stronger spatial feedback;
- reduced motion removes scale transition.

Mobile:
- press state may use opacity/surface shift;
- focus semantics are used for keyboard/assistive input when exposed by platform.

## 15. Loading language

Avoid a product full of unrelated spinners.

- inline data fetch: small native/semantic indicator;
- skeleton: content-shaped loading where layout is known;
- boot: NubArca `BootState`;
- background sync: status badge/progress in its owning surface.

## 16. Luminous edge limits

Allowed:
- selected app navigation;
- paired/connected device emphasis;
- focused TV tile;
- rare identity hero separator.

Not allowed:
- every card;
- every button;
- static decoration around whole pages;
- text glow;
- permanent neon halo.
