// Identity & Access in a real browser: what each authority can see, what the
// server refuses whatever the browser shows, and the whole forgot-password flow
// end to end through a real SMTP delivery.
//
// The seeded identities are one account per authority (see src/env.ts), so no
// spec has to mutate a shared account to observe a different one — which is what
// keeps the assertions deterministic across the three browser projects that run
// against the same seeded database.
//
// Navigation assertions are scoped to the shell's own navigation region and are
// tolerant of form factor: below the sidebar breakpoint the same model is
// rendered by the drawer, so the helper opens it there rather than asserting a
// desktop-only layout.

import { ADMIN, GRANTABLE, LAB_PLATES, OWNER, RECOVERY_USER, RESTRICTED } from '../src/env';
import { expect, signIn, test } from '../src/fixtures';
import { isDesktopShell } from '../src/appShell';
import { clearMailbox, resetTokenFrom, waitForMessage } from '../src/mail';
import type { Locator, Page } from '@playwright/test';

/** The navigation, wherever this form factor puts it. */
async function navigation(page: Page): Promise<Locator> {
  if (await isDesktopShell(page)) {
    return page.getByTestId('app-sidebar');
  }
  await page.getByTestId('nav-menu-button').click();
  const drawer = page.getByRole('dialog');
  await expect(drawer).toBeVisible();
  return drawer;
}

async function closeNavigation(page: Page): Promise<void> {
  if (!(await isDesktopShell(page))) {
    await page.keyboard.press('Escape');
    await expect(page.getByRole('dialog')).toHaveCount(0);
  }
}

/** The destination labels, in whatever language the shell is rendering. */
const DESTINATION = {
  people: 'Volti',
  laboratory: 'Laboratorio',
  cloudFunctions: 'Funzioni cloud',
  privateVault: 'Privato',
  library: 'Libreria',
  albums: 'Album',
  files: 'File',
  trash: 'Cestino',
  adminUsers: 'Utenti',
  adminJobs: 'Processi',
} as const;

async function visibleDestinations(page: Page): Promise<string[]> {
  const nav = await navigation(page);
  const labels = await nav.getByRole('link').allInnerTexts();
  await closeNavigation(page);
  return labels.map((l) => l.trim()).filter(Boolean);
}

