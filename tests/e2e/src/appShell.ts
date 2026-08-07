// Helpers for the application-shell scroll contract.
//
// Shared by shell.spec.ts and media-library.spec.ts because both ask the same
// question from different angles — who owns the vertical scrolling, and what
// stays put while it happens — and the measurement has to be identical for the
// two answers to be comparable.

import { expect, type Locator, type Page } from '@playwright/test';

/**
 * Viewport height used to make the media workspace taller than the box holding it.
 *
 * The seeded library is deliberately small: its fixtures exist to support specific
 * assertions, not to fill a screen. Rather than uploading dozens of throwaway
 * photos — which would push the seeded assets out of the virtualized window and
 * out of reach of the specs that locate them by name — the viewport is shortened
 * until the workspace genuinely overflows. What the contract is about is WHICH box
 * owns that overflow, not how many rows produced it.
 *
 * The WIDTH is always left at the project's own, so chromium-desktop,
 * chromium-mobile and chromium-desktop-zoom200 each keep exercising their real
 * responsive layout.
 */
export const PROBE_VIEWPORT_HEIGHT = 500;

export interface ShellScrollState {
  /** How far the application viewport is scrolled. */
  main: number;
  /** How much it has left to scroll — 0 means it is not the scroll owner. */
  mainOverflow: number;
  /** How far the browser document is scrolled. Must stay at 0. */
  document: number;
  /** How much the document could scroll. Must stay at 0. */
  documentOverflow: number;
  /** Horizontal document overflow, which must never appear at any width. */
  documentOverflowX: number;
}

export function shellScrollState(page: Page): Promise<ShellScrollState> {
  return page.evaluate(() => {
    const main = document.querySelector('[data-testid="app-main"]');
    const doc = document.scrollingElement;
    if (!main || !doc) throw new Error('the authenticated shell is not on screen');
    return {
      main: Math.round(main.scrollTop),
      mainOverflow: Math.round(main.scrollHeight - main.clientHeight),
      document: Math.round(doc.scrollTop),
      documentOverflow: Math.round(doc.scrollHeight - doc.clientHeight),
      documentOverflowX: Math.round(doc.scrollWidth - doc.clientWidth),
    };
  });
}

/**
 * The scroll state once it has stopped changing.
 *
 * The sidebar rail animates its width, and every frame of that animation re-flows
 * the justified wall to a new height — which the browser may answer by clamping the
 * scroll offset. Measuring mid-animation reads a transient value, so this waits for
 * two consecutive identical readings.
 */
export async function settledScrollState(page: Page): Promise<ShellScrollState> {
  let previous: ShellScrollState | null = null;
  await expect.poll(async () => {
    const current = await shellScrollState(page);
    const stable = previous !== null
      && current.main === previous.main
      && current.mainOverflow === previous.mainOverflow;
    previous = current;
    return stable;
  }, { message: 'the scroll state never settled' }).toBe(true);
  return previous!;
}

/** Scroll the application viewport, the way the shell means scrolling to happen. */
export async function scrollMain(page: Page, top: number): Promise<number> {
  return page.evaluate((to) => {
    const main = document.querySelector('[data-testid="app-main"]');
    if (!main) throw new Error('the authenticated shell is not on screen');
    main.scrollTop = to;
    return Math.round(main.scrollTop);
  }, top);
}

/** Wait for the media wall to be present and past its pre-measurement skeleton. */
export async function settledMedia(page: Page): Promise<void> {
  await expect(page.getByTestId('media-grid')).toBeVisible({ timeout: 20_000 });
  await expect(page.getByTestId('media-grid-skeleton')).toHaveCount(0, { timeout: 20_000 });
}

/**
 * Open /media on a viewport short enough that the workspace overflows it by at
 * least `minimumOverflow` pixels, and return the scroll distance available.
 *
 * The height is found rather than assumed. How much a five-item library overflows
 * depends on the project's width, its row-height band and how many rows the
 * justified layout settles on, so a single hard-coded height would be a guess that
 * happened to hold for one project. Each attempt is a real navigation, so what the
 * test then measures is a freshly laid-out page and not a mid-resize one.
 */
