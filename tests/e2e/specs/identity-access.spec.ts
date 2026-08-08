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

// ---------------------------------------------------------------- admin API
//
// Setup and teardown go through the product's own administration API, so a spec
// never reaches around the thing it is testing, and a re-run against the same
// seeded stack starts from the same state.

interface SeededRole { key: string; name: string; permissions: string[] }

async function findUserOrNull(
  admin: Page, email: string,
): Promise<{ id: string; email: string } | null> {
  const list = await admin.request.get(
    `/api/admin/users?q=${encodeURIComponent(email)}&includeDisabled=true`);
  expect(list.ok(), `looking up ${email}`).toBeTruthy();
  const users = ((await list.json()) as { items: { id: string; email: string }[] }).items;
  return users.find((u) => u.email.toLowerCase() === email.toLowerCase()) ?? null;
}

async function findUser(admin: Page, email: string): Promise<{ id: string; email: string }> {
  const found = await findUserOrNull(admin, email);
  expect(found, `no seeded user ${email}`).toBeTruthy();
  return found!;
}

async function setRole(admin: Page, userId: string, roleKey: string): Promise<void> {
  const response = await admin.request.put(
    `/api/admin/users/${userId}/role`, { data: { role: roleKey } });
  expect(response.ok(), `assigning ${roleKey}`).toBeTruthy();
}

async function listRoles(admin: Page): Promise<SeededRole[]> {
  const response = await admin.request.get('/api/admin/roles');
  expect(response.ok()).toBeTruthy();
  return ((await response.json()) as { roles: SeededRole[] }).roles;
}

/** Idempotent: reuses a role of this name if a previous run left one behind. */
async function ensureRole(
  admin: Page, name: string, permissions: string[],
): Promise<SeededRole> {
  const existing = (await listRoles(admin)).find((r) => r.name === name);
  if (existing) {
    return existing;
  }
  const response = await admin.request.post(
    '/api/admin/roles', { data: { name, description: null, permissions } });
  expect(response.ok(), `creating role ${name}`).toBeTruthy();
  return (await response.json()) as SeededRole;
}

