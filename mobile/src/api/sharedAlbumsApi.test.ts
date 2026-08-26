// Shared-albums API contract pins: method + exact path for every route the
// mobile v1 surface touches, mirroring frontend/packages/api-client/src/
// albumSharing.ts and the #16 backend endpoints. A drift here IS a broken
// screen — pin it.

import assert from 'node:assert/strict';
import { test } from 'node:test';
import {
  listSharedAlbums,
  getSharedAlbum,
  listSharedAlbumItems,
  listAlbumInvitations,
  acceptAlbumInvitation,
  declineAlbumInvitation,
  bulkContributeToSharedAlbum,
  withdrawSharedAlbumContribution,
} from './sharedAlbums.ts';
import { configureBaseUrl } from './client.ts';
import {
  setSessionCookieSource,
  staticSessionCookieSource,
} from './sessionAccess.ts';

configureBaseUrl('https://unit.test');
setSessionCookieSource(staticSessionCookieSource('NubArca.Auth=t'));

interface Recorded {
  method: string;
  url: string;
  body: unknown;
}

const ALBUM = '11111111-2222-3333-4444-555555555555';

async function record(
  run: () => Promise<unknown>,
): Promise<Recorded> {
  const original = globalThis.fetch;
  const captured: Recorded = { method: '', url: '', body: null };
  globalThis.fetch = ((url: string | URL, init?: {
    method?: string;
    body?: string;
  }) => {
    captured.method = init?.method ?? 'GET';
    captured.url = String(url);
    captured.body = init?.body ? JSON.parse(init.body) : null;
    return Promise.resolve({
      ok: true,
      status: 200,
      // client.ts reads res.headers.get('set-cookie') on EVERY response.
      headers: { get: () => null },
      text: async () => '',
    } as unknown as Response);
  }) as typeof fetch;
  try {
    await run();
    return captured;
  } finally {
    globalThis.fetch = original;
  }
}

test('listSharedAlbums hits GET /api/shared-albums', async () => {
  const r = await record(() => listSharedAlbums());
  assert.deepEqual([r.method, r.url], ['GET', 'https://unit.test/api/shared-albums']);
});

test('getSharedAlbum hits GET /api/shared-albums/{id}', async () => {
  const r = await record(() => getSharedAlbum(ALBUM));
  assert.equal(r.method, 'GET');
  assert.equal(r.url, `https://unit.test/api/shared-albums/${ALBUM}`);
});

test('items page forwards kind/cursor/limit exactly as given', async () => {
  const r = await record(() =>
    listSharedAlbumItems(ALBUM, { kind: 'video', cursor: 'cur-1', limit: 60 }),
  );
  assert.equal(r.method, 'GET');
  assert.equal(
    r.url,
    `https://unit.test/api/shared-albums/${ALBUM}/items?kind=video&cursor=cur-1&limit=60`,
  );
});

test('kind=all sends NO kind parameter (server default)', async () => {
  const r = await record(() => listSharedAlbumItems(ALBUM, { kind: 'all', limit: 10 }));
  assert.ok(r.url.includes('/items?limit=10'), r.url);
  assert.ok(!r.url.includes('kind='), r.url);
});

test('invitations listing hits GET /api/shared-albums/invitations', async () => {
  const r = await record(() => listAlbumInvitations());
  assert.equal(r.url, 'https://unit.test/api/shared-albums/invitations');
});

test('accept posts to invitations/{membershipId}/accept', async () => {
  const r = await record(() => acceptAlbumInvitation('mem-1'));
  assert.deepEqual([r.method, r.url], [
    'POST',
    'https://unit.test/api/shared-albums/invitations/mem-1/accept',
  ]);
});

test('decline posts to invitations/{membershipId}/decline', async () => {
  const r = await record(() => declineAlbumInvitation('mem-2'));
  assert.deepEqual([r.method, r.url], [
    'POST',
    'https://unit.test/api/shared-albums/invitations/mem-2/decline',
  ]);
});

test('bulk contribution posts the id set to contributions/bulk', async () => {
  const ids = ['f-1', 'f-2'];
  const r = await record(() => bulkContributeToSharedAlbum(ALBUM, ids));
  assert.equal(r.method, 'POST');
  assert.equal(r.url, `https://unit.test/api/shared-albums/${ALBUM}/contributions/bulk`);
  assert.deepEqual(r.body, { fileItemIds: ids });
});

test('withdrawal deletes the contribution by fileItemId', async () => {
  const r = await record(() => withdrawSharedAlbumContribution(ALBUM, 'f-9'));
  assert.equal(r.method, 'DELETE');
  assert.equal(r.url, `https://unit.test/api/shared-albums/${ALBUM}/contributions/f-9`);
});
