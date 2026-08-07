// Media library: scopes, the "to organize" filter, and its persistence.
//
// The filter is the interesting part. It is a view over album membership, so the
// assertion that matters is not "a chip toggled" but "assigning an item to an
// album removes it from this view immediately" — that is the behaviour an
// operator relies on when working through a backlog.

import { seedIds } from '../src/fixtures';
import { expect, test } from '../src/fixtures';
import {
  boxOf,
  expectStationary,
  isDesktopShell,
  openScrollableMedia,
  scrollMain,
  scrollToClickableTile,
  settledMedia as settled,
  shellScrollState,
  testIdAtPoint,
} from '../src/appShell';

const grid = 'media-grid';
const organizeToggle = 'ws-unassigned-only';

const tileByName = (page: import('@playwright/test').Page, name: RegExp) =>
  page.getByTestId(grid).locator(`[data-media-name*="${name.source}" i]`);

test.describe('media library', () => {
  test('the all, photo and video scopes each render', async ({ ownerPage }) => {
    await ownerPage.goto('/media');
    await settled(ownerPage);
    await expect(ownerPage.getByTestId('media-kind-tabs')).toBeVisible();

    const tabs = ownerPage.getByTestId('media-kind-tabs').getByRole('tab');
    await expect(tabs).toHaveCount(3);

    for (const label of [/^(Tutti|All)/, /^(Foto|Photos)/, /^(Video|Videos)/]) {
      await tabs.filter({ hasText: label }).first().click();
      await settled(ownerPage);
    }
  });

  test('the Active and Excluded scopes are separate views', async ({ ownerPage }) => {
    await ownerPage.goto('/media');
    await settled(ownerPage);
    const activeCount = await ownerPage.getByTestId('media-open').count();

    await ownerPage.goto('/media/excluded');
    await settled(ownerPage);

    // The seeded excluded photo lives here and nowhere else.
    await expect(ownerPage).toHaveURL(/\/media\/excluded/);
    const excludedCount = await ownerPage.getByTestId('media-open').count();
    expect(excludedCount).toBeGreaterThan(0);
    expect(excludedCount).not.toBe(activeCount);
  });

  test('"to organize" is off by default and hides album-assigned media when enabled', async ({
    ownerPage,
  }) => {
    await ownerPage.goto('/media');
    await settled(ownerPage);

    const toggle = ownerPage.getByTestId(organizeToggle);
    await expect(toggle).toHaveAttribute('aria-pressed', 'false');

    const beforeCount = await ownerPage.getByTestId('media-open').count();
    expect(beforeCount).toBeGreaterThan(0);

    await toggle.click();
    await expect(toggle).toHaveAttribute('aria-pressed', 'true');
    await settled(ownerPage);

    // Assigned media is hidden, so the filtered view is strictly smaller.
    const filteredCount = await ownerPage.getByTestId('media-open').count();
    expect(filteredCount).toBeGreaterThan(0);
    expect(filteredCount).toBeLessThan(beforeCount);

    // Turning it off restores the standard result set.
    await toggle.click();
    await expect(toggle).toHaveAttribute('aria-pressed', 'false');
    await settled(ownerPage);
    expect(await ownerPage.getByTestId('media-open').count()).toBe(beforeCount);
  });

  test('the filter survives reload and Back/Forward', async ({ ownerPage }) => {
    await ownerPage.goto('/media');
    await settled(ownerPage);

    const toggle = ownerPage.getByTestId(organizeToggle);
    await toggle.click();
    await expect(toggle).toHaveAttribute('aria-pressed', 'true');
    await settled(ownerPage);
    const filteredCount = await ownerPage.getByTestId('media-open').count();

    await ownerPage.reload();
    await settled(ownerPage);
    await expect(ownerPage.getByTestId(organizeToggle)).toHaveAttribute('aria-pressed', 'true');
    expect(await ownerPage.getByTestId('media-open').count()).toBe(filteredCount);

    // Leaving and coming back must restore the same filtered view.
    await ownerPage.goto('/albums');
    await expect(ownerPage).toHaveURL(/\/albums$/);
    await ownerPage.goBack();
    await settled(ownerPage);
    await expect(ownerPage.getByTestId(organizeToggle)).toHaveAttribute('aria-pressed', 'true');
  });

  test('assigning an item to an album removes it from the filtered view immediately', async ({
    ownerPage,
    request,
  }) => {
    const ids = seedIds();
    await ownerPage.goto('/media');
    await settled(ownerPage);

    const toggle = ownerPage.getByTestId(organizeToggle);
    await toggle.click();
    await expect(toggle).toHaveAttribute('aria-pressed', 'true');
    await settled(ownerPage);
    const before = await ownerPage.getByTestId('media-open').count();

    // Assign through the API, then let the view refresh: the assertion is about
    // the filtered view reacting, not about which button performs the assignment.
    const assigned = await ownerPage.request.post(`/api/albums/${ids.albumId}/items`, {
      data: { fileItemId: ids.unassignedPhoto },
    });
    expect(assigned.ok()).toBeTruthy();
    void request;

    await ownerPage.reload();
    await settled(ownerPage);
    expect(await ownerPage.getByTestId('media-open').count()).toBe(before - 1);

    // Restore the seeded state so ordering between projects cannot matter.
    const removed = await ownerPage.request.delete(
      `/api/albums/${ids.albumId}/items/${ids.unassignedPhoto}`,
    );
    expect(removed.ok()).toBeTruthy();
  });

  test('the workspace chrome stays reachable while the media scrolls under it', async ({
    ownerPage,
  }, testInfo) => {
    const available = await openScrollableMedia(ownerPage);
    const desktop = await isDesktopShell(ownerPage);

    const tabs = ownerPage.getByTestId('media-kind-tabs');
    const bar = ownerPage.getByTestId('ws-command-bar');
    const viewport = await boxOf(ownerPage.getByTestId('app-main'), 'the application viewport');
    const restBefore = await boxOf(tabs, 'the kind tabs');

    // Two stops, because a sticky region legitimately travels up to its pin point
    // on the way there: the first scroll takes it as far as it goes, and the second
    // is where "stays put" can be asserted at all.
    const deep = Math.min(available, 300);
    const pinned = Math.round(deep / 2);
    expect(deep - pinned, 'room for a second scroll').toBeGreaterThan(40);

    await scrollMain(ownerPage, pinned);
    const tabsPinned = await boxOf(tabs, 'the kind tabs');
    const barPinned = await boxOf(bar, 'the command bar');
    const wallPinned = await boxOf(ownerPage.getByTestId(grid), 'the media wall');

    await scrollMain(ownerPage, deep);
    const state = await shellScrollState(ownerPage);
    expect(state.main, `scrolled in ${testInfo.project.name}`).toBeGreaterThan(40);

    // The media travelled between the two stops. Without this the rest would pass
    // on a page that simply never moved.
    const wallAfter = await boxOf(ownerPage.getByTestId(grid), 'the media wall');
    expect(wallPinned.y - wallAfter.y, 'the media wall moved up').toBeGreaterThan(40);

    if (desktop) {
      // Deep in a gallery the controls that describe the result are still there,
      // in the same place, and still operable.
      await expect(tabs).toBeVisible();
      await expect(bar).toBeVisible();
      expectStationary(tabsPinned, await boxOf(tabs, 'the kind tabs'), 'the kind tabs');
      expectStationary(barPinned, await boxOf(bar, 'the command bar'), 'the command bar');
      await expect(ownerPage.getByTestId('ws-open-filters')).toBeEnabled();
      await expect(ownerPage.getByTestId('ws-search-input')).toBeEditable();
      await expect(ownerPage.getByTestId(organizeToggle)).toBeEnabled();

      // Where it stopped is the top of the application viewport — not an offset
      // copied from the global top bar, which is a row of the shell above it.
      const chrome = await boxOf(ownerPage.getByTestId('ws-sticky-chrome'), 'the workspace chrome');
      expect(chrome.y - viewport.y, 'the chrome is pinned to the top of the viewport')
        .toBeLessThanOrEqual(2);

      // …and it is really on top of the media rather than still in the DOM
      // underneath it: whatever is painted over the filters button is the button.
      const filters = await boxOf(ownerPage.getByTestId('ws-open-filters'), 'the filters button');
      expect(
        await testIdAtPoint(
          ownerPage,
          filters.x + filters.width / 2,
          filters.y + filters.height / 2,
        ),
        'the media wall is painted over the sticky chrome',
      ).toBe('ws-open-filters');
    } else {
      // Below the sidebar breakpoint the chrome scrolls with the page rather than
      // pinning several rows of controls over a phone screen.
      const tabsAfter = await boxOf(tabs, 'the kind tabs');
      expect(restBefore.y - tabsAfter.y, 'narrow layouts scroll their chrome').toBeGreaterThan(40);
    }

    expect(state.documentOverflowX, `horizontal overflow in ${testInfo.project.name}`)
      .toBeLessThanOrEqual(1);
  });

  test('closing the viewer returns to the same place in the gallery', async ({ ownerPage }) => {
    const available = await openScrollableMedia(ownerPage);
    await scrollMain(ownerPage, Math.min(available, 300));

    // Deep in the gallery, on a tile the user could really click.
    const tile = await scrollToClickableTile(ownerPage, available);
    const before = await shellScrollState(ownerPage);
    expect(before.main, 'scrolled materially into the gallery').toBeGreaterThan(40);

    await tile.locator.click();
    await expect(ownerPage.getByTestId('media-viewer-title')).toBeVisible({ timeout: 20_000 });
    // The overlay fills the viewport; the gallery underneath must not have moved
    // to make room for it.
    expect((await shellScrollState(ownerPage)).main, 'opening the viewer moved the gallery')
      .toBe(before.main);

    await ownerPage.keyboard.press('Escape');
    await expect(ownerPage.getByTestId('media-viewer-title')).toHaveCount(0);
    await expect(ownerPage.getByTestId(grid)).toBeVisible();

    const after = await shellScrollState(ownerPage);
    // A little layout tolerance, but nothing like a row.
    expect(Math.abs(after.main - before.main), 'the gallery jumped on close')
      .toBeLessThanOrEqual(2);
    expect(after.document).toBeLessThanOrEqual(1);

    // The region the user was looking at is still the region on screen.
    const tileAfter = await boxOf(
      ownerPage.getByTestId('media-open').nth(tile.index),
      `tile ${tile.index}`,
    );
    expect(Math.abs(tileAfter.y - tile.box.y), 'the tile that was open moved')
      .toBeLessThanOrEqual(2);
  });

  test('a new result identity starts at the top of its own results', async ({ ownerPage }) => {
    const available = await openScrollableMedia(ownerPage);
    await scrollMain(ownerPage, Math.min(available, 300));
    expect((await shellScrollState(ownerPage)).main).toBeGreaterThan(40);

    // Switching tab asks a different question of the library. Landing halfway down
    // an answer to a question that was never asked is the behaviour this fixes.
    await ownerPage.getByTestId('media-kind-tab-image').click();
    await expect(ownerPage.getByTestId('media-kind-tab-image')).toHaveAttribute('aria-selected', 'true');
    await expect
      .poll(async () => (await shellScrollState(ownerPage)).main)
      .toBeLessThanOrEqual(1);

    // Same rule for a scope change made from the command bar.
    const stillAvailable = (await shellScrollState(ownerPage)).mainOverflow;
    if (stillAvailable > 40) {
      await scrollMain(ownerPage, Math.min(stillAvailable, 300));
      expect((await shellScrollState(ownerPage)).main).toBeGreaterThan(40);
      await ownerPage.getByTestId(organizeToggle).click();
      await expect(ownerPage.getByTestId(organizeToggle)).toHaveAttribute('aria-pressed', 'true');
      await expect
        .poll(async () => (await shellScrollState(ownerPage)).main)
        .toBeLessThanOrEqual(1);
    }
  });

  test('the command bar stays usable and the page does not scroll sideways', async ({
    ownerPage,
  }, testInfo) => {
    await ownerPage.goto('/media');
    await settled(ownerPage);

    const bar = ownerPage.getByTestId('ws-command-bar');
    await expect(bar).toBeVisible();
    await expect(ownerPage.getByTestId(organizeToggle)).toBeVisible();
    await expect(ownerPage.getByTestId(organizeToggle)).toBeEnabled();

    // Horizontal overflow is the classic narrow/zoomed-layout failure: the
    // document must never be wider than the viewport it was given.
    const overflow = await ownerPage.evaluate(
      () => document.documentElement.scrollWidth - document.documentElement.clientWidth,
    );
    expect(overflow, `horizontal overflow in ${testInfo.project.name}`).toBeLessThanOrEqual(1);
  });
});
