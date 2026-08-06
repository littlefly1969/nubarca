// Semantic marker strip.
//
// SCOPE. These tests verify the FRONTEND MARKER ENVELOPE: that a video result
// exposes its matching moments as markers, that they are ordered and placed by
// timestamp, that all three activation paths work, and that activating one hands
// the exact timestamp to the viewer.
//
// They do NOT verify semantic RANKING. The stack runs the deterministic AI
// backend, whose embeddings are reproducible but carry no meaning, so "is this
// the most relevant result" is not a question these fixtures can answer. Backend
// ranking is covered by the backend suite.

import { expect, test } from '../src/fixtures';
import type { Page } from '@playwright/test';

const STRIP = 'semantic-marker-strip';

/**
 * Run a semantic query.
 *
 * The command bar's search box is a TEXT filter over names — it is not this. The
 * semantic query is the visual-similarity field in the filter sheet, which is
 * what sets a non-zero semanticTopK and switches the workspace to
 * /api/media/semantic.
 */
async function search(page: Page, query: string): Promise<void> {
  await page.goto('/media');
  await expect(page.getByTestId('ws-command-bar')).toBeVisible({ timeout: 20_000 });

  await page.getByTestId('ws-open-filters').click();
  const visual = page.getByTestId('filter-visual');
  await expect(visual).toBeVisible({ timeout: 10_000 });
  await visual.fill(query);
  await page.getByTestId('filter-apply').click();

  await expect(page.getByTestId('media-grid')).toBeVisible({ timeout: 30_000 });
  await expect(page.getByTestId('media-grid-skeleton')).toHaveCount(0, { timeout: 30_000 });
}

/**
 * The tile for one seeded file.
 *
 * The grid does not put the file id in the DOM, so a tile is located the way a
 * user identifies it: by the accessible name of its open control, which carries
 * the display name. Seeded names are deterministic, so this is stable.
 */
const tile = (page: Page, displayName: string) =>
  page
    .getByTestId('media-grid')
    .locator('[data-kind]')
    .filter({ has: page.locator(`[data-testid="media-open"][aria-label*="${displayName}" i]`) })
    .first();

const PHOTO = 'e2e-unorganized-1.jpg';
const VIDEO = 'e2e-video-unassigned.mp4';

/**
 * The open viewer's playback position, or null when the engine never decoded the
 * video. Headless Linux builds of WebKit and Firefox frequently ship without the
 * h264 decoder, and a test that treated that as 0 would quietly assert nothing.
 */
async function seekPosition(page: Page): Promise<number | null> {
  // Scope to the VIEWER's video. The grid renders its own hover-preview videos,
  // and picking one of those measured a tile animation instead of the viewer.
  const handle = page.getByRole('dialog').locator('video').first();
  if ((await handle.count()) === 0) return null;
  try {
    return await handle.evaluate(async (element) => {
      const video = element as HTMLVideoElement;
      // Wait only for metadata. Waiting for currentTime > 0 would be wrong: an
      // autoplaying video drifts, so "did it start at the beginning?" would
      // measure how long the wait took rather than where the viewer opened.
      for (let i = 0; i < 40; i += 1) {
        if (video.readyState >= 1) break;
        await new Promise((resolve) => setTimeout(resolve, 100));
      }
      if (video.readyState < 1) return null;
      // Give an applied seek a moment to settle, then freeze before measuring.
      await new Promise((resolve) => setTimeout(resolve, 300));
      video.pause();
      return video.currentTime;
    });
  } catch {
    return null;
  }
}

