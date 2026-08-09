import assert from 'node:assert/strict';
import test from 'node:test';
import { mapViewerRemoteEvent } from './remoteMap.ts';

test('MENU owns the overlay for photos and videos alike', () => {
  assert.equal(mapViewerRemoteEvent('menu', false), 'toggle-overlay');
  assert.equal(mapViewerRemoteEvent('menu', true), 'toggle-overlay');
});

test('photo semantics are unchanged: LEFT/RIGHT navigate, SELECT reserved', () => {
  assert.equal(mapViewerRemoteEvent('left', false), 'prev');
  assert.equal(mapViewerRemoteEvent('longLeft', false), 'prev');
  assert.equal(mapViewerRemoteEvent('right', false), 'next');
  assert.equal(mapViewerRemoteEvent('longRight', false), 'next');
  assert.equal(mapViewerRemoteEvent('playPause', false), 'toggle-play');
  assert.equal(mapViewerRemoteEvent('select', false), 'none');
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

test('the Fire TV transport keys seek on a video and are inert on a photo', () => {
  // REWIND / FAST_FORWARD are the platform's media convention: a remote that
  // has them must seek with them, whatever the D-pad is doing. On a photo there
  // is nothing to seek, so they stay inert rather than becoming a second,
  // undiscoverable way to change picture.
  assert.equal(mapViewerRemoteEvent('rewind', true), 'seek-back');
  assert.equal(mapViewerRemoteEvent('fastForward', true), 'seek-forward');
  assert.equal(mapViewerRemoteEvent('rewind', false), 'none');
  assert.equal(mapViewerRemoteEvent('fastForward', false), 'none');
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