async function deleteRole(admin: Page, roleKey: string): Promise<void> {
  const response = await admin.request.delete(`/api/admin/roles/${encodeURIComponent(roleKey)}`);
  expect(response.status(), `deleting role ${roleKey}`).toBe(204);
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

  test('moving a user onto a role with people.access makes People appear, and moving them off removes it again', async ({
    page, browser, health,
  }) => {
    void health;
    // Access is a ROLE now, so this is how an operator gives somebody People:
    // put them in a role that carries it. Made through the product's own admin
    // API by an Administrator, then observed in a second browser context signed
    // in as the target — which is what "takes effect without a re-login" means.
    const adminContext = await browser.newContext();
    const adminPage = await adminContext.newPage();
    await signIn(adminPage, ADMIN.email, ADMIN.password);

    const target = await findUser(adminPage, GRANTABLE.email);
    // Idempotent setup: back to Restricted first, so a re-run against the same
    // seeded database starts from the same place.
    await setRole(adminPage, target.id, 'Restricted');
    const peopleRole = await ensureRole(adminPage, 'E2E People', ['people.access']);

    await signIn(page, GRANTABLE.email, GRANTABLE.password);
    expect(await visibleDestinations(page)).not.toContain(DESTINATION.people);
    expect((await page.request.get('/api/people')).status()).toBe(403);

    await setRole(adminPage, target.id, peopleRole.key);

    // Same session, no re-login: the next request already sees it.
    expect((await page.request.get('/api/people')).status()).toBe(200);
    await page.reload();
    expect(await visibleDestinations(page)).toContain(DESTINATION.people);
    await page.goto('/people');
    await expect(page.getByTestId('forbidden-page')).toHaveCount(0);

    // And moving them off closes the door again, in the same session.
    await setRole(adminPage, target.id, 'Restricted');
    expect((await page.request.get('/api/people')).status()).toBe(403);

    // Leave the seeded state as it was found.
    await deleteRole(adminPage, peopleRole.key);
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

  test('the admin user detail opens as a sheet with Profile, Access and Security tabs', async ({
    page, health,
  }) => {
    void health;
    await signIn(page, ADMIN.email, ADMIN.password);
    await page.goto('/admin/users');

    await page.locator(`[data-email="${RESTRICTED.email}"]`).click();

    const detail = page.getByTestId('admin-user-detail');
    await expect(detail).toBeVisible();
    // A real overlay against the viewport, not a section further down the page.
    await expect(detail).toHaveAttribute('aria-modal', 'true');
    await expect(detail).toBeInViewport();

    // One tab visible at a time, so Security is never behind a long scroll.
    await expect(detail.getByRole('tab', { name: 'Profilo' })).toBeVisible();
    await expect(detail.getByRole('tab', { name: 'Accesso' })).toBeVisible();
    await expect(detail.getByRole('tab', { name: 'Sicurezza' })).toBeVisible();
    await expect(detail.getByRole('tabpanel')).toHaveCount(1);

    await detail.getByRole('tab', { name: 'Accesso' }).click();
    // The Access tab names permissions, never raw keys — and offers no
    // per-user exception control, because the concept no longer exists.
    await expect(detail.getByTestId('role-permission-preview')).toBeVisible();
    await expect(detail.getByText('Cassaforte privata')).toBeVisible();
    await expect(detail.getByText('private-vault.access')).toHaveCount(0);
    await expect(detail.getByRole('checkbox')).toHaveCount(0);

    await detail.getByRole('tab', { name: 'Sicurezza' }).click();
    await expect(detail.getByText('Zona critica')).toBeVisible();
    await expect(detail.getByLabel('Nome visualizzato')).toHaveCount(0);
  });

  test('the New user modal opens over the page and explains the role before saving', async ({
    page, health,
  }) => {
    void health;
    await signIn(page, ADMIN.email, ADMIN.password);
    await page.goto('/admin/users');

    await page.getByTestId('admin-users-new').click();

    const modal = page.getByTestId('create-user-modal');
    await expect(modal).toBeVisible();
    // Visible WITHOUT scrolling, and above the list rather than below it.
    await expect(modal).toBeInViewport();
    await expect(page.getByTestId('admin-users-list')).toBeAttached();

    const preview = modal.getByTestId('role-permission-preview');
    const vault = preview.locator('[data-permission="private-vault.access"]');

    // Member: every feature.
    await expect(vault).toHaveAttribute('data-included', 'yes');

    // Restricted: nothing — and the preview changes the moment the selection
    // does, with no save and no stale copy of the previous role.
    await modal.getByTestId('admin-user-role').selectOption('Restricted');
    await expect(vault).toHaveAttribute('data-included', 'no');
    await expect(modal.getByTestId('role-permission-count')).toHaveText('0 permessi');

    await modal.getByTestId('admin-user-role').selectOption('Member');
    await expect(vault).toHaveAttribute('data-included', 'yes');
    await expect(modal.getByTestId('role-permission-count')).toHaveText('8 permessi');

    // Escape closes it; the list underneath was never replaced.
    await page.keyboard.press('Escape');
    await expect(modal).toHaveCount(0);
    await expect(page.getByTestId('admin-users-list')).toBeVisible();
  });

  test('an administrator creates a role in the browser, assigns it, and edits it while the user is signed in', async ({
    page, browser, health,
  }) => {
    void health;
    // The whole operator story in one pass: make a role, give it to somebody,
    // watch the server enforce exactly that role, then widen the role and watch
    // the change reach a session that never signed in again.
    const roleName = 'Laboratory test';
    const testUser = {
      email: 'roles-e2e@nubarca.test',
      password: 'e2e-roles-password-1',
      displayName: 'E2E Roles',
    };

    await signIn(page, ADMIN.email, ADMIN.password);

    // Reset to a known state through the API BEFORE touching the UI. Three
    // browser projects run this same flow against one shared stack, and a run
    // that failed part-way leaves its role behind — with the test account still
    // on it, which is precisely what makes a role undeletable. So the account
    // comes off first, then every role of this name goes: without that, the
    // second project finds two roles called "Laboratory test" and every locator
    // for one is ambiguous.
    const priorUser = await findUserOrNull(page, testUser.email);
    if (priorUser) {
      await setRole(page, priorUser.id, 'Restricted');
    }
    for (const role of (await listRoles(page)).filter((r) => r.name === roleName)) {
      await deleteRole(page, role.key);
    }

    // --- Roles: create the custom role through the UI --------------------
    await page.goto('/admin/roles');
    await expect(page.getByRole('heading', { name: 'Ruoli', level: 2 })).toBeVisible();
    // Built-ins are present and readable.
    await expect(page.locator('[data-role="Administrator"]')).toBeVisible();
    await expect(page.locator('[data-role="Member"]')).toBeVisible();
    await expect(page.locator('[data-role="Restricted"]')).toBeVisible();
    await expect(page.locator(`[data-name="${roleName}"]`)).toHaveCount(0);

    await page.getByTestId('admin-roles-new').click();
    const editor = page.getByTestId('role-editor');
    await expect(editor).toBeVisible();
    await expect(editor).toBeInViewport();
    await editor.getByTestId('role-name').fill(roleName);
    // Ticking the section ticks the Laboratory shell with it: a section alone
    // would open nothing, and the editor makes that state unreachable.
    await editor.locator('[data-permission="laboratory.plates"] input').check();
    await expect(editor.locator('[data-permission="laboratory.access"] input')).toBeChecked();
    await editor.getByTestId('role-save').click();
    await expect(editor).toHaveCount(0);

    const created = page.locator(`[data-name="${roleName}"]`);
    await expect(created).toBeVisible();
    await expect(created.getByTestId('role-permission-total')).toHaveText('2 permessi');
    const roleKey = (await created.getAttribute('data-role'))!;
    expect(roleKey.startsWith('custom:')).toBeTruthy();

    // --- Users: create the account and give it that role ------------------
    await page.goto('/admin/users');
    const existing = await page.request.get(
      `/api/admin/users?q=${encodeURIComponent(testUser.email)}&includeDisabled=true`);
    const alreadyThere =
      ((await existing.json()) as { items: { id: string; email: string }[] }).items
        .find((u) => u.email.toLowerCase() === testUser.email);

    let testUserId: string;
    if (alreadyThere) {
      // A previous browser project already ran this flow and left the account
      // disabled. Re-enabling it is an API call, so the list rendered a moment
      // ago — which excludes disabled accounts — has to be re-fetched before
      // anything asserts that the row is on screen.
      testUserId = alreadyThere.id;
      await page.request.put(`/api/admin/users/${testUserId}/disabled`, { data: { disabled: false } });
      await page.request.post(
        `/api/admin/users/${testUserId}/password`, { data: { password: testUser.password } });
      await setRole(page, testUserId, roleKey);
      await page.reload();
    } else {
      await page.getByTestId('admin-users-new').click();
      const modal = page.getByTestId('create-user-modal');
      await modal.getByLabel('Email').fill(testUser.email);
      await modal.getByLabel('Nome visualizzato').fill(testUser.displayName);
      await modal.getByTestId('admin-user-role').selectOption(roleKey);
      await modal.getByLabel('Password iniziale').fill(testUser.password);
      await modal.getByLabel('Conferma password').fill(testUser.password);
      await modal.getByRole('button', { name: 'Crea' }).click();
      await expect(modal).toHaveCount(0);
      testUserId = (await findUser(page, testUser.email)).id;
    }

    // The new account appears in the list…
    await expect(page.locator(`[data-email="${testUser.email}"]`)).toBeVisible();
    // …and its Access tab describes exactly the role it holds.
    await page.locator(`[data-email="${testUser.email}"]`).click();
    const detail = page.getByTestId('admin-user-detail');
    await detail.getByRole('tab', { name: 'Accesso' }).click();
    await expect(detail.getByTestId('role-summary')).toContainText(roleName);
    const preview = detail.getByTestId('role-permission-preview');
    await expect(preview.locator('[data-permission="laboratory.plates"]'))
      .toHaveAttribute('data-included', 'yes');
    await expect(preview.locator('[data-permission="laboratory.aesthetics"]'))
      .toHaveAttribute('data-included', 'no');
    await expect(preview.locator('[data-permission="people.access"]'))
      .toHaveAttribute('data-included', 'no');
    await page.keyboard.press('Escape');

    // --- The user's own session ------------------------------------------
    const userContext = await browser.newContext();
    const userPage = await userContext.newPage();
    await signIn(userPage, testUser.email, testUser.password);

    expect(await visibleDestinations(userPage)).toContain(DESTINATION.laboratory);
    await userPage.goto('/lab');
    await expect(userPage).toHaveURL(/\/lab\/plates/);
    await expect(userPage.getByTestId('forbidden-page')).toHaveCount(0);
    await userPage.goto('/lab/aesthetics');
    await expect(userPage.getByTestId('forbidden-page')).toBeVisible();

    // Server-side, the same split — whatever the browser shows.
    expect((await userPage.request.get('/api/plates/images')).status()).toBe(200);
    expect((await userPage.request.get('/api/aesthetics-lab/items')).status()).toBe(403);
    expect((await userPage.request.get('/api/people')).status()).toBe(403);
    expect((await userPage.request.get('/api/media/semantic?q=colour')).status()).toBe(403);

    // --- Widen the role, with that session still open ---------------------
    await page.goto('/admin/roles');
    await page.locator(`[data-role="${roleKey}"]`).getByTestId('role-manage').click();
    const reopened = page.getByTestId('role-editor');
    await reopened.locator('[data-permission="people.access"] input').check();
    // The operator is told how many people this affects, and is not blocked.
    await expect(reopened.getByTestId('role-impact')).toContainText('1 utente');
    await reopened.getByTestId('role-save').click();
    await expect(reopened).toHaveCount(0);

    // No re-login: the SAME session sees the new permission on its next request.
    expect((await userPage.request.get('/api/people')).status()).toBe(200);
    await userPage.reload();
    expect(await visibleDestinations(userPage)).toContain(DESTINATION.people);

    // --- Restore the seeded state ----------------------------------------
    // A role with users cannot be deleted: the operator reassigns first, and
    // the server says so rather than cascading into the account.
    const refused = await page.request.delete(`/api/admin/roles/${encodeURIComponent(roleKey)}`);
    expect(refused.status()).toBe(409);

    await setRole(page, testUserId, 'Restricted');
    await page.request.put(`/api/admin/users/${testUserId}/disabled`, { data: { disabled: true } });
    await deleteRole(page, roleKey);

    await page.goto('/admin/roles');
    await expect(page.locator(`[data-name="${roleName}"]`)).toHaveCount(0);
    await userContext.close();
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