test.describe('semantic markers', () => {
  test('a photo result carries no marker strip', async ({ ownerPage }) => {
    await search(ownerPage, 'colour');

    const photo = tile(ownerPage, PHOTO);
    await expect(photo).toBeVisible();
    // A photo has no timestamped evidence, so there is nothing to mark.
    await expect(photo.getByTestId(STRIP)).toHaveCount(0);
  });

  test('a video result exposes two markers, chronologically, at 25% and 75%', async ({
    ownerPage,
  }) => {
    await search(ownerPage, 'colour');

    const video = tile(ownerPage, VIDEO);
    await expect(video).toBeVisible();

    const strip = video.getByTestId(STRIP);
    await expect(strip).toBeVisible();
    await expect(strip).toHaveAttribute('role', 'group');

    const markers = strip.getByRole('button');
    await expect(markers).toHaveCount(2);

    // The seeded 12 s video matches at 3000 ms and 9000 ms, so the markers must
    // sit at a quarter and three quarters of the timeline, in that order.
    const offsets: number[] = [];
    const stripBox = await strip.boundingBox();
    expect(stripBox).not.toBeNull();
    for (let i = 0; i < 2; i += 1) {
      const box = await markers.nth(i).boundingBox();
      expect(box).not.toBeNull();
      const centre = box!.x + box!.width / 2 - stripBox!.x;
      offsets.push(centre / stripBox!.width);
    }

    expect(offsets[0]).toBeLessThan(offsets[1]); // chronological
    expect(offsets[0]).toBeCloseTo(0.25, 1);
    expect(offsets[1]).toBeCloseTo(0.75, 1);
  });

  test('the best match is distinguishable to assistive technology', async ({ ownerPage }) => {
    await search(ownerPage, 'colour');

    const strip = tile(ownerPage, VIDEO).getByTestId(STRIP);
    await expect(strip).toBeVisible();

    // Exactly one marker is the best match, and the distinction is exposed
    // through aria-pressed rather than by colour alone.
    await expect(strip.getByTestId('semantic-marker-best')).toHaveCount(1);
    await expect(strip.getByTestId('semantic-marker-best')).toHaveAttribute(
      'aria-pressed',
      'true',
    );
    await expect(strip.getByTestId('semantic-marker')).toHaveCount(1);
    await expect(strip.getByTestId('semantic-marker')).toHaveAttribute('aria-pressed', 'false');
  });

  for (const activation of ['pointer', 'Enter', 'Space'] as const) {
    test(`activating a marker by ${activation} hands off its exact timestamp`, async ({
      ownerPage,
    }) => {
      await search(ownerPage, 'colour');

      const strip = tile(ownerPage, VIDEO).getByTestId(STRIP);
      await expect(strip).toBeVisible();

      // The earliest marker. Its exact timestamp is published on the element the
      // viewer consumes, so assert the handoff VALUE directly — 3000 ms, not a
      // rounded label.
      const marker = strip.getByRole('button').first();
      await expect(marker).toHaveAttribute('data-ms', '3000');

      if (activation === 'pointer') {
        await marker.click();
      } else {
        await marker.focus();
        await marker.press(activation === 'Enter' ? 'Enter' : ' ');
      }

      // The viewer opened, and it opened from the marker rather than the tile.
      await expect(ownerPage.getByTestId('media-viewer-title')).toBeVisible({ timeout: 20_000 });

      // Where the engine actually decoded the video, prove the seek landed on
      // the marker's moment. Headless WebKit/Firefox may not decode h264 at all,
      // so a non-decoding engine is reported rather than silently passing.
      const seek = await seekPosition(ownerPage);
      if (seek === null) {
        test.info().annotations.push({
          type: 'seek',
          description: 'engine did not decode the video; exact seek not asserted here',
        });
      } else {
        expect(seek, 'viewer opened at the marker moment').toBeGreaterThan(2);
        expect(seek).toBeLessThan(4.5);
      }
    });
  }

  test('an ordinary open uses the best match, not the marker that was clicked', async ({
    ownerPage,
  }) => {
    await search(ownerPage, 'colour');

    const video = tile(ownerPage, VIDEO);
    await expect(video.getByTestId(STRIP)).toBeVisible();

    // Opening a semantic result WITHOUT choosing a marker is defined to land on
    // the best match (9000 ms), not on the earliest marker (3000 ms). That is the
    // distinction the marker strip exists to offer, so assert it rather than
    // assuming an unseeked open.
    await video.getByTestId('media-open').first().click();
    await expect(ownerPage.getByTestId('media-viewer-title')).toBeVisible({ timeout: 20_000 });

    const seek = await seekPosition(ownerPage);
    if (seek === null) {
      test.info().annotations.push({
        type: 'seek',
        description: 'engine did not decode the video; best-match open not asserted here',
      });
    } else {
      expect(seek, 'an ordinary open lands on the best match').toBeGreaterThan(7);
      expect(seek).toBeLessThan(11);
    }
  });

  test('in an ordinary gallery a tile opens from the beginning', async ({ ownerPage }) => {
    await ownerPage.goto('/media');
    await expect(ownerPage.getByTestId('media-grid')).toBeVisible({ timeout: 20_000 });
    await expect(ownerPage.getByTestId('media-grid-skeleton')).toHaveCount(0, { timeout: 20_000 });

    // No semantic evidence here, so there is nothing to seek to: ordinary open
    // behaviour is unchanged by the marker feature existing.
    const video = tile(ownerPage, VIDEO);
    await expect(video).toBeVisible();
    await expect(video.getByTestId(STRIP)).toHaveCount(0);

    await video.getByTestId('media-open').first().click();
    await expect(ownerPage.getByTestId('media-viewer-title')).toBeVisible({ timeout: 20_000 });

    const seek = await seekPosition(ownerPage);
    if (seek === null) {
      test.info().annotations.push({
        type: 'seek',
        description: 'engine did not decode the video; start position not asserted here',
      });
    } else {
      expect(seek, 'an ordinary gallery open starts at the beginning').toBeLessThan(1.5);
    }
  });

  test('an ordinary gallery has no markers at all', async ({ ownerPage }) => {
    await ownerPage.goto('/media');
    await expect(ownerPage.getByTestId('media-grid')).toBeVisible({ timeout: 20_000 });
    await expect(ownerPage.getByTestId('media-grid-skeleton')).toHaveCount(0, { timeout: 20_000 });

    // No semantic query, so no evidence and no strips anywhere in the grid.
    await expect(ownerPage.getByTestId(STRIP)).toHaveCount(0);
    await expect(ownerPage.getByTestId('ws-semantic-notice')).toHaveCount(0);
  });
});
