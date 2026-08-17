import { strict as assert } from 'node:assert';
import { test } from 'node:test';
import { remapIndexById, sameItemIds } from './liveItems.ts';
import type { TvAlbumItem } from '../api/tv.ts';

// A party wall refreshes WHILE it is playing. The rule these two helpers
// enforce is that a refresh is not an interruption: a guest uploading a photo
// must never yank the picture off the screen, restart the slideshow, or move
// the viewer to a different item.

function item(id: string, mediaType: 'image' | 'video' = 'image'): TvAlbumItem {
  return {
    id,
    name: id,
    mediaType,
    width: 1920,
    height: 1080,
    thumbnailUrl: `/t/${id}`,
    previewUrl: `/p/${id}`,
    posterUrl: mediaType === 'video' ? `/poster/${id}` : null,
    videoUrl: mediaType === 'video' ? `/v/${id}` : null,
  };
}

test('an unchanged list is recognised so a poll causes no re-render', () => {
  const a = [item('1'), item('2')];
  assert.equal(sameItemIds(a, [item('1'), item('2')]), true);
  assert.equal(sameItemIds(a, [item('2'), item('1')]), false);
  assert.equal(sameItemIds(a, [item('1')]), false);
  assert.equal(sameItemIds([], []), true);
});

test('a new guest upload appends without moving the current item', () => {
  // The wall is showing item 2 of 3 when a guest uploads a photo AND a video.
  const before = [item('a'), item('b'), item('c')];
  const after = [...before, item('d'), item('e', 'video')];

  assert.equal(sameItemIds(before, after), false, 'the poll must notice the append');
  // Still on 'b' — the index moved only if the item did, and it did not.
  assert.equal(remapIndexById(after, 'b', 1), 1);
  assert.equal(after[remapIndexById(after, 'b', 1)].id, 'b');
});

test('the current item is tracked by id, not by position', () => {
  // The owner removed an EARLIER item, so everything after it shifts down. The
  // viewer must follow the item it was showing, not stay on index 2.
  const after = [item('a'), item('c'), item('d')];
  assert.equal(remapIndexById(after, 'c', 2), 1);
});

test('a removed current item falls back to the nearest position, never past the end', () => {
  const after = [item('a'), item('b')];
  assert.equal(remapIndexById(after, 'gone', 5), 1);
  assert.equal(remapIndexById(after, 'gone', 0), 0);
  assert.equal(remapIndexById(after, undefined, 1), 1);
  // An emptied album has no valid index at all.
  assert.equal(remapIndexById([], 'a', 3), 0);
});

test('a mixed append keeps working when the current item is a video', () => {
  const before = [item('p1'), item('v1', 'video')];
  const after = [...before, item('p2')];
  assert.equal(remapIndexById(after, 'v1', 1), 1);
  assert.equal(after[1].mediaType, 'video');
});
