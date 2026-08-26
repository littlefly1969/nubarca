// Session infrastructure wiring: binds the pure OwnerSessionCookieStore to
// expo-secure-store (Android Keystore / iOS Keychain) and owns base-URL
// persistence. Everything above this layer stays storage-agnostic.

import * as SecureStore from 'expo-secure-store';
import {
  OwnerSessionCookieStore,
  SESSION_STORAGE_KEY,
  type SessionCookieStorage,
} from './sessionCookie.ts';

// SecureStore adapter. Values are small (one cookie pair); SecureStore's
// per-key size limit comfortably covers it. The iOS keychain item is pinned
// to this-device-unlocked so a session never migrates to a fresh device via
// an unencrypted backup, and is unreachable while the device is locked.
const secureStorage: SessionCookieStorage = {
  getItem: (key) => SecureStore.getItemAsync(key),
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
  return SecureStore.getItemAsync(BASE_URL_KEY);
}

// Full sign-out hygiene: drop the durable cookie. The base URL survives so
// re-login prefills the server (it carries no secret). Callers must also
// clear in-memory media caches (clearImageCache) so no image byte outlives
// the session.
export async function clearPersistedSession(): Promise<void> {
  ownerSession.clear();
}

export { SESSION_STORAGE_KEY };
