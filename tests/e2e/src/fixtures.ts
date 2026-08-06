// Test fixtures: authenticated pages and the browser-health guards.
//
// Two of the required assertions — "no uncaught page errors" and "no failed
// critical requests" — are properties of a whole test rather than of one step.
// Collecting them in a fixture and asserting at teardown means every spec gets
// them without repeating the wiring, and a spec cannot forget to check.

import { test as base, expect, type Page } from '@playwright/test';
import { readFileSync } from 'node:fs';
import { join } from 'node:path';
import { OTHER_OWNER, OWNER } from './env';

export interface SeedIds {
  readonly albumId: string;
  readonly unassignedPhoto: string;
  readonly assignedPhoto: string;
  readonly unassignedVideo: string;
  readonly assignedVideo: string;
  readonly excludedPhoto: string;
  readonly videoDurationSeconds: number;
}

export const seedIds = (): SeedIds =>
  JSON.parse(readFileSync(join(import.meta.dirname, '..', '.state', 'seed.json'), 'utf8'));

/**
 * Requests whose failure means the application is broken, as opposed to noise.
 * A thumbnail that 404s while a derivative is still being generated is not a
 * product failure; the document, the JS bundle and the API are.
 */
const isCritical = (url: string): boolean => {
  if (/\/(assets|src)\/.*\.(js|mjs|css)(\?|$)/.test(url)) return true;
  if (/\/api\/(auth|media|albums|media-library)\b/.test(url)) return true;
  return false;
};

/** Status codes that are a legitimate answer rather than a failure. */
const isAcceptable = (status: number): boolean =>
  status < 400 || status === 401 || status === 403 || status === 404;

export interface HealthGuard {
  /** Errors seen so far, for specs that assert about them mid-test. */
  readonly pageErrors: string[];
  readonly failedRequests: string[];
  /** Stop failing the test for requests matching this pattern. */
  allowFailures(pattern: RegExp): void;
}

export const test = base.extend<{
  health: HealthGuard;
  ownerPage: Page;
}>({
  health: async ({ page }, use) => {
    const pageErrors: string[] = [];
    const failedRequests: string[] = [];
    const allowed: RegExp[] = [];

    page.on('pageerror', (error) => pageErrors.push(`${error.name}: ${error.message}`));
    page.on('response', (response) => {
      const url = response.url();
      if (!isCritical(url) || isAcceptable(response.status())) return;
      if (allowed.some((pattern) => pattern.test(url))) return;
      failedRequests.push(`${response.status()} ${url}`);
    });
    page.on('requestfailed', (request) => {
      const url = request.url();
      if (!isCritical(url)) return;
      if (allowed.some((pattern) => pattern.test(url))) return;
      // Navigations aborted by a deliberate in-test navigation are not failures.
      const failure = request.failure()?.errorText ?? 'unknown';
      if (failure.includes('ERR_ABORTED')) return;
      failedRequests.push(`${failure} ${url}`);
    });

    const guard: HealthGuard = {
      pageErrors,
      failedRequests,
      allowFailures: (pattern) => allowed.push(pattern),
    };
    await use(guard);

    expect(pageErrors, 'uncaught page errors').toEqual([]);
    expect(failedRequests, 'failed critical requests').toEqual([]);
  },

  // A page already signed in as the primary owner. Logging in through the real
  // form rather than injecting a cookie keeps the login path itself covered by
  // every spec that needs an authenticated view.
  ownerPage: async ({ page, health }, use) => {
    void health;
    await signIn(page, OWNER.email, OWNER.password);
    await use(page);
  },
});

export { expect };

export async function signIn(page: Page, email: string, password: string): Promise<void> {
  await page.goto('/login');
  // The form's own ids, so sign-in does not depend on the active locale.
  await page.locator('#email').fill(email);
  await page.locator('#password').fill(password);
  await page.locator('form.login-card button[type="submit"]').click();
  await expect(page).not.toHaveURL(/\/login/, { timeout: 20_000 });
}

export const asOtherOwner = (page: Page): Promise<void> =>
  signIn(page, OTHER_OWNER.email, OTHER_OWNER.password);
