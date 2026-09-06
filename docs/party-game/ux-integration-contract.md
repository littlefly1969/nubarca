# Party Game — UX/UI integration contract

The binding description of **which existing NubArca surfaces, components, class
families and tokens** every Party Game slice must build on.

It introduces no behaviour and changes no code. Its purpose is to make the
answer to "what should this new screen be made of?" a lookup rather than a
judgement call, so that a live party experience — composer, control room, guest
phone, TV stage — arrives looking like the product it lives in.

Scope: SLICE 00 of the Party Game programme. Every later slice cites this file
and records any divergence it had to make.

## 0. The single most important fact

**NubArca has no React component library.** There is no `<Button>`, `<Card>`,
`<Input>` or `<Badge>` component to import. The design system is expressed as:

1. **CSS custom properties** declared on `:root` in
   [frontend/src/styles.css](../../frontend/src/styles.css), re-declared for the
   light theme, and mirrored as the source of truth in
   [design/tokens/](../../design/tokens/);
2. **element-level base rules** — `button`, `input`, `select`, `textarea` and
   the headings inherit the brand faces globally, so a bare `<button>` is
   already an on-brand control;
3. **named class families** (`.form-grid`, `.field`, `.tabs`, `.status-badge`,
   `.ws-*`, `.admin-*`, `.overlay-*`) that compose those tokens;
4. a **small set of real React components** for the things a class cannot do:
   [`Modal` / `Sheet`](../../frontend/src/components/Overlay.tsx),
   [`Icon`](../../frontend/src/components/icons/Icon.tsx),
   [`BrandMark`](../../frontend/src/brand/BrandMark.tsx),
   [`LanguageSwitcher`](../../frontend/src/components/LanguageSwitcher.tsx).

So "reuse the existing component" almost always means **reuse the existing class
family and the tokens under it**, and only rarely means "import a component".
Party Game must not answer this absence by inventing `PartyButton`,
`PartyCard` or a Party token layer. Where a Party surface genuinely has no
equivalent (§7), it extends the existing vocabulary rather than starting a
second one.

## 1. Page shell

There are **four distinct shells**, because Party Game has four audiences with
four different authorities. Choosing the wrong one is the most expensive mistake
available in this programme.

| Audience | Shell | Route family | Why |
| --- | --- | --- | --- |
| Owner, authenticated | The app shell — [`Layout`](../../frontend/src/components/Layout.tsx) via the protected route tree in [App.tsx](../../frontend/src/App.tsx) | `/albums/:albumId/...` | Owner Party work is ordinary authenticated work. It gets the sidebar, the top bar and `.app-main`. |
| Guest, anonymous | The public party surface — `.party-guest-hub` in [PartyGuestHub.css](../../frontend/src/pages/PartyGuestHub.css) | `/party/:token/...` | A guest holds a capability token, not an account. No sidebar, no nav, no account chrome. |
| TV stage | A dedicated full-bleed 16:9 surface, no app chrome | `/party/:token/...` | A display, not a page. §5. |
| Native TV app | [tv/](../../tv) — React Native / Expo | n/a | A separate runtime. §6. |

### 1.1 The authenticated shell is the viewport

`.app-shell` is exactly one dynamic viewport tall; the top bar and the sidebar
are rows of it; **`.app-main` is the only box with `overflow-y: auto`**. This is
an API, not a look. Two consequences bind every owner-side Party surface:

