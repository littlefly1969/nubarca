import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import test from 'node:test';
import {
  actionableEventType, isMediaTransportEvent, MEDIA_TRANSPORT_EVENTS,
  REMOTE_ACTION_DOWN, shouldActOnRemoteEvent,
} from './remoteEvent.ts';

// One physical press must produce exactly one semantic action. The failure this
// guards is the "one press skipped two photos" defect: react-native-tvos
// delivers key-down AND key-up, and a handler that ignores the phase fires
// twice.

test('an explicit key-down never produces an action', () => {
  assert.equal(shouldActOnRemoteEvent({ eventType: 'right', eventKeyAction: REMOTE_ACTION_DOWN }), false);
  assert.equal(actionableEventType({ eventType: 'right', eventKeyAction: 0 }), null);
});

test('a key-up produces exactly one action', () => {
  assert.equal(shouldActOnRemoteEvent({ eventType: 'right', eventKeyAction: 1 }), true);
  assert.equal(actionableEventType({ eventType: 'right', eventKeyAction: 1 }), 'right');
});

test('a runtime that omits the phase still works the remote, once', () => {
  // Requiring `=== 1` would make the remote silently dead on these runtimes,
  // which is why the rule is "anything that is not an explicit DOWN acts".
  assert.equal(shouldActOnRemoteEvent({ eventType: 'select' }), true);
  assert.equal(actionableEventType({ eventType: 'select' }), 'select');
  assert.equal(actionableEventType({ eventType: 'select', eventKeyAction: undefined }), 'select');
});

test('one press yields one action across all three phase shapes', () => {
  const press = (phases: (number | undefined)[]) =>
    phases.map((eventKeyAction) => actionableEventType({ eventType: 'left', eventKeyAction }))
      .filter((type) => type !== null);
  // down + up = one action.
  assert.deepEqual(press([0, 1]), ['left']);
  // phase-less runtime delivers once = one action.
  assert.deepEqual(press([undefined]), ['left']);
  // down alone = nothing.
  assert.deepEqual(press([0]), []);
});

test('a missing or empty event is inert', () => {
  assert.equal(shouldActOnRemoteEvent(null), false);
  assert.equal(shouldActOnRemoteEvent(undefined), false);
  assert.equal(actionableEventType({ eventKeyAction: 1 }), null);
  assert.equal(actionableEventType({ eventType: '', eventKeyAction: 1 }), null);
});

test('the transport vocabulary is closed and recognised', () => {
  for (const key of MEDIA_TRANSPORT_EVENTS) {
    assert.equal(isMediaTransportEvent(key), true, key);
  }
  // The five-way keys and MENU are NOT transport keys — a screen that ignores
  // transport keys must still work normally.
  for (const key of ['up', 'down', 'left', 'right', 'select', 'menu', 'back']) {
    assert.equal(isMediaTransportEvent(key), false, key);
  }
});

test('non-media screens leave transport keys to the platform', () => {
  const code = (path: string) => readFileSync(new URL(path, import.meta.url), 'utf8')
    .split('\n').filter((l) => !l.trimStart().startsWith('//')).join('\n');
  // Repurposing Play/Pause to activate a focused button would steal transport
  // control from whatever else the television is playing.
  for (const screen of [
    '../screens/PersonalLibraryScreen.tsx',
    '../screens/AlbumItemsScreen.tsx',
    '../screens/BeautyLabScreen.tsx',
  ]) {
    const source = code(screen);
    assert.match(source, /if \(isMediaTransportEvent\(eventType\)\) return;/,
      `${screen} must not consume transport keys outside a media context`);
    assert.match(source, /actionableEventType\(evt\)/,
      `${screen} must use the shared phase rule`);
    assert.doesNotMatch(source, /evt\.eventKeyAction === 0/,
      `${screen} still carries its own copy of the phase rule`);
  }
});

test('the viewers use the shared phase rule too', () => {
  for (const viewer of [
    '../screens/library/PersonalMediaViewer.tsx',
    '../screens/ViewerScreen.tsx',
  ]) {
    const source = readFileSync(new URL(viewer, import.meta.url), 'utf8');
    assert.match(source, /actionableEventType\(evt\)/, viewer);
    assert.doesNotMatch(source, /evt\.eventKeyAction === 0/, viewer);
  }
});
