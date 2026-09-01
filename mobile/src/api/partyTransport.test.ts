// Party settings TRANSPORT tests: what actually goes on the WIRE.
//
// Source-text assertions cannot catch this class of defect. The bug these
// exist for was a payload that looked right in the editor and was wrong in the
// request: the server's body carries a NON-NULLABLE `enabled`, so a patch
// without it deserialises to false and DISABLES the party — meaning a toggle
// labelled "guest uploads" would have revoked every public link.
//
// So these tests read the real request body, not the file.

import assert from 'node:assert/strict';
import { afterEach, beforeEach, test } from 'node:test';
import { configureBaseUrl } from './client.ts';
import {
  setSessionCookieSource,
  staticSessionCookieSource,
} from './sessionAccess.ts';
import { partySettingsPatch, type AlbumPartyStatus } from '@nubarca/contracts';
import { updatePartySettings } from './party.ts';

interface Recorded {
  method: string;
  url: string;
  body: unknown;
}

let recorded: Recorded | null = null;
const realFetch = globalThis.fetch;

const STATUS: AlbumPartyStatus = {
  albumId: 'a1',
  showOnTv: true,
  partyMode: true,
  partyUrl: '/party/abc',
  uploadEnabled: false,
  uploadUrl: null,
  requireUploadApproval: false,
  requireMessageApproval: false,
  photoSlideSeconds: 6,
  maxVideoSlideSeconds: 30,
  maxPhotoUploadsPerParticipant: 0,
  maxVideoUploadsPerParticipant: 0,
};

beforeEach(() => {
  recorded = null;
  configureBaseUrl('https://unit.test');
  setSessionCookieSource(staticSessionCookieSource(`NubArca.Auth=${'v'.repeat(36)}`));
  // @ts-expect-error — replacing global fetch in the test harness.
  globalThis.fetch = async (url: string, init: { method?: string; body?: string }) => {
    recorded = {
      method: init?.method ?? 'GET',
      url,
      body: init?.body === undefined ? undefined : JSON.parse(init.body),
    };
    return {
      ok: true,
      status: 200,
      headers: { get: () => null },
      json: async () => STATUS,
      text: async () => JSON.stringify(STATUS),
    };
  };
});

afterEach(() => {
  globalThis.fetch = realFetch;
});

test('the route and method are the canonical ones', async () => {
  await updatePartySettings('a1', partySettingsPatch(STATUS, { enabled: true }));
  assert.equal(recorded?.method, 'PATCH');
  assert.equal(recorded?.url, 'https://unit.test/api/albums/a1/party-settings');
});

test('turning the party on and off sends `enabled`, the master switch', async () => {
  await updatePartySettings('a1', partySettingsPatch({ partyMode: false }, { enabled: true }));
  assert.deepEqual(recorded?.body, { enabled: true });

  await updatePartySettings('a1', partySettingsPatch({ partyMode: true }, { enabled: false }));
  assert.deepEqual(recorded?.body, { enabled: false });
});

test('a guest-upload toggle CARRIES enabled, so it cannot disable the party', async () => {
  // THE regression. Before the canonical builder this body was
  // { uploadEnabled: true } — and the server read a missing `enabled` as
  // false, revoking every public link while the user thought they were
  // turning uploads on.
  await updatePartySettings('a1', partySettingsPatch(STATUS, { uploadEnabled: true }));
  assert.deepEqual(recorded?.body, { enabled: true, uploadEnabled: true });
});

test('both approval switches carry enabled too', async () => {
  await updatePartySettings('a1', partySettingsPatch(STATUS, { requireUploadApproval: true }));
  assert.deepEqual(recorded?.body, { enabled: true, requireUploadApproval: true });

  await updatePartySettings('a1', partySettingsPatch(STATUS, { requireMessageApproval: true }));
  assert.deepEqual(recorded?.body, { enabled: true, requireMessageApproval: true });
});

test('the body NEVER carries a partyMode field: that name is not the wire', async () => {
  // The original defect in one assertion.
  for (const patch of [
    partySettingsPatch(STATUS, { enabled: true }),
    partySettingsPatch(STATUS, { uploadEnabled: true }),
    partySettingsPatch(STATUS, { requireUploadApproval: false }),
  ]) {
    await updatePartySettings('a1', patch);
    const body = recorded?.body as Record<string, unknown>;
    assert.equal('partyMode' in body, false);
    assert.equal(typeof body.enabled, 'boolean');
  }
});

test('a sub-switch on a STOPPED party keeps it stopped', async () => {
  await updatePartySettings(
    'a1',
    partySettingsPatch({ partyMode: false }, { requireUploadApproval: true }),
  );
  assert.deepEqual(recorded?.body, { enabled: false, requireUploadApproval: true });
});