- anything that measures scrolling (an `IntersectionObserver` root, a
  virtualizer's scroll element) must be told where scrolling happens, which is
  what [`AppScrollProvider` / `useAppScrollViewport`](../../frontend/src/components/appScroll.tsx)
  carry. A document-rooted observer silently never fires;
- a Party surface must not add a second scrolling container or a second
  `100vh` box inside `.app-main`.

Owner Party pages use `.page-container` (a column with `gap: 1rem` and no
padding, because `.app-main` already provides the gutter) and put their title
row in `.admin-page__head`.

### 1.2 The public guest surface

`.party-guest-hub` is the newest and most carefully built public surface in the
repository and is the reference for every guest screen:

- it is **theme-independent by design**. A guest sees the same branded dark
  cover whatever theme their browser resolved, achieved by re-declaring a small
  set of local variables (`--party-hub-text`, `--party-hub-surface`, …) **from
  brand tokens** (`var(--brand-cloud-white)`, `var(--brand-midnight-navy)`,
  `var(--brand-cyan-glow)`) rather than from literals;
- it uses `min-height: 100svh` with a `100vh` fallback, `overflow-x: clip`, and
  `env(safe-area-inset-*)` on every outer edge;
- its font is `var(--font-ui)`, explicitly, because a public page may be opened
  by a browser whose defaults we do not control.

**Every new guest surface copies this construction.** It is the only sanctioned
way to pin a dark presentation without hardcoding colour.

### 1.3 Public party routes today

```
/party/:token                 PartyPage           guest hub
/party/:token/upload          PartyUploadPage     contribution
/party/:token/challenges      PartyChallengesPage challenge voting (pre-game)
/party/:token/print           PartyPrintPage      print studio
```

New Party Game guest and TV surfaces extend this family. They do **not**
introduce a second public token scheme.

## 2. Component mapping

Only entries that exist in the repository today. Every path is real.

### 2.1 Structure

| Party Game need | Reuse | Where |
| --- | --- | --- |
| Owner page shell | `Layout` + `.app-main` | `frontend/src/components/Layout.tsx` |
| Owner page body | `.page-container` | `styles.css` |
| Owner page header | `.admin-page__head` (`h2` + `p`) | `styles.css` |
| Owner toolbar | `.admin-toolbar`, `.admin-toolbar__search`, `.admin-toolbar__filter` | `styles.css` |
| Section grouping inside a panel | `<fieldset class="ws-filter-section">` | `AlbumSettingsPanel.tsx` |
| Guest page shell | `.party-guest-hub` construction | `PartyGuestHub.css` |
| Back navigation | `.back-link` | `styles.css` |

### 2.2 Controls

| Party Game need | Reuse | Where |
| --- | --- | --- |
| Primary action | bare `<button>` inside `.create-form`, or the login-card treatment: `background: var(--accent-strong)`, `color: var(--accent-contrast)`, `border-radius: var(--radius-button)` | `styles.css` |
| Secondary action | bare `<button>` with `background: transparent` and `border: 1px solid var(--border-default)` — the outline treatment the upload panel gives its folder picker | `styles.css` |
| Destructive action | `.btn-danger` | `styles.css` |
| Icon-only control | `<button class="icon-button">` + `<Icon>` + a mandatory `aria-label` | `styles.css`, `icons/Icon.tsx` |
| Segmented control (2–4 exclusive choices) | `.media-kind-tabs` / `.media-kind-tab.is-active` (roomy) or `.media-scope-tabs` / `.media-scope-tab` (compact) — selection is fill + border + weight, never colour alone | `styles.css` |
| Tabs (a step or a section switch) | `.tabs`, `.tabs__tab.is-active`, `.tabs__panel` | `styles.css` |

`Icon` accepts a fixed `IconName` union. Party Game reuses `check`, `close`,
`chevron-left`, `chevron-right`, `chevron-down`, `plus`, `edit`, `photo`,
`video`, `tv`, `more`, `info`. **Adding a glyph means adding a member to that
union**, in the same file, in the same stroke style — never an inline `<svg>` in
a Party component.

### 2.3 Forms

| Party Game need | Reuse | Where |
| --- | --- | --- |
| Form container | `.form-grid` | `styles.css` |
| Form subsection heading | `.form-grid__section` | `styles.css` |
| Field (label + help + control) | `.field`, `.field__label`, `.field__help` | `styles.css` |
| Two-up field row | `.field-row` (auto-fit, `minmax(13rem, 1fr)`) | `styles.css` |
| Text input / textarea / select | bare elements **inside `.field`** — that is what supplies height, border, radius, focus ring and disabled state | `styles.css` |
| Checkbox with a label | `.album-tv-label` | `styles.css` |
| Numeric setting with a suffix | `.album-party-number` | `styles.css` |

Controls outside `.field` fall back to the browser's box. Party Game forms use
`.field`.

### 2.4 Feedback and state

| Party Game need | Reuse | Where |
| --- | --- | --- |
| Status chip / badge | `.status-badge` with `--on` / `--off` modifiers | `styles.css` |
| Inline validation or request error | `<p class="inline-error" role="alert">` | `styles.css` |
| Page-level error | `<p class="page-error">` | `styles.css` |
| Success / transient confirmation | `<p class="muted" role="status">` — see §7, there is no toast system | `styles.css` |
| Empty state | `.empty-state` | `styles.css` |
| Loading — owner surfaces | `aria-busy="true"` on the region + a `.visually-hidden` label; skeletons where the shape is known | `styles.css` |
| Loading — guest surfaces | `.party-skeleton`, `.party-skeleton-hero`, `.party-skeleton-row` | `styles.css` |
| Modal (create / confirm) | `Modal` from `Overlay.tsx` | `components/Overlay.tsx` |
| Drawer (manage something that exists) | `Sheet` from `Overlay.tsx` | `components/Overlay.tsx` |
| Destructive confirmation | `Modal` with `dismissable={false}` while in flight | `components/Overlay.tsx` |

`Modal` / `Sheet` already provide the portal, viewport-fixed positioning, scroll
lock, focus trap and return, Escape handling and the `ownsKeyboard` capture-phase
contract from [keyboardOwnership.ts](../../frontend/src/components/keyboardOwnership.ts).
**No Party surface re-implements any of that**, and no Party surface uses
`window.confirm` (the current `PartyChallengeManager` does — §7).

### 2.5 Media

| Party Game need | Reuse | Where |
| --- | --- | --- |
| Choosing a photo from the album | `listAlbumItems` + the album-item summary shape | `@nubarca/api-client` (`albums.ts`) |
| Choosing across the whole library | Media Library + `AlbumPickerModal` — the ONE media-selection experience | `frontend/src/gallery/AlbumPickerModal.tsx` |
| Grid / list thumbnail | the **small** thumbnail endpoint | project invariant |
| Card / stage image | the **medium** preview endpoint | project invariant |
| Guest-facing media | token-scoped, metadata-stripped party media only | `PartyEndpoints.cs` |

Party challenge media is a **reference to an existing album member**, validated
server-side in the same owner album. No blob copy, ever.

### 2.6 Cross-cutting

| Party Game need | Reuse | Where |
| --- | --- | --- |
| Translation | `useI18n()` → `t('key')`; keys in `it.ts` (source of truth) and `en.ts` | `frontend/src/i18n/` |
| Language choice on a public page | `<LanguageSwitcher className="language-switcher language-switcher-public" />` | `components/LanguageSwitcher.tsx` |
| Brand lockup | `BrandMark` | `brand/BrandMark.tsx` |
| Scroll viewport for observers | `useAppScrollViewport()` | `components/appScroll.tsx` |
| API access | the generated-by-hand client package, one module per area | `frontend/packages/api-client/src/party.ts` |

Party Game copy lives under the existing `partyGame.*` and `partyChallenges.*`
key namespaces. **No literal user-facing string in a component.**

## 3. Tokens

The authority is [design/tokens/](../../design/tokens/); the runtime is the
`:root` block of `styles.css`. `design/brand-contract.json` lists the only files
allowed to contain a colour literal at all — a Party Game file is not among
them and will not become one.

### 3.1 Colour

| Role | Token |
| --- | --- |
| Page background | `--surface-canvas` (alias `--bg`) |
| Raised surface / card | `--surface-raised` |
| Overlay surface | `--surface-overlay` |
| Recessed field background | `--surface-subtle` |
| Hairline | `--border-default` |
| Emphasised hairline | `--border-strong` |
| Body text | `--text-primary` (alias `--fg`) |
| Supporting text | `--text-secondary` |
| Quiet text | `--text-muted` (alias `--muted`) |
| Accent **fill** carrying white text | `--accent-strong` |
| Accent **text** on a dark ground | `--accent` |
| Text on an accent fill | `--accent-contrast` |
| Accent wash | `--accent-subtle` |
| Focus ring | `--focus-ring` |
| Success / live | `--success` |
| Danger / destructive | `--danger` (alias `--error`) |
| Brand primitives (pinned dark surfaces only) | `--brand-midnight-navy`, `--brand-deep-blue`, `--brand-electric-blue`, `--brand-cyan-glow`, `--brand-soft-violet`, `--brand-cloud-white` |

`--accent-strong` versus `--accent` is not a preference. `--accent-contrast`
(white) is contrast-checked at 4.84:1 against `--accent-strong`; on the lighter
`--accent` tint it is only 3.6:1. **A filled button uses `--accent-strong`. A
coloured word uses `--accent`.**

Derived shades use `color-mix(in srgb, var(--token) N%, …)`, which is already
the repository's idiom (`.inline-error`, `.status-badge--on`, `.field
input:disabled`). Never a new hex.

### 3.2 Spacing

`--space-unit: 8px`. The scale in `design/tokens/geometry.json` is
`4 / 8 / 12 / 16 / 24 / 32 / 48`. Existing CSS expresses it in `rem`
(`0.25 / 0.5 / 0.75 / 1 / 1.5 / 2 / 3`) and Party Game does the same.

### 3.3 Radius

| Token | Value | Use |
| --- | --- | --- |
| `--radius-button` | `12px` | controls, fields, chips with square ends |
| `--radius-card` | `16px` | cards, panels, raised surfaces |
| `999px` | — | pills and badges (the geometry token is `radius.pill`) |

`radius.compact` (8px) and `radius.hero` (24px) exist in the token file; the hero
radius is the one the TV stage may use for its full-bleed card.

### 3.4 Typography

| Token | Face | Use |
| --- | --- | --- |
| `--font-heading` | Space Grotesk | `h1`–`h6` (applied globally), eyebrows, display numerals |
| `--font-ui` | Exo 2 | every control and all body copy |
| `--font-mono` | system mono | codes, never prose |

Approved weights for Exo 2 are **400 / 500 / 600 only** — this is why the base
stylesheet rewrites the user agent's `bold` on `strong`, `b` and `th` to 600.
Space Grotesk may use 700+. A Party surface that wants a heavy weight uses the
heading face; it never asks Exo 2 for 700.

### 3.5 Shadow

`--shadow-sm` / `--shadow-md` / `--shadow-lg`, plus `--glow-soft` for the accent
ring. No new shadow definitions.

### 3.6 Motion

From `design/tokens/motion.json`: `fast 120ms`, `standard 180ms`,
`navigation 240ms`, `deliberate 320ms`, easing
`cubic-bezier(0.2, 0, 0, 1)`. Non-essential animation is **disabled** under
`prefers-reduced-motion`, which `styles.css` already enforces globally with
`animation-duration: 0.001ms !important`. A Party animation that carries meaning
(a result reveal) must still be *comprehensible* when it does not play — the
end state has to be readable on its own.

### 3.7 Breakpoints

| Width | Meaning |
| --- | --- |
| `max-width: 900px` | the shell breakpoint: sidebar becomes the modal drawer. Owner surfaces recompose here. |
| `max-width: 640px` | the general mobile breakpoint (the most-used in `styles.css`) |
| `max-width: 480px` | small-phone adjustments on public party surfaces |
| `min-width: 901px` | complement used by workspace chrome |

Touch target minimum is **48px** (`geometry.json` → `touch.mobileMinimum`);
existing CSS uses `min-height: 44px` in several older places. **New Party Game
touch targets use 48px.**

## 4. Responsive behaviour

### 4.1 Owner — desktop (> 900px)

Full app shell with the sidebar. The control room may use two columns: the live
state and the primary command on the left, secondary context (next activity,
guest count, TV status) on the right. The composer may show its preview beside
the form.

### 4.2 Owner — tablet (640–900px)

Sidebar collapses to the drawer. The control room becomes one column, ordered:
**status → current activity → primary command → secondary context**. The
composer's preview moves below the form. Nothing is hidden; things are
reordered.

### 4.3 Owner — mobile (< 640px)

One column. The primary command becomes full width and is the last thing above
the fold, not the last thing on the page. The composer's steps become sequential
rather than side-by-side. An owner running the party from a phone must be able
to reach the phase-advancing command without hunting.

### 4.4 Guest — mobile (design target 390px)

The only guest target that matters. One column, `.party-guest-hub`
construction, safe-area insets on every edge, thumb-reachable actions in the
lower half of the viewport, 48px minimum targets, **no horizontal scroll ever**.
The voting screen must fit one viewport without scrolling.

### 4.5 TV — 16:9

1920×1080 and 1280×720 must both work from the same CSS, which means the stage
is sized in viewport-relative units (`vw` / `vh` / `clamp()`), never in pixels
tuned for one panel. All content sits inside a **3.5% safe area** on each edge
(the same ratio the native TV app's `overscan()` uses, floored at 24px
horizontally and 16px vertically). Minimum on-screen body text at 1080p is large
enough to read from ~3m; the existing native `PartyChallengeHold` sets the
reference proportions (title ≈ 58/1080 of the height, body ≈ 36/1080).

## 5. The TV stage decision

**The Party Game TV stage is a web surface**, under the public `/party/:token`
family, rendered by the same React tree as the composer preview.

Rationale, recorded here because it is the one place this contract departs from
a literal reading of the programme brief:

- the brief requires that "preview and TV share the markup/rendering core" and
  that there is exactly **one canonical card renderer**. The native TV app
  ([tv/](../../tv)) is React Native: it shares no DOM, no CSS and no component
  with the web frontend. Implementing the stage there would mean two renderers
  by construction — precisely what SLICE 02 forbids;
- the repository already runs a browser TV experience at `/tv`
  ([TvBrowser.tsx](../../frontend/src/pages/TvBrowser.tsx)), so a 10-foot web
  surface is an established pattern, not a new one;
- a token-addressed web stage needs no pairing, no APK and no OTA to reach a
  television, which matters for a feature whose whole point is being set up
  minutes before a party.

The native app keeps its existing behaviour unchanged: the party slideshow and
the existing `PartyChallengeHold` overlay are untouched by this programme. If a
native game stage is ever wanted, it is a separate slice with its own
acceptance criteria.

## 6. Party Game UX principles

Nine rules. Every Party Game review checks against them by number.

1. **One primary action per state.** Each phase has exactly one command that
   advances it. It is the only filled accent button on screen. Everything else
   is outline, quiet or absent.
2. **Progression is visible.** The owner always sees which round this is, which
   phase it is in, and what comes next. The guest always sees whether the game
   is waiting, running, collecting votes or showing a result.
3. **No crowded toolbars.** Owner chrome carries the commands that are legal
   *now*. An illegal command is **absent**, not disabled — the same rule
   `albumCapabilities.ts` already applies to shared albums.
4. **Preview is separated from editing.** The composer's TV preview is a
   distinct region with its own frame. It is never interleaved with the fields
   that feed it, and it is never editable in place.
5. **Live state is always legible.** Session status, TV connection and guest
   count are visible without opening anything. A stale or disconnected TV says
   so.
6. **Errors are non-destructive.** A refused command (a stale version, a lost
   connection) never loses the owner's place and never duplicates an action. It
   refreshes the snapshot, states what happened inline, and leaves the operator
   in control.
7. **Destructive actions are explicit.** Finishing a game, skipping an activity
   and deleting a challenge each require a deliberate confirmation through
   `Modal` — never `window.confirm`, never a bare click.
8. **The guest phone is one-handed.** Large targets in the lower half of the
   screen, no hover dependency, no tables, no sidebars, no owner controls, no
   scrolling required to cast a vote, and immediate visual feedback on tap.
9. **The TV is readable from the sofa.** Large type, high contrast, one idea per
   scene, safe-area respected, and **no interactive controls at all** — it is a
   display, not a console.

## 7. Real gaps

Honest inventory. Each is a decision a later slice must make deliberately.

| Gap | Present state | Ruling |
| --- | --- | --- |
| **No toast/notification system** | Feedback is inline `role="status"` / `role="alert"`; the only "toast" is `.tv-gallery-toast`, local to the TV gallery | Party Game uses **inline** status regions. It does not introduce a global toast provider. |
| **Legacy party CSS hardcodes colour** | `.party-challenge-card`, `.party-vote-budget`, `.party-game-tv-preview` and friends use literals (`#171c25`, `#67a0ff`, `#2c64b8`) predating the token system | New Party Game CSS is token-only. Touching a legacy rule for another reason is the moment to convert it; a blanket restyle is out of scope. |
| **`window.confirm` for deletion** | `PartyChallengeManager` uses it | Replace with `Modal` when SLICE 03 rewrites that surface (principle 7). |
| **`<select>` as a media picker** | `PartyChallengeManager` picks a photo from a `<select>` of filenames | SLICE 03 uses a visual picker built on the existing album-items data and the `AlbumPickerModal` precedent. |
| **No stepper/wizard pattern** | The nearest is `.admin-import-steps` (numbered `li`, `.is-active`) | SLICE 03's three-step composer extends `.admin-import-steps`; it does not invent a second stepper. |
| **No timer/countdown component** | None exists | SLICE 06/07 introduce one, in the shared vocabulary, styled from tokens, with an accessible text equivalent. |
| **Realtime is polling** | There is **no** SignalR, WebSocket or SSE anywhere in application code. Every live surface polls a snapshot endpoint (`ViewerScreen`, `TvBrowser`, `PartyPage`, `PairingScreen`) | Party Game polls a versioned snapshot endpoint on the same pattern. Introducing a realtime stack is explicitly forbidden by the programme's hard constraints. |
| **44px vs 48px targets** | Older CSS uses 44px; the geometry token says 48px | New Party Game targets are 48px. Existing rules are left alone. |
| **Two TV runtimes** | Web `/tv` and native `tv/` both render party content | §5. The game stage is web; the native app is untouched. |

## 8. Acceptance checklist for later slices

A Party Game PR is contract-compliant when all of the following hold.

- [ ] No new design system, UI framework or component library.
- [ ] No colour, shadow, radius, font or spacing literal where a token exists.
- [ ] No `PartyButton` / `PartyCard` / `PartyInput` — the shared class families
      and, where needed, `Modal` / `Sheet` / `Icon` are used instead.
- [ ] Every user-facing string comes from `useI18n()`, in both `it.ts` and
      `en.ts`.
- [ ] The correct shell for the audience (§1).
- [ ] Owner surfaces respect the `.app-main` scroll ownership contract.
- [ ] Guest surfaces are built the `.party-guest-hub` way and never depend on
      the resolved theme.
- [ ] The TV surface has no interactive control and respects the safe area.
- [ ] One primary action per state; illegal commands absent, not disabled.
- [ ] Destructive actions confirmed through `Modal`.
- [ ] `prefers-reduced-motion` leaves every screen comprehensible.
- [ ] `npm run lint`, `npm run test:run`, `npm run build` and the backend suite
      pass.

## 9. Addendum — the canonical activity card (SLICE 02)

[`frontend/src/party/PartyChallengeCard.tsx`](../../frontend/src/party/PartyChallengeCard.tsx)
is **the** renderer for a party activity. Everything that shows one to a person
goes through it: the composer's preview, the television, the owner's control
room.

Its three modes are three presentations of ONE markup. The DOM does not branch
on mode — only a `data-mode` attribute and the CSS behind it do — and a test
asserts that, because a preview built from different markup predicts nothing.
`preview` and `tv` additionally share the same sizing rule, expressed in
container units, so the preview is a scale model of the screen.

| Mode | Where | Sizing |
| --- | --- | --- |
| `preview` | composer step 3 | `aspect-ratio: 16/9`, `clamp(0.6rem, 2.1cqh, 1.6rem)` |
| `tv` | the game stage | one viewport, `clamp(0.8rem, 2.1cqh, 2.4rem)`, 3.5% safe area |
| `compact` | deck rows, "coming up next" | a row at the app's own type scale |

Every dimension inside the card is `em` against that one font size, which is
what lets a single set of rules fill both a 480px box and a 1920x1080 panel at
the same proportions.

The four activity kinds are told apart **by their name in words**; the accent
(`--accent`, `--danger`, `--accent-secondary`, `--text-secondary`) reinforces
and never replaces it. The same rule governs voting state.

Nothing overflows: the title and body are line-clamped in CSS while the full
text stays in the DOM for assistive technology, long words break, and the media
column is a fixed fraction with `object-fit: cover` — so portrait, landscape and
square photographs are all correct without the card ever measuring a bitmap.

This closes the "no stepper/timer/duplicate-renderer" gap only for the renderer.
`PartyChallengeManager` now uses it instead of its own copy of the composition,
and `.party-game-tv-preview` is gone.

## 10. Addendum — the composer (SLICE 03)

Preparing an activity is three steps in a `Modal`, opened from the deck inside
the album settings sheet. The page it lives on is unchanged.

| Step | What it decides |
| --- | --- |
| Activity | kind, title, instructions, photograph |
| Rules | duration, how the room decides, the question |
| Preview | the real renderer, included/excluded, where it will be played |

Gaps this closes, from §7:

- **`window.confirm` for deletion** — gone. Deleting an activity is a `Modal`
  that names what is being lost, which a browser dialog could never do.
- **`<select>` as a media picker** — gone. The picker is a grid of the album's
  own photographs; the previous answer asked a host to remember what
  `IMG_4821.jpg` looks like.
- **No stepper pattern** — `.party-composer-steps` extends the import wizard's
  numbered `li` + `.is-active` rather than inventing a second stepper.

Unsaved work is never lost to a stray keystroke: while the draft is dirty the
overlay stops being dismissable (`dismissable={!dirty}` plus `ownsKeyboard`, so
Escape cannot reach the settings sheet underneath either), and closing
deliberately asks once, in place, with the destructive answer marked
destructive.

Ordering stays in the deck, where it already worked. Step 3 states the position
as a fact and feeds it to the card's `context`, so the preview shows the
"Activity 4 of 6" line the television will show.

Everything is built from the shared vocabulary — `.form-grid`, `.field`,
`.media-kind-tabs`, `.status-badge`, `.btn-danger`, `Modal` — and
`PartyDeck.css` holds only the four things that vocabulary lacks: a deck row, a
stepper, a photo picker and two choice grids. The rules it replaced carried nine
colour literals.

## 11. Addendum — the guest live game (SLICE 05)

`/party/:token/game`, on the `.party-guest-hub` surface, reached from a capability
card the server offers by URL (`gameUrl`) exactly as it offers printing — the hub
builds no route from a boolean and shows no disabled tile.

**There is no second state machine.** `guestScene()` is a pure projection of the
server's phase onto the one thing the guest is being asked to do
(lobby / watch / vote / waiting / result / finished), and it is tested as such. A
phone that spent two activities in somebody's pocket shows the current scene the
moment it wakes, because it renders the last snapshot rather than replaying what
it missed.

**Reconnection is the absence of a feature.**
[`usePartyGameSnapshot`](../../frontend/src/party/usePartyGameSnapshot.ts) polls
every 2.5s, stops while the tab is hidden, and reads immediately on
`visibilitychange`, `focus` and `online`. A failed poll is not an error state:
the scene stays and a quiet status line says it may be a moment behind. Only a
request that has never succeeded produces a visible failure, and a `404` — this
party has no game — is a different, permanent fact.

**A refusal is the truth, not an error.** A vote refused because the host closed
voting between the tap and its arrival carries the state it was measured
against, so the phone moves to the waiting scene rather than showing an alert
and staying wrong.

Mobile rules, all enforced by the stylesheet rather than by discipline: the
stage owns the remaining viewport height and nothing scrolls during a vote; the
two answers are 4.5rem tall and pinned to the bottom of that space, so the thumb
travels the shortest distance to the decision and the question is never pushed
off screen; there is no hover dependency (the pressed state carries the
feedback); the chosen answer is filled **and** check-marked, so state never rests
on colour; and every edge respects `env(safe-area-inset-*)`.

There is no owner control on the page, and a test asserts the only two buttons
in the stage are the two answers.

## 12. Addendum — the television stage (SLICE 06)

`/party/:token/tv` — the same public token family, so a host reaches a screen by
opening a URL rather than by pairing a device, building an APK or publishing an
OTA. §5 explains why the stage is a web surface; the native TV app is untouched.

**It is a display.** There is not one button, link or focusable element on the
page, and a test asserts that across every scene. It only ever issues `GET`
requests, and it never joins — a display that minted a participant would inflate
the very count it is showing.

Scenes, all derived from the server's phase by the pure `stageScene()`:

| Scene | Phase | What it shows |
| --- | --- | --- |
| Lobby | `lobby` | the album, and a QR to join |
| Next activity | *(a beat)* | "Prossima attività" |
| Reveal | `challenge_reveal` | the canonical card, `mode="tv"` |
| Active | `challenge_active` | the same card plus the clock |
| Vote now | `voting_open` | "VOTA ORA", the question, `8 / 12 hanno votato` |
| Voting closed | `voting_closed` | participation, and nothing else |
| Result | `result` | "Il pubblico ha deciso" → `82%` → the verdict |
| Final | `finished` | a closing card |

**The between-rounds beat is presentation, not state.** It fires only on an
*observed* change of round, never on the first snapshot after mount — so a
television switched back on mid-reveal lands straight on the current scene
instead of replaying a flourish the room has already seen — and the snapshot
always wins: the moment the host moves past the reveal, the beat is over whether
or not its timer has run.

**No result exists in the vote scene's markup**, so it cannot leak into it. The
split reaches this screen only at `result`, which is what the server already
enforces.

Readability rules: every size is in `vh`, so 720p and 1080p produce the same
physical letters on the same panel; every scene sits inside a 3.5% safe area,
the same ratio the native app's `overscan()` uses; the verdict is a word, with
colour agreeing rather than carrying; and the clock uses tabular figures so it
does not shuffle as the seconds tick.

The result reveal is sequenced — headline, number, verdict — and the reduced
motion block zeroes the **delays** explicitly. The global reduced-motion rule
collapses durations but leaves delays alone, which would otherwise hold the
number off screen for a second and a half for exactly the people who asked for
less motion.

`PartyChallengeCard`'s `tv` mode now fills the box it is given rather than
claiming the viewport, so the stage can put chrome inside the same safe area.

## 13. Addendum — the control room (SLICE 07)

`/albums/:albumId/party-game`, inside the authenticated app shell, reached from
the album settings panel where the game is switched on. Preparing and conducting
are different jobs, so they are different surfaces: the deck stays in settings,
the evening is run from here.

**The state machine is quoted, not re-implemented.** The page renders
`snapshot.availableCommands` in the order the server sent it — the
phase-advancing command first — so an illegal command is **absent**, never
present-and-disabled, and a test asserts there is no disabled button on the page
at all. Adding a phase or an edge to the server changes this screen without a
line of TypeScript.

**Exactly one filled button.** The primary action carries `--accent-strong`;
skip is outline, finish is `.btn-danger` behind a `Modal` confirmation. On a
phone the command row is sticky to the bottom of the viewport, because a host is
holding the device while a room waits and the thing that moves the party on must
never be below the fold.

**Nothing is optimistic.** The phase on screen is always one the server has
committed to — a control room that shows "voting open" before the server agrees
lies to a host who is about to speak. A poll landing while a command is in
flight is dropped, so a pre-command snapshot cannot come back and re-arm a
version the server has already spent.

**A refusal is the recovery.** `409` carries the state it was measured against,
so a second tab, a second device or a double tap ends with this screen correct
*and* told what happened — one advance, one re-render, no follow-up fetch. A
test asserts no extra read is issued.

The room strip is always visible: guests connected, whether a screen is showing
the game (§ runtime — a display says so, the server does not guess), how far the
evening has got, and a QR for guests. The vote count is shown throughout; the
yes/no split appears only from `voting_closed`, quietly, because it is a fact
for the host to act on and the announcement belongs on the television.

Responsive: two columns above 900px (now / next), one column below, and the
sticky command row below 640px.
