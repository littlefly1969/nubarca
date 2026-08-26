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

/**
 * Best-effort server notification for a logout that has ALREADY happened
 * locally. Carries the captured pre-teardown cookie via cookieOverride and
 * swallows every failure: the local session is gone either way.
 */
export async function notifyServerLogout(capturedCookie: string): Promise<void> {
  try {
    await apiPost<void>('/api/auth/logout', undefined, {
      allow401: true,
      cookieOverride: capturedCookie,
    });
  } catch {
    /* the server-side session expires on its own; local state is already gone */
  }
}

/**
 * LOCAL-FIRST logout convenience: capture the CURRENT cookie, run the caller's
 * synchronous local teardown, then start the best-effort notification WITHOUT
 * awaiting it. The returned handle lets a caller track (not block on) the
 * network word.
 *
 * NOTE on durability: wiping the SECURE STORE is the cookie store's own
 * tracked `clear()` promise — orchestrate it next to the returned
 * serverNotification (see SessionProvider). This helper only owns the
 * in-memory seam and the network notification.
 */
export function signOutLocalFirst(onLocalTeardown: () => void): {
  serverNotification: Promise<void>;
} {
  const cookie = sessionCookieSource().current ?? undefined;
  onLocalTeardown();
  const serverNotification =
    cookie === undefined ? Promise.resolve() : notifyServerLogout(cookie);
  return { serverNotification };
}

