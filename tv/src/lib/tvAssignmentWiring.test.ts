import assert from 'node:assert/strict';
import test from 'node:test';
import { read } from '../testing/sourceText.ts';

const source = (relativePath: string) => read(import.meta.url, relativePath);

const app = source('../../App.tsx');
const tvApi = source('../api/tv.ts');
const modeScreen = source('../screens/ModeSelectScreen.tsx');
const view = source('./assignmentView.ts');

// Structural guarantees about what a paired television is FOR.
//
// Pairing answers who this device is and produces a credential; the assignment
// answers what it shows and is ordinary server-side state. These assert the two
// stay apart on the device side — the television reads its assignment and the
// presentation projected beside it, and follows them; it never decides or
// stores either.
//
// Source-text assertions, in the same style as tvMediaGridWiring: comments are
// stripped before matching (see testing/sourceText), so the prose in these files
// cannot make a negative assertion pass. The navigation itself is exercised
// behaviourally in personal/flow.test.ts.

test('the assignment travels with the session and carries no link or token', () => {
  assert.match(tvApi, /interface TvDisplayAssignment\b/);
  assert.match(tvApi, /kind:\s*'general'\s*\|\s*'party'/);
  // Named by ALBUM. The party link id is an internal identifier and must never
  // reach a television, which is also why there is nothing here to leak.
  assert.match(tvApi, /albumId:\s*string \| null/);
  assert.doesNotMatch(tvApi, /partyAlbumLinkId/i);
  assert.doesNotMatch(tvApi, /interface TvDisplayAssignment[\s\S]{0,600}token/i);
  // The presentation is four words, and none of them is a game phase.
  assert.match(tvApi, /presentation\?: 'general' \| 'slideshow' \| 'game' \| 'unavailable'/);

  // It arrives on the session the app already reads — additive, so an older APK
  // simply never sees the fields.
  assert.match(tvApi, /interface TvSessionStatus[\s\S]{0,400}assignment:\s*TvDisplayAssignment/);
});

test('the television READS its assignment and never decides one', () => {
  // Read from the session, at startup, after pairing, and on every control read.
  assert.match(app, /toAssignmentView\(session\.assignment\)/);
  assert.match(view, /export function toAssignmentView/);

  // The TV app has no way to CHANGE an assignment. Those routes are owner
  // endpoints outside /api/tv, and the app must never learn they exist.
  assert.doesNotMatch(app, /tv-devices/);
  assert.doesNotMatch(tvApi, /tv-devices/);
  assert.doesNotMatch(app, /setTvDeviceAssignment/);

  // And it is not persisted: the server is the authority on every poll, so a
  // stale local copy could never outlive a revoked party.
  assert.doesNotMatch(app, /AsyncStorage[\s\S]{0,120}assignment/i);
});

test('the assignment drives navigation, and only through the flow reducer', () => {
  // A party assignment takes the television: every control read is an
  // ASSIGNMENT event, and the reducer is the one place that decides what it
  // does to the screen (personal/flow.ts, tested in flow.test.ts).
  assert.match(app, /dispatch\(\{ type: 'ASSIGNMENT', view \}\)/);

  // The mode selector no longer shows or reads an assignment — an assigned
  // television never reaches it — and its four buttons are unchanged.
  assert.doesNotMatch(modeScreen, /assignment/i);
  assert.match(modeScreen, /onPress=\{onChooseParty\}/);
  assert.match(modeScreen, /onPress=\{onChoosePersonal\}/);
  assert.match(modeScreen, /onPress=\{onChooseBeautyLab\}/);
  assert.match(modeScreen, /onPress=\{onChooseUpdates\}/);

  // Manual Party browsing on a GENERAL television is still the same event.
  assert.match(app, /rawDispatch\(\{ type: 'CHOOSE_PARTY' \}\)/);
  assert.doesNotMatch(app, /assignment[\s\S]{0,120}CHOOSE_PARTY/);
});

test('leaving a personal screen for a party goes through the teardown rules', () => {
  // The ASSIGNMENT is dispatched through the wrapper that applies flowEffects,
  // so a party taking the screen from somebody's library revokes their grant.
  assert.match(app, /const effects = flowEffects\(flowRef\.current, event\);/);
  assert.match(app, /if \(effects\.revokeGrant\) void lockTvPersonal\(\);/);
  // The unlock is dispatched the same way, so one landing after a preemption
  // is revoked rather than kept.
  assert.match(app, /dispatch\(\{ type: 'UNLOCKED', home \}\)/);
});
