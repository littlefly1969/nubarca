// Application shell: can a seeded owner sign in, and does the shell survive the
// browser controls users actually press?
//
// Reload plus Back/Forward are here because they are where SPA session handling
// breaks: a shell that works on first paint can still lose auth state on a hard
// reload, or restore a stale view from the history cache.

import { OWNER } from '../src/env';
import { expect, signIn, test } from '../src/fixtures';

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
