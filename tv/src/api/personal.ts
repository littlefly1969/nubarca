// TV Personal Area API. The unlock grant is an OPAQUE server-side token bound
// to this TV session + owner: it lives ONLY in this module's memory and is sent
// per-request in the X-Tv-Personal-Unlock header. It is NEVER written to
// AsyncStorage, the filesystem, navigation params, or URLs — an app restart
// always starts locked. clearPersonalGrant() is called when leaving the
// Personal Area and whenever the TV session is cleared/revoked.

import { setPersonalMediaHeaderProvider, tvGet, tvPost } from './client';

const UNLOCK_HEADER = 'X-Tv-Personal-Unlock';

let _grant: string | null = null;

// Personal media downloads (thumbnails/previews of the Personal Gallery) need
// the same grant header as the JSON calls; the loader reads it per-request
// through this provider so a lock immediately stops authorizing new downloads.
setPersonalMediaHeaderProvider(() => personalHeaders());

export function hasPersonalGrant(): boolean {
  return _grant !== null;
}

export function clearPersonalGrant(): void {
  _grant = null;
}

export interface TvPersonalStatus {
  pinConfigured: boolean;
  unlocked: boolean;
  // Which credential the owner holds. 'dpad-v1' is the directional code this
  // app can accept; 'pin-v1' is the retired numeric PIN, which this app has no
  // entry surface for — it shows "configure the new code from your account"
  // instead. Null only when nothing is configured, which is a DIFFERENT and
  // more serious condition (an incomplete pairing → teardown).
  scheme: 'dpad-v1' | 'pin-v1' | null;
}

export interface TvPersonalHome {
  displayName: string;
  galleryAvailable: boolean;
}

export function getTvPersonalStatus(): Promise<TvPersonalStatus> {
  return tvGet<TvPersonalStatus>('/api/tv/personal/status');
}

// Server-side directional-code check. On success the returned grant is kept in
// module memory (never persisted) and used by every subsequent personal call.
// Failures: 403 = wrong code (generic — the server deliberately does not
// distinguish "nothing configured" or "wrong scheme"); 429 = progressive
// cooldown; 401 = the TV session itself is invalid/revoked.
//
// The code is passed straight through to the request body and is never stored
// in this module, logged, or included in any debug line.
export async function unlockTvPersonal(code: string): Promise<void> {
  const result = await tvPost<{ unlockToken: string; expiresAt: string }>(
    '/api/tv/personal/unlock', { code });
  _grant = result.unlockToken;
}

// Explicit lock: drop the in-memory grant FIRST (the UI is locked no matter
// what), then best-effort revoke it server-side. Idempotent; the server's
// bounded grant lifetime covers a lost lock call.
export async function lockTvPersonal(): Promise<void> {
  _grant = null;
  try {
    await tvPost<void>('/api/tv/personal/lock');
  } catch {
    // Already locked locally; the server grant expires on its own.
  }
}

export function getTvPersonalHome(): Promise<TvPersonalHome> {
  return tvGet<TvPersonalHome>('/api/tv/personal/home', personalHeaders());
}

// Exported for the Personal Gallery API module (same in-memory grant, same
// header). Returns {} while locked — the server then answers 403 locked.
export function personalHeaders(): Record<string, string> {
  return _grant ? { [UNLOCK_HEADER]: _grant } : {};
}
