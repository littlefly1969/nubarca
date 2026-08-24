// LOCAL-FIRST logout (acceptance BLOCKER 8).
//
// Order of events, and why:
//   1. capture the CURRENT cookie — the last chance, because teardown wipes it;
//   2. run onLocalTeardown() SYNCHRONOUSLY: memory jar, SecureStore, image
//      cache, viewer/private state, UI → unauthed. From this instant the app
//      is signed out no matter what;
//   3. only then fire ONE best-effort POST /api/auth/logout carrying the
//      captured cookie via cookieOverride (the seam now returns null). It can
//      hang, fail, or be dropped — none of it can resurrect the session or
//      block the UI.
//
// Lives in its OWN module (no SecureStore import anywhere up-chain) so the
// node --test suite can exercise the ordering without native modules.

import { apiPost } from './client.ts';
import { sessionCookieSource } from './sessionAccess.ts';

export async function signOutLocalFirst(onLocalTeardown: () => void): Promise<void> {
  const cookie = sessionCookieSource().current ?? undefined;
  onLocalTeardown();
  if (cookie === undefined) return; // nothing to notify the server about
  try {
    await apiPost<void>('/api/auth/logout', undefined, {
      allow401: true,
      cookieOverride: cookie,
    });
  } catch {
    /* the server-side session expires on its own; local state is already gone */
  }
}
