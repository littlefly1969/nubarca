// Viewer IDENTITY isolation tests (final merge gate, review item B).
//
// The invariant under test: when the authenticated identity changes — account
// A → signed-out, or account A → account B — the FIRST observable viewer
// state under the new identity must be EMPTY. The mechanism is a keyed
// remount of ViewerProvider (app/_layout.tsx keys it on viewerIdentityKey):
// React unmounts/remounts on a key change, and the remounted provider builds
// a brand-new model BEFORE its first render. The old passive useEffect could
// only wipe AFTER such a render had already committed user A's sequence.
//
// The harness has no component renderer (plain node --test), so this file
// pins (a) the pure key contract that drives the remount, (b) the model-level
// behaviour of what a remount constructs, and (c) the wiring itself.

import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import { dirname, join } from 'node:path';
import { test } from 'node:test';
import { fileURLToPath } from 'node:url';
import { viewerIdentityKey, type ViewerIdentitySource } from './viewerIdentity.ts';
import { ViewerSequenceModel } from './viewerSequence.ts';

const here = dirname(fileURLToPath(import.meta.url));

async function sourceOf(relativePath: string): Promise<string> {
  return readFile(join(here, relativePath), 'utf8');
}

const AUTHED_A: ViewerIdentitySource = { status: 'authed', user: { id: 'user-a' } };
const AUTHED_B: ViewerIdentitySource = { status: 'authed', user: { id: 'user-b' } };
const ANON: ViewerIdentitySource = { status: 'unauthed', user: null };
const RESTORING: ViewerIdentitySource = { status: 'restoring', user: null };

test('every identity boundary produces a DIFFERENT key', () => {
  // The boundaries that MUST force a remount: A → signed-out, signed-out → B,
  // and a direct A → B switch.
  const keys = [
    viewerIdentityKey(AUTHED_A),
    viewerIdentityKey(ANON),
    viewerIdentityKey(AUTHED_B),
  ];
  assert.equal(new Set(keys).size, keys.length);
});

test('the SAME account keeps ONE key across re-renders (no gratuitous remount)', () => {
  // Opening the viewer, paging, closing — none of that may remount the
  // provider while the identity is unchanged.
  assert.equal(viewerIdentityKey(AUTHED_A), viewerIdentityKey({ ...AUTHED_A }));
  // restoring and unauthed are both "anonymous": no authenticated identity
  // exists in either phase, so there is nothing to isolate between them.
  assert.equal(viewerIdentityKey(RESTORING), viewerIdentityKey(ANON));
});

test('REGRESSION: first observable viewer state under a NEW identity is empty', () => {
  // Account A opens a sequence — their model now holds slides.
  const modelA = new ViewerSequenceModel(); // what mounting under key(A) builds
  modelA.open(
    [
      {
        key: 'item-1',
        kind: 'image',
        displayName: 'a.jpg',
        imagePath: '/api/files/item-1/thumbnail',
        videoSource: null,
        posterUrl: null,
      },
    ],
    'item-1', 'photos',
  );
  assert.notEqual(modelA.snapshot(), null);

  // The identity changes (logout or account B): the keys differ, so the
  // provider REMOUNTS and constructs a FRESH model. Its initial snapshot is
  // the first thing the new identity can possibly observe.
  assert.notEqual(viewerIdentityKey(AUTHED_A), viewerIdentityKey(ANON));
  assert.notEqual(viewerIdentityKey(AUTHED_A), viewerIdentityKey(AUTHED_B));

  const firstObservableUnderNewIdentity = new ViewerSequenceModel().snapshot();
  assert.equal(firstObservableUnderNewIdentity, null);
});

test('wiring: ViewerProvider is identity-keyed; the passive reset effect is GONE', async () => {
  const layout = await sourceOf('../../app/_layout.tsx');
  const context = await sourceOf('./viewerContext.tsx');

  // The provider must be keyed by identity (mount-time isolation)…
  assert.match(layout, /<ViewerProvider key=\{viewerIdentityKey\(session\)\}>/);
  // …and the racy after-render wipe must no longer exist anywhere.
  assert.doesNotMatch(context, /identityRef/);
  assert.doesNotMatch(context, /useEffect/);
});
