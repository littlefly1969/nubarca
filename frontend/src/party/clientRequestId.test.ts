import { afterEach, expect, it, vi } from 'vitest';
import { newClientRequestId } from './clientRequestId';

const V4 = /^[0-9a-f]{8}-[0-9a-f]{4}-4[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/;

afterEach(() => { vi.unstubAllGlobals(); });

it('is a fresh version-4 UUID every click', () => {
  const first = newClientRequestId();
  const second = newClientRequestId();
  expect(first).toMatch(V4);
  expect(second).toMatch(V4);
  expect(second).not.toBe(first);
});

it('is still a real UUID where randomUUID does not exist, as on plain HTTP', () => {
  // The server binds the id as a GUID, so a "unique enough" string would be a
  // refused send rather than a slightly weaker one.
  vi.stubGlobal('crypto', {
    getRandomValues: (bytes: Uint8Array) => {
      for (let i = 0; i < bytes.length; i += 1) bytes[i] = (i * 53 + 7) & 0xff;
      return bytes;
    },
  });
  expect(newClientRequestId()).toMatch(V4);
});
