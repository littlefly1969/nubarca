// The album picker's primary action must be on the screen.
//
// This spec exists because a component test could not have caught the defect it
// guards. `album-picker-add` was in the DOM, enabled, and correctly wired the
// whole time — it was simply painted underneath the media workspace's selection
// bar, which is `position: fixed` at the bottom of the viewport with a z-index
// an order of magnitude above the generic overlay layer. With a short album list
// the modal was short enough that its footer cleared the bar; with a realistic
// list the modal grew to its 90dvh ceiling, the footer landed exactly where the
// bar sits, and the only way to reach Add was to filter the list down until the
// dialog shrank. `toBeInTheDocument()` is green through all of that.
//
// So every assertion here is about REAL GEOMETRY read from a real engine: where
// the boxes are, and which element a click at a given point would actually hit.

import { expect, test } from '../src/fixtures';
import { boxOf, settledMedia as settled, testIdAtPoint } from '../src/appShell';
import { login, post, del, type Session } from '../src/api';
import { OWNER } from '../src/env';

// Enough destinations that the list must scroll inside the modal on every
// viewport in the matrix — including the 200%-zoom desktop, whose body is only
// a few rows tall. The number is the point of the fixture: the bug is invisible
// at one album and certain at thirty.
const FILLER_ALBUMS = 30;
const FILLER_PREFIX = 'E2E Picker';

let session: Session;
let fillerAlbumIds: string[] = [];

test.beforeAll(async () => {
  session = await login(OWNER.email, OWNER.password);
  fillerAlbumIds = [];
  for (let i = 0; i < FILLER_ALBUMS; i += 1) {
    const album = await post<{ id: string }>(session, '/api/albums', {
      name: `${FILLER_PREFIX} ${String(i + 1).padStart(2, '0')}`,
    });
    fillerAlbumIds.push(album.id);
  }
});

test.afterAll(async () => {
  // Deleting the album removes its membership rows too, so the seeded library
  // is exactly as the other specs expect it — this file runs first.
  for (const id of fillerAlbumIds) {
    await del(session, `/api/albums/${id}`).catch(() => undefined);
  }
  fillerAlbumIds = [];
});

/** Select one media item and open the destination picker from the selection bar. */
async function openPicker(page: import('@playwright/test').Page) {
  await page.goto('/media');
  await settled(page);

  await page.getByTestId('media-select-control').first().click();
  await expect(page.getByTestId('media-selection-bar')).toBeVisible();

  await page.getByTestId('media-sel-album').click();
  await expect(page.getByTestId('album-picker')).toBeVisible();
  // The full destination set has loaded: the filler albums are all there.
  await expect
    .poll(() => page.getByTestId('album-picker-destination').count(), {
      message: 'the picker lists the filler albums',
      timeout: 15_000,
    })
    .toBeGreaterThan(10);
}

