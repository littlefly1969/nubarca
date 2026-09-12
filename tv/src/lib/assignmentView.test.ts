import assert from 'node:assert/strict';
import test from 'node:test';
import {
  BACKOFF_BASE_MS,
  BACKOFF_MAX_MS,
  CONTROL_POLL_MS,
  SESSION_HEARTBEAT_MS,
  backoffMs,
  shouldHeartbeat,
  toAssignmentView,
} from './assignmentView.ts';

// The server's assignment, read as a navigation decision. The shell's whole
// vocabulary for a party is four words, and this is where the DTO becomes them.

const party = (over: Record<string, unknown> = {}) => ({
  kind: 'party' as const,
  albumId: 'album-1',
  albumName: 'Festa',
  partyAvailable: true,
  presentation: 'game' as const,
  assignmentKey: 'key-1',
  ...over,
});

test('a general assignment, and no assignment at all, is the general experience', () => {
  assert.deepEqual(toAssignmentView(null), { presentation: 'general' });
  assert.deepEqual(toAssignmentView(undefined), { presentation: 'general' });
  assert.deepEqual(
    toAssignmentView({ kind: 'general', albumId: null, albumName: null, partyAvailable: false }),
    { presentation: 'general' });
});

test("the server's presentation is taken exactly as given", () => {
  for (const presentation of ['slideshow', 'game', 'unavailable'] as const) {
    assert.deepEqual(toAssignmentView(party({ presentation })), {
      presentation,
      party: { key: 'key-1', albumId: 'album-1', albumName: 'Festa' },
    });
  }
});

test('a server that predates the presentation keeps its own previous contract', () => {
  // Then, an available party on screen WAS its game, and an unavailable one
  // said so. A newer shell must not make an older server show anything else.
  const legacy = { kind: 'party' as const, albumId: 'album-1', albumName: 'Festa' };
  assert.equal(toAssignmentView({ ...legacy, partyAvailable: true }).presentation, 'game');
  assert.equal(toAssignmentView({ ...legacy, partyAvailable: false }).presentation, 'unavailable');
});

test('a slideshow with no album to show fails closed', () => {
  assert.equal(toAssignmentView(party({ presentation: 'slideshow', albumId: null })).presentation,
    'unavailable');
});

test("the party's identity is the server's opaque key", () => {
  const view = toAssignmentView(party());
  assert.equal(view.presentation !== 'general' && view.party.key, 'key-1');
  // Only a server without keys falls back to the album — which is still
  // enough to tell Party A from Party B.
  const legacy = toAssignmentView(party({ assignmentKey: undefined }));
  assert.equal(legacy.presentation !== 'general' && legacy.party.key, 'album:album-1');
});

test('the takeover bound is five seconds, and presence is written once a minute', () => {
  // The prompt the owner sees on the web is "the television will switch within
  // a few seconds". That promise is this number.
  assert.equal(CONTROL_POLL_MS, 5_000);
  assert.equal(SESSION_HEARTBEAT_MS, 60_000);

  // The first read beats, so presence is stamped as soon as the app is up…
  assert.equal(shouldHeartbeat(null, 0), true);
  // …and after that once a minute, not on every five-second read.
  assert.equal(shouldHeartbeat(0, CONTROL_POLL_MS), false);
  assert.equal(shouldHeartbeat(0, SESSION_HEARTBEAT_MS - 1), false);
  assert.equal(shouldHeartbeat(0, SESSION_HEARTBEAT_MS), true);
  const beatsPerMinute = Array.from({ length: 12 }, (_, i) => i * CONTROL_POLL_MS)
    .reduce<{ last: number | null; beats: number }>((acc, now) => (shouldHeartbeat(acc.last, now)
      ? { last: now, beats: acc.beats + 1 }
      : acc), { last: null, beats: 0 }).beats;
  assert.equal(beatsPerMinute, 1);
});

test('retries of a read that got no answer back off and are capped', () => {
  assert.equal(backoffMs(0), BACKOFF_BASE_MS);
  assert.equal(backoffMs(1), BACKOFF_BASE_MS * 2);
  assert.equal(backoffMs(3), BACKOFF_BASE_MS * 8);
  // A server down for an hour is asked twice a minute, never in a loop.
  assert.equal(backoffMs(20), BACKOFF_MAX_MS);
  assert.equal(backoffMs(-4), BACKOFF_BASE_MS);
  for (let attempt = 0; attempt < 50; attempt += 1) {
    assert.ok(backoffMs(attempt) <= BACKOFF_MAX_MS);
    assert.ok(backoffMs(attempt) >= BACKOFF_BASE_MS);
  }
});
