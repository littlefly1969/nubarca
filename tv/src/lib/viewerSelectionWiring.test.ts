import assert from 'node:assert/strict';
import test from 'node:test';
import { read } from '../testing/sourceText.ts';

// The Personal viewer's remote contract is decided by pure, tested modules
// (video/remoteMap.ts, lib/tvSelection.ts). These assertions hold the WIRING:
// that the viewer actually asks for the selection contract, gives the D-pad to
// the action bar while it is open, and lets BACK close the bar before the
// viewer. They cannot replace a run on a Fire TV / Android TV device.

const source = (relativePath: string) => read(import.meta.url, relativePath);
const viewer = source('../screens/library/PersonalMediaViewer.tsx');
const library = source('../screens/PersonalLibraryScreen.tsx');
const app = source('../../App.tsx');
const bar = source('../components/ViewerActionBar.tsx');
const tile = source('../components/FocusableMediaTile.tsx');

test('the Personal viewer runs the selection contract', () => {
  assert.match(viewer, /mapViewerRemoteEvent\(eventType, isVideo, \{ selectable \}\)/);
  assert.match(viewer, /case 'toggle-select': toggleSelected\(\)/);
  assert.match(viewer, /case 'open-actions': setBarOpen\(true\)/);
  // Manual navigation pauses a running slideshow; selecting does not touch it.
  assert.match(viewer, /case 'prev': if \(selectable\) setSlideshow\(false\); goPrev\(\)/);
  assert.match(viewer, /case 'next': if \(selectable\) setSlideshow\(false\); goNext\(\)/);
  assert.doesNotMatch(viewer.slice(viewer.indexOf('const toggleSelected')), /^\s*const toggleSelected[^}]*setSlideshow/m);
});

test('while the action bar is open the D-pad and OK belong to it; BACK closes it first', () => {
  assert.match(viewer, /if \(barOpenRef\.current && action !== 'toggle-play' && action !== 'toggle-overlay'\)/);
  assert.match(viewer, /if \(barOpenRef\.current\) \{ setBarOpen\(false\); return true; \}/);
  assert.match(bar, /<MenuCommandRail/);
  assert.match(bar, /hasTVPreferredFocus=\{i === 0\}/);
});

test('the Personal Area shares one selection and drops it when it is left', () => {
  assert.match(app, /new TvSelectionStore\(\)/);
  assert.match(app, /if \(!inPersonal\) personalSelection\.clear\(\)/);
  assert.equal(app.match(/selection=\{personalSelection\}/g)?.length, 2);
  assert.match(library, /selected=\{selection\?\.has\(item\.id\) \?\? false\}/);
  assert.match(library, /extraData=\{selectionVersion\}/);
  assert.match(tile, /accessibilityState=\{\{ selected \}\}/);
});
