import assert from 'node:assert/strict';
import test from 'node:test';
import { read } from '../testing/sourceText.ts';

const source = (relativePath: string) => read(import.meta.url, relativePath);

const app = source('../../App.tsx');
const tvApi = source('../api/tv.ts');
const modeScreen = source('../screens/ModeSelectScreen.tsx');

// Structural guarantees about what a paired television is FOR.
//
// Pairing answers who this device is and produces a credential; the assignment
// answers what it shows and is ordinary server-side state. These assert the two
// stay apart on the device side — the television reads its assignment, it never
// decides or stores one, and nothing here changes what the general experience
// serves.
//
// Source-text assertions, in the same style as tvMediaGridWiring: comments are
// stripped before matching (see testing/sourceText), so the prose in these files
// cannot make a negative assertion pass.

test('the assignment travels with the session and carries no link or token', () => {
  assert.match(tvApi, /interface TvDisplayAssignment\b/);
  assert.match(tvApi, /kind:\s*'general'\s*\|\s*'party'/);
  // Named by ALBUM. The party link id is an internal identifier and must never
  // reach a television, which is also why there is nothing here to leak.
  assert.match(tvApi, /albumId:\s*string \| null/);
  assert.doesNotMatch(tvApi, /partyAlbumLinkId/i);
  assert.doesNotMatch(tvApi, /interface TvDisplayAssignment[\s\S]{0,300}token/i);

  // It arrives on the session the app already reads — additive, so an older APK
  // simply never sees the field.
  assert.match(tvApi, /interface TvSessionStatus[\s\S]{0,400}assignment:\s*TvDisplayAssignment/);
});

test('the television READS its assignment and never decides one', () => {
  // Read from the session, at startup and after pairing.
  assert.match(app, /setAssignment\(session\.assignment \?\? null\)/);
  // Re-read on the mode selector's existing beat, so an owner changing it on
  // the web reaches this device without re-pairing it.
  assert.match(app, /getTvSession\(\)[\s\S]{0,200}setAssignment/);

  // The TV app has no way to CHANGE an assignment. Those routes are owner
  // endpoints outside /api/tv, and the app must never learn they exist.
  assert.doesNotMatch(app, /tv-devices/);
  assert.doesNotMatch(tvApi, /tv-devices/);
  assert.doesNotMatch(app, /setTvDeviceAssignment/);

  // And it is not persisted: the server is the authority on every poll, so a
  // stale local copy could never outlive a revoked party.
  assert.doesNotMatch(app, /AsyncStorage[\s\S]{0,120}assignment/i);
});

test('the assignment is displayed and changes no navigation yet', () => {
  // The mode selector shows what this TV is set to…
  assert.match(modeScreen, /assignment\?:\s*TvDisplayAssignment \| null/);
  assert.match(modeScreen, /mode\.assignedParty/);

  // …and nothing reads it to choose a screen. This slice creates the concept;
  // a party assignment is not yet allowed to take the television over, and the
  // four mode buttons are exactly the ones that were there before.
  assert.doesNotMatch(modeScreen, /assignment[\s\S]{0,200}onChooseParty\(/);
  assert.match(modeScreen, /onPress=\{onChooseParty\}/);
  assert.match(modeScreen, /onPress=\{onChoosePersonal\}/);
  assert.match(modeScreen, /onPress=\{onChooseBeautyLab\}/);
  assert.match(modeScreen, /onPress=\{onChooseUpdates\}/);

  // The party experience is still entered by the SAME event as before — the
  // assignment does not fork the flow.
  assert.match(app, /rawDispatch\(\{ type: 'CHOOSE_PARTY' \}\)/);
  assert.doesNotMatch(app, /assignment[\s\S]{0,120}CHOOSE_PARTY/);
});
