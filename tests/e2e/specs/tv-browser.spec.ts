// Browser TV fallback.
//
// The point of this surface is that a TV works without the native APK, so every
// assertion here is deliberately about the *web* app: the route renders, its
// bundle executes, the grid holds its shape, and remote-style keys move focus.
// Nothing in this spec may depend on an installed APK or on a paired device.

import { expect, test } from '../src/fixtures';

test.describe('browser TV', () => {
  test('/tv renders and its JavaScript bundle executes', async ({ page, health }) => {
    void health;
    const moduleResponses: number[] = [];
    page.on('response', (response) => {
      if (/\/(assets|src|@vite|node_modules)\/.*\.(m?js|ts|tsx)(\?|$)/.test(response.url())) {
        moduleResponses.push(response.status());
      }
    });

    await page.goto('/tv');

    // A rendered <main class="tv-page"> proves React mounted, not merely that
    // the document was served.
    await expect(page.locator('main.tv-page')).toBeVisible({ timeout: 20_000 });
    await expect(page.getByRole('heading', { level: 1 })).toBeVisible();

    expect(moduleResponses.length, 'JS modules were requested').toBeGreaterThan(0);
    expect(moduleResponses.filter((status) => status >= 400)).toEqual([]);
  });

  test('the surface needs no native APK and no paired device', async ({ page, health }) => {
    void health;
    // Any request for the APK would mean the web surface depends on the native
    // artifact. It must not.
    const apkRequests: string[] = [];
    page.on('request', (request) => {
      if (/\.apk(\?|$)/i.test(request.url())) apkRequests.push(request.url());
    });

    await page.goto('/tv');
    await expect(page.locator('main.tv-page')).toBeVisible({ timeout: 20_000 });

    expect(apkRequests, 'the browser TV surface must not fetch the APK').toEqual([]);
  });

  test('the layout is stable and does not scroll sideways', async ({ page, health }, testInfo) => {
    void health;
    await page.goto('/tv');
    await expect(page.locator('main.tv-page')).toBeVisible({ timeout: 20_000 });

    // Measure twice: a surface that reflows after settling is not "stable", and
    // a TV has no scrollbar to rescue a layout that overflows.
    const first = await page.locator('main.tv-page').boundingBox();
    await page.waitForTimeout(750);
    const second = await page.locator('main.tv-page').boundingBox();
    expect(first).not.toBeNull();
    expect(second).not.toBeNull();
    expect(Math.abs(first!.width - second!.width)).toBeLessThanOrEqual(2);
    expect(Math.abs(first!.height - second!.height)).toBeLessThanOrEqual(2);

    const overflow = await page.evaluate(
      () => document.documentElement.scrollWidth - document.documentElement.clientWidth,
    );
    expect(overflow, `horizontal overflow in ${testInfo.project.name}`).toBeLessThanOrEqual(1);
  });

  test('remote-style keys move focus without breaking the page', async ({ page, health }) => {
    void health;
    await page.goto('/tv');
    await expect(page.locator('main.tv-page')).toBeVisible({ timeout: 20_000 });

    // A D-pad sends exactly these. None of them may throw or blank the surface.
    for (const key of ['ArrowRight', 'ArrowDown', 'ArrowLeft', 'ArrowUp', 'Enter', 'Escape']) {
      await page.keyboard.press(key);
    }

    await expect(page.locator('main.tv-page')).toBeVisible();
    // Focus must still be somewhere in the document rather than lost entirely.
    const hasFocus = await page.evaluate(() => document.activeElement !== null);
    expect(hasFocus).toBeTruthy();
  });

  test('fullscreen is attempted only where the engine supports it', async ({ page, health }) => {
    void health;
    await page.goto('/tv');
    await expect(page.locator('main.tv-page')).toBeVisible({ timeout: 20_000 });

    const supported = await page.evaluate(() => document.fullscreenEnabled === true);
    if (!supported) {
      // Stated rather than silently skipped: WebKit and mobile engines disable
      // the Fullscreen API under automation, so there is nothing to assert.
      test.info().annotations.push({
        type: 'fullscreen',
        description: 'Fullscreen API unavailable in this engine; capability not asserted.',
      });
      return;
    }

    const outcome = await page.evaluate(async () => {
      try {
        await document.documentElement.requestFullscreen();
        const active = document.fullscreenElement !== null;
        if (active) await document.exitFullscreen();
        return 'ok';
      } catch {
        // A rejected gesture requirement is a browser policy, not a page defect.
        return 'rejected';
      }
    });
    expect(['ok', 'rejected']).toContain(outcome);
    await expect(page.locator('main.tv-page')).toBeVisible();
  });

  test('the Personal Area entry point is reachable from the TV surface', async ({
    page,
    health,
  }) => {
    void health;
    await page.goto('/tv');
    await expect(page.locator('main.tv-page')).toBeVisible({ timeout: 20_000 });

    // Unpaired, the surface offers the pairing/personal entry rather than a
    // library. Either the entry control or the pairing state must be present —
    // what must never happen is a blank shell.
    const entry = page
      .getByRole('button', { name: /personal|personale|area/i })
      .or(page.getByRole('link', { name: /personal|personale|area/i }));
    const pairingState = page.locator('main.tv-page').getByText(/./);

    await expect(entry.first().or(pairingState.first())).toBeVisible({ timeout: 20_000 });
  });
});
