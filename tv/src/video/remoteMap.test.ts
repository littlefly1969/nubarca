import assert from 'node:assert/strict';
import test from 'node:test';
import { mapViewerRemoteEvent } from './remoteMap.ts';

test('MENU owns the overlay for photos and videos alike', () => {
  assert.equal(mapViewerRemoteEvent('menu', false), 'toggle-overlay');
  assert.equal(mapViewerRemoteEvent('menu', true), 'toggle-overlay');
});

test('a photo slideshow is operable with SELECT — the five-way route', () => {
  // SELECT used to return 'none' here, and that was an accessibility defect,
  // not a reservation: on a remote with no dedicated play/pause key there was
  // NO way to start a slideshow from inside the viewer. Every product function
  // must be reachable with UP/DOWN/LEFT/RIGHT/SELECT/BACK alone.
  assert.equal(mapViewerRemoteEvent('select', false), 'toggle-play');
  assert.equal(mapViewerRemoteEvent('playPause', false), 'toggle-play',
    'the transport key is an accelerator for the same semantic action');
  assert.equal(mapViewerRemoteEvent('left', false), 'prev');
  assert.equal(mapViewerRemoteEvent('longLeft', false), 'prev');
  assert.equal(mapViewerRemoteEvent('right', false), 'next');
  assert.equal(mapViewerRemoteEvent('longRight', false), 'next');
  // A photo has no second axis, so UP/DOWN stay inert rather than becoming an
  // undiscoverable duplicate of LEFT/RIGHT.
  assert.equal(mapViewerRemoteEvent('up', false), 'none');
  assert.equal(mapViewerRemoteEvent('down', false), 'none');
});

test('video semantics: SELECT/playPause toggle, LEFT/RIGHT seek', () => {
  assert.equal(mapViewerRemoteEvent('select', true), 'toggle-play');
  assert.equal(mapViewerRemoteEvent('playPause', true), 'toggle-play');
  assert.equal(mapViewerRemoteEvent('left', true), 'seek-back');
  assert.equal(mapViewerRemoteEvent('longLeft', true), 'seek-back');
  assert.equal(mapViewerRemoteEvent('right', true), 'seek-forward');
  assert.equal(mapViewerRemoteEvent('longRight', true), 'seek-forward');
});

test('video navigation moves to UP/DOWN while seeking owns LEFT/RIGHT', () => {
  assert.equal(mapViewerRemoteEvent('up', true), 'prev');
  assert.equal(mapViewerRemoteEvent('longUp', true), 'prev');
  assert.equal(mapViewerRemoteEvent('down', true), 'next');
  assert.equal(mapViewerRemoteEvent('longDown', true), 'next');
});

test('the transport keys seek a video and step through photos', () => {
  // REWIND / FAST_FORWARD are the platform's media convention: a remote that
  // has them must use them, whatever the D-pad is doing. On a photo there is
  // nothing to seek, so they carry the SAME meaning LEFT/RIGHT already do —
  // an accelerator, never a second feature, and never a hidden one.
  assert.equal(mapViewerRemoteEvent('rewind', true), 'seek-back');
  assert.equal(mapViewerRemoteEvent('fastForward', true), 'seek-forward');
  assert.equal(mapViewerRemoteEvent('rewind', false), 'prev');
  assert.equal(mapViewerRemoteEvent('fastForward', false), 'next');
});

test('every viewer action is reachable with the five-way keys alone', () => {
  // The CORE RULE: no product function may REQUIRE menu or a transport key.
  const fiveWay = ['up', 'down', 'left', 'right', 'select'];
  const reachable = (isVideo: boolean) =>
    new Set(fiveWay.map((key) => mapViewerRemoteEvent(key, isVideo))
      .filter((action) => action !== 'none'));

  // Photos: navigate both ways and operate the slideshow.
  assert.deepEqual(reachable(false), new Set(['prev', 'next', 'toggle-play']));
  // Videos: play/pause, seek both ways, change item both ways.
  assert.deepEqual(reachable(true),
    new Set(['toggle-play', 'seek-back', 'seek-forward', 'prev', 'next']));

  // And what the accelerators add is nothing NEW — only faster routes.
  for (const isVideo of [false, true]) {
    for (const key of ['playPause', 'rewind', 'fastForward']) {
      const action = mapViewerRemoteEvent(key, isVideo);
      assert.ok(action === 'none' || reachable(isVideo).has(action),
        `${key} on ${isVideo ? 'video' : 'photo'} offers ${action}, which the five-way keys cannot reach`);
    }
  }
});

test('unknown and system events are inert in both modes', () => {
  // BACK and HOME must never be spent as playback controls: BACK is navigation
  // (handled by the viewer's own BackHandler) and HOME is a system action the
  // app does not intercept at all.
  for (const key of ['back', 'home', 'info', 'stop', 'guide']) {
    assert.equal(mapViewerRemoteEvent(key, false), 'none');
    assert.equal(mapViewerRemoteEvent(key, true), 'none');
  }
});
