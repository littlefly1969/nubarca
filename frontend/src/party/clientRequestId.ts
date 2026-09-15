/**
 * A fresh id for ONE click of a send button.
 *
 * The server keys its invitation ledger on it, so a retry of the same click can
 * never send a second email — which also means it must be a real UUID: the
 * server binds it as one. `randomUUID` exists only in secure contexts, and an
 * installation reached over plain HTTP on a home network is not one, so the
 * version-4 layout is built from `getRandomValues` there, which exists
 * everywhere a browser runs this page.
 */
export function newClientRequestId(): string {
  const cryptoApi = globalThis.crypto;
  if (typeof cryptoApi?.randomUUID === 'function') return cryptoApi.randomUUID();
  const bytes = new Uint8Array(16);
  if (typeof cryptoApi?.getRandomValues === 'function') {
    cryptoApi.getRandomValues(bytes);
  } else {
    for (let i = 0; i < bytes.length; i += 1) bytes[i] = Math.floor(Math.random() * 256);
  }
  bytes[6] = (bytes[6] & 0x0f) | 0x40;
  bytes[8] = (bytes[8] & 0x3f) | 0x80;
  const hex = [...bytes].map((b) => b.toString(16).padStart(2, '0')).join('');
  return `${hex.slice(0, 8)}-${hex.slice(8, 12)}-${hex.slice(12, 16)}-${hex.slice(16, 20)}-${hex.slice(20)}`;
}
