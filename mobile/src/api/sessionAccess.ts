// Session-cookie access seam.
//
// The API client, image sources and video sources need the CURRENT owner
// cookie but must stay importable by plain `node --test` (no react-native,
// no expo-secure-store at module load). This module is that seam: production
// bootstrap wires the SecureStore-backed OwnerSessionCookieStore into it
// once; tests inject an in-memory fake.
//
// STALE-RESPONSE GUARD (MOBILE-SESSION-LIFECYCLE-01): a request snapshots the
// session at START and its response may feed the jar back ONLY while that
// snapshot's GENERATION is still current — never because a cookie value looks
// familiar. There is deliberately NO unconditional capture on this seam.

import type { SessionSnapshot } from './sessionCookie.ts';

export interface SessionCookieSource {
  // The exact current `NubArca.Auth=name-value` pair, or null when signed out.
  readonly current: string | null;
  // Point-in-time identity of the live session, taken when a request starts.
  snapshot(): SessionSnapshot;
  // Feed a response's Set-Cookie content into the store ONLY while `generation`
  // is still the active one. Resolves true exactly when it was accepted.
  captureIfCurrent(setCookie: string | null, generation: number): Promise<boolean>;
}

const nullSource: SessionCookieSource = {
  current: null,
  snapshot: () => ({ cookie: null, generation: 0 }),
  captureIfCurrent: () => Promise.resolve(false),
};

let source: SessionCookieSource = nullSource;

export function setSessionCookieSource(next: SessionCookieSource): void {
  source = next;
}

export function sessionCookieSource(): SessionCookieSource {
  return source;
}

// Test convenience: a seam whose cookie is FIXED and whose jar NEVER accepts
// response cookies — the exact behaviour of the retired
// `{ current, capture: noop }` fakes, expressed against the guarded contract.
export function staticSessionCookieSource(current: string | null): SessionCookieSource {
  return {
    current,
    snapshot: () => ({ cookie: current, generation: 1 }),
    captureIfCurrent: () => Promise.resolve(false),
  };
}
