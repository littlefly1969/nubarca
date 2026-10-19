import assert from 'node:assert/strict';
import test from 'node:test';
import { mapViewerRemoteEvent, viewerBarActions } from './remoteMap.ts';

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

// ---- the selection contract (a viewer opened from a grid) ------------------

const sel = { selectable: true };

test('OK selects the photo on screen; PLAY/PAUSE owns the slideshow', () => {
  assert.equal(mapViewerRemoteEvent('select', false, sel), 'toggle-select');
  assert.equal(mapViewerRemoteEvent('playPause', false, sel), 'toggle-play');
  assert.equal(mapViewerRemoteEvent('left', false, sel), 'prev');
  assert.equal(mapViewerRemoteEvent('right', false, sel), 'next');
  assert.equal(mapViewerRemoteEvent('rewind', false, sel), 'prev');
  assert.equal(mapViewerRemoteEvent('fastForward', false, sel), 'next');
  assert.equal(mapViewerRemoteEvent('menu', false, sel), 'toggle-overlay');
});

test('UP opens the action bar for photos and videos alike', () => {
  for (const isVideo of [false, true]) {
    assert.equal(mapViewerRemoteEvent('up', isVideo, sel), 'open-actions');
    assert.equal(mapViewerRemoteEvent('longUp', isVideo, sel), 'open-actions');
  }
});

test('a video keeps OK as play/pause and LEFT/RIGHT as seek; DOWN is the next item', () => {
  assert.equal(mapViewerRemoteEvent('select', true, sel), 'toggle-play');
  assert.equal(mapViewerRemoteEvent('playPause', true, sel), 'toggle-play');
  assert.equal(mapViewerRemoteEvent('left', true, sel), 'seek-back');
  assert.equal(mapViewerRemoteEvent('right', true, sel), 'seek-forward');
  assert.equal(mapViewerRemoteEvent('down', true, sel), 'next');
});

test('BACK and system keys stay unmapped in the selection contract too', () => {
  for (const key of ['back', 'home', 'info', 'stop', 'guide']) {
    assert.equal(mapViewerRemoteEvent(key, false, sel), 'none');
    assert.equal(mapViewerRemoteEvent(key, true, sel), 'none');
  }
});

test('the five-way keys reach selection, navigation and the action bar', () => {
  const fiveWay = ['up', 'down', 'left', 'right', 'select'];
  const reachable = (isVideo: boolean) =>
    new Set(fiveWay.map((key) => mapViewerRemoteEvent(key, isVideo, sel)).filter((a) => a !== 'none'));
  // The slideshow is reached through the bar's on-screen Play/Pause.
  assert.deepEqual(reachable(false), new Set(['open-actions', 'prev', 'next', 'toggle-select']));
  assert.deepEqual(reachable(true), new Set(['open-actions', 'toggle-play', 'seek-back', 'seek-forward', 'next']));
});

test('the action bar always offers an on-screen Play/Pause, and select for a video', () => {
  assert.deepEqual(viewerBarActions({ isVideo: false, selectedCount: 0 }), ['play-pause', 'select']);
  assert.deepEqual(viewerBarActions({ isVideo: true, selectedCount: 0 }), ['play-pause', 'select', 'prev', 'next']);
  assert.deepEqual(viewerBarActions({ isVideo: false, selectedCount: 3 }), ['play-pause', 'select', 'clear-selection']);
});

test('the unattended party wall keeps its slideshow semantics', () => {
  assert.equal(mapViewerRemoteEvent('select', false, { selectable: false }), 'toggle-play');
  assert.equal(mapViewerRemoteEvent('up', false), 'none');
});