test.describe('roles and permissions', () => {
  test('a Member keeps the navigation an ordinary account always had', async ({ page, health }) => {
    void health;
    // The migration promise, seen from a browser: OWNER is a Member, which is
    // what every pre-role non-admin account became.
    await signIn(page, OWNER.email, OWNER.password);

    const destinations = await visibleDestinations(page);
    expect(destinations).toContain(DESTINATION.people);
    expect(destinations).toContain(DESTINATION.laboratory);
    expect(destinations).toContain(DESTINATION.cloudFunctions);
    expect(destinations).toContain(DESTINATION.privateVault);
    // …and no administration.
    expect(destinations).not.toContain(DESTINATION.adminUsers);
  });

  test('a Restricted user sees the core personal cloud and nothing else', async ({ page, health }) => {
    void health;
    await signIn(page, RESTRICTED.email, RESTRICTED.password);

    const destinations = await visibleDestinations(page);
    // Files, media, albums and trash are not permission-gated at all.
    expect(destinations).toContain(DESTINATION.files);
    expect(destinations).toContain(DESTINATION.library);
    expect(destinations).toContain(DESTINATION.albums);
    expect(destinations).toContain(DESTINATION.trash);

    expect(destinations).not.toContain(DESTINATION.people);
    expect(destinations).not.toContain(DESTINATION.laboratory);
    expect(destinations).not.toContain(DESTINATION.cloudFunctions);
    expect(destinations).not.toContain(DESTINATION.privateVault);
    expect(destinations).not.toContain(DESTINATION.adminUsers);
  });

  test('a Restricted user gets a clean forbidden page on a direct navigation', async ({
    page, health,
  }) => {
    void health;
    await signIn(page, RESTRICTED.email, RESTRICTED.password);

    // An old bookmark, not a link they were offered.
    await page.goto('/people');

    await expect(page.getByTestId('forbidden-page')).toBeVisible();
    // A clean state, not a half-rendered page that fired its API call and lost.
    await expect(page.getByRole('link', { name: 'Torna alla home' })).toBeVisible();
  });

  test('the server refuses a Restricted user whatever the browser shows', async ({
    page, health,
  }) => {
    void health;
    await signIn(page, RESTRICTED.email, RESTRICTED.password);

    // Hiding a destination is UX. This is the actual boundary.
    for (const path of ['/api/people', '/api/private-vault', '/api/admin/users']) {
      expect((await page.request.get(path)).status(), path).toBe(403);
    }
    // Semantic search is refused while ordinary search keeps working — the
    // whole media endpoint must not go away because it also supports semantics.
    expect((await page.request.get('/api/media/semantic?q=colour')).status()).toBe(403);
    expect((await page.request.get('/api/media?q=colour')).status()).toBe(200);
  });

  test('granting people.access makes People appear and usable, and revoking it removes it again', async ({
    page, browser, health,
  }) => {
    void health;
    // The grant is made through the product's own admin API by an
    // Administrator, then observed in a second browser context signed in as the
    // target — which is what "takes effect without a re-login" means.
    const adminContext = await browser.newContext();
    const adminPage = await adminContext.newPage();
    await signIn(adminPage, ADMIN.email, ADMIN.password);

    const list = await adminPage.request.get(
      `/api/admin/users?q=${encodeURIComponent(GRANTABLE.email)}&includeDisabled=true`);
    expect(list.ok()).toBeTruthy();
    const users = ((await list.json()) as { items: { id: string; email: string }[] }).items;
    const target = users.find((u) => u.email.toLowerCase() === GRANTABLE.email)!;

    // Idempotent setup: clear first, so a re-run against the same seeded
    // database starts from the same place.
    await adminPage.request.delete(`/api/admin/users/${target.id}/permissions/people.access`);

    await signIn(page, GRANTABLE.email, GRANTABLE.password);
    expect(await visibleDestinations(page)).not.toContain(DESTINATION.people);
    expect((await page.request.get('/api/people')).status()).toBe(403);

    const granted = await adminPage.request.put(
      `/api/admin/users/${target.id}/permissions/people.access`,
      { data: { effect: 'grant' } });
    expect(granted.ok()).toBeTruthy();

    // Same session, no re-login: the next request already sees it.
    expect((await page.request.get('/api/people')).status()).toBe(200);
    await page.reload();
    expect(await visibleDestinations(page)).toContain(DESTINATION.people);
    await page.goto('/people');
    await expect(page.getByTestId('forbidden-page')).toHaveCount(0);

    // And taking it away closes the door again, in the same session.
    const cleared = await adminPage.request.delete(
      `/api/admin/users/${target.id}/permissions/people.access`);
    expect(cleared.ok()).toBeTruthy();
    expect((await page.request.get('/api/people')).status()).toBe(403);

    await adminContext.close();
  });

  test('the Laboratory shows the section a user holds and refuses the one they do not', async ({
    page, health,
  }) => {
    void health;
    // laboratory.access + laboratory.plates, seeded. Plates is usable;
    // Aesthetics is not offered and is refused if reached directly.
    await signIn(page, LAB_PLATES.email, LAB_PLATES.password);

    expect(await visibleDestinations(page)).toContain(DESTINATION.laboratory);

    await page.goto('/lab');
    // A bare /lab lands on the first section this user may actually open.
    await expect(page).toHaveURL(/\/lab\/plates/);
    await expect(page.getByTestId('forbidden-page')).toHaveCount(0);
    // Only one tab, because the other section is not theirs.
    await expect(page.getByRole('link', { name: 'Estetica' })).toHaveCount(0);

    await page.goto('/lab/aesthetics');
    await expect(page.getByTestId('forbidden-page')).toBeVisible();

    // Server-side, the same split.
    expect((await page.request.get('/api/plates/images')).status()).toBe(200);
    expect((await page.request.get('/api/aesthetics-lab/items')).status()).toBe(403);
  });

  test('an Administrator sees the administration destinations', async ({ page, health }) => {
    void health;
    await signIn(page, ADMIN.email, ADMIN.password);

    const destinations = await visibleDestinations(page);
    expect(destinations).toContain(DESTINATION.adminUsers);
    expect(destinations).toContain(DESTINATION.adminJobs);

    await page.goto('/admin/users');
    await expect(page.getByRole('heading', { name: 'Utenti' })).toBeVisible();
    await expect(page.getByTestId('forbidden-page')).toHaveCount(0);
  });

  test('the admin user detail separates Profile, Access and Security', async ({ page, health }) => {
    void health;
    await signIn(page, ADMIN.email, ADMIN.password);
    await page.goto('/admin/users');

    const row = page.locator('tr', { hasText: RESTRICTED.email });
    await row.getByRole('button', { name: 'Gestisci' }).click();

    const detail = page.getByTestId('admin-user-detail');
    await expect(detail).toBeVisible();
    await expect(detail.getByText('Profilo')).toBeVisible();
    await expect(detail.getByText('Accesso', { exact: true })).toBeVisible();
    await expect(detail.getByText('Sicurezza')).toBeVisible();
    // The Access editor names permissions, never raw keys.
    await expect(detail.getByText('Cassaforte privata')).toBeVisible();
    await expect(detail.getByText('private-vault.access')).toHaveCount(0);
  });
});

