// Application shell: can a seeded owner sign in, does the shell survive the
// browser controls users actually press, and does it own the scrolling?
//
// Reload plus Back/Forward are here because they are where SPA session handling
// breaks: a shell that works on first paint can still lose auth state on a hard
// reload, or restore a stale view from the history cache.
//
// The scroll-ownership block asserts BEHAVIOUR, never a computed style: the top
// bar and the sidebar do not move while the content does, and the browser document
// stays exactly where it started. A `position: sticky` read out of the CSSOM would
// have passed against the old shell too, which is precisely the arrangement this
// replaces.

import { OWNER } from '../src/env';
import { expect, signIn, test } from '../src/fixtures';
import {
  boxOf,
  expectStationary,
  isDesktopShell,
  openScrollableMedia,
  scrollMain,
  settledScrollState,
  shellScrollState,
} from '../src/appShell';

test.describe('application shell', () => {
  test('a seeded owner can sign in and reach the authenticated shell', async ({ page, health }) => {
    void health;
    await page.goto('/login');
    await expect(page.locator('form.login-card')).toBeVisible();

    await signIn(page, OWNER.email, OWNER.password);

    // Landing on anything other than /login means the session was established.
    await expect(page).not.toHaveURL(/\/login/);
    await expect(page.locator('#email')).toHaveCount(0);
  });

  test('the authentication state endpoint agrees with the browser session', async ({
    ownerPage,
  }) => {
    const me = await ownerPage.request.get('/api/auth/me');
    expect(me.status()).toBe(200);
    const body = (await me.json()) as { email?: string };
    expect(body.email?.toLowerCase()).toBe(OWNER.email);
  });

  test('a hard reload keeps the owner signed in', async ({ ownerPage }) => {
    await ownerPage.goto('/media');
    await expect(ownerPage.getByTestId('ws-command-bar')).toBeVisible();

    await ownerPage.reload();

    await expect(ownerPage).toHaveURL(/\/media/);
    await expect(ownerPage.getByTestId('ws-command-bar')).toBeVisible();
    await expect(ownerPage).not.toHaveURL(/\/login/);
  });

  test('Back and Forward restore the visited views', async ({ ownerPage }) => {
    await ownerPage.goto('/media');
    await expect(ownerPage.getByTestId('ws-command-bar')).toBeVisible();

    await ownerPage.goto('/albums');
    await expect(ownerPage).toHaveURL(/\/albums$/);

    await ownerPage.goBack();
    await expect(ownerPage).toHaveURL(/\/media$/);
    await expect(ownerPage.getByTestId('ws-command-bar')).toBeVisible();

    await ownerPage.goForward();
    await expect(ownerPage).toHaveURL(/\/albums$/);
  });

  test('the application viewport owns the vertical scrolling, not the document', async ({
    ownerPage,
  }, testInfo) => {
    const available = await openScrollableMedia(ownerPage);

    const topbar = await boxOf(ownerPage.getByTestId('app-topbar'), 'the top bar');
    const desktop = await isDesktopShell(ownerPage);
    const sidebarBefore = desktop
      ? await boxOf(ownerPage.getByTestId('app-sidebar'), 'the sidebar')
      : null;
    const contentBefore = await boxOf(ownerPage.getByTestId('media-grid'), 'the media wall');

    const target = Math.min(available, 300);
    const reached = await scrollMain(ownerPage, target);
    expect(reached, `scrolled in ${testInfo.project.name}`).toBeGreaterThan(40);

    const state = await shellScrollState(ownerPage);
    expect(state.main, 'the application viewport scrolled').toBeGreaterThan(40);
    expect(state.document, 'the document must stay where it was').toBeLessThanOrEqual(1);
    expect(state.documentOverflowX, 'no horizontal document overflow').toBeLessThanOrEqual(1);

    // The chrome is stationary because it is a row of the shell, not because it is
    // pinned over a moving page.
    expectStationary(topbar, await boxOf(ownerPage.getByTestId('app-topbar'), 'the top bar'), 'the top bar');
    if (sidebarBefore) {
      expectStationary(
        sidebarBefore,
        await boxOf(ownerPage.getByTestId('app-sidebar'), 'the sidebar'),
        'the sidebar',
      );
      // …and it stays usable rather than merely being visible.
      await expect(ownerPage.getByTestId('app-sidebar').getByRole('link').first()).toBeEnabled();
    }

    // The content really did travel: a stationary shell around stationary content
    // would prove nothing.
    const contentAfter = await boxOf(ownerPage.getByTestId('media-grid'), 'the media wall');
    expect(contentBefore.y - contentAfter.y, 'the media moved up by the scrolled distance')
      .toBeGreaterThan(40);
  });

  test('changing the navigation chrome mid-gallery keeps the scroll position', async ({
    ownerPage,
  }) => {
    const available = await openScrollableMedia(ownerPage);
    await scrollMain(ownerPage, Math.min(available, 300));
    const before = await shellScrollState(ownerPage);
    expect(before.main).toBeGreaterThan(40);

    if (await isDesktopShell(ownerPage)) {
      // Collapsing the rail re-lays the wall out in a wider column, which is the
      // moment a shell that owned scrolling badly would send the user to the top.
      const wide = await boxOf(ownerPage.getByTestId('app-sidebar'), 'the sidebar');
      await ownerPage.getByTestId('nav-rail-toggle').click();
      await expect
        .poll(async () => (await boxOf(ownerPage.getByTestId('app-sidebar'), 'the sidebar')).width)
        .toBeLessThan(wide.width);
    } else {
      // Below the breakpoint the equivalent action is the drawer, which locks the
      // viewport while it is open and must hand it back untouched.
      await ownerPage.getByTestId('nav-menu-button').click();
      await expect(ownerPage.getByRole('dialog')).toBeVisible();
      await ownerPage.keyboard.press('Escape');
      await expect(ownerPage.getByRole('dialog')).toHaveCount(0);
    }

    // Measured once the width animation and the wall's re-flow have finished; every
    // frame in between is a different layout.
    const after = await settledScrollState(ownerPage);
    // Deliberately not pixel identity. The rail animates its width, the justified
    // wall re-flows at every intermediate width, and a row-count step can make it
    // briefly shorter than it ends up — an offset the browser clamps on the way
    // through and does not restore afterwards. What the user must never see is the
    // gallery snapping back to the start, so that is what is asserted; the exact
    // preservation of the position is asserted where nothing re-flows (the media
    // viewer test).
    expect(after.main, 'the gallery jumped back to the top')
      .toBeGreaterThan(Math.round(before.main / 2));
    expect(after.document).toBeLessThanOrEqual(1);
  });

  test('every authenticated page scrolls inside the shell, and none of them scrolls the document', async ({
    ownerPage,
  }, testInfo) => {
    // Representative pages that are NOT the media workspace. The shell is supposed
    // to provide this generically, so a page that grew its own scroll container —
    // or that pushed the document — shows up here rather than in a bug report.
    const routes = ['/', '/albums', '/people', '/lab', '/cloud-functions', '/account'];
    const size = ownerPage.viewportSize()!;
    await ownerPage.setViewportSize({ width: size.width, height: 500 });

    for (const route of routes) {
      await ownerPage.goto(route);
      await expect(ownerPage.getByTestId('app-main'), route).toBeVisible();
      await expect(ownerPage.getByTestId('app-topbar'), route).toBeVisible();

      const state = await shellScrollState(ownerPage);
      expect(state.documentOverflow, `${route} scrolls the document in ${testInfo.project.name}`)
        .toBeLessThanOrEqual(1);
      expect(state.documentOverflowX, `${route} overflows sideways in ${testInfo.project.name}`)
        .toBeLessThanOrEqual(1);

      // Where the page is long enough to scroll, it scrolls in the shell's viewport.
      if (state.mainOverflow > 40) {
        expect(await scrollMain(ownerPage, 200), route).toBeGreaterThan(0);
        expect((await shellScrollState(ownerPage)).document, route).toBeLessThanOrEqual(1);
      }
    }
  });

  test('the navigation menu stays reachable at every width', async ({ ownerPage }) => {
    const size = ownerPage.viewportSize()!;
    await ownerPage.setViewportSize({ width: size.width, height: 500 });
    await ownerPage.goto('/media');
    await expect(ownerPage.getByTestId('ws-command-bar')).toBeVisible({ timeout: 20_000 });

    if (await isDesktopShell(ownerPage)) {
      await expect(ownerPage.getByTestId('app-sidebar').getByRole('link', { name: 'Album' })).toBeVisible();
      return;
    }
    // Below the sidebar breakpoint the same navigation arrives through the drawer.
    await ownerPage.getByTestId('nav-menu-button').click();
    const drawer = ownerPage.getByRole('dialog');
    await expect(drawer).toBeVisible();
    await expect(drawer.getByRole('link', { name: 'Album' })).toBeVisible();
    await ownerPage.keyboard.press('Escape');
    await expect(drawer).toHaveCount(0);
  });

  test('signing out ends the session and protects the shell', async ({ page, health }) => {
    void health;
    await signIn(page, OWNER.email, OWNER.password);

    // Logging out through the API is the isolated form of the action: it asserts
    // the server drops the session without depending on where the UI happens to
    // place its menu, which differs between desktop and mobile layouts.
    const loggedOut = await page.request.post('/api/auth/logout');
    expect(loggedOut.ok()).toBeTruthy();

    const me = await page.request.get('/api/auth/me');
    expect(me.status()).toBe(401);

    // A protected route must now refuse to render the shell.
    await page.goto('/media');
    await expect(page).toHaveURL(/\/login/);
  });
});
