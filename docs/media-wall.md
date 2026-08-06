# Justified media wall (frontend)

The media library (`/media`, `/media/excluded`) and the album workspace render
their photos and videos as a **full-width, justified media wall** — Flickr/Google
Photos style rows rather than a fixed square-card grid. This document covers the
frontend architecture; the derivative dimensions it consumes are defined in
[media-derivatives.md](media-derivatives.md).

## Full-width shell (media pages only)

The app shell (`Layout`) is normally centred at `max-width: 64rem`. Media pages
opt into a full-width `main` **explicitly**, not by pathname sniffing (which is
fragile: `/albums` the list vs `/albums/:id` the workspace). `MediaWorkspace`
calls `useMediaWallLayout()` (`components/mediaWallLayout.ts`), which flips a
context flag owned by `Layout`; the shell then adds `.app-main--media`
(`max-width: none; width: 100%; padding-inline: 10px`). Any page that renders
`MediaWorkspace` becomes full-width; `SimilarPhotosExplorerPage` (its own
`gallery-card` markup) and every admin/settings/form page stay centred.

## Justified layout algorithm

`media/layout/computeJustifiedRows.ts` is a pure, framework-free helper (no React,
no backend coupling). Given items `{ id, originalIndex, aspectRatio }` and
`{ containerWidth, gap, targetRowHeight, minRowHeight, maxRowHeight }` it returns
rows whose tiles keep their aspect ratio and, for every **full** row, span the
container width exactly (`Σ tile widths + Σ gaps = containerWidth`, the rounding
residual pushed onto the last tile).

- A row closes as soon as filling the width would no longer overshoot
  `targetRowHeight`; panoramic content therefore yields short rows that still
  fill the width.
- The **last** row is left-aligned at the target height when it is clearly
  incomplete (its natural fill height would exceed `maxRowHeight`), and justified
  otherwise — no stretching a couple of tiles across the whole width.
- Missing / zero / negative / non-finite aspect ratios fall back to a square, so
  a tile is never negative-width or zero-height.
- Order is always preserved; `originalIndex` maps a tile back to the source array
  so the viewer and selection stay index-correct.

**Photos and videos both use their REAL pixel ratio** — a vertical video is a
vertical tile, never forced to 16:9. The ratio comes from
`media/workspace/mediaAspectRatio.ts` (`getMediaAspectRatio` /
`normalizeAspectRatio`): the DTO's declared `width`/`height`, clamped to a prudent
`[0.35, 3.5]` band, falling back to **1:1 for photos** and **16:9 for videos**
(`VIDEO_TILE_ASPECT_RATIO`) only when the dimensions are missing/invalid. The tile
shape is derived **exclusively from the DTO** — never from the loaded
thumbnail/poster — so a tile keeps its shape from first paint and never reflows
when the image arrives.

> Video posters are generated at **source aspect ratio** (see
> [media-derivatives.md](media-derivatives.md)), and the `/api/media` DTO reports
> the video's **display** dimensions (rotation applied — a coded-landscape phone
> clip with a 90°/270° matrix reports portrait), so the tile shape matches the
> autorotated poster.

Row-height bands are responsive (desktop 230/180/280, tablet 190/155/235, mobile
150/120/185; gap 6 px). A `useLayoutEffect` + `ResizeObserver` drive
`containerWidth`, which is `null` until a real (`> 0`) measurement arrives: **no
rows are laid out against an invented width**, so there is no first-paint reflow.
While unmeasured the wall shows a stable skeleton; sub-pixel (`< 1px`) width
changes are ignored to avoid layout thrash. A real resize recomputes rows without
refetching, and without touching items or selection.

## Virtualization

`MediaGrid` virtualizes **rows** (not tiles) with `@tanstack/react-virtual`'s
window virtualizer: one virtual element per justified row, its size the exact row
height plus the inter-row gap (dropped on the last row), overscan 4. The library
is never fully rendered; the viewer index and selection reference the original
array, never the virtual row index. Infinite scroll is owned by the parent
(`MediaWorkspace`'s sentinel) — the wall only lays out and virtualizes what it is
given, so a new page appends without a scroll jump.

## Tile & overlay

A tile is not a card: no border, padding, background panel, permanent metadata
block, or 1:1 aspect. It is exactly the size the layout computed and holds two
**sibling** (never nested) buttons — an open button covering the media and a
selection control in the corner:

- **Photos** use the `small` derivative in a preview frame: a **contained**
  foreground (`object-fit: contain` — the whole photo, never cropped) over an
  `aria-hidden` **blurred cover backdrop** built from the *same* URL (one HTTP
  request — the browser reuses the decoded resource). The tile is the photo's real
  ratio, so the foreground fills it and the backdrop only shows on a justified
  row's last-tile rounding sliver. A load error shows a discreet placeholder that
  keeps the tile geometry.
- **Videos** reuse `VideoPreview` with `fit="contain"`: the poster (now
  source-aspect) is shown whole over the same blurred-backdrop stage; the
  six-frame strip is requested only after a deliberate ~300 ms hover/focus,
  animated purely in CSS. The strip cells are still produced on a 16:9 stage, so
  the hover animation of a non-16:9 video is briefly letterboxed inside its tile
  (the static poster is always correct). Duration and a discreet video badge are
  always visible; `prefers-reduced-motion` keeps the poster and skips the strip
  animation.
- **Overlay** (name + `resolution · size`, duplicate badge when count > 1)
  appears on `:hover`, `:focus-within`, and `[data-selected="true"]` via an
  opacity transition — no layout shift, no permanent text under the image.
- **Selection** is unchanged semantics (single, Ctrl/Cmd, Shift range); the
  control shows on hover/focus, always when selected, and always on touch
  (`@media (hover: none)`); the selected tile gets an **inner** ring
  (`box-shadow: inset …`) that never changes its geometry.

## Why no new `grid` size, and no URL versioning

The wall renders the existing `small` derivative (raised to a 768 px max edge) —
**no** new `grid` derivative is introduced, and the logical names, URLs,
directories, and query strings are unchanged (no `v2`, no cache-busting query
string). A later in-place regeneration can therefore replace the bytes behind the
same `small`/`poster`/`video-preview-strip` derivative without any frontend
change.

## Cross-boundary constant

Only one value crosses to the browser: `VIDEO_PREVIEW_FRAME_COUNT`
(`media/mediaDerivativeSpec.ts`), matching the backend strip frame count. The
strip is addressed in cell fractions (`background-size: N×100%`), so the
per-frame pixel size never needs to cross — only the count.
