// Session infrastructure wiring: binds the pure OwnerSessionCookieStore to
// expo-secure-store (Android Keystore / iOS Keychain) and owns base-URL
// persistence. Everything above this layer stays storage-agnostic.

import * as SecureStore from 'expo-secure-store';
import {
  OwnerSessionCookieStore,
  SESSION_STORAGE_KEY,
  type SessionCookieStorage,
} from './sessionCookie.ts';
import { withDeadline } from '../lib/promiseDeadline.ts';

const SECURE_STORE_READ_TIMEOUT_MS = 5_000;

function readSecureItem(key: string): Promise<string | null> {
  return withDeadline(
    SecureStore.getItemAsync(key),
    SECURE_STORE_READ_TIMEOUT_MS,
    `SecureStore read timed out for ${key}`,
  );
}

// SecureStore adapter. Values are small (one cookie pair); SecureStore's
// per-key size limit comfortably covers it. The iOS keychain item is pinned
// to this-device-unlocked so a session never migrates to a fresh device via
// an unencrypted backup, and is unreachable while the device is locked.
const secureStorage: SessionCookieStorage = {
  getItem: readSecureItem,
  setItem: (key, value) =>
    SecureStore.setItemAsync(key, value, {
      keychainAccessible: SecureStore.WHEN_UNLOCKED_THIS_DEVICE_ONLY,
    }),
  removeItem: (key) => SecureStore.deleteItemAsync(key),
};

// The ONE owner-session store used by every API call, image source and video
// source in the app.
export const ownerSession = new OwnerSessionCookieStore(secureStorage);

// The last server the user signed in to. Not a secret — persisted separately
// so the login screen can prefill it.
const BASE_URL_KEY = 'nubarca.mobile.base_url';

export async function persistBaseUrl(baseUrl: string): Promise<void> {
  await SecureStore.setItemAsync(BASE_URL_KEY, baseUrl);
}

export async function getStoredBaseUrl(): Promise<string | null> {
  return readSecureItem(BASE_URL_KEY);
}

// Full sign-out hygiene: drop the durable cookie. The base URL survives so
// re-login prefills the server (it carries no secret). Callers must also
// clear in-memory media caches (clearImageCache) so no image byte outlives
// the session.
export async function clearPersistedSession(): Promise<void> {
  ownerSession.clear();
}

export { SESSION_STORAGE_KEY };

// The theme choice. Not a secret either — stored here so every persisted
// preference goes through one module, and read on a cold start before the
// first frame is painted.
const THEME_PREFERENCE_KEY = 'nubarca.mobile.theme';

export async function persistThemePreference(preference: string): Promise<void> {
  await SecureStore.setItemAsync(THEME_PREFERENCE_KEY, preference);
}

export async function getStoredThemePreference(): Promise<string | null> {
  return readSecureItem(THEME_PREFERENCE_KEY);
}
