// Party contract (§30-§37, §50).

import assert from 'node:assert/strict';
import { test } from 'node:test';
import {
  DESTRUCTIVE_PARTY_MESSAGE_ACTIONS,
  PARTY_GAME_RANGES,
  PARTY_SLIDESHOW_RANGES,
  albumPartyMessageActionPath,
  clampToRange,
  invalidGameFields,
  invalidSlideshowFields,
  isPartyMessageActionAllowed,
  isWithinRange,
  partyGuestUrl,
  partyMessageActions,
  type PartyMessageAction,
  type PartyMessageModeration,
  type PartyMessageStatus,
} from './party.ts';

// ── the message transition matrix (§36) ─────────────────────────────────────

// The matrix needs only these two fields; a full PartyMessage satisfies the
// same shape structurally.
const msg = (status: PartyMessageStatus, isHero = false): PartyMessageModeration =>
  ({ status, isHero });

test('a pending message can be approved or rejected, and nothing else', () => {
  assert.deepEqual(partyMessageActions(msg('pending')), ['approve', 'reject']);
});

test('a visible message can be hidden or promoted', () => {
  assert.deepEqual(partyMessageActions(msg('visible')), ['hide', 'promote-hero']);
});

test('a hidden or rejected message can only be restored', () => {
  assert.deepEqual(partyMessageActions(msg('hidden')), ['restore']);
  assert.deepEqual(partyMessageActions(msg('rejected')), ['restore']);
});

test('Hero is offered only on a LIVE message, matching the server', () => {
  // The server refuses to promote anything not currently visible, so offering
  // it elsewhere would be a button that always fails.
  for (const status of ['pending', 'hidden', 'rejected'] as PartyMessageStatus[]) {
    assert.equal(isPartyMessageActionAllowed(msg(status), 'promote-hero'), false, status);
  }
  assert.equal(isPartyMessageActionAllowed(msg('visible'), 'promote-hero'), true);
});

test('an already-Hero message offers demotion instead of promotion', () => {
  const hero = msg('visible', true);
  assert.ok(!partyMessageActions(hero).includes('promote-hero'));
  assert.ok(partyMessageActions(hero).includes('demote-hero'));
});

test('every action is refused in the states it does not belong to', () => {
  const all: PartyMessageAction[] =
    ['approve', 'reject', 'hide', 'restore', 'promote-hero', 'demote-hero'];
  for (const status of ['pending', 'visible', 'hidden', 'rejected'] as PartyMessageStatus[]) {
    for (const isHero of [false, true]) {
      const message = msg(status, isHero);
      const allowed = new Set(partyMessageActions(message));
      for (const action of all) {
        assert.equal(
          isPartyMessageActionAllowed(message, action),
          allowed.has(action),
          `${status}${isHero ? '+hero' : ''} / ${action}`,
        );
      }
    }
  }
});

test('the actions that destroy visibility are named, so a client can confirm', () => {
  assert.deepEqual([...DESTRUCTIVE_PARTY_MESSAGE_ACTIONS], ['reject', 'hide']);
});

// ── ranges (§33, §34) ───────────────────────────────────────────────────────

test('a valid slideshow configuration reports no bad field', () => {
  assert.deepEqual(invalidSlideshowFields({
    photoSlideSeconds: 6,
    maxVideoSlideSeconds: 30,
    maxPhotoUploadsPerParticipant: 0,
    maxVideoUploadsPerParticipant: 5,
  }), []);
});

test('every out-of-range slideshow field is reported at once', () => {
  // One round-trip per bad field is a form that argues with the user.
  assert.deepEqual(invalidSlideshowFields({
    photoSlideSeconds: 1,
    maxVideoSlideSeconds: 9999,
    maxPhotoUploadsPerParticipant: -1,
    maxVideoUploadsPerParticipant: 99999,
  }).sort(), [
    'maxPhotoUploadsPerParticipant',
    'maxVideoSlideSeconds',
    'maxVideoUploadsPerParticipant',
    'photoSlideSeconds',
  ]);
});

test('zero is a legal quota: it means unlimited', () => {
  assert.equal(isWithinRange(0, PARTY_SLIDESHOW_RANGES.quota), true);
  assert.deepEqual(invalidSlideshowFields({
    photoSlideSeconds: 5,
    maxVideoSlideSeconds: 10,
    maxPhotoUploadsPerParticipant: 0,
    maxVideoUploadsPerParticipant: 0,
  }), []);
});

test('a valid game configuration reports no bad field', () => {
  assert.deepEqual(invalidGameFields({
    gameEnabled: true,
    minChallengeIntervalSeconds: 60,
    maxChallengeIntervalSeconds: 300,
    votesPerGuest: 3,
    maxChallengesPerSession: 10,
  }), []);
});

test('an INVERTED interval is caught even though both ends are in range', () => {
  // Each field passes on its own; the pair is nonsense. A per-field check
  // alone would let this reach the server.
  const bad = invalidGameFields({
    gameEnabled: true,
    minChallengeIntervalSeconds: 600,
    maxChallengeIntervalSeconds: 60,
    votesPerGuest: 3,
    maxChallengesPerSession: null,
  });
  assert.deepEqual(bad, ['maxChallengeIntervalSeconds']);
});

test('a null session cap is allowed and means no cap', () => {
  assert.deepEqual(invalidGameFields({
    gameEnabled: true,
    minChallengeIntervalSeconds: 60,
    maxChallengeIntervalSeconds: 120,
    votesPerGuest: 1,
    maxChallengesPerSession: null,
  }), []);
});

test('clamping is for steppers, and refuses nothing silently by itself', () => {
  assert.equal(clampToRange(1, PARTY_SLIDESHOW_RANGES.photoSeconds), 3);
  assert.equal(clampToRange(999, PARTY_SLIDESHOW_RANGES.photoSeconds), 60);
  assert.equal(clampToRange(10, PARTY_SLIDESHOW_RANGES.photoSeconds), 10);
  assert.equal(clampToRange(Number.NaN, PARTY_GAME_RANGES.votes), PARTY_GAME_RANGES.votes.min);
});

// ── the guest URL (§32) ─────────────────────────────────────────────────────

test('the guest URL is built from the SERVER path and the client origin', () => {
  // §32: no mobile token, no alternative URL. The server's relative path is
  // the only source, and a client only supplies where it is being read from.
  assert.equal(partyGuestUrl('https://x.example', '/party/abc'), 'https://x.example/party/abc');
  assert.equal(partyGuestUrl('https://x.example/', '/party/abc'), 'https://x.example/party/abc');
  assert.equal(partyGuestUrl('https://x.example///', '/party/abc'), 'https://x.example/party/abc');
});

test('party mode off means there is no link to share', () => {
  assert.equal(partyGuestUrl('https://x.example', null), null);
  assert.equal(partyGuestUrl('https://x.example', ''), null);
});

test('a message action route names the action the contract produced', () => {
  assert.equal(
    albumPartyMessageActionPath('a1', 'm1', 'promote-hero'),
    '/api/albums/a1/party-messages/m1/promote-hero',
  );
});
