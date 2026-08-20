// Responsive geometry for the TV panel's FIXED editors — keyboard, numeric pad,
// date pad. Pure, node-testable, no React.
//
// THE PROBLEM THIS REPLACES
// -------------------------
// Every fixed editor was written with constant paddings and key sizes chosen
// against one screen, then dropped inside PanelShell's unconditional
// ScrollView. Where the constants were too big for the actual viewport the
// panel simply scrolled: the title stayed put, the bottom key row went
// off-screen, and on a television that is not a minor cosmetic issue — the
// focusable that fell off the bottom is a control the remote can reach but the
// viewer cannot see.
//
// A scroll is the WRONG answer for a bounded editor. A key grid has a known
// number of rows; it should be sized to fit, not made scrollable to hide that
// it does not. So the geometry is computed from the real viewport and the
// result carries `fits`, which the tests assert at the real TV sizes rather
// than trusting a constant that happened to work once.
//
// Units are React Native dp (what useWindowDimensions reports), so this is
// resolution-independent by construction: 1920x1080 at 2x and 1280x720 at 2x
// arrive here as different dp boxes and both must fit.

export interface Viewport {
  width: number;
  height: number;
}

export interface FixedEditorRequest {
  /** Rows in the key grid. */
  rows: number;
  /** Largest number of keys in any row — drives key width. */
  columns: number;
  /** Lines of chrome above the grid (title, current value, hint). */
  headerLines: number;
  /** Rows of actions below the grid (OK/Cancel/Clear...). */
  actionRows: number;
}

export interface FixedEditorLayout {
  keyHeight: number;
  keyWidth: number;
  gap: number;
  fontSize: number;
  headerHeight: number;
  actionHeight: number;
  /** Total height the editor will occupy. */
  contentHeight: number;
  /** True when contentHeight is within the usable viewport. */
  fits: boolean;
}

// TV overscan: the same ~3.5% per edge the theme's `overscan()` reserves, plus
// the panel's own padding. Content outside this is not reliably visible on a
// consumer television, so it is not usable height.
const OVERSCAN_RATIO = 0.035;

// A key smaller than this cannot be read from a sofa; larger than this wastes a
// 1080p panel. Between them everything scales with the viewport — deliberately
// NOT one constant chosen against one screen, which is how the bottom key row
// ended up off-screen in the first place.
const MIN_KEY = 28;
const MAX_KEY = 64;
const MIN_GAP = 4;
const MAX_GAP = 12;

function clamp(value: number, low: number, high: number): number {
  return Math.max(low, Math.min(high, value));
}

/** Chrome heights scale with the panel too, or they eat a small viewport. */
function chrome(height: number): { title: number; headerLine: number; actionRow: number; padding: number } {
  return {
    title: clamp(Math.round(height / 14), 40, 60),
    headerLine: clamp(Math.round(height / 22), 24, 44),
    actionRow: clamp(Math.round(height / 14), 40, 60),
    padding: clamp(Math.round(height / 30), 12, 28),
  };
}

export function usableHeight(viewport: Viewport): number {
  const overscan = Math.max(16, Math.round(viewport.height * OVERSCAN_RATIO));
  const c = chrome(viewport.height);
  return Math.max(0, viewport.height - 2 * overscan - 2 * c.padding - c.title);
}

export function usableWidth(viewport: Viewport): number {
  const overscan = Math.max(24, Math.round(viewport.width * OVERSCAN_RATIO));
  return Math.max(0, viewport.width - 2 * overscan);
}

/**
 * Size a fixed editor to the viewport it will actually be shown in.
 *
 * The grid is what flexes: header and actions are text chrome with their own
 * legibility floor, so the key rows absorb the difference. `fits` is REPORTED
 * rather than silently assumed, so a test can tell "sized down" from "does not
 * fit at all" — the second being the state that used to hide behind a scroll.
 */
export function fixedEditorLayout(
  viewport: Viewport,
  request: FixedEditorRequest,
): FixedEditorLayout {
  const height = usableHeight(viewport);
  const width = usableWidth(viewport);
  const c = chrome(viewport.height);

  const headerHeight = request.headerLines * c.headerLine;
  const actionHeight = request.actionRows * c.actionRow;
  const gridBudget = Math.max(0, height - headerHeight - actionHeight);

  const gap = clamp(Math.round(viewport.height / 90), MIN_GAP, MAX_GAP);
  const rows = Math.max(1, request.rows);
  // Every row carries one gap below it.
  const perRow = Math.floor(gridBudget / rows) - gap;
  const keyHeight = clamp(perRow, MIN_KEY, MAX_KEY);

  const columns = Math.max(1, request.columns);
  const keyWidth = Math.max(
    MIN_KEY,
    Math.min(MAX_KEY * 1.4, Math.floor((width - gap * (columns - 1)) / columns)),
  );

  const contentHeight = headerHeight + actionHeight + rows * (keyHeight + gap);
  return {
    keyHeight,
    keyWidth,
    gap,
    // Readable from a sofa: roughly half the key box, bounded.
    fontSize: clamp(Math.round(keyHeight * 0.5), 15, 30),
    headerHeight,
    actionHeight,
    contentHeight,
    fits: contentHeight <= height,
  };
}

// The two TV viewports this must be correct at, in dp. Fire TV panels report dp
// rather than physical pixels, and both the common 720p and 1080p
// configurations land here.
export const TV_VIEWPORTS: readonly Viewport[] = [
  { width: 1280, height: 720 },
  { width: 1920, height: 1080 },
  // The same panels as React Native reports them at 2x density.
  { width: 640, height: 360 },
  { width: 960, height: 540 },
];
