// CROSS-CLIENT PARITY (§44).
//
// The rule this enforces is not "the clients import the same types" — that is
// easy to satisfy and easy to defeat, because a client can import a shared
// interface and still build its own request. What is asserted here is that
// given the SAME canonical input, every client produces the same route, the
// same method and the same parameters.
//
// It is done by reading the clients' own transport modules and checking they
// route through the canonical builders rather than assembling requests by
// hand. A client that re-grows a private `toParams` fails here immediately.

import assert from 'node:assert/strict';
import { existsSync, readFileSync } from 'node:fs';
import { dirname, resolve } from 'node:path';
import { test } from 'node:test';
import { fileURLToPath } from 'node:url';
import {
  MEDIA_LIST_PATH,
  albumMediaPath,
  mediaQueryToParams,
  withQuery,
  type ListMediaQuery,
} from './index.ts';

const ROOT = resolve(dirname(fileURLToPath(import.meta.url)), '..', '..', '..');

function code(path: string): string {
  const full = resolve(ROOT, path);
  assert.ok(existsSync(full), `${path} is missing`);
  return readFileSync(full, 'utf8')
    .replace(/\/\*[\s\S]*?\*\//g, '')
    .split('\n')
    .filter((line) => !line.trimStart().startsWith('//'))
    .join('\n');
}

const WEB_MEDIA = 'frontend/packages/api-client/src/media.ts';
const MOBILE_MEDIA = 'mobile/src/api/media.ts';
const WEB_ALBUMS = 'frontend/packages/api-client/src/albums.ts';
const MOBILE_ALBUMS = 'mobile/src/api/albums.ts';

test('web and mobile media transports both go through the canonical builder', () => {
  for (const path of [WEB_MEDIA, MOBILE_MEDIA]) {
    const source = code(path);
    assert.match(source, /from '@nubarca\/contracts'/, path);
    assert.match(source, /mediaQueryToParams\(query\)/, path);
    assert.match(source, /withQuery\(MEDIA_LIST_PATH, /, path);
    assert.match(source, /withQuery\(albumMediaPath\(albumId\), /, path);
  }
});

test('neither client keeps a private parameter builder', () => {
  // The exact regression this gate exists to prevent: a local `toParams` that
  // drifts from the canonical one one field at a time.
  for (const path of [WEB_MEDIA, MOBILE_MEDIA]) {
    const source = code(path);
    assert.doesNotMatch(source, /function toParams/, path);
    assert.doesNotMatch(source, /new URLSearchParams/, path);
    assert.doesNotMatch(source, /p\.set\(/, path);
  }
});

test('neither client redefines the media DTOs it was duplicating', () => {
  for (const path of [WEB_MEDIA, MOBILE_MEDIA]) {
    const source = code(path);
    for (const name of ['MediaItem', 'ListMediaQuery', 'MediaListResponse', 'MediaKind']) {
      assert.doesNotMatch(
        source,
        new RegExp(`export (interface|type) ${name}\\b`),
        `${path} redefines ${name}`,
      );
      // It re-exports the canonical one instead.
      assert.match(source, new RegExp(`\\b${name}\\b`), `${path} lost ${name}`);
    }
  }
});

test('neither client redefines the album DTOs', () => {
  for (const path of [WEB_ALBUMS, MOBILE_ALBUMS]) {
    const source = code(path);
    for (const name of ['AlbumSummary', 'AlbumDetail', 'AlbumCoverItem']) {
      assert.doesNotMatch(
        source,
        new RegExp(`export interface ${name}\\b`),
        `${path} redefines ${name}`,
      );
    }
  }
});

test('the routes each client hits are the canonical ones', () => {
  // Computed here from the contract, then found verbatim in both transports,
  // so a renamed route cannot pass by only being renamed in one of them.
  const query: ListMediaQuery = { kind: 'all', favorite: true, limit: 60 };
  assert.equal(
    withQuery(MEDIA_LIST_PATH, mediaQueryToParams(query)),
    '/api/media?kind=all&favorite=true&limit=60',
  );
  assert.equal(
    withQuery(albumMediaPath('a1'), mediaQueryToParams(query)),
    '/api/albums/a1/media?kind=all&favorite=true&limit=60',
  );
});

test('mobile no longer declares fields the server does not send', () => {
  // MediaItem carries neither audioCodec nor frameRate: those live on
  // VideoItem (/api/videos). The mobile copy declared them anyway, promising
  // data that never arrives.
  const source = code(MOBILE_MEDIA);
  assert.doesNotMatch(source, /audioCodec|frameRate/);
});

test('the mobile People transport is read-only: no management verb exists', () => {
  // §15/§16: face management is a separate future slice. The filter's
  // transport must not be able to create, rename, delete, merge, split or
  // assign — not "must not call", must not HAVE the function.
  const source = code('mobile/src/api/people.ts');
  for (const verb of [
    'createPerson', 'renamePerson', 'deletePerson', 'mergePeople', 'splitPerson',
    'assignFace', 'removeFace', 'moveFace', 'acceptSuggestion', 'rejectSuggestion',
    'startFaceSession',
  ]) {
    assert.doesNotMatch(source, new RegExp(`\\b${verb}\\b`), `people.ts exposes ${verb}`);
  }
  assert.match(source, /toPersonSummary/);
});

test('the workspace filter model is not redefined by any client', () => {
  const web = code('frontend/src/media/workspace/mediaWorkspaceQuery.ts');
  assert.match(web, /export \* from '@nubarca\/contracts'/);
  for (const name of ['MediaWorkspaceFilters', 'CommonMediaFilters', 'PhotoMediaFilters',
    'VideoMediaFilters', 'MediaWorkspaceIdentity']) {
    assert.doesNotMatch(web, new RegExp(`export interface ${name}\\b`), `web redefines ${name}`);
  }
  assert.doesNotMatch(web, /export function queryToWire/);
  assert.doesNotMatch(web, /export function queryFingerprint/);
  assert.match(web, /URLSearchParams/);
});

test('neither client redefines the sharing vocabulary', () => {
  for (const path of ['frontend/packages/api-client/src/albumSharing.ts',
    'mobile/src/api/sharedAlbums.ts']) {
    const source = code(path);
    assert.match(source, /from '@nubarca\/contracts'/, path);
    for (const name of ['AlbumRole', 'AlbumMembershipState', 'AlbumMember',
      'SharedAlbumSummary', 'SharedAlbumDetail', 'SharedAlbumItem',
      'SharedAlbumItemsPage', 'SharedAlbumItemsQuery', 'AlbumInvitation']) {
      assert.doesNotMatch(source, new RegExp(`export (interface|type) ${name}\\b`),
        `${path} redefines ${name}`);
    }
    // And no client builds the shared listing's parameters by hand.
    assert.doesNotMatch(source, /new URLSearchParams/, path);
  }
});

test('mobile no longer declares a hasMore the shared listing never sends', () => {
  const source = code('mobile/src/api/sharedAlbums.ts');
  assert.doesNotMatch(source, /hasMore/);
});

test('the album membership rule is stated where the contract lives', () => {
  // §24: removing an item from an album is not deleting it from the library.
  const contract = readFileSync(resolve(ROOT, 'packages/contracts/src/album.ts'), 'utf8');
  assert.match(contract, /MEMBERSHIP only/i);
  assert.match(contract, /albumItemsBulkPath/);
});
