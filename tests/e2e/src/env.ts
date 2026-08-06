// Environment for the browser-verification suite.
//
// Every value has a working local default, so the suite runs from a fresh clone
// with no configuration. Nothing here may reference a real installation: these
// are the ephemeral test stack's own addresses and its own throwaway credentials.

const port = (name: string, fallback: number): number => {
  const raw = process.env[name];
  if (!raw) return fallback;
  const parsed = Number(raw);
  if (!Number.isInteger(parsed) || parsed <= 0 || parsed > 65535) {
    throw new Error(`${name} must be a valid TCP port, got: ${raw}`);
  }
  return parsed;
};

/** Front door of the ephemeral stack: built app + same-origin /api. */
export const WEB_URL = process.env.E2E_WEB_URL ?? 'http://127.0.0.1:5273';

/**
 * API published by docker-compose.e2e.yml, for seeding and direct assertions.
 *
 * Deliberately NOT the development default, so the suite can run while a normal
 * dev backend is up. Browsers reach the API through the web front door instead,
 * same-origin, exactly as in production.
 */
export const API_PORT = port('E2E_API_PORT', 5277);
export const API_URL = process.env.E2E_API_URL ?? `http://127.0.0.1:${API_PORT}`;

/**
 * Seeded accounts. Throwaway credentials for a throwaway database — they are
 * deliberately visible so the suite needs no secret to run, and they are useless
 * anywhere but this ephemeral stack.
 *
 * `other` exists purely to prove owner isolation: a second owner's media must
 * never appear in the first owner's library or search results.
 */
export const OWNER = {
  email: 'owner@nubarca.test',
  password: 'e2e-owner-password',
  displayName: 'E2E Owner',
} as const;

export const OTHER_OWNER = {
  email: 'other@nubarca.test',
  password: 'e2e-other-password',
  displayName: 'E2E Other Owner',
} as const;

/** Names the seeder gives its fixtures, asserted against by the specs. */
export const SEED = {
  photoAlbum: 'E2E Album',
  organizePhotoPrefix: 'e2e-unorganized',
  organizedPhotoPrefix: 'e2e-organized',
  videoPrefix: 'e2e-video',
  excludedPrefix: 'e2e-excluded',
  otherOwnerPrefix: 'e2e-foreign',
} as const;

/** How long to wait for the worker to finish deterministic AI work. */
export const AI_TIMEOUT_MS = Number(process.env.E2E_AI_TIMEOUT_MS ?? 180_000);
