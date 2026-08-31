import assert from 'node:assert/strict';
import test from 'node:test';
import { read } from '../testing/sourceText.ts';

const viewer = read(import.meta.url, '../screens/ViewerScreen.tsx');
const api = read(import.meta.url, '../api/tv.ts');
const hold = read(import.meta.url, '../components/PartyChallengeHold.tsx');
const qr = read(import.meta.url, '../components/OverlayQrCorners.tsx');

test('challenge state uses the existing paired-TV API and poll loop', () => {
  assert.match(api, /\/party-playback/);
  assert.match(viewer, /getTvPartyPlayback\(albumId\)/);
  assert.match(viewer, /setInterval\(poll,\s*PARTY_PLAYBACK_POLL_MS\)/);
});

test('all natural media boundaries consult persisted challenge state', () => {
  assert.match(viewer, /advanceTvPartyBoundary\(albumId\)/);
  assert.match(viewer, /setTimeout\(handleMediaBoundary,\s*photoMs\)/);
  assert.match(viewer, /onEnded=\{handleMediaBoundary\}/);
  assert.match(viewer, /onCapReached=\{handleMediaBoundary\}/);
});

test('ChallengeHold has no timeout and existing next completes it', () => {
  assert.doesNotMatch(hold, /setTimeout|setInterval/);
  assert.match(viewer, /completeTvPartyChallenge\(albumId\)/);
  assert.match(viewer, /challengeHeld \? false : isVideoRef\.current/);
  assert.match(viewer, /playing: playing && hero === null && !challengeHeld/);
});

test('the visible Party overlay publishes one canonical QR', () => {
  assert.match(qr, /const cards = \[/);
  assert.match(qr, /items\.partyGuestHub/);
  assert.doesNotMatch(qr, /caption:\s*t\('items\.uploadPhotos'\)/);
});