export async function openScrollableMedia(page: Page, minimumOverflow = 160): Promise<number> {
  const size = page.viewportSize();
  if (!size) throw new Error('the project defines no viewport size');

  let best = 0;
  for (const height of [PROBE_VIEWPORT_HEIGHT, 420, 340]) {
    await page.setViewportSize({ width: size.width, height });
    await page.goto('/media');
    await settledMedia(page);

    const state = await shellScrollState(page);
    // True at every height, and the premise of everything that follows: the
    // application viewport owns the overflow and the document has none.
    expect(state.documentOverflow, 'the document must not scroll vertically').toBeLessThanOrEqual(1);
    best = Math.max(best, state.mainOverflow);
    if (state.mainOverflow >= minimumOverflow) return state.mainOverflow;
  }
  throw new Error(
    `the media workspace never overflowed by ${minimumOverflow}px `
    + `(best ${best}px at width ${size.width}) — the seeded library may have shrunk`,
  );
}

/**
 * Whether the desktop sidebar layout is in force.
 *
 * Read from the DOM rather than from the viewport width, so the responsive
 * expectations follow the stylesheet's own breakpoint instead of a number
 * duplicated here.
 */
export function isDesktopShell(page: Page): Promise<boolean> {
  return page.getByTestId('app-sidebar').isVisible();
}

export interface Box { x: number; y: number; width: number; height: number }

/** The element's viewport rectangle, rounded so sub-pixel noise is not a failure. */
export async function boxOf(locator: Locator, what: string): Promise<Box> {
  const box = await locator.boundingBox();
  expect(box, `${what} has no bounding box`).not.toBeNull();
  return {
    x: Math.round(box!.x),
    y: Math.round(box!.y),
    width: Math.round(box!.width),
    height: Math.round(box!.height),
  };
}

/** Assert an element has not moved on screen (1px of rounding allowed). */
export function expectStationary(before: Box, after: Box, what: string): void {
  expect(Math.abs(after.y - before.y), `${what} moved vertically`).toBeLessThanOrEqual(1);
  expect(Math.abs(after.x - before.x), `${what} moved horizontally`).toBeLessThanOrEqual(1);
  expect(Math.abs(after.height - before.height), `${what} changed height`).toBeLessThanOrEqual(1);
}

/** Bottom edge of the sticky workspace chrome, or 0 where it does not stick. */
async function chromeBottom(page: Page): Promise<number> {
  const chrome = page.getByTestId('ws-sticky-chrome');
  if ((await chrome.count()) === 0) return 0;
  const box = await boxOf(chrome, 'the workspace chrome');
  return box.y + box.height;
}

/**
 * Scroll so one media tile is genuinely clickable, and return it.
 *
 * Two conditions, and neither is negotiable. A tile that is not FULLY inside the
 * window makes the browser scroll it into view before the click lands, and a tile
 * whose centre is under the sticky chrome cannot be clicked at all — either would
 * be measured afterwards as the gallery having moved by itself. On a short viewport
 * holding a small seeded library the row that satisfies both has to be placed
 * deliberately, so this computes the offset that drops a row's bottom edge onto the
 * window's and verifies the result rather than hoping.
 */
export async function scrollToClickableTile(
  page: Page,
  available: number,
): Promise<{ locator: Locator; index: number; box: Box; scrollTop: number }> {
  const bottom = page.viewportSize()!.height;
  const tiles = page.getByTestId('media-open');
  const total = await tiles.count();
  const start = await shellScrollState(page);
  const tried: string[] = [];

  for (let i = 0; i < total; i += 1) {
    const seen = await tiles.nth(i).boundingBox();
    if (!seen || seen.height >= bottom) continue;
    const wanted = Math.round(start.main + seen.y - (bottom - seen.height));
    if (wanted < 60 || wanted > available) { tried.push(`tile ${i} needs ${wanted}px`); continue; }

    await scrollMain(page, wanted);
    const box = await boxOf(tiles.nth(i), `tile ${i}`);
    const clear = await chromeBottom(page);
    if (box.y >= 0 && box.y + box.height <= bottom && box.y + box.height / 2 > clear + 8) {
      return { locator: tiles.nth(i), index: i, box, scrollTop: (await shellScrollState(page)).main };
    }
    tried.push(`tile ${i} at ${wanted}px lands ${box.y}..${box.y + box.height} under ${clear}`);
  }
  throw new Error(
    `no media tile could be scrolled into a clickable position in a ${bottom}px window `
    + `(${total} mounted, ${available}px available): ${tried.join('; ')}`,
  );
}

/**
 * The test id of whatever is painted at a point, walking up from the topmost
 * element. Used to prove a sticky region is really on top of the media rather
 * than merely still in the DOM underneath it.
 */
export function testIdAtPoint(page: Page, x: number, y: number): Promise<string | null> {
  return page.evaluate(([px, py]) => {
    let node = document.elementFromPoint(px, py);
    while (node) {
      const id = node.getAttribute('data-testid');
      if (id) return id;
      node = node.parentElement;
    }
    return null;
  }, [x, y] as const);
}
