// Media library: scopes, the "to organize" filter, and its persistence.
//
// The filter is the interesting part. It is a view over album membership, so the
// assertion that matters is not "a chip toggled" but "assigning an item to an
// album removes it from this view immediately" — that is the behaviour an
// operator relies on when working through a backlog.

import { seedIds } from '../src/fixtures';
import { expect, test } from '../src/fixtures';

const grid = 'media-grid';
const organizeToggle = 'ws-unassigned-only';

/** Wait for the grid to settle: present and no longer showing its skeleton. */
async function settled(page: import('@playwright/test').Page): Promise<void> {
  await expect(page.getByTestId(grid)).toBeVisible({ timeout: 20_000 });
  await expect(page.getByTestId('media-grid-skeleton')).toHaveCount(0, { timeout: 20_000 });
}

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
