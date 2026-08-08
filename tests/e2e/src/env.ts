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

/**
 * Identity & Access fixtures. Each one exists to make a DIFFERENT authority
 * observable in a browser:
 *
 *   admin       — every permission; the only account that may edit access
 *   restricted  — no feature permission at all; never mutated by a spec, so the
 *                 "these destinations are absent" assertions stay deterministic
 *                 however many projects run against the same seeded database
 *   grantable   — Restricted, and the target of the live grant/revoke scenario
 *   labPlates   — Restricted plus laboratory.access + laboratory.plates, the
 *                 "one section, not the other" case
 *   recovery    — used only by the forgot-password flow, so a reset never
 *                 changes a password another spec signs in with
 *
 * OWNER stays a Member: it is what every pre-role account became, so the
 * existing specs keep asserting exactly the navigation they always did.
 */
export const ADMIN = {
  email: 'admin@nubarca.test',
  password: 'e2e-admin-password',
  displayName: 'E2E Admin',
} as const;

export const RESTRICTED = {
  email: 'restricted@nubarca.test',
  password: 'e2e-restricted-password',
  displayName: 'E2E Restricted',
} as const;

export const GRANTABLE = {
  email: 'grantable@nubarca.test',
  password: 'e2e-grantable-password',
  displayName: 'E2E Grantable',
} as const;

export const LAB_PLATES = {
  email: 'labplates@nubarca.test',
  password: 'e2e-labplates-password',
  displayName: 'E2E Lab Plates',
} as const;

export const RECOVERY_USER = {
  email: 'recovery@nubarca.test',
  password: 'e2e-recovery-password',
  displayName: 'E2E Recovery',
} as const;

/**
 * Mailpit's HTTP API on the ephemeral stack. The suite reads delivered messages
 * back from here, which is how a browser test can follow a real reset link
 * without a real mail server and without any risk of reaching a real mailbox.
 */
export const MAIL_PORT = port('E2E_MAIL_PORT', 58025);
export const MAIL_URL = process.env.E2E_MAIL_URL ?? `http://127.0.0.1:${MAIL_PORT}`;

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