test.describe('album picker layout', () => {
  test('the Add action stays on screen with a long destination list', async ({ ownerPage }) => {
    const page = ownerPage;
    await openPicker(page);

    const viewport = page.viewportSize()!;
    const panel = await boxOf(page.getByTestId('album-picker'), 'the picker panel');
    const add = page.getByTestId('album-picker-add');

    // Present and painted — not merely mounted.
    await expect(add).toBeVisible();
    const addBox = await boxOf(add, 'the Add button');
    expect(addBox.width, 'the Add button has a real width').toBeGreaterThan(0);
    expect(addBox.height, 'the Add button has a real height').toBeGreaterThan(0);

    // Inside the viewport: this is what a 90dvh modal with an unbounded body
    // stops being true for.
    expect(addBox.y, 'the Add button starts below the top of the viewport')
      .toBeGreaterThanOrEqual(0);
    expect(
      addBox.y + addBox.height,
      `the Add button ends above the bottom of the ${viewport.height}px viewport`,
    ).toBeLessThanOrEqual(viewport.height + 1);

    // …and inside the modal it belongs to, rather than spilling out of it.
    expect(addBox.y, 'the Add button is inside the panel').toBeGreaterThanOrEqual(panel.y - 1);
    expect(addBox.y + addBox.height, 'the Add button is inside the panel')
      .toBeLessThanOrEqual(panel.y + panel.height + 1);

    // The decisive one. A click at the centre of the Add button must land ON the
    // Add button. Before the fix this resolved to `media-selection-bar`, which is
    // precisely why the operator could not press it.
    expect(
      await testIdAtPoint(page, addBox.x + addBox.width / 2, addBox.y + addBox.height / 2),
      'something is painted over the Add button',
    ).toBe('album-picker-add');
  });

  test('the selection bar sits behind the picker, not over its footer', async ({ ownerPage }) => {
    const page = ownerPage;
    await openPicker(page);

    const add = await boxOf(page.getByTestId('album-picker-add'), 'the Add button');
    const bar = page.getByTestId('media-selection-bar');

    // The bar is deliberately NOT unmounted while the picker is open — hiding it
    // with React state would be working around the stacking order instead of
    // fixing it. It stays in the DOM, below the backdrop.
    await expect(bar).toBeAttached();
    const barBox = await boxOf(bar, 'the selection bar');

    // Wherever the two rectangles overlap, the picker is the one being painted.
    const overlaps =
      add.x < barBox.x + barBox.width && barBox.x < add.x + add.width
      && add.y < barBox.y + barBox.height && barBox.y < add.y + add.height;
    if (overlaps) {
      const corners: Array<[number, number]> = [
        [add.x + 2, add.y + 2],
        [add.x + add.width - 2, add.y + 2],
        [add.x + 2, add.y + add.height - 2],
        [add.x + add.width - 2, add.y + add.height - 2],
        [add.x + add.width / 2, add.y + add.height / 2],
      ];
      for (const [x, y] of corners) {
        expect(
          await testIdAtPoint(page, x, y),
          `the selection bar is painted over the Add button at ${Math.round(x)},${Math.round(y)}`,
        ).not.toBe('media-selection-bar');
      }
    }
  });

  test('only the destination list scrolls; header and footer stay put', async ({ ownerPage }) => {
    const page = ownerPage;
    await openPicker(page);

    const destinations = page.getByTestId('album-picker-destination');
    // The overlay's own header, not the search field: search is body content and
    // is SUPPOSED to scroll away with the list. What must never move is the
    // chrome — the title row you close from, and the footer you act from.
    const header = page.getByTestId('album-picker-close');
    const add = page.getByTestId('album-picker-add');

    const headerBefore = await boxOf(header, 'the dialog header');
    const footerBefore = await boxOf(add, 'the Add button');
    const firstBefore = await boxOf(destinations.first(), 'the first destination');

    // Drive the body to its end the way a user would.
    await destinations.last().scrollIntoViewIfNeeded();

    // The list really moved — otherwise everything below would pass on a fixture
    // that never overflowed in the first place.
    const firstAfter = await boxOf(destinations.first(), 'the first destination');
    expect(
      firstBefore.y - firstAfter.y,
      'the destination list scrolled inside the modal',
    ).toBeGreaterThan(40);

    // …while the chrome did not. If the whole modal were the scroll container,
    // both of these would have travelled with it.
    const headerAfter = await boxOf(header, 'the dialog header');
    const footerAfter = await boxOf(add, 'the Add button');
    expect(Math.abs(headerAfter.y - headerBefore.y), 'the header stayed put').toBeLessThanOrEqual(2);
    expect(Math.abs(footerAfter.y - footerBefore.y), 'the footer stayed put').toBeLessThanOrEqual(2);

    await expect(add).toBeVisible();
    expect(
      await testIdAtPoint(page, footerAfter.x + footerAfter.width / 2, footerAfter.y + footerAfter.height / 2),
      'the Add button is unreachable at the bottom of the list',
    ).toBe('album-picker-add');
  });

  test('choosing a destination enables Add, and pressing it files the selection', async ({
    ownerPage,
  }) => {
    const page = ownerPage;
    await openPicker(page);

    const add = page.getByTestId('album-picker-add');
    // Visible but refusing, before anything is chosen — never absent.
    await expect(add).toBeVisible();
    await expect(add).toBeDisabled();

    const target = page
      .getByTestId('album-picker-destination')
      .filter({ hasText: `${FILLER_PREFIX} 01` })
      .first();
    await target.scrollIntoViewIfNeeded();
    await target.click();

    await expect(add).toBeVisible();
    await expect(add).toBeEnabled();

    // The real button, clicked at its real position, produces the real request.
    const bulk = page.waitForResponse(
      (response) =>
        /\/api\/albums\/[0-9a-f-]+\/items\/bulk$/.test(new URL(response.url()).pathname)
        && response.request().method() === 'POST',
      { timeout: 15_000 },
    );
    await add.click();
    const response = await bulk;
    expect(response.status(), 'the bulk add succeeded').toBe(200);
    const body = (await response.json()) as { succeeded: number };
    expect(body.succeeded, 'one item was filed').toBe(1);

    // The workspace takes it from there: the picker closes and the selection is
    // spent. (Both are already covered by component tests; asserted here only to
    // show the real click reached the real handler.)
    await expect(page.getByTestId('album-picker')).toHaveCount(0);
    await expect(page.getByTestId('media-selection-bar')).toHaveCount(0);
  });
});