test.describe('password recovery', () => {
  test('the forgot-password form completes identically for a known and an unknown address', async ({
    page, health,
  }) => {
    void health;
    await page.goto('/forgot-password');

    await page.locator('#recovery-email').fill(RECOVERY_USER.email);
    await page.getByRole('button', { name: 'Invia le istruzioni' }).click();
    const known = await page.getByTestId('recovery-sent').innerText();

    await page.goto('/forgot-password');
    await page.locator('#recovery-email').fill('definitely-not-a-user@nubarca.test');
    await page.getByRole('button', { name: 'Invia le istruzioni' }).click();
    const unknown = await page.getByTestId('recovery-sent').innerText();

    // Byte-identical: the page cannot leak which address exists because it has
    // one completion state and never branches on the response.
    expect(unknown).toBe(known);
  });

  test('a real reset link signs the user in with the new password and cannot be reused', async ({
    page, health,
  }) => {
    void health;
    const newPassword = 'e2e-recovered-password-1';

    await clearMailbox();
    await page.goto('/forgot-password');
    await page.locator('#recovery-email').fill(RECOVERY_USER.email);
    await page.getByRole('button', { name: 'Invia le istruzioni' }).click();
    await expect(page.getByTestId('recovery-sent')).toBeVisible();

    // The message the product actually sent, over a real SMTP conversation.
    const message = await waitForMessage(RECOVERY_USER.email);
    expect(message.Subject).toContain('NubArca');
    const token = resetTokenFrom(message);

    await page.goto(`/reset-password#token=${encodeURIComponent(token)}`);
    // The token leaves the visible URL and this history entry immediately.
    await expect.poll(() => page.evaluate(() => window.location.hash)).toBe('');

    await page.locator('#reset-password').fill(newPassword);
    await page.locator('#reset-password-confirm').fill(newPassword);
    await page.getByRole('button', { name: 'Imposta la password' }).click();
    await expect(page.getByTestId('reset-done')).toBeVisible();

    // No automatic sign-in: the user returns to the login form.
    expect((await page.request.get('/api/auth/me')).status()).toBe(401);

    // The new password works…
    await signIn(page, RECOVERY_USER.email, newPassword);
    await expect(page).not.toHaveURL(/\/login/);

    // …and the spent token does not, however it is replayed.
    const replay = await page.request.post('/api/auth/password-recovery/reset', {
      data: { token, newPassword: 'another-password-99' },
    });
    expect(replay.status()).toBe(400);

    // Leave the fixture on a known password for a re-run against this stack.
    await clearMailbox();
    await page.goto('/forgot-password');
    await page.locator('#recovery-email').fill(RECOVERY_USER.email);
    await page.getByRole('button', { name: 'Invia le istruzioni' }).click();
    await expect(page.getByTestId('recovery-sent')).toBeVisible();
    const second = await waitForMessage(RECOVERY_USER.email);
    const restoreResponse = await page.request.post('/api/auth/password-recovery/reset', {
      data: { token: resetTokenFrom(second), newPassword: RECOVERY_USER.password },
    });
    expect(restoreResponse.status()).toBe(204);
  });

  test('the login page offers the recovery entry point', async ({ page, health }) => {
    void health;
    await page.goto('/login');
    await expect(page.getByRole('link', { name: 'Password dimenticata?' })).toBeVisible();
    await page.getByRole('link', { name: 'Password dimenticata?' }).click();
    await expect(page).toHaveURL(/\/forgot-password/);
  });
});
