// The selection capability matrix (§22, §38, §48).
//
// One answer for every client: the browser and the phone ask this same
// function, so an action cannot exist on one surface and quietly not on the
// other. `if (isMobile)` is what these rules replace.

import assert from 'node:assert/strict';
import { test } from 'node:test';
import { getMediaSelectionCapabilities } from './mediaSelection.ts';

const image = { kind: 'image' as const };
const video = { kind: 'video' as const };
const caps = (
  items: Array<{ kind: 'image' | 'video' }>,
  source: 'library' | 'album' = 'library',
  scope: 'active' | 'excluded' = 'active',
) => getMediaSelectionCapabilities({ items, source, scope });

test('an empty selection can do nothing at all', () => {
  const c = caps([]);
  for (const [name, value] of Object.entries(c)) {
    assert.equal(value, false, `${name} was true for an empty selection`);
  }
});

test('a normal selection can be filed, trashed and excluded', () => {
  const c = caps([image, video]);
  assert.equal(c.canAddToAlbum, true);
  assert.equal(c.canTrash, true);
  assert.equal(c.canMoveToExcluded, true);
  assert.equal(c.canMoveToPersonal, true);
});

test('mixed selections are supported where the action supports both kinds', () => {
  // §20: an add-to-album or a trash works on photos and videos together.
  const c = caps([image, video]);
  assert.equal(c.mixed, true);
  assert.equal(c.allImages, false);
  assert.equal(c.allVideos, false);
  assert.equal(c.canAddToAlbum, true);
  assert.equal(c.canTrash, true);
});

test('a photo-only destination never runs partially over a mixed selection', () => {
  // The failure this prevents: half a selection sent to a photo-only tool and
  // the videos silently skipped.
  assert.equal(caps([image, image]).canUsePhotoOnlyDestinations, true);
  assert.equal(caps([image, video]).canUsePhotoOnlyDestinations, false);
  assert.equal(caps([video, video]).canUsePhotoOnlyDestinations, false);
});

test('scope decides between excluding and restoring, and never offers both', () => {
  const active = caps([image], 'library', 'active');
  assert.equal(active.canMoveToExcluded, true);
  assert.equal(active.canRestore, false);

  const excluded = caps([image], 'library', 'excluded');
  assert.equal(excluded.canMoveToExcluded, false);
  assert.equal(excluded.canRestore, true);
});

test('removing from THIS album is offered only inside an album', () => {
  assert.equal(caps([image], 'library').canRemoveFromCurrentAlbum, false);
  assert.equal(caps([image], 'album').canRemoveFromCurrentAlbum, true);
});

test('being inside an album does not take the library actions away', () => {
  // Album removal and trashing coexist: they are different verbs on different
  // things, and an album view still lets you delete the underlying file.
  const c = caps([image], 'album');
  assert.equal(c.canRemoveFromCurrentAlbum, true);
  assert.equal(c.canTrash, true);
  assert.equal(c.canAddToAlbum, true);
});
