// Session-cookie access seam.
//
// The API client, image sources and video sources need the CURRENT owner
// cookie but must stay importable by plain `node --test` (no react-native,
// no expo-secure-store at module load). This module is that seam: production
// bootstrap wires the SecureStore-backed OwnerSessionCookieStore into it
// once; tests inject an in-memory fake.

export interface SessionCookieSource {
  // The exact current `NubArca.Auth=name-value` pair, or null when signed out.
  readonly current: string | null;
  // Feed a response's Set-Cookie content into the store.
  capture(setCookie: string | null): void;
}

const nullSource: SessionCookieSource = { current: null, capture: () => {} };

let source: SessionCookieSource = nullSource;

export function setSessionCookieSource(next: SessionCookieSource): void {
  source = next;
}

export function sessionCookieSource(): SessionCookieSource {
  return source;
}
