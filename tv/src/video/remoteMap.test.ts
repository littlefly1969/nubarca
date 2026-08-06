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

test('unknown events are inert in both modes', () => {
  assert.equal(mapViewerRemoteEvent('fastForward', false), 'none');
  assert.equal(mapViewerRemoteEvent('fastForward', true), 'none');
});
